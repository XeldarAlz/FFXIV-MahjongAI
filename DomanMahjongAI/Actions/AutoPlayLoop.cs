using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace DomanMahjongAI.Actions;

/// <summary>
/// Continuous discard-only auto-play loop. Watches the Emj addon's state code
/// (AtkValues[0]) and dispatches actions via <see cref="InputDispatcher"/>:
/// <list type="bullet">
///   <item>State 30 (our turn to discard) → policy picks → discard</item>
///   <item>State 13 (post-call discard, after chi/pon/kan) → policy picks → discard</item>
///   <item>State 15 (call prompt) → pass (option 1 = rightmost)</item>
/// </list>
/// All other states (opponent turn, animations, hand-end) are ignored —
/// tsumo/ron/riichi/kan are M8 work.
///
/// Gated by configuration: requires <c>AutomationArmed</c> true, <c>SuggestionOnly</c>
/// false, and <c>TosAccepted</c> true. Any one of those false and the loop
/// passes through without dispatching.
/// </summary>
public sealed class AutoPlayLoop : IDisposable
{
    /// <summary>Retry a dispatch for the same (state, hand) context after this many
    /// seconds — gives the game time to reject+reset without spamming.</summary>
    private const double RetrySeconds = 3.0;

    /// <summary>Force-clear <see cref="actionPending"/> if a scheduled dispatch never
    /// fired its finally (Dalamud RunOnTick issue, framework shutdown). Prevents a
    /// permanently stuck loop.</summary>
    private const double ActionPendingTimeoutSeconds = 10.0;

    private readonly Plugin plugin;
    private bool actionPending;
    private DateTime actionPendingStartedAt = DateTime.MinValue;
    private DateTime lastActionAt = DateTime.MinValue;
    private (int State, int Hand)? lastDispatchedContext;
    private bool disposed;

    /// <summary>Short human-readable description of the most recent auto action. For the overlay.</summary>
    public string LastActionDescription { get; private set; } = "(none)";

    /// <summary>State code snapshot from the last tick. For the overlay.</summary>
    public int LastObservedState { get; private set; } = -1;

    /// <summary>Hand count snapshot from the last tick. For the overlay.</summary>
    public int LastObservedHandCount { get; private set; } = -1;

    public AutoPlayLoop(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>
    /// Intent-based dispatch. Every frame we compute what we'd dispatch RIGHT NOW given
    /// the current snapshot — no transition detection, no frame-to-frame state diffing.
    /// If the (state, hand) context is new or the retry window has elapsed, we fire.
    /// Otherwise we wait. This avoids the old bug where a transient state-15 frame
    /// during opponents' calls would "eat" the 13→14 discard transition and leave the
    /// loop believing it had already handled the turn.
    /// </summary>
    private unsafe void OnUpdate(IFramework fw)
    {
        if (disposed) return;

        var cfg = plugin.Configuration;
        if (!cfg.TosAccepted || !cfg.AutomationArmed || cfg.SuggestionOnly) return;

        // Action pending with timeout recovery. If a scheduled dispatch's finally block
        // never ran (framework shutdown, RunOnTick lost), we'd otherwise be stuck forever.
        if (actionPending)
        {
            if ((DateTime.UtcNow - actionPendingStartedAt).TotalSeconds
                > ActionPendingTimeoutSeconds)
            {
                Plugin.Log.Warning("[AutoPlayLoop] resetting stuck actionPending");
                actionPending = false;
            }
            else
            {
                return;
            }
        }

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null)
        {
            lastDispatchedContext = null;
            return;
        }

        int state = ReadStateCode();
        int hand = snap.Hand.Count;
        LastObservedState = state;
        LastObservedHandCount = hand;

        // Intent from current state + legal flags.
        const Engine.ActionFlags acceptable =
            Engine.ActionFlags.Pon | Engine.ActionFlags.Chi |
            Engine.ActionFlags.MinKan | Engine.ActionFlags.ShouMinKan |
            Engine.ActionFlags.Ron | Engine.ActionFlags.Riichi |
            Engine.ActionFlags.Tsumo;
        bool isCallPrompt = state == 15 && (snap.Legal.Flags & acceptable) != 0;
        bool isDiscardTurn = state != 15 && snap.Legal.Can(Engine.ActionFlags.Discard);

        if (!isCallPrompt && !isDiscardTurn)
        {
            // Not our moment — clear context so next real turn fires fresh.
            lastDispatchedContext = null;
            return;
        }

        var context = (state, hand);
        bool sameContextAsLast = lastDispatchedContext == context;
        double sinceMs = (DateTime.UtcNow - lastActionAt).TotalMilliseconds;
        if (sameContextAsLast && sinceMs < RetrySeconds * 1000)
        {
            // Already dispatched for this exact context and retry window hasn't elapsed.
            return;
        }

        lastDispatchedContext = context;
        if (isCallPrompt) ScheduleCallDecision();
        else ScheduleDiscard();
    }

    private unsafe int ReadStateCode()
    {
        var ptr = Plugin.GameGui.GetAddonByName("Emj");
        nint addr = ptr.Address;
        if (addr == nint.Zero) return -1;
        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible || unit->AtkValues == null || unit->AtkValuesCount == 0)
            return -1;
        var v = unit->AtkValues[0];
        return v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ? v.Int : -1;
    }

    private void ScheduleDiscard()
    {
        actionPending = true;
        actionPendingStartedAt = DateTime.UtcNow;
        lastActionAt = DateTime.UtcNow;
        var delay = HumanTiming.RandomDelay(medianMs: plugin.Configuration.HumanizedDelayMs);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                var snap = plugin.AddonReader.TryBuildSnapshot();
                if (snap is null || !snap.Legal.Can(Engine.ActionFlags.Discard))
                {
                    LastActionDescription = $"discard aborted: not a discard state (hand={snap?.Hand.Count ?? -1})";
                    return;
                }

                var choice = plugin.Policy.Choose(snap);

                if (choice.Kind == ActionKind.Tsumo)
                {
                    var result0 = plugin.Dispatcher.DispatchTsumo();
                    LastActionDescription = $"auto-tsumo → {result0}";
                    return;
                }

                if (choice.Kind == ActionKind.AnKan && choice.DiscardTile is { } kanTile)
                {
                    int kanSlot = InputDispatcher.FindSlotOfTile(kanTile, snap.Hand);
                    if (kanSlot < 0)
                    {
                        LastActionDescription = $"kan tile {kanTile} not in hand";
                        return;
                    }
                    var result1 = plugin.Dispatcher.DispatchKan(kanSlot);
                    LastActionDescription = $"auto-ankan {kanTile} slot={kanSlot} → {result1}";
                    return;
                }

                if (choice.Kind != ActionKind.Discard &&
                    choice.Kind != ActionKind.Riichi)
                {
                    LastActionDescription = $"policy returned {choice.Kind} — not dispatching";
                    return;
                }
                if (choice.DiscardTile is null)
                {
                    LastActionDescription = $"policy {choice.Kind} missing tile";
                    return;
                }

                var tile = choice.DiscardTile.Value;
                int slot = InputDispatcher.FindSlotOfTile(tile, snap.Hand);
                if (slot < 0)
                {
                    LastActionDescription = $"tile {tile} not in hand";
                    return;
                }

                var result = choice.Kind == ActionKind.Riichi
                    ? plugin.Dispatcher.DispatchRiichi(slot)
                    : plugin.Dispatcher.DispatchDiscard(slot);
                string actionName = choice.Kind == ActionKind.Riichi ? "riichi" : "discard";
                LastActionDescription = $"auto-{actionName} {tile} slot={slot} → {result}";
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"AutoPlayLoop discard error: {ex}");
                LastActionDescription = $"discard exception: {ex.Message}";
            }
            finally
            {
                actionPending = false;
            }
        }, delay);
    }

    private void ScheduleCallDecision()
    {
        actionPending = true;
        actionPendingStartedAt = DateTime.UtcNow;
        lastActionAt = DateTime.UtcNow;
        var delay = HumanTiming.RandomDelay(medianMs: 700);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                var snap = plugin.AddonReader.TryBuildSnapshot();
                if (snap is null)
                {
                    LastActionDescription = "call: no snapshot";
                    return;
                }

                var choice = plugin.Policy.Choose(snap);

                // At state 15 every prompt is a modal with "Accept" at option 0 and
                // "Pass/Cancel" at option 1, so all accept-dispatches use DispatchCall().
                // The speculative opcode-based DispatchRon/Tsumo paths are not used here:
                // FireCallback opcode 11 with option 0 is the confirmed accept mechanism.
                //
                // Riichi confirmation popup: policy.Choose returns Pass because its Riichi
                // branch lives in the discard flow. If Riichi is offered on its own, we
                // accept — the user already committed by the time the popup appears.
                var legal = snap.Legal;
                bool acceptRiichiPopup =
                    choice.Kind == ActionKind.Pass && legal.Can(Engine.ActionFlags.Riichi);

                bool shouldAccept = acceptRiichiPopup || choice.Kind is
                    ActionKind.Ron or ActionKind.Tsumo or
                    ActionKind.Pon or ActionKind.Chi or
                    ActionKind.MinKan or ActionKind.ShouMinKan;

                InputDispatcher.DispatchResult result;
                if (shouldAccept)
                {
                    result = plugin.Dispatcher.DispatchCall();
                    string label = acceptRiichiPopup
                        ? "riichi-confirm"
                        : choice.Kind.ToString().ToLowerInvariant();
                    LastActionDescription = $"auto-{label} → {result}";
                    // Meld recording is handled centrally by InputEventLogger's
                    // FireCallback hook, which sees both our DispatchCall() and manual
                    // in-game clicks. Avoids double-recording.
                }
                else
                {
                    // Pass is always the rightmost button, so its option index equals the
                    // number of accept buttons shown. 2-button Pon+Pass: pass=1. 3-button
                    // Pon+Kan+Pass: pass=2. Multi-chi + Pass: pass=ChiCandidates.Count+...
                    // Hardcoding 1 misclicks on 3-button prompts (hits Kan → HookFailed).
                    int passIndex = 0;
                    if (legal.Can(Engine.ActionFlags.Pon)) passIndex++;
                    if (legal.Can(Engine.ActionFlags.Chi))
                        passIndex += Math.Max(1, legal.ChiCandidates.Count);
                    if (legal.Can(Engine.ActionFlags.MinKan)) passIndex++;
                    if (legal.Can(Engine.ActionFlags.ShouMinKan)) passIndex++;
                    if (legal.Can(Engine.ActionFlags.Ron)) passIndex++;
                    if (legal.Can(Engine.ActionFlags.Riichi)) passIndex++;
                    if (legal.Can(Engine.ActionFlags.Tsumo)) passIndex++;
                    result = plugin.Dispatcher.DispatchCallOption(passIndex);
                    LastActionDescription = $"auto-pass[opt={passIndex}] → {result}";
                }
                Plugin.Log.Info($"[AutoPlayLoop] call-prompt dispatch: {LastActionDescription}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"AutoPlayLoop call decision error: {ex}");
                LastActionDescription = $"call exception: {ex.Message}";
            }
            finally
            {
                actionPending = false;
            }
        }, delay);
    }
}

using DomanMahjongAI.Engine;
using DomanMahjongAI.GameState;
using DomanMahjongAI.Policy;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace DomanMahjongAI.Actions;

/// <summary>
/// Continuous auto-play loop. Drives the Emj addon through its state machine via
/// <see cref="InputDispatcher"/>:
/// <list type="bullet">
///   <item>Discard turn (Legal.Can(Discard)) → policy picks → discard/riichi</item>
///   <item>Call prompt at state 15 (pon/chi/kan/ron) or state 6 (riichi/tsumo
///       self-declaration) with modal visible → policy picks → accept or pass</item>
///   <item>State 25 (chi-variant selection, the follow-up after accepting chi with
///       multiple possible sequences) → dispatch opt=0 to pick the first variant</item>
/// </list>
/// All other states (opponent turn, animations, hand-end) are ignored.
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

        // State 25 = chi-variant selection popup — the follow-up after we click Chi on
        // the state-15 prompt when the claimed tile has multiple possible sequences
        // (e.g. hand 3,4,5,6 + claimed 5 → {3,4,5}, {4,5,6}). The game pauses with a
        // new modal showing each variant as a button plus Cancel. Policy already
        // committed to chi on the prior popup so we just click option 0 (first
        // variant). Signature from snap-chi-variant: AtkValues[0]=25, [2]="Chi",
        // [3]=variant count, [4..] = three-tile texture runs separated by 76041 markers.
        if (state == 25)
        {
            var ctx25 = (state, hand);
            bool sameCtx25 = lastDispatchedContext == ctx25;
            double sinceMs25 = (DateTime.UtcNow - lastActionAt).TotalMilliseconds;
            if (!sameCtx25 || sinceMs25 >= RetrySeconds * 1000)
            {
                lastDispatchedContext = ctx25;
                ScheduleVariantAccept();
            }
            return;
        }

        // Intent is driven purely by the reader's legal flags. AddonEmjReader already
        // distinguishes "modal-visible call prompt" from "our discard turn" using the
        // node-visibility gate on the id=104/id=3 shell and the hand %3==2 check — both
        // state 15 (pon/chi/kan/ron) and state 6 (riichi/tsumo self-declaration) share
        // the same shell, and state 6 also doubles as the normal discard-turn code, so
        // gating on state-codes here double-counts and blocks legitimate discards (see
        // snap-newgame-stuck: state=6, hand=14, legal=Discard, but AtkValues[6]="Discard"
        // proved it was a plain discard turn). Call-prompt flags and Discard are
        // mutually exclusive in the reader's output, so checking flags alone is safe.
        const Engine.ActionFlags acceptable =
            Engine.ActionFlags.Pon | Engine.ActionFlags.Chi |
            Engine.ActionFlags.MinKan | Engine.ActionFlags.ShouMinKan |
            Engine.ActionFlags.Ron | Engine.ActionFlags.Riichi |
            Engine.ActionFlags.Tsumo;
        bool isCallPrompt = (snap.Legal.Flags & acceptable) != 0;
        bool isDiscardTurn = snap.Legal.Can(Engine.ActionFlags.Discard);

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
        if (isCallPrompt) ScheduleCallDecision(context);
        else ScheduleDiscard();
    }

    private void ScheduleVariantAccept()
    {
        actionPending = true;
        actionPendingStartedAt = DateTime.UtcNow;
        lastActionAt = DateTime.UtcNow;
        var delay = HumanTiming.RandomDelay(medianMs: 500);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                // Re-check state at dispatch time — the modal can close during the
                // humanized delay (auto-declare elsewhere, manual click, opponent
                // timeout). Firing opcode 11 after the state machine moved on would
                // accept option 0 of whatever popup came next, which may not be a
                // chi variant. Mirrors the re-check in ScheduleDiscard.
                int currentState = ReadStateCode();
                if (currentState != 25)
                {
                    LastActionDescription =
                        $"variant aborted: state moved {25}→{currentState}";
                    return;
                }
                var result = plugin.Dispatcher.DispatchCallOption(0);
                LastActionDescription = $"auto-variant[opt=0] → {result}";
                Plugin.Log.Info($"[AutoPlayLoop] variant dispatch: {LastActionDescription}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"AutoPlayLoop variant decision error: {ex}");
                LastActionDescription = $"variant exception: {ex.Message}";
            }
            finally
            {
                actionPending = false;
            }
        }, delay);
    }

    private unsafe int ReadStateCode()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonEmjReader.AddonName);
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

    private void ScheduleCallDecision((int State, int Hand) context)
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

                // At state 15 (pon/chi/kan/ron) and state 6 (riichi/tsumo self-declaration)
                // every prompt is a modal with "Accept" at option 0 and "Pass/Cancel" at
                // option 1. Both states use the same FireCallback payload — opcode 11,
                // option = button-index. DispatchRon/Tsumo/Riichi in InputDispatcher are
                // legacy stubs kept for API shape; the actual accept path is DispatchCall().
                //
                // Riichi popup: policy.Choose returns Pass because its Riichi branch lives
                // in the discard flow. If Riichi is offered on its own, we accept — the
                // user already committed by the time the popup appears.
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
                    // Hardcoding 1 misclicks on 3-button prompts.
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

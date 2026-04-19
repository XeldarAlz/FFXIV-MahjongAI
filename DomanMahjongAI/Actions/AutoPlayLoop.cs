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
    private readonly Plugin plugin;
    private int lastStateCode = int.MinValue;
    private int lastHandCount = -1;
    private bool actionPending;
    private DateTime lastActionAt = DateTime.MinValue;
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

    private unsafe void OnUpdate(IFramework fw)
    {
        if (disposed) return;

        // Hard gates — if any fail, don't dispatch.
        var cfg = plugin.Configuration;
        if (!cfg.TosAccepted || !cfg.AutomationArmed || cfg.SuggestionOnly) return;
        if (actionPending) return;
        // Rate-limit: at least 200ms between dispatches regardless of state changes.
        if ((DateTime.UtcNow - lastActionAt).TotalMilliseconds < 200) return;

        // Read the Emj addon.
        var ptr = Plugin.GameGui.GetAddonByName("Emj");
        nint addr = ptr.Address;
        if (addr == nint.Zero)
        {
            lastStateCode = int.MinValue;
            lastHandCount = -1;
            return;
        }

        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible)
        {
            lastStateCode = int.MinValue;
            lastHandCount = -1;
            return;
        }

        // State code (AtkValues[0]) — used for call-prompt detection.
        int state = 0;
        if (unit->AtkValues != null && unit->AtkValuesCount > 0)
        {
            var v = unit->AtkValues[0];
            if (v.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int) state = v.Int;
        }

        // Hand tile count — read directly from +0xDB8 (14 slots × 4 bytes, first slot with
        // tile_id byte 0 terminates).
        byte* basePtr = (byte*)addr;
        int hand = 0;
        for (int i = 0; i < 14; i++)
        {
            byte raw = basePtr[0xDB8 + i * 4];
            if (raw == 0) break;
            int tileId = raw - 9;
            if (tileId >= 0 && tileId < 34) hand++;
        }

        LastObservedState = state;
        LastObservedHandCount = hand;

        int prevState = lastStateCode;
        int prevHand = lastHandCount;
        lastStateCode = state;
        lastHandCount = hand;

        // Call prompt: fire on state→15 transition (prevents re-firing while prompt stays).
        // Currently the policy always returns Pass because LegalActions.PonCandidates etc.
        // aren't populated yet (M4 RE owed). When they are, the policy returns Pon/Chi
        // when CallEvaluator accepts — we'd need to dispatch Call instead of Pass here.
        if (state == 15 && prevState != 15)
        {
            ScheduleCallDecision();
            return;
        }

        // Discard: fire when hand count transitions from <14 to 14. This is more robust
        // than checking state code (which varies: 30 normal, 13 post-call, 6 observed
        // in some contexts). A 14-tile closed hand means it's our turn to discard.
        if (hand == 14 && prevHand != 14 && prevHand >= 0)
        {
            ScheduleDiscard();
        }
    }

    private void ScheduleDiscard()
    {
        actionPending = true;
        var delay = HumanTiming.RandomDelay(medianMs: plugin.Configuration.HumanizedDelayMs);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                var snap = plugin.AddonReader.TryBuildSnapshot();
                if (snap is null || snap.Hand.Count != 14)
                {
                    LastActionDescription = "discard aborted: hand count != 14";
                    return;
                }

                var choice = plugin.Policy.Choose(snap);

                // Tsumo: last-drawn tile is the winning tile; no slot needed.
                if (choice.Kind == ActionKind.Tsumo)
                {
                    var result0 = plugin.Dispatcher.DispatchTsumo();
                    LastActionDescription = $"auto-tsumo → {result0}";
                    lastActionAt = DateTime.UtcNow;
                    return;
                }

                // Ankan from our turn: dispatch kan with the slot of one of the 4 tiles.
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
                    lastActionAt = DateTime.UtcNow;
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
                lastActionAt = DateTime.UtcNow;
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

    private void SchedulePass()
    {
        actionPending = true;
        var delay = HumanTiming.RandomDelay(medianMs: 700);
        _ = Plugin.Framework.RunOnTick(() =>
        {
            try
            {
                var result = plugin.Dispatcher.DispatchPass();
                LastActionDescription = $"auto-pass → {result}";
                lastActionAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"AutoPlayLoop pass error: {ex}");
                LastActionDescription = $"pass exception: {ex.Message}";
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
                InputDispatcher.DispatchResult result;
                switch (choice.Kind)
                {
                    case ActionKind.Ron:
                        result = plugin.Dispatcher.DispatchRon();
                        LastActionDescription = $"auto-ron → {result}";
                        break;
                    case ActionKind.Pon:
                    case ActionKind.Chi:
                    case ActionKind.MinKan:
                    case ActionKind.ShouMinKan:
                        result = plugin.Dispatcher.DispatchCall();
                        LastActionDescription = $"auto-{choice.Kind.ToString().ToLowerInvariant()} → {result}";
                        break;
                    default:
                        result = plugin.Dispatcher.DispatchPass();
                        LastActionDescription = $"auto-pass → {result}";
                        break;
                }
                lastActionAt = DateTime.UtcNow;
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

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DomanMahjongAI.Engine;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Finds the <c>Emj</c> addon in the running client, subscribes to its lifecycle
/// events, and exposes:
///   - a raw <see cref="AddonEmjObservation"/> (for diagnostics and the debug overlay)
///   - a <see cref="StateSnapshot"/> builder (stubbed until RE is finished)
///
/// This component must be created on (and disposed from) the framework thread.
/// </summary>
public sealed class AddonEmjReader : IDisposable
{
    public const string AddonName = "Emj";

    private readonly Plugin plugin;
    private bool disposed;
    private int lastLoggedCallPromptState = -1;
    private int lastLoggedMeldHandCount = -1;

    public AddonEmjObservation LastObservation { get; private set; } = AddonEmjObservation.Empty;

    /// <summary>Fired whenever any lifecycle event updates the observation.</summary>
    public event Action<AddonEmjObservation>? ObservationChanged;

    public AddonEmjReader(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnPostSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnPostRefresh);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnPostReceiveEvent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Plugin.AddonLifecycle.UnregisterListener(OnPostSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnPreFinalize);
        Plugin.AddonLifecycle.UnregisterListener(OnPostRefresh);
        Plugin.AddonLifecycle.UnregisterListener(OnPostReceiveEvent);
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args) => Observe("PostSetup", args);
    private void OnPostRefresh(AddonEvent type, AddonArgs args) => Observe("PostRefresh", args);
    private void OnPostReceiveEvent(AddonEvent type, AddonArgs args) => Observe("PostReceiveEvent", args);

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        LastObservation = AddonEmjObservation.Empty with { LastLifecycleEvent = "PreFinalize" };
        ObservationChanged?.Invoke(LastObservation);
    }

    private unsafe void Observe(string eventName, AddonArgs args)
    {
        var addr = args.Addon.Address;
        var obs = AddonEmjObservation.Empty;

        if (addr != 0)
        {
            var unit = (AtkUnitBase*)addr;
            obs = new AddonEmjObservation(
                Present: true,
                IsVisible: unit->IsVisible,
                Address: addr,
                Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
                Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
                LastSeenUtcTicks: DateTime.UtcNow.Ticks,
                LastLifecycleEvent: eventName);
        }

        LastObservation = obs;
        ObservationChanged?.Invoke(obs);
    }

    /// <summary>
    /// Poll the current addon state via GameGui (fallback path when lifecycle events
    /// are not firing, or when the plugin starts with the addon already visible).
    /// Safe to call from the framework thread every tick.
    /// </summary>
    public unsafe AddonEmjObservation Poll()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero)
        {
            var missing = AddonEmjObservation.Empty with
            {
                LastSeenUtcTicks = DateTime.UtcNow.Ticks,
                LastLifecycleEvent = LastObservation.LastLifecycleEvent,
            };
            LastObservation = missing;
            return missing;
        }

        var unit = (AtkUnitBase*)addr;
        var obs = new AddonEmjObservation(
            Present: true,
            IsVisible: unit->IsVisible,
            Address: addr,
            Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
            Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
            LastSeenUtcTicks: DateTime.UtcNow.Ticks,
            LastLifecycleEvent: LastObservation.LastLifecycleEvent ?? "(poll)");
        LastObservation = obs;
        return obs;
    }

    /// <summary>
    /// Build a <see cref="StateSnapshot"/> from the current addon state.
    /// Populates the fields we have from the M4 RE work: hand tiles + per-seat scores.
    /// Wall count, turn owner, dealer, discard pools, round — still unmapped.
    /// </summary>
    /// <remarks>
    /// Offset layout (AddonEmj relative to addon base):
    ///   +0x0500   self score (int32)
    ///   +0x07E0   shimocha score
    ///   +0x0AC0   toimen score
    ///   +0x0DA0   kamicha score
    ///   +0x0DB8   14 hand-tile slots, 4 bytes each: [tile_id + 9, 0x29, 0x01, 0x00]
    ///             Slots 0-12 sorted ascending; slot 13 holds the last-drawn tile when 14.
    /// </remarks>
    public unsafe StateSnapshot? TryBuildSnapshot()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero) return null;

        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible) return null;

        byte* basePtr = (byte*)addr;

        // Hand tiles at +0x0DB8.
        var hand = new List<Tile>(14);
        for (int i = 0; i < 14; i++)
        {
            byte raw = basePtr[0xDB8 + i * 4];
            if (raw == 0) break;    // empty slot
            int tileId = raw - 9;
            if (tileId < 0 || tileId >= Tile.Count34) continue;
            hand.Add(Tile.FromId(tileId));
        }

        // Scores at the known seat offsets (seat-relative: [self, shimocha, toimen, kamicha]).
        var scores = new int[4]
        {
            *(int*)(basePtr + 0x0500),
            *(int*)(basePtr + 0x07E0),
            *(int*)(basePtr + 0x0AC0),
            *(int*)(basePtr + 0x0DA0),
        };
        // Reject garbage reads (game hasn't populated the struct yet).
        bool plausibleScores = scores.All(s => s is >= 0 and <= 200000);
        if (!plausibleScores) return null;

        // Per-seat discard count bytes. Sit 2 bytes before each score field —
        // the position was pinned by diffing consecutive observations across a
        // full round and confirmed via walknodes (4 pools of 31 UldComponent
        // slots, each pool's visible count matching its byte). The actual tile
        // pool itself isn't in the addon — it lives in the game-state module
        // whose static offset is currently stale post-patch — so we track the
        // count only. That's still useful: it feeds turn-progression features
        // in the opponent model and gives us an accurate wall count in every
        // state (not just state 5).
        int[] discardCounts = new int[4]
        {
            basePtr[0x04FE],
            basePtr[0x07DE],
            basePtr[0x0ABE],
            basePtr[0x0D9E],
        };
        // Reject implausible values per-seat — each seat discards up to ~24
        // times in a 70-wall round; anything way over that means we're reading
        // garbage for *that* seat. Zeroing all four on one bad byte threw away
        // good data from the other three and collapsed the derived wall count
        // back to 70.
        for (int i = 0; i < discardCounts.Length; i++)
            if (discardCounts[i] > 40) discardCounts[i] = 0;

        // State code and wall count from AtkValues. AtkValues[0] holds the state code
        // (30=our turn, 15=call prompt, 5=post-draw idle, etc.). AtkValues[1] holds wall
        // count when state == 5.
        int stateCode = -1;
        int wallRemaining = StateSnapshot.Empty.WallRemaining;
        var atkValues = unit->AtkValues;
        int atkCount = unit->AtkValuesCount;
        if (atkValues != null && atkCount > 0
            && atkValues[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
        {
            stateCode = atkValues[0].Int;
            if (stateCode == 5 && atkCount >= 2)
            {
                int reported = atkValues[1].Int;
                if (reported is > 0 and <= 136) wallRemaining = reported;
            }
        }
        // Fall back to count-derived wall when the AtkValue path didn't fire.
        // wall_remaining ≈ 70 initial live-wall draws − total discards (each
        // discard follows a draw). Ignores kan draws from the dead wall (minor
        // under-estimate when kans occur — acceptable).
        if (wallRemaining == StateSnapshot.Empty.WallRemaining)
        {
            int totalDiscards = discardCounts.Sum();
            int derived = 70 - totalDiscards;
            if (derived is >= 0 and <= 70) wallRemaining = derived;
        }

        // Assemble the snapshot. Seat-relative: self is always index 0 here.
        // RoundWind / OurSeat (in the absolute E/S/W/N sense) aren't reliably recoverable
        // yet — leave them at the defaults; downstream scorers will treat the player as
        // East for yakuhai purposes (minor inaccuracy, fixable when M4 sig-scan lands).
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView([], [], [], false, -1, false, false,
                DiscardCount: discardCounts[i]);

        var legal = BuildLegalActions(unit, stateCode, hand, atkValues, atkCount);

        MaybeLogCallPromptTransition(addr, stateCode, atkValues, atkCount, hand, legal);
        MaybeLogMeldTransition(addr, stateCode, hand);

        // Resolve our own open melds. The Emj addon's on-disk meld struct is still
        // un-mapped; instead the MeldTracker captures each meld when the auto-play
        // (or hooked manual click) accepts a call prompt. Reset the tracker when the
        // closed-hand count proves a new round has started (≥ 13 = no melds).
        // Defensive copy: StateSnapshot is documented as immutable, so don't hand
        // out a view that aliases the tracker's mutable internal list.
        plugin.MeldTracker.ResetIfRoundEnded(hand.Count);
        var ourMelds = plugin.MeldTracker.Melds.ToArray();

        return StateSnapshot.Empty with
        {
            Hand = hand,
            OurMelds = ourMelds,
            Scores = scores,
            Seats = seats,
            WallRemaining = wallRemaining,
            Legal = legal,
        };
    }

    /// <summary>
    /// Diagnostic dump when a real call prompt is detected. State 15 is overloaded —
    /// it's also used for the "please wait for other players" idle screen — so we gate on
    /// the presence of <see cref="LegalActions"/> flags beyond plain <see cref="ActionFlags.Pass"/>,
    /// which means the button-label scan actually found one of Pon/Chi/Kan/Ron. Each
    /// entry captures the full AtkValues plus a chunk of Emj addon memory so we can
    /// diff across prompts and pin the claimed-tile offset (needed to unblock chi
    /// auto-accept).
    ///
    /// <para>Gated by <c>/mjauto log on</c> so it's off by default.</para>
    /// </summary>
    private unsafe void MaybeLogCallPromptTransition(
        nint addonBase, int stateCode, AtkValue* atkValues, int atkCount,
        IReadOnlyList<Tile> hand, LegalActions legal)
    {
        const ActionFlags promptFlags =
            ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan |
            ActionFlags.ShouMinKan | ActionFlags.Ron |
            ActionFlags.Riichi | ActionFlags.Tsumo;

        bool isPrompt = stateCode == 15 && (legal.Flags & promptFlags) != 0;
        if (!isPrompt)
        {
            // Reset so we re-log the next genuine transition.
            lastLoggedCallPromptState = -1;
            return;
        }
        if (lastLoggedCallPromptState == 15) return;
        lastLoggedCallPromptState = 15;

        if (!plugin.EventLogger.Enabled) return;
        if (atkValues == null) return;

        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-call-prompts.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {DateTime.UtcNow:o}  state=15  atkCount={atkCount}");
            sb.Append($"hand={Tiles.Render(hand)}  flags={legal.Flags}  ");
            sb.AppendLine(
                $"pon={legal.PonCandidates.Count} chi={legal.ChiCandidates.Count} kan={legal.KanCandidates.Count}");

            for (int i = 0; i < atkCount && i < 64; i++)
            {
                var v = atkValues[i];
                sb.Append($"  [{i,3}] {v.Type,-14} ");
                switch (v.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                        sb.Append($"Int={v.Int}");
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                        sb.Append($"UInt={v.UInt} (0x{v.UInt:X})");
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                        sb.Append($"Bool={v.Byte != 0}");
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8:
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                        var s = v.String.Value != null
                            ? System.Text.Encoding.UTF8.GetString(v.String)
                            : "(null)";
                        sb.Append($"String=\"{s}\"");
                        break;
                    default:
                        sb.Append($"raw=0x{v.UInt:X}");
                        break;
                }
                sb.AppendLine();
            }

            DumpMemoryRegion(sb, addonBase);
            sb.AppendLine();

            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"call-prompt diagnostic log error: {ex.Message}");
        }
    }

    /// <summary>
    /// Meld-discovery diagnostic. Fires once per distinct closed-hand count below 14
    /// — i.e., each time the player opens a new meld in the current round. Dumps the
    /// big addon-memory region [0x0500..0x1400] (covers per-seat score/discard/meld
    /// blocks plus the area right after self hand at +0xDB8) plus the first 0x1000
    /// bytes of the Emj game-state module (a separate structure from the addon;
    /// dereferenced from module_base + 0x029E1400). One of these two regions almost
    /// certainly contains the open-meld records; cross-diffing captures with different
    /// meld counts pins the exact offset.
    ///
    /// <para>Gated by <c>/mjauto log on</c>. Logs to <c>emj-meld-captures.log</c>.</para>
    /// </summary>
    private unsafe void MaybeLogMeldTransition(
        nint addonBase, int stateCode, IReadOnlyList<Tile> hand)
    {
        // Only interesting when we've called something (closed hand < 13 proves open melds).
        if (hand.Count >= 13 || hand.Count <= 0)
        {
            lastLoggedMeldHandCount = -1;
            return;
        }
        if (hand.Count == lastLoggedMeldHandCount) return;
        lastLoggedMeldHandCount = hand.Count;

        if (!plugin.EventLogger.Enabled) return;

        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-meld-captures.log");

            var sb = new System.Text.StringBuilder();
            int inferredMelds = (14 - hand.Count) / 3;
            int remainder = (14 - hand.Count) % 3;
            sb.AppendLine(
                $"# {DateTime.UtcNow:o}  state={stateCode}  closedHand={hand.Count}  " +
                $"inferredMelds={inferredMelds}{(remainder != 0 ? " (off-sync)" : "")}  " +
                $"hand={Tiles.Render(hand)}");

            byte* basePtr = (byte*)addonBase;
            sb.AppendLine("  -- addon @ +0x0500..+0x3000 (per-seat blocks + post-hand area + extended) --");
            for (int off = 0x0500; off < 0x3000; off += 16)
                AppendHexRow(sb, basePtr, off, 16);

            // AgentEmj — the UI-agent struct, separate from the AtkUnitBase. Scan with
            // the FFXIVClientStructs-backed accessor (this path worked in the existing
            // /mjauto agent command; the module-pointer-slot path in earlier captures
            // was zeroed). If melds aren't in the addon, they're almost certainly here.
            var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
            if (agentModule != null)
            {
                var agent = agentModule->GetAgentByInternalId(
                    (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId)5);
                if (agent != null)
                {
                    sb.AppendLine($"  -- AgentEmj @ 0x{(nint)agent:X} +0x0000..+0x2000 --");
                    byte* agentPtr = (byte*)agent;
                    for (int off = 0; off < 0x2000; off += 16)
                        AppendHexRow(sb, agentPtr, off, 16);
                }
                else
                {
                    sb.AppendLine("  -- AgentEmj unavailable (GetAgentByInternalId returned null) --");
                }
            }
            else
            {
                sb.AppendLine("  -- AgentModule unavailable --");
            }

            sb.AppendLine();
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"meld-capture diagnostic log error: {ex.Message}");
        }
    }

    private static unsafe void AppendHexRow(
        System.Text.StringBuilder sb, byte* basePtr, int offset, int length)
    {
        sb.Append($"  +0x{offset:X4}: ");
        for (int i = 0; i < length; i++)
        {
            sb.Append($"{basePtr[offset + i]:X2} ");
            if (i == 7) sb.Append(' ');
        }
        sb.Append(" |");
        for (int i = 0; i < length; i++)
        {
            byte b = basePtr[offset + i];
            sb.Append(b >= 32 && b < 127 ? (char)b : '.');
        }
        sb.AppendLine("|");
    }

    /// <summary>
    /// Dump two regions of Emj addon memory where the claimed tile likely lives:
    /// +0x0100..+0x0500 (header / prompt payload area, before the score region)
    /// and +0x0E00..+0x1200 (just past the hand tiles at +0xDB8, likely opponent
    /// discard-pool territory). Tile slots encode as 4 bytes <c>[tile_id+9, 0x29, 0x01, 0x00]</c>
    /// — scan for that pattern in the hex to find tile fields.
    /// </summary>
    private static unsafe void DumpMemoryRegion(System.Text.StringBuilder sb, nint addonBase)
    {
        byte* basePtr = (byte*)addonBase;
        DumpRange(sb, basePtr, 0x0100, 0x0400);
        DumpRange(sb, basePtr, 0x0E00, 0x0400);
    }

    private static unsafe void DumpRange(System.Text.StringBuilder sb, byte* basePtr, int offset, int length)
    {
        sb.AppendLine($"  -- memory @ +0x{offset:X4}..+0x{offset + length:X4} --");
        for (int row = 0; row < length; row += 16)
        {
            sb.Append($"  +0x{offset + row:X4}: ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append($"{basePtr[offset + row + i]:X2} ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16; i++)
            {
                byte b = basePtr[offset + row + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
    }

    /// <summary>
    /// Build the LegalActions record from the current state code. Three cases today:
    /// <list type="bullet">
    ///   <item>State 15 (call prompt): scan AtkValues strings for Pon/Chi/Kan/Ron/Riichi/Tsumo
    ///       button labels, then derive candidates. Pon/min-kan claims are deduced from the
    ///       unique pair/triplet in hand (no candidate when ambiguous). Chi claims are read
    ///       from <c>AtkValues[19]</c> (texture id = tile_id + 76041) and the
    ///       <see cref="CallCandidateDeriver"/> emits per-variant candidates. Ron/Riichi/Tsumo
    ///       are flag-only — the policy and AutoPlayLoop treat them as accept-via-opt-0.</item>
    ///   <item>Hand count satisfies <c>% 3 == 2</c> (14/11/8/5/2): plain discard. This covers
    ///       pre-discard for 0..4 open melds.</item>
    ///   <item>Otherwise: no actions.</item>
    /// </list>
    /// </summary>
    private static unsafe LegalActions BuildLegalActions(
        AtkUnitBase* unit, int stateCode, List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        // Call-prompt states 15 (pon/chi/kan/ron) and 6 (riichi/tsumo self-declarations)
        // are overloaded: they also tick during idle frames that carry banner text for
        // opponents' declarations, which our AtkValues label scan would otherwise pick up
        // as a prompt. Gate on the modal-shell node: the Emj addon's id=104/type=1052
        // host contains an inner id=3/type=1030 shell whose Visible flag only flips on
        // while the game is actually awaiting a decision. Verified across pon+pass,
        // riichi-confirm, and ron+pass captures. Button payload is the same for both
        // state codes — FireCallback([Int=11, Int=0..N]) — proven by RE captures of
        // manual riichi/tsumo clicks at state 6 firing opcode 11 with option 0.
        if ((stateCode == 15 || stateCode == 6)
            && atkValues != null && IsCallModalVisible(unit))
            return BuildCallPromptLegal(hand, atkValues, atkCount);

        // "Our turn to discard" = 14 tiles with 0 melds, 11 with 1 meld, 8 with 2, etc. —
        // all satisfy hand % 3 == 2. Hardcoding 14 skipped every post-call discard.
        if (hand.Count > 0 && hand.Count % 3 == 2)
            return new LegalActions(ActionFlags.Discard, [], [], [], []);

        return LegalActions.None;
    }

    private static unsafe bool IsCallModalVisible(AtkUnitBase* unit)
    {
        if (unit == null) return false;
        var host = unit->GetNodeById(104);
        if (host == null) return false;
        // Type-check before casting — component node types are ≥ 1000 (custom
        // Uld components), native types are single-digit. If a future patch
        // renumbers id=104 to a non-component node, the cast would dereference
        // the wrong struct layout. Bail safely when the type isn't what we
        // captured (observed 1052 in live).
        if ((int)host->Type < 1000) return false;
        var comp = ((AtkComponentNode*)host)->Component;
        if (comp == null) return false;
        var shell = comp->GetNodeById(3);
        return shell != null
            && shell->NodeFlags.HasFlag(
                FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible);
    }

    private static unsafe LegalActions BuildCallPromptLegal(
        List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        // Scan the first ~20 AtkValues for button labels. The capture at state 15 shows
        // labels at indices 6-8 ("Pass","Pon","Pass" for a pon+pass prompt), but slot
        // assignment varies by prompt shape; scan a wider range for robustness. Exact
        // match only — "Pon!" etc. are status indicators shown elsewhere.
        bool offersPon = false, offersChi = false, offersKan = false;
        bool offersRon = false, offersRiichi = false, offersTsumo = false;
        int scanEnd = Math.Min(atkCount, 20);
        for (int i = 0; i < scanEnd; i++)
        {
            var v = atkValues[i];
            if (v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8 &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                continue;
            if (v.String.Value == null) continue;
            var s = System.Text.Encoding.UTF8.GetString(v.String);
            switch (s)
            {
                case "Pon": offersPon = true; break;
                case "Chi": offersChi = true; break;
                case "Kan": offersKan = true; break;
                case "Ron": offersRon = true; break;
                case "Riichi": offersRiichi = true; break;
                case "Tsumo": offersTsumo = true; break;
            }
        }

        // Without any offered action it's just a pass-through — let the caller pass.
        if (!offersPon && !offersChi && !offersKan && !offersRon
            && !offersRiichi && !offersTsumo)
            return new LegalActions(ActionFlags.Pass, [], [], [], []);

        ActionFlags flags = ActionFlags.Pass;
        var pons = new List<MeldCandidate>();
        var chis = new List<MeldCandidate>();
        var kans = new List<MeldCandidate>();

        // Ron / Riichi / Tsumo: no candidate derivation — flag-only. The policy's
        // existing top-of-Choose branches handle Tsumo/Ron declarations; Riichi is
        // surfaced so AutoPlayLoop can accept the confirmation by clicking option 0.
        if (offersRon) flags |= ActionFlags.Ron;
        if (offersRiichi) flags |= ActionFlags.Riichi;
        if (offersTsumo) flags |= ActionFlags.Tsumo;

        // Count hand tiles once; used for pon/kan deduction.
        var counts = new int[Tile.Count34];
        foreach (var t in hand) counts[t.Id]++;

        // Pon: flag is set whenever the game shows the button (so AutoPlayLoop knows to
        // respond to the prompt — accept or explicitly pass). Candidate is only emitted
        // when the pair is unambiguous; without a candidate, CallEvaluator returns
        // "no candidates offered" and the policy picks Pass, which correctly dismisses
        // the popup via DispatchCallOption(passIndex).
        if (offersPon)
        {
            flags |= ActionFlags.Pon;
            int pairCount = 0;
            int pairId = -1;
            for (int id = 0; id < Tile.Count34; id++)
            {
                if (counts[id] >= 2) { pairCount++; pairId = id; }
            }
            if (pairCount == 1)
            {
                var claimed = Tile.FromId(pairId);
                var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 1);
                pons.AddRange(derived.Pon);
            }
        }

        // MinKan: flag set whenever offered so AutoPlayLoop resolves the prompt. Candidate
        // is emitted only when triplet is unambiguous AND pon isn't on the same button row
        // (Pon | Kan | Pass) — DispatchCall hardcodes option 0 = Pon, so emitting a Kan
        // candidate alongside pon would risk misfiring. When no candidate is emitted, the
        // policy passes (correct default).
        if (offersKan)
        {
            flags |= ActionFlags.MinKan;
            if (!offersPon)
            {
                int tripCount = 0;
                int tripId = -1;
                for (int id = 0; id < Tile.Count34; id++)
                {
                    if (counts[id] >= 3) { tripCount++; tripId = id; }
                }
                if (tripCount == 1)
                {
                    var claimed = Tile.FromId(tripId);
                    var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 1);
                    kans.AddRange(derived.Kan);
                }
            }
        }

        // Chi: claimed tile is at AtkValues[19], encoded as texture_id = tile_id + 76041.
        // Verified across two live chi prompts:
        //   - claim 1m ⇄ AtkValues[19]=76041 (hand 1234m89s45z → {1m,2m,3m})
        //   - claim 7s ⇄ AtkValues[19]=76065 (hand 2347m257p2567s57z → {5s,6s,7s})
        // The pon layout reuses [19] for something else (a hand tile), so we only read it
        // when the chi button is offered.
        //
        // Pon+Chi simultaneous prompt: DispatchCall() always clicks option 0, which is the
        // leftmost button. If both Pon and Chi are offered, emitting both as candidates
        // would let CallEvaluator pick chi while opt 0 is actually pon — a misfire. Mirror
        // the offersKan-if-pon skip: only emit pon when both are offered; chi flag is still
        // set so the loop resolves the prompt via Pass if pon isn't beneficial.
        if (offersChi)
        {
            flags |= ActionFlags.Chi;
            if (!offersPon && atkCount > 19 &&
                atkValues[19].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
            {
                int tex = atkValues[19].Int;
                int tileId = tex - 76041;
                if (tileId is >= 0 and < Tile.Count34)
                {
                    var claimed = Tile.FromId(tileId);
                    var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 3);
                    chis.AddRange(derived.Chi);
                }
            }
        }

        return new LegalActions(flags, [], pons, chis, kans);
    }
}

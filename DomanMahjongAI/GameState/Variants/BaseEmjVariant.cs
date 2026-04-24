using DomanMahjongAI.Engine;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DomanMahjongAI.GameState.Variants;

/// <summary>
/// Shared layout reader for every Mahjong-addon variant observed to date.
/// Scores (+0x0500 / 0x07E0 / 0x0AC0 / 0x0DA0), per-seat discard-count bytes
/// (score_offset - 2), hand tile array position (+0x0DB8), state code in
/// AtkValues[0], wall count in AtkValues[1] when state==5, and call-modal
/// node IDs (host=104, shell=3) are identical across both variants observed
/// on issue #13 (EU <c>Emj</c>, NA <c>EmjL</c>); the only layout divergence
/// is the <b>tile texture base</b> used by the hand encoding at +0x0DB8
/// and the chi-claim AtkValue[19] — Emj uses 76041, EmjL uses 76003.
///
/// <para>That single divergence is lifted to <see cref="TileTextureBase"/>,
/// which each concrete variant supplies. Everything else is shared so a
/// future patch-driven offset nudge is a one-line fix here instead of a
/// multi-variant sweep.</para>
///
/// <para>If a third variant shows up whose divergence is deeper than the
/// texture base (e.g., a different node ID for the call modal, a shifted
/// score offset), the right move is to add another abstract property here
/// — not to copy this class wholesale into a sibling.</para>
/// </summary>
internal abstract class BaseEmjVariant : IEmjVariant
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string PreferredAddonName { get; }

    /// <summary>
    /// Base texture id used by this variant's tile encoding. A populated hand
    /// slot at +0x0DB8 + i*4, read as little-endian int32, equals
    /// <c><see cref="TileTextureBase"/> + tile_id</c> for <c>tile_id</c> in
    /// <c>[0, <see cref="Tile.Count34"/>)</c>. The chi-claim texture at
    /// AtkValues[19] uses the same base. Observed values: 76041 on Emj (EU),
    /// 76003 on EmjL (NA) — different texture atlas bases per client build.
    /// </summary>
    protected abstract int TileTextureBase { get; }

    // Log de-dupe state for diagnostic dumps. Scoped per-variant so each
    // variant handles its own state-code convention without cross-talk.
    private int lastLoggedCallPromptState = -1;
    private int lastLoggedMeldHandCount = -1;

    /// <summary>
    /// Fingerprint check. Passes when:
    ///   1. self-score word at +0x0500 is in the plausible mahjong range
    ///   2. call-modal host node (id=104) exists
    ///   3. every populated hand slot at +0x0DB8 decodes to a valid tile_id
    ///      under <see cref="TileTextureBase"/>
    ///
    /// Check (3) is what distinguishes Emj from EmjL on a live hand: tile
    /// encodings use non-overlapping texture ranges, so a hand populated
    /// with Emj textures (76041+) fails EmjL's probe and vice versa. When
    /// the hand is empty (between rounds) no tile check runs and the
    /// <see cref="VariantSelector"/> falls back to a name-based tiebreaker.
    /// </summary>
    public unsafe bool Probe(AtkUnitBase* unit)
    {
        if (unit == null || unit->RootNode == null) return false;

        int selfScore = *(int*)((byte*)unit + 0x0500);
        if (selfScore is < 0 or > 200_000) return false;

        if (unit->GetNodeById(104) == null) return false;

        // Hand-tile encoding check: stop at the first empty slot (matches the
        // reader's break-on-zero semantics; slots 0..12 are contiguous with
        // any trailing slot 13 for the just-drawn tile). Any populated slot
        // whose decode falls outside [0, 34) proves this variant's
        // TileTextureBase is wrong for this addon.
        byte* basePtr = (byte*)unit;
        for (int i = 0; i < 14; i++)
        {
            int raw = *(int*)(basePtr + 0xDB8 + i * 4);
            if (raw == 0) break;
            int tileId = raw - TileTextureBase;
            if (tileId < 0 || tileId >= Tile.Count34) return false;
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Offset layout (addon base):
    ///   +0x0500   self score (int32)
    ///   +0x07E0   shimocha score
    ///   +0x0AC0   toimen score
    ///   +0x0DA0   kamicha score
    ///   +0x0DB8   14 hand-tile slots, 4 bytes each. Read as little-endian
    ///             int32 giving <see cref="TileTextureBase"/> + tile_id for
    ///             populated slots, zero for empty slots. Slots 0-12 sorted
    ///             ascending; slot 13 holds the last-drawn tile when 14.
    /// </remarks>
    public unsafe StateSnapshot? TryBuildSnapshot(AtkUnitBase* unit, VariantReadContext ctx)
    {
        nint addr = (nint)unit;
        byte* basePtr = (byte*)addr;

        // Hand tiles at +0x0DB8. Read as little-endian int32 = texture id, then
        // subtract the variant's TileTextureBase to recover tile_id. An older
        // revision used byte math (byte[0] - 9) which was a shortcut that
        // worked for Emj's 76041 base because byte[1] stayed constant at
        // 0x29 across the tile range; it silently failed on EmjL (base 76003)
        // where byte[1] flips between 0x28 and 0x29 mid-range. The int32 path
        // is the root-correct decoding — one line per variant now supplies
        // the only client-specific constant.
        var hand = new List<Tile>(14);
        for (int i = 0; i < 14; i++)
        {
            int raw = *(int*)(basePtr + 0xDB8 + i * 4);
            if (raw == 0) break;
            int tileId = raw - TileTextureBase;
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
            && atkValues[0].Type == ValueType.Int)
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

        MaybeLogCallPromptTransition(ctx, addr, stateCode, atkValues, atkCount, hand, legal);
        MaybeLogMeldTransition(ctx, addr, stateCode, hand);

        // Resolve our own open melds. The addon's on-disk meld struct is still
        // un-mapped; instead the MeldTracker captures each meld when the auto-play
        // (or hooked manual click) accepts a call prompt. Reset the tracker when the
        // closed-hand count proves a new round has started (≥ 13 = no melds).
        // Defensive copy: StateSnapshot is documented as immutable, so don't hand
        // out a view that aliases the tracker's mutable internal list.
        ctx.MeldTracker.ResetIfRoundEnded(hand.Count);
        var ourMelds = ctx.MeldTracker.Melds.ToArray();

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
    /// entry captures the full AtkValues plus a chunk of addon memory so we can
    /// diff across prompts and pin the claimed-tile offset (needed to unblock chi
    /// auto-accept).
    ///
    /// <para>Gated by <c>/mjauto log on</c> so it's off by default.</para>
    /// </summary>
    private unsafe void MaybeLogCallPromptTransition(
        VariantReadContext ctx, nint addonBase, int stateCode, AtkValue* atkValues, int atkCount,
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

        if (!ctx.EventLogger.Enabled) return;
        if (atkValues == null) return;

        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-call-prompts.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {DateTime.UtcNow:o}  variant={Name}  state=15  atkCount={atkCount}");
            sb.Append($"hand={Tiles.Render(hand)}  flags={legal.Flags}  ");
            sb.AppendLine(
                $"pon={legal.PonCandidates.Count} chi={legal.ChiCandidates.Count} kan={legal.KanCandidates.Count}");

            for (int i = 0; i < atkCount && i < 64; i++)
            {
                var v = atkValues[i];
                sb.Append($"  [{i,3}] {v.Type,-14} ");
                switch (v.Type)
                {
                    case ValueType.Int:
                        sb.Append($"Int={v.Int}");
                        break;
                    case ValueType.UInt:
                        sb.Append($"UInt={v.UInt} (0x{v.UInt:X})");
                        break;
                    case ValueType.Bool:
                        sb.Append($"Bool={v.Byte != 0}");
                        break;
                    case ValueType.String:
                    case ValueType.String8:
                    case ValueType.ManagedString:
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
        VariantReadContext ctx, nint addonBase, int stateCode, IReadOnlyList<Tile> hand)
    {
        // Only interesting when we've called something (closed hand < 13 proves open melds).
        if (hand.Count >= 13 || hand.Count <= 0)
        {
            lastLoggedMeldHandCount = -1;
            return;
        }
        if (hand.Count == lastLoggedMeldHandCount) return;
        lastLoggedMeldHandCount = hand.Count;

        if (!ctx.EventLogger.Enabled) return;

        try
        {
            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-meld-captures.log");

            var sb = new System.Text.StringBuilder();
            int inferredMelds = (14 - hand.Count) / 3;
            int remainder = (14 - hand.Count) % 3;
            sb.AppendLine(
                $"# {DateTime.UtcNow:o}  variant={Name}  state={stateCode}  closedHand={hand.Count}  " +
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
    /// Dump two regions of addon memory where the claimed tile likely lives:
    /// +0x0100..+0x0500 (header / prompt payload area, before the score region)
    /// and +0x0E00..+0x1200 (just past the hand tiles at +0xDB8, likely opponent
    /// discard-pool territory).
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
    ///       from <c>AtkValues[19]</c> (texture id = tile_id + <see cref="TileTextureBase"/>)
    ///       and the <see cref="CallCandidateDeriver"/> emits per-variant candidates. Ron/Riichi/Tsumo
    ///       are flag-only — the policy and AutoPlayLoop treat them as accept-via-opt-0.</item>
    ///   <item>Hand count satisfies <c>% 3 == 2</c> (14/11/8/5/2): plain discard. This covers
    ///       pre-discard for 0..4 open melds.</item>
    ///   <item>Otherwise: no actions.</item>
    /// </list>
    /// </summary>
    private unsafe LegalActions BuildLegalActions(
        AtkUnitBase* unit, int stateCode, List<Tile> hand, AtkValue* atkValues, int atkCount)
    {
        // Call-prompt states 15 / 6 / 28 all share the same modal-shell structure
        // (id=104 host → id=3 shell) but differ in where the button labels live:
        //  - Classic state-15 pon/chi/kan/ron: button-label Strings in parent AtkValues
        //    (scanned by BuildCallPromptLegal); also carries the claimed-tile texture
        //    at AtkValues[19] for chi-candidate derivation, which only that path does.
        //  - State-6 self-declarations and state-28 novice-table prompts: the shell is
        //    an AtkComponentList and labels live inside child ListItemRenderer text
        //    nodes (read by BuildCallPromptLegalFromListItems). Parent AtkValues 0..19
        //    carry only Ints/Bools in this mode — the string-scan finds nothing.
        // The shell-visible gate filters out idle frames where opponents' banner text
        // ("Pon!", "Riichi!") would otherwise trick the scan into thinking it's a
        // prompt. Try AtkValues first so state-15 keeps its candidate derivation; fall
        // back to list-item reading whenever the scan finds only the Pass flag.
        if ((stateCode == 15 || stateCode == 6 || stateCode == 28)
            && IsCallModalVisible(unit))
        {
            const ActionFlags acceptMask =
                ActionFlags.Pon | ActionFlags.Chi |
                ActionFlags.MinKan | ActionFlags.ShouMinKan |
                ActionFlags.Ron | ActionFlags.Riichi | ActionFlags.Tsumo;
            if (atkValues != null)
            {
                var scanned = BuildCallPromptLegal(hand, atkValues, atkCount);
                if ((scanned.Flags & acceptMask) != 0)
                    return scanned;
            }
            return BuildCallPromptLegalFromListItems(unit);
        }

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
            && shell->NodeFlags.HasFlag(NodeFlags.Visible);
    }

    private unsafe LegalActions BuildCallPromptLegal(
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
            if (v.Type != ValueType.String &&
                v.Type != ValueType.String8 &&
                v.Type != ValueType.ManagedString)
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
        // candidate alongside pon would risk misfiring.
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

        // Chi: claimed tile is at AtkValues[19], encoded as texture_id =
        // tile_id + TileTextureBase. Verified across two live chi prompts on
        // Emj (base 76041):
        //   - claim 1m ⇄ AtkValues[19]=76041 (hand 1234m89s45z → {1m,2m,3m})
        //   - claim 7s ⇄ AtkValues[19]=76065 (hand 2347m257p2567s57z → {5s,6s,7s})
        // EmjL (base 76003) follows the same pattern with its own base; the
        // pon layout reuses [19] for something else, so we only read it when
        // the chi button is offered.
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
                atkValues[19].Type == ValueType.Int)
            {
                int tex = atkValues[19].Int;
                int tileId = tex - TileTextureBase;
                if (tileId >= 0 && tileId < Tile.Count34)
                {
                    var claimed = Tile.FromId(tileId);
                    var derived = CallCandidateDeriver.Derive(hand, claimed, fromSeat: 3);
                    chis.AddRange(derived.Chi);
                }
            }
        }

        return new LegalActions(flags, [], pons, chis, kans);
    }

    /// <summary>
    /// Build LegalActions for an AtkComponentList-based call prompt (state 28 today).
    /// Reads each visible ListItemRenderer child of the modal shell and maps its text
    /// label to an <see cref="ActionFlags"/> bit. No meld candidates are derived —
    /// state-28 prompts seen so far are Riichi/Pass only, where the accept path is
    /// opt 0 regardless of candidates.
    /// </summary>
    private static unsafe LegalActions BuildCallPromptLegalFromListItems(AtkUnitBase* unit)
    {
        var labels = ReadVisibleListItemLabels(unit);
        if (labels.Count == 0)
            return new LegalActions(ActionFlags.Pass, [], [], [], []);

        ActionFlags flags = ActionFlags.Pass;
        foreach (var raw in labels)
        {
            switch (raw.Trim())
            {
                case "Pon": flags |= ActionFlags.Pon; break;
                case "Chi": flags |= ActionFlags.Chi; break;
                case "Kan": flags |= ActionFlags.MinKan; break;
                case "Ron": flags |= ActionFlags.Ron; break;
                case "Riichi": flags |= ActionFlags.Riichi; break;
                case "Tsumo": flags |= ActionFlags.Tsumo; break;
                // "Pass" / "Cancel" contribute no accept flag — AutoPlayLoop derives
                // the pass option index from the count of accept flags set.
            }
        }
        return new LegalActions(flags, [], [], [], []);
    }

    /// <summary>
    /// Read the labels of visible ListItemRenderer children under the modal shell
    /// (host id=104 → inner id=3 AtkComponentList). Returns labels in top-to-bottom
    /// visual order so the caller can rely on "packed" option indices matching the
    /// FireCallback([11, N]) convention (N = visual position of the clicked button).
    /// </summary>
    private static unsafe List<string> ReadVisibleListItemLabels(AtkUnitBase* unit)
    {
        var labels = new List<string>();
        if (unit == null) return labels;

        var host = unit->GetNodeById(104);
        if (host == null || (int)host->Type < 1000) return labels;
        var hostComp = ((AtkComponentNode*)host)->Component;
        if (hostComp == null) return labels;
        var shell = hostComp->GetNodeById(3);
        if (shell == null || (int)shell->Type < 1000) return labels;
        var shellComp = ((AtkComponentNode*)shell)->Component;
        if (shellComp == null) return labels;

        var ulm = shellComp->UldManager;
        if (ulm.NodeList == null || ulm.NodeListCount == 0) return labels;

        // Collect (Y coord, text) for every visible component child. Component
        // nodes (Type ≥ 1000) are the list-item renderers — image/nine-grid
        // children are list chrome (background, highlight) and ignored.
        var items = new List<(float y, string text)>();
        for (int i = 0; i < ulm.NodeListCount; i++)
        {
            var node = ulm.NodeList[i];
            if (node == null) continue;
            if ((int)node->Type < 1000) continue;
            if (!node->NodeFlags.HasFlag(NodeFlags.Visible))
                continue;
            var itemComp = ((AtkComponentNode*)node)->Component;
            if (itemComp == null) continue;
            string text = FindFirstTextInComponent(itemComp) ?? string.Empty;
            items.Add((node->Y, text));
        }
        // Sort top-to-bottom: the game's FireCallback option index for a list
        // click follows visual order (opt 0 = top button). NodeList order isn't
        // guaranteed to match visual order when item renderers are recycled.
        items.Sort((a, b) => a.y.CompareTo(b.y));
        foreach (var (_, t) in items) labels.Add(t);
        return labels;
    }

    /// <summary>
    /// Find the first non-empty AtkTextNode text in a component's flat NodeList.
    /// Each ListItemRenderer holds its label in a single text child; we don't need
    /// to recurse into nested components.
    /// </summary>
    private static unsafe string? FindFirstTextInComponent(AtkComponentBase* comp)
    {
        if (comp == null) return null;
        var ulm = comp->UldManager;
        if (ulm.NodeList == null || ulm.NodeListCount == 0) return null;
        for (int i = 0; i < ulm.NodeListCount; i++)
        {
            var node = ulm.NodeList[i];
            if (node == null) continue;
            if (node->Type != NodeType.Text)
                continue;
            var textNode = (AtkTextNode*)node;
            var s = textNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }
}

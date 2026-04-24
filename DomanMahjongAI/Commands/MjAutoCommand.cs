using Dalamud.Game.Command;
using DomanMahjongAI.GameState;
using DomanMahjongAI.GameState.Variants;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace DomanMahjongAI.Commands;

public sealed class MjAutoCommand : IDisposable
{
    private const string Primary = "/mjauto";
    private const string HelpText = "Open Doman Mahjong Solver. Subcommands: on | off | open | debug | policy <eff|mcts> | pass <N> | dump | addons [filter] | dumpmem [offset] [length] | atkvalues | agent [length] | emj [length] | snap <label> | autosnap <on|off> | walknodes | log <on|off> | capture <label> | variant dump | testdiscard <slot> | autodiscard";
    // Note: removed /mjauto scan and /mjauto followptr — both dereferenced untrusted pointers and crashed the client.

    private readonly Plugin plugin;

    // Auto-snap state: hash-dedup'd, rate-limited, capped capture driven off the
    // addon's ObservationChanged event. Lets the user capture every meaningful
    // state change without typing commands between fast turns.
    private bool autoSnapOn;
    private int autoSnapCounter;
    private long autoSnapLastMs;
    private ulong autoSnapLastHash;
    private const int AutoSnapMinGapMs = 150;
    private const int AutoSnapMaxCount = 500;

    public MjAutoCommand(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.CommandManager.AddHandler(Primary, new CommandInfo(OnCommand)
        {
            HelpMessage = HelpText,
            ShowInHelp = true,
        });
    }

    public void Dispose()
    {
        if (autoSnapOn)
        {
            plugin.AddonReader.ObservationChanged -= OnAutoSnapObservation;
            autoSnapOn = false;
        }
        Plugin.CommandManager.RemoveHandler(Primary);
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? parts[1] : string.Empty;

        switch (sub)
        {
            case "":
            case "open":
                plugin.ToggleMainWindow();
                break;

            case "on":
                plugin.Configuration.AutomationArmed = true;
                plugin.Configuration.Save();
                Plugin.ChatGui.Print("[MjAuto] Automation armed.");
                break;

            case "off":
                plugin.Configuration.AutomationArmed = false;
                plugin.Configuration.Save();
                Plugin.ChatGui.Print("[MjAuto] Automation disarmed.");
                break;

            case "debug":
                plugin.ToggleDebugOverlay();
                break;

            case "policy":
                HandlePolicy(rest);
                break;

            case "dump":
                Plugin.ChatGui.Print("[MjAuto] State dump not yet implemented.");
                break;

            case "addons":
                DumpAddons(rest);
                break;

            case "dumpmem":
                DumpMemory(rest);
                break;

            case "atkvalues":
                DumpAtkValues();
                break;

            case "agent":
                DumpAgent(rest);
                break;

            case "emj":
                DumpEmjModule(rest);
                break;

            case "snap":
                HandleSnap(rest);
                break;

            case "autosnap":
                HandleAutoSnap(rest);
                break;

            case "walknodes":
                HandleWalkNodes();
                break;

            case "log":
                HandleLog(rest);
                break;

            case "capture":
                HandleCapture(rest);
                break;

            case "variant":
                HandleVariant(rest);
                break;

            case "testdiscard":
                HandleTestDiscard(rest);
                break;

            case "pass":
                HandlePass(rest);
                break;

            case "autodiscard":
                HandleAutoDiscard();
                break;


            default:
                Plugin.ChatGui.PrintError($"[MjAuto] Unknown subcommand: {sub}. {HelpText}");
                break;
        }
    }

    private void HandleAutoDiscard()
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null)
            {
                Plugin.ChatGui.PrintError("[MjAuto] no snapshot — not in a match.");
                return;
            }

            if (!snap.Legal.Can(DomanMahjongAI.Engine.ActionFlags.Discard))
            {
                Plugin.ChatGui.PrintError(
                    $"[MjAuto] hand has {snap.Hand.Count} tiles — not a discard state. Wait for your turn.");
                return;
            }

            var choice = plugin.Policy.Choose(snap);
            if (choice.Kind != DomanMahjongAI.Policy.ActionKind.Discard || choice.DiscardTile is null)
            {
                Plugin.ChatGui.PrintError(
                    $"[MjAuto] policy returned {choice.Kind} — autodiscard only handles Discard. {choice.Reasoning}");
                return;
            }

            var tile = choice.DiscardTile.Value;
            int slot = Actions.InputDispatcher.FindSlotOfTile(tile, snap.Hand);
            if (slot < 0)
            {
                Plugin.ChatGui.PrintError($"[MjAuto] tile {tile} not found in hand — internal error.");
                return;
            }

            var delay = Actions.HumanTiming.RandomDelay();
            Plugin.ChatGui.Print(
                $"[MjAuto] auto-discarding {tile} (slot {slot}) in {delay.TotalMilliseconds:F0}ms. {choice.Reasoning}");

            _ = Plugin.Framework.RunOnTick(() =>
            {
                var result = plugin.Dispatcher.DispatchDiscard(slot);
                Plugin.ChatGui.Print($"[MjAuto] dispatch result: {result}");
            }, delay);
        });
    }

    private void HandlePass(string arg)
    {
        // Manual override for when the auto pass clicks the wrong option
        // (multi-chi prompts confuse our hardcoded pass=option 1).
        if (!int.TryParse(arg.Trim(), out int opt) || opt is < 0 or > 5)
        {
            Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto pass <0..5>  (0=leftmost, higher=rightward; rightmost = pass)");
            return;
        }
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var result = plugin.Dispatcher.DispatchCallOption(opt);
            Plugin.ChatGui.Print($"[MjAuto] pass opt={opt} → {result}");
        });
    }

    private void HandleTestDiscard(string arg)
    {
        if (!int.TryParse(arg.Trim(), out int slot) || slot is < 0 or > 13)
        {
            Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto testdiscard <0..13>");
            return;
        }

        // Must dispatch on the framework thread.
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var result = plugin.Dispatcher.DispatchDiscard(slot);
            Plugin.ChatGui.Print($"[MjAuto] testdiscard slot={slot} result={result}");
        });
    }

    private void HandleCapture(string arg)
    {
        var label = arg.Trim();
        if (string.IsNullOrEmpty(label))
        {
            var pending = plugin.EventLogger.PendingCaptureLabel;
            if (pending != null)
            {
                plugin.EventLogger.DisarmCapture();
                Plugin.ChatGui.Print($"[MjAuto] capture disarmed (was: {pending}).");
            }
            else
            {
                Plugin.ChatGui.Print(
                    $"[MjAuto] Usage: /mjauto capture <label>. Run again with no label to disarm. " +
                    $"File: {plugin.EventLogger.CaptureLogPath}");
            }
            return;
        }

        // Sanitize: file gets greppable, but the label appears verbatim in the
        // capture entry header so reject anything weird.
        foreach (var c in label)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                Plugin.ChatGui.PrintError(
                    $"[MjAuto] capture label must be [a-zA-Z0-9_-] only — got '{label}'.");
                return;
            }
        }

        if (plugin.Configuration.AutomationArmed)
        {
            Plugin.ChatGui.PrintError(
                "[MjAuto] auto-play is ON — its dispatches would race your manual click. " +
                "Run `/mjauto off` first, then re-arm capture.");
            return;
        }

        plugin.EventLogger.ArmCapture(label);
        Plugin.ChatGui.Print(
            $"[MjAuto] capture armed: '{label}'. Click the action in-game once. " +
            $"Auto-disarms after one click or 60s. File: {plugin.EventLogger.CaptureLogPath}");
    }

    /// <summary>
    /// Variant-diagnostic subcommand. Today the only action is <c>dump</c>, which
    /// writes one self-contained file describing the current Mahjong addon's
    /// layout — enough for a maintainer to build a new <see cref="IEmjVariant"/>
    /// from without live client access (issue #13 workflow).
    ///
    /// <para>Room intentionally left for future actions (<c>probe</c>, <c>list</c>)
    /// without disturbing the subcommand grammar.</para>
    /// </summary>
    private void HandleVariant(string arg)
    {
        var parts = arg.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "dump":
                DumpVariant();
                break;
            case "":
                Plugin.ChatGui.Print("[MjAuto] Usage: /mjauto variant dump");
                break;
            default:
                Plugin.ChatGui.PrintError(
                    $"[MjAuto] Unknown variant subcommand: '{sub}'. Known: dump.");
                break;
        }
    }

    /// <summary>
    /// Write one self-contained layout dump for the live Mahjong addon. Bundles
    /// the per-variant probe verdicts, AtkUnitBase metadata, the full AtkValues
    /// array, the flat NodeList plus every visible component's inner tree, a
    /// 1 KiB memory sample around the known Emj offset hotspots (scores at
    /// +0x0500/7E0/AC0/DA0 and hand at +0x0DB8), and a small AgentEmj header
    /// sample for cross-reference.
    ///
    /// <para>Intended output: a single file an external reporter can attach to
    /// issue #13 when their client doesn't match any registered variant. The
    /// maintainer then builds the corresponding <see cref="IEmjVariant"/> by
    /// diffing offsets/IDs against the reference Emj dump.</para>
    /// </summary>
    private unsafe void DumpVariant()
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!MahjongAddon.TryGet(out var unit, out var resolvedName))
            {
                Plugin.ChatGui.PrintError(
                    "[MjAuto] Mahjong addon not found — seat at a table first, then retry.");
                return;
            }

            var sb = new System.Text.StringBuilder();
            var now = DateTime.UtcNow;
            nint addonAddr = (nint)unit;

            // Header: everything needed to identify the capture without opening the file.
            sb.AppendLine($"# Emj variant dump — utc={now:o}");
            sb.AppendLine(
                $"# Resolved addon name: \"{resolvedName}\"  " +
                $"(candidates: {string.Join(", ", MahjongAddon.CandidateNames)})");
            sb.AppendLine($"# Addon pointer: 0x{addonAddr:X}  visible={unit->IsVisible}");
            if (unit->RootNode != null)
                sb.AppendLine(
                    $"# Root size: {unit->RootNode->Width}x{unit->RootNode->Height}");
            sb.AppendLine(
                $"# AtkValuesCount={unit->AtkValuesCount}  " +
                $"NodeListCount={unit->UldManager.NodeListCount}  " +
                $"LoadedState={unit->UldManager.LoadedState}");
            sb.AppendLine();

            // Probe results: top-of-file so the reader sees the verdict first.
            sb.AppendLine("## Variant probe results");
            var selectorVariants = plugin.AddonReader.Selector.Variants;
            if (selectorVariants.Count == 0)
            {
                sb.AppendLine("  (no variants registered)");
            }
            else
            {
                foreach (var v in selectorVariants)
                {
                    bool matched = false;
                    try { matched = v.Probe(unit); }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  {v.Name,-20} THREW: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }
                    sb.AppendLine($"  {v.Name,-20} {(matched ? "MATCH" : "miss")}");
                }
            }
            sb.AppendLine();

            // AtkValues: slot number, type, decoded value. First 64 only so strings
            // don't blow up the file, but the full count is in the header above.
            sb.AppendLine("## AtkValues");
            var values = unit->AtkValues;
            int atkCount = unit->AtkValuesCount;
            if (values == null || atkCount == 0)
            {
                sb.AppendLine("  (null or empty)");
            }
            else
            {
                int cap = Math.Min(atkCount, 64);
                for (int i = 0; i < cap; i++)
                    sb.AppendLine($"  [{i,3}] {FormatAtkValue(values[i])}");
                if (atkCount > cap)
                    sb.AppendLine($"  (... {atkCount - cap} more omitted)");
            }
            sb.AppendLine();

            // Flat NodeList — reuse WriteNodeRow so the format matches /mjauto walknodes.
            sb.AppendLine("## NodeList (flat)");
            var mgr = unit->UldManager;
            for (int i = 0; i < mgr.NodeListCount; i++)
            {
                var n = mgr.NodeList[i];
                if (n == null)
                {
                    sb.AppendLine($"  [{i,4}] null");
                    continue;
                }
                WriteNodeRow(sb, i, n);
            }
            sb.AppendLine();

            // Component inner trees — only for VISIBLE type>=1000 nodes. Invisible
            // components balloon the file without contributing signal for layout
            // disambiguation (hidden prompts share their tree with every other state).
            sb.AppendLine("## Component inner trees (visible, type >= 1000)");
            for (int i = 0; i < mgr.NodeListCount; i++)
            {
                var n = mgr.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type < 1000) continue;
                if (!n->NodeFlags.HasFlag(NodeFlags.Visible)) continue;

                var compNode = (AtkComponentNode*)n;
                var comp = compNode->Component;
                if (comp == null) continue;
                var subMgr = comp->UldManager;
                sb.AppendLine(
                    $"  # [{i,4}] type={n->Type} id={n->NodeId} @0x{(nint)n:X}  " +
                    $"subCount={subMgr.NodeListCount}  comp=0x{(nint)comp:X}");

                if (subMgr.NodeList == null || subMgr.NodeListCount == 0) continue;
                for (int j = 0; j < subMgr.NodeListCount; j++)
                {
                    var sn = subMgr.NodeList[j];
                    if (sn == null)
                    {
                        sb.AppendLine($"    sub[{j,3}] null");
                        continue;
                    }
                    sb.Append("    ");
                    WriteNodeRow(sb, j, sn);
                }
            }
            sb.AppendLine();

            // Memory sample — covers the known Emj offset hotspots so a reporter
            // can eyeball whether their client has comparable values in the same
            // places (score words, discard-count bytes, hand tile array).
            sb.AppendLine("## Addon memory sample (+0x0400..+0x0E80)");
            byte* basePtr = (byte*)addonAddr;
            for (int off = 0x0400; off < 0x0E80; off += 16)
                AppendHexRow(sb, basePtr, off, 16);
            sb.AppendLine();

            // AgentEmj header — small sample for cross-reference. Full dump is
            // still available via `/mjauto agent` if the variant work needs more.
            sb.AppendLine("## AgentEmj header sample (+0x0000..+0x0200)");
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                sb.AppendLine("  (AgentModule unavailable)");
            }
            else
            {
                var agent = agentModule->GetAgentByInternalId((AgentId)5);
                if (agent == null)
                {
                    sb.AppendLine("  (AgentEmj not found — GetAgentByInternalId(5) returned null)");
                }
                else
                {
                    sb.AppendLine($"  # AgentEmj @ 0x{(nint)agent:X}");
                    byte* agentPtr = (byte*)agent;
                    for (int off = 0; off < 0x0200; off += 16)
                        AppendHexRow(sb, agentPtr, off, 16);
                }
            }

            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-variant-dump.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Plugin.ChatGui.Print(
                $"[MjAuto] variant dump → {path}. " +
                $"Attach this file to issue #13 when reporting a new client variant.");
        });
    }

    /// <summary>
    /// Render one <see cref="AtkValue"/> as a single-line string suitable for
    /// the variant dump. Keeps strings length-capped so one weirdly-long
    /// ManagedString doesn't torpedo the file layout.
    /// </summary>
    private static unsafe string FormatAtkValue(AtkValue v)
    {
        switch (v.Type)
        {
            case ValueType.Int:
                return $"{v.Type,-14} Int={v.Int}";
            case ValueType.UInt:
                return $"{v.Type,-14} UInt={v.UInt} (0x{v.UInt:X})";
            case ValueType.Bool:
                return $"{v.Type,-14} Bool={v.Byte != 0}";
            case ValueType.String:
            case ValueType.String8:
            case ValueType.ManagedString:
                if (v.String.Value == null) return $"{v.Type,-14} (null)";
                var s = System.Text.Encoding.UTF8.GetString(v.String);
                if (s.Length > 80) s = s[..80] + "...";
                return $"{v.Type,-14} \"{s.Replace("\n", "\\n")}\"";
            default:
                return $"{v.Type,-14} raw=0x{v.UInt:X}";
        }
    }

    /// <summary>
    /// RE scratch tool: write one self-contained file capturing every known source
    /// of Emj state right now — addon bytes (0..0x3000), AgentEmj bytes (0..0x2000),
    /// AtkValues, current parsed hand and scores. Files are named with a label +
    /// timestamp so successive calls don't overwrite. Diff two snaps taken at known
    /// game moments (e.g. "before kamicha 8m discard" / "after") to pin the
    /// offset of the field that changed.
    /// </summary>
    private unsafe void HandleSnap(string arg)
    {
        var label = arg.Trim();
        if (string.IsNullOrEmpty(label))
        {
            Plugin.ChatGui.PrintError(
                "[MjAuto] Usage: /mjauto snap <label>  (label: [a-zA-Z0-9_-] only)");
            return;
        }
        foreach (var c in label)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                Plugin.ChatGui.PrintError(
                    $"[MjAuto] snap label must be [a-zA-Z0-9_-] only — got '{label}'.");
                return;
            }
        }

        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var path = WriteSnapFile(label, verbose: true);
            if (path != null)
                Plugin.ChatGui.Print($"[MjAuto] snap '{label}' → {path}");
        });
    }

    /// <summary>
    /// Toggles auto-snap: while on, writes a snap file on every addon observation
    /// where the raw memory (addon +0x500..+0x3000) has actually changed since the
    /// last snap, with a minimum gap of <see cref="AutoSnapMinGapMs"/> ms and a hard
    /// cap of <see cref="AutoSnapMaxCount"/> files per session. Hash-based dedup
    /// filters animation-only redraws; the rate limit absorbs redraw bursts that
    /// happen to touch the hashed region (rare). Files sort chronologically by the
    /// timestamp suffix — read them in order to reconstruct the event stream.
    /// </summary>
    private void HandleAutoSnap(string arg)
    {
        var v = arg.Trim().ToLowerInvariant();
        switch (v)
        {
            case "on":
                if (autoSnapOn)
                {
                    Plugin.ChatGui.Print("[MjAuto] autosnap already ON.");
                    return;
                }
                autoSnapOn = true;
                autoSnapCounter = 0;
                autoSnapLastHash = 0;
                autoSnapLastMs = 0;
                plugin.AddonReader.ObservationChanged += OnAutoSnapObservation;
                Plugin.ChatGui.Print(
                    $"[MjAuto] autosnap ON. Hash-deduped, min gap {AutoSnapMinGapMs}ms, cap {AutoSnapMaxCount}. " +
                    $"Files: snap-auto-NNN-<ts>.txt in plugin config dir.");
                break;

            case "off":
                if (!autoSnapOn)
                {
                    Plugin.ChatGui.Print("[MjAuto] autosnap already OFF.");
                    return;
                }
                autoSnapOn = false;
                plugin.AddonReader.ObservationChanged -= OnAutoSnapObservation;
                Plugin.ChatGui.Print($"[MjAuto] autosnap OFF. Wrote {autoSnapCounter} file(s).");
                break;

            case "":
                Plugin.ChatGui.Print(
                    $"[MjAuto] autosnap is {(autoSnapOn ? "ON" : "OFF")} " +
                    $"(wrote {autoSnapCounter}/{AutoSnapMaxCount}).");
                break;

            default:
                Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto autosnap <on|off>");
                break;
        }
    }

    private unsafe void OnAutoSnapObservation(AddonEmjObservation obs)
    {
        if (!autoSnapOn) return;
        if (!obs.Present || obs.Address == 0) return;
        if (autoSnapCounter >= AutoSnapMaxCount)
        {
            // Auto-disarm at the cap so we don't silently drop events forever.
            autoSnapOn = false;
            plugin.AddonReader.ObservationChanged -= OnAutoSnapObservation;
            Plugin.ChatGui.Print(
                $"[MjAuto] autosnap hit cap ({AutoSnapMaxCount}) — auto-disarmed. " +
                $"Toggle off then on to reset.");
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - autoSnapLastMs < AutoSnapMinGapMs) return;

        // FNV-1a 64-bit hash over addon + agent bytes only. Earlier versions
        // also hashed memory behind agent-slot pointers to catch game-state
        // changes that don't touch the agent itself — but dereferencing
        // pointers from agent memory with only a range-and-alignment heuristic
        // is the same failure mode that got /mjauto scan and followptr removed
        // (unmapped/stale targets crash the client). Hashing addon+agent is
        // enough to trigger a capture on any meaningful UI state change, and
        // the dump-time walker (which fires when the user explicitly runs
        // /mjauto snap) still inspects pointer targets for RE diagnostics.
        ulong hash = 1469598103934665603UL; // FNV offset basis
        byte* addonPtr = (byte*)obs.Address;
        for (int i = 0x0500; i < 0x3000; i++)
            hash = (hash ^ addonPtr[i]) * 1099511628211UL;
        var agentModule = AgentModule.Instance();
        if (agentModule != null)
        {
            var agent = agentModule->GetAgentByInternalId((AgentId)5);
            if (agent != null)
            {
                byte* agentPtr = (byte*)agent;
                for (int i = 0; i < 0x3000; i++)
                    hash = (hash ^ agentPtr[i]) * 1099511628211UL;
            }
        }
        if (hash == autoSnapLastHash) return;

        autoSnapLastHash = hash;
        autoSnapLastMs = nowMs;
        var label = $"auto-{autoSnapCounter:D3}";
        autoSnapCounter++;
        // Quiet write — don't spam chat while the user is playing.
        WriteSnapFile(label, verbose: false);
    }

    /// <summary>
    /// Shared file-writer for <c>/mjauto snap &lt;label&gt;</c> and auto-snap.
    /// Must be called on the framework thread. Returns the written path, or null
    /// if the addon wasn't available.
    /// </summary>
    private unsafe string? WriteSnapFile(string label, bool verbose)
    {
        if (!MahjongAddon.TryGet(out var unit, out _))
        {
            if (verbose)
                Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found — open a table first.");
            return null;
        }
        nint addonAddr = (nint)unit;

        var sb = new System.Text.StringBuilder();
        var now = DateTime.UtcNow;
        string ts = now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);

        sb.AppendLine($"# SNAP label='{label}'  utc={now:o}  addon=0x{addonAddr:X}");

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap != null)
        {
            sb.AppendLine(
                $"  hand={DomanMahjongAI.Engine.Tiles.Render(snap.Hand)}  " +
                $"wall={snap.WallRemaining}  scores=[{string.Join(",", snap.Scores)}]  " +
                $"legal={snap.Legal.Flags}");
        }
        else
        {
            sb.AppendLine("  (TryBuildSnapshot returned null — addon not visible yet)");
        }

        var atkValues = unit->AtkValues;
        int atkCount = unit->AtkValuesCount;
        int stateCode = -1;
        if (atkValues != null && atkCount > 0
            && atkValues[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int)
            stateCode = atkValues[0].Int;
        sb.AppendLine($"  stateCode={stateCode}  atkValuesCount={atkCount}");

        sb.AppendLine("  -- AtkValues --");
        if (atkValues != null)
        {
            for (int i = 0; i < atkCount && i < 128; i++)
            {
                var v = atkValues[i];
                sb.Append($"  [{i,3}] {v.Type,-14} ");
                switch (v.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                        sb.Append($"Int={v.Int}"); break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                        sb.Append($"UInt={v.UInt} (0x{v.UInt:X})"); break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                        sb.Append($"Bool={v.Byte != 0}"); break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8:
                    case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                        var s = v.String.Value != null
                            ? System.Text.Encoding.UTF8.GetString(v.String) : "(null)";
                        sb.Append($"String=\"{s}\""); break;
                    default:
                        sb.Append($"raw=0x{v.UInt:X}"); break;
                }
                sb.AppendLine();
            }
        }

        // Addon dump: extended to 0x6000 to catch any post-AtkUnitBase fields the
        // UI might tack on. Prior 0x3000 cap was arbitrary and tile-pattern scans
        // showed discard pools aren't within it — they're in the game-state module.
        sb.AppendLine("  -- addon @ +0x0000..+0x6000 --");
        byte* addonPtr = (byte*)addonAddr;
        for (int off = 0x0000; off < 0x6000; off += 16)
            AppendHexRow(sb, addonPtr, off, 16);

        var agentModule = AgentModule.Instance();
        if (agentModule != null)
        {
            var agent = agentModule->GetAgentByInternalId((AgentId)5);
            if (agent != null)
            {
                sb.AppendLine($"  -- AgentEmj @ 0x{(nint)agent:X} +0x0000..+0x3000 --");
                byte* agentPtr = (byte*)agent;
                for (int off = 0; off < 0x3000; off += 16)
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

        // Client::Game::UI::Emj game-state module. The static slot at
        // module_base + 0x029E1400 is stale after recent patches (empty in live),
        // so instead we auto-discover by walking the first 0x100 bytes of
        // AgentEmj as candidate 8-byte pointers. Any pointer that looks like a
        // valid FFXIV heap address (0x14xx or similar) and points to non-zero
        // memory gets its first 0x2000 bytes dumped. The game-state struct will
        // be the one that contains tile patterns ([xx, 29, 01, 00]) when scanned.
        sb.AppendLine("  -- Agent-referenced candidate structs --");
        if (agentModule != null)
        {
            var agent = agentModule->GetAgentByInternalId((AgentId)5);
            if (agent != null)
            {
                nint* slots = (nint*)agent;
                int dumped = 0;
                var seen = new System.Collections.Generic.HashSet<nint>();
                // First field is always the vtable; skip slots[0].
                for (int i = 1; i < 32 && dumped < 8; i++)
                {
                    nint p = slots[i];
                    // Heuristic for a valid FFXIV heap pointer: high 16 bits in
                    // the usual user-range (~0x0000_01xx_xxxx_xxxx), 16-byte
                    // aligned, not already seen.
                    if (p == nint.Zero) continue;
                    if ((ulong)p < 0x10000UL || (ulong)p > 0x0000_7FFF_FFFF_FFFFUL) continue;
                    if (((ulong)p & 0xF) != 0) continue;
                    if (!seen.Add(p)) continue;
                    // Guard against unmapped memory: probe first byte via
                    // try/catch isn't possible from unsafe; instead trust that
                    // the aligned-heap-range heuristic is sufficient (agents
                    // don't normally hold stale pointers).
                    bool nonZero = false;
                    byte* pb = (byte*)p;
                    for (int j = 0; j < 16 && !nonZero; j++)
                        if (pb[j] != 0) nonZero = true;
                    if (!nonZero) continue;

                    sb.AppendLine(
                        $"  -- candidate[{i}] @ 0x{p:X}  (agent+0x{i * 8:X2})  +0x0000..+0x2000 --");
                    for (int off = 0; off < 0x2000; off += 16)
                        AppendHexRow(sb, pb, off, 16);
                    dumped++;
                }
                if (dumped == 0)
                    sb.AppendLine("  (no valid pointer candidates found in agent+0..+0x100)");
            }
        }

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"snap-{label}-{ts}.txt");
        System.IO.File.WriteAllText(path, sb.ToString());
        return path;
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
    /// Walk the Emj addon's UldManager.NodeList, dumping every node's index, type,
    /// id, visibility, position, and — for AtkImageNodes — the texture id. Mahjong
    /// tiles render as image nodes with texture ids in the Doman tile range
    /// (76041 + tile_id, same encoding as hand AtkValues). Identifying which nodes
    /// are discard-pool tiles lets us read opponent discards directly from the UI,
    /// bypassing the (stale) game-state module offset.
    /// </summary>
    private unsafe void HandleWalkNodes()
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!MahjongAddon.TryGet(out var unit, out _))
            {
                Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found — open a table first.");
                return;
            }

            nint addonAddr = (nint)unit;
            var mgr = unit->UldManager;
            var sb = new System.Text.StringBuilder();
            var now = DateTime.UtcNow;
            sb.AppendLine($"# walknodes  addon=0x{addonAddr:X}  utc={now:o}");
            sb.AppendLine(
                $"  NodeListCount={mgr.NodeListCount}  ObjectsCount={mgr.ObjectCount}  " +
                $"LoadedState={mgr.LoadedState}");

            // Full flat NodeList. Every tile image on screen is somewhere in here.
            sb.AppendLine();
            sb.AppendLine("## NodeList (flat)");
            for (int i = 0; i < mgr.NodeListCount; i++)
            {
                var n = mgr.NodeList[i];
                if (n == null)
                {
                    sb.AppendLine($"  [{i,4}] null");
                    continue;
                }
                WriteNodeRow(sb, i, n);
            }

            // Recurse into every UldComponent's own inner node list. Custom
            // component types (type >= 1000) carry their tile visuals as
            // children under Component->UldManager — NOT under the addon's flat
            // NodeList — so they have to be inspected separately. For Doman
            // Mahjong the 4 discard-pool seats use custom types 1021..1024
            // (31 slots each) and the "opponent hand" strip is type 1057.
            // Also dump the first 0x100 bytes of each component's raw state;
            // for discard-pool slots the tile identity sits in there and is
            // discoverable by diffing visible vs invisible slots of the same
            // component type.
            sb.AppendLine();
            sb.AppendLine("## Component inner trees (type >= 1000)");
            for (int i = 0; i < mgr.NodeListCount; i++)
            {
                var n = mgr.NodeList[i];
                if (n == null) continue;
                if ((int)n->Type < 1000) continue;
                var compNode = (AtkComponentNode*)n;
                var comp = compNode->Component;
                if (comp == null) continue;
                var sub = comp->UldManager;
                bool visible = n->NodeFlags.HasFlag(
                    FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible);
                sb.AppendLine(
                    $"  # [{i,4}] type={n->Type} id={n->NodeId} @0x{(nint)n:X}  " +
                    $"vis={(visible ? "1" : "0")}  subCount={sub.NodeListCount}  " +
                    $"comp=0x{(nint)comp:X}");

                // Raw component memory — 0x300 bytes, annotated as hex rows.
                // The tile id might live deep in the component's custom state,
                // so the default 0x100 window turned out to be too shallow.
                sb.AppendLine("    -- comp bytes +0x00..+0x300 --");
                byte* cb = (byte*)comp;
                for (int off = 0; off < 0x300; off += 16)
                    AppendHexRow(sb, cb, off, 16);

                // NOTE: an earlier revision dereferenced speculative pointer slots
                // at offsets { 0x18, 0x20, 0x58, 0x80 } and dumped 0x80 bytes from
                // each target to help RE the meld-pointer layout. The range/alignment
                // checks weren't strong enough — the AtkComponentList popup at the
                // state-6 Riichi prompt holds one of those slots as garbage that
                // passed the checks but crashed the client with AccessViolation when
                // read (follow-up repro on issue #22). Meld tracking is driven by
                // the FireCallback hook in InputEventLogger now, not these
                // speculative reads, so the block is removed rather than made
                // SEH-safe.

                if (sub.NodeListCount == 0 || sub.NodeList == null) continue;
                for (int j = 0; j < sub.NodeListCount; j++)
                {
                    var sn = sub.NodeList[j];
                    if (sn == null)
                    {
                        sb.AppendLine($"    sub[{j,3}] null");
                        continue;
                    }
                    sb.Append("    ");
                    WriteNodeRow(sb, j, sn);
                }
            }

            var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "emj-nodes.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Plugin.ChatGui.Print($"[MjAuto] walknodes → {path}  ({mgr.NodeListCount} nodes)");
        });
    }

    private static unsafe void WriteNodeRow(
        System.Text.StringBuilder sb, int index, AtkResNode* n)
    {
        string idxStr = index >= 0 ? $"[{index,4}]" : "     ";
        sb.Append(
            $"  {idxStr} @0x{(nint)n:X}  type={n->Type,-18}  id={n->NodeId,-5}  " +
            $"vis={(n->NodeFlags.HasFlag(FFXIVClientStructs.FFXIV.Component.GUI.NodeFlags.Visible) ? "1" : "0")}  " +
            $"xy=({n->X:F0},{n->Y:F0})  wh=({n->Width},{n->Height})");

        // For image nodes, dig out the texture / part info — this is where the
        // tile identity lives for any visible tile.
        if (n->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Image)
        {
            var img = (FFXIVClientStructs.FFXIV.Component.GUI.AtkImageNode*)n;
            sb.Append($"  partId={img->PartId}");
            var pl = img->PartsList;
            if (pl != null)
            {
                sb.Append($"  partsListId={pl->Id}  partCount={pl->PartCount}");
                if (img->PartId < pl->PartCount && pl->Parts != null)
                {
                    var part = &pl->Parts[img->PartId];
                    sb.Append($"  u/v=({part->U},{part->V})  w/h=({part->Width},{part->Height})");
                    var ui = part->UldAsset;
                    if (ui != null)
                    {
                        sb.Append($"  uldAssetId={ui->Id}");
                    }
                }
            }
        }
        else if (n->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
        {
            var txt = (FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)n;
            var s = txt->NodeText.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                // Truncate; some text nodes hold very long strings.
                if (s.Length > 40) s = s[..40] + "...";
                sb.Append($"  text=\"{s.Replace("\n", "\\n")}\"");
            }
        }
        sb.AppendLine();
    }

    private void HandleLog(string arg)
    {
        var v = arg.Trim().ToLowerInvariant();
        switch (v)
        {
            case "on":
                plugin.EventLogger.Enabled = true;
                plugin.EventLogger.OpenLog();
                Plugin.ChatGui.Print($"[MjAuto] event logger ON. Writing to {plugin.EventLogger.LogPath}");
                break;
            case "off":
                plugin.EventLogger.Enabled = false;
                plugin.EventLogger.CloseLog();
                Plugin.ChatGui.Print("[MjAuto] event logger OFF.");
                break;
            case "":
                Plugin.ChatGui.Print(
                    $"[MjAuto] event logger is {(plugin.EventLogger.Enabled ? "ON" : "OFF")}. " +
                    $"Path: {plugin.EventLogger.LogPath}");
                break;
            default:
                Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto log <on|off>");
                break;
        }
    }

    private unsafe void DumpEmjModule(string args)
    {
        // Static address of Client::Game::UI::Emj per data.yml: ea 0x1429E1400.
        // That's the IDA preferred address — at runtime it's module_base + 0x29E1400.
        // Unclear whether the slot holds the instance directly or a pointer to it;
        // try both, prefer whichever gives non-zero first 8 bytes.
        var moduleBase = Plugin.SigScanner.Module.BaseAddress;
        nint slot = moduleBase + 0x029E1400;
        nint derefPtr = *(nint*)slot;

        string mode;
        nint instanceAddr;
        if (derefPtr != nint.Zero && derefPtr > 0x10000)
        {
            instanceAddr = derefPtr;
            mode = "deref";
        }
        else
        {
            instanceAddr = slot;   // fall back to inline at slot
            mode = "inline";
        }

        // Sanity: confirm instance actually holds non-zero bytes.
        bool anyNonZero = false;
        for (int i = 0; i < 32 && !anyNonZero; i++)
            if (((byte*)instanceAddr)[i] != 0) anyNonZero = true;

        if (!anyNonZero)
        {
            Plugin.ChatGui.PrintError(
                $"[MjAuto] Emj module instance at 0x{instanceAddr:X} ({mode}) is zeroed. " +
                $"Slot value: 0x{derefPtr:X}. Patch offset may have changed — need sig-scan.");
            return;
        }

        int length = 0x1000;
        if (!string.IsNullOrEmpty(args.Trim()) && TryParseHex(args.Trim(), out int parsed))
            length = Math.Clamp(parsed, 1, 0x8000);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(
            $"# Client::Game::UI::Emj @ 0x{instanceAddr:X}  mode={mode}  (slot 0x{slot:X}, module_base 0x{moduleBase:X})  " +
            $"length=0x{length:X}  utc={DateTime.UtcNow:o}");

        byte* basePtr = (byte*)instanceAddr;
        for (int row = 0; row < length; row += 16)
        {
            sb.Append($"0x{row:X4}: ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < length)
                    sb.Append($"{basePtr[row + i]:X2} ");
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16 && row + i < length; i++)
            {
                byte b = basePtr[row + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "emj-module.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Plugin.ChatGui.Print($"[MjAuto] wrote Emj module (0x{length:X} bytes) to {path}");
    }

    private unsafe void DumpAgent(string args)
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null)
        {
            Plugin.ChatGui.PrintError("[MjAuto] AgentModule unavailable.");
            return;
        }

        // AgentId.Emj = 5
        var agent = agentModule->GetAgentByInternalId((AgentId)5);
        if (agent == null)
        {
            Plugin.ChatGui.PrintError("[MjAuto] AgentEmj not found (id=5).");
            return;
        }

        int length = 0x800;
        if (!string.IsNullOrEmpty(args.Trim()) && TryParseHex(args.Trim(), out int parsed))
            length = Math.Clamp(parsed, 1, 0x4000);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# AgentEmj @ 0x{(nint)agent:X}  length=0x{length:X}  utc={DateTime.UtcNow:o}");

        byte* basePtr = (byte*)agent;
        for (int row = 0; row < length; row += 16)
        {
            sb.Append($"0x{row:X4}: ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < length)
                    sb.Append($"{basePtr[row + i]:X2} ");
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16 && row + i < length; i++)
            {
                byte b = basePtr[row + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "emj-agent.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Plugin.ChatGui.Print($"[MjAuto] wrote AgentEmj (0x{length:X} bytes) to {path}");
    }

    private unsafe void DumpAtkValues()
    {
        if (!MahjongAddon.TryGet(out var unit, out _))
        {
            Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found.");
            return;
        }

        var values = unit->AtkValues;
        int count = unit->AtkValuesCount;
        if (values == null || count == 0)
        {
            Plugin.ChatGui.PrintError("[MjAuto] AtkValues is null or empty.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Emj AtkValues @ 0x{(nint)values:X}  count={count}  utc={DateTime.UtcNow:o}");

        for (int i = 0; i < count; i++)
        {
            var v = values[i];
            string display;
            switch (v.Type)
            {
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int:
                    display = $"Int={v.Int}";
                    break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt:
                    display = $"UInt={v.UInt} (0x{v.UInt:X})";
                    break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool:
                    display = $"Bool={v.Byte != 0}";
                    break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String:
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8:
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString:
                    display = $"String=\"{(v.String.Value != null ? System.Text.Encoding.UTF8.GetString(v.String) : "(null)")}\"";
                    break;
                case FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Vector:
                    display = "Vector";
                    break;
                default:
                    display = $"(type={v.Type}) raw=0x{v.UInt:X}";
                    break;
            }
            sb.AppendLine($"[{i,3}] {v.Type,-16} {display}");
        }

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "emj-atkvalues.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Plugin.ChatGui.Print($"[MjAuto] wrote {count} AtkValues to {path}");
    }

    private unsafe void DumpMemory(string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int offset = 0x238;
        int length = 0x400;
        if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]) && !TryParseHex(parts[0], out offset))
        {
            Plugin.ChatGui.PrintError($"[MjAuto] bad offset '{parts[0]}'. Use hex, optional 0x prefix.");
            return;
        }
        if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) && !TryParseHex(parts[1], out length))
        {
            Plugin.ChatGui.PrintError($"[MjAuto] bad length '{parts[1]}'. Use hex, optional 0x prefix.");
            return;
        }
        length = Math.Clamp(length, 1, 0x2000);

        if (!MahjongAddon.TryGet(out var unit, out _))
        {
            Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found.");
            return;
        }
        nint addr = (nint)unit;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Emj @ 0x{addr:X}  offset=0x{offset:X}  length=0x{length:X}  utc={DateTime.UtcNow:o}");

        byte* basePtr = (byte*)addr;
        for (int row = 0; row < length; row += 16)
        {
            sb.Append($"0x{offset + row:X4}: ");
            for (int i = 0; i < 16; i++)
            {
                if (row + i < length)
                    sb.Append($"{basePtr[offset + row + i]:X2} ");
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16 && row + i < length; i++)
            {
                byte b = basePtr[offset + row + i];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "emj-dump.txt");
        System.IO.File.WriteAllText(path, sb.ToString());

        Plugin.ChatGui.Print($"[MjAuto] wrote 0x{length:X} bytes @ +0x{offset:X} to {path}");
    }

    private static bool TryParseHex(string s, out int value)
    {
        if (s.StartsWith("0x") || s.StartsWith("0X")) s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private unsafe void DumpAddons(string filter)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            Plugin.ChatGui.PrintError("[MjAuto] AtkStage not available (not in game?)");
            return;
        }

        var unitManagers = stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var filterLower = filter?.Trim().ToLowerInvariant() ?? string.Empty;

        int count = 0;
        for (int i = 0; i < unitManagers.Count; i++)
        {
            var unit = unitManagers.Entries[i].Value;
            if (unit == null) continue;

            var name = unit->NameString;
            if (!string.IsNullOrEmpty(filterLower) &&
                !name.ToLowerInvariant().Contains(filterLower))
                continue;

            Plugin.ChatGui.Print(
                $"[MjAuto] {name,-24} @ 0x{(nint)unit:X}  vis={unit->IsVisible}");
            count++;
        }
        Plugin.ChatGui.Print($"[MjAuto] {count} addon(s) {(string.IsNullOrEmpty(filterLower) ? "total" : $"matching \"{filter}\"")}.");
    }

    private void HandlePolicy(string arg)
    {
        var tier = arg.Trim().ToLowerInvariant();
        if (tier != "eff" && tier != "mcts")
        {
            Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto policy <eff|mcts>");
            return;
        }

        plugin.SetPolicy(tier == "eff" ? "efficiency" : "mcts");
        Plugin.ChatGui.Print($"[MjAuto] Policy tier: {plugin.Configuration.PolicyTier}");
    }
}

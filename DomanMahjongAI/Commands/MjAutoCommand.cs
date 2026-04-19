using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;

namespace DomanMahjongAI.Commands;

public sealed class MjAutoCommand : IDisposable
{
    private const string Primary = "/mjauto";
    private const string HelpText = "Open Doman Mahjong Solver. Subcommands: on | off | open | debug | policy <eff|mcts> | pass <N> | dump | addons [filter] | dumpmem [offset] [length] | atkvalues | agent [length] | emj [length] | log <on|off> | testdiscard <slot> | autodiscard";
    // Note: removed /mjauto scan and /mjauto followptr — both dereferenced untrusted pointers and crashed the client.

    private readonly Plugin plugin;

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

            case "log":
                HandleLog(rest);
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
        var ptr = Plugin.GameGui.GetAddonByName("Emj");
        if (ptr.Address == nint.Zero)
        {
            Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found.");
            return;
        }

        var unit = (AtkUnitBase*)ptr.Address;
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

        var ptr = Plugin.GameGui.GetAddonByName("Emj");
        nint addr = ptr.Address;
        if (addr == nint.Zero)
        {
            Plugin.ChatGui.PrintError("[MjAuto] Emj addon not found.");
            return;
        }

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

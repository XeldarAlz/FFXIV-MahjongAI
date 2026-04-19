using Dalamud.Game.Command;
using System;

namespace DomanMahjongAI.Commands;

public sealed class MjAutoCommand : IDisposable
{
    private const string Primary = "/mjauto";
    private const string HelpText = "Doman Mahjong AI. Subcommands: on | off | debug | policy <eff|mcts> | dump";

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
                plugin.ToggleDebugOverlay();
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

            default:
                Plugin.ChatGui.PrintError($"[MjAuto] Unknown subcommand: {sub}. {HelpText}");
                break;
        }
    }

    private void HandlePolicy(string arg)
    {
        var tier = arg.Trim().ToLowerInvariant();
        if (tier != "eff" && tier != "mcts")
        {
            Plugin.ChatGui.PrintError("[MjAuto] Usage: /mjauto policy <eff|mcts>");
            return;
        }

        plugin.Configuration.PolicyTier = tier == "eff" ? "efficiency" : "mcts";
        plugin.Configuration.Save();
        Plugin.ChatGui.Print($"[MjAuto] Policy tier: {plugin.Configuration.PolicyTier}");
    }
}

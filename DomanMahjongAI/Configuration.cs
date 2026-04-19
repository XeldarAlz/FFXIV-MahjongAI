using Dalamud.Configuration;
using System;

namespace DomanMahjongAI;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool AutomationArmed { get; set; } = false;

    public bool SuggestionOnly { get; set; } = true;

    public string PolicyTier { get; set; } = "efficiency";

    public bool TosAccepted { get; set; } = false;

    /// <summary>
    /// When true, the MainWindow exposes a "Developer tools" section that opens
    /// the debug overlay (live state, dispatch tests, memory dumps). End-user
    /// builds leave this false.
    /// </summary>
    public bool DevMode { get; set; } = false;

    /// <summary>Target median delay (ms) between auto-play actions.</summary>
    public int HumanizedDelayMs { get; set; } = 1200;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

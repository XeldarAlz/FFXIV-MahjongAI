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

    /// <summary>
    /// Draw a colored box + arrow on the recommended discard tile directly in the
    /// Doman Mahjong game UI. Intended as the primary cue in "Suggestions" mode so
    /// users don't have to parse shanten/ukeire numbers.
    /// </summary>
    public bool ShowInGameHighlight { get; set; } = true;

    /// <summary>
    /// When true, the main window shows the shanten / ukeire / score table under the
    /// headline pick. Defaults off — most users just want the "discard X" cue.
    /// </summary>
    public bool ShowSuggestionDetails { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

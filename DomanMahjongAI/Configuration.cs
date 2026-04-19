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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

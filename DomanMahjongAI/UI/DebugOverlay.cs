using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace DomanMahjongAI.UI;

public sealed class DebugOverlay : Window, IDisposable
{
    private readonly Plugin plugin;

    public DebugOverlay(Plugin plugin)
        : base("Doman Mahjong AI###domanmahjong-debug")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        ImGui.TextUnformatted("Doman Mahjong AI — debug overlay");
        ImGui.Separator();

        if (!cfg.TosAccepted)
        {
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f),
                "Automation disabled until ToS acknowledgement is accepted.");
            if (ImGui.Button("Acknowledge and enable automation controls"))
            {
                cfg.TosAccepted = true;
                cfg.Save();
            }
            ImGui.Separator();
        }

        var armed = cfg.AutomationArmed;
        if (ImGui.Checkbox("Automation armed", ref armed))
        {
            cfg.AutomationArmed = armed && cfg.TosAccepted;
            cfg.Save();
        }

        var suggestion = cfg.SuggestionOnly;
        if (ImGui.Checkbox("Suggestion-only mode", ref suggestion))
        {
            cfg.SuggestionOnly = suggestion;
            cfg.Save();
        }

        ImGui.TextUnformatted($"Policy tier: {cfg.PolicyTier}");

        ImGui.Separator();
        ImGui.TextDisabled("Engine, opponent model, and policy output will render here.");
    }
}

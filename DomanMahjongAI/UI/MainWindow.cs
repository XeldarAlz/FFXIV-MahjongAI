using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy;
using DomanMahjongAI.Policy.Efficiency;
using System;
using System.Numerics;

namespace DomanMahjongAI.UI;

/// <summary>
/// End-user facing window. Three zones:
///   1. Status header — big auto-play mode + live game detection
///   2. Live panel — hand, score, policy suggestion (visible when in a match)
///   3. Settings — policy tier, humanized speed, ToS, dev-mode toggle (collapsed by default)
/// Debug controls (memory dumps, dispatch tests, event logger) live in the
/// separate DebugOverlay window, which only opens when DevMode is on.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Colors — consistent palette for the whole window.
    private static readonly Vector4 ColorAccent    = new(0.26f, 0.80f, 0.60f, 1f);   // teal-green
    private static readonly Vector4 ColorSuggest   = new(0.95f, 0.80f, 0.30f, 1f);   // amber
    private static readonly Vector4 ColorDisabled  = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ColorMuted     = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 ColorWarn      = new(1.00f, 0.55f, 0.20f, 1f);
    private static readonly Vector4 ColorHeader    = new(0.95f, 0.95f, 1.00f, 1f);

    public MainWindow(Plugin plugin)
        : base("Doman Mahjong Solver###domanmahjong-main")
    {
        this.plugin = plugin;
        Size = new Vector2(460, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 360),
            MaximumSize = new Vector2(900, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        if (!cfg.TosAccepted)
        {
            DrawTosGate(cfg);
            return;
        }

        DrawHeader(cfg);
        ImGui.Spacing();
        DrawModeSelector(cfg);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawLivePanel();

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Settings"))
        {
            DrawSettings(cfg);
        }
    }

    private void DrawTosGate(Configuration cfg)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColorHeader);
        ImGui.TextUnformatted("Welcome to Doman Mahjong Solver");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.TextWrapped(
            "This plugin can play Doman Mahjong for you by reading the game and clicking " +
            "on your behalf. Before you turn that on, please read and accept:");
        ImGui.Spacing();
        ImGui.BulletText("Third-party automation is against the FFXIV Terms of Service.");
        ImGui.BulletText("Use at your own risk — your account may be sanctioned.");
        ImGui.BulletText("\"Hints\" mode only shows advice and never clicks for you.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("I understand — continue", new Vector2(-1, 32)))
        {
            cfg.TosAccepted = true;
            cfg.Save();
        }
    }

    private void DrawHeader(Configuration cfg)
    {
        // Status dot + descriptive text for the current mode.
        var (dot, label) = GetStatusBadge(cfg);
        ImGui.PushStyleColor(ImGuiCol.Text, dot);
        ImGui.TextUnformatted("●");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColorHeader);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X - 80, 0));
        ImGui.SameLine();
        var addonOk = plugin.AddonReader.Poll().Present;
        ImGui.PushStyleColor(ImGuiCol.Text, addonOk ? ColorAccent : ColorDisabled);
        ImGui.TextUnformatted(addonOk ? "in match" : "idle");
        ImGui.PopStyleColor();
    }

    private (Vector4 dot, string label) GetStatusBadge(Configuration cfg)
    {
        if (!cfg.AutomationArmed) return (ColorDisabled, "Off");
        if (cfg.SuggestionOnly)    return (ColorSuggest, "Hints only");
        return (ColorAccent, "Auto-play active");
    }

    private void DrawModeSelector(Configuration cfg)
    {
        // Three-way pill: Off / Hints / Auto.
        var (offColor, suggestColor, autoColor) = (ColorDisabled, ColorSuggest, ColorAccent);
        int current = !cfg.AutomationArmed ? 0 : (cfg.SuggestionOnly ? 1 : 2);

        float buttonW = (ImGui.GetContentRegionAvail().X - 16) / 3f;
        if (PillButton("Off",       current == 0, offColor,     new Vector2(buttonW, 34))) SetMode(cfg, 0);
        ImGui.SameLine();
        if (PillButton("Hints",     current == 1, suggestColor, new Vector2(buttonW, 34))) SetMode(cfg, 1);
        ImGui.SameLine();
        if (PillButton("Auto-play", current == 2, autoColor,    new Vector2(buttonW, 34))) SetMode(cfg, 2);
    }

    private static bool PillButton(string label, bool selected, Vector4 tint, Vector2 size)
    {
        var idle   = new Vector4(tint.X * 0.18f, tint.Y * 0.18f, tint.Z * 0.18f, 1f);
        var hover  = new Vector4(tint.X * 0.30f, tint.Y * 0.30f, tint.Z * 0.30f, 1f);
        var active = new Vector4(tint.X * 0.55f, tint.Y * 0.55f, tint.Z * 0.55f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? active : idle);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? new Vector4(1f, 1f, 1f, 1f) : tint);
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    private static void SetMode(Configuration cfg, int mode)
    {
        cfg.AutomationArmed = mode > 0;
        cfg.SuggestionOnly = mode == 1;
        cfg.Save();
    }

    private void DrawLivePanel()
    {
        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextWrapped("Open a Doman Mahjong table in the Gold Saucer to see hints here.");
            ImGui.PopStyleColor();
            return;
        }

        DrawSeatRow(snap);
        ImGui.Spacing();
        DrawHand(snap);
        ImGui.Spacing();
        DrawSuggestion(snap);
    }

    private void DrawSeatRow(StateSnapshot snap)
    {
        string[] labels = { "You", "Shimo", "Toimen", "Kami" };
        for (int i = 0; i < 4; i++)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, i == 0 ? ColorAccent : ColorMuted);
            ImGui.TextUnformatted($"{labels[i]} {snap.Scores[i]}");
            ImGui.PopStyleColor();
            if (i < 3) ImGui.SameLine(0, 18);
        }
    }

    private void DrawHand(StateSnapshot snap)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ColorHeader);
        ImGui.TextUnformatted("Hand");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled($"({snap.Hand.Count} tiles)");

        var rendered = Tiles.Render(snap.Hand);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.88f, 0.92f, 1f, 1f));
        ImGui.TextWrapped(string.IsNullOrEmpty(rendered) ? "—" : rendered);
        ImGui.PopStyleColor();
    }

    private void DrawSuggestion(StateSnapshot snap)
    {
        var cfg = plugin.Configuration;

        ImGui.PushStyleColor(ImGuiCol.Text, ColorHeader);
        ImGui.TextUnformatted("Best move");
        ImGui.PopStyleColor();

        if (snap.Hand.Count != 14)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextUnformatted($"  waiting for your turn ({snap.Hand.Count}/14 tiles)");
            ImGui.PopStyleColor();
            return;
        }

        DiscardScorer.ScoredDiscard[] scored;
        ActionChoice choice;
        try
        {
            scored = DiscardScorer.Score(snap);
            choice = plugin.Policy.Choose(snap);
        }
        catch (Exception ex)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorWarn);
            ImGui.TextWrapped($"scorer error: {ex.Message}");
            ImGui.PopStyleColor();
            return;
        }

        // Plain-language headline: "→ Discard  South Wind (2z)" instead of "→ Discard 2z".
        string verb = FriendlyActionVerb(choice.Kind);
        string tileLabel = choice.DiscardTile is { } t ? FriendlyTileName(t) : "—";
        ImGui.PushStyleColor(ImGuiCol.Text, ColorAccent);
        ImGui.TextUnformatted($"  → {verb}  {tileLabel}");
        ImGui.PopStyleColor();

        if (cfg.ShowInGameHighlight)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextUnformatted("    The tile is highlighted in the mahjong window.");
            ImGui.PopStyleColor();
        }

        // Details (shanten / ukeire table) behind a toggle — off by default.
        ImGui.Spacing();
        bool details = cfg.ShowSuggestionDetails;
        if (ImGui.Checkbox("Show analysis details", ref details))
        {
            cfg.ShowSuggestionDetails = details;
            cfg.Save();
        }

        if (details)
        {
            ImGui.TextDisabled(
                "shanten = turns away from ready.  ukeire = tiles that complete your wait " +
                "(kinds / weighted by copies left).");
            int show = Math.Min(3, scored.Length);
            for (int i = 0; i < show; i++)
            {
                var s = scored[i];
                var color = i == 0 ? ColorAccent : ColorMuted;
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextUnformatted(
                    $"    {i + 1}.  {s.Discard}   shanten {s.ShantenAfter}   " +
                    $"ukeire {s.UkeireKinds}k/{s.UkeireWeighted}w");
                ImGui.PopStyleColor();
            }
        }

        if (plugin.AutoPlay.LastActionDescription != "(none)")
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, ColorMuted);
            ImGui.TextWrapped($"Last action: {plugin.AutoPlay.LastActionDescription}");
            ImGui.PopStyleColor();
        }
    }

    private static string FriendlyActionVerb(ActionKind kind) => kind switch
    {
        ActionKind.Discard    => "Discard",
        ActionKind.Riichi     => "Riichi on",
        ActionKind.Tsumo      => "Win (tsumo)",
        ActionKind.Ron        => "Win (ron)",
        ActionKind.Pon        => "Pon",
        ActionKind.Chi        => "Chi",
        ActionKind.AnKan      => "Kan",
        ActionKind.MinKan     => "Kan",
        ActionKind.ShouMinKan => "Kan",
        _                     => kind.ToString(),
    };

    private static string FriendlyTileName(Engine.Tile tile)
    {
        string code = tile.ShortName;
        string name = tile.Suit switch
        {
            Engine.TileSuit.Man => $"{tile.Number} Character",
            Engine.TileSuit.Pin => $"{tile.Number} Dot",
            Engine.TileSuit.Sou => $"{tile.Number} Bamboo",
            Engine.TileSuit.Honor => tile.HonorNumber switch
            {
                1 => "East Wind",
                2 => "South Wind",
                3 => "West Wind",
                4 => "North Wind",
                5 => "White Dragon",
                6 => "Green Dragon",
                7 => "Red Dragon",
                _ => code,
            },
            _ => code,
        };
        return $"{name}  ({code})";
    }

    private void DrawSettings(Configuration cfg)
    {
        ImGui.Spacing();

        // How strong the plugin tries to play.
        ImGui.TextUnformatted("Play style");
        int tierIdx = cfg.PolicyTier == "mcts" ? 1 : 0;
        string[] tiers = { "Standard (fast)", "Stronger (slower to think)" };
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("##policy-tier", ref tierIdx, tiers, tiers.Length))
        {
            plugin.SetPolicy(tierIdx == 0 ? "efficiency" : "mcts");
        }

        ImGui.Spacing();

        // Click timing.
        ImGui.TextUnformatted("Click speed");
        int delay = cfg.HumanizedDelayMs;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderInt("##delay", ref delay, 400, 3000, "%d ms"))
        {
            cfg.HumanizedDelayMs = delay;
            cfg.Save();
        }
        ImGui.TextDisabled("Average time the plugin waits before each click.");

        ImGui.Spacing();

        bool highlight = cfg.ShowInGameHighlight;
        if (ImGui.Checkbox("Highlight the suggested tile in the mahjong window", ref highlight))
        {
            cfg.ShowInGameHighlight = highlight;
            cfg.Save();
        }
        ImGui.TextDisabled("A colored box + arrow on the tile to discard. Shown while Suggestions mode is on.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool dev = cfg.DevMode;
        if (ImGui.Checkbox("Developer tools", ref dev))
        {
            cfg.DevMode = dev;
            cfg.Save();
            if (dev) plugin.DebugOverlay.IsOpen = true;
        }
        ImGui.TextDisabled("For debugging. Leave unchecked unless you know what it is.");

        if (cfg.DevMode)
        {
            ImGui.Spacing();
            if (ImGui.Button("Open debug overlay"))
                plugin.DebugOverlay.IsOpen = true;
            ImGui.SameLine();
            if (ImGui.Button("Show terms notice again"))
            {
                cfg.TosAccepted = false;
                cfg.AutomationArmed = false;
                cfg.Save();
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Type /mjauto in chat to open this window.");
    }
}

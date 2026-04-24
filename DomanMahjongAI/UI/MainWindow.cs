using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy;
using DomanMahjongAI.Policy.Efficiency;
using System;
using System.Numerics;

namespace DomanMahjongAI.UI;

/// <summary>
/// End-user facing window, styled as stacked surface cards:
///   1. Status     — filled mode pill on the left, match/idle badge on the right
///   2. Mode       — three rich pills (Off / Hints / Auto-play) with subtitles
///   3. Live game  — seat pills, hand rendered as tiles, headline best-move
///   4. Settings   — collapsible, grouped into Play style / Appearance / Developer
/// Debug controls (memory dumps, dispatch tests, event logger) live in the
/// separate DebugOverlay window, which only opens when DevMode is on.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Doman Mahjong Solver###domanmahjong-main")
    {
        this.plugin = plugin;
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(900, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        using var _s = Theme.PushWindowStyle();

        if (!cfg.TosAccepted)
        {
            DrawTosGate(cfg);
            return;
        }

        DrawStatusCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));
        DrawModeCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));
        DrawLiveCard();
        ImGui.Dummy(new Vector2(0, 6));
        DrawSettings(cfg);
    }

    // ============================================================
    // ToS gate
    // ============================================================
    private void DrawTosGate(Configuration cfg)
    {
        using (Theme.BeginCard("tos"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted("Welcome to Doman Mahjong Solver");
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 6));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
            ImGui.TextWrapped(
                "This plugin can play Doman Mahjong for you by reading the game and clicking " +
                "on your behalf. Before turning that on, please read:");
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4));

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
            ImGui.BulletText("Third-party automation is against the FFXIV Terms of Service.");
            ImGui.BulletText("Use at your own risk — your account may be sanctioned.");
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.BulletText("\"Hints\" mode only shows advice — never clicks for you.");
            ImGui.PopStyleColor();

            ImGui.Dummy(new Vector2(0, 10));

            ImGui.PushStyleColor(ImGuiCol.Button,        Theme.Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Theme.Accent.X * 1.15f, Theme.Accent.Y * 1.15f, Theme.Accent.Z * 1.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(Theme.Accent.X * 0.85f, Theme.Accent.Y * 0.85f, Theme.Accent.Z * 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.05f, 0.10f, 0.08f, 1f));
            float btnW = ImGui.GetContentRegionAvail().X;
            if (ImGui.Button("I understand — continue", new Vector2(btnW, 36)))
            {
                cfg.TosAccepted = true;
                cfg.Save();
            }
            ImGui.PopStyleColor(4);
        }
    }

    // ============================================================
    // Status card — filled mode pill + match/idle badge
    // ============================================================
    private void DrawStatusCard(Configuration cfg)
    {
        using (Theme.BeginCard("status"))
        {
            var (tint, label) = GetStatusBadge(cfg);
            Theme.Pill(label, tint, filled: true);

            bool addonOk = plugin.AddonReader.Poll().Present;
            string badgeText = addonOk ? "in match" : "idle";
            var badgeTint = addonOk ? Theme.Accent : Theme.Muted;
            float badgeW = ImGui.CalcTextSize(badgeText).X + 28;
            Theme.RightAlign(badgeW);
            Theme.Pill(badgeText, badgeTint, filled: false);
        }
    }

    private static (Vector4 tint, string label) GetStatusBadge(Configuration cfg)
    {
        if (!cfg.AutomationArmed) return (Theme.Muted,  "Off");
        if (cfg.SuggestionOnly)   return (Theme.Warn,   "Hints only");
        return (Theme.Accent, "Auto-play active");
    }

    // ============================================================
    // Mode card — rich three-way pill
    // ============================================================
    private void DrawModeCard(Configuration cfg)
    {
        using (Theme.BeginCard("mode"))
        {
            Theme.SectionHeader("Mode");

            int current = !cfg.AutomationArmed ? 0 : (cfg.SuggestionOnly ? 1 : 2);
            float avail = ImGui.GetContentRegionAvail().X;
            float gap = 6f;
            float w = (avail - gap * 2) / 3f;
            var size = new Vector2(w, 50);

            if (ModePill("Off",       "Do nothing",             Theme.Muted,  current == 0, size)) SetMode(cfg, 0);
            ImGui.SameLine(0, gap);
            if (ModePill("Hints",     "Highlight best move",    Theme.Warn,   current == 1, size)) SetMode(cfg, 1);
            ImGui.SameLine(0, gap);
            if (ModePill("Auto-play", "Click for you",          Theme.Accent, current == 2, size)) SetMode(cfg, 2);
        }
    }

    private static bool ModePill(string title, string sub, Vector4 tint, bool selected, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;

        bool clicked = ImGui.InvisibleButton($"##mode-{title}", size);
        bool hovered = ImGui.IsItemHovered();

        Vector4 bg = selected ? Theme.Fade(tint, 0.30f)
                     : hovered ? Theme.Fade(tint, 0.15f)
                               : Theme.Fade(tint, 0.07f);
        Vector4 border = selected ? tint : Theme.Fade(tint, 0.40f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(border), 6f, ImDrawFlags.None, selected ? 2f : 1f);

        var titleSize = ImGui.CalcTextSize(title);
        var subSize   = ImGui.CalcTextSize(sub);
        Vector4 titleColor = selected ? new Vector4(1f, 1f, 1f, 1f) : tint;
        Vector4 subColor   = selected ? new Vector4(1f, 1f, 1f, 0.75f) : Theme.Fade(tint, 0.65f);
        var titlePos = min + new Vector2((size.X - titleSize.X) * 0.5f, 8);
        var subPos   = min + new Vector2((size.X - subSize.X)   * 0.5f, size.Y - subSize.Y - 6);
        dl.AddText(titlePos, Theme.Pack(titleColor), title);
        dl.AddText(subPos,   Theme.Pack(subColor),   sub);

        return clicked;
    }

    private static void SetMode(Configuration cfg, int mode)
    {
        cfg.AutomationArmed = mode > 0;
        cfg.SuggestionOnly = mode == 1;
        cfg.Save();
    }

    // ============================================================
    // Live game card — seats, hand, suggestion
    // ============================================================
    private void DrawLiveCard()
    {
        using (Theme.BeginCard("live"))
        {
            Theme.SectionHeader("Live game");

            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null)
            {
                Theme.Subtle("Open a Doman Mahjong table in the Gold Saucer to see hints here.");
                return;
            }

            DrawSeatRow(snap);
            ImGui.Dummy(new Vector2(0, 10));
            DrawHandRow(snap);
            ImGui.Dummy(new Vector2(0, 10));
            DrawSuggestion(snap);
        }
    }

    private void DrawSeatRow(StateSnapshot snap)
    {
        string[] labels = { "You", "Right", "Across", "Left" };
        float avail = ImGui.GetContentRegionAvail().X;
        float gap = 6f;
        float pillW = (avail - gap * 3) / 4f;
        for (int i = 0; i < 4; i++)
        {
            DrawSeatPill(labels[i], snap.Scores[i], isYou: i == 0, new Vector2(pillW, 40));
            if (i < 3) ImGui.SameLine(0, gap);
        }
    }

    private static void DrawSeatPill(string label, int score, bool isYou, Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        Vector4 tint = isYou ? Theme.Accent : Theme.Muted;
        Vector4 bg   = Theme.Fade(tint, isYou ? 0.18f : 0.08f);

        dl.AddRectFilled(min, max, Theme.Pack(bg), 6f);
        dl.AddRect(min, max, Theme.Pack(tint, isYou ? 0.85f : 0.45f), 6f, ImDrawFlags.None, 1f);

        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = min + new Vector2((size.X - labelSize.X) * 0.5f, 5);
        dl.AddText(labelPos, Theme.Pack(tint, 0.8f), label);

        string scoreStr = score.ToString();
        var scoreSize = ImGui.CalcTextSize(scoreStr);
        var scorePos = min + new Vector2((size.X - scoreSize.X) * 0.5f, size.Y - scoreSize.Y - 5);
        Vector4 scoreColor = isYou ? Theme.Header : Theme.Body;
        dl.AddText(scorePos, Theme.Pack(scoreColor), scoreStr);

        ImGui.Dummy(size);
    }

    private void DrawHandRow(StateSnapshot snap)
    {
        Theme.Caption($"Hand · {snap.Hand.Count} tiles");
        ImGui.Dummy(new Vector2(0, 3));

        int highlightSlot = -1;
        if (snap.Legal.Can(ActionFlags.Discard))
        {
            try
            {
                var choice = plugin.Policy.Choose(snap);
                if (choice.DiscardTile is { } t)
                    highlightSlot = InputDispatcher.FindSlotOfTile(t, snap.Hand);
            }
            catch { /* best-move lookup is cosmetic — ignore failures */ }
        }

        Theme.DrawHand(snap.Hand, highlightSlot);
    }

    private void DrawSuggestion(StateSnapshot snap)
    {
        var cfg = plugin.Configuration;

        Theme.Caption("Best move");
        ImGui.Dummy(new Vector2(0, 3));

        if (!snap.Legal.Can(ActionFlags.Discard))
        {
            Theme.Subtle($"Waiting for your turn ({snap.Hand.Count} tiles in hand).");
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
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
            ImGui.TextWrapped($"scorer error: {ex.Message}");
            ImGui.PopStyleColor();
            return;
        }

        string verb = FriendlyActionVerb(choice.Kind);

        // Headline row: verb (accent) + big tile + friendly name, all vertically centered.
        float startY = ImGui.GetCursorPosY();
        float bigH = Theme.BigTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = startY + (bigH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
        ImGui.TextUnformatted(verb);
        ImGui.PopStyleColor();

        if (choice.DiscardTile is { } t)
        {
            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(startY);
            Theme.DrawTile(t, new Vector2(Theme.BigTileW, Theme.BigTileH), Theme.Pulse(1.4f, 0.55f, 1.0f));

            ImGui.SameLine(0, 12);
            ImGui.SetCursorPosY(textY);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextUnformatted(FriendlyTileName(t));
            ImGui.PopStyleColor();
        }

        // Force cursor past the big tile.
        if (choice.DiscardTile is not null)
            ImGui.SetCursorPosY(startY + bigH + 4);

        if (cfg.ShowInGameHighlight)
        {
            Theme.Subtle("The tile is outlined in the mahjong window.");
        }

        ImGui.Dummy(new Vector2(0, 6));
        bool details = cfg.ShowSuggestionDetails;
        if (ImGui.Checkbox("Show analysis details", ref details))
        {
            cfg.ShowSuggestionDetails = details;
            cfg.Save();
        }

        if (details)
        {
            ImGui.Dummy(new Vector2(0, 2));
            Theme.Subtle(
                "shanten = turns away from ready.  ukeire = tiles that complete your wait — " +
                "counted as distinct kinds and total copies remaining in the live wall.");
            ImGui.Dummy(new Vector2(0, 4));
            int show = Math.Min(3, scored.Length);
            for (int i = 0; i < show; i++) DrawScoredPickRow(i, scored[i]);
        }

        if (plugin.AutoPlay.LastActionDescription != "(none)")
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextWrapped($"Last action: {plugin.AutoPlay.LastActionDescription}");
            ImGui.PopStyleColor();
        }
    }

    private static void DrawScoredPickRow(int rank, DiscardScorer.ScoredDiscard s)
    {
        float rowStart = ImGui.GetCursorPosY();
        float tileH = Theme.SmallTileH;
        float textH = ImGui.CalcTextSize("X").Y;
        float textY = rowStart + (tileH - textH) * 0.5f;

        ImGui.SetCursorPosY(textY);
        Vector4 rankColor = rank == 0 ? Theme.Accent : Theme.Muted;
        ImGui.PushStyleColor(ImGuiCol.Text, rankColor);
        ImGui.TextUnformatted($"{rank + 1}.");
        ImGui.PopStyleColor();

        ImGui.SameLine(0, 8);
        ImGui.SetCursorPosY(rowStart);
        Theme.DrawTile(s.Discard, new Vector2(Theme.SmallTileW, Theme.SmallTileH));

        ImGui.SameLine(0, 10);
        ImGui.SetCursorPosY(textY);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextUnformatted($"shanten {s.ShantenAfter}    ukeire {s.UkeireKinds} kinds · {s.UkeireWeighted} tiles");
        ImGui.PopStyleColor();

        ImGui.SetCursorPosY(rowStart + tileH + 3);
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

    // ============================================================
    // Settings — collapsible, grouped
    // ============================================================
    private void DrawSettings(Configuration cfg)
    {
        if (!ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.None))
            return;

        ImGui.Dummy(new Vector2(0, 4));

        // ---- Play style ----
        using (Theme.BeginCard("settings-play", alt: true))
        {
            Theme.SectionHeader("Play style");

            int tierIdx = cfg.PolicyTier == "mcts" ? 1 : 0;
            string[] tiers = { "Standard (fast)", "Stronger (slower to think)" };
            ImGui.SetNextItemWidth(300);
            if (ImGui.Combo("Strength", ref tierIdx, tiers, tiers.Length))
                plugin.SetPolicy(tierIdx == 0 ? "efficiency" : "mcts");

            ImGui.Dummy(new Vector2(0, 4));

            int delay = cfg.HumanizedDelayMs;
            ImGui.SetNextItemWidth(300);
            if (ImGui.SliderInt("Click speed", ref delay, 400, 3000, "%d ms"))
            {
                cfg.HumanizedDelayMs = delay;
                cfg.Save();
            }
            Theme.Subtle("Average delay before each auto-play click.");
        }

        ImGui.Dummy(new Vector2(0, 4));

        // ---- Appearance ----
        using (Theme.BeginCard("settings-appearance", alt: true))
        {
            Theme.SectionHeader("Appearance");

            bool highlight = cfg.ShowInGameHighlight;
            if (ImGui.Checkbox("Highlight suggested tile in the mahjong window", ref highlight))
            {
                cfg.ShowInGameHighlight = highlight;
                cfg.Save();
            }
            Theme.Subtle("A pulsing outline on the discard to make. Shown in Hints mode.");
        }

        ImGui.Dummy(new Vector2(0, 4));

        // ---- Developer ----
        using (Theme.BeginCard("settings-dev", alt: true))
        {
            Theme.SectionHeader("Developer");

            bool dev = cfg.DevMode;
            if (ImGui.Checkbox("Enable developer tools", ref dev))
            {
                cfg.DevMode = dev;
                cfg.Save();
                if (dev) plugin.DebugOverlay.IsOpen = true;
            }
            Theme.Subtle("Unlocks the debug overlay. Leave off for normal play.");

            if (cfg.DevMode)
            {
                ImGui.Dummy(new Vector2(0, 4));
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
        }

        ImGui.Dummy(new Vector2(0, 4));
        Theme.Subtle("Type /mjauto in chat to open this window.");
    }
}

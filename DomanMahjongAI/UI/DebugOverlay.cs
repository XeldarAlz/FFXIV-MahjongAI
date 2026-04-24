using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Engine;
using DomanMahjongAI.GameState;
using DomanMahjongAI.Policy;
using DomanMahjongAI.Policy.Efficiency;
using System;
using System.Numerics;

namespace DomanMahjongAI.UI;

/// <summary>
/// Developer console, organized as tabs:
///   Status       — flags, auto-play loop readout, live policy pick
///   Addon        — AddonEmj lifecycle + address/visibility
///   Actions      — auto-discard, test-slot dispatch, test-call option dispatch
///   Diagnostics  — event logger + memory dump buttons
/// The last dispatch result is pinned as a toast below the tab content so it
/// stays visible regardless of which tab you're on.
/// </summary>
public sealed class DebugOverlay : Window, IDisposable
{
    private readonly Plugin plugin;
    private int testDiscardSlot = 13;
    private int testCallOption  = 0;
    private string lastDispatchMsg = "";

    public DebugOverlay(Plugin plugin)
        : base("Doman Mahjong · Developer###domanmahjong-debug")
    {
        this.plugin = plugin;
        Size = new Vector2(580, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 480),
            MaximumSize = new Vector2(1000, 2000),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        using var _s = Theme.PushWindowStyle();

        DrawHeaderCard(cfg);
        ImGui.Dummy(new Vector2(0, 4));

        if (ImGui.BeginTabBar("##debug-tabs"))
        {
            if (ImGui.BeginTabItem("Status"))       { ImGui.Dummy(new Vector2(0, 4)); DrawStatusTab();       ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Addon"))        { ImGui.Dummy(new Vector2(0, 4)); DrawAddonTab();        ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Actions"))      { ImGui.Dummy(new Vector2(0, 4)); DrawActionsTab();      ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Diagnostics"))  { ImGui.Dummy(new Vector2(0, 4)); DrawDiagnosticsTab();  ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        if (!string.IsNullOrEmpty(lastDispatchMsg))
        {
            ImGui.Dummy(new Vector2(0, 6));
            DrawToast(lastDispatchMsg);
        }
    }

    // ============================================================
    // Header
    // ============================================================
    private void DrawHeaderCard(Configuration cfg)
    {
        using (Theme.BeginCard("debug-header"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted("Developer console");
            ImGui.PopStyleColor();

            var (tint, label) = GetDebugStatus(cfg);
            float pillW = ImGui.CalcTextSize(label).X + 28;
            Theme.RightAlign(pillW);
            Theme.Pill(label, tint, filled: tint.X == Theme.Accent.X);

            if (!cfg.TosAccepted)
            {
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                ImGui.TextWrapped("Automation is disabled until the ToS notice is acknowledged in the main window.");
                ImGui.PopStyleColor();
                if (ImGui.Button("Acknowledge and enable"))
                {
                    cfg.TosAccepted = true;
                    cfg.Save();
                }
            }
        }
    }

    private static (Vector4 tint, string label) GetDebugStatus(Configuration cfg)
    {
        if (!cfg.TosAccepted)                                   return (Theme.Danger, "TOS");
        if (cfg.AutomationArmed && !cfg.SuggestionOnly)         return (Theme.Accent, "AUTO");
        if (cfg.AutomationArmed && cfg.SuggestionOnly)          return (Theme.Warn,   "HINTS");
        return (Theme.Muted, "OFF");
    }

    // ============================================================
    // Status tab
    // ============================================================
    private void DrawStatusTab()
    {
        var cfg = plugin.Configuration;

        using (Theme.BeginCard("status-flags"))
        {
            Theme.SectionHeader("Flags");

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

            ImGui.Dummy(new Vector2(0, 4));
            KeyValueRow("Policy tier", cfg.PolicyTier);
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("status-loop"))
        {
            Theme.SectionHeader("Auto-play loop");
            KeyValueRow("State",  plugin.AutoPlay.LastObservedState.ToString());
            KeyValueRow("Hand",   plugin.AutoPlay.LastObservedHandCount.ToString());
            KeyValueRow("Last",   plugin.AutoPlay.LastActionDescription);
            ImGui.Dummy(new Vector2(0, 4));
            DrawAtkValuesRow();
        }

        ImGui.Dummy(new Vector2(0, 4));
        DrawPolicyPickCard();
    }

    private unsafe void DrawAtkValuesRow()
    {
        string atkLine = "(addon not found)";
        if (MahjongAddon.TryGet(out var unit, out _))
        {
            if (unit->AtkValues != null && unit->AtkValuesCount > 0)
            {
                var v0 = unit->AtkValues[0];
                string v0s = v0.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ? v0.Int.ToString() : "?";
                string v1s = "-", v2s = "-";
                if (unit->AtkValuesCount > 1)
                {
                    var v1 = unit->AtkValues[1];
                    v1s = v1.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ? v1.Int.ToString() : v1.Type.ToString();
                }
                if (unit->AtkValuesCount > 2)
                {
                    var v2 = unit->AtkValues[2];
                    v2s = v2.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ? v2.Int.ToString() : v2.Type.ToString();
                }
                atkLine = $"{v0s}   {v1s}   {v2s}";
            }
        }
        KeyValueRow("AtkValues [0..2]", atkLine);
    }

    private void DrawPolicyPickCard()
    {
        using (Theme.BeginCard("status-pick"))
        {
            Theme.SectionHeader("Suggestions (discard-only)");

            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null)
            {
                Theme.Subtle("No snapshot — not in a match, or the addon struct is not readable.");
                return;
            }

            Theme.Caption($"Hand · {snap.Hand.Count} tiles");
            ImGui.Dummy(new Vector2(0, 2));
            Theme.DrawHand(snap.Hand);

            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
            ImGui.TextUnformatted(
                $"Scores   you: {snap.Scores[0]}    right: {snap.Scores[1]}    " +
                $"across: {snap.Scores[2]}    left: {snap.Scores[3]}");
            ImGui.PopStyleColor();

            if (!snap.Legal.Can(ActionFlags.Discard))
            {
                ImGui.Dummy(new Vector2(0, 4));
                Theme.Subtle($"Waiting for our turn ({snap.Hand.Count} tiles).");
                return;
            }

            DiscardScorer.ScoredDiscard[] scored;
            try { scored = DiscardScorer.Score(snap); }
            catch (Exception ex)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Danger);
                ImGui.TextWrapped($"scorer error: {ex.Message}");
                ImGui.PopStyleColor();
                return;
            }

            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Header);
            ImGui.TextUnformatted("Top picks");
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4));

            int show = Math.Min(5, scored.Length);
            for (int i = 0; i < show; i++) DrawPickRow(i, scored[i]);

            var choice = plugin.Policy.Choose(snap);
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Info);
            ImGui.TextUnformatted($"Policy pick:  {choice.Kind} {(choice.DiscardTile?.ToString() ?? "")}");
            ImGui.PopStyleColor();
            if (!string.IsNullOrEmpty(choice.Reasoning))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Faint);
                ImGui.TextWrapped($"  {choice.Reasoning}");
                ImGui.PopStyleColor();
            }
        }
    }

    private static void DrawPickRow(int rank, DiscardScorer.ScoredDiscard s)
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
        ImGui.TextUnformatted(
            $"shanten={s.ShantenAfter}   ukeire={s.UkeireKinds}k/{s.UkeireWeighted}w   " +
            $"dora={s.DoraRetained}   yaku={s.YakuhaiRetained}   score={s.Score:F1}");
        ImGui.PopStyleColor();

        ImGui.SetCursorPosY(rowStart + tileH + 3);
    }

    // ============================================================
    // Addon tab
    // ============================================================
    private void DrawAddonTab()
    {
        using (Theme.BeginCard("addon"))
        {
            Theme.SectionHeader("AddonEmj");
            var obs = plugin.AddonReader.Poll();
            if (!obs.Present)
            {
                Theme.Subtle("Addon \"Emj\" not found. Open a Doman Mahjong match in-game.");
                if (obs.LastLifecycleEvent is not null)
                {
                    ImGui.Dummy(new Vector2(0, 4));
                    KeyValueRow("Last event", obs.LastLifecycleEvent);
                }
                return;
            }

            KeyValueRow("Address",     $"0x{obs.Address:X}");
            KeyValueRow("Visible",     obs.IsVisible.ToString());
            KeyValueRow("Last event",  obs.LastLifecycleEvent ?? "(none)");
        }
    }

    // ============================================================
    // Actions tab
    // ============================================================
    private void DrawActionsTab()
    {
        var snap = plugin.AddonReader.TryBuildSnapshot();
        bool addonPresent = snap is not null;
        bool ourTurn = addonPresent && snap!.Legal.Can(ActionFlags.Discard);

        using (Theme.BeginCard("actions-auto"))
        {
            Theme.SectionHeader("Auto-discard");
            using (Disable(!ourTurn))
            {
                float w = ImGui.GetContentRegionAvail().X;
                if (ImGui.Button("Run policy pick", new Vector2(w, 34)))
                    Plugin.Framework.RunOnFrameworkThread(() => TriggerAutoDiscard());
            }
            if (!ourTurn)
            {
                Theme.Subtle(addonPresent ? "Not our turn." : "No snapshot — open a match first.");
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testslot"))
        {
            Theme.SectionHeader("Test discard slot");

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##slot", ref testDiscardSlot);
            testDiscardSlot = Math.Clamp(testDiscardSlot, 0, 13);
            ImGui.SameLine(0, 8);
            using (Disable(!ourTurn))
            {
                if (ImGui.Button($"Dispatch slot {testDiscardSlot}"))
                {
                    int slot = testDiscardSlot;
                    Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        var r = plugin.Dispatcher.DispatchDiscard(slot);
                        lastDispatchMsg = $"discard slot={slot} → {r}";
                    });
                }
            }

            ImGui.Dummy(new Vector2(0, 3));
            if (ImGui.SmallButton("drawn (13)"))   testDiscardSlot = 13;
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("leftmost (0)")) testDiscardSlot = 0;
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("actions-testcall"))
        {
            Theme.SectionHeader("Test call option");

            ImGui.SetNextItemWidth(90);
            ImGui.InputInt("##opt", ref testCallOption);
            testCallOption = Math.Clamp(testCallOption, 0, 5);
            ImGui.SameLine(0, 8);
            if (ImGui.Button($"Dispatch opt {testCallOption}"))
            {
                int opt = testCallOption;
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var r = plugin.Dispatcher.DispatchCallOption(opt);
                    lastDispatchMsg = $"call opt={opt} → {r}";
                });
            }

            ImGui.Dummy(new Vector2(0, 3));
            for (int i = 0; i < 3; i++)
            {
                if (i > 0) ImGui.SameLine(0, 4);
                int v = i;
                if (ImGui.SmallButton(v.ToString())) testCallOption = v;
            }

            ImGui.Dummy(new Vector2(0, 3));
            Theme.Subtle("pon/pass: 0=Pass, 1=Pon.  chi/pass: likely 0=Chi, 1=Pass.  Verify by logging.");
        }
    }

    // ============================================================
    // Diagnostics tab
    // ============================================================
    private void DrawDiagnosticsTab()
    {
        using (Theme.BeginCard("diag-log"))
        {
            Theme.SectionHeader("Event logger");
            bool logEnabled = plugin.EventLogger.Enabled;
            if (ImGui.Checkbox("Record clicks to emj-events.log", ref logEnabled))
            {
                plugin.EventLogger.Enabled = logEnabled;
                if (logEnabled) plugin.EventLogger.OpenLog();
                else            plugin.EventLogger.CloseLog();
            }
        }

        ImGui.Dummy(new Vector2(0, 4));
        using (Theme.BeginCard("diag-dump"))
        {
            Theme.SectionHeader("Memory dumps");
            if (ImGui.Button("Dump memory (0x0, 0x2000)"))
                Plugin.CommandManager.ProcessCommand("/mjauto dumpmem 0x0 0x2000");
            ImGui.SameLine(0, 6);
            if (ImGui.Button("Dump AtkValues"))
                Plugin.CommandManager.ProcessCommand("/mjauto atkvalues");
            ImGui.SameLine(0, 6);
            if (ImGui.Button("Dump AgentEmj"))
                Plugin.CommandManager.ProcessCommand("/mjauto agent 0x1000");
        }
    }

    // ============================================================
    // Shared helpers
    // ============================================================
    private static void KeyValueRow(string key, string value)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Muted);
        ImGui.TextUnformatted(key);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, 140 - ImGui.CalcTextSize(key).X));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Body);
        ImGui.TextUnformatted(value);
        ImGui.PopStyleColor();
    }

    private static void DrawToast(string text)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(w, 30);
        var min = pos;
        var max = min + size;
        dl.AddRectFilled(min, max, Theme.Pack(Theme.Info, 0.15f), 6f);
        dl.AddRect(min, max, Theme.Pack(Theme.Info, 0.55f), 6f, ImDrawFlags.None, 1f);
        var ts = ImGui.CalcTextSize(text);
        var tp = min + new Vector2(12, (size.Y - ts.Y) * 0.5f);
        dl.AddText(tp, Theme.Pack(Theme.Info), text);
        ImGui.Dummy(size);
    }

    private void TriggerAutoDiscard()
    {
        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null || !snap.Legal.Can(ActionFlags.Discard))
        {
            lastDispatchMsg = "auto-discard: not our turn";
            return;
        }

        var choice = plugin.Policy.Choose(snap);
        if (choice.Kind != ActionKind.Discard || choice.DiscardTile is null)
        {
            lastDispatchMsg = $"auto-discard: policy returned {choice.Kind}";
            return;
        }

        var tile = choice.DiscardTile.Value;
        int slot = InputDispatcher.FindSlotOfTile(tile, snap.Hand);
        if (slot < 0)
        {
            lastDispatchMsg = $"auto-discard: tile {tile} not found in hand";
            return;
        }

        var delay = HumanTiming.RandomDelay();
        lastDispatchMsg = $"auto-discarding {tile} slot {slot} in {delay.TotalMilliseconds:F0}ms";

        _ = Plugin.Framework.RunOnTick(() =>
        {
            var r = plugin.Dispatcher.DispatchDiscard(slot);
            lastDispatchMsg = $"auto-discarded {tile} → {r}";
        }, delay);
    }

    private static ImDisable Disable(bool disabled) => new(disabled);

    private readonly struct ImDisable : IDisposable
    {
        private readonly bool active;
        public ImDisable(bool disable) { active = disable; if (disable) ImGui.BeginDisabled(); }
        public void Dispose() { if (active) ImGui.EndDisabled(); }
    }
}

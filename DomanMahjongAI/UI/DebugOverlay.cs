using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Engine;
using DomanMahjongAI.GameState;
using DomanMahjongAI.Policy;
using DomanMahjongAI.Policy.Efficiency;
using System;
using System.Linq;
using System.Numerics;

namespace DomanMahjongAI.UI;

public sealed class DebugOverlay : Window, IDisposable
{
    private readonly Plugin plugin;
    private int testDiscardSlot = 13;   // persists between button clicks
    private int testCallOption = 0;
    private string lastDispatchMsg = "";

    public DebugOverlay(Plugin plugin)
        : base("Doman Mahjong Solver###domanmahjong-debug")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        ImGui.TextUnformatted("Doman Mahjong Solver — debug overlay");
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
        DrawAutoPlayPanel();
        ImGui.Spacing();
        DrawAddonPanel();
        ImGui.Spacing();
        DrawSuggestionPanel();
        ImGui.Spacing();
        DrawActionsPanel();
    }

    private unsafe void DrawAutoPlayPanel()
    {
        var cfg = plugin.Configuration;
        bool active = cfg.TosAccepted && cfg.AutomationArmed && !cfg.SuggestionOnly;

        ImGui.TextUnformatted("Auto-play");
        ImGui.SameLine();
        if (active)
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "ACTIVE");
        else if (cfg.AutomationArmed && cfg.SuggestionOnly)
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), "SUGGESTION ONLY");
        else
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "DISARMED");
        ImGui.Separator();

        // Live AtkValues[0..2] readout.
        string atkLine = "(addon not found)";
        var ptr = Plugin.GameGui.GetAddonByName(AddonEmjReader.AddonName);
        if (ptr.Address != nint.Zero)
        {
            var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)ptr.Address;
            if (unit->AtkValues != null && unit->AtkValuesCount > 0)
            {
                var v0 = unit->AtkValues[0];
                var v0s = v0.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ? v0.Int.ToString() : "?";
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
                atkLine = $"AtkValues[0..2]: {v0s}  {v1s}  {v2s}";
            }
        }

        ImGui.TextUnformatted($"Loop state: {plugin.AutoPlay.LastObservedState}   hand: {plugin.AutoPlay.LastObservedHandCount}");
        ImGui.TextUnformatted(atkLine);
        ImGui.TextUnformatted($"last: {plugin.AutoPlay.LastActionDescription}");
    }

    private void DrawActionsPanel()
    {
        ImGui.TextUnformatted("Actions");
        ImGui.Separator();

        var snap = plugin.AddonReader.TryBuildSnapshot();
        bool addonPresent = snap is not null;
        bool ourTurn = addonPresent && snap!.Legal.Can(ActionFlags.Discard);

        // Auto-discard: snapshot → policy → dispatcher, humanized.
        using (Disable(!ourTurn))
        {
            if (ImGui.Button("Auto-discard (policy pick)"))
            {
                Plugin.Framework.RunOnFrameworkThread(() => TriggerAutoDiscard());
            }
        }
        if (!ourTurn)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(addonPresent ? "(not our turn)" : "(no snapshot)");
        }

        // Test-discard a specific slot.
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##slot", ref testDiscardSlot);
        testDiscardSlot = Math.Clamp(testDiscardSlot, 0, 13);
        ImGui.SameLine();
        using (Disable(!ourTurn))
        {
            if (ImGui.Button($"Test discard slot {testDiscardSlot}"))
            {
                int slot = testDiscardSlot;
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var r = plugin.Dispatcher.DispatchDiscard(slot);
                    lastDispatchMsg = $"discard slot={slot} → {r}";
                });
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("drawn (13)"))
            testDiscardSlot = 13;
        ImGui.SameLine();
        if (ImGui.SmallButton("leftmost (0)"))
            testDiscardSlot = 0;

        // Generic call-option dispatch — the option number is context-dependent.
        // Leftmost button = 0, next = 1, etc. Experiment to find which matches each action.
        ImGui.SetNextItemWidth(80);
        ImGui.InputInt("##opt", ref testCallOption);
        testCallOption = Math.Clamp(testCallOption, 0, 5);
        ImGui.SameLine();
        if (ImGui.Button($"Test call option {testCallOption}"))
        {
            int opt = testCallOption;
            Plugin.Framework.RunOnFrameworkThread(() =>
            {
                var r = plugin.Dispatcher.DispatchCallOption(opt);
                lastDispatchMsg = $"call opt={opt} → {r}";
            });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("0")) testCallOption = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("1")) testCallOption = 1;
        ImGui.SameLine();
        if (ImGui.SmallButton("2")) testCallOption = 2;

        ImGui.Spacing();
        ImGui.TextDisabled("pon/pass: 0=Pass,1=Pon.  chi/pass: likely 0=Chi,1=Pass.  Verify by logging.");
        ImGui.Spacing();
        ImGui.TextUnformatted("Diagnostics");
        ImGui.Separator();

        // Event-logger toggle.
        bool logEnabled = plugin.EventLogger.Enabled;
        if (ImGui.Checkbox("Event logger (record clicks to emj-events.log)", ref logEnabled))
        {
            plugin.EventLogger.Enabled = logEnabled;
            if (logEnabled) plugin.EventLogger.OpenLog();
            else plugin.EventLogger.CloseLog();
        }

        if (ImGui.Button("Dump memory (0x0, 0x2000)"))
            Plugin.CommandManager.ProcessCommand("/mjauto dumpmem 0x0 0x2000");
        ImGui.SameLine();
        if (ImGui.Button("Dump AtkValues"))
            Plugin.CommandManager.ProcessCommand("/mjauto atkvalues");
        ImGui.SameLine();
        if (ImGui.Button("Dump AgentEmj"))
            Plugin.CommandManager.ProcessCommand("/mjauto agent 0x1000");

        if (!string.IsNullOrEmpty(lastDispatchMsg))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.8f, 0.9f, 1f, 1f), $"last: {lastDispatchMsg}");
        }
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

    private void DrawAddonPanel()
    {
        ImGui.TextUnformatted("AddonEmj status");
        ImGui.Separator();

        var obs = plugin.AddonReader.Poll();

        if (!obs.Present)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                "Addon \"Emj\" not found. Open a Doman Mahjong match in-game.");
            if (obs.LastLifecycleEvent is not null)
                ImGui.TextDisabled($"last event: {obs.LastLifecycleEvent}");
            return;
        }

        ImGui.TextUnformatted($"Address:  0x{obs.Address:X}");
        ImGui.TextUnformatted($"Visible:  {obs.IsVisible}");
        ImGui.TextUnformatted($"Event:    {obs.LastLifecycleEvent ?? "(none)"}");
    }

    private void DrawSuggestionPanel()
    {
        ImGui.TextUnformatted("Suggestions (discard-only)");
        ImGui.Separator();

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null)
        {
            ImGui.TextDisabled("no snapshot — not in a match or struct not readable");
            return;
        }

        // Hand display.
        ImGui.TextUnformatted($"Hand ({snap.Hand.Count}): {Tiles.Render(snap.Hand)}");

        // Scores.
        ImGui.TextUnformatted(
            $"Scores  self: {snap.Scores[0]}   shimocha: {snap.Scores[1]}   " +
            $"toimen: {snap.Scores[2]}   kamicha: {snap.Scores[3]}");

        if (!snap.Legal.Can(ActionFlags.Discard))
        {
            ImGui.TextDisabled($"waiting for our turn (hand is {snap.Hand.Count} tiles)");
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Top discard picks:");

        DiscardScorer.ScoredDiscard[] scored;
        try
        {
            scored = DiscardScorer.Score(snap);
        }
        catch (Exception ex)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"scorer error: {ex.Message}");
            return;
        }

        int show = Math.Min(5, scored.Length);
        for (int i = 0; i < show; i++)
        {
            var s = scored[i];
            var color = i == 0 ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(0.85f, 0.85f, 0.85f, 1f);
            ImGui.TextColored(color,
                $"  {i + 1}. {s.Discard}   shanten={s.ShantenAfter}   " +
                $"ukeire={s.UkeireKinds}kinds/{s.UkeireWeighted}w   " +
                $"dora={s.DoraRetained}   yakuhai={s.YakuhaiRetained}   " +
                $"score={s.Score:F1}");
        }

        // Summary: the policy's actual chosen action.
        var choice = plugin.Policy.Choose(snap);
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f),
            $"Policy pick: {choice.Kind} {(choice.DiscardTile?.ToString() ?? "")}");
        if (!string.IsNullOrEmpty(choice.Reasoning))
            ImGui.TextDisabled($"  {choice.Reasoning}");
    }
}

using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DomanMahjongAI.Actions;
using DomanMahjongAI.Commands;
using DomanMahjongAI.GameState;
using DomanMahjongAI.Policy;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Mcts;
using DomanMahjongAI.UI;

namespace DomanMahjongAI;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("DomanMahjongAI");
    public Configuration Configuration { get; }
    public MainWindow MainWindow { get; }
    public DebugOverlay DebugOverlay { get; }
    public AddonEmjReader AddonReader { get; }
    public StateAggregator Aggregator { get; }
    public IPolicy Policy { get; private set; }
    public IPolicy EfficiencyPolicyInstance { get; }
    public IPolicy IsmctsPolicyInstance { get; }
    public InputEventLogger EventLogger { get; }
    public InputDispatcher Dispatcher { get; } = new();
    public AutoPlayLoop AutoPlay { get; }

    private readonly MjAutoCommand command;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        AddonReader = new AddonEmjReader(this);
        Aggregator = new StateAggregator(AddonReader);
        EfficiencyPolicyInstance = new EfficiencyPolicy();
        IsmctsPolicyInstance = new IsmctsPolicy();
        Policy = Configuration.PolicyTier == "mcts" ? IsmctsPolicyInstance : EfficiencyPolicyInstance;
        EventLogger = new InputEventLogger(AddonReader);
        AutoPlay = new AutoPlayLoop(this);

        MainWindow = new MainWindow(this);
        DebugOverlay = new DebugOverlay(this);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(DebugOverlay);

        command = new MjAutoCommand(this);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;

        Log.Information("Doman Mahjong AI loaded.");
    }

    public void Dispose()
    {
        command.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        DebugOverlay.Dispose();
        AutoPlay.Dispose();
        EventLogger.Dispose();
        Aggregator.Dispose();
        AddonReader.Dispose();
    }

    public void ToggleMainWindow() => MainWindow.Toggle();

    public void ToggleDebugOverlay() => DebugOverlay.Toggle();

    public void SetPolicy(string tier)
    {
        var t = tier.ToLowerInvariant();
        Policy = t == "mcts" ? IsmctsPolicyInstance : EfficiencyPolicyInstance;
        Configuration.PolicyTier = t == "mcts" ? "mcts" : "efficiency";
        Configuration.Save();
    }
}

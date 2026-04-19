using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DomanMahjongAI.Commands;
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
    public DebugOverlay DebugOverlay { get; }

    private readonly MjAutoCommand command;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        DebugOverlay = new DebugOverlay(this);
        WindowSystem.AddWindow(DebugOverlay);

        command = new MjAutoCommand(this);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleDebugOverlay;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleDebugOverlay;

        Log.Information("Doman Mahjong AI loaded.");
    }

    public void Dispose()
    {
        command.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleDebugOverlay;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleDebugOverlay;
        WindowSystem.RemoveAllWindows();
        DebugOverlay.Dispose();
    }

    public void ToggleDebugOverlay() => DebugOverlay.Toggle();
}

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DomanMahjongAI.Engine;
using DomanMahjongAI.GameState.Variants;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Finds the Mahjong addon in the running client, subscribes to its lifecycle
/// events, and exposes:
///   - a raw <see cref="AddonEmjObservation"/> (for diagnostics and the debug overlay)
///   - a <see cref="StateSnapshot"/> builder that delegates to the selected
///     <see cref="IEmjVariant"/> for layout-specific reads.
///
/// This component owns the addon-lifecycle wiring and the observation record
/// (both framework-level and variant-agnostic). Every offset / node ID /
/// AtkValue slot lives in a variant — see
/// <see cref="DomanMahjongAI.GameState.Variants.EmjVariant"/> and the
/// <see cref="DomanMahjongAI.GameState.Variants.VariantSelector"/> registry.
///
/// Must be created on (and disposed from) the framework thread.
/// </summary>
public sealed class AddonEmjReader : IDisposable
{
    private readonly Plugin plugin;
    private readonly VariantSelector selector;
    private bool disposed;

    /// <summary>
    /// The variant selector in use. Exposed for diagnostic surfaces
    /// (<c>/mjauto variant dump</c>) that need to enumerate registered
    /// variants and re-probe them on demand.
    /// </summary>
    internal VariantSelector Selector => selector;

    public AddonEmjObservation LastObservation { get; private set; } = AddonEmjObservation.Empty;

    /// <summary>Fired whenever any lifecycle event updates the observation.</summary>
    public event Action<AddonEmjObservation>? ObservationChanged;

    public AddonEmjReader(Plugin plugin)
    {
        this.plugin = plugin;

        // Sequence 4 appends EmjLVariant to this list; for now only Emj is
        // registered, so VariantSelector's probe gate is effectively a
        // passthrough on EU / existing clients. On a client whose layout
        // doesn't match Emj's fingerprint (e.g. Iris's EmjL), the selector
        // logs a one-time warning and TryBuildSnapshot returns null — a
        // loud failure instead of today's silent mis-parse.
        this.selector = new VariantSelector(new IEmjVariant[]
        {
            new EmjVariant(),
        });

        // Register against every known Mahjong addon name (issue #13): some clients
        // expose "Emj", others "EmjL". Whichever one exists locally will fire — the
        // other is a silent no-op. MahjongAddon.TryGet resolves the live pointer.
        var names = MahjongAddon.CandidateNames;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, names, OnPostSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, names, OnPreFinalize);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, names, OnPostRefresh);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, names, OnPostReceiveEvent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Plugin.AddonLifecycle.UnregisterListener(OnPostSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnPreFinalize);
        Plugin.AddonLifecycle.UnregisterListener(OnPostRefresh);
        Plugin.AddonLifecycle.UnregisterListener(OnPostReceiveEvent);
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args) => Observe("PostSetup", args);
    private void OnPostRefresh(AddonEvent type, AddonArgs args) => Observe("PostRefresh", args);
    private void OnPostReceiveEvent(AddonEvent type, AddonArgs args) => Observe("PostReceiveEvent", args);

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        LastObservation = AddonEmjObservation.Empty with { LastLifecycleEvent = "PreFinalize" };
        ObservationChanged?.Invoke(LastObservation);
    }

    private unsafe void Observe(string eventName, AddonArgs args)
    {
        var addr = args.Addon.Address;
        var obs = AddonEmjObservation.Empty;

        if (addr != 0)
        {
            var unit = (AtkUnitBase*)addr;
            obs = new AddonEmjObservation(
                Present: true,
                IsVisible: unit->IsVisible,
                Address: addr,
                Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
                Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
                LastSeenUtcTicks: DateTime.UtcNow.Ticks,
                LastLifecycleEvent: eventName);
        }

        LastObservation = obs;
        ObservationChanged?.Invoke(obs);
    }

    /// <summary>
    /// Poll the current addon state via GameGui (fallback path when lifecycle events
    /// are not firing, or when the plugin starts with the addon already visible).
    /// Safe to call from the framework thread every tick.
    /// </summary>
    public unsafe AddonEmjObservation Poll()
    {
        if (!MahjongAddon.TryGet(out var unit, out _))
        {
            var missing = AddonEmjObservation.Empty with
            {
                LastSeenUtcTicks = DateTime.UtcNow.Ticks,
                LastLifecycleEvent = LastObservation.LastLifecycleEvent,
            };
            LastObservation = missing;
            return missing;
        }

        nint addr = (nint)unit;
        var obs = new AddonEmjObservation(
            Present: true,
            IsVisible: unit->IsVisible,
            Address: addr,
            Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
            Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
            LastSeenUtcTicks: DateTime.UtcNow.Ticks,
            LastLifecycleEvent: LastObservation.LastLifecycleEvent ?? "(poll)");
        LastObservation = obs;
        return obs;
    }

    /// <summary>
    /// Build a <see cref="StateSnapshot"/> from the current addon state by
    /// delegating to the selected <see cref="IEmjVariant"/>. Returns null when
    /// the addon is absent, not visible, or no registered variant's probe
    /// matches the live layout.
    /// </summary>
    public unsafe StateSnapshot? TryBuildSnapshot()
    {
        if (!MahjongAddon.TryGet(out var unit, out _)) return null;
        if (!unit->IsVisible) return null;

        var variant = selector.Resolve(unit);
        if (variant is null) return null;

        return variant.TryBuildSnapshot(
            unit,
            new VariantReadContext(plugin.MeldTracker, plugin.EventLogger));
    }
}

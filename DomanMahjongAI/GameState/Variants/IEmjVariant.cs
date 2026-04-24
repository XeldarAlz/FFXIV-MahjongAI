using FFXIVClientStructs.FFXIV.Component.GUI;
using DomanMahjongAI.Engine;

namespace DomanMahjongAI.GameState.Variants;

/// <summary>
/// Strategy for a single Mahjong-addon layout. Two addons observed on different
/// clients (<c>Emj</c> vs <c>EmjL</c>, issue #13) share the name-registration
/// path via <see cref="MahjongAddon"/>, but differ in node IDs, AtkValue slot
/// assignments, and raw struct offsets, so every layout-dependent read lives
/// behind this interface. <see cref="VariantSelector"/> picks one per session.
/// </summary>
internal interface IEmjVariant
{
    /// <summary>Diagnostic label — doesn't have to equal the addon's game name.</summary>
    string Name { get; }

    /// <summary>
    /// The addon name this variant was designed against (<c>"Emj"</c>,
    /// <c>"EmjL"</c>, ...). Used by <see cref="VariantSelector"/> as a
    /// tiebreaker when more than one variant's <see cref="Probe"/> returns
    /// true — which happens legitimately on an empty hand where the tile-
    /// encoding fingerprint is inconclusive.
    /// </summary>
    string PreferredAddonName { get; }

    /// <summary>
    /// Cheap structural fingerprint. Called on a live addon pointer. Must not
    /// allocate, log, or mutate plugin state. Returning true means "this
    /// variant's offsets are consistent with what I see here" — not a guarantee
    /// the parse will succeed, just a guard against reading the wrong layout.
    /// </summary>
    unsafe bool Probe(AtkUnitBase* unit);

    /// <summary>
    /// Build a <see cref="StateSnapshot"/> from the addon's current contents.
    /// All offset / AtkValue / node-ID knowledge lives here. Return null on any
    /// plausibility-check failure — the reader logs and skips the tick.
    /// </summary>
    unsafe StateSnapshot? TryBuildSnapshot(AtkUnitBase* unit, VariantReadContext ctx);
}

/// <summary>
/// Side-channel inputs a variant needs but shouldn't own: round-scoped mutable
/// state (<see cref="MeldTracker"/>) and the diagnostic log gate
/// (<see cref="InputEventLogger"/>). Passing via a context record keeps the
/// <see cref="IEmjVariant"/> surface narrow and the variant itself stateless
/// with respect to cross-round data.
/// </summary>
internal readonly record struct VariantReadContext(
    MeldTracker MeldTracker,
    InputEventLogger EventLogger);

using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DomanMahjongAI.GameState.Variants;

/// <summary>
/// Picks one <see cref="IEmjVariant"/> per live addon pointer and caches it for
/// the session. The selector's probe-first discipline gives us a loud "no
/// variant matched" signal when a regional/client layout shows up that we
/// haven't implemented yet (issue #13 scenario) — instead of silently reading
/// garbage through the wrong offsets.
///
/// <para>Sequence 1 ships with a single registered variant (<see cref="EmjVariant"/>).
/// The probe gate has no effect until a second variant is added in Sequence 4;
/// it's built in now so the read-side call chain is already variant-aware by
/// the time EmjL support lands.</para>
/// </summary>
internal sealed class VariantSelector
{
    // Settle window: how long a sustained probe-miss streak must last before
    // the unmatched-variant warning fires. Addons briefly appear in a
    // post-setup state where nodes and memory aren't fully populated — the
    // probe correctly rejects that transient layout, but warning immediately
    // produces a one-shot false positive on every plugin load. 2 seconds is
    // well past any legitimate setup race and still prompt enough that a
    // genuine mismatch (issue #13's EmjL) gets flagged in the same session.
    private static readonly TimeSpan UnmatchedWarnDelay = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IEmjVariant> variants;
    private IEmjVariant? cached;
    private bool loggedUnmatched;
    private DateTime? firstMissAt;

    public VariantSelector(IReadOnlyList<IEmjVariant> variants)
    {
        this.variants = variants;
    }

    /// <summary>
    /// All registered variants, in probe order. Exposed for diagnostic surfaces
    /// (e.g. <c>/mjauto variant dump</c>) so they can report probe results for
    /// every candidate without the selector's caching layer in the way.
    /// </summary>
    public IReadOnlyList<IEmjVariant> Variants => variants;

    /// <summary>
    /// Resolve the variant for the live addon pointer. Caches the chosen
    /// variant for the session; on a <see cref="IEmjVariant.Probe"/> miss
    /// (e.g. a patch nudges an offset enough to fail the fingerprint), the
    /// next tick re-scans all registered variants.
    ///
    /// <para>When more than one variant's probe returns true — which happens
    /// on an empty hand where the tile-encoding fingerprint can't distinguish
    /// bases — the variant whose <see cref="IEmjVariant.PreferredAddonName"/>
    /// matches <paramref name="resolvedAddonName"/> wins. If none match, the
    /// first registered winner is picked (stable, arbitrary).</para>
    /// </summary>
    public unsafe IEmjVariant? Resolve(AtkUnitBase* unit, string resolvedAddonName)
    {
        // Keep the cached variant only if both the probe and the name
        // tiebreaker still agree — a cached "Emj" that matches because the
        // hand is empty shouldn't linger after the addon rebinds to "EmjL".
        if (cached is not null
            && cached.Probe(unit)
            && cached.PreferredAddonName == resolvedAddonName)
        {
            firstMissAt = null;
            return cached;
        }

        IEmjVariant? winner = null;
        foreach (var v in variants)
        {
            if (!v.Probe(unit)) continue;
            // Prefer a name match; otherwise keep the first passing variant
            // as the fallback.
            if (v.PreferredAddonName == resolvedAddonName)
            {
                winner = v;
                break;
            }
            winner ??= v;
        }

        if (winner is not null)
        {
            if (cached != winner)
            {
                Plugin.Log.Info(
                    $"[MjAuto] Emj variant resolved as \"{winner.Name}\" " +
                    $"(addon=\"{resolvedAddonName}\")");
                cached = winner;
                loggedUnmatched = false;
            }
            firstMissAt = null;
            return cached;
        }

        // No probe matched — either a transient post-setup frame or an
        // un-implemented client variant. Start the settle clock on first
        // miss, only warn once sustained misses cross the threshold, and
        // reset both clock and logged-flag on any recovery above.
        firstMissAt ??= DateTime.UtcNow;
        if (!loggedUnmatched && DateTime.UtcNow - firstMissAt.Value >= UnmatchedWarnDelay)
        {
            Plugin.Log.Warning(
                "[MjAuto] No Emj variant matched this addon layout. " +
                $"Registered variants: {string.Join(", ", variants.Select(v => v.Name))}. " +
                "Run `/mjauto variant dump` and attach the output to issue #13.");
            loggedUnmatched = true;
        }
        cached = null;
        return null;
    }
}

namespace DomanMahjongAI.GameState.Variants;

/// <summary>
/// Addon variant observed on NA / English / non-Steam clients (issue #13,
/// confirmed against a live Golden Saucer dump from @Packetlosslady on
/// 2026-04-24). Addon is exposed as <c>EmjL</c>; hand tiles and chi-claim
/// textures use base <c>76003</c> (vs <c>76041</c> on Emj) — the only known
/// layout divergence.
///
/// <para>Scores (+0x0500 / 0x07E0 / 0x0AC0 / 0x0DA0), discard-count bytes,
/// hand offset (+0x0DB8), state-code AtkValue slot, and call-modal node IDs
/// all match Emj per that dump. If a future capture shows divergence at any
/// of those, lift it to another abstract in <see cref="BaseEmjVariant"/>
/// rather than overriding per-site here.</para>
/// </summary>
internal sealed class EmjLVariant : BaseEmjVariant
{
    public override string Name => "EmjL";
    public override string PreferredAddonName => "EmjL";
    protected override int TileTextureBase => 76003;
}

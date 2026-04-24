namespace DomanMahjongAI.GameState.Variants;

/// <summary>
/// Addon variant observed on most clients (EU / most non-Steam, and historically
/// every reported region except NA EmjL). Addon is exposed as <c>Emj</c>; hand
/// tiles and chi-claim textures use base <c>76041</c>.
///
/// <para>All parsing logic lives in <see cref="BaseEmjVariant"/>. This class
/// only supplies the three variant-specific constants — if the divergence with
/// EmjL ever grows beyond the tile texture base, add another abstract in the
/// base class rather than re-implementing here.</para>
/// </summary>
internal sealed class EmjVariant : BaseEmjVariant
{
    public override string Name => "Emj";
    public override string PreferredAddonName => "Emj";
    protected override int TileTextureBase => 76041;
}

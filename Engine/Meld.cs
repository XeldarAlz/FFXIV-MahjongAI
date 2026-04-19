namespace DomanMahjongAI.Engine;

public enum MeldKind : byte
{
    Chi,          // open run (three consecutive in suit), from left neighbor
    Pon,          // open triplet
    AnKan,        // concealed kan — no meld-reveal penalty, adds kan-dora
    MinKan,       // open kan (daiminkan) — from opponent discard
    ShouMinKan,   // added kan (from pon → kan)
}

/// <summary>
/// An open or concealed meld. Tiles are canonical: for Chi the run is sorted,
/// for Pon/Kan all tiles share a kind. ClaimedTile is the tile we took from
/// an opponent (null for AnKan and for the original closed tile of ShouMinKan).
/// </summary>
public readonly record struct Meld(
    MeldKind Kind,
    Tile[] Tiles,
    Tile? ClaimedTile,
    int ClaimedFromSeat)
{
    public bool IsKan => Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan;
    public bool IsOpen => Kind is not MeldKind.AnKan;
    public int TileCount => IsKan ? 4 : 3;

    public static Meld Chi(Tile low, Tile claimed, int fromSeat)
    {
        if (low.Suit == TileSuit.Honor)
            throw new ArgumentException("Chi requires a suited tile");
        var t0 = low;
        var t1 = new Tile((byte)(low.Id + 1));
        var t2 = new Tile((byte)(low.Id + 2));
        return new Meld(MeldKind.Chi, [t0, t1, t2], claimed, fromSeat);
    }

    public static Meld Pon(Tile kind, Tile claimed, int fromSeat)
        => new(MeldKind.Pon, [kind, kind, kind], claimed, fromSeat);

    public static Meld AnKan(Tile kind)
        => new(MeldKind.AnKan, [kind, kind, kind, kind], null, -1);

    public static Meld MinKan(Tile kind, Tile claimed, int fromSeat)
        => new(MeldKind.MinKan, [kind, kind, kind, kind], claimed, fromSeat);

    public static Meld ShouMinKan(Tile kind, Tile addedClaimed, int originalFromSeat)
        => new(MeldKind.ShouMinKan, [kind, kind, kind, kind], addedClaimed, originalFromSeat);

    /// <summary>
    /// Construct an open meld from a <see cref="MeldCandidate"/> that was just accepted.
    /// For chi, the low tile of the run is computed from claimed + hand tiles (sorted).
    /// Not valid for AnKan — those come from self-declaration, not call prompts.
    /// </summary>
    public static Meld FromAcceptedCandidate(MeldCandidate c) => c.Kind switch
    {
        MeldKind.Pon        => Pon(c.ClaimedTile, c.ClaimedTile, c.FromSeat),
        MeldKind.MinKan     => MinKan(c.ClaimedTile, c.ClaimedTile, c.FromSeat),
        MeldKind.ShouMinKan => ShouMinKan(c.ClaimedTile, c.ClaimedTile, c.FromSeat),
        MeldKind.Chi        => ChiFromCandidate(c),
        _ => throw new ArgumentException($"FromAcceptedCandidate: unsupported meld kind {c.Kind}"),
    };

    private static Meld ChiFromCandidate(MeldCandidate c)
    {
        // Hand-tiles + claimed form a run. Find the lowest tile ID to anchor Meld.Chi.
        byte lowId = c.ClaimedTile.Id;
        foreach (var t in c.HandTiles)
            if (t.Id < lowId) lowId = t.Id;
        return Chi(new Tile(lowId), c.ClaimedTile, c.FromSeat);
    }
}

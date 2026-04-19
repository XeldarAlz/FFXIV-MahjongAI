namespace DomanMahjongAI.Engine;

/// <summary>
/// A player's hand: closed-tile counts (34-space) plus open melds.
/// Immutable — mutation returns a new instance.
/// </summary>
public sealed class Hand
{
    public IReadOnlyList<int> ClosedCounts { get; }
    public IReadOnlyList<Meld> OpenMelds { get; }

    public int ClosedTileCount { get; }

    public Hand(int[] closedCounts, IReadOnlyList<Meld>? openMelds = null)
    {
        if (closedCounts.Length != Tile.Count34)
            throw new ArgumentException($"closedCounts must be length {Tile.Count34}");

        int total = 0;
        for (int i = 0; i < Tile.Count34; i++)
        {
            if (closedCounts[i] < 0 || closedCounts[i] > Tile.CopiesPerKind)
                throw new ArgumentException($"invalid count {closedCounts[i]} at tile {i}");
            total += closedCounts[i];
        }

        ClosedCounts = closedCounts;
        ClosedTileCount = total;
        OpenMelds = openMelds ?? [];
    }

    public static Hand FromTiles(IEnumerable<Tile> closed, IReadOnlyList<Meld>? melds = null)
        => new(Tiles.ToCounts(closed), melds);

    public static Hand FromNotation(string notation, IReadOnlyList<Meld>? melds = null)
        => FromTiles(Tiles.Parse(notation), melds);

    /// <summary>Total tiles including open melds (closed + 3 per meld; kans still count as 3 for shanten).</summary>
    public int TotalShantenTileCount => ClosedTileCount + OpenMelds.Count * 3;

    /// <summary>Return a mutable copy of the closed counts for in-place DP.</summary>
    public int[] CloneCounts()
    {
        var copy = new int[Tile.Count34];
        for (int i = 0; i < Tile.Count34; i++) copy[i] = ClosedCounts[i];
        return copy;
    }

    public Hand WithTileAdded(Tile t)
    {
        var copy = CloneCounts();
        copy[t.Id]++;
        return new Hand(copy, OpenMelds);
    }

    public Hand WithTileRemoved(Tile t)
    {
        var copy = CloneCounts();
        if (copy[t.Id] == 0)
            throw new InvalidOperationException($"hand does not contain {t}");
        copy[t.Id]--;
        return new Hand(copy, OpenMelds);
    }

    public override string ToString()
    {
        var counts = new int[Tile.Count34];
        for (int i = 0; i < Tile.Count34; i++) counts[i] = ClosedCounts[i];
        var closed = Tiles.RenderCounts(counts);
        if (OpenMelds.Count == 0) return closed;
        var melds = string.Join(" ", OpenMelds.Select(m => Tiles.Render(m.Tiles)));
        return $"{closed} | {melds}";
    }
}

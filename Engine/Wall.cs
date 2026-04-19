namespace DomanMahjongAI.Engine;

/// <summary>
/// Live-tile tracker. Counts every publicly-visible copy of each tile kind —
/// our hand, all discards, all open melds, and all dora/kan-dora indicators.
/// Live count for kind k is `4 - Seen[k]`.
/// </summary>
public sealed class Wall
{
    private readonly int[] seen = new int[Tile.Count34];

    public IReadOnlyList<int> Seen => seen;

    public int SeenOf(int id34) => seen[id34];
    public int SeenOf(Tile t) => seen[t.Id];

    public int LiveOf(int id34) => Tile.CopiesPerKind - seen[id34];
    public int LiveOf(Tile t) => Tile.CopiesPerKind - seen[t.Id];

    public void Observe(Tile t, int delta = 1)
    {
        int v = seen[t.Id] + delta;
        if (v < 0 || v > Tile.CopiesPerKind)
            throw new InvalidOperationException(
                $"seen count for {t} would become {v}, out of [0,{Tile.CopiesPerKind}]");
        seen[t.Id] = v;
    }

    public void ObserveCounts(ReadOnlySpan<int> counts)
    {
        if (counts.Length != Tile.Count34)
            throw new ArgumentException($"counts must be length {Tile.Count34}");
        for (int i = 0; i < Tile.Count34; i++)
        {
            int v = seen[i] + counts[i];
            if (v < 0 || v > Tile.CopiesPerKind)
                throw new InvalidOperationException(
                    $"seen count for tile {i} would become {v}");
            seen[i] = v;
        }
    }

    public void Clear()
    {
        Array.Clear(seen);
    }

    public int[] LiveSnapshot()
    {
        var live = new int[Tile.Count34];
        for (int i = 0; i < Tile.Count34; i++) live[i] = Tile.CopiesPerKind - seen[i];
        return live;
    }
}

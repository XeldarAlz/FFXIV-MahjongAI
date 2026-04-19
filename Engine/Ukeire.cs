namespace DomanMahjongAI.Engine;

public readonly record struct UkeireEntry(
    Tile Discard,
    int ShantenAfter,
    Tile[] AcceptedKinds,
    int WeightedCount);

/// <summary>
/// For a 14-tile hand (plus optional melds), enumerate each possible discard
/// and, for each, which tile kinds are accepted (= non-increasing shanten after
/// drawing that kind). Weighted by live-tile counts from a <see cref="Wall"/>.
/// </summary>
public static class UkeireEnumerator
{
    /// <summary>
    /// Enumerate ukeire for each legal discard from a 14-tile hand.
    /// The returned array lists one entry per distinct closed-tile kind in hand.
    /// </summary>
    public static UkeireEntry[] Enumerate(Hand hand, Wall? wall = null)
    {
        if (hand.ClosedTileCount + hand.OpenMelds.Count * 3 != 14)
            throw new ArgumentException(
                $"Ukeire requires a 14-tile hand (got {hand.ClosedTileCount} closed + {hand.OpenMelds.Count} melds)");

        var counts = hand.CloneCounts();
        int meldCount = hand.OpenMelds.Count;

        int shantenBefore = ShantenCalculator.Standard(counts, meldCount);
        int ci = meldCount == 0 ? ShantenCalculator.Chiitoitsu(counts) : 8;
        int ko = meldCount == 0 ? ShantenCalculator.Kokushi(counts) : 8;
        shantenBefore = Math.Min(shantenBefore, Math.Min(ci, ko));

        var result = new List<UkeireEntry>(14);

        for (int d = 0; d < Tile.Count34; d++)
        {
            if (counts[d] == 0) continue;
            counts[d]--;

            var accepted = new List<Tile>();
            int weighted = 0;

            int shantenAfterDiscard;
            {
                int std = ShantenCalculator.Standard(counts, meldCount);
                int c2 = meldCount == 0 ? ShantenCalculator.Chiitoitsu(counts) : 8;
                int k2 = meldCount == 0 ? ShantenCalculator.Kokushi(counts) : 8;
                shantenAfterDiscard = Math.Min(std, Math.Min(c2, k2));
            }

            for (int k = 0; k < Tile.Count34; k++)
            {
                if (counts[k] >= Tile.CopiesPerKind) continue;

                counts[k]++;
                int std = ShantenCalculator.Standard(counts, meldCount);
                int c2 = meldCount == 0 ? ShantenCalculator.Chiitoitsu(counts) : 8;
                int k2 = meldCount == 0 ? ShantenCalculator.Kokushi(counts) : 8;
                int shantenAfterDraw = Math.Min(std, Math.Min(c2, k2));
                counts[k]--;

                if (shantenAfterDraw < shantenAfterDiscard)
                {
                    var tile = new Tile((byte)k);
                    accepted.Add(tile);
                    int live = wall is null ? Tile.CopiesPerKind : wall.LiveOf(k);
                    weighted += live;
                }
            }

            counts[d]++;
            result.Add(new UkeireEntry(
                new Tile((byte)d),
                shantenAfterDiscard,
                accepted.ToArray(),
                weighted));
        }

        return result.ToArray();
    }
}

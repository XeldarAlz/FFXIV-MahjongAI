namespace DomanMahjongAI.Engine;

public readonly record struct ShantenResult(
    int Standard,
    int Chiitoitsu,
    int Kokushi)
{
    public int Min => Math.Min(Standard, Math.Min(Chiitoitsu, Kokushi));
    public bool IsTenpai => Min == 0;
    public bool IsAgari => Min <= -1;
}

/// <summary>
/// Shanten calculator. Pure functions over 34-space counts.
/// Naive-correct implementation for the standard form — to be replaced by
/// table-lookup (target &lt; 20 µs) once the reference suite is wired up.
/// </summary>
public static class ShantenCalculator
{
    private const int Agari = -1;

    public static ShantenResult Compute(Hand hand)
    {
        int meldCount = hand.OpenMelds.Count;
        var counts = hand.CloneCounts();

        int std = Standard(counts, meldCount);
        int ci = meldCount == 0 ? Chiitoitsu(counts) : 8;
        int ko = meldCount == 0 ? Kokushi(counts) : 8;

        return new ShantenResult(std, ci, ko);
    }

    /// <summary>
    /// Chiitoitsu shanten: 6 − pairs + max(0, 7 − distinct).
    /// Requires a closed hand. Returns 8 if hand has open melds.
    /// </summary>
    public static int Chiitoitsu(ReadOnlySpan<int> counts)
    {
        int pairs = 0, distinct = 0;
        for (int i = 0; i < Tile.Count34; i++)
        {
            if (counts[i] > 0) distinct++;
            if (counts[i] >= 2) pairs++;
        }
        return 6 - pairs + Math.Max(0, 7 - distinct);
    }

    /// <summary>
    /// Kokushi musou shanten: 13 − distinct_terminal_honor − (hasPair ? 1 : 0).
    /// </summary>
    public static int Kokushi(ReadOnlySpan<int> counts)
    {
        ReadOnlySpan<int> yaochuu = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];
        int distinct = 0;
        bool hasPair = false;
        foreach (int idx in yaochuu)
        {
            if (counts[idx] >= 1) distinct++;
            if (counts[idx] >= 2) hasPair = true;
        }
        return 13 - distinct - (hasPair ? 1 : 0);
    }

    /// <summary>
    /// Standard-form shanten (4 sets + 1 pair).
    /// meldsAlreadyCalled contributes sets at zero cost to the scan.
    /// </summary>
    public static int Standard(int[] counts, int meldsAlreadyCalled = 0)
    {
        const int BlocksNeeded = 4;
        int calledSets = meldsAlreadyCalled;

        int best = 8;

        // Try each candidate pair (head).
        for (int i = 0; i < Tile.Count34; i++)
        {
            if (counts[i] < 2) continue;
            counts[i] -= 2;
            var (sets, partials) = Decompose(counts);
            counts[i] += 2;

            int totalSets = sets + calledSets;
            int useful = Math.Min(partials, BlocksNeeded - totalSets);
            int s = 8 - 2 * totalSets - useful - 1;
            if (s < best) best = s;
        }

        // No-pair branch.
        {
            var (sets, partials) = Decompose(counts);
            int totalSets = sets + calledSets;
            int useful = Math.Min(partials, BlocksNeeded - totalSets);
            int s = 8 - 2 * totalSets - useful;
            if (s < best) best = s;
        }

        return Math.Max(best, Agari);
    }

    private readonly record struct Decomp(int Sets, int Partials)
    {
        public int Score => Sets * 2 + Partials;
        public bool Dominates(Decomp other) =>
            Sets >= other.Sets && Partials >= other.Partials;
    }

    /// <summary>
    /// Returns (sets, partials) from the single-best decomposition over the 34-space
    /// counts. "Best" = maximizes (2*sets + partials) with a tie-break preferring
    /// more sets. Honors are handled trivially (only triplets / pairs possible).
    /// </summary>
    private static Decomp Decompose(int[] counts)
    {
        var best = new Decomp(0, 0);
        Scan(counts, 0, 0, 0, ref best);
        return best;
    }

    private static void Scan(int[] counts, int pos, int sets, int partials, ref Decomp best)
    {
        // Honors run 27..33: no runs possible, only triplets and pairs.
        while (pos < Tile.Count34 && counts[pos] == 0) pos++;
        if (pos >= Tile.Count34)
        {
            var cand = new Decomp(sets, partials);
            if (cand.Score > best.Score ||
                (cand.Score == best.Score && cand.Sets > best.Sets))
            {
                best = cand;
            }
            return;
        }

        bool isHonor = pos >= 27;
        bool canRun = !isHonor && (pos % 9) <= 6
                      && counts[pos + 1] > 0 && counts[pos + 2] > 0;
        bool canKanchan = !isHonor && (pos % 9) <= 6 && counts[pos + 2] > 0;
        bool canRyanmen = !isHonor && (pos % 9) <= 7 && counts[pos + 1] > 0;

        // Try triplet.
        if (counts[pos] >= 3)
        {
            counts[pos] -= 3;
            Scan(counts, pos, sets + 1, partials, ref best);
            counts[pos] += 3;
        }

        // Try run (three consecutive).
        if (canRun)
        {
            counts[pos]--; counts[pos + 1]--; counts[pos + 2]--;
            Scan(counts, pos, sets + 1, partials, ref best);
            counts[pos]++; counts[pos + 1]++; counts[pos + 2]++;
        }

        // Try pair as partial (shanpon candidate).
        if (counts[pos] >= 2)
        {
            counts[pos] -= 2;
            Scan(counts, pos, sets, partials + 1, ref best);
            counts[pos] += 2;
        }

        // Try ryanmen / penchan (consecutive two).
        if (canRyanmen)
        {
            counts[pos]--; counts[pos + 1]--;
            Scan(counts, pos, sets, partials + 1, ref best);
            counts[pos]++; counts[pos + 1]++;
        }

        // Try kanchan (skip-one).
        if (canKanchan)
        {
            counts[pos]--; counts[pos + 2]--;
            Scan(counts, pos, sets, partials + 1, ref best);
            counts[pos]++; counts[pos + 2]++;
        }

        // Skip the remaining copies at this position.
        int save = counts[pos];
        counts[pos] = 0;
        Scan(counts, pos + 1, sets, partials, ref best);
        counts[pos] = save;
    }
}

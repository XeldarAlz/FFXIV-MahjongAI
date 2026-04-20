using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Opponents;

/// <summary>
/// Rule-based Bayesian opponent model (plan §6). Maintains per-opponent estimates of:
/// <list type="bullet">
///   <item><c>TenpaiProb[3]</c> — logistic-ish score on public evidence</item>
///   <item><c>HandMarginal[3][34]</c> — P(tile_k ∈ hand[opp]), factorized per-tile</item>
///   <item><c>DangerMap[3][34]</c> — P(deal-in | we discard k), composited from genbutsu/suji/kabe/tenpai</item>
/// </list>
/// Indexed relative to self (<c>state.OurSeat</c>): index 0=shimocha, 1=toimen, 2=kamicha.
///
/// Phase 2 MVP note: discard pools aren't yet read from the game (M4 RE owed). Until
/// they are, TenpaiProb uses only wall-remaining and meld-count heuristics, and
/// DangerMap treats <b>our own discards</b> as the only reliable genbutsu (opponents
/// never discard again what they previously discarded, but we don't see their pools
/// yet — so the model can't learn from those). Once discard pools are populated in
/// the snapshot, <see cref="Update"/> will pick them up automatically.
/// </summary>
public sealed class OpponentModel
{
    public const int OpponentCount = 3;
    private const int TileKinds = Tile.Count34;

    public double[] TenpaiProb { get; } = new double[OpponentCount];
    public double[][] HandMarginal { get; } = new double[OpponentCount][];
    public double[][] DangerMap { get; } = new double[OpponentCount][];

    /// <summary>
    /// Expected value of each opponent's hand in case they win. Scales the deal-in cost
    /// at the danger-map level. Phase 2 MVP: flat 4000 points (mangan-ish default).
    /// </summary>
    public double[] ExpectedHandValue { get; } = new double[OpponentCount];

    public OpponentModel()
    {
        for (int i = 0; i < OpponentCount; i++)
        {
            HandMarginal[i] = new double[TileKinds];
            DangerMap[i] = new double[TileKinds];
            ExpectedHandValue[i] = 4000.0;
        }
    }

    /// <summary>
    /// Recompute all per-opponent estimates from the current snapshot. Pure — no
    /// cross-tick state. Cheap enough to call every decision point.
    /// </summary>
    public void Update(StateSnapshot state)
    {
        // --- Tenpai probability: heuristic features ---
        for (int opp = 0; opp < OpponentCount; opp++)
        {
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            var seat = state.Seats[absSeat];

            // Feature 1: discard count. Later in the round = more likely tenpai.
            // Prefer the authoritative DiscardCount (pinned from addon memory
            // on every snapshot); fall back to the tile list's length only if
            // that byte wasn't resolvable.
            double discardCount = seat.DiscardCount > 0
                ? seat.DiscardCount
                : seat.Discards.Count;
            // Feature 2: meld count. Open melds accelerate tenpai.
            double meldCount = seat.Melds.Count;
            // Feature 3: riichi declared → tenpai certain.
            if (seat.Riichi) { TenpaiProb[opp] = 1.0; continue; }

            // Feature 4: wall remaining (proxy for total turn count elapsed).
            int turnsElapsed = 70 - state.WallRemaining;

            // Simple logistic with hand-picked weights. Calibration owed to M9 weight tuner.
            double z =
                -2.0
                + 0.08 * discardCount
                + 0.35 * meldCount
                + 0.02 * turnsElapsed;
            TenpaiProb[opp] = Sigmoid(z);
        }

        // --- Hand marginals: start uniform over live tiles, then adjust on public evidence. ---
        ComputeLiveTileCounts(state, out var live);
        double unseenTotal = 0;
        for (int k = 0; k < TileKinds; k++) unseenTotal += live[k];
        // Each opponent holds roughly 13 tiles; each live tile has ~13/unseenTotal chance of
        // being in their hand (minus what's in their own open melds). First approximation
        // assumes uniform over unseen tiles.
        double perTileBase = unseenTotal > 0 ? 13.0 / unseenTotal : 0.0;
        for (int opp = 0; opp < OpponentCount; opp++)
        {
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            var seat = state.Seats[absSeat];

            for (int k = 0; k < TileKinds; k++)
            {
                double p = live[k] * perTileBase;
                // Tiles an opponent has already discarded can't be in hand.
                foreach (var t in seat.Discards)
                    if (t.Id == k) { p = 0; break; }
                // Tiles in their own melds are already accounted for (not "in closed hand").
                foreach (var m in seat.Melds)
                    foreach (var t in m.Tiles)
                        if (t.Id == k) { p = 0; break; }
                HandMarginal[opp][k] = System.Math.Clamp(p, 0.0, 1.0);
            }
        }

        // --- Danger map: P(deal-in | discard k) per opponent ---
        for (int opp = 0; opp < OpponentCount; opp++)
        {
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            var seat = state.Seats[absSeat];
            double tenpai = TenpaiProb[opp];

            for (int k = 0; k < TileKinds; k++)
            {
                // Genbutsu: opponent already discarded this tile → 0% deal-in (game rule: no
                // furiten-escape for this opp).
                bool genbutsu = false;
                foreach (var t in seat.Discards)
                    if (t.Id == k) { genbutsu = true; break; }
                if (genbutsu)
                {
                    DangerMap[opp][k] = 0.0;
                    continue;
                }

                // Suji: if a 4-5-6 middle tile is in the opponent's pool, some ryanmen waits
                // on adjacent outer tiles are ruled out. Phase 2 MVP: suji-discount applies
                // only to a rough 40% of cases (approx ryanmen share of wait types).
                bool suji = false;
                if (!Tile.FromId(k).IsHonor)
                {
                    int pos = k % 9;
                    int suitBase = (k / 9) * 9;
                    int middle;
                    if (pos == 0) middle = suitBase + 3;        // 1 → middle 4
                    else if (pos == 8) middle = suitBase + 5;   // 9 → middle 6
                    else middle = -1;
                    if (middle >= 0)
                        foreach (var t in seat.Discards)
                            if (t.Id == middle) { suji = true; break; }
                }

                // Baseline wait probability given tenpai: rough 1/8 (typical tenpai has
                // ~8 accepting tile kinds; a particular kind has 1/8 chance of being a wait).
                double baseDealIn = tenpai * 0.125;
                if (suji) baseDealIn *= 0.6;

                // Kabe: 4 copies of this tile all visible → nobody can wait on it.
                if (live[k] == 0) baseDealIn = 0;

                DangerMap[opp][k] = System.Math.Clamp(baseDealIn, 0.0, 1.0);
            }
        }
    }

    /// <summary>Sum of P(deal-in) × value across all opponents if we discard kind k.</summary>
    public double ExpectedDealInCost(int tileId)
    {
        double total = 0;
        for (int opp = 0; opp < OpponentCount; opp++)
            total += DangerMap[opp][tileId] * ExpectedHandValue[opp];
        return total;
    }

    private static void ComputeLiveTileCounts(StateSnapshot state, out int[] live)
    {
        var seen = new int[TileKinds];
        foreach (var t in state.Hand) seen[t.Id]++;
        foreach (var m in state.OurMelds)
            foreach (var t in m.Tiles) seen[t.Id]++;
        foreach (var seat in state.Seats)
        {
            foreach (var t in seat.Discards) seen[t.Id]++;
            foreach (var m in seat.Melds)
                foreach (var t in m.Tiles) seen[t.Id]++;
        }
        foreach (var t in state.DoraIndicators) seen[t.Id]++;

        live = new int[TileKinds];
        for (int k = 0; k < TileKinds; k++)
            live[k] = System.Math.Max(0, Tile.CopiesPerKind - seen[k]);
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + System.Math.Exp(-z));
}

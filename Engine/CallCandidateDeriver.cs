namespace DomanMahjongAI.Engine;

/// <summary>
/// Given the closed hand and an opponent's discard that the game is offering us
/// to claim, enumerate the pon / chi / minkan <see cref="MeldCandidate"/>s we
/// could legally form. Pure — no UI or game-memory coupling.
///
/// <para>
/// <paramref name="fromSeat"/> is seat-relative with the plugin's convention:
/// 0 = self (never valid for a call candidate), 1 = shimocha, 2 = toimen,
/// 3 = kamicha. Chi is only legal when the claim comes from kamicha (3).
/// </para>
/// </summary>
public static class CallCandidateDeriver
{
    public readonly record struct Result(
        List<MeldCandidate> Pon,
        List<MeldCandidate> Chi,
        List<MeldCandidate> Kan);

    public static Result Derive(IReadOnlyList<Tile> hand, Tile claimed, int fromSeat)
    {
        var counts = new int[Tile.Count34];
        foreach (var t in hand) counts[t.Id]++;

        var pon = new List<MeldCandidate>();
        var chi = new List<MeldCandidate>();
        var kan = new List<MeldCandidate>();

        int claimedCount = counts[claimed.Id];

        if (claimedCount >= 2)
        {
            pon.Add(new MeldCandidate(
                MeldKind.Pon,
                claimed,
                [claimed, claimed],
                fromSeat));
        }

        if (claimedCount >= 3)
        {
            kan.Add(new MeldCandidate(
                MeldKind.MinKan,
                claimed,
                [claimed, claimed, claimed],
                fromSeat));
        }

        if (fromSeat == 3 && claimed.Suit != TileSuit.Honor)
        {
            int baseId = (int)claimed.Suit * 9;
            int n = claimed.Number;

            // Three run positions for the claimed tile: high-end, middle, low-end.
            TryChi(counts, claimed, fromSeat, baseId, n, -2, -1, chi);
            TryChi(counts, claimed, fromSeat, baseId, n, -1, +1, chi);
            TryChi(counts, claimed, fromSeat, baseId, n, +1, +2, chi);
        }

        return new Result(pon, chi, kan);
    }

    private static void TryChi(
        int[] counts, Tile claimed, int fromSeat,
        int baseId, int n, int offA, int offB,
        List<MeldCandidate> chi)
    {
        int aNum = n + offA;
        int bNum = n + offB;
        if (aNum < 1 || aNum > 9) return;
        if (bNum < 1 || bNum > 9) return;

        int aId = baseId + aNum - 1;
        int bId = baseId + bNum - 1;
        if (counts[aId] == 0 || counts[bId] == 0) return;

        chi.Add(new MeldCandidate(
            MeldKind.Chi,
            claimed,
            [Tile.FromId(aId), Tile.FromId(bId)],
            fromSeat));
    }
}

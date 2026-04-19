using DomanMahjongAI.Engine;
using System.Collections.Generic;

namespace DomanMahjongAI.GameState;

/// <summary>
/// In-plugin tracker for the player's own open melds within the current round.
/// The Emj addon doesn't surface open-meld records in any memory region we've been
/// able to decode; instead we record melds at the moment the plugin (or the user,
/// via hooked FireCallback) accepts a call prompt. Cleared at round start —
/// detected when the closed hand returns to a no-meld count (13 or 14).
/// </summary>
public sealed class MeldTracker
{
    private readonly List<Meld> melds = new();

    public IReadOnlyList<Meld> Melds => melds;

    /// <summary>Record a meld formed by accepting a call prompt.</summary>
    public void Record(Meld meld) => melds.Add(meld);

    /// <summary>
    /// Reset the tracked melds if the current closed-hand count indicates the round
    /// has advanced past any open melds. With 0 melds the discardable closed count is
    /// 14 and post-discard is 13 — so any count ≥ 13 proves the tracker has gone stale.
    /// </summary>
    public void ResetIfRoundEnded(int closedHandCount)
    {
        if (melds.Count > 0 && closedHandCount >= 13)
            melds.Clear();
    }

    /// <summary>Manual reset for commands / tests.</summary>
    public void Clear() => melds.Clear();
}

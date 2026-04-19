using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Tier-1 heuristic policy (plan §7). Covers the discard decision.
/// Calls (pon/chi/kan), riichi, and push/fold are owed for M8 — for now this
/// passes on call opportunities and declines riichi.
/// </summary>
public sealed class EfficiencyPolicy : IPolicy
{
    private readonly DiscardScorer.Weights weights;
    private readonly Opponents.OpponentModel opponentModel = new();

    public EfficiencyPolicy(DiscardScorer.Weights? weights = null)
    {
        this.weights = weights ?? DiscardScorer.Weights.Default;
    }

    public ActionChoice Choose(StateSnapshot state)
    {
        var legal = state.Legal;

        // Agari: if we can win, win. (Yaku check is downstream — the game shouldn't
        // expose Tsumo/Ron in legal actions unless a yaku exists, but trust the
        // caller here.)
        if (legal.Can(ActionFlags.Tsumo))
            return ActionChoice.DeclareTsumo("tsumo legal");
        if (legal.Can(ActionFlags.Ron))
            return ActionChoice.DeclareRon("ron legal");

        // Call decision via CallEvaluator.
        if (legal.Can(ActionFlags.Pon) || legal.Can(ActionFlags.Chi) ||
            legal.Can(ActionFlags.MinKan) || legal.Can(ActionFlags.ShouMinKan) ||
            legal.Can(ActionFlags.AnKan))
        {
            var callDecision = CallEvaluator.Evaluate(state);
            if (callDecision.Accept && callDecision.Chosen is { } cand)
            {
                var kind = cand.Kind switch
                {
                    MeldKind.Pon => ActionKind.Pon,
                    MeldKind.Chi => ActionKind.Chi,
                    MeldKind.AnKan => ActionKind.AnKan,
                    MeldKind.MinKan => ActionKind.MinKan,
                    MeldKind.ShouMinKan => ActionKind.ShouMinKan,
                    _ => ActionKind.Pass,
                };
                return new ActionChoice(kind, Call: cand, Reasoning: $"call: {callDecision.Reason}");
            }
            if (legal.Can(ActionFlags.Pass))
                return ActionChoice.Pass($"pass: {callDecision.Reason}");
        }

        // Defer riichi decision to M8's RiichiEvaluator; for now just discard normally.
        // (Riichi is technically "discard + declare"; without the evaluator we always
        // choose the plain discard route.)

        if (legal.Can(ActionFlags.Discard))
        {
            // Safety net for inconsistent counts: if our tracked melds + closed tiles
            // don't sum to 14, DiscardScorer would throw. The MeldTracker normally keeps
            // this consistent, but an un-tracked meld (manual click during plugin reload,
            // round-end race) can break the invariant. Tsumogiri is the safe out.
            int totalTiles = state.Hand.Count + state.OurMelds.Count * 3;
            if (totalTiles != 14 && state.Hand.Count > 0)
            {
                var drawn = state.Hand[^1];
                return ActionChoice.Discard(drawn,
                    $"tsumogiri fallback — count mismatch (closed={state.Hand.Count}, melds={state.OurMelds.Count})");
            }

            opponentModel.Update(state);
            var scored = DiscardScorer.Score(state, weights, opponentModel: opponentModel);
            if (scored.Length == 0)
                return ActionChoice.Pass("no legal discards found");

            var best = scored[0];
            int currentShanten = scored[0].ShantenAfter == 0 ? 0 : scored[0].ShantenAfter;

            // Push/fold check: if the scorer's top pick is too dangerous, switch to a
            // safe-discard regime — prefer cuts with minimal deal-in cost regardless of
            // tenpai progression.
            var pushFold = PushFoldEvaluator.Evaluate(state, currentShanten, opponentModel, best.Discard);
            if (pushFold.Fold)
            {
                var safestFirst = scored.OrderBy(sd => sd.DealInCost).ToArray();
                if (safestFirst.Length > 0)
                {
                    best = safestFirst[0];
                }
            }

            // Consider riichi: if legal and evaluator says declare.
            if (legal.Can(ActionFlags.Riichi))
            {
                var riichi = RiichiEvaluator.Evaluate(
                    state,
                    intendedDiscard: best.Discard,
                    weightedUkeireAfterDiscard: best.UkeireWeighted,
                    acceptedKindsAfterDiscard: best.UkeireKinds,
                    shantenAfterDiscard: best.ShantenAfter);

                if (riichi.Declare)
                {
                    return ActionChoice.DeclareRiichi(
                        best.Discard,
                        $"riichi on {best.Discard}: {riichi.Reason}");
                }
            }

            var reasoning =
                $"best={best.Discard} shanten={best.ShantenAfter} ukeire={best.UkeireKinds}kinds/{best.UkeireWeighted}w " +
                $"dora={best.DoraRetained} yakuhai={best.YakuhaiRetained} score={best.Score:F1}";
            return ActionChoice.Discard(best.Discard, reasoning);
        }

        return ActionChoice.Pass("no actionable legal action for efficiency policy");
    }
}

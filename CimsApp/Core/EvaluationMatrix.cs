namespace CimsApp.Core;

/// <summary>
/// Pure-function weighted-score aggregator for the procurement
/// evaluation matrix (T-S6-05, PAFM-SD F.7 third bullet — "price
/// and quality weighted"). Mirrors <see cref="StakeholderMatrix"/> /
/// <see cref="RiskMatrix"/> in shape — pure, no IO, no DB.
///
/// Caller passes per-criterion weights + per-(tender, criterion)
/// scores; the function returns per-tender weighted overall scores.
/// A tender's overall score is null until ALL criteria have a
/// recorded score for it (partial scoring is not informative).
/// </summary>
public static class EvaluationMatrix
{
    /// <summary>Floating-point tolerance for the weight-sum
    /// invariant. Σ weights must be 1.0 ± Epsilon for the matrix
    /// to be valid; outside that the OverallScores are still
    /// computed but the IsValid flag warns the caller.</summary>
    public const decimal Epsilon = 0.0001m;

    public readonly record struct CriterionInput(Guid Id, decimal Weight);
    public readonly record struct ScoreInput(Guid TenderId, Guid CriterionId, decimal Score);
    public readonly record struct TenderResult(Guid TenderId, decimal? OverallScore);
    public readonly record struct MatrixResult(
        decimal TotalWeight,
        bool IsValid,
        IReadOnlyList<TenderResult> Tenders);

    /// <summary>
    /// Compute per-tender weighted overall scores. For each tender:
    /// - If every criterion has a recorded score: OverallScore =
    ///   Σ(weight × score). Range 0..100 when weights sum to 1.0
    ///   and every score is in [0, 100].
    /// - If any criterion is unscored: OverallScore = null
    ///   (incomplete evaluation).
    ///
    /// TotalWeight is reported alongside; IsValid = true iff
    /// |TotalWeight - 1.0| < Epsilon. Caller decides whether to
    /// surface OverallScore values when IsValid is false.
    /// </summary>
    public static MatrixResult Compute(
        IReadOnlyCollection<Guid> tenderIds,
        IReadOnlyCollection<CriterionInput> criteria,
        IReadOnlyCollection<ScoreInput> scores)
    {
        var totalWeight = criteria.Sum(c => c.Weight);
        var isValid = Math.Abs(totalWeight - 1.0m) < Epsilon;

        // (TenderId, CriterionId) → Score lookup.
        var scoresByPair = new Dictionary<(Guid, Guid), decimal>(scores.Count);
        foreach (var s in scores)
        {
            scoresByPair[(s.TenderId, s.CriterionId)] = s.Score;
        }

        var results = new List<TenderResult>(tenderIds.Count);
        foreach (var tid in tenderIds)
        {
            decimal overall = 0m;
            var complete = true;
            foreach (var c in criteria)
            {
                if (!scoresByPair.TryGetValue((tid, c.Id), out var s))
                {
                    complete = false;
                    break;
                }
                overall += c.Weight * s;
            }
            results.Add(new TenderResult(
                tid,
                complete ? overall : null));
        }

        return new MatrixResult(totalWeight, isValid, results);
    }
}

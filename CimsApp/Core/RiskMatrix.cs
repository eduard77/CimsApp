using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// 5×5 probability-impact matrix primitives for the Risk module
/// (T-S2-05, PAFM-SD F.3 second bullet). Pure functions: no IO, no
/// DB, no DI. Inputs are Risk rows already filtered to a project
/// (caller's responsibility); outputs are matrix cells suitable for
/// heat-map rendering.
///
/// The matrix is the standard PMBOK 5×5 grid with Probability and
/// Impact each ranging 1..5; Score = Probability × Impact ranges 1..25.
/// Risk score thresholds (low / medium / high mapping) are NOT baked
/// in here — they belong to S14 Admin Console as a per-tenant setting
/// per the S2 kickoff Top-3-risks mitigation. Numeric outputs only.
/// </summary>
public static class RiskMatrix
{
    public const int Min = 1;
    public const int Max = 5;

    /// <summary>P × I, with both arguments range-checked. Out-of-range
    /// inputs return 0 — never throws. Service layer is the place
    /// that enforces 1..5 (RisksService validates at write time).</summary>
    public static int Score(int probability, int impact)
    {
        if (probability < Min || probability > Max) return 0;
        if (impact < Min || impact > Max) return 0;
        return probability * impact;
    }

    /// <summary>
    /// Project a sequence of Risk rows onto the 25-cell matrix. Always
    /// returns exactly 25 cells (one per (P, I) coordinate from
    /// (1, 1) through (5, 5)), in row-major order: (P=1,I=1) first,
    /// then (P=1,I=2)..(P=1,I=5), then (P=2,I=*)..., (P=5,I=*) last.
    /// Empty cells carry an empty RiskIds list — the UI can render the
    /// full grid without filtering. Risks whose Probability or Impact
    /// fall outside 1..5 are silently dropped (defensive: should never
    /// happen if writes go through RisksService validation).
    /// </summary>
    public static List<RiskMatrixCell> Build(IEnumerable<Risk> risks)
    {
        var cells = new List<RiskMatrixCell>(Max * Max);
        for (int p = Min; p <= Max; p++)
            for (int i = Min; i <= Max; i++)
                cells.Add(new RiskMatrixCell
                {
                    Probability = p,
                    Impact      = i,
                    Score       = p * i,
                    RiskIds     = new List<Guid>(),
                });

        foreach (var r in risks)
        {
            if (r.Probability < Min || r.Probability > Max) continue;
            if (r.Impact < Min || r.Impact > Max) continue;
            var idx = (r.Probability - Min) * Max + (r.Impact - Min);
            cells[idx].RiskIds.Add(r.Id);
        }
        return cells;
    }
}

/// <summary>
/// One cell of the 5×5 risk matrix. RiskIds is mutable by design — the
/// builder appends; once returned, treat as a read-only view.
/// Serialised via the existing API JSON conventions.
/// </summary>
public sealed class RiskMatrixCell
{
    public int Probability { get; init; }
    public int Impact      { get; init; }
    public int Score       { get; init; }
    public List<Guid> RiskIds { get; init; } = new();
}

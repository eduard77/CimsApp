using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// 5×5 Power/Interest matrix primitives for the Stakeholder module
/// (T-S3-04, PAFM-SD F.4 first bullet — power/interest scoring).
/// Mirrors <see cref="RiskMatrix"/> in shape: pure functions, no IO,
/// no DB, no DI; inputs are Stakeholder rows already filtered to a
/// project, outputs are cells suitable for heat-map rendering.
///
/// The matrix follows Mendelow's grid (Power × Interest, both 1..5).
/// Threshold mapping (which quadrant each row sits in) is captured
/// per-row as <see cref="Stakeholder.EngagementApproach"/>; this file
/// just produces the 25-cell layout. Per-tenant threshold override
/// is S14 territory.
/// </summary>
public static class StakeholderMatrix
{
    public const int Min = 1;
    public const int Max = 5;

    /// <summary>P × I, range-checked. Out-of-range inputs return 0.
    /// Service layer enforces 1..5 at write time.</summary>
    public static int Score(int power, int interest)
    {
        if (power < Min || power > Max) return 0;
        if (interest < Min || interest > Max) return 0;
        return power * interest;
    }

    /// <summary>
    /// Project a sequence of Stakeholder rows onto the 25-cell matrix.
    /// Always returns exactly 25 cells in row-major order from
    /// (P=1,I=1) to (P=5,I=5). Empty cells carry an empty
    /// StakeholderIds list. Out-of-range rows are silently dropped.
    /// </summary>
    public static List<StakeholderMatrixCell> Build(IEnumerable<Stakeholder> stakeholders)
    {
        var cells = new List<StakeholderMatrixCell>(Max * Max);
        for (int p = Min; p <= Max; p++)
            for (int i = Min; i <= Max; i++)
                cells.Add(new StakeholderMatrixCell
                {
                    Power           = p,
                    Interest        = i,
                    Score           = p * i,
                    StakeholderIds  = new List<Guid>(),
                });

        foreach (var s in stakeholders)
        {
            if (s.Power < Min || s.Power > Max) continue;
            if (s.Interest < Min || s.Interest > Max) continue;
            var idx = (s.Power - Min) * Max + (s.Interest - Min);
            cells[idx].StakeholderIds.Add(s.Id);
        }
        return cells;
    }
}

/// <summary>One cell of the 5×5 stakeholder matrix. StakeholderIds is
/// mutable by design — the builder appends; once returned, treat as
/// read-only.</summary>
public sealed class StakeholderMatrixCell
{
    public int Power     { get; init; }
    public int Interest  { get; init; }
    public int Score     { get; init; }
    public List<Guid> StakeholderIds { get; init; } = new();
}

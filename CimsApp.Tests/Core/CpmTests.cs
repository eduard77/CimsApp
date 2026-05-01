using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="Cpm"/> (T-S4-04). Textbook
/// PMBOK fixtures: linear chain, diamond, lag, four dependency
/// types, scheduling constraints, and the free-float-vs-total-float
/// distinction. No DB / DI / IO.
///
/// All test schedules use day-granular durations and a fixed
/// project-start of 2026-06-01 (a Monday — although the v1.0 solver
/// ignores calendar / non-working-day rules, picking a Monday avoids
/// reader confusion when expanding to v1.1).
/// </summary>
public class CpmTests
{
    private static readonly DateTime PS = new(2026, 6, 1);

    private static Cpm.CpmActivity Act(Guid id, decimal duration) =>
        new(id, duration);

    private static Cpm.CpmDependency Dep(Guid pred, Guid succ,
        DependencyType type = DependencyType.FS, decimal lag = 0m) =>
        new(pred, succ, type, lag);

    // ── Empty / trivial ─────────────────────────────────────────────

    [Fact]
    public void Solve_returns_empty_for_empty_activity_set()
    {
        var r = Cpm.Solve(PS, [], []);
        Assert.Empty(r.Activities);
        Assert.Equal(PS, r.ProjectFinish);
    }

    [Fact]
    public void Solve_single_activity_no_dependencies()
    {
        var a = Guid.NewGuid();
        var r = Cpm.Solve(PS, [Act(a, 5m)], []);
        var x = r.Activities.Single();
        Assert.Equal(PS,                  x.EarlyStart);
        Assert.Equal(PS.AddDays(5),       x.EarlyFinish);
        Assert.Equal(PS,                  x.LateStart);
        Assert.Equal(PS.AddDays(5),       x.LateFinish);
        Assert.Equal(0m,                  x.TotalFloat);
        Assert.Equal(0m,                  x.FreeFloat);
        Assert.True(x.IsCritical);
        Assert.Equal(PS.AddDays(5),       r.ProjectFinish);
    }

    // ── Linear chain ────────────────────────────────────────────────

    [Fact]
    public void Solve_linear_chain_three_activities_FS_zero_lag()
    {
        // A(2) → B(3) → C(4)   project finish = 9 days; everything critical.
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 2m), Act(b, 3m), Act(c, 4m)],
            [Dep(a, b),  Dep(b, c)]);

        var ra = r.Activities.Single(x => x.Id == a);
        var rb = r.Activities.Single(x => x.Id == b);
        var rc = r.Activities.Single(x => x.Id == c);

        Assert.Equal(PS,                ra.EarlyStart);
        Assert.Equal(PS.AddDays(2),     ra.EarlyFinish);
        Assert.Equal(PS.AddDays(2),     rb.EarlyStart);
        Assert.Equal(PS.AddDays(5),     rb.EarlyFinish);
        Assert.Equal(PS.AddDays(5),     rc.EarlyStart);
        Assert.Equal(PS.AddDays(9),     rc.EarlyFinish);
        Assert.Equal(PS.AddDays(9),     r.ProjectFinish);

        // Linear chain → all activities critical.
        Assert.True(ra.IsCritical);
        Assert.True(rb.IsCritical);
        Assert.True(rc.IsCritical);
        Assert.Equal(0m, ra.TotalFloat);
        Assert.Equal(0m, rb.TotalFloat);
        Assert.Equal(0m, rc.TotalFloat);
    }

    // ── Parallel paths with floats ──────────────────────────────────

    [Fact]
    public void Solve_diamond_identifies_critical_path_and_assigns_float_to_shorter_branch()
    {
        //         A(2) ─→ B(5) ─┐
        // start                  → D(2)
        //         A(2) ─→ C(2) ─┘
        // Critical path: A → B → D (total 9 days).
        // C is the slack branch: total float = 5 - 2 = 3 days.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var c = Guid.NewGuid(); var d = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 2m), Act(b, 5m), Act(c, 2m), Act(d, 2m)],
            [Dep(a, b),  Dep(a, c),  Dep(b, d),  Dep(c, d)]);

        var ra = r.Activities.Single(x => x.Id == a);
        var rb = r.Activities.Single(x => x.Id == b);
        var rc = r.Activities.Single(x => x.Id == c);
        var rd = r.Activities.Single(x => x.Id == d);

        Assert.Equal(PS.AddDays(9), r.ProjectFinish);
        Assert.True(ra.IsCritical);
        Assert.True(rb.IsCritical);
        Assert.True(rd.IsCritical);
        Assert.False(rc.IsCritical);                    // slack branch
        Assert.Equal(3m, rc.TotalFloat);                // 5 - 2 = 3 days slack
        Assert.Equal(3m, rc.FreeFloat);                 // delaying C by 3 days still doesn't move D
    }

    [Fact]
    public void Solve_distinguishes_free_float_from_total_float()
    {
        //   A(2) ─→ B(2) ─→ D(3)
        //               ↗
        //   A(2) ─→ C(1)
        // Path A→B→D = 7 (critical). C contributes to D's start at day 3,
        // but D actually starts at day 4 (B finishes then). C's earliest
        // finish is day 3, so:
        //   C.TotalFloat = 4 - 3 = 1 day
        //   C.FreeFloat  = 4 - 3 = 1 day (delaying C delays D)
        // Hmm — that example collapses TF and FF. Use the classic example:
        //
        //   A(2) ─→ B(3) ─→ D(2)
        //   A(2) ─→ C(1) ─→ E(2)  (E has no successors after; D and E both
        //                          terminal at project finish)
        // Project finish = max(D.EF=7, E.EF=5) = 7.
        // Path A→B→D critical (TF=0).
        // E.TotalFloat = 7 - 5 = 2 days.
        // C.TotalFloat = 7 - 3 = 4? Actually LS_C = LS_E - 0 lag = ?
        //   E.LF = 7, E.LS = 5; C is predecessor of E with FS lag 0:
        //   C.LF = E.LS = 5; C.LS = 5 - 1 = 4; C.ES = 2; C.TF = 4 - 2 = 2.
        // C.FreeFloat: looking forward to E, slack = E.ES - (C.EF + 0) = 3 - 3 = 0.
        // So C.FF = 0 even though C.TF = 2. Classic distinction.
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var d = Guid.NewGuid(); var e = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 2m), Act(b, 3m), Act(c, 1m), Act(d, 2m), Act(e, 2m)],
            [Dep(a, b),  Dep(a, c),  Dep(b, d),  Dep(c, e)]);

        var rc = r.Activities.Single(x => x.Id == c);
        var re = r.Activities.Single(x => x.Id == e);

        Assert.Equal(2m, rc.TotalFloat);
        Assert.Equal(0m, rc.FreeFloat);     // delay of C immediately delays E
        Assert.Equal(2m, re.TotalFloat);
        Assert.Equal(2m, re.FreeFloat);     // E is a terminal, slack to project finish
    }

    // ── Lag and lead ────────────────────────────────────────────────

    [Fact]
    public void Solve_FS_dependency_with_positive_lag_pushes_successor_later()
    {
        // A(3) → B(2) with FS lag 4 days. B starts at A.EF + 4 = day 7.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 3m), Act(b, 2m)],
            [Dep(a, b, DependencyType.FS, 4m)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(7), rb.EarlyStart);
        Assert.Equal(PS.AddDays(9), rb.EarlyFinish);
        Assert.Equal(PS.AddDays(9), r.ProjectFinish);
    }

    [Fact]
    public void Solve_FS_dependency_with_negative_lag_overlaps_successor()
    {
        // A(5) → B(3) with FS lag -2 days (i.e. lead). B starts 2 days
        // before A finishes: B.ES = 5 - 2 = 3; B.EF = 6.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 5m), Act(b, 3m)],
            [Dep(a, b, DependencyType.FS, -2m)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(3), rb.EarlyStart);
        Assert.Equal(PS.AddDays(6), rb.EarlyFinish);
    }

    // ── Dependency types ────────────────────────────────────────────

    [Fact]
    public void Solve_SS_dependency_starts_successor_when_predecessor_starts()
    {
        // A(5) →SS B(3) with lag 1. B.ES = A.ES + 1 = 1; B.EF = 4.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 5m), Act(b, 3m)],
            [Dep(a, b, DependencyType.SS, 1m)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(1), rb.EarlyStart);
        Assert.Equal(PS.AddDays(4), rb.EarlyFinish);
    }

    [Fact]
    public void Solve_FF_dependency_finishes_successor_when_predecessor_finishes()
    {
        // A(5) →FF B(3) with lag 0. B.EF = A.EF = 5; B.ES = 5 - 3 = 2.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 5m), Act(b, 3m)],
            [Dep(a, b, DependencyType.FF, 0m)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(2), rb.EarlyStart);
        Assert.Equal(PS.AddDays(5), rb.EarlyFinish);
    }

    [Fact]
    public void Solve_SF_dependency_finishes_successor_after_predecessor_starts()
    {
        // A(2) →SF B(3) with lag 5. B.EF = A.ES + 5 = 5; B.ES = 5 - 3 = 2.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 2m), Act(b, 3m)],
            [Dep(a, b, DependencyType.SF, 5m)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(2), rb.EarlyStart);
        Assert.Equal(PS.AddDays(5), rb.EarlyFinish);
    }

    // ── Scheduling constraints ──────────────────────────────────────

    [Fact]
    public void Solve_SNET_constraint_pushes_activity_later()
    {
        // A(3) with SNET = day 5: A.ES = 5 (not 0); A.EF = 8.
        var a = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [new Cpm.CpmActivity(a, 3m, ConstraintType.SNET, PS.AddDays(5))],
            []);

        var ra = r.Activities.Single();
        Assert.Equal(PS.AddDays(5), ra.EarlyStart);
        Assert.Equal(PS.AddDays(8), ra.EarlyFinish);
    }

    [Fact]
    public void Solve_MSO_constraint_hard_pins_start()
    {
        // A(3) with MSO = day 4. ES = 4 regardless of dependency-driven ES.
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 1m),
             new Cpm.CpmActivity(b, 3m, ConstraintType.MSO, PS.AddDays(4))],
            [Dep(a, b)]);

        var rb = r.Activities.Single(x => x.Id == b);
        Assert.Equal(PS.AddDays(4), rb.EarlyStart);
        Assert.Equal(PS.AddDays(7), rb.EarlyFinish);
    }

    [Fact]
    public void Solve_MFO_constraint_hard_pins_finish()
    {
        // A(5) with MFO = day 10. EF = 10; ES = 5.
        var a = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [new Cpm.CpmActivity(a, 5m, ConstraintType.MFO, PS.AddDays(10))],
            []);

        var ra = r.Activities.Single();
        Assert.Equal(PS.AddDays(5),  ra.EarlyStart);
        Assert.Equal(PS.AddDays(10), ra.EarlyFinish);
    }

    // ── Cycle handling (delegated to DependencyGraph) ───────────────

    [Fact]
    public void Solve_throws_on_cyclic_input()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            Cpm.Solve(PS,
                [Act(a, 1m), Act(b, 1m)],
                [Dep(a, b),  Dep(b, a)]));
    }

    // ── Half-day durations round-trip ───────────────────────────────

    [Fact]
    public void Solve_handles_decimal_half_day_duration()
    {
        // A(2.5) → B(0.5) → C(1). Linear, project finish = 4 days.
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var r = Cpm.Solve(PS,
            [Act(a, 2.5m), Act(b, 0.5m), Act(c, 1m)],
            [Dep(a, b),    Dep(b, c)]);

        Assert.Equal(PS.AddHours(96), r.ProjectFinish);  // 4 days
    }
}

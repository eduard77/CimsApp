using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Critical Path Method (CPM) solver for the Schedule & Programme
/// module (T-S4-04, PAFM-SD F.5 first bullet — "CPM-based scheduling
/// with tasks, dependencies, constraints"). Pure functions, no IO,
/// no DB, no DI — caller passes activity + dependency snapshots and
/// gets back early/late dates, total/free float, and a critical-path
/// flag per activity.
///
/// Algorithm:
/// 1. Topological sort of activities (Core/DependencyGraph.cs).
///    Cycles are caller's problem to reject; this solver throws if
///    fed a cyclic graph.
/// 2. Forward pass (topological order): for each activity, ES =
///    max over predecessor links of the link's ES-driver
///    (FS/SS/FF/SF + Lag); apply scheduling constraint (SNET / MSO
///    / FNET / MFO); EF = ES + Duration.
/// 3. Backward pass (reverse topological order): project finish =
///    max(EF). For each activity in reverse, LF = min over successor
///    links of the link's LF-driver; LS = LF - Duration.
/// 4. Floats: TotalFloat = LS - ES; FreeFloat = min over successors
///    of "how much can this activity slip without delaying the
///    successor's earliest possible start (or finish, for FF/SF)".
/// 5. Critical path: TotalFloat == 0.
///
/// Decimal duration days are converted to TimeSpan via double — sub-
/// second drift accumulates over fractional-day arithmetic but stays
/// well below the day-granularity the UI shows. v1.0 only supports
/// the Day unit; Hour reserved for v1.1 (DurationUnit on Activity).
/// </summary>
public static class Cpm
{
    /// <summary>Activity input snapshot — the minimum set of fields
    /// the solver needs from a persisted Activity row.</summary>
    public readonly record struct CpmActivity(
        Guid Id,
        decimal Duration,
        ConstraintType Constraint = ConstraintType.ASAP,
        DateTime? ConstraintDate = null);

    /// <summary>Dependency input snapshot.</summary>
    public readonly record struct CpmDependency(
        Guid PredecessorId,
        Guid SuccessorId,
        DependencyType Type = DependencyType.FS,
        decimal Lag = 0m);

    /// <summary>Per-activity solver result. Decimal floats so half-day
    /// granularity round-trips without representational error.</summary>
    public readonly record struct CpmActivityResult(
        Guid Id,
        DateTime EarlyStart,
        DateTime EarlyFinish,
        DateTime LateStart,
        DateTime LateFinish,
        decimal TotalFloat,
        decimal FreeFloat,
        bool IsCritical);

    /// <summary>Whole-schedule solver output. ProjectStart is the
    /// caller's input data-date; ProjectFinish is the latest
    /// EarlyFinish across the activity set.</summary>
    public readonly record struct CpmResult(
        DateTime ProjectStart,
        DateTime ProjectFinish,
        IReadOnlyList<CpmActivityResult> Activities);

    /// <summary>
    /// Solve the schedule. Activity IDs and dependency endpoints
    /// must be consistent (every endpoint must appear in the activity
    /// set). Cycles are rejected via DependencyGraph.TopologicalSort
    /// (throws InvalidOperationException). Empty activity set returns
    /// an empty result with ProjectFinish = ProjectStart.
    /// </summary>
    public static CpmResult Solve(
        DateTime projectStart,
        IReadOnlyCollection<CpmActivity> activities,
        IReadOnlyCollection<CpmDependency> dependencies)
    {
        if (activities.Count == 0)
            return new CpmResult(projectStart, projectStart, []);

        var byId = activities.ToDictionary(a => a.Id);
        var deps = dependencies.ToList();

        // Group dependencies by successor for forward pass and by
        // predecessor for backward / free-float passes.
        var predLinks = deps.GroupBy(d => d.SuccessorId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var succLinks = deps.GroupBy(d => d.PredecessorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var topoEdges = deps.Select(d => (d.PredecessorId, d.SuccessorId)).ToList();
        var order = DependencyGraph.TopologicalSort([..byId.Keys], topoEdges);

        // ── Forward pass: ES / EF ───────────────────────────────────
        var es = new Dictionary<Guid, DateTime>(activities.Count);
        var ef = new Dictionary<Guid, DateTime>(activities.Count);
        foreach (var id in order)
        {
            var act = byId[id];
            var dur = ToTimeSpan(act.Duration);
            DateTime computedES;
            if (predLinks.TryGetValue(id, out var preds))
            {
                computedES = preds.Select(p => DriverEsForward(p, es[p.PredecessorId], ef[p.PredecessorId], dur))
                    .Max();
            }
            else
            {
                computedES = projectStart;
            }
            // Apply scheduling constraints on the start side.
            computedES = ApplyForwardConstraint(act, computedES, dur);
            es[id] = computedES;
            ef[id] = ApplyForwardFinishConstraint(act, computedES + dur);
        }

        var projectFinish = ef.Values.Max();

        // ── Backward pass: LF / LS ──────────────────────────────────
        var ls = new Dictionary<Guid, DateTime>(activities.Count);
        var lf = new Dictionary<Guid, DateTime>(activities.Count);
        foreach (var id in order.AsEnumerable().Reverse())
        {
            var act = byId[id];
            var dur = ToTimeSpan(act.Duration);
            DateTime computedLF;
            if (succLinks.TryGetValue(id, out var succs))
            {
                computedLF = succs.Select(s => DriverLfBackward(s, ls[s.SuccessorId], lf[s.SuccessorId], dur))
                    .Min();
            }
            else
            {
                computedLF = projectFinish;
            }
            // Apply scheduling constraints on the finish side.
            computedLF = ApplyBackwardConstraint(act, computedLF, dur);
            lf[id] = computedLF;
            ls[id] = computedLF - dur;
        }

        // ── Floats + critical path ──────────────────────────────────
        var results = new List<CpmActivityResult>(activities.Count);
        foreach (var id in order)
        {
            var act = byId[id];
            var totalFloat = ToDecimalDays(ls[id] - es[id]);
            decimal freeFloat;
            if (succLinks.TryGetValue(id, out var succs))
            {
                freeFloat = succs.Select(s => FreeFloatContribution(s, es[id], ef[id], es[s.SuccessorId], ef[s.SuccessorId])).Min();
            }
            else
            {
                // No successors → free float bounded by project finish.
                freeFloat = ToDecimalDays(projectFinish - ef[id]);
            }
            var isCritical = totalFloat == 0m;
            results.Add(new CpmActivityResult(id, es[id], ef[id], ls[id], lf[id], totalFloat, freeFloat, isCritical));
        }

        return new CpmResult(projectStart, projectFinish, results);
    }

    // ── Forward-pass dependency drivers ─────────────────────────────
    // For each dependency type the predecessor's contribution to the
    // successor's earliest-start is computed differently. Lag (decimal
    // days, can be negative) is applied to the trigger event:
    //   FS: succ.ES = pred.EF + lag
    //   SS: succ.ES = pred.ES + lag
    //   FF: succ.EF = pred.EF + lag → succ.ES = succ.EF - dur
    //   SF: succ.EF = pred.ES + lag → succ.ES = succ.EF - dur
    private static DateTime DriverEsForward(
        CpmDependency d, DateTime predES, DateTime predEF, TimeSpan succDur)
    {
        var lag = ToTimeSpan(d.Lag);
        return d.Type switch
        {
            DependencyType.FS => predEF + lag,
            DependencyType.SS => predES + lag,
            DependencyType.FF => (predEF + lag) - succDur,
            DependencyType.SF => (predES + lag) - succDur,
            _                 => predEF + lag,
        };
    }

    // ── Backward-pass dependency drivers ────────────────────────────
    // Reverse of the forward drivers: each successor constrains the
    // predecessor's latest-finish.
    //   FS: pred.LF = succ.LS - lag
    //   SS: pred.LS = succ.LS - lag → pred.LF = pred.LS + dur
    //   FF: pred.LF = succ.LF - lag
    //   SF: pred.LS = succ.LF - lag → pred.LF = pred.LS + dur
    private static DateTime DriverLfBackward(
        CpmDependency d, DateTime succLS, DateTime succLF, TimeSpan predDur)
    {
        var lag = ToTimeSpan(d.Lag);
        return d.Type switch
        {
            DependencyType.FS => succLS - lag,
            DependencyType.SS => (succLS - lag) + predDur,
            DependencyType.FF => succLF - lag,
            DependencyType.SF => (succLF - lag) + predDur,
            _                 => succLS - lag,
        };
    }

    // ── Free float contribution per successor link ──────────────────
    // FreeFloat = "how much can THIS activity slip without delaying
    // the successor's earliest possible start/finish given the link
    // type". Negative contributions clamp to zero — a negative answer
    // would mean the schedule is already infeasible (caught elsewhere).
    private static decimal FreeFloatContribution(
        CpmDependency d, DateTime predES, DateTime predEF,
        DateTime succES, DateTime succEF)
    {
        var lag = ToTimeSpan(d.Lag);
        var slack = d.Type switch
        {
            DependencyType.FS => succES - (predEF + lag),
            DependencyType.SS => succES - (predES + lag),
            DependencyType.FF => succEF - (predEF + lag),
            DependencyType.SF => succEF - (predES + lag),
            _                 => succES - (predEF + lag),
        };
        var days = ToDecimalDays(slack);
        return days < 0m ? 0m : days;
    }

    // ── Constraint application ──────────────────────────────────────
    // ASAP / ALAP are the no-op defaults for forward / backward passes
    // respectively. SNET / MSO pin the start side; FNET / MFO pin the
    // finish side. SNLT / FNLT are validation-only (don't push the
    // schedule earlier; flagged by reporting layers if violated). v1.0
    // implements the four hard pins (SNET / FNET / MSO / MFO); SNLT /
    // FNLT are accepted but treated as ASAP — no v1.0 endpoint
    // surfaces the violation, deferred to v1.1 reporting.
    private static DateTime ApplyForwardConstraint(CpmActivity a, DateTime computedES, TimeSpan dur)
    {
        if (!a.ConstraintDate.HasValue) return computedES;
        var d = a.ConstraintDate.Value;
        return a.Constraint switch
        {
            ConstraintType.SNET => computedES > d ? computedES : d,           // start no earlier than
            ConstraintType.MSO  => d,                                         // hard-pin start
            ConstraintType.MFO  => d - dur,                                   // hard-pin finish ⇒ start
            ConstraintType.FNET => (computedES + dur) > d ? computedES : d - dur,
            _                   => computedES,
        };
    }

    private static DateTime ApplyForwardFinishConstraint(CpmActivity a, DateTime computedEF)
    {
        if (!a.ConstraintDate.HasValue) return computedEF;
        return a.Constraint switch
        {
            ConstraintType.MFO  => a.ConstraintDate!.Value,
            ConstraintType.FNET => computedEF > a.ConstraintDate!.Value ? computedEF : a.ConstraintDate!.Value,
            _                   => computedEF,
        };
    }

    private static DateTime ApplyBackwardConstraint(CpmActivity a, DateTime computedLF, TimeSpan dur)
    {
        if (!a.ConstraintDate.HasValue) return computedLF;
        var d = a.ConstraintDate.Value;
        return a.Constraint switch
        {
            ConstraintType.FNLT => computedLF < d ? computedLF : d,
            ConstraintType.MFO  => d,                                         // hard-pin finish
            ConstraintType.MSO  => d + dur,                                   // hard-pin start ⇒ finish
            ConstraintType.SNLT => (computedLF - dur) < d ? computedLF : d + dur,
            _                   => computedLF,
        };
    }

    // ── Decimal ↔ TimeSpan helpers ──────────────────────────────────
    private static TimeSpan ToTimeSpan(decimal days) =>
        TimeSpan.FromTicks((long)(days * TimeSpan.TicksPerDay));

    private static decimal ToDecimalDays(TimeSpan ts) =>
        (decimal)ts.Ticks / TimeSpan.TicksPerDay;
}

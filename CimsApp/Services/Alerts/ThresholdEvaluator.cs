using CimsApp.Data;
using CimsApp.Models;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services.Alerts;

/// <summary>
/// Pure metric computation for the v1.0 AlertMetric set. T-S14-04.
/// Each method takes a project id and returns the current observed
/// value. Kept separate from <see cref="ThresholdEvaluatorHostedService"/>
/// so the comparison + cooldown logic stays unit-testable.
/// </summary>
public static class AlertMetricComputer
{
    public static async Task<decimal> ComputeAsync(
        CimsDbContext db, AlertMetric metric, Guid projectId,
        CancellationToken ct = default) => metric switch
    {
        AlertMetric.CostUtilizationPercent => await CostUtilizationPercent(db, projectId, ct),
        AlertMetric.OpenEarlyWarnings      => await OpenEarlyWarnings(db, projectId, ct),
        AlertMetric.OpenRisks              => await OpenRisks(db, projectId, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static async Task<decimal> CostUtilizationPercent(
        CimsDbContext db, Guid projectId, CancellationToken ct)
    {
        var totalBudget = await db.CostBreakdownItems
            .IgnoreQueryFilters()
            .Where(c => c.ProjectId == projectId && c.Budget.HasValue)
            .SumAsync(c => c.Budget!.Value, ct);
        if (totalBudget == 0m) return 0m;
        var totalCommitted = await db.Commitments.IgnoreQueryFilters()
            .Where(c => c.ProjectId == projectId)
            .SumAsync(c => (decimal?)c.Amount, ct) ?? 0m;
        var totalActuals = await db.ActualCosts.IgnoreQueryFilters()
            .Where(a => a.ProjectId == projectId)
            .SumAsync(a => (decimal?)a.Amount, ct) ?? 0m;
        return Math.Round(100m * (totalCommitted + totalActuals) / totalBudget, 4);
    }

    private static async Task<decimal> OpenEarlyWarnings(
        CimsDbContext db, Guid projectId, CancellationToken ct)
        => await db.EarlyWarnings.IgnoreQueryFilters()
            .CountAsync(w => w.ProjectId == projectId && w.State != EarlyWarningState.Closed, ct);

    private static async Task<decimal> OpenRisks(
        CimsDbContext db, Guid projectId, CancellationToken ct)
        => await db.Risks.IgnoreQueryFilters()
            .CountAsync(r => r.ProjectId == projectId && r.Status != RiskStatus.Closed, ct);
}

/// <summary>
/// Pure comparison + cooldown gating. Decoupled from EF / DI so
/// tests cover the decision logic without touching the database.
/// </summary>
public static class ThresholdRule
{
    public static bool IsBreached(decimal observed, AlertComparison comparison, decimal threshold)
        => comparison switch
        {
            AlertComparison.GreaterThan        => observed >  threshold,
            AlertComparison.GreaterThanOrEqual => observed >= threshold,
            AlertComparison.LessThan           => observed <  threshold,
            AlertComparison.LessThanOrEqual    => observed <= threshold,
            AlertComparison.Equal              => observed == threshold,
            _ => false,
        };

    public static bool IsInCooldown(DateTime? lastFiredAt, int cooldownMinutes, DateTime now)
        => lastFiredAt.HasValue && now < lastFiredAt.Value.AddMinutes(cooldownMinutes);
}

using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Alerts;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Alerts;

/// <summary>
/// T-S14-04 pure-logic tests for the threshold evaluator's three
/// decision paths: <see cref="ThresholdRule.IsBreached"/>,
/// <see cref="ThresholdRule.IsInCooldown"/>, and the metric
/// computation against a seeded fixture.
/// </summary>
public class ThresholdEvaluatorTests
{
    [Theory]
    [InlineData(AlertComparison.GreaterThan, 110.0, 100.0, true)]
    [InlineData(AlertComparison.GreaterThan, 100.0, 100.0, false)]
    [InlineData(AlertComparison.GreaterThanOrEqual, 100.0, 100.0, true)]
    [InlineData(AlertComparison.LessThan, 5.0, 10.0, true)]
    [InlineData(AlertComparison.LessThan, 10.0, 10.0, false)]
    [InlineData(AlertComparison.LessThanOrEqual, 10.0, 10.0, true)]
    [InlineData(AlertComparison.Equal, 5.0, 5.0, true)]
    [InlineData(AlertComparison.Equal, 5.0, 6.0, false)]
    public void IsBreached_evaluates_each_comparison_operator(
        AlertComparison op, double observed, double threshold, bool expected)
    {
        Assert.Equal(expected,
            ThresholdRule.IsBreached((decimal)observed, op, (decimal)threshold));
    }

    [Fact]
    public void IsInCooldown_returns_false_when_never_fired()
    {
        Assert.False(ThresholdRule.IsInCooldown(null, 60, DateTime.UtcNow));
    }

    [Fact]
    public void IsInCooldown_returns_true_within_window()
    {
        var now = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc);
        var lastFired = now.AddMinutes(-30);
        Assert.True(ThresholdRule.IsInCooldown(lastFired, 60, now));
    }

    [Fact]
    public void IsInCooldown_returns_false_after_window()
    {
        var now = new DateTime(2026, 5, 3, 12, 0, 0, DateTimeKind.Utc);
        var lastFired = now.AddMinutes(-90);
        Assert.False(ThresholdRule.IsInCooldown(lastFired, 60, now));
    }

    [Fact]
    public async Task ComputeAsync_OpenRisks_counts_non_closed()
    {
        var (db, projectId, _) = BuildSeededDb();
        // 2 active risks + 1 closed.
        db.Risks.Add(NewRisk(projectId, RiskStatus.Identified));
        db.Risks.Add(NewRisk(projectId, RiskStatus.Active));
        db.Risks.Add(NewRisk(projectId, RiskStatus.Closed));
        await db.SaveChangesAsync();

        var n = await AlertMetricComputer.ComputeAsync(db, AlertMetric.OpenRisks, projectId);
        Assert.Equal(2m, n);
    }

    [Fact]
    public async Task ComputeAsync_CostUtilizationPercent_handles_zero_budget()
    {
        var (db, projectId, _) = BuildSeededDb();
        // No CBS budget rows.
        var pct = await AlertMetricComputer.ComputeAsync(
            db, AlertMetric.CostUtilizationPercent, projectId);
        Assert.Equal(0m, pct);
    }

    [Fact]
    public async Task ComputeAsync_CostUtilizationPercent_computes_committed_plus_actuals_over_budget()
    {
        var (db, projectId, _) = BuildSeededDb();
        var cbsId = Guid.NewGuid();
        db.CostBreakdownItems.Add(new CostBreakdownItem
        {
            Id = cbsId,
            ProjectId = projectId, Code = "1.1", Description = "X",
            Budget = 100_000m,
        });
        db.Commitments.Add(new Commitment
        {
            ProjectId = projectId, Reference = "PO-1",
            Type = CommitmentType.PO, Amount = 70_000m,
            Counterparty = "Sub",
            CostBreakdownItemId = cbsId,
        });
        db.ActualCosts.Add(new ActualCost
        {
            ProjectId = projectId, Reference = "INV-1",
            Amount = 50_000m, Description = "x",
        });
        await db.SaveChangesAsync();

        var pct = await AlertMetricComputer.ComputeAsync(
            db, AlertMetric.CostUtilizationPercent, projectId);
        Assert.Equal(120m, pct); // (70k + 50k) / 100k = 120%
    }

    private static CimsApp.Models.Risk NewRisk(Guid projectId, RiskStatus status) => new()
    {
        ProjectId = projectId,
        Title = "Risk",
        Description = "x",
        Status = status,
        Probability = 3,
        Impact = 3,
    };

    private static (CimsDbContext db, Guid projectId, Guid orgId) BuildSeededDb()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "T", LastName = "U",
                OrganisationId = orgId,
            });
            seed.Projects.Add(new Project
            {
                Id = projectId, Name = "P", Code = "P-1",
                AppointingPartyId = orgId, Currency = "GBP",
                Status = ProjectStatus.Execution,
            });
            seed.SaveChanges();
        }
        return (new CimsDbContext(options, tenant), projectId, orgId);
    }
}

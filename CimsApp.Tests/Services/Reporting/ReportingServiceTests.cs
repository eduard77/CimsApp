using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using RiskEntity = CimsApp.Models.Risk;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Reporting;

/// <summary>
/// Behavioural tests for <see cref="ReportingService"/> (T-S7-03).
/// Covers the Monthly Project Report aggregator. Exercises the
/// default-period rule, each section's aggregation, the period
/// window on Variations / Change Requests, and the cross-tenant
/// 404. PDF rendering is deferred to v1.1 / B-055; this DTO is
/// the surface area v1.0 needs.
/// </summary>
public class ReportingServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId, GlobalRole = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "Test Project", Code = "TP-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
            EndDate = new DateTime(2026, 12, 31),
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    [Fact]
    public async Task GenerateMonthlyProjectReportAsync_defaults_period_to_last_calendar_month()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db);

        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        var now = DateTime.UtcNow;
        var firstOfThisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(firstOfThisMonth,                dto.PeriodEnd);
        Assert.Equal(firstOfThisMonth.AddMonths(-1),  dto.PeriodStart);
        Assert.Equal("Test Project",                  dto.ProjectName);
        Assert.Equal("TP-1",                          dto.ProjectCode);
        Assert.Equal("Execution",                     dto.ExecutiveSummary.ProjectStatus);
        Assert.Equal(new DateTime(2026, 12, 31),      dto.ExecutiveSummary.PlannedEndDate);
    }

    [Fact]
    public async Task Programme_section_computes_percent_complete_and_finish_dates()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Activities.AddRange(
                new Activity
                {
                    ProjectId = projectId, Code = "A1", Name = "Survey",
                    Duration = 5, PercentComplete = 1m,
                    EarlyStart  = new DateTime(2026, 5, 1),
                    EarlyFinish = new DateTime(2026, 5, 6),
                },
                new Activity
                {
                    ProjectId = projectId, Code = "A2", Name = "Foundations",
                    Duration = 10, PercentComplete = 0.5m,
                    EarlyStart  = new DateTime(2026, 5, 6),
                    EarlyFinish = new DateTime(2026, 5, 20),
                },
                new Activity
                {
                    ProjectId = projectId, Code = "A3", Name = "Frame",
                    Duration = 20, PercentComplete = 0m,
                    EarlyStart  = new DateTime(2026, 5, 20),
                    EarlyFinish = new DateTime(2026, 6, 30),
                });
            db.ScheduleBaselines.Add(new ScheduleBaseline
            {
                ProjectId = projectId, Label = "Baseline-0",
                CapturedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        Assert.Equal(3,                              dto.Programme.TotalActivities);
        Assert.Equal(1,                              dto.Programme.CompletedActivities);
        Assert.Equal(50.00m,                         dto.Programme.PercentComplete);
        Assert.Equal(new DateTime(2026, 5, 1),       dto.Programme.EarliestEarlyStart);
        Assert.Equal(new DateTime(2026, 6, 30),      dto.Programme.LatestEarlyFinish);
        Assert.Equal("Baseline-0",                   dto.Programme.LatestBaselineLabel);
        Assert.Equal(new DateTime(2026, 6, 30),      dto.ExecutiveSummary.EstimatedEndDate);
    }

    [Fact]
    public async Task Cost_section_aggregates_budget_committed_actuals_and_percent_spent()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var cbs1 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1.0", Name = "Substructure",
                Budget = 600_000m,
            };
            var cbs2 = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "2.0", Name = "Superstructure",
                Budget = 400_000m,
            };
            db.CostBreakdownItems.AddRange(cbs1, cbs2);
            db.Commitments.Add(new Commitment
            {
                ProjectId = projectId, CostBreakdownItemId = cbs1.Id,
                Type = CommitmentType.PO, Reference = "PO-1",
                Amount = 300_000m,
            });
            var period = new CostPeriod
            {
                ProjectId = projectId, Label = "2026-04",
                StartDate = new DateTime(2026, 4, 1),
                EndDate   = new DateTime(2026, 4, 30),
            };
            db.CostPeriods.Add(period);
            db.ActualCosts.Add(new ActualCost
            {
                ProjectId = projectId, CostBreakdownItemId = cbs1.Id,
                PeriodId = period.Id, Amount = 250_000m,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        Assert.Equal("GBP",      dto.Cost.Currency);
        Assert.Equal(1_000_000m, dto.Cost.TotalBudget);
        Assert.Equal(300_000m,   dto.Cost.TotalCommitted);
        Assert.Equal(250_000m,   dto.Cost.TotalActuals);
        Assert.Equal(25.00m,     dto.Cost.PercentSpent);
    }

    [Fact]
    public async Task Risk_section_buckets_by_PMI_5x5_severity_thresholds()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            // 1 high (Score=20), 2 medium (Score=9 and 12), 1 low
            // (Score=4), 1 closed (excluded entirely).
            db.Risks.AddRange(
                new RiskEntity { ProjectId = projectId, Title = "H",  Probability = 4, Impact = 5, Score = 20, Status = RiskStatus.Active },
                new RiskEntity { ProjectId = projectId, Title = "M1", Probability = 3, Impact = 3, Score = 9,  Status = RiskStatus.Assessed },
                new RiskEntity { ProjectId = projectId, Title = "M2", Probability = 4, Impact = 3, Score = 12, Status = RiskStatus.Active },
                new RiskEntity { ProjectId = projectId, Title = "L",  Probability = 2, Impact = 2, Score = 4,  Status = RiskStatus.Identified },
                new RiskEntity { ProjectId = projectId, Title = "X",  Probability = 5, Impact = 5, Score = 25, Status = RiskStatus.Closed });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        Assert.Equal(4, dto.Risk.OpenTotal);
        Assert.Equal(1, dto.Risk.OpenHighSeverity);
        Assert.Equal(2, dto.Risk.OpenMediumSeverity);
        Assert.Equal(1, dto.Risk.OpenLowSeverity);
        Assert.Equal(4, dto.ExecutiveSummary.OpenRisksCount);
    }

    [Fact]
    public async Task Variations_period_window_filters_correctly_by_CreatedAt_and_DecidedAt()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var inWindow  = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);
        var outOfWindow = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        using (var db = new CimsDbContext(options, tenant))
        {
            // Raised in window.
            db.Variations.Add(new Variation
            {
                ProjectId = projectId, VariationNumber = "VAR-0001",
                Title = "In-window raise", State = VariationState.Raised,
                EstimatedCostImpact = 5_000m, RaisedById = userId,
                CreatedAt = inWindow,
            });
            // Approved in window — its 8000 should land in
            // ApprovedValueInPeriod even though it was raised earlier.
            db.Variations.Add(new Variation
            {
                ProjectId = projectId, VariationNumber = "VAR-0002",
                Title = "In-window approve", State = VariationState.Approved,
                EstimatedCostImpact = 8_000m, RaisedById = userId,
                CreatedAt = outOfWindow, DecidedAt = inWindow,
            });
            // Approved outside window — must be excluded.
            db.Variations.Add(new Variation
            {
                ProjectId = projectId, VariationNumber = "VAR-0003",
                Title = "Out-of-window approve", State = VariationState.Approved,
                EstimatedCostImpact = 99_000m, RaisedById = userId,
                CreatedAt = outOfWindow, DecidedAt = outOfWindow,
            });
            db.ChangeRequests.Add(new ChangeRequest
            {
                ProjectId = projectId, Number = "CR-0001",
                Title = "Add scope", Category = ChangeRequestCategory.Scope,
                State = ChangeRequestState.Approved,
                RaisedById = userId, CreatedAt = inWindow, UpdatedAt = inWindow,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(
            projectId,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1,       dto.Changes.VariationsRaisedInPeriod);
        Assert.Equal(1,       dto.Changes.VariationsApprovedInPeriod);
        Assert.Equal(8_000m,  dto.Changes.VariationsApprovedValueInPeriod);
        Assert.Equal(1,       dto.Changes.ChangeRequestsRaisedInPeriod);
        Assert.Equal(1,       dto.Changes.ChangeRequestsApprovedInPeriod);
    }

    [Fact]
    public async Task Issues_section_counts_open_RFIs_and_actions()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Rfis.AddRange(
                new Rfi { ProjectId = projectId, RfiNumber = "RFI-0001", Subject = "A", Description = "x", Status = RfiStatus.Open,    Priority = Priority.Medium, RaisedById = userId },
                new Rfi { ProjectId = projectId, RfiNumber = "RFI-0002", Subject = "B", Description = "x", Status = RfiStatus.Closed,  Priority = Priority.Medium, RaisedById = userId });
            db.ActionItems.Add(new ActionItem
            {
                ProjectId = projectId, Title = "T", Source = "src", Priority = Priority.Medium,
                Status = ActionStatus.InProgress, CreatedById = userId,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        Assert.Equal(1, dto.Issues.OpenRfis);
        Assert.Equal(1, dto.Issues.OpenActions);
        Assert.Equal(0, dto.Issues.OpenEarlyWarnings);
        Assert.Equal(0, dto.Issues.OpenCompensationEvents);
        Assert.Equal(2, dto.ExecutiveSummary.OpenIssuesCount);
    }

    [Fact]
    public async Task Stakeholders_section_counts_engagements_in_period()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var inWindow    = new DateTime(2026, 4, 15, 9, 0, 0, DateTimeKind.Utc);
        var outOfWindow = new DateTime(2026, 3, 15, 9, 0, 0, DateTimeKind.Utc);
        using (var db = new CimsDbContext(options, tenant))
        {
            var s1 = new Stakeholder
            {
                ProjectId = projectId, Name = "Acme", Power = 4, Interest = 4, Score = 16,
                EngagementApproach = EngagementApproach.ManageClosely, IsActive = true,
            };
            var s2 = new Stakeholder
            {
                ProjectId = projectId, Name = "Inactive", Power = 1, Interest = 1, Score = 1,
                EngagementApproach = EngagementApproach.Monitor, IsActive = false,
            };
            db.Stakeholders.AddRange(s1, s2);
            db.EngagementLogs.AddRange(
                new EngagementLog { ProjectId = projectId, StakeholderId = s1.Id, Type = EngagementType.Meeting, OccurredAt = inWindow,    Summary = "in",  RecordedById = userId },
                new EngagementLog { ProjectId = projectId, StakeholderId = s1.Id, Type = EngagementType.Email,   OccurredAt = outOfWindow, Summary = "out", RecordedById = userId });
            db.CommunicationItems.Add(new CommunicationItem
            {
                ProjectId = projectId, ItemType = "Monthly project report",
                Audience = "Client", Frequency = CommunicationFrequency.Monthly,
                Channel = CommunicationChannel.Email, OwnerId = userId, IsActive = true,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GenerateMonthlyProjectReportAsync(
            projectId,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, dto.Stakeholders.StakeholdersTotal);
        Assert.Equal(1, dto.Stakeholders.EngagementLogsInPeriod);
        Assert.Equal(1, dto.Stakeholders.CommunicationsTotal);
    }

    [Fact]
    public async Task GenerateMonthlyProjectReportAsync_falls_back_to_Project_EndDate_when_no_schedule()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db);

        var dto = await svc.GenerateMonthlyProjectReportAsync(projectId, null, null);

        Assert.Equal(0,                              dto.Programme.TotalActivities);
        Assert.Null(dto.Programme.PercentComplete);
        Assert.Null(dto.Programme.LatestEarlyFinish);
        Assert.Equal(new DateTime(2026, 12, 31),     dto.ExecutiveSummary.EstimatedEndDate);
    }

    [Fact]
    public async Task GenerateMonthlyProjectReportAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherOrgId = Guid.NewGuid();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = otherOrgId, UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, otherTenant);
        var svc = new ReportingService(db);

        await Assert.ThrowsAsync<CimsApp.Core.NotFoundException>(
            () => svc.GenerateMonthlyProjectReportAsync(Guid.NewGuid(), null, null));
    }
}

using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Reporting;

/// <summary>
/// Behavioural tests for the T-S7-04 KPI cards endpoint. The KPI
/// list is the project-level success-criteria dashboard; cards
/// are honest v1.0 proxies where genuine EVM (CPI / SPI) needs
/// a per-line progress signal that arrives in v1.1.
/// </summary>
public class KpiCardsServiceTests
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
    public async Task GetProjectKpiCardsAsync_returns_seven_cards_with_default_placeholders()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db);

        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        Assert.Equal("Test Project", dto.ProjectName);
        Assert.Equal(7, dto.Cards.Count);
        Assert.Equal("0",  dto.Cards.Single(c => c.Name == "Module Activity (Last 30d)").Value);
        Assert.Equal("—",  dto.Cards.Single(c => c.Name == "MPR Period Coverage").Value);
        Assert.Equal("0",  dto.Cards.Single(c => c.Name == "Critical Path Activities").Value);
        Assert.Equal("—",  dto.Cards.Single(c => c.Name == "Cost Spent vs Budget").Value);
        Assert.Equal("—",  dto.Cards.Single(c => c.Name == "Schedule Completion").Value);
        Assert.Equal("—",  dto.Cards.Single(c => c.Name == "RFI Avg Response (Last 30d, days)").Value);
        Assert.Equal("0",  dto.Cards.Single(c => c.Name == "Overdue Actions").Value);
    }

    [Fact]
    public async Task ModuleActivity_counts_only_rows_created_in_last_30_days()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var now = DateTime.UtcNow;
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Rfis.Add(new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-IN", Subject = "in", Description = "x",
                Status = RfiStatus.Open, Priority = Priority.Medium, RaisedById = userId,
                CreatedAt = now.AddDays(-5),
            });
            db.Rfis.Add(new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-OLD", Subject = "old", Description = "x",
                Status = RfiStatus.Closed, Priority = Priority.Medium, RaisedById = userId,
                CreatedAt = now.AddDays(-90),
            });
            db.ActionItems.Add(new ActionItem
            {
                ProjectId = projectId, Title = "T", Source = "src", Priority = Priority.Medium,
                Status = ActionStatus.Open, CreatedById = userId,
                CreatedAt = now.AddDays(-2),
            });
            db.ChangeRequests.Add(new ChangeRequest
            {
                ProjectId = projectId, Number = "CR-IN", Title = "Add",
                Category = ChangeRequestCategory.Scope, State = ChangeRequestState.Raised,
                RaisedById = userId, CreatedAt = now.AddDays(-1),
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        Assert.Equal("3", dto.Cards.Single(c => c.Name == "Module Activity (Last 30d)").Value);
    }

    [Fact]
    public async Task CostSpent_proxies_actuals_over_budget_as_percentage()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var cbs = new CostBreakdownItem
            {
                ProjectId = projectId, Code = "1.0", Name = "Substructure",
                Budget = 200_000m,
            };
            db.CostBreakdownItems.Add(cbs);
            var period = new CostPeriod
            {
                ProjectId = projectId, Label = "P1",
                StartDate = new DateTime(2026, 4, 1), EndDate = new DateTime(2026, 4, 30),
            };
            db.CostPeriods.Add(period);
            db.ActualCosts.Add(new ActualCost
            {
                ProjectId = projectId, CostBreakdownItemId = cbs.Id,
                PeriodId = period.Id, Amount = 60_000m,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        Assert.Equal("30",          dto.Cards.Single(c => c.Name == "Cost Spent vs Budget").Value);
        Assert.Equal("2026-04-30",  dto.Cards.Single(c => c.Name == "MPR Period Coverage").Value);
    }

    [Fact]
    public async Task ScheduleCompletion_proxies_completed_over_total_active_activities()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Activities.AddRange(
                new Activity { ProjectId = projectId, Code = "A1", Name = "x", Duration = 5,  PercentComplete = 1m,    IsActive = true,  IsCritical = true },
                new Activity { ProjectId = projectId, Code = "A2", Name = "y", Duration = 5,  PercentComplete = 0.5m,  IsActive = true,  IsCritical = false },
                new Activity { ProjectId = projectId, Code = "A3", Name = "z", Duration = 5,  PercentComplete = 0m,    IsActive = true,  IsCritical = true },
                new Activity { ProjectId = projectId, Code = "A4", Name = "w", Duration = 5,  PercentComplete = 1m,    IsActive = false, IsCritical = true });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        // 1 of 3 active activities at 100% → 33.33%; 2 critical
        // active activities (A4 excluded for IsActive=false).
        Assert.Equal("33.33", dto.Cards.Single(c => c.Name == "Schedule Completion").Value);
        Assert.Equal("2",     dto.Cards.Single(c => c.Name == "Critical Path Activities").Value);
    }

    [Fact]
    public async Task RfiAvgResponse_averages_closed_RFIs_in_last_30_days()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var now = DateTime.UtcNow;
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Rfis.AddRange(
                // Closed 5 days ago, raised 7 days ago → 2-day response.
                new Rfi
                {
                    ProjectId = projectId, RfiNumber = "RFI-A", Subject = "a", Description = "x",
                    Status = RfiStatus.Closed, Priority = Priority.Medium, RaisedById = userId,
                    CreatedAt = now.AddDays(-7), ClosedAt = now.AddDays(-5),
                },
                // Closed 1 day ago, raised 5 days ago → 4-day response.
                new Rfi
                {
                    ProjectId = projectId, RfiNumber = "RFI-B", Subject = "b", Description = "x",
                    Status = RfiStatus.Closed, Priority = Priority.Medium, RaisedById = userId,
                    CreatedAt = now.AddDays(-5), ClosedAt = now.AddDays(-1),
                },
                // Closed 60 days ago → outside window, ignored.
                new Rfi
                {
                    ProjectId = projectId, RfiNumber = "RFI-OLD", Subject = "old", Description = "x",
                    Status = RfiStatus.Closed, Priority = Priority.Medium, RaisedById = userId,
                    CreatedAt = now.AddDays(-90), ClosedAt = now.AddDays(-60),
                });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        // Avg of 2 and 4 = 3 days.
        Assert.Equal("3", dto.Cards.Single(c => c.Name == "RFI Avg Response (Last 30d, days)").Value);
    }

    [Fact]
    public async Task OverdueActions_counts_only_open_or_in_progress_with_past_due_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var now = DateTime.UtcNow;
        using (var db = new CimsDbContext(options, tenant))
        {
            db.ActionItems.AddRange(
                // Overdue, Open.
                new ActionItem
                {
                    ProjectId = projectId, Title = "1", Source = "x", Priority = Priority.Medium,
                    Status = ActionStatus.Open, CreatedById = userId,
                    DueDate = now.AddDays(-1),
                },
                // Overdue, InProgress.
                new ActionItem
                {
                    ProjectId = projectId, Title = "2", Source = "x", Priority = Priority.Medium,
                    Status = ActionStatus.InProgress, CreatedById = userId,
                    DueDate = now.AddDays(-3),
                },
                // Overdue but Closed → excluded.
                new ActionItem
                {
                    ProjectId = projectId, Title = "3", Source = "x", Priority = Priority.Medium,
                    Status = ActionStatus.Closed, CreatedById = userId,
                    DueDate = now.AddDays(-5),
                },
                // Open, due in future → not overdue.
                new ActionItem
                {
                    ProjectId = projectId, Title = "4", Source = "x", Priority = Priority.Medium,
                    Status = ActionStatus.Open, CreatedById = userId,
                    DueDate = now.AddDays(5),
                },
                // Open, no due date → not overdue.
                new ActionItem
                {
                    ProjectId = projectId, Title = "5", Source = "x", Priority = Priority.Medium,
                    Status = ActionStatus.Open, CreatedById = userId,
                });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ReportingService(db2);
        var dto = await svc.GetProjectKpiCardsAsync(projectId);

        Assert.Equal("2", dto.Cards.Single(c => c.Name == "Overdue Actions").Value);
    }

    [Fact]
    public async Task GetProjectKpiCardsAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, otherTenant);
        var svc = new ReportingService(db);

        await Assert.ThrowsAsync<CimsApp.Core.NotFoundException>(
            () => svc.GetProjectKpiCardsAsync(Guid.NewGuid()));
    }
}

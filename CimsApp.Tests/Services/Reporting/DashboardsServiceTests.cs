using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Reporting;

/// <summary>
/// Behavioural tests for <see cref="DashboardsService"/> (T-S7-02).
/// Covers each of the six per-role dashboards: PM / CM / SM / IM
/// / HSE / Client. Aggregation queries hit ~20 different entity
/// tables across S0..S6; the test fixture seeds a representative
/// project and asserts the cards carry the expected counts.
/// </summary>
public class DashboardsServiceTests
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

    // ── PM Dashboard ────────────────────────────────────────────────

    [Fact]
    public async Task GetPmDashboardAsync_returns_open_counts_across_modules()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            // Seed 2 open RFIs + 1 closed RFI.
            db.Rfis.AddRange(
                new Rfi { ProjectId = projectId, RfiNumber = "RFI-0001", Subject = "A", Description = "x", Status = RfiStatus.Open, Priority = Priority.Medium, RaisedById = userId },
                new Rfi { ProjectId = projectId, RfiNumber = "RFI-0002", Subject = "B", Description = "x", Status = RfiStatus.UnderReview, Priority = Priority.Medium, RaisedById = userId },
                new Rfi { ProjectId = projectId, RfiNumber = "RFI-0003", Subject = "C", Description = "x", Status = RfiStatus.Closed, Priority = Priority.Medium, RaisedById = userId });
            // 1 open Action.
            db.ActionItems.Add(new ActionItem
            {
                ProjectId = projectId, Title = "T", Source = "src", Priority = Priority.Medium,
                Status = ActionStatus.Open, CreatedById = userId,
            });
            // 1 open ChangeRequest.
            db.ChangeRequests.Add(new ChangeRequest
            {
                ProjectId = projectId, Number = "CR-0001", Title = "Add",
                Category = ChangeRequestCategory.Scope, State = ChangeRequestState.Raised,
                RaisedById = userId,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db2);
        var dto = await svc.GetPmDashboardAsync(projectId);

        Assert.Equal("PM",            dto.Role);
        Assert.Equal("Test Project",  dto.ProjectName);
        var rfiCard = dto.Cards.Single(c => c.Name == "Open RFIs");
        Assert.Equal("2",             rfiCard.Value);  // 2 of 3 RFIs open
        Assert.Equal(DashboardCardType.Count, rfiCard.Type);
        Assert.Equal("1",             dto.Cards.Single(c => c.Name == "Open Actions").Value);
        Assert.Equal("1",             dto.Cards.Single(c => c.Name == "Open Change Requests").Value);
        Assert.Equal("0",             dto.Cards.Single(c => c.Name == "Open Early Warnings").Value);
    }

    // ── CM Dashboard ────────────────────────────────────────────────

    [Fact]
    public async Task GetCmDashboardAsync_aggregates_cost_and_variation_data()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var cbs1 = Guid.NewGuid();
            var cbs2 = Guid.NewGuid();
            db.CostBreakdownItems.AddRange(
                new CostBreakdownItem { Id = cbs1, ProjectId = projectId, Code = "1", Name = "Civils", SortOrder = 1, Budget = 500_000m },
                new CostBreakdownItem { Id = cbs2, ProjectId = projectId, Code = "2", Name = "MEP",    SortOrder = 2, Budget = 250_000m });
            // 1 Raised + 2 Approved variations.
            db.Variations.AddRange(
                new Variation { ProjectId = projectId, VariationNumber = "VAR-0001", Title = "V1", State = VariationState.Raised,   RaisedById = userId },
                new Variation { ProjectId = projectId, VariationNumber = "VAR-0002", Title = "V2", State = VariationState.Approved, RaisedById = userId },
                new Variation { ProjectId = projectId, VariationNumber = "VAR-0003", Title = "V3", State = VariationState.Approved, RaisedById = userId });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db2);
        var dto = await svc.GetCmDashboardAsync(projectId);

        Assert.Equal("CM", dto.Role);
        var budgetCard = dto.Cards.Single(c => c.Name == "Total CBS Budget");
        Assert.Equal(DashboardCardType.Currency, budgetCard.Type);
        Assert.Contains("750,000", budgetCard.Value);   // GBP 750,000.00 (sum of 500k + 250k)
        Assert.Contains("GBP",     budgetCard.Value);
        Assert.Equal("1",          dto.Cards.Single(c => c.Name == "Raised Variations").Value);
        Assert.Equal("2",          dto.Cards.Single(c => c.Name == "Approved Variations").Value);
    }

    // ── SM Dashboard ────────────────────────────────────────────────

    [Fact]
    public async Task GetSmDashboardAsync_computes_latest_PPC_on_read()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var actId = Guid.NewGuid();
            var wwpId = Guid.NewGuid();
            db.Activities.Add(new Activity
            {
                Id = actId, ProjectId = projectId, Code = "A1", Name = "Act",
                Duration = 5m, IsActive = true,
            });
            db.WeeklyWorkPlans.Add(new WeeklyWorkPlan
            {
                Id = wwpId, ProjectId = projectId,
                WeekStarting = new DateTime(2026, 6, 1),
                CreatedById = userId,
            });
            // 4 commitments: 3 completed → PPC = 75%.
            for (var i = 0; i < 4; i++)
            {
                db.WeeklyTaskCommitments.Add(new WeeklyTaskCommitment
                {
                    WeeklyWorkPlanId = wwpId,
                    ProjectId = projectId,
                    ActivityId = actId,
                    Committed = true,
                    Completed = i < 3,
                    Reason = i < 3 ? null : LpsReasonForNonCompletion.WeatherImpact,
                });
            }
            db.LookaheadEntries.Add(new LookaheadEntry
            {
                ProjectId = projectId, ActivityId = actId,
                WeekStarting = new DateTime(2026, 6, 8),
                IsActive = true, CreatedById = userId,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db2);
        var dto = await svc.GetSmDashboardAsync(projectId);

        Assert.Equal("SM",             dto.Role);
        Assert.Equal("1",              dto.Cards.Single(c => c.Name == "Active Lookaheads").Value);
        Assert.Equal("2026-06-01",     dto.Cards.Single(c => c.Name == "Latest WWP").Value);
        Assert.Equal("75",             dto.Cards.Single(c => c.Name == "Latest WWP PPC").Value);
        Assert.Equal(DashboardCardType.Percentage, dto.Cards.Single(c => c.Name == "Latest WWP PPC").Type);
    }

    [Fact]
    public async Task GetSmDashboardAsync_handles_no_WWP_with_dash_placeholder()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db);
        var dto = await svc.GetSmDashboardAsync(projectId);

        Assert.Equal("—", dto.Cards.Single(c => c.Name == "Latest WWP").Value);
        Assert.Equal("—", dto.Cards.Single(c => c.Name == "Latest WWP PPC").Value);
    }

    // ── IM Dashboard ────────────────────────────────────────────────

    [Fact]
    public async Task GetImDashboardAsync_groups_documents_by_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Documents.AddRange(
                NewDoc(projectId, userId, "DOC-0001", CdeState.WorkInProgress),
                NewDoc(projectId, userId, "DOC-0002", CdeState.WorkInProgress),
                NewDoc(projectId, userId, "DOC-0003", CdeState.Shared),
                NewDoc(projectId, userId, "DOC-0004", CdeState.Published));
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db2);
        var dto = await svc.GetImDashboardAsync(projectId);

        Assert.Equal("IM", dto.Role);
        Assert.Equal("2",  dto.Cards.Single(c => c.Name == "Docs - Work in Progress").Value);
        Assert.Equal("1",  dto.Cards.Single(c => c.Name == "Docs - Shared").Value);
        Assert.Equal("1",  dto.Cards.Single(c => c.Name == "Docs - Published").Value);
        Assert.Equal("0",  dto.Cards.Single(c => c.Name == "Docs - Archived").Value);
    }

    private static Document NewDoc(Guid projectId, Guid userId, string number, CdeState state) =>
        new Document
        {
            ProjectId = projectId, DocumentNumber = number,
            Title = $"Doc {number}",
            ProjectCode = "TP-1", Originator = "ABC", DocType = "RP", Number = "0001",
            CurrentState = state, CreatorId = userId,
        };

    // ── HSE Dashboard ───────────────────────────────────────────────

    [Fact]
    public async Task GetHseDashboardAsync_returns_sparse_S12_placeholder()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db);
        var dto = await svc.GetHseDashboardAsync(projectId);

        Assert.Equal("HSE", dto.Role);
        Assert.Single(dto.Cards);
        var card = dto.Cards[0];
        Assert.Equal("HSE Module",    card.Name);
        Assert.Equal("Coming in S12", card.Value);
        Assert.Contains("B-059",      card.Subtitle);
    }

    // ── Client Dashboard ────────────────────────────────────────────

    [Fact]
    public async Task GetClientDashboardAsync_uses_max_EarlyFinish_for_estimate()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        var ef1 = new DateTime(2026, 8, 15);
        var ef2 = new DateTime(2026, 9, 30);
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Activities.AddRange(
                new Activity { ProjectId = projectId, Code = "A1", Name = "X", Duration = 1m, IsActive = true, EarlyFinish = ef1 },
                new Activity { ProjectId = projectId, Code = "A2", Name = "Y", Duration = 1m, IsActive = true, EarlyFinish = ef2 });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db2);
        var dto = await svc.GetClientDashboardAsync(projectId);

        Assert.Equal("Client",        dto.Role);
        Assert.Equal("Execution",     dto.Cards.Single(c => c.Name == "Project Status").Value);
        Assert.Equal("2026-09-30",    dto.Cards.Single(c => c.Name == "Estimated Finish").Value);
    }

    [Fact]
    public async Task GetClientDashboardAsync_falls_back_to_Project_EndDate_when_no_schedule()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        // No activities seeded — should fall back to Project.EndDate
        // (set to 2026-12-31 in BuildFixture).
        using var db = new CimsDbContext(options, tenant);
        var svc = new DashboardsService(db);
        var dto = await svc.GetClientDashboardAsync(projectId);
        Assert.Equal("2026-12-31", dto.Cards.Single(c => c.Name == "Estimated Finish").Value);
    }

    // ── Cross-tenant ────────────────────────────────────────────────

    [Fact]
    public async Task GetPmDashboardAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        using var db = new CimsDbContext(options, attacker);
        var svc = new DashboardsService(db);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetPmDashboardAsync(projectId));
    }
}

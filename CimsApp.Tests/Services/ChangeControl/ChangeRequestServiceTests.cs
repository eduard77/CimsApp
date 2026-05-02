using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.ChangeControl;

/// <summary>
/// Behavioural tests for <see cref="ChangeRequestService"/> (T-S5-04).
/// Covers the 6 transitions (Raise / Assess / Approve / Reject /
/// Implement / Close), role-gate enforcement on each, the
/// auto-numbering CR-NNNN pattern, audit-twin emission, the
/// CreateVariation side-effect (T-S5-06 lite — full T-S5-06
/// integration tests live separately), and cross-tenant 404 via
/// the query filter.
/// </summary>
public class ChangeRequestServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId,
            UserId         = userId,
            GlobalRole     = UserRole.OrgAdmin,
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
            Id = projectId, Name = "Project", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static RaiseChangeRequestRequest BasicRaise(string title = "Add basement parking") =>
        new(Title: title, Description: "Client variation request",
            Category: ChangeRequestCategory.Scope,
            BsaCategory: BsaHrbCategory.NotApplicable,
            ProgrammeImpactSummary: null, CostImpactSummary: null,
            EstimatedCostImpact: null, EstimatedTimeImpactDays: null);

    // ── Raise ───────────────────────────────────────────────────────

    [Fact]
    public async Task RaiseAsync_persists_with_auto_number_and_initial_state()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.Equal("CR-0001",                       c.Number);
        Assert.Equal(ChangeRequestState.Raised,       c.State);
        Assert.Equal(userId,                          c.RaisedById);
        Assert.Equal(ChangeRequestCategory.Scope,     c.Category);
    }

    [Fact]
    public async Task RaiseAsync_auto_numbers_sequentially()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ChangeRequestService(db, new AuditService(db));
        await svc.RaiseAsync(projectId, BasicRaise("First"),  userId);
        await svc.RaiseAsync(projectId, BasicRaise("Second"), userId);
        var third = await svc.RaiseAsync(projectId, BasicRaise("Third"), userId);
        Assert.Equal("CR-0003", third.Number);
    }

    [Fact]
    public async Task RaiseAsync_rejects_empty_title()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ChangeRequestService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RaiseAsync(projectId, BasicRaise() with { Title = "  " }, userId));
    }

    [Fact]
    public async Task RaiseAsync_emits_change_request_raised_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            await svc.RaiseAsync(projectId,
                BasicRaise() with { BsaCategory = BsaHrbCategory.A, EstimatedCostImpact = 50000m },
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "change_request.raised");
        Assert.Equal("ChangeRequest",                row.Entity);
        Assert.Contains("\"number\":\"CR-0001\"",    row.Detail!);
        Assert.Contains("\"category\":\"Scope\"",    row.Detail);
        Assert.Contains("\"bsaCategory\":\"A\"",     row.Detail);
        Assert.Contains("\"costImpact\":50000",      row.Detail);
    }

    // ── Assess ──────────────────────────────────────────────────────

    [Fact]
    public async Task AssessAsync_transitions_Raised_to_Assessed_and_records_assessor()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest(
                    AssessmentNote: "+10 days, +£50k",
                    ProgrammeImpactSummary: "Slips foundations by 10 days",
                    CostImpactSummary: "£50k additional excavation",
                    EstimatedCostImpact: 50000m,
                    EstimatedTimeImpactDays: 10,
                    BsaCategory: BsaHrbCategory.B),
                userId, UserRole.InformationManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ChangeRequestState.Assessed, c.State);
        Assert.Equal(userId,                      c.AssessedById);
        Assert.Equal("+10 days, +£50k",           c.AssessmentNote);
        Assert.Equal(BsaHrbCategory.B,            c.BsaCategory);   // updated by assess
        Assert.Equal(50000m,                      c.EstimatedCostImpact);
    }

    [Fact]
    public async Task AssessAsync_rejects_TaskTeamMember_role()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc2.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("note", null, null, null, null, null),
                userId, UserRole.TaskTeamMember));
    }

    [Fact]
    public async Task AssessAsync_rejects_empty_assessment_note()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("", null, null, null, null, null),
                userId, UserRole.InformationManager));
    }

    // ── Approve ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_transitions_Assessed_to_Approved_and_records_decision()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("Done", null, null, null, null, null),
                userId, UserRole.InformationManager);
            await svc.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest(
                    DecisionNote: "Client agreed cost + time impact",
                    CreateVariation: false),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ChangeRequestState.Approved,            c.State);
        Assert.Equal(userId,                                  c.DecisionById);
        Assert.Equal("Client agreed cost + time impact",      c.DecisionNote);
        Assert.Null(c.GeneratedVariationId);
    }

    [Fact]
    public async Task ApproveAsync_with_CreateVariation_spawns_Variation_and_links_FK()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId,
                BasicRaise() with { EstimatedCostImpact = 50000m, EstimatedTimeImpactDays = 10 },
                userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("Done", null, null, null, null, null),
                userId, UserRole.InformationManager);
            await svc.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("OK", CreateVariation: true),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.NotNull(c.GeneratedVariationId);
        var v = await verify.Variations.SingleAsync(x => x.Id == c.GeneratedVariationId);
        Assert.Equal("VAR-0001",                v.VariationNumber);
        Assert.Contains("CR-0001",              v.Title);
        Assert.Equal(50000m,                    v.EstimatedCostImpact);
        Assert.Equal(VariationState.Raised,     v.State);

        // Both audit events landed.
        var audits = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action.StartsWith("change_request.")).ToListAsync();
        Assert.Contains(audits, a => a.Action == "change_request.variation_created");
        Assert.Contains(audits, a => a.Action == "change_request.approved");
    }

    [Fact]
    public async Task ApproveAsync_rejects_skipping_Assessed()
    {
        // Raised → Approved direct: not allowed in v1.0 (no expedited path).
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("note", false),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task ApproveAsync_rejects_InformationManager_role()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("note", null, null, null, null, null),
                userId, UserRole.InformationManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc2.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("note", false),
                userId, UserRole.InformationManager));
    }

    // ── Reject ──────────────────────────────────────────────────────

    [Fact]
    public async Task RejectAsync_from_Raised_succeeds()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.RejectAsync(projectId, id,
                new RejectChangeRequestRequest("Out of scope per contract"),
                userId, UserRole.ProjectManager);
        }
        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ChangeRequestState.Rejected,    c.State);
        Assert.Equal("Out of scope per contract",    c.DecisionNote);
    }

    [Fact]
    public async Task RejectAsync_after_Approved_rejected_with_conflict()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("note", null, null, null, null, null),
                userId, UserRole.InformationManager);
            await svc.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("ok", false),
                userId, UserRole.ProjectManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.RejectAsync(projectId, id,
                new RejectChangeRequestRequest("changed mind"),
                userId, UserRole.ProjectManager));
    }

    [Fact]
    public async Task RejectAsync_rejects_empty_decision_note()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.RejectAsync(projectId, id,
                new RejectChangeRequestRequest(""),
                userId, UserRole.ProjectManager));
    }

    // ── Implement / Close ───────────────────────────────────────────

    [Fact]
    public async Task ImplementAsync_then_CloseAsync_completes_workflow()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("note", null, null, null, null, null),
                userId, UserRole.InformationManager);
            await svc.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("ok", false),
                userId, UserRole.ProjectManager);
            await svc.ImplementAsync(projectId, id,
                new ImplementChangeRequestRequest("Done by site team"),
                userId, UserRole.ProjectManager);
            await svc.CloseAsync(projectId, id,
                new CloseChangeRequestRequest("Verified"),
                userId, UserRole.ProjectManager);
        }

        using var verify = new CimsDbContext(options, tenant);
        var c = await verify.ChangeRequests.SingleAsync(x => x.Id == id);
        Assert.Equal(ChangeRequestState.Closed, c.State);
        Assert.NotNull(c.ImplementedAt);
        Assert.NotNull(c.ClosedAt);

        // All 5 transition audit events present.
        var audits = await verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action.StartsWith("change_request.")).ToListAsync();
        Assert.Contains(audits, a => a.Action == "change_request.raised");
        Assert.Contains(audits, a => a.Action == "change_request.assessed");
        Assert.Contains(audits, a => a.Action == "change_request.approved");
        Assert.Contains(audits, a => a.Action == "change_request.implemented");
        Assert.Contains(audits, a => a.Action == "change_request.closed");
    }

    [Fact]
    public async Task CloseAsync_skipping_Implement_rejected()
    {
        // Approved → Closed direct: not allowed; must Implement first.
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
            await svc.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("note", null, null, null, null, null),
                userId, UserRole.InformationManager);
            await svc.ApproveAsync(projectId, id,
                new ApproveChangeRequestRequest("ok", false),
                userId, UserRole.ProjectManager);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CloseAsync(projectId, id,
                new CloseChangeRequestRequest(null),
                userId, UserRole.ProjectManager));
    }

    // ── Listing + filtering ─────────────────────────────────────────

    [Fact]
    public async Task ListAsync_filters_by_state_and_category()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            await svc.RaiseAsync(projectId, BasicRaise("Scope #1") with { Category = ChangeRequestCategory.Scope }, userId);
            await svc.RaiseAsync(projectId, BasicRaise("Cost #1")  with { Category = ChangeRequestCategory.Cost  }, userId);
            await svc.RaiseAsync(projectId, BasicRaise("Cost #2")  with { Category = ChangeRequestCategory.Cost  }, userId);
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        var allCost = await svc2.ListAsync(projectId, null, ChangeRequestCategory.Cost);
        Assert.Equal(2, allCost.Count);
        var allRaised = await svc2.ListAsync(projectId, ChangeRequestState.Raised, null);
        Assert.Equal(3, allRaised.Count);
    }

    // ── Cross-tenant ────────────────────────────────────────────────

    [Fact]
    public async Task AssessAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ChangeRequestService(db, new AuditService(db));
            id = (await svc.RaiseAsync(projectId, BasicRaise(), userId)).Id;
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new ChangeRequestService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.AssessAsync(projectId, id,
                new AssessChangeRequestRequest("pwn", null, null, null, null, null),
                attacker.UserId!.Value, UserRole.InformationManager));
    }
}

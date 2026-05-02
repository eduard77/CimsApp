using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CimsApp.Tests.Services.Gdpr;

/// <summary>
/// Behavioural tests for the S11 UK GDPR module: ROPA, DPIA,
/// SAR, Data Breach, Retention Schedule services. Org-scoped
/// services derive OrganisationId from ITenantContext (4 of 5
/// services); DPIA is project-scoped via ProjectId param.
/// // GDPR ref: Arts 5(1)(e), 12, 15, 30, 33, 34, 35.
/// </summary>
public class GdprServicesTests
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
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
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
            Id = projectId, Name = "P", Code = "TP-1",
            AppointingPartyId = orgId, Currency = "GBP",
            Status = ProjectStatus.Execution,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    // ── ROPA ────────────────────────────────────────────────────────

    [Fact]
    public async Task RopaService_CreateAsync_persists_with_lawful_basis()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new RopaService(db, new AuditService(db), tenant);

        var dto = await svc.CreateAsync(
            new CreateRopaEntryRequest(
                "Process project member roles",
                LawfulBasis.LegitimateInterest,
                "name,email,role", "Other project members",
                "Duration of project + 1 year", "Encrypted at rest"),
            userId, null, null);

        Assert.Equal(LawfulBasis.LegitimateInterest, dto.LawfulBasis);
        Assert.Equal("name,email,role",              dto.DataCategoriesCsv);
    }

    [Fact]
    public async Task RopaService_UpdateAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new RopaService(db, new AuditService(db), tenant);
        var created = await svc.CreateAsync(
            new CreateRopaEntryRequest("X", LawfulBasis.Consent, null, null, null, null),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(created.Id,
                new UpdateRopaEntryRequest(null, null, null, null, null, null),
                userId, null, null));
    }

    // ── DPIA ────────────────────────────────────────────────────────

    [Fact]
    public void DpiaWorkflow_supports_full_state_set()
    {
        Assert.True(DpiaWorkflow.IsValidTransition(
            DpiaState.Drafting, DpiaState.UnderReview));
        Assert.True(DpiaWorkflow.IsValidTransition(
            DpiaState.UnderReview, DpiaState.Approved));
        Assert.True(DpiaWorkflow.IsValidTransition(
            DpiaState.UnderReview, DpiaState.RequiresChanges));
        Assert.True(DpiaWorkflow.IsValidTransition(
            DpiaState.RequiresChanges, DpiaState.Drafting));
        // Approved is terminal.
        Assert.True(DpiaWorkflow.IsTerminal(DpiaState.Approved));
        // Cannot skip UnderReview.
        Assert.False(DpiaWorkflow.IsValidTransition(
            DpiaState.Drafting, DpiaState.Approved));
    }

    [Fact]
    public async Task DpiaService_TransitionAsync_full_happy_path()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DpiaService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateDpiaRequest("Site CCTV processing",
                "CCTV recording on construction site",
                "Continuous filming may capture passers-by",
                "Signage; 30-day retention"),
            userId, null, null);

        var underReview = await svc.TransitionAsync(projectId, dto.Id,
            DpiaState.UnderReview, decisionNote: null,
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Equal(DpiaState.UnderReview, underReview.State);

        var approved = await svc.TransitionAsync(projectId, dto.Id,
            DpiaState.Approved, "Risk acceptable with mitigations",
            userId, UserRole.ProjectManager, null, null);
        Assert.Equal(DpiaState.Approved,           approved.State);
        Assert.Equal(userId,                       approved.ReviewedById);
        Assert.NotNull(approved.ReviewedAt);
        Assert.Contains("acceptable",              approved.DecisionNote);
    }

    [Fact]
    public async Task DpiaService_Approve_requires_decision_note()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DpiaService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateDpiaRequest("X", "y", null, null), userId, null, null);
        await svc.TransitionAsync(projectId, dto.Id, DpiaState.UnderReview, null,
            userId, UserRole.TaskTeamMember, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.TransitionAsync(projectId, dto.Id, DpiaState.Approved, "",
                userId, UserRole.ProjectManager, null, null));
    }

    [Fact]
    public async Task DpiaService_RequiresChanges_can_return_to_Drafting()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DpiaService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateDpiaRequest("X", "y", null, null), userId, null, null);
        await svc.TransitionAsync(projectId, dto.Id, DpiaState.UnderReview, null,
            userId, UserRole.TaskTeamMember, null, null);
        await svc.TransitionAsync(projectId, dto.Id, DpiaState.RequiresChanges,
            "Need more on retention", userId, UserRole.ProjectManager, null, null);

        var redrafted = await svc.TransitionAsync(projectId, dto.Id,
            DpiaState.Drafting, decisionNote: null,
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Equal(DpiaState.Drafting, redrafted.State);
    }

    [Fact]
    public async Task DpiaService_UpdateAsync_rejects_edits_to_Approved_DPIA()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DpiaService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateDpiaRequest("X", "y", null, null), userId, null, null);
        await svc.TransitionAsync(projectId, dto.Id, DpiaState.UnderReview, null,
            userId, UserRole.TaskTeamMember, null, null);
        await svc.TransitionAsync(projectId, dto.Id, DpiaState.Approved, "ok",
            userId, UserRole.ProjectManager, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateAsync(projectId, dto.Id,
                new UpdateDpiaRequest("Edited", null, null, null),
                userId, null, null));
    }

    // ── SAR ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SarService_CreateAsync_computes_30_day_due_date()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new SarService(db, new AuditService(db), tenant);
        var requested = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

        var dto = await svc.CreateAsync(
            new CreateSarRequest("Jane Subject", "jane@example.com",
                "Please send all my data", requested),
            userId, null, null);

        Assert.Equal(SarState.Received,                            dto.State);
        Assert.Equal(requested,                                    dto.RequestedAt);
        Assert.Equal(requested.AddDays(30),                        dto.DueAt);
        Assert.Equal("SAR-0001",                                   dto.Number);
    }

    [Fact]
    public async Task SarService_full_workflow_Received_to_Fulfilled()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new SarService(db, new AuditService(db), tenant);
        var dto = await svc.CreateAsync(
            new CreateSarRequest("X", null, "y", null), userId, null, null);
        await svc.StartFulfilmentAsync(dto.Id, new StartSarFulfilmentRequest(null), userId, null, null);
        var fulfilled = await svc.FulfilAsync(dto.Id,
            new FulfilSarRequest("Sent encrypted ZIP via secure portal"),
            userId, null, null);

        Assert.Equal(SarState.Fulfilled,           fulfilled.State);
        Assert.Equal(userId,                       fulfilled.FulfilledById);
        Assert.NotNull(fulfilled.FulfilledAt);
    }

    [Fact]
    public async Task SarService_RefuseAsync_requires_reason()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new SarService(db, new AuditService(db), tenant);
        var dto = await svc.CreateAsync(
            new CreateSarRequest("X", null, "y", null), userId, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RefuseAsync(dto.Id, new RefuseSarRequest(""),
                userId, null, null));
    }

    // ── Data Breach Log ─────────────────────────────────────────────

    [Fact]
    public async Task DataBreachService_CreateAsync_assigns_BR_number()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DataBreachService(db, new AuditService(db), tenant);

        var dto = await svc.CreateAsync(
            new CreateBreachRequest(
                "Lost laptop", "Engineer's laptop stolen from car",
                BreachSeverity.High,
                OccurredAt: new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc),
                DiscoveredAt: new DateTime(2026, 7, 2, 9, 0, 0, DateTimeKind.Utc),
                "name,address,project_data", 12),
            userId, null, null);

        Assert.Equal("BR-0001",                dto.Number);
        Assert.False(dto.ReportedToIco);
        Assert.False(dto.NotifiedDataSubjects);
    }

    [Fact]
    public async Task DataBreachService_MarkReportedToIcoAsync_records_reference()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DataBreachService(db, new AuditService(db), tenant);
        var dto = await svc.CreateAsync(new CreateBreachRequest(
            "X", "y", BreachSeverity.Medium,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow,
            null, null), userId, null, null);

        var reported = await svc.MarkReportedToIcoAsync(dto.Id,
            new MarkBreachReportedToIcoRequest("ICO-CASE-2026-0042"),
            userId, null, null);

        Assert.True(reported.ReportedToIco);
        Assert.Equal("ICO-CASE-2026-0042", reported.IcoReference);
    }

    [Fact]
    public async Task DataBreachService_MarkSubjectsNotified_rejects_already_notified()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new DataBreachService(db, new AuditService(db), tenant);
        var dto = await svc.CreateAsync(new CreateBreachRequest(
            "X", "y", BreachSeverity.Critical,
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow,
            null, null), userId, null, null);
        await svc.MarkNotifiedDataSubjectsAsync(dto.Id, userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.MarkNotifiedDataSubjectsAsync(dto.Id, userId, null, null));
    }

    // ── Retention Schedule ─────────────────────────────────────────

    [Fact]
    public async Task RetentionScheduleService_CreateAsync_rejects_duplicate_category()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new RetentionScheduleService(db, new AuditService(db), tenant);
        await svc.CreateAsync(new CreateRetentionScheduleRequest(
            "Project documents", 84,
            "Construction Act 1996 + 6-year limitation period", null),
            userId, null, null);

        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CreateAsync(new CreateRetentionScheduleRequest(
                "Project documents", 60, "Other basis", null),
                userId, null, null));
    }

    [Fact]
    public async Task RetentionScheduleService_DeleteAsync_frees_category_for_reuse()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new RetentionScheduleService(db, new AuditService(db), tenant);
        var first = await svc.CreateAsync(new CreateRetentionScheduleRequest(
            "Audit logs", 36, "Internal audit", null),
            userId, null, null);
        await svc.DeleteAsync(first.Id, userId, null, null);

        // Re-creating with the same category should succeed since
        // the unique index is filtered on IsActive.
        var second = await svc.CreateAsync(new CreateRetentionScheduleRequest(
            "Audit logs", 60, "Updated basis", null),
            userId, null, null);
        Assert.Equal(60, second.RetentionPeriodMonths);
    }

    [Fact]
    public async Task RetentionScheduleService_CreateAsync_rejects_zero_months()
    {
        var (options, tenant, _, userId, _) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new RetentionScheduleService(db, new AuditService(db), tenant);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(new CreateRetentionScheduleRequest(
                "X", 0, "Y", null), userId, null, null));
    }
}

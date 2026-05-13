using GeneraPm.Core;
using GeneraPm.Data;
using GeneraPm.Models;
using GeneraPm.Services.Audit;
using GeneraPm.Services.SiteUserWorkflows;
using GeneraPm.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace GeneraPm.Tests.Services.SiteUserWorkflows;

/// <summary>
/// T-S20-05 site-user workflow tests. Per-service positive paths,
/// uniqueness/validation guards, NCR state-machine transitions
/// (valid + invalid), audit-twin pinning.
/// </summary>
public class SiteUserServicesTests
{
    // ── DailyDiaryService ─────────────────────────────────────────

    [Fact]
    public async Task DailyDiaryCreate_writes_dailydiary_created_audit_event()
    {
        var f = BuildFixture();
        var svc = new DailyDiaryService(f.Db, new AuditService(f.Db));
        await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(
                DateOnly.FromDateTime(DateTime.UtcNow),
                Weather: "Clear", Manpower: null, Plant: null,
                Deliveries: null, Incidents: null, ProgressNotes: "Day 1"),
            f.ActorId);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "dailydiary.created").ToListAsync();
        Assert.Single(audits);
    }

    [Fact]
    public async Task DailyDiaryCreate_rejects_same_user_same_date_twice()
    {
        var f = BuildFixture();
        var svc = new DailyDiaryService(f.Db, new AuditService(f.Db));
        var date = DateOnly.FromDateTime(DateTime.UtcNow);
        await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(date, "Sun", null, null, null, null, "first"),
            f.ActorId);
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CreateAsync(f.ProjectId,
                new CreateDailyDiaryRequest(date, "Rain", null, null, null, null, "second"),
                f.ActorId));
    }

    [Fact]
    public async Task DailyDiaryList_filters_by_date_range()
    {
        var f = BuildFixture();
        var svc = new DailyDiaryService(f.Db, new AuditService(f.Db));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(today.AddDays(-5), "x", null, null, null, null, null),
            f.ActorId);
        await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(today.AddDays(-1), "x", null, null, null, null, null),
            f.ActorId);
        await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(today, "x", null, null, null, null, null),
            f.ActorId);

        var page = await svc.ListAsync(f.ProjectId, from: today.AddDays(-2));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task DailyDiaryUpdate_writes_dailydiary_updated_audit_event()
    {
        var f = BuildFixture();
        var svc = new DailyDiaryService(f.Db, new AuditService(f.Db));
        var row = await svc.CreateAsync(f.ProjectId,
            new CreateDailyDiaryRequest(
                DateOnly.FromDateTime(DateTime.UtcNow),
                "x", null, null, null, null, null),
            f.ActorId);
        await svc.UpdateAsync(row.Id,
            new UpdateDailyDiaryRequest("y", null, null, null, null, "more notes"),
            f.ActorId);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "dailydiary.updated").ToListAsync();
        Assert.Single(audits);
        var refreshed = await f.Db.DailyDiaryEntries.FirstAsync(e => e.Id == row.Id);
        Assert.Equal("y", refreshed.Weather);
    }

    // ── NcrService ─────────────────────────────────────────────────

    [Fact]
    public async Task NcrCreate_assigns_sequential_number_and_writes_audit()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n1 = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("First", "desc", NcrSeverity.Medium, null, null),
            f.ActorId);
        var n2 = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("Second", "desc", NcrSeverity.High, null, null),
            f.ActorId);
        Assert.Equal("NCR-0001", n1.Number);
        Assert.Equal("NCR-0002", n2.Number);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "ncr.created").ToListAsync();
        Assert.Equal(2, audits.Count);
    }

    [Fact]
    public async Task NcrCreate_rejects_empty_title_or_description()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(f.ProjectId,
                new CreateNcrRequest("", "desc", NcrSeverity.Low, null, null),
                f.ActorId));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateAsync(f.ProjectId,
                new CreateNcrRequest("title", "  ", NcrSeverity.Low, null, null),
                f.ActorId));
    }

    [Fact]
    public async Task NcrAssign_writes_ncr_assigned_event_and_sets_status()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("t", "d", NcrSeverity.Medium, null, null),
            f.ActorId);
        // Need an active ProjectMember to assign to.
        f.Db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = f.ProjectId, UserId = f.OtherMemberId,
            Role = UserRole.TaskTeamMember, IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await svc.AssignAsync(n.Id, new AssignNcrRequest(f.OtherMemberId),
            UserRole.InformationManager, f.ActorId);
        var refreshed = await f.Db.Ncrs.FirstAsync(x => x.Id == n.Id);
        Assert.Equal(NcrState.Assigned, refreshed.Status);
        Assert.Equal(f.OtherMemberId, refreshed.AssignedToId);
        var audits = await f.Db.AuditLogs.Where(a => a.Action == "ncr.assigned").ToListAsync();
        Assert.Single(audits);
    }

    [Fact]
    public async Task NcrAssign_rejects_non_member_assignee()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("t", "d", NcrSeverity.Medium, null, null),
            f.ActorId);
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AssignAsync(n.Id, new AssignNcrRequest(Guid.NewGuid()),
                UserRole.InformationManager, f.ActorId));
    }

    [Fact]
    public async Task NcrAssign_rejects_TaskTeamMember_role()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("t", "d", NcrSeverity.Medium, null, null),
            f.ActorId);
        f.Db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = f.ProjectId, UserId = f.OtherMemberId,
            Role = UserRole.TaskTeamMember, IsActive = true,
        });
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.AssignAsync(n.Id, new AssignNcrRequest(f.OtherMemberId),
                UserRole.TaskTeamMember, f.ActorId));
    }

    [Fact]
    public async Task NcrTransition_rejects_invalid_transition()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("t", "d", NcrSeverity.Medium, null, null),
            f.ActorId);
        // Raised → Closed is invalid (must go via Assigned, InProgress,
        // Resolved per NcrWorkflow).
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.TransitionAsync(n.Id, new TransitionNcrRequest(NcrState.Closed, null),
                UserRole.OrgAdmin, f.ActorId));
    }

    [Fact]
    public async Task NcrTransition_writes_audit_and_updates_timestamps()
    {
        var f = BuildFixture();
        var svc = new NcrService(f.Db, new AuditService(f.Db));
        var n = await svc.CreateAsync(f.ProjectId,
            new CreateNcrRequest("t", "d", NcrSeverity.Medium, null, null),
            f.ActorId);
        f.Db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = f.ProjectId, UserId = f.OtherMemberId,
            Role = UserRole.TaskTeamMember, IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await svc.AssignAsync(n.Id, new AssignNcrRequest(f.OtherMemberId),
            UserRole.InformationManager, f.ActorId);
        await svc.TransitionAsync(n.Id, new TransitionNcrRequest(NcrState.InProgress, null),
            UserRole.TaskTeamMember, f.ActorId);
        await svc.TransitionAsync(n.Id, new TransitionNcrRequest(NcrState.Resolved, "fixed"),
            UserRole.TaskTeamMember, f.ActorId);
        await svc.TransitionAsync(n.Id, new TransitionNcrRequest(NcrState.Closed, null),
            UserRole.InformationManager, f.ActorId);

        var refreshed = await f.Db.Ncrs.FirstAsync(x => x.Id == n.Id);
        Assert.Equal(NcrState.Closed, refreshed.Status);
        Assert.NotNull(refreshed.AssignedAt);
        Assert.NotNull(refreshed.ResolvedAt);
        Assert.NotNull(refreshed.ClosedAt);
        Assert.Equal("fixed", refreshed.ResolutionNotes);

        var transitions = await f.Db.AuditLogs.Where(a => a.Action == "ncr.transitioned").CountAsync();
        Assert.Equal(3, transitions);
    }

    // ── NcrWorkflow (pure function) ───────────────────────────────

    [Fact]
    public void NcrWorkflow_terminal_states_have_no_transitions()
    {
        Assert.True(NcrWorkflow.IsTerminal(NcrState.Closed));
        Assert.True(NcrWorkflow.IsTerminal(NcrState.Cancelled));
        Assert.False(NcrWorkflow.IsTerminal(NcrState.Raised));
    }

    [Fact]
    public void NcrWorkflow_cancellation_only_from_raised_or_assigned()
    {
        Assert.True(NcrWorkflow.IsValidTransition(NcrState.Raised, NcrState.Cancelled));
        Assert.True(NcrWorkflow.IsValidTransition(NcrState.Assigned, NcrState.Cancelled));
        Assert.False(NcrWorkflow.IsValidTransition(NcrState.InProgress, NcrState.Cancelled));
        Assert.False(NcrWorkflow.IsValidTransition(NcrState.Resolved, NcrState.Cancelled));
    }

    [Fact]
    public void NcrWorkflow_close_requires_IM_or_above()
    {
        Assert.True(NcrWorkflow.CanTransition(NcrState.Resolved, NcrState.Closed,
            UserRole.InformationManager));
        Assert.True(NcrWorkflow.CanTransition(NcrState.Resolved, NcrState.Closed,
            UserRole.OrgAdmin));
        Assert.False(NcrWorkflow.CanTransition(NcrState.Resolved, NcrState.Closed,
            UserRole.TaskTeamMember));
    }

    // ── Fixture ────────────────────────────────────────────────────

    private sealed record Fixture(
        Guid OrgId, Guid ProjectId, Guid ActorId, Guid OtherMemberId,
        StubTenantContext Tenant, GeneraPmDbContext Db);

    private static Fixture BuildFixture()
    {
        var orgId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = actorId,
            GlobalRole = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<GeneraPmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .AddInterceptors(new AuditInterceptor(tenant, httpAccessor: null))
            .Options;

        using (var seed = new GeneraPmDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = actorId, Email = $"a-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "A", LastName = "U",
                OrganisationId = orgId, GlobalRole = UserRole.OrgAdmin,
            });
            seed.Users.Add(new User
            {
                Id = otherId, Email = $"b-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "B", LastName = "U",
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

        return new Fixture(orgId, projectId, actorId, otherId, tenant,
            new GeneraPmDbContext(options, tenant));
    }
}

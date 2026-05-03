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

namespace CimsApp.Tests.Services.Inspections;

/// <summary>
/// Behavioural tests for T-S13-02 InspectionActivity. PAFM-SD
/// F.13 Option A scope cut: v1.0 ships the data shape +
/// manual workflow; Genera bidirectional sync deferred to
/// v1.1 / B-089.
/// </summary>
public class InspectionActivityTests
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

    [Fact]
    public void Workflow_Scheduled_to_InProgress_to_Completed_or_Cancelled()
    {
        Assert.True(InspectionActivityWorkflow.IsValidTransition(
            InspectionActivityStatus.Scheduled, InspectionActivityStatus.InProgress));
        Assert.True(InspectionActivityWorkflow.IsValidTransition(
            InspectionActivityStatus.InProgress, InspectionActivityStatus.Completed));
        Assert.True(InspectionActivityWorkflow.IsValidTransition(
            InspectionActivityStatus.InProgress, InspectionActivityStatus.Cancelled));
        Assert.True(InspectionActivityWorkflow.IsValidTransition(
            InspectionActivityStatus.Scheduled, InspectionActivityStatus.Cancelled));
        // Both end states terminal.
        Assert.True(InspectionActivityWorkflow.IsTerminal(InspectionActivityStatus.Completed));
        Assert.True(InspectionActivityWorkflow.IsTerminal(InspectionActivityStatus.Cancelled));
        // Cannot skip Scheduled → Completed.
        Assert.False(InspectionActivityWorkflow.IsValidTransition(
            InspectionActivityStatus.Scheduled, InspectionActivityStatus.Completed));
    }

    [Fact]
    public async Task CreateAsync_assigns_INSP_number_and_starts_in_Scheduled()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));

        var dto = await svc.CreateAsync(projectId,
            new CreateInspectionActivityRequest(
                "Pre-pour rebar inspection",
                "Foundations grid B-D, before concrete",
                "QA",
                ScheduledAt: new DateTime(2026, 5, 10, 9, 0, 0, DateTimeKind.Utc),
                AssigneeId: userId),
            userId, null, null);

        Assert.Equal("INSP-0001",                            dto.Number);
        Assert.Equal(InspectionActivityStatus.Scheduled,     dto.Status);
        Assert.Equal("QA",                                   dto.InspectionType);
        Assert.Equal(userId,                                 dto.AssigneeId);
    }

    [Fact]
    public async Task Full_happy_path_Scheduled_Started_Completed()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateInspectionActivityRequest("X", null, null, DateTime.UtcNow, null),
            userId, null, null);

        dto = await svc.StartAsync(projectId, dto.Id,
            new StartInspectionActivityRequest("Inspector on site"),
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Equal(InspectionActivityStatus.InProgress, dto.Status);
        Assert.Equal(userId, dto.StartedById);
        Assert.NotNull(dto.StartedAt);

        dto = await svc.CompleteAsync(projectId, dto.Id,
            new CompleteInspectionActivityRequest(
                Outcome: "Pass — 2 minor observations",
                CompletionNotes: "Photos uploaded; observations actioned"),
            userId, UserRole.TaskTeamMember, null, null);
        Assert.Equal(InspectionActivityStatus.Completed, dto.Status);
        Assert.Contains("Pass", dto.Outcome);
        Assert.NotNull(dto.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_requires_Outcome()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateInspectionActivityRequest("X", null, null, DateTime.UtcNow, null),
            userId, null, null);
        await svc.StartAsync(projectId, dto.Id, new StartInspectionActivityRequest(null),
            userId, UserRole.TaskTeamMember, null, null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CompleteAsync(projectId, dto.Id,
                new CompleteInspectionActivityRequest("", null),
                userId, UserRole.TaskTeamMember, null, null));
    }

    [Fact]
    public async Task CancelAsync_requires_Reason_and_PM_role()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateInspectionActivityRequest("X", null, null, DateTime.UtcNow, null),
            userId, null, null);

        // Empty reason rejected.
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CancelAsync(projectId, dto.Id,
                new CancelInspectionActivityRequest(""),
                userId, UserRole.ProjectManager, null, null));
        // TaskTeamMember role rejected.
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.CancelAsync(projectId, dto.Id,
                new CancelInspectionActivityRequest("Site closed"),
                userId, UserRole.TaskTeamMember, null, null));

        // PM with reason: succeeds.
        var cancelled = await svc.CancelAsync(projectId, dto.Id,
            new CancelInspectionActivityRequest("Site closed for weather"),
            userId, UserRole.ProjectManager, null, null);
        Assert.Equal(InspectionActivityStatus.Cancelled, cancelled.Status);
        Assert.Equal("Site closed for weather", cancelled.CancellationReason);
    }

    [Fact]
    public async Task CompleteAsync_rejects_skip_from_Scheduled()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));
        var dto = await svc.CreateAsync(projectId,
            new CreateInspectionActivityRequest("X", null, null, DateTime.UtcNow, null),
            userId, null, null);

        // Cannot complete without first starting.
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CompleteAsync(projectId, dto.Id,
                new CompleteInspectionActivityRequest("Pass", null),
                userId, UserRole.TaskTeamMember, null, null));
    }

    [Fact]
    public async Task ListAsync_filters_by_Status()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var db = new CimsDbContext(options, tenant);
        var svc = new InspectionActivityService(db, new AuditService(db));
        var sched = await svc.CreateAsync(projectId, new CreateInspectionActivityRequest("Sched", null, null, new DateTime(2026, 5, 10), null), userId, null, null);
        var inProg = await svc.CreateAsync(projectId, new CreateInspectionActivityRequest("InProg", null, null, new DateTime(2026, 5, 11), null), userId, null, null);
        await svc.StartAsync(projectId, inProg.Id, new StartInspectionActivityRequest(null), userId, UserRole.TaskTeamMember, null, null);

        var scheduled = await svc.ListAsync(projectId, InspectionActivityStatus.Scheduled);
        Assert.Single(scheduled);
        Assert.Equal("Sched", scheduled[0].Title);

        var inProgressList = await svc.ListAsync(projectId, InspectionActivityStatus.InProgress);
        Assert.Single(inProgressList);
        Assert.Equal("InProg", inProgressList[0].Title);

        var all = await svc.ListAsync(projectId);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAsync_cross_tenant_lookup_404s()
    {
        var (options, _, _, _, _) = BuildFixture();
        var otherTenant = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(), UserId = Guid.NewGuid(),
            GlobalRole = UserRole.OrgAdmin,
        };
        var db = new CimsDbContext(options, otherTenant);
        var svc = new InspectionActivityService(db, new AuditService(db));

        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.GetAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}

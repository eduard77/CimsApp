using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Authorization;

/// <summary>
/// B-005 (assignee ownership on PATCH actions) and B-006 (responder
/// verification on POST rfis/respond). Both items were known
/// deferred per `docs/security/role-matrix.md#known-deferred-checks`
/// at S0 and S1 close; lifting them to honest service-level guards
/// here.
///
/// Rules:
/// - Action update: caller must be the action's assignee OR
///   ProjectManager+. Unassigned actions can be updated by any
///   TaskTeamMember+ (the floor enforced at the controller).
/// - RFI respond: if AssignedToId is set, caller must be that user
///   OR InformationManager+. Unassigned RFIs remain open for any
///   TaskTeamMember+ to respond.
/// </summary>
public class OwnershipCheckTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid assigneeId, Guid otherUserId, Guid pmUserId, Guid imUserId, Guid projectId)
        BuildFixture()
    {
        var orgId        = Guid.NewGuid();
        var assigneeId   = Guid.NewGuid();
        var otherUserId  = Guid.NewGuid();
        var pmUserId     = Guid.NewGuid();
        var imUserId     = Guid.NewGuid();
        var projectId    = Guid.NewGuid();

        // The tenant context is shared across writes — `userId` on the
        // tenant only matters for audit attribution; the ownership
        // check examines the *caller's* userId passed into the service
        // method, not the tenant's.
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = assigneeId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.AddRange(
            new User { Id = assigneeId,  Email = $"a-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = orgId },
            new User { Id = otherUserId, Email = $"o-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "O", LastName = "U", OrganisationId = orgId },
            new User { Id = pmUserId,    Email = $"p-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "P", LastName = "U", OrganisationId = orgId },
            new User { Id = imUserId,    Email = $"i-{Guid.NewGuid():N}@e.com", PasswordHash = "x", FirstName = "I", LastName = "U", OrganisationId = orgId });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "P", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
        });
        seed.SaveChanges();
        return (options, tenant, orgId, assigneeId, otherUserId, pmUserId, imUserId, projectId);
    }

    // ── B-005 ActionsService.UpdateAsync ─────────────────────────────────────

    [Fact]
    public async Task Action_assignee_can_update_their_own_action()
    {
        var (options, tenant, _, assigneeId, _, _, _, projectId) = BuildFixture();
        Guid actionId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var a = new ActionItem
            {
                ProjectId = projectId, Title = "T", CreatedById = assigneeId,
                AssigneeId = assigneeId,
            };
            seed.ActionItems.Add(a);
            seed.SaveChanges();
            actionId = a.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new ActionsService(db, new AuditService(db));
        // Assignee updates with TaskTeamMember role — allowed because
        // they are the assignee.
        var updated = await svc.UpdateAsync(actionId, projectId,
            new UpdateActionRequest("Updated title", null, null, null, null, null),
            assigneeId, UserRole.TaskTeamMember, ip: null, ua: null);
        Assert.Equal("Updated title", updated.Title);
    }

    [Fact]
    public async Task Action_non_assignee_TaskTeamMember_is_forbidden()
    {
        var (options, tenant, _, assigneeId, otherUserId, _, _, projectId) = BuildFixture();
        Guid actionId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var a = new ActionItem
            {
                ProjectId = projectId, Title = "T", CreatedById = assigneeId,
                AssigneeId = assigneeId,
            };
            seed.ActionItems.Add(a);
            seed.SaveChanges();
            actionId = a.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new ActionsService(db, new AuditService(db));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.UpdateAsync(actionId, projectId,
                new UpdateActionRequest("attempted", null, null, null, null, null),
                otherUserId, UserRole.TaskTeamMember, null, null));
    }

    [Fact]
    public async Task Action_PM_can_update_someone_elses_action()
    {
        // PM-level override path — needed for re-assignments and
        // stale-action cleanup, see comment in ActionsService.UpdateAsync.
        var (options, tenant, _, assigneeId, _, pmUserId, _, projectId) = BuildFixture();
        Guid actionId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var a = new ActionItem
            {
                ProjectId = projectId, Title = "T", CreatedById = assigneeId,
                AssigneeId = assigneeId,
            };
            seed.ActionItems.Add(a);
            seed.SaveChanges();
            actionId = a.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new ActionsService(db, new AuditService(db));
        var updated = await svc.UpdateAsync(actionId, projectId,
            new UpdateActionRequest(null, null, null, ActionStatus.Closed, null, null),
            pmUserId, UserRole.ProjectManager, null, null);
        Assert.Equal(ActionStatus.Closed, updated.Status);
        Assert.NotNull(updated.ClosedAt);
    }

    [Fact]
    public async Task Action_unassigned_can_be_updated_by_any_TaskTeamMember()
    {
        // Gap-of-ownership case — unassigned actions don't grind.
        var (options, tenant, _, _, otherUserId, _, _, projectId) = BuildFixture();
        Guid actionId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var a = new ActionItem
            {
                ProjectId = projectId, Title = "T", CreatedById = otherUserId,
                AssigneeId = null,
            };
            seed.ActionItems.Add(a);
            seed.SaveChanges();
            actionId = a.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new ActionsService(db, new AuditService(db));
        var updated = await svc.UpdateAsync(actionId, projectId,
            new UpdateActionRequest("picked up", null, null, null, null, null),
            otherUserId, UserRole.TaskTeamMember, null, null);
        Assert.Equal("picked up", updated.Title);
    }

    // ── B-006 RfiService.RespondAsync ───────────────────────────────────────

    [Fact]
    public async Task Rfi_assigned_responder_can_respond()
    {
        var (options, tenant, _, assigneeId, _, _, _, projectId) = BuildFixture();
        Guid rfiId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var r = new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-0001",
                Subject = "Q", Description = "...",
                RaisedById = assigneeId, AssignedToId = assigneeId,
            };
            seed.Rfis.Add(r);
            seed.SaveChanges();
            rfiId = r.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new RfiService(db, new AuditService(db));
        var responded = await svc.RespondAsync(rfiId, projectId,
            new RespondRfiRequest("answered", RfiStatus.Responded),
            assigneeId, UserRole.TaskTeamMember, null, null);
        Assert.Equal("answered", responded.Response);
    }

    [Fact]
    public async Task Rfi_non_assigned_TaskTeamMember_is_forbidden()
    {
        var (options, tenant, _, assigneeId, otherUserId, _, _, projectId) = BuildFixture();
        Guid rfiId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var r = new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-0001",
                Subject = "Q", Description = "...",
                RaisedById = assigneeId, AssignedToId = assigneeId,
            };
            seed.Rfis.Add(r);
            seed.SaveChanges();
            rfiId = r.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new RfiService(db, new AuditService(db));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            svc.RespondAsync(rfiId, projectId,
                new RespondRfiRequest("attempted", RfiStatus.Responded),
                otherUserId, UserRole.TaskTeamMember, null, null));
    }

    [Fact]
    public async Task Rfi_InformationManager_can_respond_to_someone_elses_assignment()
    {
        // IM-level override — IMs are the natural escalation when an
        // assigned responder is unavailable.
        var (options, tenant, _, assigneeId, _, _, imUserId, projectId) = BuildFixture();
        Guid rfiId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var r = new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-0001",
                Subject = "Q", Description = "...",
                RaisedById = assigneeId, AssignedToId = assigneeId,
            };
            seed.Rfis.Add(r);
            seed.SaveChanges();
            rfiId = r.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new RfiService(db, new AuditService(db));
        var responded = await svc.RespondAsync(rfiId, projectId,
            new RespondRfiRequest("escalation", RfiStatus.Responded),
            imUserId, UserRole.InformationManager, null, null);
        Assert.Equal("escalation", responded.Response);
    }

    [Fact]
    public async Task Rfi_unassigned_can_be_responded_by_any_TaskTeamMember()
    {
        var (options, tenant, _, _, otherUserId, _, _, projectId) = BuildFixture();
        Guid rfiId;
        using (var seed = new CimsDbContext(options, tenant))
        {
            var r = new Rfi
            {
                ProjectId = projectId, RfiNumber = "RFI-0001",
                Subject = "Q", Description = "...",
                RaisedById = otherUserId, AssignedToId = null,
            };
            seed.Rfis.Add(r);
            seed.SaveChanges();
            rfiId = r.Id;
        }

        using var db = new CimsDbContext(options, tenant);
        var svc = new RfiService(db, new AuditService(db));
        var responded = await svc.RespondAsync(rfiId, projectId,
            new RespondRfiRequest("picked up", RfiStatus.Responded),
            otherUserId, UserRole.TaskTeamMember, null, null);
        Assert.Equal("picked up", responded.Response);
    }
}

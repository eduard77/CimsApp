using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Schedule;

/// <summary>
/// Behavioural tests for the activity-CRUD + RecomputeAsync slice of
/// <see cref="ScheduleService"/> (T-S4-05). Covers Create / Update /
/// Deactivate / List, the assignee-must-be-project-member rule, the
/// "remove dependencies first" deactivation guard, the recompute
/// happy path (CPM fields populated end-to-end through DB), and
/// cross-tenant 404 via the query filter.
/// </summary>
public class ScheduleServiceActivityTests
{
    private static readonly DateTime ProjectStart = new(2026, 6, 1);

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
            StartDate = ProjectStart,
        });
        seed.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = projectId, UserId = userId, Role = UserRole.ProjectManager,
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    private static CreateActivityRequest Basic(string code = "A1010", decimal duration = 5m) =>
        new(Code: code, Name: $"Activity {code}", Description: null,
            Duration: duration, DurationUnit: DurationUnit.Day,
            ScheduledStart: null, ScheduledFinish: null,
            ConstraintType: ConstraintType.ASAP, ConstraintDate: null,
            PercentComplete: 0m, AssigneeId: null, Discipline: null);

    [Fact]
    public async Task CreateActivityAsync_persists_with_audit_twin()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            id = (await svc.CreateActivityAsync(projectId, Basic(), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var act = await verify.Activities.SingleAsync(a => a.Id == id);
        Assert.Equal("A1010", act.Code);
        Assert.Equal(5m,      act.Duration);
        Assert.True(act.IsActive);

        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "activity.created");
        Assert.Contains("\"code\":\"A1010\"",      row.Detail!);
        Assert.Contains("\"durationUnit\":\"Day\"", row.Detail);
    }

    [Fact]
    public async Task CreateActivityAsync_rejects_duplicate_code_in_same_project()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, Basic("A"), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.CreateActivityAsync(projectId, Basic("A"), userId));
    }

    [Fact]
    public async Task CreateActivityAsync_rejects_negative_duration()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateActivityAsync(projectId, Basic() with { Duration = -1m }, userId));
    }

    [Fact]
    public async Task CreateActivityAsync_rejects_constraint_without_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        // SNET requires a ConstraintDate.
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateActivityAsync(projectId,
                Basic() with { ConstraintType = ConstraintType.SNET, ConstraintDate = null },
                userId));
    }

    [Fact]
    public async Task CreateActivityAsync_rejects_assignee_who_is_not_project_member()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var outsider = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Users.Add(new User
            {
                Id = outsider, Email = $"o-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "O", LastName = "U",
                OrganisationId = tenant.OrganisationId!.Value,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateActivityAsync(projectId,
                Basic() with { AssigneeId = outsider }, userId));
    }

    [Fact]
    public async Task UpdateActivityAsync_partial_update_only_named_fields()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            id = (await svc.CreateActivityAsync(projectId, Basic(), userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.UpdateActivityAsync(projectId, id,
                new UpdateActivityRequest(
                    Code: null, Name: null, Description: null,
                    Duration: 7m, DurationUnit: null,
                    ScheduledStart: null, ScheduledFinish: null,
                    ConstraintType: null, ConstraintDate: null,
                    PercentComplete: 0.25m,
                    AssigneeId: null, Discipline: null),
                userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var act = await verify.Activities.SingleAsync(a => a.Id == id);
        Assert.Equal(7m,    act.Duration);
        Assert.Equal(0.25m, act.PercentComplete);
        Assert.Equal("A1010", act.Code);   // unchanged
    }

    [Fact]
    public async Task UpdateActivityAsync_rejects_no_op()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            id = (await svc.CreateActivityAsync(projectId, Basic(), userId)).Id;
        }
        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc2.UpdateActivityAsync(projectId, id,
                new UpdateActivityRequest(null, null, null, null, null, null, null, null, null, null, null, null),
                userId));
    }

    [Fact]
    public async Task UpdateActivityAsync_constraint_to_ASAP_clears_dangling_constraint_date()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            id = (await svc.CreateActivityAsync(projectId,
                Basic() with { ConstraintType = ConstraintType.SNET, ConstraintDate = ProjectStart.AddDays(5) },
                userId)).Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.UpdateActivityAsync(projectId, id,
                new UpdateActivityRequest(
                    null, null, null, null, null, null, null,
                    ConstraintType: ConstraintType.ASAP,
                    null, null, null, null),
                userId);
        }
        using var verify = new CimsDbContext(options, tenant);
        var act = await verify.Activities.SingleAsync(a => a.Id == id);
        Assert.Equal(ConstraintType.ASAP, act.ConstraintType);
        Assert.Null(act.ConstraintDate);
    }

    [Fact]
    public async Task DeactivateActivityAsync_rejects_when_dependencies_exist()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid aId, bId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            aId = (await svc.CreateActivityAsync(projectId, Basic("A"), userId)).Id;
            bId = (await svc.CreateActivityAsync(projectId, Basic("B"), userId)).Id;
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.FS, 0m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.DeactivateActivityAsync(projectId, aId, userId));
    }

    [Fact]
    public async Task DeactivateActivityAsync_succeeds_after_dependencies_removed()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid aId, bId, depId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            aId = (await svc.CreateActivityAsync(projectId, Basic("A"), userId)).Id;
            bId = (await svc.CreateActivityAsync(projectId, Basic("B"), userId)).Id;
            depId = (await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.FS, 0m), userId)).Id;
            await svc.RemoveDependencyAsync(projectId, depId, userId);
            await svc.DeactivateActivityAsync(projectId, aId, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var act = await verify.Activities.SingleAsync(a => a.Id == aId);
        Assert.False(act.IsActive);
    }

    [Fact]
    public async Task ListActivitiesAsync_returns_active_only_ordered_by_code()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid keepId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, Basic("Z"), userId);
            keepId = (await svc.CreateActivityAsync(projectId, Basic("M"), userId)).Id;
            var staleId = (await svc.CreateActivityAsync(projectId, Basic("A"), userId)).Id;
            await svc.DeactivateActivityAsync(projectId, staleId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var list = await svc2.ListActivitiesAsync(projectId);

        Assert.Equal(2, list.Count);
        Assert.Equal("M", list[0].Code);
        Assert.Equal("Z", list[1].Code);
    }

    [Fact]
    public async Task RecomputeAsync_populates_CPM_fields_end_to_end()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid aId, bId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            aId = (await svc.CreateActivityAsync(projectId, Basic("A", 3m), userId)).Id;
            bId = (await svc.CreateActivityAsync(projectId, Basic("B", 2m), userId)).Id;
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.FS, 0m), userId);
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var result = await svc.RecomputeAsync(projectId, dataDate: null, userId);
            Assert.Equal(2, result.ActivitiesCount);
            Assert.Equal(2, result.CriticalActivitiesCount);
            Assert.Equal(ProjectStart.AddDays(5), result.ProjectFinish);
        }

        using var verify = new CimsDbContext(options, tenant);
        var a = await verify.Activities.SingleAsync(x => x.Id == aId);
        var b = await verify.Activities.SingleAsync(x => x.Id == bId);
        Assert.Equal(ProjectStart,            a.EarlyStart);
        Assert.Equal(ProjectStart.AddDays(3), a.EarlyFinish);
        Assert.True(a.IsCritical);
        Assert.Equal(ProjectStart.AddDays(3), b.EarlyStart);
        Assert.Equal(ProjectStart.AddDays(5), b.EarlyFinish);
        Assert.True(b.IsCritical);
        Assert.Equal(0m, a.TotalFloat);
        Assert.Equal(0m, b.TotalFloat);

        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(x => x.Action == "schedule.recomputed");
        Assert.Contains("\"activities\":2",   row.Detail!);
        Assert.Contains("\"criticalCount\":2", row.Detail);
    }

    [Fact]
    public async Task RecomputeAsync_uses_explicit_dataDate_override()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, Basic("A", 3m), userId);
        }

        var override_ = new DateTime(2027, 1, 1);
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var result = await svc.RecomputeAsync(projectId, override_, userId);
            Assert.Equal(override_, result.ProjectStart);
            Assert.Equal(override_.AddDays(3), result.ProjectFinish);
        }
    }

    [Fact]
    public async Task RecomputeAsync_rejects_when_no_data_date_and_project_has_no_StartDate()
    {
        var (options, tenant, orgId, userId, _) = BuildFixture();
        // Seed a second project with no StartDate.
        var p2 = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Projects.Add(new Project
            {
                Id = p2, Name = "No-start", Code = "NS",
                AppointingPartyId = orgId, Currency = "GBP",
                StartDate = null,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RecomputeAsync(p2, dataDate: null, userId));
    }

    [Fact]
    public async Task UpdateActivityAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid id;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            id = (await svc.CreateActivityAsync(projectId, Basic(), userId)).Id;
        }

        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.UpdateActivityAsync(projectId, id,
                new UpdateActivityRequest(Code: "PWN",
                    null, null, null, null, null, null, null, null, null, null, null),
                attacker.UserId!.Value));
    }
}

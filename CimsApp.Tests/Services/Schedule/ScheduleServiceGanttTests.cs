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
/// Behavioural tests for <see cref="ScheduleService.GetGanttAsync"/>
/// (T-S4-11). The endpoint is a read-only DTO transformation — the
/// tests cover the CPM-vs-Scheduled fallback rule, project-bound
/// computation, dependency-list pass-through, and cross-tenant
/// 404 via the query filter.
/// </summary>
public class ScheduleServiceGanttTests
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

    private static CreateActivityRequest BasicAct(string code, decimal duration = 5m) =>
        new(Code: code, Name: $"Activity {code}", Description: null,
            Duration: duration, DurationUnit: DurationUnit.Day,
            ScheduledStart: null, ScheduledFinish: null,
            ConstraintType: ConstraintType.ASAP, ConstraintDate: null,
            PercentComplete: 0m, AssigneeId: null, Discipline: null);

    [Fact]
    public async Task GetGanttAsync_returns_empty_for_project_with_no_activities()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        var dto = await svc.GetGanttAsync(projectId);
        Assert.Empty(dto.Activities);
        Assert.Empty(dto.Dependencies);
        Assert.Null(dto.ProjectStart);
        Assert.Null(dto.ProjectFinish);
    }

    [Fact]
    public async Task GetGanttAsync_uses_CPM_dates_after_recompute()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var aId = (await svc.CreateActivityAsync(projectId, BasicAct("A", 3m), userId)).Id;
            var bId = (await svc.CreateActivityAsync(projectId, BasicAct("B", 2m), userId)).Id;
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.FS, 0m), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetGanttAsync(projectId);

        Assert.Equal(2, dto.Activities.Count);
        Assert.Single(dto.Dependencies);
        Assert.Equal(ProjectStart,            dto.ProjectStart);
        Assert.Equal(ProjectStart.AddDays(5), dto.ProjectFinish);

        var bRow = dto.Activities.Single(a => a.Code == "B");
        Assert.Equal(ProjectStart.AddDays(3), bRow.Start);
        Assert.Equal(ProjectStart.AddDays(5), bRow.Finish);
        Assert.True(bRow.IsCritical);
    }

    [Fact]
    public async Task GetGanttAsync_falls_back_to_scheduled_dates_before_recompute()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var manualStart  = new DateTime(2026, 6, 1);
        var manualFinish = new DateTime(2026, 6, 4);
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId,
                BasicAct("A", 3m) with { ScheduledStart = manualStart, ScheduledFinish = manualFinish },
                userId);
            // No RecomputeAsync — Early* fields stay null.
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetGanttAsync(projectId);

        var row = dto.Activities.Single();
        Assert.Equal(manualStart,  row.Start);
        Assert.Equal(manualFinish, row.Finish);
        Assert.False(row.IsCritical);
        Assert.Equal(manualStart,  dto.ProjectStart);
        Assert.Equal(manualFinish, dto.ProjectFinish);
    }

    [Fact]
    public async Task GetGanttAsync_excludes_deactivated_activities()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid keepId, staleId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            keepId  = (await svc.CreateActivityAsync(projectId, BasicAct("K"), userId)).Id;
            staleId = (await svc.CreateActivityAsync(projectId, BasicAct("S"), userId)).Id;
            await svc.DeactivateActivityAsync(projectId, staleId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetGanttAsync(projectId);

        Assert.Single(dto.Activities);
        Assert.Equal(keepId, dto.Activities[0].Id);
    }

    [Fact]
    public async Task GetGanttAsync_returns_dependencies_with_type_and_lag()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var aId = (await svc.CreateActivityAsync(projectId, BasicAct("A"), userId)).Id;
            var bId = (await svc.CreateActivityAsync(projectId, BasicAct("B"), userId)).Id;
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.SS, 2m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetGanttAsync(projectId);

        var dep = dto.Dependencies.Single();
        Assert.Equal(DependencyType.SS, dep.Type);
        Assert.Equal(2m, dep.Lag);
    }

    [Fact]
    public async Task GetGanttAsync_unknown_project_404s()
    {
        var (options, tenant, _, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetGanttAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetGanttAsync_cross_tenant_404s_via_query_filter()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, BasicAct("A"), userId);
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
            svc2.GetGanttAsync(projectId));
    }
}

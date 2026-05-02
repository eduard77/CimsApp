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
/// Behavioural tests for the baseline-CRUD slice of
/// <see cref="ScheduleService"/> (T-S4-06). Covers capture, listing,
/// comparison happy-path, the new / removed activity-set delta, and
/// cross-tenant 404 via the query filter.
/// </summary>
public class ScheduleServiceBaselineTests
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
    public async Task CreateBaselineAsync_snapshots_active_activities_after_recompute()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid baselineId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var aId = (await svc.CreateActivityAsync(projectId, BasicAct("A", 3m), userId)).Id;
            var bId = (await svc.CreateActivityAsync(projectId, BasicAct("B", 2m), userId)).Id;
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(aId, bId, DependencyType.FS, 0m), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            baselineId = (await svc.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Original baseline"), userId)).Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var b = await verify.ScheduleBaselines.SingleAsync(x => x.Id == baselineId);
        Assert.Equal("Original baseline", b.Label);
        Assert.Equal(2, b.ActivitiesCount);
        Assert.Equal(ProjectStart.AddDays(5), b.ProjectFinishAtBaseline);

        var rows = await verify.ScheduleBaselineActivities
            .Where(x => x.ScheduleBaselineId == baselineId).ToListAsync();
        Assert.Equal(2, rows.Count);
        var aRow = rows.Single(r => r.Code == "A");
        Assert.Equal(ProjectStart,            aRow.EarlyStart);
        Assert.Equal(ProjectStart.AddDays(3), aRow.EarlyFinish);
        Assert.True(aRow.IsCritical);
    }

    [Fact]
    public async Task CreateBaselineAsync_emits_audit_twin()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, BasicAct("A"), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("Rev 1"), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "schedule_baseline.captured");
        Assert.Contains("\"label\":\"Rev 1\"",       row.Detail!);
        Assert.Contains("\"activitiesCount\":1",     row.Detail);
    }

    [Fact]
    public async Task CreateBaselineAsync_rejects_empty_label()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("  "), userId));
    }

    [Fact]
    public async Task CreateBaselineAsync_accepts_empty_activity_set()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("Anchor"), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var b = await verify.ScheduleBaselines.SingleAsync();
        Assert.Equal(0, b.ActivitiesCount);
        Assert.Null(b.ProjectFinishAtBaseline);
    }

    [Fact]
    public async Task ListBaselinesAsync_returns_newest_first()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("Original"), userId);
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("Rev 1"), userId);
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("Rev 2"), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var list = await svc2.ListBaselinesAsync(projectId);
        Assert.Equal(3, list.Count);
        Assert.Equal("Rev 2",    list[0].Label);
        Assert.Equal("Original", list[2].Label);
    }

    [Fact]
    public async Task GetBaselineComparisonAsync_reports_zero_variance_when_unchanged()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid baselineId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, BasicAct("A", 3m), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            baselineId = (await svc.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Original"), userId)).Id;
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetBaselineComparisonAsync(projectId, baselineId);
        Assert.Single(dto.Activities);
        var row = dto.Activities[0];
        Assert.Equal(0m, row.StartVarianceDays);
        Assert.Equal(0m, row.FinishVarianceDays);
        Assert.Equal(0m, row.DurationVarianceDays);
        Assert.False(row.IsNewSinceBaseline);
        Assert.False(row.IsRemovedSinceBaseline);
        Assert.Equal(0m, dto.ProjectFinishVarianceDays);
    }

    [Fact]
    public async Task GetBaselineComparisonAsync_reports_positive_variance_when_activity_extended()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid aId, baselineId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            aId = (await svc.CreateActivityAsync(projectId, BasicAct("A", 3m), userId)).Id;
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            baselineId = (await svc.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Original"), userId)).Id;

            // Extend A from 3 days to 7 days, then recompute.
            await svc.UpdateActivityAsync(projectId, aId,
                new UpdateActivityRequest(null, null, null,
                    Duration: 7m, null, null, null, null, null, null, null, null),
                userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetBaselineComparisonAsync(projectId, baselineId);
        var row = dto.Activities.Single();
        Assert.Equal(0m, row.StartVarianceDays);     // start unchanged
        Assert.Equal(4m, row.FinishVarianceDays);    // slipped 4 days later
        Assert.Equal(4m, row.DurationVarianceDays);  // 7 - 3 = 4
        Assert.Equal(4m, dto.ProjectFinishVarianceDays);
    }

    [Fact]
    public async Task GetBaselineComparisonAsync_flags_activities_added_after_baseline()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid baselineId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, BasicAct("A"), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            baselineId = (await svc.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Original"), userId)).Id;
            // Add a new activity after baseline.
            await svc.CreateActivityAsync(projectId, BasicAct("B"), userId);
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetBaselineComparisonAsync(projectId, baselineId);
        Assert.Equal(1, dto.AddedActivitiesCount);
        Assert.Equal(0, dto.RemovedActivitiesCount);
        var bRow = dto.Activities.Single(x => x.Code == "B");
        Assert.True(bRow.IsNewSinceBaseline);
        Assert.Null(bRow.BaselineEarlyStart);
        Assert.NotNull(bRow.CurrentEarlyStart);
    }

    [Fact]
    public async Task GetBaselineComparisonAsync_flags_activities_removed_after_baseline()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        Guid bId, baselineId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateActivityAsync(projectId, BasicAct("A"), userId);
            bId = (await svc.CreateActivityAsync(projectId, BasicAct("B"), userId)).Id;
            await svc.RecomputeAsync(projectId, dataDate: null, userId);
            baselineId = (await svc.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Original"), userId)).Id;
            await svc.DeactivateActivityAsync(projectId, bId, userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var dto = await svc2.GetBaselineComparisonAsync(projectId, baselineId);
        Assert.Equal(0, dto.AddedActivitiesCount);
        Assert.Equal(1, dto.RemovedActivitiesCount);
        var bRow = dto.Activities.Single(x => x.Code == "B");
        Assert.True(bRow.IsRemovedSinceBaseline);
        Assert.Null(bRow.CurrentEarlyStart);
        Assert.NotNull(bRow.BaselineEarlyStart);
    }

    [Fact]
    public async Task CreateBaselineAsync_cross_tenant_404s_via_query_filter()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.CreateBaselineAsync(projectId, new CreateBaselineRequest("OK"), userId);
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
            svc2.CreateBaselineAsync(projectId,
                new CreateBaselineRequest("Pwn"), attacker.UserId!.Value));
    }

    [Fact]
    public async Task GetBaselineComparisonAsync_unknown_baseline_404s()
    {
        var (options, tenant, _, _, projectId) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.GetBaselineComparisonAsync(projectId, Guid.NewGuid()));
    }
}

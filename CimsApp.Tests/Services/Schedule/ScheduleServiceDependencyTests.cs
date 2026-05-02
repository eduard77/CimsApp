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
/// Behavioural tests for the dependency-CRUD slice of
/// <see cref="ScheduleService"/> (T-S4-03). Covers same-project
/// enforcement, lag bounds, self-loop / duplicate / cycle rejection,
/// audit-twin emission, cross-tenant 404 via the query filter.
/// </summary>
public class ScheduleServiceDependencyTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId, Guid actA, Guid actB, Guid actC) BuildFixture()
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

        var actA = Guid.NewGuid();
        var actB = Guid.NewGuid();
        var actC = Guid.NewGuid();

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
        seed.Activities.AddRange(
            new Activity { Id = actA, ProjectId = projectId, Code = "A", Name = "Act A", Duration = 5m },
            new Activity { Id = actB, ProjectId = projectId, Code = "B", Name = "Act B", Duration = 3m },
            new Activity { Id = actC, ProjectId = projectId, Code = "C", Name = "Act C", Duration = 2m });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId, actA, actB, actC);
    }

    [Fact]
    public async Task AddDependencyAsync_persists_FS_with_zero_lag()
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();
        Guid depId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            var dep = await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, 0m), userId);
            depId = dep.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var d = await verify.Dependencies.SingleAsync(x => x.Id == depId);
        Assert.Equal(actA, d.PredecessorId);
        Assert.Equal(actB, d.SuccessorId);
        Assert.Equal(DependencyType.FS, d.Type);
        Assert.Equal(0m, d.Lag);
    }

    [Fact]
    public async Task AddDependencyAsync_emits_dependency_added_audit_twin()
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.SS, 2m), userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "dependency.added");
        Assert.Equal("Dependency", row.Entity);
        Assert.Contains(actA.ToString(), row.Detail!);
        Assert.Contains(actB.ToString(), row.Detail);
        Assert.Contains("SS",  row.Detail);
        Assert.Contains("\"lag\":2", row.Detail);
    }

    [Fact]
    public async Task AddDependencyAsync_rejects_self_loop()
    {
        var (options, tenant, _, userId, projectId, actA, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actA, DependencyType.FS, 0m), userId));
    }

    [Theory]
    [InlineData(-366)]
    [InlineData( 366)]
    public async Task AddDependencyAsync_rejects_lag_out_of_range(decimal lag)
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, lag), userId));
    }

    [Fact]
    public async Task AddDependencyAsync_rejects_unknown_predecessor_404()
    {
        var (options, tenant, _, userId, projectId, _, actB, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(Guid.NewGuid(), actB, DependencyType.FS, 0m), userId));
    }

    [Fact]
    public async Task AddDependencyAsync_rejects_duplicate_pair()
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, 0m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.SS, 1m), userId));
    }

    [Fact]
    public async Task AddDependencyAsync_rejects_edge_that_would_create_cycle()
    {
        var (options, tenant, _, userId, projectId, actA, actB, actC) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            // Build A → B → C
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, 0m), userId);
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actB, actC, DependencyType.FS, 0m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        // Try to close C → A. That makes A → B → C → A — cycle.
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            svc2.AddDependencyAsync(projectId,
                new AddDependencyRequest(actC, actA, DependencyType.FS, 0m), userId));
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public async Task AddDependencyAsync_cross_project_predecessor_404s()
    {
        var (options, tenant, orgId, userId, _, actA, _, _) = BuildFixture();
        // Seed a second project + activity in the same tenant.
        var project2 = Guid.NewGuid();
        var actX     = Guid.NewGuid();
        using (var db = new CimsDbContext(options, tenant))
        {
            db.Projects.Add(new Project
            {
                Id = project2, Name = "Project 2", Code = "PR2",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            db.Activities.Add(new Activity
            {
                Id = actX, ProjectId = project2, Code = "X", Name = "Act X", Duration = 1m,
            });
            db.SaveChanges();
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db2, new AuditService(db2));
        // Try to add Project1's actA as predecessor of Project2's actX.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.AddDependencyAsync(project2,
                new AddDependencyRequest(actA, actX, DependencyType.FS, 0m), userId));
    }

    [Fact]
    public async Task RemoveDependencyAsync_deletes_row_and_emits_audit()
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();
        Guid depId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            depId = (await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FF, 5m), userId)).Id;
            await svc.RemoveDependencyAsync(projectId, depId, userId);
        }

        using var verify = new CimsDbContext(options, tenant);
        Assert.False(await verify.Dependencies.AnyAsync(d => d.Id == depId));
        var row = await verify.AuditLogs.IgnoreQueryFilters()
            .SingleAsync(a => a.Action == "dependency.removed");
        Assert.Contains("FF",  row.Detail!);
        Assert.Contains("\"lag\":5", row.Detail);
    }

    [Fact]
    public async Task RemoveDependencyAsync_unknown_id_404s()
    {
        var (options, tenant, _, userId, projectId, _, _, _) = BuildFixture();
        using var db = new CimsDbContext(options, tenant);
        var svc = new ScheduleService(db, new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RemoveDependencyAsync(projectId, Guid.NewGuid(), userId));
    }

    [Fact]
    public async Task ListDependenciesAsync_returns_project_scoped_rows()
    {
        var (options, tenant, _, userId, projectId, actA, actB, actC) = BuildFixture();
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, 0m), userId);
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actB, actC, DependencyType.FS, 1m), userId);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        var list = await svc2.ListDependenciesAsync(projectId);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task AddDependencyAsync_cross_tenant_lookup_404s()
    {
        var (options, tenant, _, userId, projectId, actA, actB, _) = BuildFixture();

        // First, populate one valid edge as the legitimate tenant.
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new ScheduleService(db, new AuditService(db));
            await svc.AddDependencyAsync(projectId,
                new AddDependencyRequest(actA, actB, DependencyType.FS, 0m), userId);
        }

        // Attempt the same edge as an attacker in a different tenant.
        var attacker = new StubTenantContext
        {
            OrganisationId = Guid.NewGuid(),
            UserId         = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        using var db2 = new CimsDbContext(options, attacker);
        var svc2 = new ScheduleService(db2, new AuditService(db2));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc2.AddDependencyAsync(projectId,
                new AddDependencyRequest(Guid.NewGuid(), Guid.NewGuid(), DependencyType.FS, 0m),
                attacker.UserId!.Value));
    }
}

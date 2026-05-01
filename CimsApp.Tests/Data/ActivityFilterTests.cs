using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the Activity tenant query filter
/// (T-S4-02). Mirrors the StakeholderFilterTests / RiskFilterTests
/// shape — two tenants, prove a tenant-A session sees only its own
/// activities and IgnoreQueryFilters returns both.
/// </summary>
public class ActivityFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA, Guid projectA, Guid projectB)
        SeedTwoTenants(string dbName)
    {
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var userA    = Guid.NewGuid();
        var userB    = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using var seed = new CimsDbContext(options, seedTenant);
        seed.Organisations.AddRange(
            new Organisation { Id = OrgA, Name = "Tenant A", Code = "TA" },
            new Organisation { Id = OrgB, Name = "Tenant B", Code = "TB" });
        seed.Users.AddRange(
            new User { Id = userA, Email = $"a-{Guid.NewGuid():N}@example.com", PasswordHash = "x", FirstName = "A", LastName = "User", OrganisationId = OrgA },
            new User { Id = userB, Email = $"b-{Guid.NewGuid():N}@example.com", PasswordHash = "x", FirstName = "B", LastName = "User", OrganisationId = OrgB });
        seed.Projects.AddRange(
            new Project { Id = projectA, Name = "Project A", Code = "PA", AppointingPartyId = OrgA, Currency = "GBP" },
            new Project { Id = projectB, Name = "Project B", Code = "PB", AppointingPartyId = OrgB, Currency = "GBP" });
        seed.SaveChanges();
        return (options, userA, projectA, projectB);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_activities()
    {
        var (options, userA, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Activities.AddRange(
                new Activity { ProjectId = projectA, Code = "A1010", Name = "Excavation A", Duration = 5m },
                new Activity { ProjectId = projectB, Code = "B1010", Name = "Excavation B", Duration = 5m });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.Activities.ToList();

        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
        Assert.Equal("A1010",  list[0].Code);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_activities()
    {
        var (options, userA, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Activities.AddRange(
                new Activity { ProjectId = projectA, Code = "A", Name = "A act", Duration = 1m },
                new Activity { ProjectId = projectB, Code = "B", Name = "B act", Duration = 1m });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.Activities.IgnoreQueryFilters().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Activity_with_optional_CPM_fields_round_trips_null()
    {
        // Defensive: most CPM fields are nullable until first solver run.
        var (options, userA, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Activities.Add(new Activity
            {
                ProjectId = projectA,
                Code      = "MIL-100",
                Name      = "Practical Completion",
                Duration  = 0m,           // milestone
            });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var act = db.Activities.Single();
        Assert.Null(act.EarlyStart);
        Assert.Null(act.EarlyFinish);
        Assert.Null(act.LateStart);
        Assert.Null(act.LateFinish);
        Assert.Null(act.TotalFloat);
        Assert.Null(act.FreeFloat);
        Assert.False(act.IsCritical);
        Assert.Null(act.AssigneeId);
        Assert.Equal(ConstraintType.ASAP, act.ConstraintType);
        Assert.Equal(DurationUnit.Day,    act.DurationUnit);
    }

    [Fact]
    public void Dependency_filter_scopes_to_owning_tenant()
    {
        var (options, userA, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var aStart = new Activity { ProjectId = projectA, Code = "A1", Name = "Start A", Duration = 1m };
            var aEnd   = new Activity { ProjectId = projectA, Code = "A2", Name = "End A",   Duration = 1m };
            var bStart = new Activity { ProjectId = projectB, Code = "B1", Name = "Start B", Duration = 1m };
            var bEnd   = new Activity { ProjectId = projectB, Code = "B2", Name = "End B",   Duration = 1m };
            seed.Activities.AddRange(aStart, aEnd, bStart, bEnd);
            seed.Dependencies.AddRange(
                new Dependency { ProjectId = projectA, PredecessorId = aStart.Id, SuccessorId = aEnd.Id, Type = DependencyType.FS },
                new Dependency { ProjectId = projectB, PredecessorId = bStart.Id, SuccessorId = bEnd.Id, Type = DependencyType.FS });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var deps = db.Dependencies.ToList();

        Assert.Single(deps);
        Assert.Equal(projectA, deps[0].ProjectId);
    }
}

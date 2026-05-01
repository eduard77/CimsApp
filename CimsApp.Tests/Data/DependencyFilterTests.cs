using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the Dependency tenant query filter
/// (T-S4-12 sweep, post T-S4-02). Same shape as the other
/// XxxFilterTests files: two tenants, prove a tenant-A session sees
/// only its own dependencies and IgnoreQueryFilters returns both.
/// </summary>
public class DependencyFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA,
        Guid projectA, Guid projectB,
        Guid actA1, Guid actA2, Guid actB1, Guid actB2)
        SeedTwoTenants(string dbName)
    {
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var userA    = Guid.NewGuid();
        var userB    = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var actA1    = Guid.NewGuid();
        var actA2    = Guid.NewGuid();
        var actB1    = Guid.NewGuid();
        var actB2    = Guid.NewGuid();

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
        seed.Activities.AddRange(
            new Activity { Id = actA1, ProjectId = projectA, Code = "A1", Name = "A1", Duration = 1m },
            new Activity { Id = actA2, ProjectId = projectA, Code = "A2", Name = "A2", Duration = 1m },
            new Activity { Id = actB1, ProjectId = projectB, Code = "B1", Name = "B1", Duration = 1m },
            new Activity { Id = actB2, ProjectId = projectB, Code = "B2", Name = "B2", Duration = 1m });
        seed.SaveChanges();
        return (options, userA, projectA, projectB, actA1, actA2, actB1, actB2);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_dependencies()
    {
        var (options, userA, projectA, projectB, actA1, actA2, actB1, actB2)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Dependencies.AddRange(
                new Dependency { ProjectId = projectA, PredecessorId = actA1, SuccessorId = actA2, Type = DependencyType.FS },
                new Dependency { ProjectId = projectB, PredecessorId = actB1, SuccessorId = actB2, Type = DependencyType.FS });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var deps = db.Dependencies.ToList();
        Assert.Single(deps);
        Assert.Equal(projectA, deps[0].ProjectId);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_dependencies()
    {
        var (options, userA, projectA, projectB, actA1, actA2, actB1, actB2)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Dependencies.AddRange(
                new Dependency { ProjectId = projectA, PredecessorId = actA1, SuccessorId = actA2, Type = DependencyType.FS },
                new Dependency { ProjectId = projectB, PredecessorId = actB1, SuccessorId = actB2, Type = DependencyType.SS });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.Dependencies.IgnoreQueryFilters().ToList();
        Assert.Equal(2, all.Count);
    }
}

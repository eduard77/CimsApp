using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the LookaheadEntry tenant query
/// filter (T-S4-12 sweep, post T-S4-07). Two tenants, prove a
/// tenant-A session sees only its own lookahead entries and
/// IgnoreQueryFilters returns both.
/// </summary>
public class LookaheadEntryFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA,
        Guid projectA, Guid projectB, Guid actA, Guid actB)
        SeedTwoTenants(string dbName)
    {
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var userA    = Guid.NewGuid();
        var userB    = Guid.NewGuid();
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var actA     = Guid.NewGuid();
        var actB     = Guid.NewGuid();

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
            new Activity { Id = actA, ProjectId = projectA, Code = "A", Name = "A", Duration = 1m },
            new Activity { Id = actB, ProjectId = projectB, Code = "B", Name = "B", Duration = 1m });
        seed.SaveChanges();
        return (options, userA, projectA, projectB, actA, actB);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_lookahead_entries()
    {
        var (options, userA, projectA, projectB, actA, actB)
            = SeedTwoTenants(Guid.NewGuid().ToString());
        var monday = new DateTime(2026, 6, 1);

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.LookaheadEntries.AddRange(
                new LookaheadEntry { ProjectId = projectA, ActivityId = actA, WeekStarting = monday, CreatedById = userA },
                new LookaheadEntry { ProjectId = projectB, ActivityId = actB, WeekStarting = monday, CreatedById = userA });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.LookaheadEntries.ToList();
        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_lookahead_entries()
    {
        var (options, userA, projectA, projectB, actA, actB)
            = SeedTwoTenants(Guid.NewGuid().ToString());
        var monday = new DateTime(2026, 6, 1);

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.LookaheadEntries.AddRange(
                new LookaheadEntry { ProjectId = projectA, ActivityId = actA, WeekStarting = monday, CreatedById = userA },
                new LookaheadEntry { ProjectId = projectB, ActivityId = actB, WeekStarting = monday, CreatedById = userA });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.LookaheadEntries.IgnoreQueryFilters().ToList();
        Assert.Equal(2, all.Count);
    }
}

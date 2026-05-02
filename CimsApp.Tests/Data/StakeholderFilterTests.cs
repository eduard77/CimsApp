using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the Stakeholder tenant query filter
/// (T-S3-02). The model-level check in
/// <see cref="CimsDbContextTenantFilterTests"/> verifies a query filter
/// is registered on <see cref="Stakeholder"/>; this class proves the
/// filter actually scopes reads at runtime.
///
/// Same shape as the S2 filter tests (RiskFilterTests / RiskCategory
/// FilterTests).
/// </summary>
public class StakeholderFilterTests
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
    public void Tenant_A_sees_only_its_own_stakeholders()
    {
        var (options, userA, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Stakeholders.AddRange(
                new Stakeholder { ProjectId = projectA, Name = "A council planner", Organisation = "Council A", Power = 4, Interest = 5, Score = 20, EngagementApproach = EngagementApproach.ManageClosely },
                new Stakeholder { ProjectId = projectB, Name = "B council planner", Organisation = "Council B", Power = 4, Interest = 5, Score = 20, EngagementApproach = EngagementApproach.ManageClosely });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.Stakeholders.ToList();

        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
        Assert.Equal("A council planner", list[0].Name);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_stakeholders()
    {
        var (options, userA, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Stakeholders.AddRange(
                new Stakeholder { ProjectId = projectA, Name = "A", Power = 1, Interest = 1, Score = 1, EngagementApproach = EngagementApproach.Monitor },
                new Stakeholder { ProjectId = projectB, Name = "B", Power = 5, Interest = 5, Score = 25, EngagementApproach = EngagementApproach.ManageClosely });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.Stakeholders.IgnoreQueryFilters().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Stakeholder_with_optional_contact_fields_round_trips()
    {
        // Defensive: most stakeholder fields are optional. Confirm they
        // round-trip null without losing the row.
        var (options, userA, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Stakeholders.Add(new Stakeholder
            {
                ProjectId          = projectA,
                Name               = "Anonymous neighbour",
                Power              = 2,
                Interest           = 3,
                Score              = 6,
                EngagementApproach = EngagementApproach.KeepInformed,
            });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var s = db.Stakeholders.Single();
        Assert.Null(s.Organisation);
        Assert.Null(s.Role);
        Assert.Null(s.Email);
        Assert.Null(s.Phone);
        Assert.Null(s.EngagementNotes);
        Assert.Equal(EngagementApproach.KeepInformed, s.EngagementApproach);
    }
}

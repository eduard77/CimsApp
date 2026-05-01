using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the EngagementLog tenant query filter
/// (T-S3-08 sweep, post T-S3-06). Matches the StakeholderFilterTests
/// shape — two tenants with one EngagementLog each, prove a tenant-A
/// session sees only its own row and IgnoreQueryFilters returns both.
/// </summary>
public class EngagementLogFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA,
        Guid projectA, Guid projectB, Guid stakeholderA, Guid stakeholderB)
        SeedTwoTenants(string dbName)
    {
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var userA         = Guid.NewGuid();
        var userB         = Guid.NewGuid();
        var projectA      = Guid.NewGuid();
        var projectB      = Guid.NewGuid();
        var stakeholderA  = Guid.NewGuid();
        var stakeholderB  = Guid.NewGuid();

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
        seed.Stakeholders.AddRange(
            new Stakeholder { Id = stakeholderA, ProjectId = projectA, Name = "A planner", Power = 4, Interest = 5, Score = 20, EngagementApproach = EngagementApproach.ManageClosely },
            new Stakeholder { Id = stakeholderB, ProjectId = projectB, Name = "B planner", Power = 4, Interest = 5, Score = 20, EngagementApproach = EngagementApproach.ManageClosely });
        seed.SaveChanges();
        return (options, userA, projectA, projectB, stakeholderA, stakeholderB);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_engagement_logs()
    {
        var (options, userA, projectA, projectB, stakeholderA, stakeholderB)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.EngagementLogs.AddRange(
                new EngagementLog { ProjectId = projectA, StakeholderId = stakeholderA, Type = EngagementType.Meeting, OccurredAt = DateTime.UtcNow, Summary = "A meeting", RecordedById = userA },
                new EngagementLog { ProjectId = projectB, StakeholderId = stakeholderB, Type = EngagementType.Email,   OccurredAt = DateTime.UtcNow, Summary = "B email",   RecordedById = userA });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.EngagementLogs.ToList();

        Assert.Single(list);
        Assert.Equal(projectA,     list[0].ProjectId);
        Assert.Equal(stakeholderA, list[0].StakeholderId);
        Assert.Equal("A meeting",  list[0].Summary);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_engagement_logs()
    {
        var (options, userA, projectA, projectB, stakeholderA, stakeholderB)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.EngagementLogs.AddRange(
                new EngagementLog { ProjectId = projectA, StakeholderId = stakeholderA, Type = EngagementType.Meeting, OccurredAt = DateTime.UtcNow, Summary = "A", RecordedById = userA },
                new EngagementLog { ProjectId = projectB, StakeholderId = stakeholderB, Type = EngagementType.Email,   OccurredAt = DateTime.UtcNow, Summary = "B", RecordedById = userA });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.EngagementLogs.IgnoreQueryFilters().ToList();

        Assert.Equal(2, all.Count);
    }
}

using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the CommunicationItem tenant query
/// filter (T-S3-08 sweep, post T-S3-07). Same shape as
/// StakeholderFilterTests / EngagementLogFilterTests.
/// </summary>
public class CommunicationItemFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA,
        Guid userB, Guid projectA, Guid projectB)
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
        return (options, userA, userB, projectA, projectB);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_communication_items()
    {
        var (options, userA, userB, projectA, projectB)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CommunicationItems.AddRange(
                new CommunicationItem
                {
                    ProjectId = projectA, ItemType = "Monthly Project Report A",
                    Audience = "Client A", Frequency = CommunicationFrequency.Monthly,
                    Channel = CommunicationChannel.Email, OwnerId = userA,
                },
                new CommunicationItem
                {
                    ProjectId = projectB, ItemType = "Monthly Project Report B",
                    Audience = "Client B", Frequency = CommunicationFrequency.Monthly,
                    Channel = CommunicationChannel.Email, OwnerId = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.CommunicationItems.ToList();

        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
        Assert.Equal("Monthly Project Report A", list[0].ItemType);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_communication_items()
    {
        var (options, userA, userB, projectA, projectB)
            = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CommunicationItems.AddRange(
                new CommunicationItem
                {
                    ProjectId = projectA, ItemType = "A item", Audience = "A aud",
                    Frequency = CommunicationFrequency.Weekly, Channel = CommunicationChannel.Meeting,
                    OwnerId = userA,
                },
                new CommunicationItem
                {
                    ProjectId = projectB, ItemType = "B item", Audience = "B aud",
                    Frequency = CommunicationFrequency.Weekly, Channel = CommunicationChannel.Meeting,
                    OwnerId = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.CommunicationItems.IgnoreQueryFilters().ToList();

        Assert.Equal(2, all.Count);
    }
}

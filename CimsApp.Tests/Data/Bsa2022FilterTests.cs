using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural test for the new tenant-scoped entities
/// added in S10: <see cref="GatewayPackage"/> + <see
/// cref="MandatoryOccurrenceReport"/>. Model-level coverage is in
/// <see cref="CimsDbContextTenantFilterTests"/>.
/// </summary>
public class Bsa2022FilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA, Guid userB,
        Guid projectA, Guid projectB) SeedTwoTenants(string dbName)
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
    public void Tenant_A_sees_only_its_own_GatewayPackages_and_MORs()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.GatewayPackages.AddRange(
                new GatewayPackage { ProjectId = projectA, Number = "GW1-0001", Type = GatewayType.Gateway1, Title = "A G1", State = GatewayPackageState.Drafting, CreatedById = userA },
                new GatewayPackage { ProjectId = projectB, Number = "GW1-0001", Type = GatewayType.Gateway1, Title = "B G1", State = GatewayPackageState.Drafting, CreatedById = userB });
            seed.MandatoryOccurrenceReports.AddRange(
                new MandatoryOccurrenceReport { ProjectId = projectA, Number = "MOR-0001", Title = "A MOR", Description = "x", Severity = MorSeverity.Low, OccurredAt = DateTime.UtcNow, ReporterId = userA },
                new MandatoryOccurrenceReport { ProjectId = projectB, Number = "MOR-0001", Title = "B MOR", Description = "x", Severity = MorSeverity.Low, OccurredAt = DateTime.UtcNow, ReporterId = userB });
            seed.SaveChanges();
        }

        using var asA = OpenAs(options, OrgA, userA);
        Assert.Single(asA.GatewayPackages.ToList());
        Assert.Equal("A G1", asA.GatewayPackages.Single().Title);
        Assert.Single(asA.MandatoryOccurrenceReports.ToList());
        Assert.Equal("A MOR", asA.MandatoryOccurrenceReports.Single().Title);

        // SuperAdmin / IgnoreQueryFilters sees both.
        Assert.Equal(2, asA.GatewayPackages.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.MandatoryOccurrenceReports.IgnoreQueryFilters().Count());
    }
}

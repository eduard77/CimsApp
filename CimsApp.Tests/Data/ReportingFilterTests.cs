using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural test for the only tenant-scoped entity
/// added in S7: <see cref="CustomReportDefinition"/> (T-S7-05).
/// Dashboards / MPR / KPI are read-only with no new entities, so
/// they're already covered by the per-existing-entity filters.
/// Model-level coverage is in
/// <see cref="CimsDbContextTenantFilterTests"/>; this fills in
/// the per-row data-layer check.
/// </summary>
public class ReportingFilterTests
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
    public void Tenant_A_sees_only_its_own_custom_report_definitions()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CustomReportDefinitions.AddRange(
                new CustomReportDefinition
                {
                    ProjectId = projectA, Name = "A's saved query",
                    EntityType = CustomReportEntityType.Rfi,
                    FilterJson = "{}", ColumnsJson = "[]",
                    CreatedById = userA,
                },
                new CustomReportDefinition
                {
                    ProjectId = projectB, Name = "B's saved query",
                    EntityType = CustomReportEntityType.Risk,
                    FilterJson = "{}", ColumnsJson = "[]",
                    CreatedById = userB,
                });
            seed.SaveChanges();
        }

        using var asA = OpenAs(options, OrgA, userA);
        var list = asA.CustomReportDefinitions.ToList();
        Assert.Single(list);
        Assert.Equal("A's saved query", list[0].Name);

        // Sanity check: SuperAdmin / IgnoreQueryFilters sees both.
        Assert.Equal(2, asA.CustomReportDefinitions.IgnoreQueryFilters().Count());
    }
}

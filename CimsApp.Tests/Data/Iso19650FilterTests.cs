using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural test for the two new tenant-scoped entities
/// added in S9: <see cref="MidpEntry"/> and <see cref="TidpEntry"/>
/// (T-S9-05 / T-S9-06). Model-level coverage is in
/// <see cref="CimsDbContextTenantFilterTests"/>; this is the
/// per-row data-layer cross-tenant check.
/// </summary>
public class Iso19650FilterTests
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
    public void Tenant_A_sees_only_its_own_MIDP_and_TIDP_rows()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        Guid midpA, midpB;
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            midpA = Guid.NewGuid();
            midpB = Guid.NewGuid();
            seed.MidpEntries.AddRange(
                new MidpEntry { Id = midpA, ProjectId = projectA, Title = "A's MIDP", DueDate = DateTime.UtcNow, OwnerId = userA },
                new MidpEntry { Id = midpB, ProjectId = projectB, Title = "B's MIDP", DueDate = DateTime.UtcNow, OwnerId = userB });
            seed.TidpEntries.AddRange(
                new TidpEntry { ProjectId = projectA, MidpEntryId = midpA, TeamName = "A-Team", DueDate = DateTime.UtcNow },
                new TidpEntry { ProjectId = projectB, MidpEntryId = midpB, TeamName = "B-Team", DueDate = DateTime.UtcNow });
            seed.SaveChanges();
        }

        using var asA = OpenAs(options, OrgA, userA);
        var midps = asA.MidpEntries.ToList();
        var tidps = asA.TidpEntries.ToList();
        Assert.Single(midps);
        Assert.Equal("A's MIDP", midps[0].Title);
        Assert.Single(tidps);
        Assert.Equal("A-Team", tidps[0].TeamName);

        // SuperAdmin / IgnoreQueryFilters sees both.
        Assert.Equal(2, asA.MidpEntries.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.TidpEntries.IgnoreQueryFilters().Count());
    }
}

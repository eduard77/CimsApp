using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the RBS (Risk Breakdown Structure)
/// tenant query filter (T-S2-02). The model-level check in
/// <see cref="CimsDbContextTenantFilterTests"/> verifies a query filter
/// is registered on <see cref="RiskCategory"/>; this class proves the
/// filter actually scopes reads at runtime, including across the
/// hierarchical Parent/Children edge.
///
/// Same shape as <see cref="CostBreakdownItemFilterTests"/> from S1 —
/// the two entities share an identical hierarchy / per-project pattern.
/// </summary>
public class RiskCategoryFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA, Guid userB, Guid projectA, Guid projectB)
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
            OrganisationId = OrgA,
            UserId         = userA,
            GlobalRole     = UserRole.SuperAdmin,
        };
        using var seed = new CimsDbContext(options, seedTenant);
        seed.Organisations.AddRange(
            new Organisation { Id = OrgA, Name = "Tenant A", Code = "TA" },
            new Organisation { Id = OrgB, Name = "Tenant B", Code = "TB" });
        seed.Users.AddRange(
            new User
            {
                Id = userA, Email = $"a-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "A", LastName = "User",
                OrganisationId = OrgA,
            },
            new User
            {
                Id = userB, Email = $"b-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "B", LastName = "User",
                OrganisationId = OrgB,
            });
        seed.Projects.AddRange(
            new Project
            {
                Id = projectA, Name = "Project A", Code = "PA",
                AppointingPartyId = OrgA, Currency = "GBP",
            },
            new Project
            {
                Id = projectB, Name = "Project B", Code = "PB",
                AppointingPartyId = OrgB, Currency = "GBP",
            });
        seed.SaveChanges();
        return (options, userA, userB, projectA, projectB);
    }

    private static CimsDbContext OpenAs(DbContextOptions<CimsDbContext> options, Guid orgId, Guid userId) =>
        new(options, new StubTenantContext { OrganisationId = orgId, UserId = userId });

    [Fact]
    public void Tenant_A_sees_only_its_own_RBS_categories()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(dbName);

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.RiskCategories.AddRange(
                new RiskCategory { ProjectId = projectA, Code = "1", Name = "A Technical" },
                new RiskCategory { ProjectId = projectB, Code = "1", Name = "B Technical" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.RiskCategories.ToList();

        Assert.Single(items);
        Assert.Equal(projectA, items[0].ProjectId);
        Assert.Equal("A Technical", items[0].Name);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_RBS_categories()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(dbName);

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.RiskCategories.AddRange(
                new RiskCategory { ProjectId = projectA, Code = "1", Name = "A Technical" },
                new RiskCategory { ProjectId = projectB, Code = "1", Name = "B Technical" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.RiskCategories.IgnoreQueryFilters().ToList();

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Hierarchical_RBS_under_tenant_A_remain_visible()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, _) = SeedTwoTenants(dbName);

        Guid technicalId;
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var technical = new RiskCategory { ProjectId = projectA, Code = "1", Name = "Technical" };
            seed.RiskCategories.Add(technical);
            seed.SaveChanges();
            technicalId = technical.Id;

            seed.RiskCategories.AddRange(
                new RiskCategory { ProjectId = projectA, ParentId = technicalId, Code = "1.1", Name = "Design" },
                new RiskCategory { ProjectId = projectA, ParentId = technicalId, Code = "1.2", Name = "Construction methodology" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.RiskCategories.OrderBy(r => r.Code).ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal("1",   items[0].Code);
        Assert.Equal("1.1", items[1].Code);
        Assert.Equal("1.2", items[2].Code);
        Assert.Null(items[0].ParentId);
        Assert.Equal(technicalId, items[1].ParentId);
        Assert.Equal(technicalId, items[2].ParentId);
    }
}

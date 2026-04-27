using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the CBS tenant query filter
/// (T-S1-02). The model-level check in
/// <see cref="CimsDbContextTenantFilterTests"/> already verifies a
/// query filter is registered on <see cref="CostBreakdownItem"/>;
/// this class proves the filter actually scopes reads at runtime,
/// including across the hierarchical Parent/Children edge.
/// </summary>
public class CostBreakdownItemFilterTests
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
    public void Tenant_A_sees_only_its_own_CBS_lines()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(dbName);

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CostBreakdownItems.AddRange(
                new CostBreakdownItem { ProjectId = projectA, Code = "1", Name = "A root" },
                new CostBreakdownItem { ProjectId = projectB, Code = "1", Name = "B root" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.CostBreakdownItems.ToList();

        Assert.Single(items);
        Assert.Equal(projectA, items[0].ProjectId);
        Assert.Equal("A root", items[0].Name);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_CBS_lines()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(dbName);

        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CostBreakdownItems.AddRange(
                new CostBreakdownItem { ProjectId = projectA, Code = "1", Name = "A root" },
                new CostBreakdownItem { ProjectId = projectB, Code = "1", Name = "B root" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.CostBreakdownItems.IgnoreQueryFilters().ToList();

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Hierarchical_CBS_lines_under_tenant_A_remain_visible()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, _, projectA, _) = SeedTwoTenants(dbName);

        Guid rootId;
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var root = new CostBreakdownItem { ProjectId = projectA, Code = "1", Name = "A root" };
            seed.CostBreakdownItems.Add(root);
            seed.SaveChanges();
            rootId = root.Id;

            seed.CostBreakdownItems.AddRange(
                new CostBreakdownItem { ProjectId = projectA, ParentId = rootId, Code = "1.1", Name = "A child 1" },
                new CostBreakdownItem { ProjectId = projectA, ParentId = rootId, Code = "1.2", Name = "A child 2" });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var items = db.CostBreakdownItems.OrderBy(i => i.Code).ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal("1",   items[0].Code);
        Assert.Equal("1.1", items[1].Code);
        Assert.Equal("1.2", items[2].Code);
        Assert.Null(items[0].ParentId);
        Assert.Equal(rootId, items[1].ParentId);
        Assert.Equal(rootId, items[2].ParentId);
    }
}

using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the Risk tenant query filter
/// (T-S2-03). The model-level check in
/// <see cref="CimsDbContextTenantFilterTests"/> verifies a query filter
/// is registered on <see cref="Risk"/>; this class proves the filter
/// scopes reads at runtime and that the optional Category / Owner
/// navigations work through it.
/// </summary>
public class RiskFilterTests
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
    public void Tenant_A_sees_only_its_own_risks()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Risks.AddRange(
                new Risk { ProjectId = projectA, Title = "A risk", Probability = 3, Impact = 4, Score = 12 },
                new Risk { ProjectId = projectB, Title = "B risk", Probability = 2, Impact = 5, Score = 10, OwnerId = userB });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var risks = db.Risks.ToList();

        Assert.Single(risks);
        Assert.Equal(projectA, risks[0].ProjectId);
        Assert.Equal("A risk", risks[0].Title);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_risks()
    {
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Risks.AddRange(
                new Risk { ProjectId = projectA, Title = "A", Probability = 1, Impact = 1, Score = 1 },
                new Risk { ProjectId = projectB, Title = "B", Probability = 5, Impact = 5, Score = 25 });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.Risks.IgnoreQueryFilters().ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Risk_with_Category_and_Owner_navigations_eager_load()
    {
        var (options, userA, _, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());

        Guid categoryId;
        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var cat = new RiskCategory { ProjectId = projectA, Code = "1", Name = "Technical" };
            seed.RiskCategories.Add(cat);
            seed.SaveChanges();
            categoryId = cat.Id;

            seed.Risks.Add(new Risk
            {
                ProjectId         = projectA,
                CategoryId        = categoryId,
                Title             = "Foundation design risk",
                Probability       = 4,
                Impact            = 5,
                Score             = 20,
                OwnerId           = userA,
                Status            = RiskStatus.Active,
                ResponseStrategy  = ResponseStrategy.Mitigate,
                ResponsePlan      = "Engage geotechnical consultant",
                ContingencyAmount = 50_000m,
            });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var risk = db.Risks
            .Include(r => r.Category)
            .Include(r => r.Owner)
            .Single();

        Assert.Equal("Foundation design risk", risk.Title);
        Assert.Equal(20, risk.Score);
        Assert.Equal(RiskStatus.Active, risk.Status);
        Assert.Equal(ResponseStrategy.Mitigate, risk.ResponseStrategy);
        Assert.Equal(50_000m, risk.ContingencyAmount);
        Assert.NotNull(risk.Category);
        Assert.Equal("Technical", risk.Category.Name);
        Assert.NotNull(risk.Owner);
        Assert.Equal(userA, risk.Owner.Id);
    }

    [Fact]
    public void Risk_without_Category_or_Owner_remains_queryable()
    {
        // Defensive: at registration time Category and Owner are
        // nullable. A bare-identity risk should still come back from
        // the filter.
        var (options, userA, _, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Risks.Add(new Risk
            {
                ProjectId   = projectA,
                Title       = "Bare risk",
                Probability = 2,
                Impact      = 2,
                Score       = 4,
            });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var risk = db.Risks.Single();

        Assert.Null(risk.CategoryId);
        Assert.Null(risk.OwnerId);
        Assert.Null(risk.ResponseStrategy);
        Assert.Equal(RiskStatus.Identified, risk.Status);
    }
}

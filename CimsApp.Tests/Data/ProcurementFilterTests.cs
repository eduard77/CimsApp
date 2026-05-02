using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the S6 procurement-domain tenant
/// query filters (T-S6-09). All 8 procurement entities
/// (ProcurementStrategy / TenderPackage / Tender /
/// EvaluationCriterion / EvaluationScore / Contract / EarlyWarning
/// / CompensationEvent) use the same `Project.AppointingPartyId
/// == _tenant.OrganisationId` filter shape — model-level coverage
/// is in <see cref="CimsDbContextTenantFilterTests"/>; per-service
/// cross-tenant 404 tests live in each procurement service's test
/// file. This consolidated sweep adds runtime data-layer
/// verification for two representative entities (TenderPackage at
/// the top of the chain, CompensationEvent at the bottom) — the
/// other six follow identically and are covered by the model-level
/// + service-level guards. Pragmatic departure from the per-entity
/// XxxFilterTests file pattern because S6 has 8 entities all using
/// the same shape.
/// </summary>
public class ProcurementFilterTests
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

    // ── TenderPackage (top of the procurement chain) ────────────────

    [Fact]
    public void Tenant_A_sees_only_its_own_tender_packages()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.TenderPackages.AddRange(
                new TenderPackage
                {
                    ProjectId = projectA, Number = "TP-0001", Name = "A pkg",
                    EstimatedValue = 100_000m, State = TenderPackageState.Issued,
                    CreatedById = userA,
                },
                new TenderPackage
                {
                    ProjectId = projectB, Number = "TP-0001", Name = "B pkg",
                    EstimatedValue = 200_000m, State = TenderPackageState.Draft,
                    CreatedById = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.TenderPackages.ToList();
        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
        Assert.Equal("A pkg",  list[0].Name);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_tender_packages()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.TenderPackages.AddRange(
                new TenderPackage { ProjectId = projectA, Number = "TP-0001", Name = "A", EstimatedValue = 1m, State = TenderPackageState.Draft, CreatedById = userA },
                new TenderPackage { ProjectId = projectB, Number = "TP-0001", Name = "B", EstimatedValue = 1m, State = TenderPackageState.Draft, CreatedById = userB });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        Assert.Equal(2, db.TenderPackages.IgnoreQueryFilters().Count());
    }

    // ── CompensationEvent (bottom of the procurement chain) ─────────

    [Fact]
    public void Tenant_A_sees_only_its_own_compensation_events()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            // Need a TenderPackage + Tender + Contract chain per tenant
            // because CompensationEvent FK-references Contract.
            var pkgA = Guid.NewGuid(); var pkgB = Guid.NewGuid();
            var tA = Guid.NewGuid(); var tB = Guid.NewGuid();
            var cA = Guid.NewGuid(); var cB = Guid.NewGuid();
            seed.TenderPackages.AddRange(
                new TenderPackage { Id = pkgA, ProjectId = projectA, Number = "TP-0001", Name = "A", EstimatedValue = 1m, State = TenderPackageState.Closed, AwardedTenderId = tA, CreatedById = userA },
                new TenderPackage { Id = pkgB, ProjectId = projectB, Number = "TP-0001", Name = "B", EstimatedValue = 1m, State = TenderPackageState.Closed, AwardedTenderId = tB, CreatedById = userB });
            seed.Tenders.AddRange(
                new Tender { Id = tA, ProjectId = projectA, TenderPackageId = pkgA, BidderName = "A bidder", BidAmount = 1m, State = TenderState.Awarded, CreatedById = userA },
                new Tender { Id = tB, ProjectId = projectB, TenderPackageId = pkgB, BidderName = "B bidder", BidAmount = 1m, State = TenderState.Awarded, CreatedById = userB });
            seed.Contracts.AddRange(
                new Contract { Id = cA, ProjectId = projectA, Number = "CON-0001", TenderPackageId = pkgA, AwardedTenderId = tA, ContractorName = "A", ContractValue = 1m, ContractForm = ContractForm.Nec4OptionA, State = ContractState.Active, AwardedById = userA },
                new Contract { Id = cB, ProjectId = projectB, Number = "CON-0001", TenderPackageId = pkgB, AwardedTenderId = tB, ContractorName = "B", ContractValue = 1m, ContractForm = ContractForm.Nec4OptionA, State = ContractState.Active, AwardedById = userB });
            seed.CompensationEvents.AddRange(
                new CompensationEvent { ProjectId = projectA, ContractId = cA, Number = "CE-0001", Title = "A CE", State = CompensationEventState.Notified, NotifiedById = userA },
                new CompensationEvent { ProjectId = projectB, ContractId = cB, Number = "CE-0001", Title = "B CE", State = CompensationEventState.Notified, NotifiedById = userB });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.CompensationEvents.ToList();
        Assert.Single(list);
        Assert.Equal(projectA, list[0].ProjectId);
        Assert.Equal("A CE",   list[0].Title);

        // Sanity: IgnoreQueryFilters returns both.
        Assert.Equal(2, db.CompensationEvents.IgnoreQueryFilters().Count());
    }
}

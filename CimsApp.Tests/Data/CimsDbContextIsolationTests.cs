using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime multi-tenant isolation behavioural tests (T-S0-04).
/// Companion to <see cref="CimsDbContextTenantFilterTests"/> which only
/// verifies that the model registers a query filter; this test class
/// proves the filter actually scopes reads at runtime.
///
/// Each test uses <c>UseInMemoryDatabase(Guid.NewGuid().ToString())</c>
/// for a hermetic per-test DB, and seeds two tenants (A and B) with
/// one User and one Project each. The stub tenant flips between A
/// and B to demonstrate that data shipped through the same context
/// connection is partitioned correctly.
/// </summary>
public class CimsDbContextIsolationTests
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

        // Seed under a SuperAdmin tenant so writes bypass the read-side
        // filter (the filter is a WHERE on read, but EF still respects
        // the SaveChanges interceptor's tenant context for AuditLog).
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA,
            UserId = userA,
            GlobalRole = UserRole.SuperAdmin,
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
    public void Tenant_A_sees_only_its_own_project()
    {
        var (options, userA, _, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());
        using var db = OpenAs(options, OrgA, userA);

        var projects = db.Projects.ToList();

        Assert.Single(projects);
        Assert.Equal(projectA, projects[0].Id);
        Assert.Equal(OrgA, projects[0].AppointingPartyId);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_projects()
    {
        var (options, userA, _, _, _) = SeedTwoTenants(Guid.NewGuid().ToString());
        using var db = OpenAs(options, OrgA, userA);

        var projects = db.Projects.IgnoreQueryFilters().ToList();

        Assert.Equal(2, projects.Count);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_user()
    {
        var (options, userA, _, _, _) = SeedTwoTenants(Guid.NewGuid().ToString());
        using var db = OpenAs(options, OrgA, userA);

        var users = db.Users.ToList();

        Assert.Single(users);
        Assert.Equal(userA, users[0].Id);
        Assert.Equal(OrgA, users[0].OrganisationId);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_documents()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(dbName);

        // Seed one Document per tenant, each under its own project.
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA,
            UserId = userA,
            GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Documents.AddRange(
                new Document
                {
                    ProjectId = projectA, ProjectCode = "PA", Originator = "ORG",
                    DocType = "RP", Number = "0001", DocumentNumber = "PA-ORG-RP-0001",
                    Title = "A doc", CreatorId = userA,
                },
                new Document
                {
                    ProjectId = projectB, ProjectCode = "PB", Originator = "ORG",
                    DocType = "RP", Number = "0001", DocumentNumber = "PB-ORG-RP-0001",
                    Title = "B doc", CreatorId = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var docs = db.Documents.ToList();

        Assert.Single(docs);
        Assert.Equal(projectA, docs[0].ProjectId);
        Assert.Equal("PA-ORG-RP-0001", docs[0].DocumentNumber);
    }

    // ── T-S1-13 sweep: per-entity runtime isolation for S1 cost-domain
    //    additions. Each test mirrors the documents pattern above —
    //    seed one row per tenant, open as tenant A, expect the
    //    A-side row only. The CostBreakdownItem case is already
    //    covered by CostBreakdownItemFilterTests; the five S1 entities
    //    below complete the sweep.

    [Fact]
    public void Tenant_A_sees_only_its_own_commitments()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        Guid lineA, lineB;
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var lA = new CostBreakdownItem { ProjectId = projectA, Code = "1", Name = "A line" };
            var lB = new CostBreakdownItem { ProjectId = projectB, Code = "1", Name = "B line" };
            seed.CostBreakdownItems.AddRange(lA, lB);
            seed.SaveChanges();
            lineA = lA.Id; lineB = lB.Id;

            seed.Commitments.AddRange(
                new Commitment
                {
                    ProjectId = projectA, CostBreakdownItemId = lineA,
                    Type = CommitmentType.PO, Reference = "PO-A-1",
                    Counterparty = "Acme A", Amount = 100m,
                },
                new Commitment
                {
                    ProjectId = projectB, CostBreakdownItemId = lineB,
                    Type = CommitmentType.Subcontract, Reference = "SC-B-1",
                    Counterparty = "Beta B", Amount = 200m,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var rows = db.Commitments.ToList();

        Assert.Single(rows);
        Assert.Equal(projectA, rows[0].ProjectId);
        Assert.Equal("PO-A-1", rows[0].Reference);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_cost_periods()
    {
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.CostPeriods.AddRange(
                new CostPeriod
                {
                    ProjectId = projectA, Label = "A April",
                    StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                },
                new CostPeriod
                {
                    ProjectId = projectB, Label = "B April",
                    StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var rows = db.CostPeriods.ToList();

        Assert.Single(rows);
        Assert.Equal(projectA, rows[0].ProjectId);
        Assert.Equal("A April", rows[0].Label);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_actual_costs()
    {
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            // Both an ActualCost write and the supporting CBS line +
            // CostPeriod must be seeded under the right project so the
            // FK chain is honest.
            var lineA = new CostBreakdownItem { ProjectId = projectA, Code = "1", Name = "A line" };
            var lineB = new CostBreakdownItem { ProjectId = projectB, Code = "1", Name = "B line" };
            var perA  = new CostPeriod
            {
                ProjectId = projectA, Label = "A Apr",
                StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            };
            var perB = new CostPeriod
            {
                ProjectId = projectB, Label = "B Apr",
                StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.CostBreakdownItems.AddRange(lineA, lineB);
            seed.CostPeriods.AddRange(perA, perB);
            seed.SaveChanges();

            seed.ActualCosts.AddRange(
                new ActualCost
                {
                    ProjectId = projectA, CostBreakdownItemId = lineA.Id,
                    PeriodId  = perA.Id, Amount = 50m, Reference = "INV-A-1",
                },
                new ActualCost
                {
                    ProjectId = projectB, CostBreakdownItemId = lineB.Id,
                    PeriodId  = perB.Id, Amount = 60m, Reference = "INV-B-1",
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var rows = db.ActualCosts.ToList();

        Assert.Single(rows);
        Assert.Equal(projectA, rows[0].ProjectId);
        Assert.Equal("INV-A-1", rows[0].Reference);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_variations()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Variations.AddRange(
                new Variation
                {
                    ProjectId = projectA, VariationNumber = "VAR-0001",
                    Title = "A change", State = VariationState.Raised,
                    RaisedById = userA,
                },
                new Variation
                {
                    ProjectId = projectB, VariationNumber = "VAR-0001",
                    Title = "B change", State = VariationState.Raised,
                    RaisedById = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var rows = db.Variations.ToList();

        Assert.Single(rows);
        Assert.Equal(projectA, rows[0].ProjectId);
        Assert.Equal("A change", rows[0].Title);
    }

    [Fact]
    public void Tenant_A_sees_only_its_own_payment_certificates()
    {
        var (options, userA, _, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            var perA = new CostPeriod
            {
                ProjectId = projectA, Label = "A Apr",
                StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            };
            var perB = new CostPeriod
            {
                ProjectId = projectB, Label = "B Apr",
                StartDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                EndDate   = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.CostPeriods.AddRange(perA, perB);
            seed.SaveChanges();

            seed.PaymentCertificates.AddRange(
                new PaymentCertificate
                {
                    ProjectId = projectA, PeriodId = perA.Id,
                    CertificateNumber = "PC-0001",
                    State = PaymentCertificateState.Draft,
                    CumulativeValuation = 100m, RetentionPercent = 3m,
                },
                new PaymentCertificate
                {
                    ProjectId = projectB, PeriodId = perB.Id,
                    CertificateNumber = "PC-0001",
                    State = PaymentCertificateState.Draft,
                    CumulativeValuation = 200m, RetentionPercent = 3m,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var rows = db.PaymentCertificates.ToList();

        Assert.Single(rows);
        Assert.Equal(projectA, rows[0].ProjectId);
        Assert.Equal(100m, rows[0].CumulativeValuation);
    }
}

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
}

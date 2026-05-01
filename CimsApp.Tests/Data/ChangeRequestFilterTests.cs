using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural tests for the ChangeRequest tenant query
/// filter (T-S5-02). Same shape as the other XxxFilterTests files:
/// two tenants, prove a tenant-A session sees only its own
/// change requests and IgnoreQueryFilters returns both.
/// </summary>
public class ChangeRequestFilterTests
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
    public void Tenant_A_sees_only_its_own_change_requests()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.ChangeRequests.AddRange(
                new ChangeRequest
                {
                    ProjectId = projectA, Number = "CR-0001",
                    Title = "Add basement parking",
                    Category = ChangeRequestCategory.Scope,
                    BsaCategory = BsaHrbCategory.NotApplicable,
                    State = ChangeRequestState.Raised,
                    RaisedById = userA,
                },
                new ChangeRequest
                {
                    ProjectId = projectB, Number = "CR-0001",
                    Title = "Tenant B change",
                    Category = ChangeRequestCategory.Cost,
                    BsaCategory = BsaHrbCategory.NotApplicable,
                    State = ChangeRequestState.Raised,
                    RaisedById = userB,
                });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var list = db.ChangeRequests.ToList();
        Assert.Single(list);
        Assert.Equal(projectA,                   list[0].ProjectId);
        Assert.Equal("Add basement parking",     list[0].Title);
    }

    [Fact]
    public void IgnoreQueryFilters_returns_both_tenants_change_requests()
    {
        var (options, userA, userB, projectA, projectB) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.ChangeRequests.AddRange(
                new ChangeRequest { ProjectId = projectA, Number = "CR-0001", Title = "A", Category = ChangeRequestCategory.Scope, RaisedById = userA },
                new ChangeRequest { ProjectId = projectB, Number = "CR-0001", Title = "B", Category = ChangeRequestCategory.Cost, RaisedById = userB });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var all = db.ChangeRequests.IgnoreQueryFilters().ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ChangeRequest_with_optional_audit_fields_round_trips_null()
    {
        // Defensive: most state-transition audit fields are nullable
        // until the corresponding transition fires. Confirm they
        // round-trip null without losing the row.
        var (options, userA, _, projectA, _) = SeedTwoTenants(Guid.NewGuid().ToString());

        var seedTenant = new StubTenantContext { OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.ChangeRequests.Add(new ChangeRequest
            {
                ProjectId   = projectA,
                Number      = "CR-0001",
                Title       = "Fresh request",
                Category    = ChangeRequestCategory.Quality,
                BsaCategory = BsaHrbCategory.A,
                State       = ChangeRequestState.Raised,
                RaisedById  = userA,
            });
            seed.SaveChanges();
        }

        using var db = OpenAs(options, OrgA, userA);
        var c = db.ChangeRequests.Single();
        Assert.Null(c.AssessedById);
        Assert.Null(c.AssessedAt);
        Assert.Null(c.AssessmentNote);
        Assert.Null(c.DecisionById);
        Assert.Null(c.DecisionAt);
        Assert.Null(c.DecisionNote);
        Assert.Null(c.ImplementedAt);
        Assert.Null(c.ClosedAt);
        Assert.Null(c.GeneratedVariationId);
        Assert.Equal(BsaHrbCategory.A, c.BsaCategory);
        Assert.Equal(ChangeRequestState.Raised, c.State);
    }
}

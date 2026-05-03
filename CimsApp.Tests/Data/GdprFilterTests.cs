using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural test for the new tenant-scoped entities
/// added in S11. **Notably, 4 of 5 are org-scoped (filter shape
/// `OrganisationId == _tenant.OrganisationId`); 1 is project-
/// scoped (DPIA via Project.AppointingPartyId).** This test
/// exercises both filter shapes to catch the kickoff Top-3 risk
/// #2 (org-scoped vs project-scoped filter inversion).
/// </summary>
public class GdprFilterTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static (DbContextOptions<CimsDbContext> options, Guid userA, Guid userB,
        Guid projectA, Guid projectB) SeedTwoTenants(string dbName)
    {
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
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
    public void Tenant_A_sees_only_its_own_GDPR_rows_across_both_filter_shapes()
    {
        var (options, userA, userB, projectA, projectB) =
            SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            // Org-scoped entities: ROPA / SAR / breach log /
            // retention schedule. Filter shape is
            // OrganisationId == _tenant.OrganisationId.
            seed.RopaEntries.AddRange(
                new RopaEntry { OrganisationId = OrgA, Purpose = "A's ROPA", LawfulBasis = LawfulBasis.LegitimateInterest },
                new RopaEntry { OrganisationId = OrgB, Purpose = "B's ROPA", LawfulBasis = LawfulBasis.Contract });
            seed.SubjectAccessRequests.AddRange(
                new SubjectAccessRequest { OrganisationId = OrgA, Number = "SAR-0001", DataSubjectName = "A subject", RequestDescription = "x", State = SarState.Received, RequestedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddDays(30) },
                new SubjectAccessRequest { OrganisationId = OrgB, Number = "SAR-0001", DataSubjectName = "B subject", RequestDescription = "x", State = SarState.Received, RequestedAt = DateTime.UtcNow, DueAt = DateTime.UtcNow.AddDays(30) });
            seed.DataBreachLogs.AddRange(
                new DataBreachLog { OrganisationId = OrgA, Number = "BR-0001", Title = "A breach", Description = "x", Severity = BreachSeverity.Low, OccurredAt = DateTime.UtcNow, DiscoveredAt = DateTime.UtcNow, CreatedById = userA },
                new DataBreachLog { OrganisationId = OrgB, Number = "BR-0001", Title = "B breach", Description = "x", Severity = BreachSeverity.Low, OccurredAt = DateTime.UtcNow, DiscoveredAt = DateTime.UtcNow, CreatedById = userB });
            seed.RetentionSchedules.AddRange(
                new RetentionSchedule { OrganisationId = OrgA, DataCategory = "Project docs", RetentionPeriodMonths = 84, LawfulBasisForRetention = "Construction Act + 6-year limitation" },
                new RetentionSchedule { OrganisationId = OrgB, DataCategory = "Project docs", RetentionPeriodMonths = 84, LawfulBasisForRetention = "Construction Act + 6-year limitation" });
            // Project-scoped entity: DPIA. Filter shape is
            // Project.AppointingPartyId == _tenant.OrganisationId.
            seed.Dpias.AddRange(
                new DataProtectionImpactAssessment { ProjectId = projectA, Title = "A DPIA", Description = "x", CreatedById = userA },
                new DataProtectionImpactAssessment { ProjectId = projectB, Title = "B DPIA", Description = "x", CreatedById = userB });
            seed.SaveChanges();
        }

        using var asA = OpenAs(options, OrgA, userA);
        // Org-scoped checks.
        Assert.Single(asA.RopaEntries.ToList());
        Assert.Equal("A's ROPA", asA.RopaEntries.Single().Purpose);
        Assert.Single(asA.SubjectAccessRequests.ToList());
        Assert.Single(asA.DataBreachLogs.ToList());
        Assert.Single(asA.RetentionSchedules.ToList());
        // Project-scoped check (different filter shape).
        Assert.Single(asA.Dpias.ToList());
        Assert.Equal("A DPIA", asA.Dpias.Single().Title);

        // SuperAdmin / IgnoreQueryFilters sees both.
        Assert.Equal(2, asA.RopaEntries.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.SubjectAccessRequests.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.DataBreachLogs.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.RetentionSchedules.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.Dpias.IgnoreQueryFilters().Count());
    }
}

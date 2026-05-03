using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Data;

/// <summary>
/// Runtime behavioural test for the S12 Kaizen / Lessons Learned
/// entities. Mixed shape: 2 project-scoped (Improvement,
/// Opportunity), 1 org-scoped (LessonLearned cross-project
/// library).
/// </summary>
public class KaizenFilterTests
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
    public void Tenant_A_sees_only_its_own_Kaizen_rows_across_both_filter_shapes()
    {
        var (options, userA, userB, projectA, projectB) =
            SeedTwoTenants(Guid.NewGuid().ToString());
        var seedTenant = new StubTenantContext
        {
            OrganisationId = OrgA, UserId = userA, GlobalRole = UserRole.SuperAdmin,
        };
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.ImprovementRegisterEntries.AddRange(
                new ImprovementRegisterEntry { ProjectId = projectA, Number = "IMP-0001", Title = "A IMP", Description = "x", OwnerId = userA, CreatedById = userA },
                new ImprovementRegisterEntry { ProjectId = projectB, Number = "IMP-0001", Title = "B IMP", Description = "x", OwnerId = userB, CreatedById = userB });
            seed.LessonsLearned.AddRange(
                new LessonLearned { OrganisationId = OrgA, Title = "A lesson", Description = "x", RecordedById = userA },
                new LessonLearned { OrganisationId = OrgB, Title = "B lesson", Description = "x", RecordedById = userB });
            seed.OpportunitiesToImprove.AddRange(
                new OpportunityToImprove { ProjectId = projectA, Number = "OFI-0001", Title = "A OFI", Description = "x", RaisedById = userA },
                new OpportunityToImprove { ProjectId = projectB, Number = "OFI-0001", Title = "B OFI", Description = "x", RaisedById = userB });
            seed.SaveChanges();
        }

        using var asA = OpenAs(options, OrgA, userA);
        Assert.Single(asA.ImprovementRegisterEntries.ToList());
        Assert.Equal("A IMP", asA.ImprovementRegisterEntries.Single().Title);
        Assert.Single(asA.LessonsLearned.ToList());
        Assert.Equal("A lesson", asA.LessonsLearned.Single().Title);
        Assert.Single(asA.OpportunitiesToImprove.ToList());
        Assert.Equal("A OFI", asA.OpportunitiesToImprove.Single().Title);

        // SuperAdmin / IgnoreQueryFilters sees both.
        Assert.Equal(2, asA.ImprovementRegisterEntries.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.LessonsLearned.IgnoreQueryFilters().Count());
        Assert.Equal(2, asA.OpportunitiesToImprove.IgnoreQueryFilters().Count());
    }
}

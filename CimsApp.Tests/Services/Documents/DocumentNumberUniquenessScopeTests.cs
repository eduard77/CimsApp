using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Documents;

/// <summary>
/// Cross-tenant DocumentNumber uniqueness. The original schema
/// declared `HasIndex(d => d.DocumentNumber).IsUnique()` which
/// is GLOBALLY unique — but two tenants can each legitimately
/// have a Project with the same Code (Project.Code is per-tenant
/// unique via `(AppointingPartyId, Code)`), so two tenants can
/// each derive the same `DocumentNumber` from
/// `PROJ-ORIG-VOL-LVL-TYPE-ROLE-NNNN`.
///
/// Pre-fix: the second tenant's create would pass the
/// service-layer duplicate check (tenant filter hides the other
/// tenant's row) but explode at SaveChanges with a unique-index
/// violation — HTTP 500 to the user instead of a clean success.
///
/// Post-fix: the unique index is scoped to (ProjectId,
/// DocumentNumber) and the service-layer duplicate check
/// includes the projectId filter explicitly. Same-tenant
/// duplicates still rejected with the existing
/// `ConflictException`.
/// </summary>
public class DocumentNumberUniquenessScopeTests
{
    private static (DbContextOptions<CimsDbContext> options, Guid orgA, Guid orgB,
        Guid userA, Guid userB, Guid projectInA, Guid projectInB)
        BuildTwoTenantFixture()
    {
        var orgA       = Guid.NewGuid();
        var orgB       = Guid.NewGuid();
        var userA      = Guid.NewGuid();
        var userB      = Guid.NewGuid();
        var projectInA = Guid.NewGuid();
        var projectInB = Guid.NewGuid();

        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userA,
            GlobalRole     = UserRole.SuperAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Users.AddRange(
                new User { Id = userA, Email = $"a-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = orgA },
                new User { Id = userB, Email = $"b-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "B", LastName = "U", OrganisationId = orgB });
            // Both projects use Code "SHARED" — legitimate per the
            // (AppointingPartyId, Code) per-tenant uniqueness rule.
            seed.Projects.AddRange(
                new Project { Id = projectInA, Name = "A's", Code = "SHARED",
                    AppointingPartyId = orgA, Currency = "GBP" },
                new Project { Id = projectInB, Name = "B's", Code = "SHARED",
                    AppointingPartyId = orgB, Currency = "GBP" });
            seed.SaveChanges();
        }
        return (options, orgA, orgB, userA, userB, projectInA, projectInB);
    }

    private static CreateDocumentRequest NewRequest() => new(
        ProjectCode: "SHARED", Originator: "ORG",
        Volume: null, Level: null, DocType: "RP",
        Role: null, Number: 1,
        Title: "Same-numbered doc", Description: null,
        Type: DocumentType.Report, ContainerId: null, Tags: null);

    [Fact]
    public async Task Two_tenants_with_same_project_code_can_each_create_DOC_0001()
    {
        // Both tenants legitimately have a Project with Code
        // "SHARED" → both derive DocumentNumber
        // "SHARED-ORG-ZZ-ZZ-RP-XX-0001". Pre-fix the second
        // SaveChanges threw a unique-index violation. Post-fix
        // both succeed because the unique index is scoped to
        // (ProjectId, DocumentNumber).
        var (options, orgA, orgB, userA, userB, projectInA, projectInB) =
            BuildTwoTenantFixture();
        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        // Tenant A creates first.
        using (var dbA = new CimsDbContext(options, tenantA))
        {
            var svc = new DocumentsService(dbA, new AuditService(dbA));
            await svc.CreateAsync(projectInA, NewRequest(), userA, null, null);
        }

        // Tenant B creates the SAME-shaped DocumentNumber under
        // their own project — must succeed.
        using (var dbB = new CimsDbContext(options, tenantB))
        {
            var svc = new DocumentsService(dbB, new AuditService(dbB));
            await svc.CreateAsync(projectInB, NewRequest(), userB, null, null);
        }

        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userA,
            GlobalRole     = UserRole.SuperAdmin,
        };
        using var verify = new CimsDbContext(options, seedTenant);
        var rows = verify.Documents.IgnoreQueryFilters()
            .Where(d => d.DocumentNumber == "SHARED-ORG-ZZ-ZZ-RP-XX-0001")
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ProjectId == projectInA);
        Assert.Contains(rows, r => r.ProjectId == projectInB);
    }

    [Fact]
    public async Task Same_tenant_duplicate_DocumentNumber_still_throws_ConflictException()
    {
        // The narrowed scope must NOT widen the within-project
        // duplicate-prevention rule. A second create of the same
        // DocumentNumber on the SAME project still throws
        // ConflictException at the service layer.
        var (options, orgA, _, userA, _, projectInA, _) = BuildTwoTenantFixture();
        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };

        using var db = new CimsDbContext(options, tenantA);
        var svc = new DocumentsService(db, new AuditService(db));
        await svc.CreateAsync(projectInA, NewRequest(), userA, null, null);
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.CreateAsync(projectInA, NewRequest(), userA, null, null));
    }
}

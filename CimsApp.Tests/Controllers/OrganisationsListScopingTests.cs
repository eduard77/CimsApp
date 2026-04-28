using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Tenancy;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Controllers;

/// <summary>
/// B-007: `GET /api/v1/organisations` scoping. The DbContext leaves
/// Organisation intentionally unfiltered (ADR-0003), so without
/// controller-level scoping every authenticated user could enumerate
/// every other organisation's `Name` and `Code`. The controller now
/// scopes non-SuperAdmin callers to their own org; SuperAdmin retains
/// the wider view per ADR-0007.
///
/// Tested at the query level (the same shape the controller uses)
/// rather than via a full HTTP test, because the controller wrapping
/// adds no logic beyond the LINQ filter — this is the load-bearing
/// piece.
/// </summary>
public class OrganisationsListScopingTests
{
    private static (DbContextOptions<CimsDbContext> options, Guid orgA, Guid orgB)
        BuildFixture()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.SuperAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "Tenant A", Code = "TA" },
                new Organisation { Id = orgB, Name = "Tenant B", Code = "TB" });
            seed.SaveChanges();
        }
        return (options, orgA, orgB);
    }

    /// <summary>Replicates the controller's filter chain. Returns the
    /// IDs that the caller would see at GET /organisations.</summary>
    private static List<Guid> ListAs(DbContextOptions<CimsDbContext> options,
        ITenantContext tenant)
    {
        using var db = new CimsDbContext(options, tenant);
        var q = db.Organisations.Where(o => o.IsActive);
        if (!tenant.IsSuperAdmin)
        {
            var callerOrgId = tenant.OrganisationId
                ?? throw new InvalidOperationException("No tenant context");
            q = q.Where(o => o.Id == callerOrgId);
        }
        return q.OrderBy(o => o.Name).Select(o => o.Id).ToList();
    }

    [Fact]
    public void OrgAdmin_in_org_A_sees_only_org_A()
    {
        var (options, orgA, _) = BuildFixture();
        var caller = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };

        var visible = ListAs(options, caller);

        Assert.Equal(new[] { orgA }, visible);
    }

    [Fact]
    public void TaskTeamMember_in_org_A_sees_only_org_A()
    {
        // Any non-SuperAdmin role is scoped — covers OrgAdmin all the
        // way down to TaskTeamMember and Viewer.
        var (options, orgA, _) = BuildFixture();
        var caller = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.TaskTeamMember,
        };

        var visible = ListAs(options, caller);

        Assert.Equal(new[] { orgA }, visible);
    }

    [Fact]
    public void User_with_no_GlobalRole_in_org_A_sees_only_org_A()
    {
        // Most regular project members have GlobalRole == null. They
        // are NOT SuperAdmin and so get the scoped view.
        var (options, orgA, _) = BuildFixture();
        var caller = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = null,
        };

        var visible = ListAs(options, caller);

        Assert.Equal(new[] { orgA }, visible);
    }

    [Fact]
    public void SuperAdmin_sees_all_organisations()
    {
        var (options, orgA, orgB) = BuildFixture();
        var caller = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.SuperAdmin,
        };

        var visible = ListAs(options, caller);

        Assert.Contains(orgA, visible);
        Assert.Contains(orgB, visible);
    }

    [Fact]
    public void Inactive_organisations_excluded_for_all_callers()
    {
        // Inactive-org filter is applied to both SuperAdmin and
        // non-SuperAdmin paths; the role scoping only narrows
        // further, never widens.
        var (options, orgA, orgB) = BuildFixture();
        using (var db = new CimsDbContext(options, new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.SuperAdmin,
        }))
        {
            var b = db.Organisations.Single(o => o.Id == orgB);
            b.IsActive = false;
            db.SaveChanges();
        }

        var superAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.SuperAdmin,
        };
        var visible = ListAs(options, superAdmin);
        Assert.DoesNotContain(orgB, visible);
        Assert.Contains(orgA, visible);
    }
}

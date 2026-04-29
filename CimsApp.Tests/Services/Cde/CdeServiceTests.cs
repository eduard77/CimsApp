using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Cde;

/// <summary>
/// Direct behavioural coverage for `CdeService` — previously
/// exercised only indirectly through tenant-filter sweep tests
/// (Section: CdeContainer in CimsDbContextIsolationTests). Adds
/// per-method coverage for `CreateContainerAsync` (uppercases
/// ISO 19650 codes, emits `cde.container_created` audit) and
/// `ListContainersAsync` (active filter + tenant scope).
/// </summary>
public class CdeServiceTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant,
        Guid orgId, Guid userId, Guid projectId) BuildFixture()
    {
        var orgId     = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId,
            UserId         = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var interceptor = new AuditInterceptor(tenant, httpAccessor: null);
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;

        using var seed = new CimsDbContext(options, tenant);
        seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
        seed.Users.Add(new User
        {
            Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
            PasswordHash = "x", FirstName = "T", LastName = "U",
            OrganisationId = orgId,
        });
        seed.Projects.Add(new Project
        {
            Id = projectId, Name = "Project", Code = "PR1",
            AppointingPartyId = orgId, Currency = "GBP",
        });
        seed.SaveChanges();
        return (options, tenant, orgId, userId, projectId);
    }

    // ── CreateContainerAsync ────────────────────────────────────────────────

    [Fact]
    public async Task CreateContainer_writes_row_with_uppercased_ISO_19650_codes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid containerId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CdeService(db, new AuditService(db));
            var c = await svc.CreateContainerAsync(projectId,
                new CreateContainerRequest(
                    Name:        "WIP — Architecture",
                    Originator:  "abc",         // 3-char originator code, lowercase
                    Volume:      "v1",          // optional, lowercase
                    Level:       "l2",
                    Type:        "rp",          // 2-char doctype, lowercase
                    Discipline:  "ar",
                    Description: "Live working area for architectural work."),
                userId, ip: "203.0.113.1", ua: "ua-test");
            containerId = c.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var stored = verify.CdeContainers.Single(c => c.Id == containerId);
        // ISO 19650 codes uppercase per the service contract; Name
        // and Description preserved verbatim.
        Assert.Equal("ABC", stored.Originator);
        Assert.Equal("V1",  stored.Volume);
        Assert.Equal("L2",  stored.Level);
        Assert.Equal("RP",  stored.Type);
        Assert.Equal("AR",  stored.Discipline);
        Assert.Equal("WIP — Architecture", stored.Name);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task CreateContainer_handles_null_optional_codes()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using var db = new CimsDbContext(options, tenant);
        var svc = new CdeService(db, new AuditService(db));
        var c = await svc.CreateContainerAsync(projectId,
            new CreateContainerRequest(
                Name: "Minimal", Originator: "ABC",
                Volume: null, Level: null, Type: "RP",
                Discipline: null, Description: null),
            userId, null, null);
        Assert.Null(c.Volume);
        Assert.Null(c.Level);
        Assert.Null(c.Discipline);
        Assert.Null(c.Description);
        Assert.Equal("RP", c.Type);
    }

    [Fact]
    public async Task CreateContainer_emits_cde_container_created_audit()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid containerId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CdeService(db, new AuditService(db));
            var c = await svc.CreateContainerAsync(projectId,
                new CreateContainerRequest(
                    Name: "X", Originator: "ABC",
                    Volume: null, Level: null, Type: "RP",
                    Discipline: null, Description: null),
                userId, ip: "203.0.113.1", ua: "ua-test");
            containerId = c.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cde.container_created"));
        Assert.Equal("CdeContainer", audit.Entity);
        Assert.Equal(containerId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
        Assert.Equal("203.0.113.1", audit.IpAddress);
        Assert.Equal("ua-test", audit.UserAgent);
    }

    // ── ListContainersAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ListContainers_returns_active_containers_for_project()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CdeService(db, new AuditService(db));
            await svc.CreateContainerAsync(projectId,
                new CreateContainerRequest("WIP", "ABC", null, null, "RP", null, null),
                userId, null, null);
            await svc.CreateContainerAsync(projectId,
                new CreateContainerRequest("Shared", "ABC", null, null, "RP", null, null),
                userId, null, null);
        }

        using var db2 = new CimsDbContext(options, tenant);
        var svc2 = new CdeService(db2, new AuditService(db2));
        var rows = await svc2.ListContainersAsync(projectId);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ListContainers_excludes_inactive_containers()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        Guid activeId, inactiveId;
        using (var seedDb = new CimsDbContext(options, tenant))
        {
            var seedSvc = new CdeService(seedDb, new AuditService(seedDb));
            var a = await seedSvc.CreateContainerAsync(projectId,
                new CreateContainerRequest("Live", "ABC", null, null, "RP", null, null),
                userId, null, null);
            var i = await seedSvc.CreateContainerAsync(projectId,
                new CreateContainerRequest("Decommissioned", "ABC", null, null, "RP", null, null),
                userId, null, null);
            activeId = a.Id; inactiveId = i.Id;
        }

        // Mark one container inactive directly (no service method
        // exists to do this — soft-delete is an inferred future
        // operation; the test verifies the existing list filter
        // honours `IsActive`).
        using (var deactivateDb = new CimsDbContext(options, tenant))
        {
            var c = deactivateDb.CdeContainers.Single(x => x.Id == inactiveId);
            c.IsActive = false;
            deactivateDb.SaveChanges();
        }

        using var verify = new CimsDbContext(options, tenant);
        var svc = new CdeService(verify, new AuditService(verify));
        var rows = await svc.ListContainersAsync(projectId);
        var only = Assert.Single(rows);
        Assert.Equal(activeId, only.Id);
    }

    [Fact]
    public async Task ListContainers_returns_empty_for_cross_tenant_project()
    {
        // Cross-tenant: tenant A queries a project that lives in B.
        // The CdeContainer query filter is
        // `c.Project.AppointingPartyId == _tenant.OrganisationId`,
        // so tenant A sees none of B's containers regardless of
        // ProjectId match.
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var projectB = Guid.NewGuid();

        var tenantB = new StubTenantContext
        {
            OrganisationId = orgB, UserId = userB,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new AuditInterceptor(tenantB, httpAccessor: null))
            .Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Users.AddRange(
                new User { Id = userA, Email = $"a-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = orgA },
                new User { Id = userB, Email = $"b-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "B", LastName = "U", OrganisationId = orgB });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }
        using (var db = new CimsDbContext(optionsB, tenantB))
        {
            var svc = new CdeService(db, new AuditService(db));
            await svc.CreateContainerAsync(projectB,
                new CreateContainerRequest("B-only", "ABC", null, null, "RP", null, null),
                userB, null, null);
        }

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svcA = new CdeService(dbA, new AuditService(dbA));
        var rows = await svcA.ListContainersAsync(projectB);
        Assert.Empty(rows);
    }
}

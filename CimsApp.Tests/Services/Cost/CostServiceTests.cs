using System.Text;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Cost;

/// <summary>
/// Behavioural tests for <see cref="CostService.ImportCbsAsync"/>
/// (T-S1-03). The service-layer half landed in <c>b0ab2fb</c>; this
/// suite covers the happy path, every parser/validation branch
/// listed in the T-S1-03 handoff, the conflict guard for
/// re-import, and the tenant query-filter 404 for cross-tenant
/// project lookup.
/// </summary>
public class CostServiceTests
{
    private static readonly string Header =
        "Code,Name,ParentCode,Description,SortOrder";

    private static MemoryStream Csv(string body) =>
        new(Encoding.UTF8.GetBytes(body));

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
            Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
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

    [Fact]
    public async Task Happy_path_imports_tree_resolves_parents_and_writes_audit_row()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,Top-level,1\n1.1,Child A,1,,2\n1.2,Child B,1,,3\n");

        int rowsImported;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new CostService(db, new AuditService(db));
            var r = await svc.ImportCbsAsync(projectId, csv, userId);
            rowsImported = r.RowsImported;
        }

        Assert.Equal(3, rowsImported);

        using var verify = new CimsDbContext(options, tenant);
        var items = verify.CostBreakdownItems.OrderBy(c => c.Code).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("1",   items[0].Code);
        Assert.Equal("1.1", items[1].Code);
        Assert.Equal("1.2", items[2].Code);
        Assert.Null(items[0].ParentId);
        Assert.Equal(items[0].Id, items[1].ParentId);
        Assert.Equal(items[0].Id, items[2].ParentId);

        var imported = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "cbs.imported").ToList();
        var audit = Assert.Single(imported);
        Assert.Equal("CostBreakdownItem", audit.Entity);
        Assert.Equal(projectId.ToString(), audit.EntityId);
        Assert.Equal(projectId, audit.ProjectId);
        Assert.Equal(userId, audit.UserId);
        Assert.NotNull(audit.Detail);
        Assert.Contains("\"rowCount\":3", audit.Detail);
    }

    [Fact]
    public async Task Empty_file_throws_ValidationException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv("");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("CSV is empty", ex.Errors[0]);
    }

    [Fact]
    public async Task Header_mismatch_throws_ValidationException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv("Wrong,Headers,Here,Description,SortOrder\n1,Root,,,1\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("CSV header must be: Code,Name,ParentCode,Description,SortOrder",
            ex.Errors[0]);
    }

    [Fact]
    public async Task Forward_reference_to_unseen_ParentCode_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        // Child appears before its parent — parser order is depth order,
        // so the lookup must fail.
        var csv = Csv($"{Header}\n1.1,Child,1,,1\n1,Root,,,2\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 2: ParentCode '1' not found earlier in file", ex.Errors);
    }

    [Fact]
    public async Task Duplicate_Code_within_file_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,,1\n1,Dup,,,2\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 3: duplicate Code '1'", ex.Errors);
    }

    [Fact]
    public async Task Non_integer_SortOrder_is_rejected()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();
        var csv = Csv($"{Header}\n1,Root,,,foo\n");

        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
        Assert.Contains("Line 2: SortOrder 'foo' is not a valid integer", ex.Errors[0]);
    }

    [Fact]
    public async Task Project_with_existing_CBS_rows_throws_ConflictException()
    {
        var (options, tenant, _, userId, projectId) = BuildFixture();

        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.CostBreakdownItems.Add(new CostBreakdownItem
            {
                ProjectId = projectId, Code = "X", Name = "Pre-existing",
            });
            seed.SaveChanges();
        }

        var csv = Csv($"{Header}\n1,Root,,,1\n");
        using var db = new CimsDbContext(options, tenant);
        var svc = new CostService(db, new AuditService(db));
        await Assert.ThrowsAsync<ConflictException>(() =>
            svc.ImportCbsAsync(projectId, csv, userId));
    }

    [Fact]
    public async Task Cross_tenant_project_lookup_is_NotFound()
    {
        // Two tenants share an in-memory store. Project belongs to B; the
        // call comes from A. The Project query filter (AppointingPartyId
        // == _tenant.OrganisationId) hides B's project from A entirely,
        // and CostService.ImportCbsAsync surfaces that as NotFound rather
        // than leaking existence.
        var dbName    = Guid.NewGuid().ToString();
        var orgA      = Guid.NewGuid();
        var orgB      = Guid.NewGuid();
        var userA     = Guid.NewGuid();
        var userB     = Guid.NewGuid();
        var projectB  = Guid.NewGuid();

        var tenantA = new StubTenantContext { OrganisationId = orgA, UserId = userA };
        var tenantB = new StubTenantContext { OrganisationId = orgB, UserId = userB };

        var optionsB = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        using (var seed = new CimsDbContext(optionsB, tenantB))
        {
            seed.Organisations.AddRange(
                new Organisation { Id = orgA, Name = "A", Code = "TA" },
                new Organisation { Id = orgB, Name = "B", Code = "TB" });
            seed.Projects.Add(new Project
            {
                Id = projectB, Name = "B Project", Code = "PB",
                AppointingPartyId = orgB, Currency = "GBP",
            });
            seed.SaveChanges();
        }

        var optionsA = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var csv = Csv($"{Header}\n1,Root,,,1\n");
        using var dbA = new CimsDbContext(optionsA, tenantA);
        var svc = new CostService(dbA, new AuditService(dbA));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.ImportCbsAsync(projectB, csv, userA));
    }
}

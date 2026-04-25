using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CimsApp.Tests.Services.Audit;

/// <summary>
/// Runtime behavioural tests for <see cref="AuditInterceptor"/> (T-S0-06b).
/// Companion to <see cref="AuditInterceptorTests"/>'s static helpers; these
/// drive the interceptor end-to-end through SaveChangesAsync and assert
/// the AuditLog rows it produced.
/// </summary>
public class AuditInterceptorBehaviourTests
{
    private static (DbContextOptions<CimsDbContext> options, StubTenantContext tenant, Guid orgId, Guid userId)
        BuildFixture()
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
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

        // Seed Organisation + User using the SAME options + tenant so the
        // interceptor sees a consistent tenant. The seed write itself is
        // audited, but only the AuditLog rows from the operation under
        // test are interesting — assertions use IgnoreQueryFilters and
        // filter to the entity types being verified.
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "Test Org", Code = "TO" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@example.com",
                PasswordHash = "x", FirstName = "T", LastName = "User",
                OrganisationId = orgId,
            });
            seed.SaveChanges();
        }

        return (options, tenant, orgId, userId);
    }

    [Fact]
    public async Task Insert_emits_audit_row_with_AfterValue_and_no_BeforeValue()
    {
        var (options, tenant, orgId, userId) = BuildFixture();

        Guid projectId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var project = new Project
            {
                Name = "Insert Project", Code = "IP",
                AppointingPartyId = orgId, Currency = "GBP",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var projectAudits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entity == "Project")
            .ToList();

        var insert = Assert.Single(projectAudits);
        Assert.Equal("Insert", insert.Action);
        Assert.Equal(projectId.ToString(), insert.EntityId);
        Assert.Null(insert.BeforeValue);
        Assert.NotNull(insert.AfterValue);
        Assert.Contains("Insert Project", insert.AfterValue);
        Assert.Equal(userId, insert.UserId);
        Assert.Equal(projectId, insert.ProjectId);
    }

    [Fact]
    public async Task Update_emits_audit_row_with_both_Before_and_After()
    {
        var (options, tenant, orgId, _) = BuildFixture();

        Guid projectId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var project = new Project
            {
                Name = "Original Name", Code = "UP",
                AppointingPartyId = orgId, Currency = "GBP",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            project.Name = "Renamed";
            await db.SaveChangesAsync();
        }

        using var verify = new CimsDbContext(options, tenant);
        var projectAudits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entity == "Project")
            .OrderBy(a => a.CreatedAt)
            .ToList();

        Assert.Equal(2, projectAudits.Count);
        var update = projectAudits[1];
        Assert.Equal("Update", update.Action);
        Assert.NotNull(update.BeforeValue);
        Assert.NotNull(update.AfterValue);
        Assert.Contains("Original Name", update.BeforeValue);
        Assert.Contains("Renamed", update.AfterValue);
    }

    [Fact]
    public async Task Delete_emits_audit_row_with_BeforeValue_and_no_AfterValue()
    {
        var (options, tenant, orgId, _) = BuildFixture();

        Guid projectId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var project = new Project
            {
                Name = "Delete Project", Code = "DP",
                AppointingPartyId = orgId, Currency = "GBP",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            projectId = project.Id;
        }
        using (var db = new CimsDbContext(options, tenant))
        {
            var project = await db.Projects.FirstAsync(p => p.Id == projectId);
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
        }

        using var verify = new CimsDbContext(options, tenant);
        var projectAudits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entity == "Project")
            .OrderBy(a => a.CreatedAt)
            .ToList();

        var delete = projectAudits.Last();
        Assert.Equal("Delete", delete.Action);
        Assert.Null(delete.AfterValue);
        Assert.NotNull(delete.BeforeValue);
        Assert.Contains("Delete Project", delete.BeforeValue);
    }

    [Fact]
    public async Task Direct_AuditLog_insert_does_not_recurse()
    {
        var (options, tenant, _, userId) = BuildFixture();

        // Snapshot count of audit rows from the seed phase to compare against.
        int seedCount;
        using (var verify = new CimsDbContext(options, tenant))
            seedCount = await verify.AuditLogs.IgnoreQueryFilters().CountAsync();

        using (var db = new CimsDbContext(options, tenant))
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId   = userId,
                Action   = "Manual",
                Entity   = "Manual",
                EntityId = "manual-1",
            });
            await db.SaveChangesAsync();
        }

        using var verify2 = new CimsDbContext(options, tenant);
        var totalCount = await verify2.AuditLogs.IgnoreQueryFilters().CountAsync();

        // Exactly one new row — the manually-added one. The interceptor
        // must not produce a second audit row about the AuditLog insert.
        Assert.Equal(seedCount + 1, totalCount);
    }

    [Fact]
    public async Task Anonymous_tenant_context_skips_audit_capture()
    {
        // Build options with an interceptor bound to an anonymous tenant
        // (UserId=null). The CaptureAuditLogs guard must short-circuit so
        // no audit rows are produced for the operation under test.
        var orgId = Guid.NewGuid();
        var anonTenant = new StubTenantContext
        {
            OrganisationId = orgId,
            UserId         = null,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new AuditInterceptor(anonTenant, httpAccessor: null))
            .Options;

        // Seed an org so the project FK can satisfy without a User row
        // (no audit produced because UserId is null).
        using (var seed = new CimsDbContext(options, anonTenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "Anon Org", Code = "AO" });
            await seed.SaveChangesAsync();
        }
        using (var db = new CimsDbContext(options, anonTenant))
        {
            db.Projects.Add(new Project
            {
                Name = "Anon Project", Code = "AP",
                AppointingPartyId = orgId, Currency = "GBP",
            });
            await db.SaveChangesAsync();
        }

        using var verify = new CimsDbContext(options, anonTenant);
        var totalAudits = await verify.AuditLogs.IgnoreQueryFilters().CountAsync();

        Assert.Equal(0, totalAudits);
    }
}

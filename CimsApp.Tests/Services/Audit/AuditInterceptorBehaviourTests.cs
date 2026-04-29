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
    public async Task User_audit_row_does_not_contain_PasswordHash()
    {
        // Defense-in-depth: bcrypt'd hashes are not plaintext but
        // the audit log has a wider blast radius than the User
        // table. The interceptor's SkippedFieldNames must keep
        // PasswordHash out of every BeforeValue / AfterValue JSON.
        var (options, tenant, orgId, _) = BuildFixture();

        Guid newUserId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var u = new User
            {
                Email = $"new-{Guid.NewGuid():N}@example.com",
                PasswordHash = "$2a$11$verysecretbcryptdigeststring",
                FirstName = "New", LastName = "User",
                OrganisationId = orgId,
            };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            newUserId = u.Id;
            // Mutate so we exercise BOTH AfterValue (from Insert)
            // AND BeforeValue (from Update — original is the
            // post-insert state).
            u.FirstName = "Renamed";
            await db.SaveChangesAsync();
        }

        using var verify = new CimsDbContext(options, tenant);
        var audits = verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entity == "User" && a.EntityId == newUserId.ToString())
            .ToList();
        Assert.Equal(2, audits.Count);
        foreach (var a in audits)
        {
            // Neither the literal field name nor the bcrypt prefix
            // may appear in the JSON. The literal-prefix check
            // catches "the secret leaked under a different key
            // name" regressions.
            Assert.DoesNotContain("PasswordHash", a.BeforeValue ?? "");
            Assert.DoesNotContain("PasswordHash", a.AfterValue ?? "");
            Assert.DoesNotContain("$2a$11$", a.BeforeValue ?? "");
            Assert.DoesNotContain("$2a$11$", a.AfterValue ?? "");
        }
    }

    [Fact]
    public async Task Invitation_audit_row_does_not_contain_TokenHash()
    {
        // Same defense-in-depth as PasswordHash. TokenHash is the
        // SHA-256 of the once-shown plaintext invitation token; the
        // plaintext is not recoverable from the hash, but exposing
        // the hash to audit-log readers needlessly widens the
        // attack surface for an offline brute-force search across
        // the limited entropy space of plaintext tokens.
        var (options, tenant, orgId, _) = BuildFixture();

        Guid invId;
        using (var db = new CimsDbContext(options, tenant))
        {
            var inv = new Invitation
            {
                OrganisationId = orgId,
                TokenHash = "F0E1D2C3B4A5968778695A4B3C2D1E0F",
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            };
            db.Invitations.Add(inv);
            await db.SaveChangesAsync();
            invId = inv.Id;
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Entity == "Invitation" && a.EntityId == invId.ToString()));
        Assert.DoesNotContain("TokenHash", audit.AfterValue ?? "");
        Assert.DoesNotContain("F0E1D2C3B4A5968778695A4B3C2D1E0F", audit.AfterValue ?? "");
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

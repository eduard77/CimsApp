using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Services.Auth;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// Regression coverage for the pre-existing NRE surfaced during the
/// B-002 rate-limiting smoke (2026-04-28). When a Login or Register
/// request payload is missing required fields, the service must
/// short-circuit with a structured `AppException` (HTTP 401 for login,
/// HTTP 400 for register) rather than letting `null.ToLowerInvariant()`
/// or `null.Trim()` blow up deep inside EF / InvitationService and
/// surface as HTTP 500 to the caller.
/// </summary>
public class AuthServiceInputValidationTests
{
    private static AuthService BuildService()
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new CimsDbContext(options, tenant);

        // Minimum config for AuthService — JWT secrets etc. None of the
        // input-validation tests reach token generation, so values are
        // immaterial as long as configuration access doesn't NRE.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:AccessSecret"]         = new string('a', 64),
                ["Jwt:RefreshSecret"]        = new string('b', 64),
                ["Jwt:Issuer"]               = "TestIssuer",
                ["Jwt:Audience"]             = "TestAudience",
                ["Jwt:AccessExpiresMinutes"] = "60",
                ["Jwt:RefreshExpiresDays"]   = "7",
            })
            .Build();

        return new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
    }

    private static ILoginAttemptTracker NewTracker() =>
        new LoginAttemptTracker(new MemoryCache(new MemoryCacheOptions()));

    // ── LoginAsync — null / empty Email or Password → 401 not 500 ───────────

    [Fact]
    public async Task Login_with_null_email_throws_401_not_NRE()
    {
        var svc = BuildService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest(null!, "anything"), null, null));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
    }

    [Fact]
    public async Task Login_with_empty_email_throws_401_not_NRE()
    {
        var svc = BuildService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("", "anything"), null, null));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Login_with_null_password_throws_401_not_NRE()
    {
        var svc = BuildService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("user@example.com", null!), null, null));
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task Login_with_well_formed_payload_but_no_matching_user_still_throws_401()
    {
        // Sanity: the existing happy-path-no-user case must still return
        // 401 (not 500) after the guard is in place. The defensive guard
        // only short-circuits the null/empty cases; a well-formed payload
        // that happens to not match any user reaches the existing
        // `?? throw` and produces the same 401.
        var svc = BuildService();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("nobody@example.com", "wrong"), null, null));
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("INVALID_CREDENTIALS", ex.Code);
    }

    // ── RegisterAsync — null / empty required fields → 400 not 500 ─────────

    [Fact]
    public async Task Register_with_null_email_throws_400_not_NRE()
    {
        var svc = BuildService();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RegisterAsync(new RegisterRequest(
                Email: null!, Password: "x", FirstName: "A", LastName: "B",
                JobTitle: null, InvitationToken: "tok")));
    }

    [Fact]
    public async Task Register_with_null_password_throws_400_not_NRE()
    {
        var svc = BuildService();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RegisterAsync(new RegisterRequest(
                Email: "a@b.com", Password: null!, FirstName: "A", LastName: "B",
                JobTitle: null, InvitationToken: "tok")));
    }

    [Fact]
    public async Task Register_with_null_FirstName_throws_400_not_NRE()
    {
        var svc = BuildService();
        await Assert.ThrowsAsync<ValidationException>(() =>
            svc.RegisterAsync(new RegisterRequest(
                Email: "a@b.com", Password: "x", FirstName: null!, LastName: "B",
                JobTitle: null, InvitationToken: "tok")));
    }

    // ── B-001 / ADR-0014 revocation endpoints ────────────────────────────────

    private static (DbContextOptions<CimsDbContext> options, IConfiguration cfg, Guid orgA, Guid orgB, Guid userInA, Guid userInB)
        BuildTwoTenantFixture()
    {
        var orgA    = Guid.NewGuid();
        var orgB    = Guid.NewGuid();
        var userInA = Guid.NewGuid();
        var userInB = Guid.NewGuid();
        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userInA,
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
                new User { Id = userInA, Email = $"a-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "A", LastName = "U", OrganisationId = orgA },
                new User { Id = userInB, Email = $"b-{Guid.NewGuid():N}@e.com",
                    PasswordHash = "x", FirstName = "B", LastName = "U", OrganisationId = orgB });
            seed.SaveChanges();
        }
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:AccessSecret"]  = new string('a', 64),
                ["Jwt:RefreshSecret"] = new string('b', 64),
                ["Jwt:Issuer"]        = "I", ["Jwt:Audience"] = "A",
                ["Jwt:AccessExpiresMinutes"] = "60", ["Jwt:RefreshExpiresDays"] = "7",
            }).Build();
        return (options, cfg, orgA, orgB, userInA, userInB);
    }

    [Fact]
    public async Task RevokeOwnTokens_sets_cutoff_to_UtcNow()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var tenant = new StubTenantContext { OrganisationId = orgA, UserId = userInA };

        var before = DateTime.UtcNow;
        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.RevokeOwnTokensAsync(userInA);
        }
        var after = DateTime.UtcNow;

        using var verify = new CimsDbContext(options, tenant);
        var u = verify.Users.IgnoreQueryFilters().Single(x => x.Id == userInA);
        Assert.NotNull(u.TokenInvalidationCutoff);
        Assert.InRange(u.TokenInvalidationCutoff!.Value, before, after);
    }

    [Fact]
    public async Task RevokeOwnTokens_unknown_user_throws_NotFound()
    {
        var svc = BuildService();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RevokeOwnTokensAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RevokeUserTokens_admin_OrgAdmin_can_target_own_org_user()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };

        using (var db = new CimsDbContext(options, orgAdmin))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.RevokeUserTokensAsync(userInA, orgAdmin);
        }

        using var verify = new CimsDbContext(options, orgAdmin);
        Assert.NotNull(verify.Users.IgnoreQueryFilters().Single(x => x.Id == userInA).TokenInvalidationCutoff);
    }

    [Fact]
    public async Task RevokeUserTokens_admin_OrgAdmin_cannot_target_other_org_user()
    {
        // Cross-tenant attempt: OrgAdmin in A tries to revoke a user
        // in B. The lookup uses the tenant query filter (filter
        // u.OrganisationId == orgA), so the userInB row is not
        // visible — service returns NotFound, not Forbidden, so the
        // existence of the cross-org user doesn't leak.
        var (options, cfg, orgA, _, _, userInB) = BuildTwoTenantFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };

        using var db = new CimsDbContext(options, orgAdmin);
        var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.RevokeUserTokensAsync(userInB, orgAdmin));
    }

    [Fact]
    public async Task RevokeUserTokens_admin_SuperAdmin_can_target_any_org_user()
    {
        var (options, cfg, orgA, _, _, userInB) = BuildTwoTenantFixture();
        var superAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.SuperAdmin,
        };

        using (var db = new CimsDbContext(options, superAdmin))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.RevokeUserTokensAsync(userInB, superAdmin);
        }

        using var verify = new CimsDbContext(options, superAdmin);
        Assert.NotNull(verify.Users.IgnoreQueryFilters().Single(x => x.Id == userInB).TokenInvalidationCutoff);
    }

    [Fact]
    public async Task DeactivateUser_sets_IsActive_false_and_bumps_cutoff()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };

        var before = DateTime.UtcNow;
        using (var db = new CimsDbContext(options, orgAdmin))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.DeactivateUserAsync(userInA, orgAdmin);
        }
        var after = DateTime.UtcNow;

        using var verify = new CimsDbContext(options, orgAdmin);
        var u = verify.Users.IgnoreQueryFilters().Single(x => x.Id == userInA);
        Assert.False(u.IsActive);
        Assert.NotNull(u.TokenInvalidationCutoff);
        Assert.InRange(u.TokenInvalidationCutoff!.Value, before, after);
    }

    // ── B-021 structured auth-domain audit events ───────────────────────────

    [Fact]
    public async Task RevokeOwnTokens_emits_auth_user_self_revoke_audit()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var tenant = new StubTenantContext { OrganisationId = orgA, UserId = userInA };

        // Seed two active refresh tokens so the swept count is > 0.
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.RefreshTokens.AddRange(
                new RefreshToken { Token = $"t1-{Guid.NewGuid():N}", UserId = userInA, ExpiresAt = DateTime.UtcNow.AddDays(7) },
                new RefreshToken { Token = $"t2-{Guid.NewGuid():N}", UserId = userInA, ExpiresAt = DateTime.UtcNow.AddDays(7) });
            seed.SaveChanges();
        }

        using (var db = new CimsDbContext(options, tenant))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.RevokeOwnTokensAsync(userInA);
        }

        using var verify = new CimsDbContext(options, tenant);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "auth.user_self_revoke"));
        Assert.Equal("User", audit.Entity);
        Assert.Equal(userInA.ToString(), audit.EntityId);
        Assert.Equal(userInA, audit.UserId);
        Assert.Contains("\"refreshTokensSwept\":2", audit.Detail);
    }

    [Fact]
    public async Task RevokeUserTokens_admin_emits_auth_user_admin_revoke_audit()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var adminId  = Guid.NewGuid();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = adminId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        using (var seed = new CimsDbContext(options, orgAdmin))
        {
            seed.Users.Add(new User
            {
                Id = adminId, Email = $"adm-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "Adm", LastName = "U",
                OrganisationId = orgA,
            });
            seed.SaveChanges();
        }

        using (var db = new CimsDbContext(options, orgAdmin))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.RevokeUserTokensAsync(userInA, orgAdmin);
        }

        using var verify = new CimsDbContext(options, orgAdmin);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "auth.user_admin_revoke"));
        Assert.Equal(userInA.ToString(), audit.EntityId);
        Assert.Equal(adminId, audit.UserId);
        Assert.Contains($"\"targetUserId\":\"{userInA}\"", audit.Detail);
    }

    [Fact]
    public async Task DeactivateUser_emits_auth_user_deactivated_audit()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildTwoTenantFixture();
        var adminId  = Guid.NewGuid();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = adminId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        using (var seed = new CimsDbContext(options, orgAdmin))
        {
            seed.Users.Add(new User
            {
                Id = adminId, Email = $"adm-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "Adm", LastName = "U",
                OrganisationId = orgA,
            });
            seed.SaveChanges();
        }

        using (var db = new CimsDbContext(options, orgAdmin))
        {
            var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
            await svc.DeactivateUserAsync(userInA, orgAdmin);
        }

        using var verify = new CimsDbContext(options, orgAdmin);
        var audit = Assert.Single(verify.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.Action == "auth.user_deactivated"));
        Assert.Equal(userInA.ToString(), audit.EntityId);
        Assert.Equal(adminId, audit.UserId);
        Assert.Contains($"\"targetUserId\":\"{userInA}\"", audit.Detail);
    }

    [Fact]
    public async Task DeactivateUser_OrgAdmin_cannot_target_other_org_user()
    {
        var (options, cfg, orgA, _, _, userInB) = BuildTwoTenantFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };

        using var db = new CimsDbContext(options, orgAdmin);
        var svc = new AuthService(db, cfg, new InvitationService(db), NewTracker(), new AuditService(db));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.DeactivateUserAsync(userInB, orgAdmin));

        // Confirm the target user wasn't mutated.
        using var verify = new CimsDbContext(options, orgAdmin);
        var u = verify.Users.IgnoreQueryFilters().Single(x => x.Id == userInB);
        Assert.True(u.IsActive);
        Assert.Null(u.TokenInvalidationCutoff);
    }
}

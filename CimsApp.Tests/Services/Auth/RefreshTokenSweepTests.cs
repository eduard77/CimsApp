using CimsApp.Core;
using CimsApp.Data;
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
/// B-019 refresh-token bulk-revoke. Closes the gap that
/// ADR-0014 §3 deferred — the access-token cutoff alone left a
/// refresh-token-shaped hole in "log out everywhere": a
/// multi-device user could refresh on another device after the
/// cutoff bump and mint a fresh access token whose `iat` is
/// strictly greater than the cutoff. Sweeping active refresh
/// tokens at the same time closes that hole.
/// </summary>
public class RefreshTokenSweepTests
{
    private static (DbContextOptions<CimsDbContext> options, IConfiguration cfg,
        Guid orgA, Guid orgB, Guid userInA, Guid userInB) BuildFixture()
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
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
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

    private static AuthService NewService(DbContextOptions<CimsDbContext> options,
        IConfiguration cfg, StubTenantContext tenant)
    {
        var db = new CimsDbContext(options, tenant);
        var tracker = new LoginAttemptTracker(new MemoryCache(new MemoryCacheOptions()));
        return new AuthService(db, cfg, new InvitationService(db), tracker, new AuditService(db));
    }

    private static (Guid activeId, Guid alreadyRevokedId, Guid expiredId)
        SeedRefreshTokensFor(DbContextOptions<CimsDbContext> options,
            StubTenantContext tenant, Guid userId)
    {
        var active   = Guid.NewGuid();
        var revoked  = Guid.NewGuid();
        var expired  = Guid.NewGuid();
        using var seed = new CimsDbContext(options, tenant);
        seed.RefreshTokens.AddRange(
            new RefreshToken
            {
                Id = active, Token = $"a-{Guid.NewGuid():N}", UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                RevokedAt = null,
            },
            new RefreshToken
            {
                Id = revoked, Token = $"r-{Guid.NewGuid():N}", UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                RevokedAt = DateTime.UtcNow.AddHours(-1),  // already revoked
            },
            new RefreshToken
            {
                Id = expired, Token = $"e-{Guid.NewGuid():N}", UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddHours(-1),  // expired
                RevokedAt = null,
            });
        seed.SaveChanges();
        return (active, revoked, expired);
    }

    [Fact]
    public async Task RevokeOwnTokens_revokes_only_active_refresh_tokens()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildFixture();
        var tenant = new StubTenantContext { OrganisationId = orgA, UserId = userInA };
        var (activeId, alreadyRevokedId, expiredId) =
            SeedRefreshTokensFor(options, tenant, userInA);

        var alreadyRevokedAtBefore = await ReadRevokedAtAsync(options, tenant, alreadyRevokedId);

        var svc = NewService(options, cfg, tenant);
        await svc.RevokeOwnTokensAsync(userInA);

        // Active token now revoked.
        Assert.NotNull(await ReadRevokedAtAsync(options, tenant, activeId));

        // Already-revoked token's RevokedAt is unchanged — sweep
        // doesn't touch tokens that already have a RevokedAt set
        // (avoids resetting the original revocation timestamp).
        Assert.Equal(alreadyRevokedAtBefore,
            await ReadRevokedAtAsync(options, tenant, alreadyRevokedId));

        // Expired token left alone — no point revoking a token that's
        // already past ExpiresAt.
        Assert.Null(await ReadRevokedAtAsync(options, tenant, expiredId));
    }

    [Fact]
    public async Task RevokeUserTokens_admin_sweeps_target_users_refresh_tokens()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        var (activeId, _, _) = SeedRefreshTokensFor(options, orgAdmin, userInA);

        var svc = NewService(options, cfg, orgAdmin);
        await svc.RevokeUserTokensAsync(userInA, orgAdmin);

        Assert.NotNull(await ReadRevokedAtAsync(options, orgAdmin, activeId));
    }

    [Fact]
    public async Task DeactivateUser_sweeps_target_users_refresh_tokens()
    {
        var (options, cfg, orgA, _, userInA, _) = BuildFixture();
        var orgAdmin = new StubTenantContext
        {
            OrganisationId = orgA, UserId = Guid.NewGuid(),
            GlobalRole     = UserRole.OrgAdmin,
        };
        var (activeId, _, _) = SeedRefreshTokensFor(options, orgAdmin, userInA);

        var svc = NewService(options, cfg, orgAdmin);
        await svc.DeactivateUserAsync(userInA, orgAdmin);

        Assert.NotNull(await ReadRevokedAtAsync(options, orgAdmin, activeId));
    }

    [Fact]
    public async Task Sweep_does_not_revoke_other_users_refresh_tokens()
    {
        // Critical: revoking user A must not touch user B's refresh
        // tokens. Otherwise an admin revoke could collateral-damage
        // the entire org.
        var (options, cfg, orgA, _, userInA, userInB) = BuildFixture();
        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgA, UserId = userInA,
            GlobalRole     = UserRole.SuperAdmin,
        };
        var (activeForA, _, _) = SeedRefreshTokensFor(options, seedTenant, userInA);
        var (activeForB, _, _) = SeedRefreshTokensFor(options, seedTenant, userInB);

        var svc = NewService(options, cfg, seedTenant);
        await svc.RevokeUserTokensAsync(userInA, seedTenant);

        Assert.NotNull(await ReadRevokedAtAsync(options, seedTenant, activeForA));
        Assert.Null(await ReadRevokedAtAsync(options, seedTenant, activeForB));
    }

    private static async Task<DateTime?> ReadRevokedAtAsync(
        DbContextOptions<CimsDbContext> options, StubTenantContext tenant, Guid tokenId)
    {
        using var db = new CimsDbContext(options, tenant);
        var t = await db.RefreshTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == tokenId);
        return t?.RevokedAt;
    }
}

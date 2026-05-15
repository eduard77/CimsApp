using GeneraPm.Core;
using GeneraPm.Data;
using GeneraPm.DTOs;
using GeneraPm.Models;
using GeneraPm.Services;
using GeneraPm.Services.Auth;
using GeneraPm.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GeneraPm.Tests.Services.Auth;

/// <summary>
/// Covers `AuthService.RefreshAsync`. Pre-fix, RefreshAsync called
/// `Validate(token, RefreshSecret)` to JWT-validate the input — but
/// `CreateRefreshAsync` mints opaque hex strings, NOT JWTs. So
/// /auth/refresh returned 401 INVALID_REFRESH on EVERY call since
/// the initial commit. Surfaced 2026-04-29 by smoke-testing the
/// bootstrap → register → login → refresh flow against real SQL
/// Server. No unit test existed because RefreshAsync was never
/// exercised in tests.
/// </summary>
public class RefreshTokenAuthTests
{
    private static (DbContextOptions<GeneraPmDbContext> options, IConfiguration cfg, Guid orgId, Guid userId)
        BuildFixture()
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<GeneraPmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var seed = new GeneraPmDbContext(options, seedTenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
            seed.Users.Add(new User
            {
                Id = userId, Email = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash = "x", FirstName = "U", LastName = "T",
                OrganisationId = orgId,
            });
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
        return (options, cfg, orgId, userId);
    }

    private static AuthService NewService(DbContextOptions<GeneraPmDbContext> options,
        IConfiguration cfg, Guid orgId, Guid userId, out GeneraPmDbContext db)
    {
        var tenant = new StubTenantContext { OrganisationId = orgId, UserId = userId };
        db = new GeneraPmDbContext(options, tenant);
        return new AuthService(db, cfg, new InvitationService(db, new AuditService(db)),
            new LoginAttemptTracker(new MemoryCache(new MemoryCacheOptions())),
            new AuditService(db));
    }

    private static async Task<string> SeedRefreshTokenAsync(
        DbContextOptions<GeneraPmDbContext> options, Guid orgId, Guid userId,
        string token, DateTime? revokedAt = null,
        DateTime? expiresAt = null)
    {
        var tenant = new StubTenantContext { OrganisationId = orgId, UserId = userId };
        using var db = new GeneraPmDbContext(options, tenant);
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = token,
            UserId = userId,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            RevokedAt = revokedAt,
        });
        await db.SaveChangesAsync();
        return token;
    }

    [Fact]
    public async Task RefreshAsync_with_valid_opaque_token_returns_new_access_and_refresh()
    {
        var (options, cfg, orgId, userId) = BuildFixture();
        var token = "opaque-hex-token-" + Guid.NewGuid().ToString("N");
        await SeedRefreshTokenAsync(options, orgId, userId, token);

        var svc = NewService(options, cfg, orgId, userId, out var db);
        using (db)
        {
            var (access, refresh) = await svc.RefreshAsync(token);
            Assert.NotNull(access);
            Assert.NotEmpty(access);
            Assert.NotNull(refresh);
            Assert.NotEqual(token, refresh);  // new token minted
        }

        // Old token must now be revoked; one new active token must exist.
        var verifyTenant = new StubTenantContext { OrganisationId = orgId, UserId = userId };
        using var verify = new GeneraPmDbContext(options, verifyTenant);
        var rows = verify.RefreshTokens.IgnoreQueryFilters().Where(r => r.UserId == userId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Token == token && r.RevokedAt != null);
        Assert.Single(rows, r => r.Token != token && r.RevokedAt == null);
    }

    [Fact]
    public async Task RefreshAsync_with_unknown_token_throws_INVALID_REFRESH()
    {
        var (options, cfg, orgId, userId) = BuildFixture();

        var svc = NewService(options, cfg, orgId, userId, out var db);
        using (db)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() =>
                svc.RefreshAsync("nope-not-a-real-token"));
            Assert.Equal(401, ex.StatusCode);
            Assert.Equal("INVALID_REFRESH", ex.Code);
        }
    }

    [Fact]
    public async Task RefreshAsync_with_revoked_token_throws_TOKEN_REVOKED()
    {
        var (options, cfg, orgId, userId) = BuildFixture();
        var token = "already-revoked-" + Guid.NewGuid().ToString("N");
        await SeedRefreshTokenAsync(options, orgId, userId, token,
            revokedAt: DateTime.UtcNow.AddMinutes(-1));

        var svc = NewService(options, cfg, orgId, userId, out var db);
        using (db)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.RefreshAsync(token));
            Assert.Equal(401, ex.StatusCode);
            Assert.Equal("TOKEN_REVOKED", ex.Code);
        }
    }

    [Fact]
    public async Task RefreshAsync_with_expired_token_throws_TOKEN_REVOKED()
    {
        var (options, cfg, orgId, userId) = BuildFixture();
        var token = "expired-" + Guid.NewGuid().ToString("N");
        await SeedRefreshTokenAsync(options, orgId, userId, token,
            expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var svc = NewService(options, cfg, orgId, userId, out var db);
        using (db)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.RefreshAsync(token));
            Assert.Equal(401, ex.StatusCode);
            Assert.Equal("TOKEN_REVOKED", ex.Code);
        }
    }

    [Fact]
    public async Task RefreshAsync_with_null_or_empty_token_throws_INVALID_REFRESH()
    {
        var (options, cfg, orgId, userId) = BuildFixture();

        var svc = NewService(options, cfg, orgId, userId, out var db);
        using (db)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => svc.RefreshAsync(""));
            Assert.Equal("INVALID_REFRESH", ex.Code);
            ex = await Assert.ThrowsAsync<AppException>(() => svc.RefreshAsync(null!));
            Assert.Equal("INVALID_REFRESH", ex.Code);
        }
    }
}

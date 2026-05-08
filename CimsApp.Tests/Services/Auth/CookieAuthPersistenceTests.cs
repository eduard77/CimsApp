using System.Security.Claims;
using CimsApp.Core;
using CimsApp.Data;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Auth;
using CimsApp.Services.Tenancy;
using CimsApp.Tests.TestDoubles;
using CimsApp.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// ADR-0016 — auth-persistence cookie tests.
///
/// Covers:
///   (a) <see cref="AuthService.BuildCookiePrincipalAsync"/> claim shape
///       (sub, cims:org, cims:role, iat) for OrgAdmin and roleless users.
///   (b) NotFoundException on unknown user id.
///   (c) <see cref="CookieRevalidation.ParsePrincipal"/> branches —
///       missing / malformed sub or iat returns Ok=false; well-formed
///       returns the parsed values.
///
/// Integration coverage of /login Set-Cookie and /logout clearing
/// Set-Cookie requires WebApplicationFactory; Microsoft.AspNetCore.Mvc.Testing
/// is not currently referenced and adding it would breach
/// "no new top-level NuGet packages." Smoke-test verification covers
/// those paths.
/// </summary>
public class CookieAuthPersistenceTests
{
    // ──────────────────────────────────────────────────────────────────
    // BuildCookiePrincipalAsync
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildCookiePrincipalAsync_OrgAdmin_emits_sub_org_role_iat()
    {
        var (svc, userId, orgId, _) = await NewServiceWithUserAsync(role: UserRole.OrgAdmin);

        var principal = await svc.BuildCookiePrincipalAsync(userId);

        Assert.Equal(userId.ToString(),
            principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(orgId.ToString(),
            principal.FindFirstValue(HttpTenantContext.OrganisationClaimType));
        Assert.Equal(UserRole.OrgAdmin.ToString(),
            principal.FindFirstValue(HttpTenantContext.GlobalRoleClaimType));
        var iat = principal.FindFirstValue("iat");
        Assert.False(string.IsNullOrEmpty(iat));
        Assert.True(long.TryParse(iat, out var seconds));
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        Assert.True(DateTimeOffset.UtcNow - issuedAt < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task BuildCookiePrincipalAsync_roleless_user_omits_role_claim()
    {
        var (svc, userId, _, _) = await NewServiceWithUserAsync(role: null);

        var principal = await svc.BuildCookiePrincipalAsync(userId);

        Assert.Null(principal.FindFirstValue(HttpTenantContext.GlobalRoleClaimType));
        // Sub and org still present.
        Assert.NotNull(principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.NotNull(principal.FindFirstValue(HttpTenantContext.OrganisationClaimType));
    }

    [Fact]
    public async Task BuildCookiePrincipalAsync_unknown_user_throws_NotFound()
    {
        var (svc, _, _, _) = await NewServiceWithUserAsync(role: UserRole.OrgAdmin);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.BuildCookiePrincipalAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BuildCookiePrincipalAsync_uses_IgnoreQueryFilters_so_works_without_tenant_context()
    {
        // The Blazor circuit hits BuildCookiePrincipalAsync via a path
        // where HttpTenantContext sees null HttpContext (tenant filters
        // would otherwise return zero rows). A fresh AuthService built
        // with a stub tenant carrying nulls must still find the user.
        var (svc, userId, _, _) = await NewServiceWithUserAsync(role: UserRole.OrgAdmin,
            laterTenant: new StubTenantContext { OrganisationId = null, UserId = null });

        var principal = await svc.BuildCookiePrincipalAsync(userId);
        Assert.Equal(userId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    // ──────────────────────────────────────────────────────────────────
    // CookieRevalidation.ParsePrincipal
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsePrincipal_well_formed_returns_userid_and_iat()
    {
        var userId = Guid.NewGuid();
        var iatSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var principal = NewPrincipal(
            (ClaimTypes.NameIdentifier, userId.ToString()),
            ("iat", iatSeconds.ToString()));

        var (ok, parsedId, issuedAt) = CookieRevalidation.ParsePrincipal(principal);

        Assert.True(ok);
        Assert.Equal(userId, parsedId);
        Assert.Equal(iatSeconds, ((DateTimeOffset)issuedAt).ToUnixTimeSeconds());
    }

    [Fact]
    public void ParsePrincipal_missing_sub_returns_false()
    {
        var principal = NewPrincipal(("iat", "1700000000"));

        var (ok, _, _) = CookieRevalidation.ParsePrincipal(principal);
        Assert.False(ok);
    }

    [Fact]
    public void ParsePrincipal_malformed_sub_returns_false()
    {
        var principal = NewPrincipal(
            (ClaimTypes.NameIdentifier, "not-a-guid"),
            ("iat", "1700000000"));

        var (ok, _, _) = CookieRevalidation.ParsePrincipal(principal);
        Assert.False(ok);
    }

    [Fact]
    public void ParsePrincipal_missing_iat_returns_false()
    {
        var principal = NewPrincipal(
            (ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        var (ok, _, _) = CookieRevalidation.ParsePrincipal(principal);
        Assert.False(ok);
    }

    [Fact]
    public void ParsePrincipal_malformed_iat_returns_false()
    {
        var principal = NewPrincipal(
            (ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            ("iat", "not-a-number"));

        var (ok, _, _) = CookieRevalidation.ParsePrincipal(principal);
        Assert.False(ok);
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static ClaimsPrincipal NewPrincipal(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)),
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(AuthService Svc, Guid UserId, Guid OrgId, CimsDbContext Db)>
        NewServiceWithUserAsync(UserRole? role, StubTenantContext? laterTenant = null)
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var seedTenant = new StubTenantContext
        {
            OrganisationId = orgId, UserId = userId,
            GlobalRole     = UserRole.OrgAdmin,
        };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using (var seed = new CimsDbContext(options, seedTenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "Org", Code = "OG" });
            seed.Users.Add(new User
            {
                Id             = userId,
                Email          = $"u-{Guid.NewGuid():N}@e.com",
                PasswordHash   = "x",
                FirstName      = "Eve",
                LastName       = "Roe",
                OrganisationId = orgId,
                GlobalRole     = role,
                IsActive       = true,
            });
            await seed.SaveChangesAsync();
        }

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:AccessSecret"]         = new string('a', 64),
                ["Jwt:RefreshSecret"]        = new string('b', 64),
                ["Jwt:Issuer"]               = "I",
                ["Jwt:Audience"]             = "A",
                ["Jwt:AccessExpiresMinutes"] = "60",
                ["Jwt:RefreshExpiresDays"]   = "7",
            }).Build();

        var tenant = laterTenant ?? new StubTenantContext { OrganisationId = orgId, UserId = userId };
        var db = new CimsDbContext(options, tenant);
        var svc = new AuthService(db, cfg,
            new InvitationService(db, new AuditService(db)),
            new LoginAttemptTracker(new MemoryCache(new MemoryCacheOptions())),
            new AuditService(db));
        return (svc, userId, orgId, db);
    }
}

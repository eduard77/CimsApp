using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Audit;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
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

        return new AuthService(db, cfg, new InvitationService(db));
    }

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
}

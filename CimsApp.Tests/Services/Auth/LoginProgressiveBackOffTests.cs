using CimsApp.Core;
using CimsApp.Data;
using CimsApp.DTOs;
using CimsApp.Models;
using CimsApp.Services;
using CimsApp.Services.Auth;
using CimsApp.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// B-002 progressive back-off integration through
/// <see cref="AuthService.LoginAsync"/>. Verifies the three
/// load-bearing behaviours:
///
///  - Locked-out IP short-circuits with HTTP 429 LOGIN_LOCKOUT
///    BEFORE any DB lookup happens (no `?? throw`-class 401 leaks).
///  - A failed login records a failure against the IP.
///  - A successful login resets the counter.
///
/// Uses a stub tracker so the test can drive the IsLockedOut / hit
/// counters directly without timing.
/// </summary>
public class LoginProgressiveBackOffTests
{
    private sealed class StubTracker : ILoginAttemptTracker
    {
        public Dictionary<string, int> Failures { get; } = new();
        public HashSet<string> SuccessIps { get; } = new();
        public Func<string, bool> LockoutPredicate { get; set; } = _ => false;

        public bool IsLockedOut(string ipAddress) => LockoutPredicate(ipAddress);
        public void RecordFailure(string ipAddress)
        {
            Failures.TryGetValue(ipAddress, out var n);
            Failures[ipAddress] = n + 1;
        }
        public void RecordSuccess(string ipAddress) => SuccessIps.Add(ipAddress);
    }

    private static (AuthService svc, StubTracker tracker, Guid userId)
        BuildWithSeededUser(string passwordPlaintext)
    {
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new StubTenantContext { OrganisationId = orgId, UserId = userId };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = userId, Email = "user@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(passwordPlaintext),
                FirstName = "U", LastName = "U", OrganisationId = orgId,
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

        var db      = new CimsDbContext(options, tenant);
        var tracker = new StubTracker();
        var svc     = new AuthService(db, cfg, new InvitationService(db), tracker);
        return (svc, tracker, userId);
    }

    [Fact]
    public async Task Locked_out_IP_is_rejected_with_429_before_any_DB_work()
    {
        var (svc, tracker, _) = BuildWithSeededUser("password1");
        tracker.LockoutPredicate = ip => ip == "10.0.0.5";

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("user@example.com", "password1"),
                ua: null, ip: "10.0.0.5"));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("LOGIN_LOCKOUT", ex.Code);

        // Critical: even though credentials WOULD have been valid,
        // the lockout fires first. No success recorded; no failure
        // recorded for the lockout itself (it didn't reach the DB).
        Assert.Empty(tracker.SuccessIps);
        Assert.False(tracker.Failures.ContainsKey("10.0.0.5"));
    }

    [Fact]
    public async Task Failed_login_records_failure_against_caller_IP()
    {
        var (svc, tracker, _) = BuildWithSeededUser("password1");

        await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("user@example.com", "wrong-password"),
                ua: null, ip: "203.0.113.10"));

        Assert.Equal(1, tracker.Failures["203.0.113.10"]);
    }

    [Fact]
    public async Task Failed_login_for_unknown_user_also_records_failure()
    {
        // Critical: the failure must be recorded even when the user
        // doesn't exist. Otherwise an attacker who only sends emails
        // they know don't exist could probe for valid emails without
        // ever incurring a lockout — which is a back-channel oracle.
        var (svc, tracker, _) = BuildWithSeededUser("password1");

        await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("nobody@example.com", "anything"),
                ua: null, ip: "203.0.113.11"));

        Assert.Equal(1, tracker.Failures["203.0.113.11"]);
    }

    [Fact]
    public async Task Successful_login_records_success_clearing_the_counter()
    {
        var (svc, tracker, _) = BuildWithSeededUser("password1");

        var resp = await svc.LoginAsync(
            new LoginRequest("user@example.com", "password1"),
            ua: null, ip: "203.0.113.20");

        Assert.NotNull(resp);
        Assert.Contains("203.0.113.20", tracker.SuccessIps);
    }

    [Fact]
    public async Task Empty_credential_payload_does_not_record_a_failure()
    {
        // The null/empty-credential guard throws 401 BEFORE the
        // try/catch that records failures. Rationale: a client
        // submitting empty credentials hasn't actually attempted
        // auth — they've sent a malformed request. Counting that
        // toward lockout would let an operator with a buggy form
        // get themselves locked out by accident, and wouldn't
        // meaningfully help an attacker (they could trivially
        // generate non-empty random strings).
        var (svc, tracker, _) = BuildWithSeededUser("password1");

        await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest(null!, null!),
                ua: null, ip: "203.0.113.30"));

        Assert.False(tracker.Failures.ContainsKey("203.0.113.30"));
    }

    [Fact]
    public async Task Real_tracker_locks_out_after_five_failures()
    {
        // End-to-end with the real LoginAttemptTracker (no stub).
        // Confirms the integration works at the boundary the rest of
        // the test class abstracts away with the stub.
        var orgId  = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tenant = new StubTenantContext { OrganisationId = orgId, UserId = userId };
        var options = new DbContextOptionsBuilder<CimsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using (var seed = new CimsDbContext(options, tenant))
        {
            seed.Organisations.Add(new Organisation { Id = orgId, Name = "O", Code = "O" });
            seed.Users.Add(new User
            {
                Id = userId, Email = "user@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("password1"),
                FirstName = "U", LastName = "U", OrganisationId = orgId,
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
        var db = new CimsDbContext(options, tenant);
        var tracker = new LoginAttemptTracker(
            new MemoryCache(new MemoryCacheOptions()),
            lockoutThreshold: 5, window: TimeSpan.FromMinutes(15));
        var svc = new AuthService(db, cfg, new InvitationService(db), tracker);

        // Five failures from the same IP — each returns the standard
        // INVALID_CREDENTIALS 401.
        for (int i = 0; i < 5; i++)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() =>
                svc.LoginAsync(new LoginRequest("user@example.com", "wrong"),
                    ua: null, ip: "203.0.113.40"));
            Assert.Equal(401, ex.StatusCode);
        }

        // Sixth attempt — even with correct credentials — is locked out.
        var lockoutEx = await Assert.ThrowsAsync<AppException>(() =>
            svc.LoginAsync(new LoginRequest("user@example.com", "password1"),
                ua: null, ip: "203.0.113.40"));
        Assert.Equal(429, lockoutEx.StatusCode);
        Assert.Equal("LOGIN_LOCKOUT", lockoutEx.Code);
    }
}

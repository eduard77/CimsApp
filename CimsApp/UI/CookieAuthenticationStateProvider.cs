using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.EntityFrameworkCore;
using CimsApp.Data;
using CimsApp.Services.Auth;

namespace CimsApp.UI;

// ADR-0016 §3: revalidating provider for the Blazor cookie session.
//
// Initial state: handed off from the cookie-authenticated HTTP request
// via the framework's IHostEnvironmentAuthenticationStateProvider — see
// ADR-0016 §6 (capture-once). We do NOT re-read IHttpContextAccessor on
// later calls; HttpContext is null after the SignalR circuit attaches.
//
// Freshness: every RevalidationInterval the predicate below loads the
// User row and applies the same revocation rule the JWT pipeline uses
// in Program.cs OnTokenValidated. The cookie carries an `iat` claim
// (added by AuthController.Login) so cutoff comparison is exact.
public sealed class CookieAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var (ok, userId, issuedAt) = CookieRevalidation.ParsePrincipal(authenticationState.User);
        if (!ok) return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db   = scope.ServiceProvider.GetRequiredService<CimsDbContext>();
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        return !TokenRevocation.IsRevoked(user, issuedAt);
    }
}

/// <summary>
/// Pure-function helper around the cookie principal claim shape, so
/// the parsing branches in <see cref="CookieAuthenticationStateProvider"/>
/// are unit-testable without a Blazor circuit fixture.
/// </summary>
public static class CookieRevalidation
{
    public static (bool Ok, Guid UserId, DateTime IssuedAt) ParsePrincipal(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId))
            return (false, Guid.Empty, DateTime.MinValue);

        var iatClaim = user.FindFirstValue("iat");
        if (!long.TryParse(iatClaim, out var iatSeconds))
            return (false, Guid.Empty, DateTime.MinValue);

        return (true, userId, DateTimeOffset.FromUnixTimeSeconds(iatSeconds).UtcDateTime);
    }
}

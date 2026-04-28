using CimsApp.Models;

namespace CimsApp.Services.Auth;

/// <summary>
/// B-001: per-user access-token revocation rules. Pure function so
/// the JWT bearer hook is unit-testable without a full
/// `WebApplicationFactory`. Returns true when the bearer should be
/// REJECTED.
///
/// The two reject paths:
/// - User row missing or `IsActive == false`. Closes the today-bug
///   where deactivating a user did not invalidate their existing
///   access token (the JWT kept working until natural expiry, up to
///   60 minutes of residual authority).
/// - User has a `TokenInvalidationCutoff` set AND the token's `iat`
///   falls before it. Bumped by an explicit
///   `AuthService.RevokeUserTokensAsync` call on role demotion or
///   any other security-sensitive User mutation.
/// </summary>
public static class TokenRevocation
{
    public static bool IsRevoked(User? user, DateTime tokenIssuedAtUtc)
    {
        if (user is null || !user.IsActive) return true;
        if (user.TokenInvalidationCutoff is { } cutoff
            && tokenIssuedAtUtc < cutoff)
            return true;
        return false;
    }
}

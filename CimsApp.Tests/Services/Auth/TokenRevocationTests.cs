using CimsApp.Models;
using CimsApp.Services.Auth;
using Xunit;

namespace CimsApp.Tests.Services.Auth;

/// <summary>
/// B-001 token-revocation rules. Pure-function tests on
/// <see cref="TokenRevocation.IsRevoked"/>; the JwtBearer
/// `OnTokenValidated` hook in `Program.cs` is the single
/// caller and just delegates to this helper after loading the
/// User row.
/// </summary>
public class TokenRevocationTests
{
    private static readonly DateTime Iat = new(2026, 4, 28, 10, 0, 0, DateTimeKind.Utc);

    private static User Active(DateTime? cutoff = null) => new()
    {
        Email = "u@example.com", PasswordHash = "x",
        FirstName = "U", LastName = "U",
        IsActive = true,
        TokenInvalidationCutoff = cutoff,
    };

    [Fact]
    public void Null_user_is_revoked()
    {
        Assert.True(TokenRevocation.IsRevoked(null, Iat));
    }

    [Fact]
    public void Inactive_user_is_revoked_regardless_of_cutoff()
    {
        var u = Active();
        u.IsActive = false;
        Assert.True(TokenRevocation.IsRevoked(u, Iat));
    }

    [Fact]
    public void Active_user_with_no_cutoff_is_not_revoked()
    {
        Assert.False(TokenRevocation.IsRevoked(Active(cutoff: null), Iat));
    }

    [Fact]
    public void Active_user_with_cutoff_after_iat_is_revoked()
    {
        // Cutoff was set AFTER the token was issued — token is
        // strictly older than the revocation moment, so reject.
        var cutoff = Iat.AddMinutes(5);
        Assert.True(TokenRevocation.IsRevoked(Active(cutoff: cutoff), Iat));
    }

    [Fact]
    public void Active_user_with_cutoff_before_iat_is_not_revoked()
    {
        // Cutoff was set BEFORE this token was issued — the token
        // post-dates the revocation, so it's a fresh token and
        // remains valid. (Re-login after revoke yields a new iat
        // strictly greater than the cutoff.)
        var cutoff = Iat.AddMinutes(-5);
        Assert.False(TokenRevocation.IsRevoked(Active(cutoff: cutoff), Iat));
    }

    [Fact]
    public void Active_user_with_cutoff_equal_to_iat_is_not_revoked()
    {
        // Boundary case: cutoff == iat. The rule is strict-less-than
        // (`iat < cutoff`), so a token issued at exactly the same
        // instant is still valid. Avoids spurious rejections at
        // millisecond clock granularity if a token happened to be
        // minted in the same instant as the revoke call.
        Assert.False(TokenRevocation.IsRevoked(Active(cutoff: Iat), Iat));
    }
}

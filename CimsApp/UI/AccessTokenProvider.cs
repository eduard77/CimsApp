using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.Tokens;
using CimsApp.Services.Tenancy;

namespace CimsApp.UI;

// ADR-0016 §4: BlazorApiClient calls /api/* using a short-lived JWT
// minted on demand from the cookie principal. The JWT never lives in
// the browser. Cached for the circuit until just before expiry, then
// re-minted from the same cookie claims.
public interface IAccessTokenProvider
{
    Task<string?> GetTokenAsync();
}

public sealed class AccessTokenProvider(
    AuthenticationStateProvider authProvider,
    IConfiguration cfg) : IAccessTokenProvider
{
    private const int LifetimeMinutes = 5;
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(1);

    private string?  _cached;
    private DateTime _expiresUtc = DateTime.MinValue;

    public async Task<string?> GetTokenAsync()
    {
        if (_cached is not null && DateTime.UtcNow + SafetyMargin < _expiresUtc)
            return _cached;

        var state = await authProvider.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated != true) return null;

        // Build the JWT directly from cookie claims. Sub / cims:org /
        // cims:role only — display fields stay out of the JWT just as
        // they stay out of the cookie (ADR-0016 §2).
        var claims = state.User.Claims
            .Where(c => c.Type == ClaimTypes.NameIdentifier
                     || c.Type == HttpTenantContext.OrganisationClaimType
                     || c.Type == HttpTenantContext.GlobalRoleClaimType)
            .ToList();

        var key      = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:AccessSecret"]!));
        var expires  = DateTime.UtcNow.AddMinutes(LifetimeMinutes);
        var token    = new JwtSecurityToken(
            issuer:             cfg["Jwt:Issuer"],
            audience:           cfg["Jwt:Audience"],
            claims:             claims,
            expires:            expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        _cached     = new JwtSecurityTokenHandler().WriteToken(token);
        _expiresUtc = expires;
        return _cached;
    }
}

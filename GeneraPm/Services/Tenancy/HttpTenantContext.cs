using System.Security.Claims;
using GeneraPm.Models;
using Microsoft.AspNetCore.Http;

namespace GeneraPm.Services.Tenancy;

/// <summary>
/// Reads the calling tenant from the authenticated ClaimsPrincipal.
/// UserId comes from the standard NameIdentifier claim; OrganisationId
/// comes from the custom "genera:org" claim and GlobalRole from the
/// custom "genera:role" claim emitted by AuthService.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public const string OrganisationClaimType = "genera:org";
    public const string GlobalRoleClaimType   = "genera:role";

    public Guid? UserId =>
        TryParseGuidClaim(ClaimTypes.NameIdentifier);

    public Guid? OrganisationId =>
        TryParseGuidClaim(OrganisationClaimType);

    public UserRole? GlobalRole
    {
        get
        {
            var value = accessor.HttpContext?.User?.FindFirstValue(GlobalRoleClaimType);
            return Enum.TryParse<UserRole>(value, ignoreCase: false, out var role)
                ? role
                : null;
        }
    }

    public bool IsSuperAdmin => GlobalRole == UserRole.SuperAdmin;

    private Guid? TryParseGuidClaim(string claimType)
    {
        var value = accessor.HttpContext?.User?.FindFirstValue(claimType);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}

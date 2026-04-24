using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace CimsApp.Services.Tenancy;

/// <summary>
/// Reads the calling tenant from the authenticated ClaimsPrincipal.
/// UserId comes from the standard NameIdentifier claim; OrganisationId
/// comes from the custom "cims:org" claim emitted by AuthService.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public const string OrganisationClaimType = "cims:org";

    public Guid? UserId =>
        TryParseClaim(ClaimTypes.NameIdentifier);

    public Guid? OrganisationId =>
        TryParseClaim(OrganisationClaimType);

    private Guid? TryParseClaim(string claimType)
    {
        var value = accessor.HttpContext?.User?.FindFirstValue(claimType);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}

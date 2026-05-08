using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using CimsApp.Data;

namespace CimsApp.UI;

// ADR-0016 §2: cookie carries only sub / cims:org / cims:role.
// Display fields (FirstName, LastName, OrgName) come from a single
// per-circuit DB lookup keyed on the cookie principal's sub.
//
// IgnoreQueryFilters because the Users tenant filter reads from
// HttpTenantContext, which sees null HttpContext inside the circuit.
// Loading by Id is a same-user lookup; no cross-tenant exposure.
public sealed class CurrentUserService(
    AuthenticationStateProvider authProvider,
    CimsDbContext               db)
{
    private bool   _loaded;
    private Guid   _userId;
    private string _firstName = "";
    private string _lastName  = "";
    private string _orgName   = "";

    public bool   IsLoaded => _loaded;
    public Guid   UserId   => _userId;
    public string FullName => string.IsNullOrEmpty(_firstName) && string.IsNullOrEmpty(_lastName)
        ? ""
        : $"{_firstName} {_lastName}".Trim();
    public string Initials => string.Concat(
        _firstName.Length > 0 ? char.ToUpperInvariant(_firstName[0]).ToString() : "",
        _lastName.Length  > 0 ? char.ToUpperInvariant(_lastName[0]).ToString()  : "");
    public string OrgName  => _orgName;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        var state = await authProvider.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated != true) return;

        var sub = state.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out _userId)) return;

        var user = await db.Users.IgnoreQueryFilters()
            .Include(u => u.Organisation)
            .FirstOrDefaultAsync(u => u.Id == _userId);
        if (user is null) return;

        _firstName = user.FirstName;
        _lastName  = user.LastName;
        _orgName   = user.Organisation.Name;
        _loaded    = true;
    }
}

using CimsApp.Models;

namespace CimsApp.UI;

// ADR-0016 §2: ephemeral UI state only. Auth identity lives on the
// cookie principal cascaded by CookieAuthenticationStateProvider;
// display fields (name, initials, org name) live on
// CurrentUserService; the JWT lives nowhere in this process other
// than the per-call AccessTokenProvider mint.
public class UiStateService
{
    public Project? CurrentProject { get; private set; }

    public event Action? OnChange;

    public void SetProject(Project? p) { CurrentProject = p; OnChange?.Invoke(); }
}

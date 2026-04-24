using CimsApp.Models;

namespace CimsApp.UI;

// Holds client-side login state and selected project
public class UiStateService
{
    public string?      AccessToken  { get; private set; }
    public string?      UserFullName { get; private set; }
    public string?      UserInitials { get; private set; }
    public string?      OrgName      { get; private set; }
    public Guid         UserId       { get; private set; }
    public Project?     CurrentProject { get; private set; }
    public bool         IsLoggedIn   => AccessToken != null;

    public event Action? OnChange;

    public void SetLogin(string token, Guid userId, string firstName, string lastName, string orgName)
    {
        AccessToken  = token;
        UserId       = userId;
        UserFullName = $"{firstName} {lastName}";
        UserInitials = $"{firstName.FirstOrDefault()}{lastName.FirstOrDefault()}".ToUpperInvariant();
        OrgName      = orgName;
        OnChange?.Invoke();
    }

    public void Logout()
    {
        AccessToken     = null;
        UserFullName    = null;
        UserInitials    = null;
        OrgName         = null;
        CurrentProject  = null;
        OnChange?.Invoke();
    }

    public void SetProject(Project? p) { CurrentProject = p; OnChange?.Invoke(); }
}

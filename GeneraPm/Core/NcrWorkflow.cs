using GeneraPm.Models;

namespace GeneraPm.Core;

/// <summary>
/// T-S20-03 NCR (Non-Conformance Report) state machine. 5-state
/// linear: Raised → Assigned → InProgress → Resolved → Closed.
/// Cancelled branch from Raised or Assigned only — once work is
/// In-Progress the issue is "real" and proceeds to Resolved or
/// Closed rather than Cancelled. 8th state machine in Genera PM
/// (Change / TenderPackage / CompensationEvent / GatewayPackage /
/// Dpia / Pdca / InspectionActivity / Ncr).
///
/// Pure-function shape — no IO, no DB, no DI. Mirrors the
/// established <see cref="ChangeWorkflow"/> /
/// <see cref="GatewayPackageWorkflow"/> /
/// <see cref="InspectionActivityWorkflow"/> patterns.
/// </summary>
public static class NcrWorkflow
{
    private static readonly Dictionary<NcrState, NcrState[]> Transitions = new()
    {
        [NcrState.Raised]     = [NcrState.Assigned,  NcrState.Cancelled],
        [NcrState.Assigned]   = [NcrState.InProgress, NcrState.Cancelled],
        [NcrState.InProgress] = [NcrState.Resolved],
        [NcrState.Resolved]   = [NcrState.Closed],
        [NcrState.Closed]     = [],
        [NcrState.Cancelled]  = [],
    };

    private static readonly Dictionary<(NcrState, NcrState), UserRole[]> TransitionRoles = new()
    {
        // Assigning an NCR to a responsible party — InformationManager+
        // (typical: site supervisor / IM assigns to subcontractor lead).
        [(NcrState.Raised,     NcrState.Assigned)] =
            [UserRole.InformationManager, UserRole.ProjectManager,
             UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Starting work — field-team can do this.
        [(NcrState.Assigned,   NcrState.InProgress)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Marking resolved — field-team work + IM verification path.
        [(NcrState.InProgress, NcrState.Resolved)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Closing requires a quality / IM sign-off — narrower role.
        [(NcrState.Resolved,   NcrState.Closed)] =
            [UserRole.InformationManager, UserRole.ProjectManager,
             UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Cancellation is a PM-level decision (formal close-without-fix).
        [(NcrState.Raised,   NcrState.Cancelled)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(NcrState.Assigned, NcrState.Cancelled)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    public static bool IsValidTransition(NcrState from, NcrState to) =>
        Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(NcrState from, NcrState to, UserRole role) =>
        TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool IsTerminal(NcrState s) =>
        Transitions.TryGetValue(s, out var a) && a.Length == 0;
}

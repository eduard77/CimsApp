using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// T-S11-03 DPIA state machine. PAFM-SD F.11 second bullet (UK
/// GDPR Art. 35). 4-state with branching: Drafting →
/// UnderReview → Approved | RequiresChanges; RequiresChanges →
/// Drafting (re-work). Approved is terminal. Pure-function
/// shape — no IO, no DB, no DI.
///
/// Pattern reuse from S5 ChangeWorkflow / S6
/// CompensationEventWorkflow / S10 GatewayPackageWorkflow.
/// // GDPR ref: Art. 35 (Data Protection Impact Assessment).
/// </summary>
public static class DpiaWorkflow
{
    private static readonly Dictionary<DpiaState, DpiaState[]> Transitions = new()
    {
        [DpiaState.Drafting]         = [DpiaState.UnderReview],
        [DpiaState.UnderReview]      = [DpiaState.Approved, DpiaState.RequiresChanges],
        [DpiaState.RequiresChanges]  = [DpiaState.Drafting],
        [DpiaState.Approved]         = [],
    };

    private static readonly Dictionary<(DpiaState, DpiaState), UserRole[]> TransitionRoles = new()
    {
        [(DpiaState.Drafting, DpiaState.UnderReview)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(DpiaState.UnderReview, DpiaState.Approved)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(DpiaState.UnderReview, DpiaState.RequiresChanges)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(DpiaState.RequiresChanges, DpiaState.Drafting)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    public static bool IsValidTransition(DpiaState from, DpiaState to) =>
        Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(DpiaState from, DpiaState to, UserRole role) =>
        TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool IsTerminal(DpiaState s) =>
        Transitions.TryGetValue(s, out var a) && a.Length == 0;
}

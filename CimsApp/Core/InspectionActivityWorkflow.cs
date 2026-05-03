using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// T-S13-02 InspectionActivity state machine. PAFM-SD F.13
/// fourth bullet (CIMS quality record half). 4-state linear
/// with two terminal end-states: Scheduled → InProgress →
/// (Completed | Cancelled). Cancelled also reachable from
/// Scheduled directly when an inspection is dropped before
/// it starts. Pure-function shape — no IO, no DB, no DI.
///
/// 7th state machine in CIMS following the established shape
/// (Change / TenderPackage / CompensationEvent /
/// GatewayPackage / Dpia / Pdca / InspectionActivity). v1.0
/// shape is intentionally simple — Ch 47 may extend with
/// AwaitingApproval / ReportedNonCompliant / etc. when it
/// arrives; v1.1 / B-086 handles that.
/// </summary>
public static class InspectionActivityWorkflow
{
    private static readonly Dictionary<InspectionActivityStatus, InspectionActivityStatus[]> Transitions = new()
    {
        [InspectionActivityStatus.Scheduled]  = [InspectionActivityStatus.InProgress, InspectionActivityStatus.Cancelled],
        [InspectionActivityStatus.InProgress] = [InspectionActivityStatus.Completed,  InspectionActivityStatus.Cancelled],
        [InspectionActivityStatus.Completed]  = [],
        [InspectionActivityStatus.Cancelled]  = [],
    };

    private static readonly Dictionary<(InspectionActivityStatus, InspectionActivityStatus), UserRole[]> TransitionRoles = new()
    {
        // Starting + completing an inspection is field-team work.
        [(InspectionActivityStatus.Scheduled,  InspectionActivityStatus.InProgress)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(InspectionActivityStatus.InProgress, InspectionActivityStatus.Completed)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Cancelling an inspection is PM+ — formal decision.
        [(InspectionActivityStatus.Scheduled,  InspectionActivityStatus.Cancelled)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(InspectionActivityStatus.InProgress, InspectionActivityStatus.Cancelled)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    public static bool IsValidTransition(InspectionActivityStatus from, InspectionActivityStatus to) =>
        Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(InspectionActivityStatus from, InspectionActivityStatus to, UserRole role) =>
        TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool IsTerminal(InspectionActivityStatus s) =>
        Transitions.TryGetValue(s, out var a) && a.Length == 0;
}

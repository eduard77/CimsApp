using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// T-S10-03 GatewayPackage state machine. PAFM-SD F.10 second
/// bullet (BSA 2022 Gateway 1/2/3 submission packages).
/// 3-state linear: Drafting → Submitted → Decided. Decided is
/// terminal (Approved | ApprovedWithConditions | Refused).
/// Pure-function shape — no IO, no DB, no DI.
///
/// Pattern reuse from S5 ChangeWorkflow / S6 TenderPackageWorkflow:
/// - Transitions dictionary captures the allowed (from,to) pairs.
/// - TransitionRoles dictionary captures the role floor for each
///   pair.
/// - HasMinimumRole reuses the S0 role-hierarchy helper.
/// </summary>
public static class GatewayPackageWorkflow
{
    private static readonly Dictionary<GatewayPackageState, GatewayPackageState[]> Transitions = new()
    {
        [GatewayPackageState.Drafting]  = [GatewayPackageState.Submitted],
        [GatewayPackageState.Submitted] = [GatewayPackageState.Decided],
        [GatewayPackageState.Decided]   = [],
    };

    private static readonly Dictionary<(GatewayPackageState, GatewayPackageState), UserRole[]> TransitionRoles = new()
    {
        [(GatewayPackageState.Drafting,  GatewayPackageState.Submitted)] =
            [UserRole.InformationManager, UserRole.ProjectManager,
             UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(GatewayPackageState.Submitted, GatewayPackageState.Decided)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    public static bool IsValidTransition(GatewayPackageState from, GatewayPackageState to) =>
        Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(GatewayPackageState from, GatewayPackageState to, UserRole role) =>
        TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool IsTerminal(GatewayPackageState s) =>
        Transitions.TryGetValue(s, out var a) && a.Length == 0;
}

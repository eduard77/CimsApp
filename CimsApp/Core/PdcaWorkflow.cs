using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// T-S12-02 PDCA state machine. PAFM-SD F.12 first bullet
/// (Plan-Do-Check-Act continuous improvement). 5-state with a
/// cycle-back: Plan → Do → Check → Act → (Plan for next cycle |
/// Closed). Closed is terminal. Pure-function shape — no IO,
/// no DB, no DI.
///
/// Pattern reuse from S5/S6/S10/S11 workflow modules. Now the
/// 6th state machine in CIMS following the same shape.
/// </summary>
public static class PdcaWorkflow
{
    private static readonly Dictionary<PdcaState, PdcaState[]> Transitions = new()
    {
        [PdcaState.Plan]   = [PdcaState.Do,    PdcaState.Closed],
        [PdcaState.Do]     = [PdcaState.Check, PdcaState.Closed],
        [PdcaState.Check]  = [PdcaState.Act,   PdcaState.Closed],
        [PdcaState.Act]    = [PdcaState.Plan,  PdcaState.Closed],
        [PdcaState.Closed] = [],
    };

    private static readonly Dictionary<(PdcaState, PdcaState), UserRole[]> TransitionRoles = new()
    {
        // The PDCA forward path is open to TaskTeamMember+ —
        // continuous improvement is a team activity.
        [(PdcaState.Plan,  PdcaState.Do)]    =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(PdcaState.Do,    PdcaState.Check)] =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(PdcaState.Check, PdcaState.Act)]   =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Cycle-back to Plan increments the cycle counter; team-level.
        [(PdcaState.Act,   PdcaState.Plan)]  =
            [UserRole.TaskTeamMember, UserRole.InformationManager,
             UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        // Closing an Improvement is PM+ — the formal "this
        // improvement has been completed or abandoned" decision.
        [(PdcaState.Plan,  PdcaState.Closed)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(PdcaState.Do,    PdcaState.Closed)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(PdcaState.Check, PdcaState.Closed)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
        [(PdcaState.Act,   PdcaState.Closed)] =
            [UserRole.ProjectManager, UserRole.OrgAdmin, UserRole.SuperAdmin],
    };

    public static bool IsValidTransition(PdcaState from, PdcaState to) =>
        Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(PdcaState from, PdcaState to, UserRole role) =>
        TransitionRoles.TryGetValue((from, to), out var p) && p.Contains(role);

    public static bool IsTerminal(PdcaState s) =>
        Transitions.TryGetValue(s, out var a) && a.Length == 0;
}

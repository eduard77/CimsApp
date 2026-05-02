using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Pure-function state-machine for the ChangeRequest workflow
/// (T-S5-03, PAFM-SD F.6 first bullet — "raise, assess, approve,
/// implement, close"). Mirrors <see cref="CdeStateMachine"/> in
/// shape: no DB, no DI; caller passes (from, to, role) and gets a
/// boolean answer. Service layer wraps with persistence + audit.
///
/// State machine:
///
///   Raised ──assess──▶ Assessed ──approve──▶ Approved ──implement──▶ Implemented ──close──▶ Closed
///      │                  │                     │
///      └─reject─▶ Rejected └─reject─▶ Rejected   │ (no path back from Approved)
///                                                │
///                                                └─ Approve carries an optional `CreateVariation` flag in the
///                                                   service layer (T-S5-06), spawning an S1 Variation atomically.
///
/// Notes:
/// - Reject is allowed from Raised or Assessed only. Once a CR is
///   Approved, the only forward path is Implemented → Closed.
/// - Rejected and Closed are terminal states (no outbound transitions).
/// - Skipping Assessed (Raised → Approved direct) is NOT allowed in
///   v1.0; the assess step is what produces the impact summaries.
///   v1.1 candidate: an "expedited path" with PM+ approval bypassing
///   Assessed for emergency changes, similar to NEC4 compensation
///   event Project Manager's own assessment.
/// </summary>
public static class ChangeWorkflow
{
    private static readonly Dictionary<ChangeRequestState, ChangeRequestState[]> Transitions = new()
    {
        [ChangeRequestState.Raised]      = [ChangeRequestState.Assessed,    ChangeRequestState.Rejected],
        [ChangeRequestState.Assessed]    = [ChangeRequestState.Approved,    ChangeRequestState.Rejected],
        [ChangeRequestState.Approved]    = [ChangeRequestState.Implemented],
        [ChangeRequestState.Implemented] = [ChangeRequestState.Closed],
        [ChangeRequestState.Rejected]    = [],
        [ChangeRequestState.Closed]      = [],
    };

    /// <summary>
    /// Per-transition role gates. v1.0 implementation per kickoff:
    /// - Raise: TaskTeamMember+ (the floor; anyone on the project can
    ///   raise a CR — this is the equivalent of an RFI for scope).
    /// - Assess: InformationManager+ (the IM produces impact summaries
    ///   and BSA categorisation tagging).
    /// - Approve / Reject / Implement / Close: ProjectManager+ (the
    ///   delegation-routing F.6 fourth bullet — v1.0 simple form;
    ///   per-tenant chains → v1.1 / B-036).
    ///
    /// Note: there's no Raise transition in this table — Raise is the
    /// constructor (no `from` state). The Raise role gate is enforced
    /// at the controller layer via HasMinimumRole(role, TaskTeamMember).
    /// </summary>
    private static readonly Dictionary<(ChangeRequestState, ChangeRequestState), UserRole> TransitionMinimumRole = new()
    {
        [(ChangeRequestState.Raised,      ChangeRequestState.Assessed)]    = UserRole.InformationManager,
        [(ChangeRequestState.Raised,      ChangeRequestState.Rejected)]    = UserRole.ProjectManager,
        [(ChangeRequestState.Assessed,    ChangeRequestState.Approved)]    = UserRole.ProjectManager,
        [(ChangeRequestState.Assessed,    ChangeRequestState.Rejected)]    = UserRole.ProjectManager,
        [(ChangeRequestState.Approved,    ChangeRequestState.Implemented)] = UserRole.ProjectManager,
        [(ChangeRequestState.Implemented, ChangeRequestState.Closed)]      = UserRole.ProjectManager,
    };

    /// <summary>True if the transition is structurally valid
    /// (ignoring role).</summary>
    public static bool IsValidTransition(ChangeRequestState from, ChangeRequestState to)
        => Transitions.TryGetValue(from, out var a) && a.Contains(to);

    /// <summary>True if the transition is structurally valid AND the
    /// caller's role meets the minimum for that transition.</summary>
    public static bool CanTransition(ChangeRequestState from, ChangeRequestState to, UserRole role)
    {
        if (!IsValidTransition(from, to)) return false;
        if (!TransitionMinimumRole.TryGetValue((from, to), out var minRole)) return false;
        return CdeStateMachine.HasMinimumRole(role, minRole);
    }

    /// <summary>The state-graph successors of <paramref name="from"/>
    /// (ignores role). Empty array for terminal states (Rejected,
    /// Closed).</summary>
    public static ChangeRequestState[] GetValidTransitions(ChangeRequestState from)
        => Transitions.TryGetValue(from, out var a) ? a : [];

    /// <summary>The state-graph successors of <paramref name="from"/>
    /// the caller's role can actually perform. Useful for UI
    /// "available actions" rendering.</summary>
    public static ChangeRequestState[] GetAvailableTransitions(ChangeRequestState from, UserRole role)
        => GetValidTransitions(from).Where(to => CanTransition(from, to, role)).ToArray();

    /// <summary>True for terminal states (Rejected, Closed). The
    /// service uses this to short-circuit further-transition
    /// attempts with a ConflictException.</summary>
    public static bool IsTerminal(ChangeRequestState s) =>
        s == ChangeRequestState.Rejected || s == ChangeRequestState.Closed;
}

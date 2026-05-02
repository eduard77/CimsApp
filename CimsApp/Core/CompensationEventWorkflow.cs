using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Pure-function state machine for the NEC4 compensation-event
/// workflow (T-S6-08, PAFM-SD F.7 fifth bullet — NEC4 clause 60.1).
/// Mirrors <see cref="ChangeWorkflow"/> in shape — caller passes
/// (from, to, role) and gets a boolean answer. Service layer wraps
/// with persistence + audit.
///
/// State machine:
///
///   Notified ──quote──▶ Quoted ──accept──▶ Accepted ──implement──▶ Implemented
///       │                  │
///       └─reject─▶ Rejected └─reject─▶ Rejected
///
/// Notes:
/// - Notified → Rejected covers NEC4 clause 61.4 ("PM notifies it
///   is not a CE") — PM rejects the notification before quoting.
/// - Quoted → Rejected is the standard rejection path after the
///   contractor has submitted a quotation but the PM doesn't
///   accept it.
/// - Once Accepted, the only forward path is Implemented.
/// - Rejected and Implemented are terminal.
/// - Skipping Quoted (Notified → Accepted direct) is NOT allowed
///   in v1.0 — every CE must have a quotation before acceptance.
///
/// v1.0 limitations explicitly deferred:
/// - PM 4-week notification deadline + deemed-acceptance rules
///   → v1.1 / B-048.
/// - Contractor 3-week quotation deadline + deemed-acceptance
///   after PM non-response → v1.1 / B-049.
/// - Risk-allowance / disallowance pricing rules at Quote →
///   v1.1 / B-050.
/// </summary>
public static class CompensationEventWorkflow
{
    private static readonly Dictionary<CompensationEventState, CompensationEventState[]> Transitions = new()
    {
        [CompensationEventState.Notified]    = [CompensationEventState.Quoted,    CompensationEventState.Rejected],
        [CompensationEventState.Quoted]      = [CompensationEventState.Accepted,  CompensationEventState.Rejected],
        [CompensationEventState.Accepted]    = [CompensationEventState.Implemented],
        [CompensationEventState.Rejected]    = [],
        [CompensationEventState.Implemented] = [],
    };

    /// <summary>v1.0 role gates per transition:
    /// - Notified → Quoted: TaskTeamMember+ (contractor-side
    ///   quotation submission; in v1.0 the project admin records
    ///   it as a proxy).
    /// - Notified → Rejected: ProjectManager+ (clause 61.4 PM
    ///   determination that the event is not a CE).
    /// - Quoted → Accepted: ProjectManager+.
    /// - Quoted → Rejected: ProjectManager+.
    /// - Accepted → Implemented: ProjectManager+.
    ///
    /// Notify (the constructor) has no `from` state and is gated at
    /// the controller layer (TaskTeamMember+ — same floor as RFI /
    /// Action raise).
    /// </summary>
    private static readonly Dictionary<(CompensationEventState, CompensationEventState), UserRole> TransitionMinimumRole = new()
    {
        [(CompensationEventState.Notified,    CompensationEventState.Quoted)]      = UserRole.TaskTeamMember,
        [(CompensationEventState.Notified,    CompensationEventState.Rejected)]    = UserRole.ProjectManager,
        [(CompensationEventState.Quoted,      CompensationEventState.Accepted)]    = UserRole.ProjectManager,
        [(CompensationEventState.Quoted,      CompensationEventState.Rejected)]    = UserRole.ProjectManager,
        [(CompensationEventState.Accepted,    CompensationEventState.Implemented)] = UserRole.ProjectManager,
    };

    public static bool IsValidTransition(CompensationEventState from, CompensationEventState to)
        => Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(CompensationEventState from, CompensationEventState to, UserRole role)
    {
        if (!IsValidTransition(from, to)) return false;
        if (!TransitionMinimumRole.TryGetValue((from, to), out var minRole)) return false;
        return CdeStateMachine.HasMinimumRole(role, minRole);
    }

    public static CompensationEventState[] GetValidTransitions(CompensationEventState from)
        => Transitions.TryGetValue(from, out var a) ? a : [];

    public static CompensationEventState[] GetAvailableTransitions(CompensationEventState from, UserRole role)
        => GetValidTransitions(from).Where(to => CanTransition(from, to, role)).ToArray();

    public static bool IsTerminal(CompensationEventState s) =>
        s == CompensationEventState.Rejected || s == CompensationEventState.Implemented;
}

using CimsApp.Models;

namespace CimsApp.Core;

/// <summary>
/// Pure-function state machine for the TenderPackage workflow
/// (T-S6-03, PAFM-SD F.7 second bullet — "Tender package
/// creation"). Mirrors <see cref="ChangeWorkflow"/> in shape.
///
/// State machine:
///
///   Draft ──issue──▶ Issued ──close──▶ Closed
///
/// Notes:
/// - Draft is the working-state where the package details (Name,
///   Description, EstimatedValue, dates, evaluation criteria) can
///   be edited freely.
/// - Issue freezes the package — bidders are now working off the
///   issued details. Editing is rejected by the service after Issue.
/// - Close is reached two ways:
///   (a) explicit close (abandon-without-award) via the
///       /close endpoint (T-S6-03);
///   (b) Award workflow (T-S6-06) closes the package atomically as
///       part of the Award→Contract spawn.
/// - There is no path back from Issued to Draft in v1.0. If a
///   tender is issued in error, the operator must Close the
///   package and create a fresh one. v1.1 candidate: an "amend
///   and re-issue" path (B-NNN — to be added inline if a real
///   workflow demands it).
/// </summary>
public static class TenderPackageWorkflow
{
    private static readonly Dictionary<TenderPackageState, TenderPackageState[]> Transitions = new()
    {
        [TenderPackageState.Draft]  = [TenderPackageState.Issued],
        [TenderPackageState.Issued] = [TenderPackageState.Closed],
        [TenderPackageState.Closed] = [],
    };

    /// <summary>v1.0 role gates per transition:
    /// - Draft → Issued: ProjectManager+ (issuing locks the package).
    /// - Issued → Closed: ProjectManager+ (close = abandon-without-award
    ///   OR called from Award workflow; both PM+).
    ///
    /// Create / Update of a Draft package is gated at the controller
    /// layer (TaskTeamMember+); Deactivate of a Draft is also at the
    /// controller layer (ProjectManager+).
    /// </summary>
    private static readonly Dictionary<(TenderPackageState, TenderPackageState), UserRole> TransitionMinimumRole = new()
    {
        [(TenderPackageState.Draft,  TenderPackageState.Issued)] = UserRole.ProjectManager,
        [(TenderPackageState.Issued, TenderPackageState.Closed)] = UserRole.ProjectManager,
    };

    public static bool IsValidTransition(TenderPackageState from, TenderPackageState to)
        => Transitions.TryGetValue(from, out var a) && a.Contains(to);

    public static bool CanTransition(TenderPackageState from, TenderPackageState to, UserRole role)
    {
        if (!IsValidTransition(from, to)) return false;
        if (!TransitionMinimumRole.TryGetValue((from, to), out var minRole)) return false;
        return CdeStateMachine.HasMinimumRole(role, minRole);
    }

    public static TenderPackageState[] GetValidTransitions(TenderPackageState from)
        => Transitions.TryGetValue(from, out var a) ? a : [];

    public static TenderPackageState[] GetAvailableTransitions(TenderPackageState from, UserRole role)
        => GetValidTransitions(from).Where(to => CanTransition(from, to, role)).ToArray();

    public static bool IsTerminal(TenderPackageState s) => s == TenderPackageState.Closed;
}

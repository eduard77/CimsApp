using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for `CdeStateMachine`. The CDE state
/// machine is the load-bearing authorisation gate for document
/// transitions (`POST /documents/{id}/transition` calls
/// `IsValidTransition` + `CanTransition`); the role-hierarchy
/// helper is also used by every project-role gate across the
/// API. Direct coverage was missing — exercised only indirectly
/// through `DocumentsService` integration tests.
/// </summary>
public class CdeStateMachineTests
{
    // ── IsValidTransition: structural transitions per ISO 19650 ──────────────

    [Theory]
    [InlineData(CdeState.WorkInProgress, CdeState.Shared,         true)]
    [InlineData(CdeState.WorkInProgress, CdeState.Voided,         true)]
    [InlineData(CdeState.WorkInProgress, CdeState.Published,      false)]    // must go via Shared
    [InlineData(CdeState.WorkInProgress, CdeState.Archived,       false)]
    [InlineData(CdeState.Shared,         CdeState.WorkInProgress, true)]    // can revert
    [InlineData(CdeState.Shared,         CdeState.Published,      true)]
    [InlineData(CdeState.Shared,         CdeState.Voided,         true)]
    [InlineData(CdeState.Shared,         CdeState.Archived,       false)]
    [InlineData(CdeState.Published,      CdeState.Archived,       true)]
    [InlineData(CdeState.Published,      CdeState.Voided,         true)]
    [InlineData(CdeState.Published,      CdeState.WorkInProgress, false)]   // can't reopen
    [InlineData(CdeState.Published,      CdeState.Shared,         false)]
    [InlineData(CdeState.Archived,       CdeState.WorkInProgress, false)]   // terminal
    [InlineData(CdeState.Archived,       CdeState.Voided,         false)]
    [InlineData(CdeState.Voided,         CdeState.WorkInProgress, false)]   // terminal
    public void IsValidTransition(CdeState from, CdeState to, bool expected)
    {
        Assert.Equal(expected, CdeStateMachine.IsValidTransition(from, to));
    }

    [Fact]
    public void IsValidTransition_self_loops_are_invalid()
    {
        // Self-transitions aren't in the allowlist for any state.
        foreach (var s in Enum.GetValues<CdeState>())
            Assert.False(CdeStateMachine.IsValidTransition(s, s),
                $"Self-loop at {s} should be invalid");
    }

    // ── CanTransition: role allowlist per (from, to) pair ────────────────────

    [Fact]
    public void Wip_to_Shared_allows_TaskTeamMember_and_above()
    {
        // The most permissive transition — TaskTeamMember and
        // everyone above can move WIP → Shared.
        Assert.True(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Shared, UserRole.TaskTeamMember));
        Assert.True(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Shared, UserRole.InformationManager));
        Assert.True(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Shared, UserRole.SuperAdmin));
        // Lower roles (Viewer, ClientRep) blocked.
        Assert.False(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Shared, UserRole.Viewer));
        Assert.False(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Shared, UserRole.ClientRep));
    }

    [Fact]
    public void Shared_to_Published_requires_InformationManager_or_above()
    {
        // Publishing is the contractual-issue moment — IM and above only.
        Assert.True(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.InformationManager));
        Assert.True(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.ProjectManager));
        Assert.True(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.OrgAdmin));
        Assert.True(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.SuperAdmin));
        // Below IM blocked.
        Assert.False(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.TaskTeamMember));
        Assert.False(CdeStateMachine.CanTransition(CdeState.Shared,
            CdeState.Published, UserRole.Viewer));
    }

    [Fact]
    public void Published_to_Voided_requires_OrgAdmin_or_above()
    {
        // Voiding a Published doc is the most consequential
        // transition — OrgAdmin and SuperAdmin only.
        Assert.True(CdeStateMachine.CanTransition(CdeState.Published,
            CdeState.Voided, UserRole.OrgAdmin));
        Assert.True(CdeStateMachine.CanTransition(CdeState.Published,
            CdeState.Voided, UserRole.SuperAdmin));
        // Even ProjectManager is blocked.
        Assert.False(CdeStateMachine.CanTransition(CdeState.Published,
            CdeState.Voided, UserRole.ProjectManager));
        Assert.False(CdeStateMachine.CanTransition(CdeState.Published,
            CdeState.Voided, UserRole.InformationManager));
    }

    [Fact]
    public void CanTransition_returns_false_for_invalid_transitions_regardless_of_role()
    {
        // A SuperAdmin can't move from a terminal state — there
        // is no row for that transition in the allowlist.
        Assert.False(CdeStateMachine.CanTransition(CdeState.Archived,
            CdeState.WorkInProgress, UserRole.SuperAdmin));
        Assert.False(CdeStateMachine.CanTransition(CdeState.Voided,
            CdeState.WorkInProgress, UserRole.SuperAdmin));
        // A SuperAdmin can't skip Shared on the way to Published.
        Assert.False(CdeStateMachine.CanTransition(CdeState.WorkInProgress,
            CdeState.Published, UserRole.SuperAdmin));
    }

    // ── HasMinimumRole: role hierarchy ───────────────────────────────────────

    [Theory]
    [InlineData(UserRole.SuperAdmin,         UserRole.SuperAdmin,         true)]
    [InlineData(UserRole.SuperAdmin,         UserRole.OrgAdmin,           true)]
    [InlineData(UserRole.SuperAdmin,         UserRole.Viewer,             true)]
    [InlineData(UserRole.OrgAdmin,           UserRole.OrgAdmin,           true)]
    [InlineData(UserRole.OrgAdmin,           UserRole.SuperAdmin,         false)]
    [InlineData(UserRole.ProjectManager,     UserRole.TaskTeamMember,     true)]
    [InlineData(UserRole.ProjectManager,     UserRole.OrgAdmin,           false)]
    [InlineData(UserRole.TaskTeamMember,     UserRole.TaskTeamMember,     true)]
    [InlineData(UserRole.TaskTeamMember,     UserRole.InformationManager, false)]
    [InlineData(UserRole.Viewer,             UserRole.Viewer,             true)]
    [InlineData(UserRole.Viewer,             UserRole.ClientRep,          false)]
    [InlineData(UserRole.InformationManager, UserRole.ProjectManager,     false)]   // IM is below PM
    [InlineData(UserRole.ProjectManager,     UserRole.InformationManager, true)]    // PM is above IM
    public void HasMinimumRole(UserRole role, UserRole minimum, bool expected)
    {
        Assert.Equal(expected, CdeStateMachine.HasMinimumRole(role, minimum));
    }

    // ── GetValidTransitions: full set per state ──────────────────────────────

    [Fact]
    public void GetValidTransitions_for_each_state_matches_the_table()
    {
        Assert.Equal(
            new[] { CdeState.Shared, CdeState.Voided },
            CdeStateMachine.GetValidTransitions(CdeState.WorkInProgress));
        Assert.Equal(
            new[] { CdeState.WorkInProgress, CdeState.Published, CdeState.Voided },
            CdeStateMachine.GetValidTransitions(CdeState.Shared));
        Assert.Equal(
            new[] { CdeState.Archived, CdeState.Voided },
            CdeStateMachine.GetValidTransitions(CdeState.Published));
        Assert.Empty(CdeStateMachine.GetValidTransitions(CdeState.Archived));
        Assert.Empty(CdeStateMachine.GetValidTransitions(CdeState.Voided));
    }
}

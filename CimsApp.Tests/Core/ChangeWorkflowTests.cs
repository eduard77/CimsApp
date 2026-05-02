using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="ChangeWorkflow"/> (T-S5-03).
/// No DB / DI / IO. Cover happy-path forward transitions, the
/// reject branch, terminal states, role-gate boundaries, and the
/// "no skipping Assessed" rule.
/// </summary>
public class ChangeWorkflowTests
{
    // ── Structural transitions ──────────────────────────────────────

    [Theory]
    [InlineData(ChangeRequestState.Raised,      ChangeRequestState.Assessed)]
    [InlineData(ChangeRequestState.Raised,      ChangeRequestState.Rejected)]
    [InlineData(ChangeRequestState.Assessed,    ChangeRequestState.Approved)]
    [InlineData(ChangeRequestState.Assessed,    ChangeRequestState.Rejected)]
    [InlineData(ChangeRequestState.Approved,    ChangeRequestState.Implemented)]
    [InlineData(ChangeRequestState.Implemented, ChangeRequestState.Closed)]
    public void IsValidTransition_accepts_workflow_edges(
        ChangeRequestState from, ChangeRequestState to)
    {
        Assert.True(ChangeWorkflow.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(ChangeRequestState.Raised,      ChangeRequestState.Approved)]    // skip Assessed
    [InlineData(ChangeRequestState.Raised,      ChangeRequestState.Implemented)] // skip more
    [InlineData(ChangeRequestState.Assessed,    ChangeRequestState.Implemented)] // skip Approved
    [InlineData(ChangeRequestState.Approved,    ChangeRequestState.Rejected)]    // no reject after approve
    [InlineData(ChangeRequestState.Implemented, ChangeRequestState.Rejected)]    // ditto
    [InlineData(ChangeRequestState.Approved,    ChangeRequestState.Assessed)]    // backwards
    [InlineData(ChangeRequestState.Rejected,    ChangeRequestState.Approved)]    // from terminal
    [InlineData(ChangeRequestState.Closed,      ChangeRequestState.Approved)]    // from terminal
    public void IsValidTransition_rejects_invalid_edges(
        ChangeRequestState from, ChangeRequestState to)
    {
        Assert.False(ChangeWorkflow.IsValidTransition(from, to));
    }

    [Fact]
    public void Rejected_is_terminal()
    {
        Assert.Empty(ChangeWorkflow.GetValidTransitions(ChangeRequestState.Rejected));
        Assert.True(ChangeWorkflow.IsTerminal(ChangeRequestState.Rejected));
    }

    [Fact]
    public void Closed_is_terminal()
    {
        Assert.Empty(ChangeWorkflow.GetValidTransitions(ChangeRequestState.Closed));
        Assert.True(ChangeWorkflow.IsTerminal(ChangeRequestState.Closed));
    }

    [Theory]
    [InlineData(ChangeRequestState.Raised)]
    [InlineData(ChangeRequestState.Assessed)]
    [InlineData(ChangeRequestState.Approved)]
    [InlineData(ChangeRequestState.Implemented)]
    public void Non_terminal_states_have_outgoing_transitions(ChangeRequestState s)
    {
        Assert.NotEmpty(ChangeWorkflow.GetValidTransitions(s));
        Assert.False(ChangeWorkflow.IsTerminal(s));
    }

    // ── Role-gate boundaries ────────────────────────────────────────

    [Fact]
    public void Assess_requires_InformationManager_or_above()
    {
        // Assessor must be IM+; TaskTeamMember can raise but not assess.
        Assert.False(ChangeWorkflow.CanTransition(
            ChangeRequestState.Raised, ChangeRequestState.Assessed, UserRole.TaskTeamMember));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Raised, ChangeRequestState.Assessed, UserRole.InformationManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Raised, ChangeRequestState.Assessed, UserRole.ProjectManager));
    }

    [Fact]
    public void Approve_requires_ProjectManager_or_above()
    {
        Assert.False(ChangeWorkflow.CanTransition(
            ChangeRequestState.Assessed, ChangeRequestState.Approved, UserRole.InformationManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Assessed, ChangeRequestState.Approved, UserRole.ProjectManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Assessed, ChangeRequestState.Approved, UserRole.OrgAdmin));
    }

    [Fact]
    public void Reject_requires_ProjectManager_or_above_from_either_Raised_or_Assessed()
    {
        Assert.False(ChangeWorkflow.CanTransition(
            ChangeRequestState.Raised, ChangeRequestState.Rejected, UserRole.InformationManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Raised, ChangeRequestState.Rejected, UserRole.ProjectManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Assessed, ChangeRequestState.Rejected, UserRole.ProjectManager));
    }

    [Fact]
    public void Implement_and_Close_require_ProjectManager_or_above()
    {
        Assert.False(ChangeWorkflow.CanTransition(
            ChangeRequestState.Approved, ChangeRequestState.Implemented, UserRole.InformationManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Approved, ChangeRequestState.Implemented, UserRole.ProjectManager));
        Assert.True(ChangeWorkflow.CanTransition(
            ChangeRequestState.Implemented, ChangeRequestState.Closed, UserRole.ProjectManager));
    }

    [Theory]
    [InlineData(UserRole.SuperAdmin)]
    [InlineData(UserRole.OrgAdmin)]
    [InlineData(UserRole.ProjectManager)]
    public void Highest_roles_can_drive_every_transition(UserRole role)
    {
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Raised,      ChangeRequestState.Assessed,    role));
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Raised,      ChangeRequestState.Rejected,    role));
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Assessed,    ChangeRequestState.Approved,    role));
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Assessed,    ChangeRequestState.Rejected,    role));
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Approved,    ChangeRequestState.Implemented, role));
        Assert.True(ChangeWorkflow.CanTransition(ChangeRequestState.Implemented, ChangeRequestState.Closed,      role));
    }

    [Fact]
    public void Viewer_and_ClientRep_cannot_drive_any_transition()
    {
        foreach (var role in new[] { UserRole.Viewer, UserRole.ClientRep })
        {
            Assert.False(ChangeWorkflow.CanTransition(ChangeRequestState.Raised,      ChangeRequestState.Assessed,    role));
            Assert.False(ChangeWorkflow.CanTransition(ChangeRequestState.Assessed,    ChangeRequestState.Approved,    role));
            Assert.False(ChangeWorkflow.CanTransition(ChangeRequestState.Approved,    ChangeRequestState.Implemented, role));
            Assert.False(ChangeWorkflow.CanTransition(ChangeRequestState.Implemented, ChangeRequestState.Closed,      role));
        }
    }

    // ── GetAvailableTransitions ─────────────────────────────────────

    [Fact]
    public void GetAvailableTransitions_for_TaskTeamMember_at_Raised_is_empty()
    {
        // TaskTeamMember can raise (constructor, not in this table) but
        // cannot drive any transition.
        Assert.Empty(ChangeWorkflow.GetAvailableTransitions(
            ChangeRequestState.Raised, UserRole.TaskTeamMember));
    }

    [Fact]
    public void GetAvailableTransitions_for_IM_at_Raised_is_only_Assessed()
    {
        var avail = ChangeWorkflow.GetAvailableTransitions(
            ChangeRequestState.Raised, UserRole.InformationManager);
        Assert.Single(avail);
        Assert.Contains(ChangeRequestState.Assessed, avail);
    }

    [Fact]
    public void GetAvailableTransitions_for_PM_at_Raised_is_both_Assessed_and_Rejected()
    {
        var avail = ChangeWorkflow.GetAvailableTransitions(
            ChangeRequestState.Raised, UserRole.ProjectManager);
        Assert.Equal(2, avail.Length);
        Assert.Contains(ChangeRequestState.Assessed, avail);
        Assert.Contains(ChangeRequestState.Rejected, avail);
    }

    [Fact]
    public void GetAvailableTransitions_at_terminal_state_is_empty_for_every_role()
    {
        foreach (var role in (UserRole[])Enum.GetValues(typeof(UserRole)))
        {
            Assert.Empty(ChangeWorkflow.GetAvailableTransitions(ChangeRequestState.Rejected, role));
            Assert.Empty(ChangeWorkflow.GetAvailableTransitions(ChangeRequestState.Closed, role));
        }
    }
}

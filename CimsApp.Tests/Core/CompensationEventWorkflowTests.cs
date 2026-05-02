using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="CompensationEventWorkflow"/>
/// (T-S6-08). Same shape as <see cref="ChangeWorkflowTests"/>;
/// 5-state machine with the Notified→Rejected branch covering NEC4
/// clause 61.4.
/// </summary>
public class CompensationEventWorkflowTests
{
    [Theory]
    [InlineData(CompensationEventState.Notified, CompensationEventState.Quoted)]
    [InlineData(CompensationEventState.Notified, CompensationEventState.Rejected)]
    [InlineData(CompensationEventState.Quoted,   CompensationEventState.Accepted)]
    [InlineData(CompensationEventState.Quoted,   CompensationEventState.Rejected)]
    [InlineData(CompensationEventState.Accepted, CompensationEventState.Implemented)]
    public void IsValidTransition_accepts_workflow_edges(
        CompensationEventState from, CompensationEventState to)
    {
        Assert.True(CompensationEventWorkflow.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(CompensationEventState.Notified,    CompensationEventState.Accepted)]    // skip Quoted
    [InlineData(CompensationEventState.Notified,    CompensationEventState.Implemented)] // skip more
    [InlineData(CompensationEventState.Quoted,      CompensationEventState.Implemented)] // skip Accepted
    [InlineData(CompensationEventState.Accepted,    CompensationEventState.Rejected)]    // no reject post-accept
    [InlineData(CompensationEventState.Implemented, CompensationEventState.Rejected)]    // ditto
    [InlineData(CompensationEventState.Accepted,    CompensationEventState.Quoted)]      // backwards
    [InlineData(CompensationEventState.Rejected,    CompensationEventState.Accepted)]    // from terminal
    [InlineData(CompensationEventState.Implemented, CompensationEventState.Accepted)]    // from terminal
    public void IsValidTransition_rejects_invalid_edges(
        CompensationEventState from, CompensationEventState to)
    {
        Assert.False(CompensationEventWorkflow.IsValidTransition(from, to));
    }

    [Fact]
    public void Rejected_is_terminal()
    {
        Assert.Empty(CompensationEventWorkflow.GetValidTransitions(CompensationEventState.Rejected));
        Assert.True(CompensationEventWorkflow.IsTerminal(CompensationEventState.Rejected));
    }

    [Fact]
    public void Implemented_is_terminal()
    {
        Assert.Empty(CompensationEventWorkflow.GetValidTransitions(CompensationEventState.Implemented));
        Assert.True(CompensationEventWorkflow.IsTerminal(CompensationEventState.Implemented));
    }

    [Theory]
    [InlineData(CompensationEventState.Notified)]
    [InlineData(CompensationEventState.Quoted)]
    [InlineData(CompensationEventState.Accepted)]
    public void Non_terminal_states_have_outgoing_transitions(CompensationEventState s)
    {
        Assert.NotEmpty(CompensationEventWorkflow.GetValidTransitions(s));
        Assert.False(CompensationEventWorkflow.IsTerminal(s));
    }

    // ── Role-gate boundaries ────────────────────────────────────────

    [Fact]
    public void Quote_requires_TaskTeamMember_or_above()
    {
        Assert.False(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Quoted, UserRole.Viewer));
        Assert.False(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Quoted, UserRole.ClientRep));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Quoted, UserRole.TaskTeamMember));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Quoted, UserRole.ProjectManager));
    }

    [Fact]
    public void Reject_requires_ProjectManager_or_above_from_Notified_and_Quoted()
    {
        Assert.False(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Rejected, UserRole.InformationManager));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Notified, CompensationEventState.Rejected, UserRole.ProjectManager));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Quoted,   CompensationEventState.Rejected, UserRole.ProjectManager));
    }

    [Fact]
    public void Accept_and_Implement_require_ProjectManager_or_above()
    {
        Assert.False(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Quoted,   CompensationEventState.Accepted, UserRole.InformationManager));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Quoted,   CompensationEventState.Accepted, UserRole.ProjectManager));
        Assert.False(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Accepted, CompensationEventState.Implemented, UserRole.InformationManager));
        Assert.True(CompensationEventWorkflow.CanTransition(
            CompensationEventState.Accepted, CompensationEventState.Implemented, UserRole.ProjectManager));
    }

    [Fact]
    public void GetAvailableTransitions_for_TaskTeamMember_at_Notified_is_only_Quoted()
    {
        var avail = CompensationEventWorkflow.GetAvailableTransitions(
            CompensationEventState.Notified, UserRole.TaskTeamMember);
        Assert.Single(avail);
        Assert.Contains(CompensationEventState.Quoted, avail);
    }

    [Fact]
    public void GetAvailableTransitions_for_PM_at_Notified_is_both_Quoted_and_Rejected()
    {
        var avail = CompensationEventWorkflow.GetAvailableTransitions(
            CompensationEventState.Notified, UserRole.ProjectManager);
        Assert.Equal(2, avail.Length);
        Assert.Contains(CompensationEventState.Quoted,   avail);
        Assert.Contains(CompensationEventState.Rejected, avail);
    }

    [Fact]
    public void GetAvailableTransitions_at_terminal_state_is_empty_for_every_role()
    {
        foreach (var role in (UserRole[])Enum.GetValues(typeof(UserRole)))
        {
            Assert.Empty(CompensationEventWorkflow.GetAvailableTransitions(
                CompensationEventState.Rejected, role));
            Assert.Empty(CompensationEventWorkflow.GetAvailableTransitions(
                CompensationEventState.Implemented, role));
        }
    }
}

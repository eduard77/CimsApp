using CimsApp.Core;
using CimsApp.Models;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="TenderPackageWorkflow"/>
/// (T-S6-03). Same shape as <see cref="ChangeWorkflowTests"/>;
/// 3-state machine is simpler so the matrix is smaller.
/// </summary>
public class TenderPackageWorkflowTests
{
    [Theory]
    [InlineData(TenderPackageState.Draft,  TenderPackageState.Issued)]
    [InlineData(TenderPackageState.Issued, TenderPackageState.Closed)]
    public void IsValidTransition_accepts_workflow_edges(
        TenderPackageState from, TenderPackageState to)
    {
        Assert.True(TenderPackageWorkflow.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(TenderPackageState.Draft,  TenderPackageState.Closed)]   // skip Issued
    [InlineData(TenderPackageState.Issued, TenderPackageState.Draft)]    // backwards
    [InlineData(TenderPackageState.Closed, TenderPackageState.Issued)]   // from terminal
    [InlineData(TenderPackageState.Closed, TenderPackageState.Draft)]
    public void IsValidTransition_rejects_invalid_edges(
        TenderPackageState from, TenderPackageState to)
    {
        Assert.False(TenderPackageWorkflow.IsValidTransition(from, to));
    }

    [Fact]
    public void Closed_is_terminal()
    {
        Assert.Empty(TenderPackageWorkflow.GetValidTransitions(TenderPackageState.Closed));
        Assert.True(TenderPackageWorkflow.IsTerminal(TenderPackageState.Closed));
    }

    [Theory]
    [InlineData(TenderPackageState.Draft)]
    [InlineData(TenderPackageState.Issued)]
    public void Non_terminal_states_have_outgoing_transitions(TenderPackageState s)
    {
        Assert.NotEmpty(TenderPackageWorkflow.GetValidTransitions(s));
        Assert.False(TenderPackageWorkflow.IsTerminal(s));
    }

    [Fact]
    public void Issue_requires_ProjectManager_or_above()
    {
        Assert.False(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Draft, TenderPackageState.Issued, UserRole.TaskTeamMember));
        Assert.False(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Draft, TenderPackageState.Issued, UserRole.InformationManager));
        Assert.True(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Draft, TenderPackageState.Issued, UserRole.ProjectManager));
        Assert.True(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Draft, TenderPackageState.Issued, UserRole.OrgAdmin));
    }

    [Fact]
    public void Close_requires_ProjectManager_or_above()
    {
        Assert.False(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Issued, TenderPackageState.Closed, UserRole.InformationManager));
        Assert.True(TenderPackageWorkflow.CanTransition(
            TenderPackageState.Issued, TenderPackageState.Closed, UserRole.ProjectManager));
    }

    [Fact]
    public void GetAvailableTransitions_for_PM_at_Draft_is_only_Issued()
    {
        var avail = TenderPackageWorkflow.GetAvailableTransitions(
            TenderPackageState.Draft, UserRole.ProjectManager);
        Assert.Single(avail);
        Assert.Contains(TenderPackageState.Issued, avail);
    }

    [Fact]
    public void GetAvailableTransitions_for_TaskTeamMember_at_Draft_is_empty()
    {
        Assert.Empty(TenderPackageWorkflow.GetAvailableTransitions(
            TenderPackageState.Draft, UserRole.TaskTeamMember));
    }

    [Fact]
    public void GetAvailableTransitions_at_terminal_state_is_empty_for_every_role()
    {
        foreach (var role in (UserRole[])Enum.GetValues(typeof(UserRole)))
        {
            Assert.Empty(TenderPackageWorkflow.GetAvailableTransitions(TenderPackageState.Closed, role));
        }
    }
}

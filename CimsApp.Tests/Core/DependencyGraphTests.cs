using CimsApp.Core;
using Xunit;

namespace CimsApp.Tests.Core;

/// <summary>
/// Pure-function tests for <see cref="DependencyGraph"/> (T-S4-03).
/// No DB, no DI — just graph fixtures and the cycle-detect /
/// topological-sort answers we expect.
/// </summary>
public class DependencyGraphTests
{
    private static Guid G() => Guid.NewGuid();

    [Fact]
    public void DetectCycle_returns_false_for_empty_graph()
    {
        var result = DependencyGraph.DetectCycle([], []);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_returns_false_for_isolated_nodes()
    {
        var a = G(); var b = G(); var c = G();
        var result = DependencyGraph.DetectCycle([a, b, c], []);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_returns_false_for_simple_chain()
    {
        // A → B → C
        var a = G(); var b = G(); var c = G();
        var result = DependencyGraph.DetectCycle([a, b, c], [(a, b), (b, c)]);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_returns_false_for_diamond_DAG()
    {
        //    A
        //   / \
        //  B   C
        //   \ /
        //    D
        var a = G(); var b = G(); var c = G(); var d = G();
        var result = DependencyGraph.DetectCycle(
            [a, b, c, d],
            [(a, b), (a, c), (b, d), (c, d)]);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_returns_true_for_self_loop()
    {
        var a = G();
        var result = DependencyGraph.DetectCycle([a], [(a, a)]);
        Assert.True(result.HasCycle);
        Assert.Contains(a, result.CycleNodes);
    }

    [Fact]
    public void DetectCycle_returns_true_for_two_node_cycle()
    {
        // A → B → A
        var a = G(); var b = G();
        var result = DependencyGraph.DetectCycle([a, b], [(a, b), (b, a)]);
        Assert.True(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_returns_true_for_three_node_cycle_and_names_participants()
    {
        // A → B → C → A
        var a = G(); var b = G(); var c = G();
        var result = DependencyGraph.DetectCycle(
            [a, b, c],
            [(a, b), (b, c), (c, a)]);
        Assert.True(result.HasCycle);
        Assert.Contains(a, result.CycleNodes);
        Assert.Contains(b, result.CycleNodes);
        Assert.Contains(c, result.CycleNodes);
    }

    [Fact]
    public void DetectCycle_finds_back_edge_in_complex_DAG_with_local_cycle()
    {
        //      A → B → C
        //       ↘  ↑
        //         D    (edge C → D forms cycle B → C → D → B)
        var a = G(); var b = G(); var c = G(); var d = G();
        var result = DependencyGraph.DetectCycle(
            [a, b, c, d],
            [(a, b), (a, d), (b, c), (c, d), (d, b)]);
        Assert.True(result.HasCycle);
    }

    [Fact]
    public void DetectCycle_skips_edges_pointing_outside_the_node_set()
    {
        // External `outside` node referenced as a successor; service
        // layer enforces that endpoints belong to the project, but the
        // pure function should not throw — just ignore.
        var a = G(); var b = G(); var outside = G();
        var result = DependencyGraph.DetectCycle([a, b], [(a, b), (b, outside)]);
        Assert.False(result.HasCycle);
    }

    [Fact]
    public void TopologicalSort_orders_simple_chain()
    {
        var a = G(); var b = G(); var c = G();
        var order = DependencyGraph.TopologicalSort([a, b, c], [(a, b), (b, c)]);
        Assert.Equal(3, order.Count);
        Assert.Equal(a, order[0]);
        Assert.Equal(b, order[1]);
        Assert.Equal(c, order[2]);
    }

    [Fact]
    public void TopologicalSort_orders_diamond_with_predecessors_before_successors()
    {
        //    A
        //   / \
        //  B   C
        //   \ /
        //    D
        var a = G(); var b = G(); var c = G(); var d = G();
        var order = DependencyGraph.TopologicalSort(
            [a, b, c, d],
            [(a, b), (a, c), (b, d), (c, d)]);
        Assert.Equal(a, order[0]);                 // root first
        Assert.Equal(d, order[^1]);                // sink last
        Assert.True(order.IndexOf(b) < order.IndexOf(d));
        Assert.True(order.IndexOf(c) < order.IndexOf(d));
    }

    [Fact]
    public void TopologicalSort_throws_on_cycle()
    {
        var a = G(); var b = G();
        Assert.Throws<InvalidOperationException>(() =>
            DependencyGraph.TopologicalSort([a, b], [(a, b), (b, a)]));
    }

    [Fact]
    public void TopologicalSort_is_stable_by_insertion_order_for_independent_nodes()
    {
        // Three disconnected nodes — they all have indegree 0 from
        // step 0, so the order should match the activityIds order.
        var a = G(); var b = G(); var c = G();
        var order = DependencyGraph.TopologicalSort([a, b, c], []);
        Assert.Equal(a, order[0]);
        Assert.Equal(b, order[1]);
        Assert.Equal(c, order[2]);
    }
}

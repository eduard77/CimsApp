namespace CimsApp.Core;

/// <summary>
/// Directed-acyclic-graph primitives for the schedule module
/// (T-S4-03, PAFM-SD F.5 first bullet — "tasks, dependencies,
/// constraints"). Pure functions, no IO, no DB, no DI; inputs are
/// activity IDs and predecessor/successor edges, outputs are
/// cycle-detection / topological-sort answers.
///
/// Reused by the CPM solver (T-S4-04) for the forward-pass topological
/// order, by the dependency CRUD service (T-S4-03) to reject cyclic
/// inputs before they hit the DB, and by the MS Project XML import
/// (T-S4-09) to validate the imported schedule before commit.
/// </summary>
public static class DependencyGraph
{
    /// <summary>
    /// Result of a cycle-detection run. <see cref="HasCycle"/> is the
    /// caller's primary signal; <see cref="CycleNodes"/> carries the
    /// IDs that participate in the back-edge for diagnostic messages
    /// (e.g. "A → B → C → A").
    /// </summary>
    public readonly record struct CycleResult(bool HasCycle, IReadOnlyList<Guid> CycleNodes);

    /// <summary>
    /// DFS three-colour cycle detection. WHITE = unvisited, GRAY =
    /// on the current DFS path, BLACK = fully explored. A GRAY-to-GRAY
    /// edge during traversal is a back-edge → cycle. Linear-time in
    /// nodes + edges. Edges in <paramref name="dependencies"/> are
    /// directed (Predecessor → Successor); duplicates and self-loops
    /// are accepted as input but a self-loop reports as a cycle.
    /// </summary>
    public static CycleResult DetectCycle(
        IReadOnlyCollection<Guid> activityIds,
        IReadOnlyCollection<(Guid Predecessor, Guid Successor)> dependencies)
    {
        var adj = BuildAdjacency(activityIds, dependencies);
        var color = new Dictionary<Guid, byte>(activityIds.Count);
        foreach (var id in activityIds) color[id] = 0;  // 0=white, 1=gray, 2=black

        var stack = new Stack<(Guid Node, IEnumerator<Guid> Iter)>();
        var path  = new List<Guid>();

        foreach (var start in activityIds)
        {
            if (color[start] != 0) continue;
            color[start] = 1;
            path.Add(start);
            stack.Push((start, adj[start].GetEnumerator()));

            while (stack.Count > 0)
            {
                var (node, iter) = stack.Peek();
                if (iter.MoveNext())
                {
                    var next = iter.Current;
                    var c = color.TryGetValue(next, out var v) ? v : (byte)0;
                    if (c == 1)
                    {
                        // Back-edge: cycle from `next` round to `node`.
                        var idx = path.IndexOf(next);
                        var cycle = path.GetRange(idx, path.Count - idx);
                        cycle.Add(next);
                        return new CycleResult(true, cycle);
                    }
                    if (c == 0 && color.ContainsKey(next))
                    {
                        color[next] = 1;
                        path.Add(next);
                        stack.Push((next, adj[next].GetEnumerator()));
                    }
                    // c == 2 → already explored, no work; missing key
                    // (edge points outside activityIds) → skip silently;
                    // service layer enforces edge endpoints belong to
                    // the activity set.
                }
                else
                {
                    color[node] = 2;
                    path.RemoveAt(path.Count - 1);
                    stack.Pop();
                }
            }
        }
        return new CycleResult(false, []);
    }

    /// <summary>
    /// Topological order via Kahn's algorithm. Stable by insertion
    /// order: when multiple nodes have indegree zero at the same step,
    /// they emit in the order they appear in <paramref name="activityIds"/>.
    /// Throws <see cref="InvalidOperationException"/> on cycles —
    /// callers should call <see cref="DetectCycle"/> first if cycles
    /// are possible. The CPM solver (T-S4-04) relies on this order
    /// for the forward pass.
    /// </summary>
    public static List<Guid> TopologicalSort(
        IReadOnlyCollection<Guid> activityIds,
        IReadOnlyCollection<(Guid Predecessor, Guid Successor)> dependencies)
    {
        var adj = BuildAdjacency(activityIds, dependencies);
        var indegree = activityIds.ToDictionary(id => id, _ => 0);
        foreach (var (_, succ) in dependencies)
        {
            if (indegree.ContainsKey(succ)) indegree[succ]++;
        }

        var ready = new Queue<Guid>(activityIds.Where(id => indegree[id] == 0));
        var result = new List<Guid>(activityIds.Count);

        while (ready.Count > 0)
        {
            var n = ready.Dequeue();
            result.Add(n);
            foreach (var s in adj[n])
            {
                if (!indegree.ContainsKey(s)) continue;
                if (--indegree[s] == 0) ready.Enqueue(s);
            }
        }

        if (result.Count != activityIds.Count)
            throw new InvalidOperationException("Graph contains a cycle; topological sort undefined");
        return result;
    }

    private static Dictionary<Guid, List<Guid>> BuildAdjacency(
        IReadOnlyCollection<Guid> activityIds,
        IReadOnlyCollection<(Guid Predecessor, Guid Successor)> dependencies)
    {
        var adj = activityIds.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var (p, s) in dependencies)
        {
            if (adj.TryGetValue(p, out var list)) list.Add(s);
        }
        return adj;
    }
}

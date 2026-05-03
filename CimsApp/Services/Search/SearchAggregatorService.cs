namespace CimsApp.Services.Search;

/// <summary>
/// Cross-entity search aggregator. T-S15-02. Fans out the query
/// to every registered <see cref="ISearchProvider"/> in parallel,
/// applies an optional <c>types</c> allow-list, and merges into a
/// single ranked hit list. Per-provider take is bounded so a
/// large hit set in one entity type doesn't starve the others;
/// the overall <c>take</c> is applied after the merge.
///
/// Performance ceiling on <c>EF.Functions.Like</c> + project-scope
/// is documented in the kickoff doc; FTS upgrade → v1.1 / B-095.
/// </summary>
public sealed class SearchAggregatorService(IEnumerable<ISearchProvider> providers)
{
    public const int DefaultPerProviderTake = 10;
    public const int DefaultOverallTake = 50;
    public const int MinQueryLength = 2;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query,
        IReadOnlySet<string>? typesFilter = null,
        int? overallTake = null,
        CancellationToken ct = default)
    {
        if (query is null || query.Trim().Length < MinQueryLength)
            return Array.Empty<SearchHit>();
        var trimmed = query.Trim();

        var selected = providers
            .Where(p => typesFilter is null || typesFilter.Contains(p.EntityType))
            .ToList();
        if (selected.Count == 0) return Array.Empty<SearchHit>();

        // Sequential await per provider — every provider shares the
        // same scoped CimsDbContext and EF DbContext is NOT
        // thread-safe; Task.WhenAll would throw "second operation
        // started before previous completed" on the first concurrent
        // dispatch. The seven indexed LIKE queries are fast enough
        // sequentially for v1.0 pilot scale; FTS upgrade (B-095)
        // moves performance off this hot path entirely.
        var merged = new List<SearchHit>(selected.Count * DefaultPerProviderTake);
        foreach (var p in selected)
        {
            var hits = await p.SearchAsync(projectId, trimmed, DefaultPerProviderTake, ct);
            merged.AddRange(hits);
        }

        return merged
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(overallTake ?? DefaultOverallTake)
            .ToList();
    }
}

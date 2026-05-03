namespace CimsApp.Services.Search;

/// <summary>
/// One implementation per searchable entity type. T-S15-02.
/// Each provider runs a project-scoped LIKE query within the
/// existing tenant query filter and returns the top-N hits as
/// uniform <see cref="SearchHit"/> rows. The aggregator merges
/// across providers and ranks by score.
/// </summary>
public interface ISearchProvider
{
    /// <summary>Stable, lowercase identifier used by the
    /// optional <c>types[]</c> query-string filter (e.g.
    /// <c>"document"</c>, <c>"rfi"</c>, <c>"action"</c>).</summary>
    string EntityType { get; }

    Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default);
}

/// <summary>
/// Uniform hit shape across every entity type. <see cref="Score"/>
/// is the per-provider relevance signal (title hit = 3, number/code
/// hit = 2, body hit = 1; provider may sum if multiple match). The
/// aggregator orders by Score desc then Title asc for stable output.
/// </summary>
public sealed record SearchHit(
    string EntityType,
    Guid Id,
    string Title,
    string? Snippet,
    int Score);

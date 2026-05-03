using CimsApp.Data;
using Microsoft.EntityFrameworkCore;

namespace CimsApp.Services.Search;

/// <summary>
/// Searchable entity providers. T-S15-02. Each provider runs the
/// LIKE query through <see cref="CimsDbContext"/>'s ordinary
/// <c>DbSet</c> so the existing project-scoped tenant query filter
/// (Project.AppointingPartyId == _tenant.OrganisationId) is
/// honoured automatically — no <c>IgnoreQueryFilters</c> here.
/// Score: title hit = 3, number/code hit = 2, body hit = 1; sum
/// when multiple match.
///
/// Snippet: first 160 chars of Description / Body, trimmed —
/// good enough for v1.0 result rendering. Full highlighting →
/// v1.1 / B-095 (FTS upgrade).
/// </summary>
internal static class SearchSnippet
{
    public const int Length = 160;
    public static string? From(string? s) =>
        string.IsNullOrEmpty(s) ? null
            : s.Length <= Length ? s
            : s[..Length].TrimEnd() + "…";
}

public sealed class DocumentSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "document";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.Documents
            .Where(d => d.ProjectId == projectId
                && (EF.Functions.Like(d.Title,          pattern, SearchQueryEscape.EscapeCharacter)
                 || EF.Functions.Like(d.DocumentNumber, pattern, SearchQueryEscape.EscapeCharacter)
                 || (d.Description != null
                  && EF.Functions.Like(d.Description,   pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(d => new
            {
                d.Id, d.Title, d.DocumentNumber, d.Description,
            })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var score = 0;
            if (Like(r.Title, query))          score += 3;
            if (Like(r.DocumentNumber, query)) score += 2;
            if (Like(r.Description, query))    score += 1;
            return new SearchHit(EntityType, r.Id,
                $"{r.DocumentNumber} — {r.Title}",
                SearchSnippet.From(r.Description), score);
        }).ToList();
    }

    internal static bool Like(string? haystack, string needle)
        => haystack != null
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

public sealed class RfiSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "rfi";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.Rfis
            .Where(r => r.ProjectId == projectId
                && (EF.Functions.Like(r.Subject,     pattern, SearchQueryEscape.EscapeCharacter)
                 || EF.Functions.Like(r.RfiNumber,   pattern, SearchQueryEscape.EscapeCharacter)
                 || EF.Functions.Like(r.Description, pattern, SearchQueryEscape.EscapeCharacter)))
            .Take(take)
            .Select(r => new { r.Id, r.RfiNumber, r.Subject, r.Description })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(r.Subject, query))   score += 3;
            if (DocumentSearchProvider.Like(r.RfiNumber, query)) score += 2;
            if (DocumentSearchProvider.Like(r.Description, query)) score += 1;
            return new SearchHit(EntityType, r.Id,
                $"{r.RfiNumber} — {r.Subject}",
                SearchSnippet.From(r.Description), score);
        }).ToList();
    }
}

public sealed class ActionSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "action";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.ActionItems
            .Where(a => a.ProjectId == projectId
                && (EF.Functions.Like(a.Title, pattern, SearchQueryEscape.EscapeCharacter)
                 || (a.Description != null
                  && EF.Functions.Like(a.Description, pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(a => new { a.Id, a.Title, a.Description })
            .ToListAsync(ct);
        return rows.Select(a =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(a.Title, query))       score += 3;
            if (DocumentSearchProvider.Like(a.Description, query)) score += 1;
            return new SearchHit(EntityType, a.Id, a.Title,
                SearchSnippet.From(a.Description), score);
        }).ToList();
    }
}

public sealed class RiskSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "risk";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.Risks
            .Where(r => r.ProjectId == projectId
                && (EF.Functions.Like(r.Title, pattern, SearchQueryEscape.EscapeCharacter)
                 || (r.Description != null
                  && EF.Functions.Like(r.Description, pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(r => new { r.Id, r.Title, r.Description })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(r.Title, query))       score += 3;
            if (DocumentSearchProvider.Like(r.Description, query)) score += 1;
            return new SearchHit(EntityType, r.Id, r.Title,
                SearchSnippet.From(r.Description), score);
        }).ToList();
    }
}

public sealed class ChangeRequestSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "change-request";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.ChangeRequests
            .Where(c => c.ProjectId == projectId
                && (EF.Functions.Like(c.Title,  pattern, SearchQueryEscape.EscapeCharacter)
                 || EF.Functions.Like(c.Number, pattern, SearchQueryEscape.EscapeCharacter)
                 || (c.Description != null
                  && EF.Functions.Like(c.Description, pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(c => new { c.Id, c.Number, c.Title, c.Description })
            .ToListAsync(ct);
        return rows.Select(c =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(c.Title, query))       score += 3;
            if (DocumentSearchProvider.Like(c.Number, query))      score += 2;
            if (DocumentSearchProvider.Like(c.Description, query)) score += 1;
            return new SearchHit(EntityType, c.Id,
                $"{c.Number} — {c.Title}",
                SearchSnippet.From(c.Description), score);
        }).ToList();
    }
}

public sealed class EarlyWarningSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "early-warning";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.EarlyWarnings
            .Where(w => w.ProjectId == projectId
                && (EF.Functions.Like(w.Title, pattern, SearchQueryEscape.EscapeCharacter)
                 || (w.Description != null
                  && EF.Functions.Like(w.Description, pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(w => new { w.Id, w.Title, w.Description })
            .ToListAsync(ct);
        return rows.Select(w =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(w.Title, query))       score += 3;
            if (DocumentSearchProvider.Like(w.Description, query)) score += 1;
            return new SearchHit(EntityType, w.Id, w.Title,
                SearchSnippet.From(w.Description), score);
        }).ToList();
    }
}

public sealed class CompensationEventSearchProvider(CimsDbContext db) : ISearchProvider
{
    public string EntityType => "compensation-event";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        Guid projectId, string query, int take, CancellationToken ct = default)
    {
        var pattern = SearchQueryEscape.ContainsPattern(query);
        var rows = await db.CompensationEvents
            .Where(c => c.ProjectId == projectId
                && (EF.Functions.Like(c.Title,  pattern, SearchQueryEscape.EscapeCharacter)
                 || EF.Functions.Like(c.Number, pattern, SearchQueryEscape.EscapeCharacter)
                 || (c.Description != null
                  && EF.Functions.Like(c.Description, pattern, SearchQueryEscape.EscapeCharacter))))
            .Take(take)
            .Select(c => new { c.Id, c.Number, c.Title, c.Description })
            .ToListAsync(ct);
        return rows.Select(c =>
        {
            var score = 0;
            if (DocumentSearchProvider.Like(c.Title, query))       score += 3;
            if (DocumentSearchProvider.Like(c.Number, query))      score += 2;
            if (DocumentSearchProvider.Like(c.Description, query)) score += 1;
            return new SearchHit(EntityType, c.Id,
                $"{c.Number} — {c.Title}",
                SearchSnippet.From(c.Description), score);
        }).ToList();
    }
}

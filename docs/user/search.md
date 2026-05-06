# Search

S15 module — cross-entity project search aggregator. Single
endpoint returns hits across the high-traffic project-scoped
entity types in one ranked list.

## Surface

`GET /api/v1/projects/{projectId}/search?q={query}&types={types}`

The seven searched entity types in v1.0:

- Documents (DocumentNumber, Title, Description)
- RFIs (RfiNumber, Subject, Description)
- Actions (Title, Description)
- Risks (Title, Description)
- ChangeRequests (Number, Title, Description)
- EarlyWarnings (Number, Title, Description)
- CompensationEvents (Number, Title, Description)

`types=` query param narrows the search (comma-separated list).
Per-provider take = 10; overall take = 50; minimum query length
= 2 characters.

## Ranking

Score per hit: title match = 3, number/code match = 2, body
match = 1. Aggregator merges, ranks, and returns a unified
`(EntityType, Id, Title, Snippet, Score)` discriminator list.

## Implementation pattern

Per-entity providers (`ISearchProvider`) run `EF.Functions.Like`
against the relevant table within the project's tenant filter.
Wildcard escape via `SearchQueryEscape.EscapeLike` handles SQL
Server LIKE specials (`%`, `_`, `[`, `\`).

## Common gotchas

- **Saved searches / search history** is v1.1 / B-096 — v1.0
  is stateless.
- **Additional entity types** (stakeholders, MIDP, TIDP,
  lessons-learned, opportunities, inspections, alert rules) are
  v1.1 / B-097.
- **SQL Server full-text upgrade** to FTS catalogues is v1.1 /
  B-095 once pilot scale exposes LIKE-against-indexed-columns
  latency (>500 ms on > 1000 rows in any one searchable table).
- **Search analytics** (top queries, zero-hit, per-user usage)
  is v1.1 / B-098.

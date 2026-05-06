# Reporting & Dashboards

S7 module — pre-built dashboards + a custom report builder over
the per-project entity surface.

## Pre-built dashboards

`/dashboard` aggregates the project's headline metrics:
- Cost utilisation (committed + actuals / budget)
- Schedule variance (against current baseline)
- Open Risk count + heatmap
- Open RFI / Action / NCR counts
- Recent audit activity

The dashboard is project-scoped — change project to refresh.

## Custom report builder

`CustomReportDefinition` rows let users save parameterised queries
over the v1.0 entity allow-list:

- Risk
- ActionItem
- Rfi
- Variation
- ChangeRequest

Each saved query carries: target entity type, a filter expression
(JSON tree of comparisons), and a column selection. Run produces
a paged table; export to CSV is available on the API.

`Core/CustomReportRunner.cs` translates the saved-query JSON to
EF Core LINQ at run time. Cross-entity joins (e.g. Actions joined
to RFIs) are v1.1 / B-056.

## Common gotchas

- **Custom reports are tenant-scoped** — definitions saved in
  Org A are not visible in Org B.
- **Field allow-list** is intentionally narrow — adding a new
  column to a saved query requires editing
  `CustomReportRunner` + a new EntityType in the
  `CustomReportEntityType` enum.

# ADR-0003 — Row-level multi-tenancy with global query filter

**Status:** Accepted (2026-04-24, Sprint S0, Session 3)
**Supersedes:** —

## Context

PAFM Appendix F.1 requires tenant isolation across all tenant-scoped
entities in CimsApp. The Sprint 0 DoD explicitly names "`OrganisationId`
global query filter" as the mechanism. Three options were considered:

1. **Row-level with global query filter** — every tenant's data shares
   the same tables, filtered on read by `OrganisationId`. Schema-light,
   backup-light, one DB to operate.
2. **Database-per-tenant** — one SQL Server database per organisation,
   strongest isolation, highest operational cost, incompatible with the
   single-DbContext design.
3. **Schema-per-tenant** — one SQL schema per organisation within the
   same DB, middle ground, incompatible with EF Core migrations as
   currently configured (one `DbSet<T>` per entity, one schema).

## Decision

Option 1: row-level multi-tenancy with EF Core global query filters.

- `ITenantContext` service (request-scoped) exposes `OrganisationId`
  and `UserId`, resolved from JWT claims `ClaimTypes.NameIdentifier`
  and the custom `cims:org` claim emitted by `AuthService`.
- `CimsDbContext` takes `ITenantContext?` in its constructor and falls
  back to a `NullTenantContext` for EF design-time (migrations,
  scaffolding).
- `HasQueryFilter` is registered on every tenant-scoped entity in
  `OnModelCreating`:
  - Direct: `User`, `Project`.
  - Indirect via `Project.AppointingPartyId`: `ProjectMember`,
    `ProjectAppointment`, `CdeContainer`, `Document`,
    `DocumentRevision`, `Rfi`, `ActionItem`, `ProjectTemplate`.
- Pre-auth paths in `AuthService` (`LoginAsync`, `RefreshAsync`,
  `RegisterAsync` email-uniqueness check) use `IgnoreQueryFilters()`
  because the caller's tenant is not yet known.
- `Organisation`, `RefreshToken`, `RfiDocument`, `AuditLog`,
  `Notification` are intentionally unfiltered in this ADR; they each
  need a bespoke rule and will be addressed in follow-up commits or
  ADRs (T-S0-06 audit, T-S0-08 role audit).

## Alternatives considered

**Database-per-tenant** — rejected. Operational overhead is
inappropriate for a pre-v1.0 solo-dev project, and EF migrations would
need to run across N databases. Revisit post-v1.0 if regulatory or
enterprise isolation demands it.

**Schema-per-tenant** — rejected. EF Core migrations model one default
schema per context; per-tenant schemas would require dynamic
`ToTable(..., schema: …)` plumbing and invalidate our existing
migrations. Cost outweighs the marginal isolation win.

## Consequences

**Positive:**
- One database, one migration path, one backup.
- Filter is centrally defined in `CimsDbContext`; services and
  controllers inherit isolation for free.
- SuperAdmin cross-tenant visibility is modelled later as
  `IgnoreQueryFilters()` at the query site with audit (T-S0-07).

**Negative / risks:**
- A single bug in the filter (or a forgotten entity) leaks across
  tenants. Mitigation: code review on `OnModelCreating` changes;
  integration tests (T-S0-04) must exercise multi-tenant scenarios.
- Navigation-based filters (e.g. `d => d.Project.AppointingPartyId ==
  …`) add joins at query time. Acceptable for v1.0 scale; revisit if
  profiling shows hot paths.
- Any code forgetting to use `IgnoreQueryFilters()` pre-auth will see
  empty result sets. Today only `AuthService` paths qualify; future
  pre-auth code must remember this.

## Related

- PAFM Appendix F.1 (Sprint 0 module-level DoD).
- `docs/sprint-log/s0.md` T-S0-02, T-S0-03, T-S0-04.
- PAFM-SD Ch 29.7 early-ADR batch (this is the third of that batch).

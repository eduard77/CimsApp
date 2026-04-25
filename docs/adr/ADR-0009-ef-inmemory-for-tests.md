# ADR-0009 — Use EF Core InMemory provider for tenant-isolation integration tests

**Status:** Proposed (2026-04-24, Sprint S0, Session 3; 24-hour wait per PAFM-SD Ch 24.6 expires 2026-04-25)
**Supersedes:** —

## Context

T-S0-04 requires a behavioural integration test proving that two
tenants running against the same `CimsDbContext` see only their own
`Project` and `Document` rows. A model-inspection unit test already
asserts each tenant-scoped entity has a query filter registered
(`CimsApp.Tests/Data/CimsDbContextTenantFilterTests.cs`), but that
doesn't prove runtime behaviour.

Testing runtime behaviour needs a database. Three options:

1. **`Microsoft.EntityFrameworkCore.InMemory`** — first-party Microsoft
   package, no external dependencies, fast, supports query filters
   since 2.1. Doesn't enforce referential integrity or transactions
   the way SQL Server does, which matters for some tests but not for
   this one — the filter is an SQL `WHERE` clause, not a constraint.
2. **`Microsoft.EntityFrameworkCore.Sqlite`** — first-party Microsoft
   package, closer to real RDBMS semantics (FK constraints, real SQL),
   slightly more setup, in-memory mode available via
   `:memory:` connection string.
3. **SQL Server LocalDB** — most realistic, Windows-only, requires a
   running service, slow per-test.

## Decision

Use **EF Core InMemory** for the tenant-isolation behavioural test
and any future test where the goal is to verify query-level behaviour
rather than database-specific SQL. For tests that need real SQL
semantics (migrations, FK enforcement, complex transactions), future
ADRs will choose between Sqlite and LocalDB on the same per-test-need
basis.

Per PAFM-SD Ch 24.6, a 24-hour wait applies before this package is
added. Clock starts at this ADR's date (2026-04-24). The package
addition and T-S0-04 behavioural test may land from 2026-04-25
onward.

## Package details

- **Name:** `Microsoft.EntityFrameworkCore.InMemory`
- **Version:** `8.0.2` (match existing EF Core packages)
- **Licence:** MIT (Microsoft)
- **Last release:** actively maintained by the EF Core team.
- **Security history:** no CVEs as of 2026-04-24.
- **Maintenance burden:** tracks EF Core versions; upgrade alongside
  the main EF package.
- **Scope:** referenced only from `CimsApp.Tests`, never from
  `CimsApp`.

## Alternatives considered

**Option 2 (Sqlite):** rejected for this ADR because referential
integrity isn't needed to verify a `WHERE`-clause query filter. If a
future test needs it, a separate ADR adds Sqlite.

**Option 3 (LocalDB):** rejected. Windows-only, slow, service-dependent.

**No behavioural test at all:** rejected. Feature-level DoD (Ch 10.3)
requires integration tests on critical paths; tenant isolation is
critical.

## Consequences

**Positive:**
- Fast feedback loop on tenant isolation regressions.
- One-line provider swap in test fixture (`UseInMemoryDatabase`).

**Negative:**
- InMemory is an approximation, not a real DB. Must not be used to
  test behaviours that depend on SQL Server semantics (e.g., string
  collation, transactions, FK cascading).
- One more package in the test project's dependency graph.

## Related

- PAFM-SD Ch 10.3 (Feature-level DoD — integration tests on critical
  paths), Ch 24.6 (new-library rule).
- `docs/sprint-log/s0.md` T-S0-04.
- ADR-0003 (row-level multi-tenancy) is the feature under test.

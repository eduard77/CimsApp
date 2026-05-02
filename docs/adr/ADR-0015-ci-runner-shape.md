# ADR-0015 — CI runner shape: Linux + `mssql/server` Docker service container

**Status:** Accepted (2026-05-02, Sprint S8, kickoff)
**Supersedes:** —

## Context

The existing CI workflow at `.github/workflows/ci.yml` runs build
+ test on `ubuntu-latest`. That catches compile errors and unit-
test failures but **misses the FK-permissive class of bugs** that
bit at PR #41 (`AuditLog.UserId` non-nullable + `Guid.Empty`
sentinel for anonymous flows = production FK violation, latent
for ~a week under unit-test-only validation because the EF Core
in-memory provider ignores FK constraints).

B-027 promoted to active scope in S8 closes that gap by adding a
SQL Server-backed smoke job. The remaining decision is the runner
shape for that smoke job. Three plausible options:

1. **Linux runner + `mssql/server` Docker service container.**
   Ubuntu host running the official `mcr.microsoft.com/mssql/server`
   image as a GitHub Actions service container; the workflow steps
   talk to SQL Server over TCP at `localhost:1433`. Standard .NET
   CI shape across the industry.
2. **Windows runner + LocalDB.** Mirrors Eduard's dev environment
   exactly (`appsettings.json` points `DefaultConnection` at
   LocalDB). Connection-string identical between dev and CI.
3. **Linux + Windows matrix.** Run both A and B on every PR.
   Maximum coverage; double the cost.

## Decision

**Option A — Linux runner + `mssql/server` Docker service
container.**

Connection string for CI: `Server=localhost,1433;Database=cims_ci;
User Id=sa;Password=<set-via-Actions-secret>;TrustServerCertificate=
True;` — handled via `appsettings.CI.json` overlay or an
`ASPNETCORE_*` env var set in the workflow step. Dev keeps using
LocalDB locally; the connection-string divergence is the price
paid for Option A and is genuinely small.

## Alternatives considered

**Option B (Windows + LocalDB):** rejected.
- GitHub Actions Windows runners cost 2× Linux on private repos
  (the user is on a private repo). At even modest CI volumes the
  monthly cost difference compounds.
- Windows runner provisioning (~30-90s) is slower than Linux
  (~10-30s); compounds across PR check loops.
- LocalDB itself is a developer-convenience installation, not a
  production-shape RDBMS. A real customer deploy will use SQL
  Server proper (Azure SQL, AWS RDS for SQL Server, or self-
  hosted MSSQL); CI mirroring that shape is a closer pre-pilot
  signal than CI mirroring LocalDB.
- The dev/CI connection-string divergence already exists for
  every other dev/prod pair (dev=LocalDB, prod=SQL Server proper);
  CI sitting on the prod side of that divergence is the right
  place for it.

**Option C (Linux + Windows matrix):** rejected.
- Every benefit of Option A's coverage is already there; the
  delta of Windows-on-top is ~zero (LocalDB-specific bugs almost
  certainly don't exist — the Microsoft `Microsoft.Data.SqlClient`
  driver is the same on both platforms).
- 2× CI cost for no marginal benefit.
- Twice the maintenance burden when the workflow needs touching.

**Status quo (no SQL Server in CI):** rejected. This is exactly
the class of gap that PR #41 surfaced and that motivated the
B-027 backlog entry.

## PAFM-SD Ch 24.6 24-hour wait — does it apply?

Ch 24.6 governs adding new NuGet **packages** to the project (the
ADR-0009 EF InMemory wait was for `Microsoft.EntityFrameworkCore.
InMemory`, a NuGet reference added to `CimsApp.Tests.csproj`).

This ADR introduces a Docker image (`mcr.microsoft.com/mssql/server`)
used only as a GitHub Actions service container. The image is:
- Official Microsoft build at `mcr.microsoft.com`,
- Not added to the application's runtime or test dependency graph,
- Not bundled into any deployable artefact,
- Used solely in CI infrastructure.

The 24.6 spirit (a wait against rushing third-party code into
the app) is honoured de facto: nothing changes in the application
package graph. The ADR explicitly notes this and proceeds without
the 24h wait. If a future ADR adds a NuGet package alongside this
work — e.g. a `Testcontainers.MsSql` reference for a different
testing layer — that ADR will respect Ch 24.6.

## Consequences

**Positive:**
- CI catches the FK-permissive bug class that motivated B-027.
- CI shape mirrors the production deployment target (SQL Server
  proper, not LocalDB).
- Free Linux runner minutes; low monthly cost overhead.
- Standard idiom: `services.<name>.image` + `services.<name>.options`
  with health checks is well-documented in GitHub Actions.

**Negative:**
- Connection-string divergence between dev (LocalDB) and CI
  (`localhost:1433` SQL Server). Mitigated by `appsettings.CI.json`
  overlay or workflow env var; dev keeps `appsettings.json` +
  `appsettings.Development.json`.
- ~30-60s container boot time on every PR push. Mitigated by
  the GitHub Actions `--health-cmd`/`--health-interval` poll loop
  and a 90s timeout cap before failing the step (T-S8-03 Top-3
  risk #1).
- The smoke job is a new failure mode the CI will surface; will
  occasionally fail on `mssql/server` upstream issues unrelated
  to the PR's code. Acceptable as the price of catching the FK
  bug class.

## Related

- B-027 backlog entry (`docs/v1.1-backlog.md`).
- PR #41 / post-S1 hardening session 2026-04-29 (the FK-permissive
  bug that motivated B-027).
- ADR-0009 (EF InMemory provider — covers the unit-test layer; this
  ADR covers the CI integration layer).
- PAFM-SD Ch 17 (ADR shaping), Ch 24.6 (new-library rule —
  considered, found not strictly applicable here).
- `docs/sprint-log/s8.md` T-S8-02 (this ADR's task) and T-S8-03
  (the SQL Server CI job that implements the decision).

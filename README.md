# CIMS

**Construction Information Management System.** ASP.NET Core
8 + Blazor Server. Multi-tenant, ISO 19650 / PMBOK 7 aligned,
NEC4 cumulative payment-certificate semantics by default.

## Status

v1.0 in development, internal pilot stage. Sprint-based
delivery per `docs/sprint-log/`. Sprint S0 (multi-tenant
foundation) and S1 (Cost & Commercial) closed; S2 (Risk
Management) not yet kicked off.

CI runs `dotnet build` + `dotnet test` on every push to
`master` and every PR. Latest run on master is green; the
current test count is recorded at the foot of each sprint
log Day entry.

## Quick start

Prerequisites:

- **.NET SDK 8.0+** (CI pins to `8.0.x`).
- **SQL Server** — LocalDB (`(localdb)\mssqllocaldb`) is fine
  for development; the connection string lives in
  `CimsApp/appsettings.json` under
  `ConnectionStrings:DefaultConnection`.
- **`dotnet-ef` global tool** —
  `dotnet tool install --global dotnet-ef` (currently pinned to
  10.0.6 to match the
  `Microsoft.EntityFrameworkCore.Tools` package version in
  the project).

Build, migrate, run:

```bash
dotnet restore CimsApp.sln
dotnet ef database update --project CimsApp     # applies all migrations
dotnet build CimsApp.sln
cd CimsApp && dotnet run                        # http://localhost:5000
```

Test:

```bash
dotnet test CimsApp.sln
```

To start fresh, register an organisation via
`POST /api/v1/organisations` (anonymous) — the response
includes a 24-hour bootstrap invitation token, which you then
consume via `POST /api/v1/auth/register` to create the first
OrgAdmin user (see ADR-0011).

## Project structure

```
CimsApp/                  Web app (ASP.NET Core + Blazor Server)
  Components/             Razor pages and layouts
  Controllers/            HTTP API controllers
  Core/                   Domain primitives (Evm.cs, exceptions, CDE state machine)
  Data/                   CimsDbContext + tenant query filters
  DTOs/                   Request / response records
  Middleware/             ErrorHandlingMiddleware (AppException -> JSON)
  Migrations/             EF Core migrations
  Models/                 Entities + enums
  Services/               Business logic; one service per aggregate
    Audit/                AuditInterceptor + audit-twin pattern
    Auth/                 TokenRevocation, LoginAttemptTracker
    Iso19650/             Parked per ADR-0008 (Sprint 8)
    Tenancy/              ITenantContext, HttpTenantContext
  UI/                     Blazor-side helpers (BlazorApiClient, UiStateService)

CimsApp.Tests/            Behavioural tests against the in-memory provider
  Core/                   Pure-function tests (Evm, etc.)
  Controllers/            Controller-level scoping tests
  Data/                   Tenant-filter sweep + per-entity isolation
  Services/               Per-service behavioural coverage
  TestDoubles/            StubTenantContext

docs/
  adr/                    Architecture Decision Records (ADR-0001..0014)
  retrospectives/         Per-sprint retrospectives
  security/               Security review docs + role-matrix
  sprint-log/             Per-sprint daily logs and handoff notes
  change-register.md      Chapter 17 change records (CR-001..003)
  current-sprint.md       AI-brief pointer for new sessions
  v1.1-backlog.md         Deferred work (closed entries struck through)
```

## Documentation

Start here:

- **`docs/current-sprint.md`** — current state of the project,
  inherited rules, out-of-scope items.
- **`docs/security/role-matrix.md`** — authoritative
  authorization surface; every endpoint with its global /
  project gates.
- **`docs/v1.1-backlog.md`** — every deferred-or-out-of-scope
  item with disposition. Closed items struck through with the
  closing rationale.
- **`docs/adr/`** — design decisions. ADR-0010 covers the
  two-tier role model; ADR-0011 invitation tokens; ADR-0012
  project tenancy; ADR-0013 NEC4 default; ADR-0014
  access-token residual-authority SLA.

For domain context:

- **`docs/sprint-log/`** — the daily log captures what was
  done, why, and what was decided. Each sprint also has a
  retrospective in `docs/retrospectives/`.
- **`docs/security/s0-review-2026-04-24.md`** and the post-S1
  audit docs — security reviews. Every finding is either
  closed in code or formally tracked in the v1.1 backlog with
  a CLOSED entry.

## Tech stack

- ASP.NET Core 8, Blazor Server, MudBlazor 9
- Entity Framework Core 8 (SQL Server provider for production,
  in-memory provider for tests per ADR-0009)
- BCrypt.Net-Next for password hashing
- JWT bearer auth with custom `cims:role` claim (ADR-0010);
  per-user revocation via `User.TokenInvalidationCutoff`
  checked in `JwtBearerEvents.OnTokenValidated`
  (B-001 / ADR-0014)
- ASP.NET Core RateLimiting (B-002) plus a per-IP failed-login
  back-off via `LoginAttemptTracker`
- xUnit for tests; in-memory EF provider; behavioural-test-first
  for every multi-tenant guard

## Conventions

- Conventional commits. Sprint branches during sprints
  (`sprint/sN-*`); short-lived feature branches between
  sprints (`feature/...`, `fix/...`, `chore/...`).
- The audit-twin pattern: every security-sensitive mutation
  emits both (a) a per-row `AuditInterceptor` audit and (b) a
  structured `AuditService.WriteAsync(action, ...)` event with
  semantic action name and structured detail.
- Behavioural tests use `UseInMemoryDatabase(Guid.NewGuid())`
  for hermetic per-test DBs and `StubTenantContext` for tenant
  context mutation.
- `docs/security/role-matrix.md` is authoritative for
  authorization. Any new endpoint or changed gate updates the
  matrix in the **same commit** as the code change.
- v1.1 backlog items get closed (struck-through) inline with
  the closing PR; CLOSED entries link the closing branch and
  record the rationale.

## Reporting security findings

This is an internal-pilot project; the developer / AI workflow
runs review-as-you-go via the `docs/security/` review docs.
External reporters: open a private GitHub Security Advisory.

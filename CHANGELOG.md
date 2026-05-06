# Changelog

All notable changes to CIMS — Construction Information Management
System.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [1.0] — 2026-MM-DD (release candidate)

The v1.0 internal-pilot release. 22 PAFM Appendix F functional
modules across 21 development sprints + 1 ship sprint, ISO 19650
+ PMBOK 7 alignment, multi-tenant by construction, audit-twin
forensic adequacy across the full mutation surface.

### Added — Foundations

- **Multi-tenant foundation** (S0). `ITenantContext` + per-entity
  global query filters keyed on
  `Project.AppointingPartyId == _tenant.OrganisationId`
  (or `OrganisationId` for non-project-scoped entities). ADR-0003.
- **Two-tier role authorization model** (S0). UserRole enum +
  RoleHierarchy + `HasMinimumRole` helper. ADR-0010.
- **Invitation-token user provisioning** (S0). Single-use
  SHA-256-hashed tokens; bootstrap-org flow for new tenants.
  ADR-0011.
- **Project tenancy semantics** (S0). `AppointingPartyId` locked
  to caller's org with SuperAdmin bypass. ADR-0012.
- **Audit interceptor + audit-twin atomicity** (S0 + post-S1
  PR #33). Every business mutation produces a structured audit
  row in the same DB transaction as the entity write.
- **Access-token residual-authority SLA** (post-S1 / B-001).
  `TokenInvalidationCutoff` + refresh sweep on revoke /
  deactivate. ADR-0014.

### Added — Functional modules (PAFM Appendix F.1–F.20)

- **F.2 Cost & Commercial** (S1). NEC4 payment certs (cumulative
  semantics per ADR-0013), variations (3-state), CBS,
  commitments, cost periods, EVM (PV/EV/AC, SPI/CPI, EAC),
  cashflow forecast.
- **F.3 Risk Management** (S2). Risk register with RBS taxonomy,
  qualitative + 3-point quantitative assessment, response
  strategies, contingency drawdown.
- **F.4 Stakeholder & Communications** (S3). Mendelow's
  Power/Interest matrix with auto-classification, engagement
  log, project-level communications matrix.
- **F.5 Schedule & Programme** (S4). CPM solver with
  MS-Project-style constraints, four dependency types (FS/SS/FF/SF
  with lag), baselines, Last Planner System (LPS), MS Project
  XML round-trip.
- **F.6 Change Control** (S5). 5-state change-request workflow;
  Scope/Time/Cost/Quality categorisation; BSA HRB tagging.
- **F.7 Procurement** (S6). Procurement strategy capture, tender
  packages (3-state), evaluation matrix (Price/Quality
  weighted), contracts, NEC4 early warnings + compensation
  events (5-state).
- **F.8 Reporting & Dashboards** (S7). Pre-built project
  dashboard, custom report builder (Risk / ActionItem / Rfi /
  Variation / ChangeRequest at v1.0).
- **F.9 ISO 19650 / MIDP / TIDP** (S9). Filename validator with
  full Type/Role/Suitability codebook, MIDP / TIDP entities.
- **F.10 Golden Thread / BSA 2022** (S10). Gateway packages
  (3-state), Mandatory Occurrence Reports, Safety Case, HRB
  project metadata.
- **F.11 UK GDPR** (S11). ROPA, DPIA (4-state), SAR (4-state with
  30-day clock), data breach log (72-hour notification clock),
  retention schedules.
- **F.12 Kaizen & Lessons Learned** (S12). Improvement Register
  with PDCA cycle, cross-project lessons library, Opportunity
  to Improve.
- **F.13 Inspection Activities** (S13, Option A scope cut).
  4-state workflow per inspection. Genera Systems QA/HSE
  bidirectional sync deferred to v1.1 / B-086..B-089.
- **F.14 Notifications & Alerts** (S14). In-app SignalR,
  SMTP email pipeline, threshold-based alerts on cost
  utilisation / open early warnings / open risks.
- **F.15 Search & Discovery** (S15). EF.Functions.Like cross-
  entity aggregator across 7 high-traffic project entity types
  with weighted ranking and types-filter.
- **F.16 Mobile / Responsive Views** (S17). MudDrawer
  Variant=Responsive at sm breakpoint, per-page table column
  responsive hiding, dialog FullWidth fixes.
- **F.17 Audit & Compliance Support** (S18). Evidence library
  (discriminated link to Document/RFI/AuditLog/InspectionActivity),
  External Auditor role with time-limited project assignment,
  audit export ZIP bundle.
- **F.18 Security & Performance** (S19). OWASP Top 10 review
  signed off, secrets management ADR (env vars), pen test
  scope spec, k6 load test script.
- **F.19 Documentation & Training** (S21). 22 user docs +
  3 admin docs + API doc + 3 role-based walkthrough scripts.
- **PAFM F.15 Admin Console** (= our S16, off-by-one mapping).
  4 admin pages under `/admin/*` (users / organisations /
  invitations / tenant audit) closing the bootstrap-via-SQL
  ritual.
- **Site-User Mobile Workflows** (S20, off-roadmap, B-107 +
  B-108 promoted from v1.1). Daily Diary entity + page,
  NCR raising flow + page (8th state machine,
  `Core/NcrWorkflow.cs`), 5 new audit-twin events.

### Security

- **OWASP Top 10 (2021) review** SIGNED OFF at v1.0 (S19).
  9 categories PASS, 1 PARTIAL closed by B-115 transitive
  upgrade. See `docs/security/owasp-top10-review-2026-05-05.md`.
- **SecurityHeadersMiddleware** (S19) sets
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, starter Content-Security-Policy.
- **HSTS** (`UseHsts()`, production-only, 30-day max-age) on
  HTTPS-deployed instances.
- **8-character minimum password floor** (S19 / OWASP A07).
  Full complexity rules deferred to v1.1 / B-117.
- **Zero vulnerable packages** post-B-115. EF Core + JwtBearer
  bumped to 8.0.10; explicit pins for System.Formats.Asn1 8.0.1,
  Azure.Identity 1.13.1, Microsoft.Identity.Client 4.66.2.
- **Secrets management** via env vars only per ADR-0016. Azure
  Key Vault deferred to v1.1 / B-114 (SaaS-mode rollout).

### Hardening (post-S1)

The post-S1 era retired ~30 backlog items via a smoke-test pass
that found four real, latent production bugs:

- **AuditLog.UserId nullable** (PR #41) — bootstrap-invitation
  audit row had been writing `Guid.Empty`, violating SQL Server
  FK constraint. Latent because EF in-memory ignores FKs.
- **AuthController body binding + 3 secret leaks** (PR #42) —
  missing `[ApiController]` meant register/login/refresh were
  silently broken in production; Project response leaked
  `User.PasswordHash`.
- **RefreshAsync opaque-token validation** (PR #43) — refresh
  endpoint was JWT-validating opaque hex tokens, returning
  401 INVALID_REFRESH on every call since initial commit.
- **AuthController [AllowAnonymous] scope** (PR #44) —
  class-level `[AllowAnonymous]` overrode action-level
  `[Authorize]` on `/me` and `/logout-everywhere`, returning
  500 ArgumentNullException instead of 401.

All four were 100% latent under unit tests; B-027 (SQL Server
smoke test in CI) caught them and now runs on every PR.

### Documentation (S21)

- 22 module docs in `docs/user/`
- 3 admin docs in `docs/admin/` (setup, operations, troubleshooting)
- API documentation at `docs/api/openapi.md` + Swagger UI in dev
- 3 role-based training walkthrough scripts in `docs/training/`
- 16 ADRs documenting architecture decisions
- 22 sprint logs + 22 retrospectives
- Scope reconciliation doc capturing the F-section / sprint
  number offset

### Tests

- **985 passing tests** at v1.0 (CI verified). Coverage spans
  service-layer behaviour, state machines, tenant isolation,
  audit-twin pinning, validation guards, role gates.

### Deferred to v1.1

See `docs/v1.1-backlog.md`. Highlights:

- Optimistic concurrency control on mutable entities (B-024)
- Cross-region scale-out (B-018, B-022)
- Genera Systems QA/HSE integration (B-086..B-089)
- Per-tenant configurable rules (HRB, evaluation criteria,
  retention, NCR workflow)
- Photo / file attachments on NCR + Daily Diary (B-120 / B-121)
- Multimedia training videos (B-124)
- ISO 19650 reconciliation (B-008) — S8 work parked

### Carry-forwards closed at v1.0 ship

- External pen test passed with zero open Critical findings
  (S19 spec at `docs/security/pen-test-scope-2026-05-05.md`).
- Load test passed at 100 VU × 5 min, p95 < 1s, error rate <
  0.1% (S19 script at `scripts/load-test/k6-100-vu.js`).
- UAT pass with one internal test user against one pilot
  project (S22).

---

## [Pre-1.0]

Pre-v1.0 sprint cadence. Each sprint shipped via squash-merge
PR with retro + backlog updates. See `docs/sprint-log/sN.md` +
`docs/retrospectives/sN.md` for per-sprint detail.

- **S0** Foundations (multi-tenant, audit, auth)
- **S1** Cost & Commercial — `v0.1-sprint-s1` @ 2026-04-27
- **S2** Risk & Opportunities — `v0.2-sprint-s2`
- **S3..S6** Stakeholder, Schedule, Documents, Quality
- **S7** Reporting & Dashboards — `v0.7-s7`
- **S8** CI Hardening — `v0.8-s8` (off-roadmap)
- **S9** ISO 19650 / MIDP — `v0.9-s9`
- **S10** Golden Thread / BSA 2022 — `v0.10-s10`
- **S11** UK GDPR — `v0.11-s11`
- **S12** Kaizen & Lessons Learned — `v0.12-s12`
- **S13** Inspection Activities — `v0.13-s13`
- **S14** Notifications & Alerts
- **S15** Search & Discovery (off-roadmap)
- **S16** Admin Console
- **S17** Mobile / Responsive Views
- **S18** Audit & Compliance Support
- **S19** Security & Performance
- **S20** Site-User Mobile Workflows (off-roadmap, B-107/B-108)
- **B-115** Vulnerable transitive packages upgrade (fast-follower)
- **S21** Documentation & Training
- **S22** UAT & Release (this entry)

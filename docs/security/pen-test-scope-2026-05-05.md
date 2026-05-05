# Pen test scope spec — CIMS v1.0 — 2026-05-05

T-S19-04 / PAFM F.18 first bullet. This document is the
**commissioning brief** for an external penetration test of CIMS
v1.0. It captures the target surface, threat model, scope
boundaries, severity rubric, and acceptance bar so the engaged
tester has everything needed to execute and report.

**Status: ready for commission.** User action: send to a
qualified pen-test firm; commission engagement; receive report;
close any open Critical findings before v1.0 ship (S22 UAT &
Release gate).

---

## 1. Application under test

- **Name:** CIMS — Construction Information Management System
- **Version:** v1.0 internal pilot (release-candidate at S22)
- **Stack:** ASP.NET Core 8.0 (Blazor Server + Web API),
  EF Core 8.0, SQL Server, MudBlazor 9 UI components.
- **Repo:** github.com/eduard77/CimsApp
- **Deployment shape:** single-tenant containerised deployment
  (per-customer); HTTPS via reverse proxy (e.g. Nginx, Azure
  Front Door, Cloudflare).

## 2. Target surface

### 2.1 Authentication endpoints (anonymous-allowed)

- `POST /api/v1/auth/register` — create user via invitation token
- `POST /api/v1/auth/login` — issue access + refresh tokens
- `POST /api/v1/auth/refresh` — rotate refresh token + issue access
- `POST /api/v1/auth/logout` — revoke refresh
- `POST /api/v1/organisations` — bootstrap a new tenant + invitation

### 2.2 Authenticated API surface (~80 endpoints across 18 sprints)

Enumerable from `Controllers.cs` + per-domain `*Controllers.cs`
files in `CimsApp/Controllers/`. High-level groupings:

- Auth & user admin: `/auth/*`, `/users/*`, `/admin/users/*`
- Organisation admin: `/admin/organisations/*`, `/admin/invitations/*`
- Project: `/projects/*`, `/projects/{id}/members/*`, `/projects/{id}/hrb`
- CDE: `/projects/{id}/cde/containers/*`, `/projects/{id}/documents/*`
- RFI / Actions: `/projects/{id}/rfis/*`, `/projects/{id}/actions/*`
- Cost: `/projects/{id}/cost/*`, `/projects/{id}/payment-certificates/*`
- Schedule: `/projects/{id}/schedule/*`, `/projects/{id}/lps/*`
- Risk / Change / Procurement / Stakeholder / Communications
- ISO 19650: `/projects/{id}/midp/*`, `/projects/{id}/tidp/*`
- BSA 2022 / Golden Thread: `/projects/{id}/gateway-packages/*`,
  `/projects/{id}/mor/*`, `/projects/{id}/safety-case/*`
- UK GDPR: `/organisations/{id}/ropa/*`, `/dpia/*`, `/sar/*`,
  `/data-breach/*`, `/retention-schedule/*`
- Kaizen / Lessons: `/projects/{id}/improvement-register/*`,
  `/lessons-learned/*`, `/opportunities-to-improve/*`
- Inspection (S13): `/projects/{id}/inspections/*`
- Notifications & Alerts (S14): `/notifications/*`, `/alert-rules/*`,
  SignalR hub at `/hubs/notifications`
- Search (S15): `/projects/{id}/search`
- Admin Console (S16): `/admin/*` (users / organisations /
  invitations / audit / auditor-assignments)
- Audit & Compliance (S18): `/projects/{id}/evidence/*`,
  `/admin/auditor-assignments/*`, `/projects/{id}/audit-export`

### 2.3 Static surface

- Blazor Server SignalR connection at `/_blazor`
- Static asset surface at `/_content/` (MudBlazor JS/CSS)
- Swagger at `/swagger/*` (development only — disabled in
  production unless explicitly enabled via env var; verify at
  deployed instance).

## 3. Threat model

### 3.1 Adversaries

- **Anonymous external attacker.** No credentials. Goal: gain
  initial foothold, extract tenant data, deny service.
- **Authenticated lower-privileged user (TaskTeamMember).**
  Member of one project. Goal: privilege escalation, cross-tenant
  data access, evidence tampering.
- **Authenticated higher-privileged user (OrgAdmin).** Tenant
  admin. Goal: SuperAdmin escalation, cross-tenant access via
  some bypass.
- **External Auditor (S18).** Project-scoped read-only role.
  Goal: write access on the assigned project, read access on
  unassigned projects in the same tenant.
- **Compromised SuperAdmin** (post-compromise scenario). Goal:
  exfiltrate every tenant's data without leaving an audit trail.

### 3.2 Asset value (high → low)

1. Per-tenant project data (commercial-sensitive: cost, risk,
   programme, NCRs, daily diaries).
2. UK GDPR-regulated personal data (ROPA / DPIA / SAR /
   data breach records).
3. Building Safety Act 2022 Golden Thread records (statutory).
4. Authentication credentials (BCrypt password hashes; opaque
   refresh tokens; SHA-256 invitation token hashes).
5. Audit trail (forensic value for incident response).

### 3.3 Cross-cutting expectations

- Tenant isolation: row-level via EF query filters
  (`AppointingPartyId == _tenant.OrganisationId`). Verified by
  per-sprint tenant-isolation tests (~30 entities × tests).
- Cross-tenant attempts return 404, never 403, to avoid
  existence-leak (post-S1 audit pattern; S16 OrganisationAdmin
  Update fix-forward).
- Audit-twin atomicity (PR #33): every business mutation
  produces a structured audit row in the same transaction.

## 4. Scope IN

- Web app at the deployed URL (HTTPS endpoint).
- All `/api/v1/*` endpoints, including admin and audit-export.
- Authentication flows (register / login / refresh / logout /
  invitation consumption / SuperAdmin promotion).
- Multi-tenant isolation: cross-tenant read AND write attempts
  with various role combinations.
- Privilege escalation: lower-privileged user → higher;
  ExternalAuditor → mutating role.
- Token security: JWT manipulation, refresh-token replay,
  expired-token reuse, signing-key brute-force resistance.
- Input validation / injection: SQL injection (EF Core
  parameterization), XSS (Blazor render context),
  command injection (none expected — no shell execution
  endpoints).
- Rate limiting / DoS resilience: login brute-force,
  general-purpose API flood, payload-size attacks.
- File upload surfaces (CBS CSV import, document register,
  evidence library): malformed file uploads, oversize uploads,
  unexpected MIME.
- Audit trail integrity: can an authenticated user mutate a
  record without the audit-twin event firing? Can the audit log
  be altered after the fact?
- HTTPS / transport: HSTS configured, certificate validation,
  protocol downgrade attempts.

## 5. Scope OUT

- Source code review — separate exercise (this OWASP review is
  the internal version; the pen test should attack the running
  app rather than reviewing source).
- Infrastructure / network layer (firewalls, load balancers,
  DNS) — covered by the deployment-team's separate posture.
- Dependent services (SQL Server itself, SMTP relay, hosting
  provider). Test the app's USE of these, not their internals.
- Social-engineering attacks against operators (out of scope
  for an application pen test).
- Physical security.
- Stress / load testing — that's T-S19-05 / k6-100-vu.js.

## 6. Test environment

- Pre-production deployment at a URL the user will provide on
  commission. Should mirror production configuration: HTTPS,
  HSTS enabled, security headers active, real SQL Server (not
  LocalDB), realistic seed data.
- **Test accounts** to be provisioned for the engagement:
  - One SuperAdmin (existing pattern: see
    `memory/project_dev_machines.md` post-S0 SQL UPDATE for
    GlobalRole = 0).
  - Two OrgAdmin accounts in two separate tenants (Org A and
    Org B) for cross-tenant testing.
  - One TaskTeamMember in Org A's project.
  - One ExternalAuditor with active assignment to Org A's
    project.
- **Test data:** at least one project per tenant with documents,
  RFIs, actions, evidence rows. Synthetic data — no real personal
  information, no real Building Safety Act records.

## 7. Severity rubric

Standard CVSS v3.1 base score for severity. CIMS context maps:

- **Critical (CVSS 9.0+):** Unauthenticated RCE, unauthenticated
  full-tenant data exfiltration, authentication bypass against
  any authenticated endpoint, signing-key compromise leading to
  token forgery.
- **High (CVSS 7.0–8.9):** Cross-tenant data access without
  legitimate authority, privilege escalation
  (TaskTeamMember → OrgAdmin / SuperAdmin), audit-trail
  bypass on a tenant-scoped mutation, persistent stored XSS in
  any authenticated view.
- **Medium (CVSS 4.0–6.9):** Reflected XSS in the auth flow,
  IDOR yielding stale-data leak (e.g. expired-but-readable
  records), CSRF on a state-changing endpoint, rate-limit
  evasion enabling brute-force at >100 attempts/minute.
- **Low (CVSS 0.1–3.9):** Verbose error message leaking stack
  traces, missing security header (e.g. CSP), banner-grabbable
  framework version, weak TLS cipher in the staging env.
- **Info:** Best-practice violations without a directly-named
  attack vector.

## 8. Acceptance bar — v1.0 ship gate

- **0 (zero) Critical findings open** at v1.0 ship.
- **0 High findings open**, OR every High finding has a
  written, signed-off mitigation/compensating-control plan
  with a v1.1 close-out date.
- **Medium findings:** triaged into v1.1 backlog as B-NNN
  entries with explicit unblock conditions. No close-out
  required for v1.0 ship UNLESS triage discovers a Medium
  that's actually a High in disguise.
- **Low / Info:** Catalogued in the report; informs v1.1
  hardening backlog; no close-out required.

## 9. Deliverables expected from the tester

- Executive summary (one page).
- Per-finding writeup: title, severity, CVSS vector, affected
  endpoint(s), reproduction steps, remediation recommendation.
- Demonstrative artefacts (HTTP captures, screenshots) for
  any finding ≥ Medium.
- Re-test pass for any finding marked "fixed" by the
  development team (one re-test per finding, included in the
  engagement).

## 10. Engagement timeline (suggested)

- **Day 0:** Kickoff call. Hand over test environment URL +
  credentials + this scope doc.
- **Day 1–8:** Active testing.
- **Day 9–10:** Report drafting.
- **Day 11:** Report delivery.
- **Day 12–18:** Development team remediates findings in the
  v1.0 ship-gate Critical / High set.
- **Day 19–20:** Re-test pass for fixed findings.
- **Day 21:** Sign-off on the v1.0 ship gate (PAFM F.18 first
  bullet).

## 11. Known not-yet-shipped items the tester should NOT report
   as findings

- B-018 LoginAttemptTracker single-instance state (designed for
  multi-instance scale-out, deferred to v1.1).
- B-022 OnTokenValidated DB lookup cache (deferred to v1.1
  alongside scale-out).
- B-024 Optimistic concurrency control on mutable entities
  (rowversion / ETag / 409 mapping; deferred to v1.1).
- B-115 Vulnerable transitive packages (S19 OWASP review tracked;
  fix-forward candidate before pen test starts).
- B-117 Full password complexity rules (v1.0 ships 8-char
  minimum; full rules deferred).

If the tester finds an issue mapped to one of the above, please
raise it for discussion rather than as a v1.0 ship-blocking
finding.

---

**Sign-off:** When the engagement is closed and the v1.0 ship
gate is passed, append a sign-off block here naming the tester,
report date, and the agreed Critical / High close-out outcome.

# OWASP Top 10 (2021) review — 2026-05-05

T-S19-02. Per-category walkthrough of the CIMS codebase against the
OWASP Top 10 2021 list. Each category gets PASS / PARTIAL / FAIL,
with code-citations and rationale. **PASS** = the category is
adequately covered for v1.0 internal pilot. **PARTIAL** = some
controls in place but with named gaps; gap closure is fix-forward
inside this sprint OR a tracked v1.1 backlog entry. **FAIL** = a
control is required and missing; fix-forward in this sprint is
required before sign-off.

This review is signed off when every category is **PASS** or
**PARTIAL with a tracked unblock**.

---

## A01:2021 — Broken Access Control

**Result: PASS**

Tenant query filters apply globally to every tenant-scoped entity
(post-S1 audit retired this as a structural concern):

- `CimsDbContext.cs:876` — `m.Entity<AuditLog>().HasQueryFilter(...)` and
  per-entity filters for every project / org-scoped entity (~25
  entities total).
- ADR-0003 codifies the row-level multi-tenancy pattern.
- ADR-0007 documents the SuperAdmin filter-bypass exception
  (`IgnoreQueryFilters` only on auth-domain mutations and explicit
  cross-tenant admin actions).

Role hierarchy + `HasMinimumRole` helper:

- `Core.cs:44-54` — `RoleHierarchy` array; `HasMinimumRole` returns
  false when role is outside the hierarchy (e.g.
  `UserRole.ExternalAuditor` from S18).
- Two-tier model documented at ADR-0010.

IDOR (insecure direct object reference) protections:

- Cross-tenant reads return 404 (no existence leak), enforced via
  the query filter — see PR #41 era post-S1 audits.
- Cross-tenant Org Update specifically corrected during S16 CI:
  `OrganisationAdminService.UpdateAsync` pre-scopes the lookup
  (commit `1007e58`); the role-set fix from S16 was the headline
  test of this pattern.

ExternalAuditor scope (S18):

- `AuditorProjectAssignment.IsActive` computed property returns
  false on revoke or expiry; `CimsControllerBase.GetProjectRoleAsync`
  fall-through (`Controllers.cs:22-46`) reflects this.
- ADR-0014 cutoff bump + refresh sweep on revoke kills in-flight
  sessions.

**No new findings. Sign-off: PASS.**

---

## A02:2021 — Cryptographic Failures

**Result: PASS**

Password storage:

- `Services.cs:57` — `BCrypt.Net.BCrypt.HashPassword(req.Password)`
  with the library's default cost factor (work factor 11 in
  BCrypt.Net 4.x). Verify-only path at `Services.cs:117`.

Token shape:

- Access tokens: JWT signed HS256 with the configured
  `Jwt:AccessSecret`. Rotation by env-var swap at deploy.
- Refresh tokens: opaque hex (`Guid.NewGuid().ToString("N")` × 2 =
  64 chars), DB-stored, NOT JWT-validated (PR #43 corrected the
  earlier bug where opaque tokens were JWT-validated and 100%
  failed).
- Invitation tokens: SHA-256 hash stored, plaintext shown once
  (Invitation entity comment at `Entities.cs:653-661`).

Secrets in transit:

- `Program.cs:292` — `app.UseHttpsRedirection()` enforces HTTPS.
- HSTS missing at v1.0 — see A05 below; fix-forward in this sprint.

Sensitive field exclusion:

- `[JsonIgnore]` on `User.PasswordHash`, `RefreshToken.Token`,
  `Invitation.TokenHash` (PR #42) — `EntitySerializationTests` pin
  the contract.
- `AuditInterceptor.SkippedFieldNames` excludes the same fields
  from audit JSON.

**No new findings. Sign-off: PASS.**

---

## A03:2021 — Injection

**Result: PASS**

EF Core parameterized queries:

- All `db.<DbSet>.Where(...)` calls use LINQ → parameterized SQL.
- `EF.Functions.Like` used in S15 search uses parameterized LIKE
  patterns; `SearchQueryEscape.EscapeLike` handles `%` `_` `[`
  wildcards explicitly (S15 T-S15-04 tests).

Raw SQL audit:

- Single occurrence: `Program.cs:310` —
  `db.Database.ExecuteSqlRawAsync("SELECT 1")` for health check.
  Static literal, no input concatenation. Not exploitable.

Cross-cutting input validation:

- `[ApiController]` triggers automatic 400 on model state
  validation failures.
- Service-layer null/empty guards for user-supplied DTOs
  (e.g. `Services.cs:37-39` AuthService.RegisterAsync).

**No new findings. Sign-off: PASS.**

---

## A04:2021 — Insecure Design

**Result: PASS**

Threat-model-by-sprint discipline:

- Every sprint kickoff includes a "Top-3 risks (CATEGORY only)"
  section since S9-retro pattern. Risks are named at design time;
  mitigations land alongside the implementation.
- ADRs document architecture decisions; `docs/adr/ADR-0001`..
  `docs/adr/ADR-0015` (with -0015 landing this sprint).

Security-sensitive surfaces audited:

- `docs/security/post-s1-auth-mutation-audit-2026-04-28.md`
- `docs/security/post-s1-role-matrix-audit-2026-04-28.md`
- `docs/security/post-s1-secondary-mutation-audit-2026-04-29.md`
- (this doc)

Audit-twin atomicity (PR #33) — every business mutation produces
a structured audit row in the same transaction. Forensic
discoverability is by-design, not bolted on.

**No new findings. Sign-off: PASS.**

---

## A05:2021 — Security Misconfiguration

**Result: PARTIAL → PASS after this commit's fix-forward**

Missing middleware (FOUND, FIX-FORWARD this commit):

- ❌ `UseHsts()` not present. Production deployments are
  HTTPS-redirected but a downgrade-attack window exists for
  the very first request to a new client.
- ❌ Security headers middleware not present. No
  `X-Frame-Options`, `X-Content-Type-Options`,
  `Referrer-Policy`, or basic `Content-Security-Policy`.
- ❌ HSTS lifetime not configured.

Fix-forward in this commit: add a small `SecurityHeadersMiddleware`
that sets `X-Frame-Options: DENY`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
and a starter `Content-Security-Policy` (default-src 'self';
connect-src 'self'; img-src 'self' data:; style-src 'self'
'unsafe-inline'; script-src 'self' 'unsafe-inline'; — the
'unsafe-inline' relaxations are needed for the MudBlazor inline
styles + Blazor's interop scripts; tightened in v1.1 / B-115).
HSTS configured at `app.UseHsts()` in non-Development environments
with 1-year max-age.

**Sign-off: PASS** (after fix).

---

## A06:2021 — Vulnerable and Outdated Components

**Result: PARTIAL → tracked**

`dotnet list package --vulnerable --include-transitive` reports:

| Package | Resolved | Severity | Source |
|---|---|---|---|
| Microsoft.Extensions.Caching.Memory | 8.0.0 | HIGH | GHSA-qj66-m88j-hmgj (DoS) |
| System.Formats.Asn1 | 5.0.0 | HIGH | GHSA-447r-wph3-92pm |
| System.Text.Json | 8.0.0 | HIGH | GHSA-hh2w-p6rv-4g7w + GHSA-8g4q-xg66-9fp4 |
| Azure.Identity | 1.10.3 | Moderate | × 2 |
| Microsoft.Identity.Client | 4.56.0 | Low + Moderate | × 2 |

All five are TRANSITIVE. Resolution path: update direct
dependencies (.csproj `PackageReference`) which pull current
transitive versions.

**Fix-forward this sprint:** B-115-NEW promoted as a fast-follower
on master rather than a v1.1 deferral — the high-severity DoS
vector in `Microsoft.Extensions.Caching.Memory` is reachable from
the login attempt tracker (S0 / B-002) and the alert evaluator
(S14). Deferred until I can land the version bumps cleanly with
build + tests; the upgrade path is straightforward but adding to
this commit risks dependency conflicts.

(Update at sprint-close-time: see B-115 / B-116 in v1.1-backlog +
the carry-forward in `docs/sprint-log/s19.md`.)

**Sign-off: PARTIAL** (with B-115 tracking).

---

## A07:2021 — Identification and Authentication Failures

**Result: PARTIAL → PASS after this commit's fix-forward**

Login rate limiting + back-off (B-002, S0):

- ✅ `LoginAttemptTracker` (`Services/Auth/LoginAttemptTracker.cs`)
  + `[EnableRateLimiting("anon-login")]` on the login endpoint.

Token revocation:

- ✅ B-001 / ADR-0014 — `TokenInvalidationCutoff` + refresh sweep.

Session management:

- ✅ Refresh tokens rotate on every refresh; old token row is
  marked revoked.

Password complexity (FOUND, FIX-FORWARD this commit):

- ❌ No password length / complexity rules. `RegisterAsync` accepts
  any non-empty password and BCrypt-hashes it. A user could
  register with `password=a`. PAFM doesn't specify a complexity
  rule explicitly but A07 calls out "weak password recovery
  process" and "permitting brute force or other automated
  attacks" — adding a minimum bar is honest.

Fix-forward: add a minimum-length check (≥ 8 chars) at
`RegisterAsync`. v1.1 / B-117 candidate: full complexity rules
(uppercase + digit + symbol; common-password dictionary lookup).

**Sign-off: PASS** (after fix).

---

## A08:2021 — Software and Data Integrity Failures

**Result: PASS**

CI pipeline (S8 era):

- ✅ Build + test on every PR (`Build and test` check on PR #60
  onwards).
- ✅ Migration round-trip CI check (Up → Down to 0 → Up cleanly).
- ✅ B-027 SQL Server smoke CI check — caught the
  OrganisationAdminService bug at S16.

Audit-twin atomicity:

- ✅ `AuditService.WriteAsync` adds the audit row to the change
  tracker; a single SaveChanges commits both halves of the
  audit-twin atomically (PR #33).

Migration discipline:

- ✅ EF migrations versioned in git; never edited post-merge.
- ✅ Migration round-trip CI check forces every migration to be
  reversible.

**No new findings. Sign-off: PASS.**

---

## A09:2021 — Security Logging and Monitoring Failures

**Result: PASS**

Audit interceptor (S0):

- ✅ `AuditInterceptor` logs every CRUD operation on tenant-scoped
  entities with Action / Entity / Before / After / UserId.

Structured audit-twin events (B-021 + extensions):

- ✅ Auth domain: `auth.user_admin_revoke`,
  `auth.user_deactivated`, `auth.user_self_revoke`,
  `auth.user_global_role_set` (S16).
- ✅ Domain mutations: `project.created`, `document.created`,
  `rfi.created`, `rfi.responded`, `action.created`,
  `action.updated`, `evidence.added`, `evidence.removed`
  (S18), `auditor.assigned`, `auditor.revoked` (S18), and
  ~15 more across domain modules.
- Every structured event is pinned by at least one explicit
  test assertion.

Tenant-scoped audit query:

- ✅ `/admin/audit` (S16) — tenant-wide audit viewer with
  date / action / entity / userId filters.

App-level logging:

- ✅ ASP.NET Core's default `ILogger` infrastructure;
  Information / Warning / Error levels reach the console (dev)
  and host log target (prod).

**Possible enhancement (NOT a finding, deferred):** structured
log shipping to a SIEM (Application Insights / Seq / Splunk) is
deployment-config territory, not v1.0 codebase concern. Tracked
as v1.1 / B-118 if pilot operations need it.

**No new findings. Sign-off: PASS.**

---

## A10:2021 — Server-Side Request Forgery (SSRF)

**Result: PASS (vacuous)**

The codebase has **no outbound HTTP request surface that takes a
user-supplied URL**. Verified by grep:

- `IEmailSender` (S14) → SMTP only, configured server-side.
- No webhook subscription endpoints (B-087 deferred to v1.1).
- No Genera Systems REST integration yet (B-086 deferred).
- No OAuth or external-IdP federation (B-009 deferred).
- No `HttpClient` instances reading from user-supplied URLs.
- No image proxy or URL-fetch features.

If B-086 / B-087 (Genera REST + webhooks) lands in v1.1, the
incoming URLs MUST be validated against an allow-list (no
arbitrary external destinations).

**No new findings. Sign-off: PASS.**

---

## Summary

| Category | Result | Notes |
|---|---|---|
| A01 Broken Access Control | PASS | Tenant filters + role hierarchy + IDOR-safe |
| A02 Cryptographic Failures | PASS | BCrypt + JWT HS256 + opaque refresh + JsonIgnore |
| A03 Injection | PASS | EF Core parameterized; LIKE escape; one static health-check raw SQL |
| A04 Insecure Design | PASS | Threat-model-by-sprint + ADRs + audit-twin |
| A05 Security Misconfig | **PASS after fix-forward** | HSTS + security headers added this commit |
| A06 Vulnerable Components | **PARTIAL with B-115** | 3 HIGH transitive vulns; package upgrade tracked |
| A07 AuthN Failures | **PASS after fix-forward** | Min-8-char password rule added this commit |
| A08 SW & Data Integrity | PASS | CI checks + migration round-trip + audit-twin atomicity |
| A09 Logging & Monitoring | PASS | Audit interceptor + structured events + tenant audit viewer |
| A10 SSRF | PASS | Vacuous — no outbound request surface |

**OWASP Top 10 review SIGNED OFF** for v1.0 with one tracked
follow-up (B-115 vulnerable transitive packages, fix-forward
candidate within S19 if dependencies update cleanly; otherwise
v1.1).

Sign-off date: 2026-05-05.
Next OWASP review: at S22 UAT & Release before v1.0 ship.

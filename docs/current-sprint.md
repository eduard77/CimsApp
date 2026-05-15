# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Status:** Between sprints — S21 closed 2026-05-06;
**S22 not yet kicked off** (scope pinned). **One sprint to v1.0
ship.**

**Active sprint:** none.

**Next sprint (S22):** **F.20 — UAT & Release.** v1.0 ship
gate. Five bullets:
- 2-week UAT with one internal test user against one real
  pilot project
- All critical UAT defects closed
- Release candidate tagged + deployed to production
- Go-live checklist completed
- CHANGELOG updated to v1.0

**Structurally different from S0-S21.** Most work is
**gated on external execution**: user runs UAT, commissions /
receives pen-test report, runs load test, optionally records
training videos. The agent's role at S22 is **fixing UAT
findings + writing the release artefacts** (CHANGELOG,
go-live checklist), not kicking off a single-session
implementation sprint.

**Three ship-gate carry-forwards close at S22:**

- **Pen test commission** —
  `docs/security/pen-test-scope-2026-05-05.md` is the
  commissioning brief. User actions: send to qualified firm,
  receive report, close any Critical findings.
- **Load test execution** — `scripts/load-test/k6-100-vu.js`
  + README. User actions: provision staging instance + seed
  user, run k6, document pass/fail.
- **Training video production (optional)** — scripts at
  `docs/training/*.md`; videos = v1.1 / B-124. v1.0 can ship
  without if pilot operator is OK with text walkthroughs.

**v1.0 code is feature-complete after S21.** Backlog at
B-001..B-126 (B-116 unused; B-107/B-108/B-115 closed). All 22
PAFM Appendix F functional modules shipped. OWASP Top 10
SIGNED OFF, zero vulnerable packages, audit-twin atomicity,
multi-tenant isolation, External Auditor role with
time-limited access, complete documentation surface.

**Important — F-section / sprint-number offset:** PAFM maps
**`F.N = S(N-1)`** end-to-end (drift originated at our S8 = "CI
Hardening", which is not in the PAFM roadmap; another off-roadmap
insertion at S15 = Search & Discovery). Our S16 / S17 kickoffs
labelled themselves F.16 / F.17 which was wrong — they shipped
PAFM F.15 (Admin Console) and F.16 (Mobile Views) respectively.

**Confirmed v1.0 sprint roadmap (post-reconciliation, S18 / S19
/ S20 / S21 + B-115 shipped):**

| Sprint | Scope | Source |
|---|---|---|
| ~~S18~~ | ~~Audit & Compliance Support~~ — **shipped** | PAFM F.17 |
| ~~S19~~ | ~~Security & Performance~~ — **shipped** (carry-forwards: pen test, load test) | PAFM F.18 |
| ~~S20~~ | ~~Site-User Mobile Workflows~~ — **shipped** | B-107 / B-108 |
| ~~B-115~~ | ~~Vulnerable transitive packages~~ — **shipped** | OWASP A06 from S19 |
| ~~S21~~ | ~~Documentation & Training~~ — **shipped** | PAFM F.19 |
| **S22 (next)** | UAT & Release | PAFM F.20 — v1.0 ship gate (closes pen test + load test + training video carry-forwards) |

S20 inserts a third off-roadmap sprint to close two real PAFM F.16
product gaps that the original Mobile Views sprint (our S17, scoped
as a responsive sweep) couldn't address — Daily Diary and NCR-raising
flows don't yet exist as v1.0 product features. Slotting between
F.18 Security and F.19 Documentation rather than before F.17 trades
"Evidence library covers them" against "minimal disruption to
already-pinned S18".

**Last sprint:** S21 — Documentation & Training (PAFM-SD F.19).
Squash-merged at `f024aa4` (2026-05-06, PR #71). See
`docs/sprint-log/s21.md` and `docs/retrospectives/s21.md`. 4 of
4 module-DoD bullets at v1.0: 22 user docs in `docs/user/`,
3 admin docs in `docs/admin/`, API doc at `docs/api/openapi.md`,
3 role-based walkthrough scripts in `docs/training/`. Training
videos deferred to v1.1 / B-124.
Twenty-second consecutive single-session sprint; eighteenth
without a CR.

**Most recent fast-follower:** B-115 vulnerable transitive
packages upgrade (PR #69 merged 2026-05-06). Five transitive
vulnerabilities cleared. `dotnet list package --vulnerable`
now reports zero findings.

**Previously closed:**
- S20 — Site-User Mobile Workflows (off-roadmap; B-107 + B-108).
  Squash-merged at `44e049b` (2026-05-05, PR #68). Daily Diary
  + NCR raising. Heaviest sprint by hours since S18.
- S19 — Security & Performance (= PAFM F.18). Squash-merged at
  `1347056` (2026-05-05, PR #66). OWASP Top 10 review SIGNED
  OFF. ADR-0016 secrets management. Pen test + load test as
  external carry-forwards.
- S18 — Audit & Compliance Support (= PAFM F.17). Squash-merged
  at `a9e77a9` (2026-05-05, PR #64). 3 of 3 bullets shipped.
- S17 — Mobile / Responsive Views (= PAFM F.16). Squash-merged
  at `bbdae7c` (2026-05-05, PR #62). UI-only sprint.
- S16 — Admin Console (= PAFM F.15). Squash-merged at `dd62441`
  (2026-05-04, PR #60). CI caught a latent existence-leak bug,
  fixed pre-merge.
- S15 — Search & Discovery (off-roadmap; not in PAFM Appendix F).
  Merged at `190fa87` (2026-05-03).

**Branch:** `master` (live work between sprints lands on short-lived
feature branches off master, not a long-running sprint branch;
direct pushes to master are blocked — every change goes through PR).

**Tests:** 985 / 985 green (CI). Build clean (0 warnings, 0 errors).
Zero vulnerable packages reported by
`dotnet list package --vulnerable --include-transitive`.
Backlog at B-001..B-126 (B-107/B-108/B-115 closed; B-116 unused;
numbering aligned with what landed).

---

## Sprints closed in the S2 → S15 sweep (2026-05-01 → 2026-05-03)

Twelve sprints, zero CRs raised, single-session cadence throughout.
Each ships a feature module + behavioural tests + tenant-filter sweep
+ role-matrix entry + retrospective + v1.1 backlog entries with
explicit unblock conditions. Per-sprint scope:

- **S2 — Risk & Opportunities** (PAFM F.3). Tag `v0.2-sprint-s2`.
  8 of 9 module-DoD bullets; CR-004 deferrals B-028 (Monte Carlo,
  also blocked on S4 CPM data), B-029 (opportunity register),
  B-030 (cross-module contingency drawdown link).
- **S3 — Stakeholder & Communications** (PAFM F.4).
- **S4 — Schedule & Time** (PAFM F.5).
- **S5 — Document & Drawing Control** (PAFM F.6).
- **S6 — Quality & HSE** (PAFM F.7).
- **S7 — Reporting & Dashboards** (PAFM F.8). Tag `v0.7-s7`.
- **S8 — CI Hardening / branch-protection groundwork.** Tag
  `v0.8-s8`. T-S8-05 follow-up: scheduled agent
  `trig_01BJG8zcU9WBEh3hbM6RRtEN` armed for **2026-05-18 09:00 UTC**
  to enable `master` branch protection.
- **S9 — ISO 19650 / MIDP / TIDP** (PAFM F.9). Tag `v0.9-s9`.
  Backlog B-065..B-069.
- **S10 — Golden Thread / BSA 2022** (PAFM F.10). Tag `v0.10-s10`.
  Gateway / MOR / SafetyCase / GoldenThread + HRB project metadata.
  Backlog B-070..B-075.
- **S11 — UK GDPR** (PAFM F.11). Tag `v0.11-s11`. Backlog B-076..B-080.
- **S12 — Kaizen & Lessons Learned** (PAFM F.12). Tag `v0.12-s12`.
  Backlog B-081..B-085.
- **S13 — Inspection & Activities** (PAFM F.13, Option A scope cut).
  Tag `v0.13-s13`. Backlog B-086..B-089. **PAFM Ch 47 paste**
  outstanding for any future Genera REST integration work.
- **S14 — Notifications & Alerts** (PAFM F.14). SignalR
  NotificationsHub + IEmailSender + AlertRule + threshold evaluator.
  Backlog B-090..B-094. (No version tag landed.)
- **S15 — Search & Discovery** (PAFM F.15). EF.Functions.Like-based
  cross-entity search aggregator over 7 entity types; types[] filter;
  LIKE wildcard escape. Backlog B-095..B-098. (No version tag landed.)
- **S16 — Admin Console** (PAFM F.16, predicted). 4 services + 4
  controllers + 4 MudBlazor pages under `/admin/*` (users / orgs /
  invitations / tenant audit). Closes the laptop-pickup ritual that
  previously needed PowerShell + raw SQL UPDATE for SuperAdmin
  promotion. Backlog B-099..B-101. (No version tag landed.)
- **S17 — Mobile / Responsive Views** (= PAFM F.16). UI sweep:
  `MainLayout` MudDrawer flipped from Mini to Responsive (mobile
  gets slide-in temporary drawer + hamburger; desktop unchanged);
  six MudTable pages get per-column responsive hiding via
  `d-none d-{breakpoint}-table-cell` utility classes;
  AdminOrganisations Edit dialog FullWidth fix. No service /
  contract changes; test count unchanged at 957. Backlog
  B-102..B-104. (No version tag landed.)
- **S18 — Audit & Compliance Support** (= PAFM F.17). Three
  services + three controllers + two entities + Razor UI
  fast-follower. EvidenceArtefact, AuditorProjectAssignment,
  audit-export ZIP. New `UserRole.ExternalAuditor` enum value
  outside the role hierarchy. Test count 957 → 972. Backlog
  B-109..B-111.
- **S19 — Security & Performance** (= PAFM F.18).
  Non-development sprint. OWASP Top 10 (2021) review SIGNED
  OFF. SecurityHeadersMiddleware + UseHsts (A05). 8-char
  password floor (A07). ADR-0016 secrets management. Pen test
  scope spec + k6 load-test script delivered for external
  user-execution. Test count 972 → 973. Backlog
  B-112..B-115 + B-117..B-119.
- **S20 — Site-User Mobile Workflows** (off-roadmap; B-107 +
  B-108 promoted to v1.0). Two new entities (DailyDiaryEntry +
  Ncr), 8th state machine (NcrWorkflow), 5 new audit-twin
  events, two Razor pages, nav entries. Closes the PAFM F.16
  product gaps the S17 responsive-sweep couldn't address.
  Test count 973 → 985. Backlog B-120..B-123.
- **B-115** fast-follower (PR #69, 2026-05-06): vulnerable
  transitive packages upgrade. EF Core / JwtBearer 8.0.2 →
  8.0.10; explicit pins for System.Formats.Asn1, Azure.Identity,
  Microsoft.Identity.Client. Zero vulnerable packages
  remaining.
- **S21 — Documentation & Training** (= PAFM F.19). 22 user
  docs (`docs/user/`) + 3 admin docs (`docs/admin/`) + 1 API
  doc (`docs/api/openapi.md`) + 3 training walkthrough scripts
  (`docs/training/`). Training videos deferred to v1.1 / B-124.
  Test count unchanged at 985 (docs sprint). Backlog
  B-124..B-126.

Detail: `docs/sprint-log/s<N>.md` + `docs/retrospectives/s<N>.md`
per sprint. Git: `git log --oneline c0cfd53..f024aa4`.

---

## Earlier (S0 → S1 + post-S1 hardening, kept for reference)

S0 — Foundations (auth, multi-tenancy, audit). S1 — Cost & Commercial
(NEC4 payment certs, EVM, cashflow). Tag `v0.1-sprint-s1` at
`330a09c` (2026-04-27).

Post-S1 hardening (2026-04-27 → 2026-04-30) retired ~30 items:
B-001 token revocation, B-002 rate limit + back-off, B-005/B-006
ownership checks, B-007 org-enumeration leak, B-013 MudBlazor 6 → 9,
B-017 EVM/cashflow wire-ups, B-019 refresh-token bulk-revoke,
B-021 audit-domain structured events, B-023 AddMember org-match,
ADR-0007 written, README populated, transaction wraps for
RegisterAsync / Document.TransitionAsync / OrganisationsController.Create
(PR #29 / #34 / #35), audit-twin atomicity refactor (PR #33),
audit-twin coverage completion (PR #36 + #39).

**The four real, latent production bugs caught by the SQL Server
smoke walk (PRs #41-#44):** AuditLog.UserId FK violation;
AuthController missing [ApiController] (auth surface 100% broken in
prod) + Project response leaking PasswordHash; RefreshAsync
JWT-validating opaque tokens (refresh 100% broken); class-level
[AllowAnonymous] silently overriding action-level [Authorize] on /me
+ /logout-everywhere. All four were 100% latent under unit tests
because EF in-memory ignores FK constraints, model binding, and JWT
parsing edges. **B-027 (SQL Server smoke test in CI)** is concretely
justified four times over — promotion is in the v1.1 backlog.

Post-S1 audit docs (still load-bearing as checklists when touching
the relevant surface):

- `docs/security/post-s1-auth-mutation-audit-2026-04-28.md`
- `docs/security/post-s1-role-matrix-audit-2026-04-28.md`
- `docs/security/post-s1-secondary-mutation-audit-2026-04-29.md`

---

## Inherited invariants (must NOT regress)

From S0 / S1 / post-S1 hardening:
- `ITenantContext` + global query filters on every tenant-scoped
  entity (ADR-0003).
- Two-tier role authorization model (ADR-0010).
- Audit interceptor populates Action / Entity / Before / After /
  UserId on every tenant-scoped write; `SkippedFieldNames` excludes
  PasswordHash / TokenHash from audit JSON.
- Invitation-token registration flow (ADR-0011) — every new User
  comes through a token.
- Project AppointingPartyId locked to caller's org with SuperAdmin
  bypass (ADR-0012).
- NEC4 cumulative semantics for payment certificates (ADR-0013).
- Access-token residual-authority SLA: cutoff bump + refresh sweep
  on revoke / deactivate (ADR-0014, §3 amended for refresh tokens).
- Audit-twin atomicity (PR #33): `AuditService.WriteAsync` adds the
  AuditLog row to the change tracker; a single SaveChanges commits
  both the entity write and the structured event in one transaction.
- `[JsonIgnore]` on `User.PasswordHash`, `RefreshToken.Token`,
  `Invitation.TokenHash` (PR #42); `EntitySerializationTests` pin
  the contract.
- `AuditLog.UserId` is `Guid?` (PR #41) — anonymous-actor audit
  rows record null honestly; query filter handles `User == null`.
- `[AllowAnonymous]` is scoped per-action on `AuthController`, never
  class-level (PR #44).
- Refresh tokens are opaque hex (Guid×2 = 64 chars); RefreshAsync
  authenticates by DB lookup, NOT by JWT validate (PR #43).

From S2 → S15 sprints:
- Every new tenant-scoped entity gets a global query filter and a
  tenant-isolation behavioural test (verified by per-sprint tenant
  filter sweep, e.g. `chore(s11): T-S11-07 tenant filter sweep`).
- Every new endpoint gets a row in the role matrix.
- EF DbContext is NOT thread-safe under `Task.WhenAll` over a
  single scoped context. Fan-out across providers is **sequential
  await**, not parallel (S15 aggregator pattern).

---

## Pending obligations (carry-forward)

- **`master` branch protection** scheduled to land 2026-05-18 09:00 UTC
  via agent `trig_01BJG8zcU9WBEh3hbM6RRtEN` (T-S8-05 follow-up;
  verified armed at S15 kickoff).
- ~~**PAFM-SD F.18..F.19 .docx paste**~~ **CLOSED 2026-05-05.** The
  PAFM-SD `.docx` was read end-to-end. Reconciliation findings at
  `docs/scope-reconciliation-2026-05-05.md`; B-105..B-108
  promoted from PAFM gaps. F-section / sprint-number offset
  documented (`F.N = S(N-1)`).
- **PAFM Ch 47 paste** for any future F.13 / Genera Systems REST
  integration work (B-086..B-089 unblock condition).
- **Pre-pilot legal review** carry-forward unchanged (UK GDPR + BSA
  2022 field shapes).
- **Laptop WDAC** blocks rebuilt `GeneraPm.dll` from `dotnet test`
  discovery (error `0x800711C7`, kernel-level, hash-based). First
  hit S16. Resolution options at the policy layer (WDAC rule for
  `C:\Code\GeneraPm\**\*.dll`, Smart App Control toggle, signed
  assemblies); until then, test verification on the laptop happens
  via push-to-CI rather than locally. See
  `memory/project_laptop_wdac.md`.

---

## Out of scope between sprints

- Everything in PAFM-SD Ch 3 anti-scope.
- v1.1-tagged hardening that's sprint-bound:
  - B-014 Construction Act notices (CR-003 deferral).
  - B-015 Final account schedule (CR-003 deferral).
  - B-016 Variations 6-state workflow (CR-003 deferral).
  - B-018 LoginAttemptTracker single-instance (pre-customer
    scale-out).
  - B-022 OnTokenValidated DB lookup cache (alongside B-018).
  - B-024 Optimistic concurrency control on mutable entities
    (rowversion / ETag / 409 mapping; ~12-16h sprint-shaped).
  - B-028..B-030 (S2 CR-004 deferrals).
  - B-095..B-098 (S15 deferrals).
  - B-099..B-101 (S16 deferrals — Admin Console v2, audit viewer
    optimisation, bulk admin operations).
  - B-102..B-104 (S17 deferrals — mobile-first UX redesign,
    per-page card-view tables, bottom navigation).
  - B-105 / B-106 (PAFM F.15 reconciliation gaps — template
    library, subscription view).
  - B-107 / B-108 — **closed S20** (Daily Diary + NCR).
  - B-109..B-111 (S18 deferrals — External Auditor cross-tenant
    scoping, PDF audit export, per-tenant evidence required-set).
  - B-112..B-114 + B-117..B-119 (S19 deferrals — pen test
    substitute, cloud load testing, Azure Key Vault, password
    complexity, SIEM log shipping, CSP tightening).
  - B-115 — **closed 2026-05-06** (vulnerable transitives).
  - B-120..B-123 (S20 deferrals — NCR / Daily Diary photo
    attachments, NCR workflow extensions, NCR assignee UI).
  - B-124..B-126 (S21 deferrals — training video production,
    per-tenant docs customisation, in-app contextual help).
  - All other v1.1 backlog entries — see `docs/v1.1-backlog.md`.

---

## Working rules (PAFM-SD)

- Ch 27 git: conventional commits, short-lived feature branches off
  master between sprints, sprint branches during sprints.
- Ch 9.3 commit cadence: at least once per day; at task completion;
  before ending a session.
- Ch 17 change control: scope additions require a written change
  record in `docs/change-register.md`. v1.1 backlog promotions /
  closures count as scope decisions and are documented inline in the
  backlog entries.
- Ch 24 architecture: no new libraries / patterns without ADR. ADR
  amendments (§3 of ADR-0014) record changes to a shipped decision
  via an Amendment log on the ADR itself.
- Ch 4 C-11: at least one full day off per week.

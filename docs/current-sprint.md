# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Status:** Between sprints — S17 closed 2026-05-05; **S18 not yet
kicked off** (scope decision pending).

**Active sprint:** none.

**Next sprint (S18):** **F.17 — Audit & Compliance Support** per
PAFM Appendix F.17 (read end-to-end on 2026-05-05; reconciliation
at `docs/scope-reconciliation-2026-05-05.md`). Three bullets:
Evidence library (organised artefact collection per compliance
area), External Auditor role (time-limited, scope-limited read
access), Audit export (PDF or ZIP bundle). **Scope-spec caveat
pattern is no longer needed — the .docx is now read.**

**Important — F-section / sprint-number offset:** PAFM maps
**`F.N = S(N-1)`** end-to-end (drift originated at our S8 = "CI
Hardening", which is not in the PAFM roadmap; another off-roadmap
insertion at S15 = Search & Discovery). Our S16 / S17 kickoffs
labelled themselves F.16 / F.17 which was wrong — they shipped
PAFM F.15 (Admin Console) and F.16 (Mobile Views) respectively.

**Confirmed v1.0 sprint roadmap (post-reconciliation, 2026-05-05):**

| Sprint | Scope | Source |
|---|---|---|
| **S18 (next)** | Audit & Compliance Support | PAFM F.17 |
| S19 | Security & Performance | PAFM F.18 |
| **S20 (NEW)** | Site-User Mobile Workflows (Daily Diary + NCR raising) | B-107 / B-108 (promoted from v1.1 to v1.0 per 2026-05-05 product-owner call) |
| S21 | Documentation & Training | PAFM F.19 |
| S22 | UAT & Release | PAFM F.20 — v1.0 ship gate |

S20 inserts a third off-roadmap sprint to close two real PAFM F.16
product gaps that the original Mobile Views sprint (our S17, scoped
as a responsive sweep) couldn't address — Daily Diary and NCR-raising
flows don't yet exist as v1.0 product features. Slotting between
F.18 Security and F.19 Documentation rather than before F.17 trades
"Evidence library covers them" against "minimal disruption to
already-pinned S18".

**Last sprint:** S17 — Mobile / Responsive Views (PAFM-SD F.17,
predicted). Squash-merged at `bbdae7c` (2026-05-05, PR #62). See
`docs/sprint-log/s17.md` and `docs/retrospectives/s17.md`. 4 of 5
predicted F.17 module-DoD bullets delivered at v1.0 (responsive
sidebar drawer + per-page table column gating + dialog FullWidth
fix; the fifth "no desktop regression" is structurally satisfied
but visually verified by the user post-merge). Eighteenth
consecutive single-session sprint, fourteenth consecutive sprint
without a CR. UI-only sprint; no service / contract changes; test
count unchanged at 957.

**Previously closed:**
- S16 — Admin Console (PAFM-SD F.16, predicted). Squash-merged
  at `dd62441` (2026-05-04, PR #60). 4 of 4 predicted F.16
  module-DoD bullets delivered. CI caught one latent
  existence-leak bug in `OrganisationAdminService.UpdateAsync`
  (cross-tenant attempt returned 403 vs 404, confirming row
  existence) — fixed pre-merge.
- S15 — Search & Discovery (PAFM-SD F.15). Merged at `190fa87`
  (2026-05-03). 3 of 4 predicted F.15 module-DoD bullets
  delivered; saved-searches explicitly deferred to v1.1 / B-096.

**Branch:** `master` (live work between sprints lands on short-lived
feature branches off master, not a long-running sprint branch;
direct pushes to master are blocked — every change goes through PR).

**Tests:** 957 / 957 green (CI). Build clean (0 warnings, 0 errors).
Backlog at B-001..B-104.

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
- **S17 — Mobile / Responsive Views** (PAFM F.17, predicted). UI
  sweep: `MainLayout` MudDrawer flipped from Mini to Responsive
  (mobile gets slide-in temporary drawer + hamburger; desktop
  unchanged); six MudTable pages get per-column responsive hiding
  via `d-none d-{breakpoint}-table-cell` utility classes;
  AdminOrganisations Edit dialog FullWidth fix. No service /
  contract changes; test count unchanged at 957. Backlog
  B-102..B-104. (No version tag landed.)

Detail: `docs/sprint-log/s<N>.md` + `docs/retrospectives/s<N>.md`
per sprint. Git: `git log --oneline c0cfd53..bbdae7c`.

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
- **Laptop WDAC** blocks rebuilt `CimsApp.dll` from `dotnet test`
  discovery (error `0x800711C7`, kernel-level, hash-based). First
  hit S16. Resolution options at the policy layer (WDAC rule for
  `C:\Code\CimsApp\**\*.dll`, Smart App Control toggle, signed
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
  - B-105..B-108 (PAFM F.15 / F.16 reconciliation gaps —
    template library, subscription view, Daily Diary, NCR raising).
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

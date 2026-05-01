# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Status:** Between sprints — S2 closed (PR awaiting merge); S3 not
yet kicked off.

**Last sprint:** S2 — Risk & Opportunities. Branch
`sprint/s2-risk-management` (PR awaiting merge); tag
`v0.2-sprint-s2`. See `docs/sprint-log/s2.md` and
`docs/retrospectives/s2.md`. 8 of 9 PAFM Appendix F.3 module-DoD
bullets delivered; the 1 unticked is B-029 (opportunity register,
deferred per CR-004).

CR-004 deferrals to v1.1 backlog: B-028 (schedule-side Monte
Carlo — also blocked on S4 CPM data), B-029 (opportunity
register), B-030 (cross-module contingency drawdown link).

**Previously closed:** S1 — Cost & Commercial. Merged at `330a09c`
(2026-04-27). Tag `v0.1-sprint-s1`. See
`docs/sprint-log/s1.md` and `docs/retrospectives/s1.md`. 8 of
10 PAFM Appendix F.2 module-DoD bullets delivered; the 2
unticked are the CR-003 deferrals (B-014 Construction Act
notices, B-015 final account schedule).

**Next sprint:** S3 — Stakeholder & Communications. PAFM Appendix
F.4. Estimated 60h over 3 weeks. Stakeholder register, power /
interest matrix, engagement plan + log, communications matrix.
Not yet kicked off.

**Branch:** `master` (live work between sprints lands on
short-lived feature branches off master, not a long-running
sprint branch).

**Active work:** post-S1 hardening passes. Each lands as a
small focused PR off master with full test coverage and the
relevant docs (role-matrix, ADRs, v1.1 backlog) updated in
the same commit. No active sprint scope at the moment.

**Hardening retired since S1 close (2026-04-27 → 2026-04-29):**
- B-001 Access-token revocation (primitive + ADR-0014 +
  endpoints; full close).
- B-002 Rate limit + progressive back-off (full close;
  CAPTCHA / email-verification stay v1.1).
- B-003 Constant-time register (obsolete via T-S0-11).
- B-005 / B-006 Action / RFI ownership checks.
- B-007 `/organisations` admin scoping (real org-enumeration
  leak found in role-matrix audit).
- B-013 MudBlazor 6 → 9 upgrade.
- B-017 Per-CBS-line schedule + progress + EVM/valuation/cashflow
  wire-ups (full close).
- B-019 Refresh-token bulk-revoke (closed an ADR-0014 §3
  reasoning gap).
- B-021 Auth-domain structured audit events.
- B-023 (promoted from SR-S0-05) AddMember org-match check.
- SR-S0-03 RoleClaimType comment fix.
- ADR-0007 SuperAdmin filter-bypass written (was being cited
  but didn't exist).
- README.md filled in (was empty since first commit).
- Sprint log ADR-001 → ADR-0008 annotation fix.
- Pagination types removed (`PaginationParams` /
  `PagedResult<T>` were dead code).
- CdeService / CdeStateMachine / DocumentNaming /
  AuditInterceptor pure-function and behavioural test gaps
  closed.
- `Document.DocumentNumber` unique index scope fix
  (global → per-project — real cross-tenant correctness
  bug).
- AuditInterceptor `SkippedFieldNames` (PasswordHash and
  TokenHash now never appear in audit JSON — defense-in-depth).
- `project.member_added` and `invitation.created` /
  `invitation.consumed` structured audit events
  (audit-twin pattern extended).
- RegisterAsync transaction wrap (User insert + invitation
  consume now atomic — closes a "one-invitation-two-users"
  crash window; PR #29).
- Variation-omission test: payment-cert valuation correctly
  nets a negative EstimatedCostImpact against approved
  additions (PR #32 — pinned a documented-but-untested
  contract).
- **Audit-twin atomicity refactor (PR #33).** Every
  business mutation used to produce two transactions
  (entity save + audit save). `AuditService.WriteAsync` now
  adds the AuditLog row to the change tracker without
  saving; 28 call sites flipped so a single SaveChanges
  commits both halves of the audit-twin atomically. Closes
  the "structured event lost on crash between two saves"
  discoverability gap.
- Document.TransitionAsync transaction wrap (PR #34) —
  same shape as the RegisterAsync wrap, covers the
  ExecuteUpdateAsync (revision publish) + SaveChanges (doc
  state) pair.
- OrganisationsController.Create transaction wrap (PR #35)
  — same shape, covers the org save + bootstrap-invitation
  save pair. Closes a "Code reserved but no bootstrap
  invitation, retry impossible" stuck-state.
- Audit-twin coverage tests (PR #36) for
  payment_certificate.draft_updated /
  payment_certificate.issued / document.state_transition.
- Dormant entity scaffolds documented as B-025 (Notification
  feature) and B-026 (ProjectAppointment / B2B contractor
  membership) — surfaced during dead-code sweep, kept in
  schema rather than deleted to avoid migration churn when
  features land (PR #38).
- Audit-twin coverage completion (PR #39) — six more action
  names tested: project.created, document.created,
  rfi.created, rfi.responded, action.created,
  action.updated. Every structured audit-twin event now
  pinned by at least one explicit assertion.
- **AuditLog.UserId nullable (PR #41) — real, latent
  production bug caught by SQL Server smoke test.** The
  bootstrap-invitation path (POST /api/v1/organisations,
  anon flow) wrote Guid.Empty to AuditLog.UserId as a "no
  actor" sentinel; SQL Server rejected the INSERT with FK
  violation. Broken in production since T-S0-11 (~one week).
  EF in-memory ignores FKs, which is why every unit test
  passed. Column is now `Guid?`, the audit log records null
  honestly, the query filter handles `User == null`. New
  migration `AuditLogUserIdNullable`. Promoted **B-027**:
  add SQL Server smoke test to CI to catch this class of
  bug pre-merge.
- **AuthController body binding + 3 secret leaks (PR #42) —
  two more real, latent production bugs from continuing the
  smoke test.** (a) AuthController extends ControllerBase
  directly to bypass [Authorize] but lost [ApiController]
  in the process; without it the model binder doesn't infer
  [FromBody] and every auth endpoint received an empty DTO,
  so register / login / refresh were all silently broken
  100% of the time in production. (b) Project responses
  serialised Members[].User including User.PasswordHash;
  any authenticated member could read every other member's
  bcrypt hash. Fix: [ApiController] on AuthController +
  [JsonIgnore] on User.PasswordHash, RefreshToken.Token,
  Invitation.TokenHash. Three new EntitySerializationTests
  pin the contract.
- **RefreshAsync opaque-token validation (PR #43) — third
  real, latent production bug from the smoke test.**
  CreateRefreshAsync mints opaque hex (Guid×2 = 64 chars),
  but RefreshAsync was JWT-validating tokens via
  `Validate(token, RefreshSecret)`. Opaque hex is not a JWT,
  so /auth/refresh threw 401 INVALID_REFRESH on every call
  since the initial commit. 100% latent because no unit
  test exercised RefreshAsync. Fix: drop the JWT validate;
  use the DB lookup as the authentication (rows rotate on
  every refresh); pull stored.UserId for the user lookup.
  Five new RefreshTokenAuthTests cover happy path + unknown
  / revoked / expired / null-empty edges.

- **AuthController [AllowAnonymous] scope (PR #44) — fourth
  smoke-test bug.** Class-level [AllowAnonymous] overrides
  every action-level [Authorize] (per ASP.NET Core docs), so
  /me and /logout-everywhere were silently anonymous-allowed.
  Calling /me without auth (or with a post-revoke access
  token) threw ArgumentNullException at
  Guid.Parse(NameIdentifier!) → HTTP 500 instead of 401.
  Fix: scope [AllowAnonymous] per-action (register / login /
  refresh / logout); Me + LogoutEverywhere keep their
  [Authorize] and now auth middleware short-circuits with
  401 before the action runs.

**Four consecutive smoke-test PRs (#41-#44) found real,
latent production bugs. The full bootstrap → register →
login → project → downstream-domain smoke walk is now green
end-to-end against real SQL Server. Cross-tenant isolation
verified live (read + write both correctly blocked).
B-027 (SQL Server smoke test in CI) is concretely justified
four times over — the unit-test-only gap is real.**

**Post-S1 audits landed (all clean / dormant findings only
beyond the items above):**
- `docs/security/post-s1-auth-mutation-audit-2026-04-28.md`
  — User mutation surface; the only present site
  (DeactivateUserAsync) is correct. Two follow-on findings
  delivered (PasswordHash leak, RegisterAsync transaction
  wrap). Two refinements promoted (B-021 closed, B-022 open).
- `docs/security/post-s1-role-matrix-audit-2026-04-28.md`
  — Every role-matrix row verified. RM-01 (logout missing
  rate limit) closed in same commit.
- `docs/security/post-s1-secondary-mutation-audit-2026-04-29.md`
  — Organisation / Project / ProjectMember mutation surfaces.
  All gaps dormant pending future endpoints; one refinement
  (B-024 optimistic concurrency) promoted to backlog.

**Inherited from S0 / S1 (must NOT regress):**
- `ITenantContext` + global query filters on every
  tenant-scoped entity (ADR-0003).
- Two-tier role authorization model (ADR-0010).
- Audit interceptor populates Action / Entity / Before /
  After / UserId on every tenant-scoped write (audit-twin
  pattern from S1 cost domain extended to auth domain at
  B-021).
- Invitation-token registration flow (ADR-0011) — every new
  User comes through a token.
- Project AppointingPartyId locked to caller's org with
  SuperAdmin bypass (ADR-0012).
- NEC4 cumulative semantics for payment certificates
  (ADR-0013).
- Access-token residual-authority SLA: cutoff bump + refresh
  sweep on revoke / deactivate (ADR-0014, §3 amended for
  refresh tokens).

**Out of scope between sprints:**
- ISO 19650 / MIDP — Sprint 8, parked per ADR-0008 / CR-001.
- Risk module (S2), Stakeholder (S3), Schedule (S4), etc.
  Open in their own sprint kickoffs.
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

**Working rules (PAFM-SD):**
- Ch 27 git: conventional commits, short-lived feature
  branches off master between sprints, sprint branches during
  sprints.
- Ch 9.3 commit cadence: at least once per day; at task
  completion; before ending a session.
- Ch 17 change control: scope additions require a written
  change record in `docs/change-register.md`. v1.1 backlog
  promotions / closures count as scope decisions and are
  documented inline in the backlog entries.
- Ch 24 architecture: no new libraries / patterns without
  ADR. ADR amendments (§3 of ADR-0014) record changes to a
  shipped decision via an Amendment log on the ADR itself.
- Ch 4 C-11: at least one full day off per week.

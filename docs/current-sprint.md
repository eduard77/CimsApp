# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Status:** Between sprints — S1 closed, S2 not yet kicked off.

**Last sprint:** S1 — Cost & Commercial. Merged at `330a09c`
(2026-04-27). Tag `v0.1-sprint-s1`. See
`docs/sprint-log/s1.md` and `docs/retrospectives/s1.md`. 8 of
10 PAFM Appendix F.2 module-DoD bullets delivered; the 2
unticked are the CR-003 deferrals (B-014 Construction Act
notices, B-015 final account schedule).

**Next sprint:** S2 — Risk Management. PAFM-SD Ch 6.4 / Appendix
F.3. PMBOK 5 Risk knowledge area. Not yet kicked off — kickoff
needs PAFM-SD `.docx` Appendix F.3 paste, F.3 module-DoD
backfill, capacity check, and a new `docs/sprint-log/s2.md`.

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

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

**Hardening retired since S1 close (2026-04-27 → 2026-04-28):**
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

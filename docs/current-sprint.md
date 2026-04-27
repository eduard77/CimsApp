# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Sprint:** S1 — Cost & Commercial
**Dates:** 2026-04-26 → 2026-05-17 (3 weeks)
**Module:** Cost & Commercial (PMBOK 8 Finance domain)
**Module DoD:** PAFM Appendix F.2 — see `docs/sprint-log/s1.md`
            (F.2 paste pending — DoD and task table are scaffold).

**Branch:** `sprint/s1-cost-commercial` (not `master`)
**Roadmap reference:** PAFM-SD Ch 6.3 Phase 2

**In scope this sprint:**
- Cost Breakdown Structure (CBS) per project.
- Earned Value Management (CV / SV / CPI / SPI).
- Variations / change orders with workflow.
- Payment certificates (NEC4 / JCT alignment — choose one in ADR).
- Cashflow forecast (S-curve from baseline + actuals).
- MudBlazor 7 upgrade (small carry-over from S0 retrospective).

**Out of scope this sprint (deferred — not forbidden):**
- ISO 19650 / MIDP — Sprint 8, parked per ADR-0008 / CR-001.
- Risk module (S2), Stakeholder (S3), Schedule (S4), etc.
- Everything in PAFM-SD Ch 3 anti-scope.

**Inherited from S0 (must NOT regress):**
- ITenantContext + global query filters on every tenant-scoped
  entity (ADR-0003).
- Two-tier role authorization model (ADR-0010).
- Audit interceptor populates Action / Entity / Before / After /
  UserId on every tenant-scoped write.
- Invitation-token registration flow (ADR-0011) — every new User
  comes through a token.
- Project AppointingPartyId locked to caller's org with
  SuperAdmin bypass (ADR-0012).

**Working rules (PAFM-SD):**
- Ch 27 git: conventional commits, sprint branches, merge (not
  force) at sprint close, tag `v0.1-sprint-s1`.
- Ch 9.3 commit cadence: at least once per day; at task completion;
  before ending a session.
- Ch 10 DoD: module-level + sprint-level; fuzziness test applies.
- Ch 17 change control: scope additions require a written change
  record in `docs/change-register.md`.
- Ch 24 architecture: no new libraries / patterns without ADR.
- Ch 4 C-11: at least one full day off per week.

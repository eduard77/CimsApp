# Current sprint context

This file is the AI-brief pointer: paste the block below into any
non-trivial AI session so the model starts with the right scope anchor.

---

**Sprint:** S0 — Multi-tenant Foundation
**Dates:** 2026-04-24 → 2026-05-15 (3 weeks)
**Module:** Multi-tenant foundation
**Module DoD:** PAFM Appendix F.1 — see `docs/sprint-log/s0.md`

**Branch:** `sprint/s0-multi-tenant` (not `main`)
**Roadmap reference:** PAFM-SD Ch 6.3 Phase 1

**In scope this sprint:**
- `ITenantContext` service.
- `OrganisationId` global query filter on tenant-scoped entities.
- Audit trail before/after capture via EF interceptor.
- SuperAdmin cross-tenant bypass.
- Role enforcement audit across controllers.
- CI pipeline + branch protection on `main`.

**Out of scope this sprint (deferred — not forbidden):**
- PMBOK modules (Sprints 1-7).
- ISO 19650 validator extension — **parked until Sprint 8** per
  PAFM-SD Ch 0.4. Sessions 1-2 already shipped a filename validator;
  do not touch `CimsApp/Services/Iso19650/` in S0.
- Everything in PAFM-SD Ch 3 anti-scope (no new frameworks,
  no mobile app, no microservices, etc.).

**Working rules (PAFM-SD):**
- Ch 27 git: conventional commits, sprint branches, merge (not force)
  at sprint close, tag `v0.0-sprint-s0`.
- Ch 9.3 commit cadence: at least once per day; at task completion;
  before ending a session.
- Ch 10 DoD: module-level + sprint-level; fuzziness test applies.
- Ch 17 change control: scope additions require a written change record.
- Ch 24 architecture: no new libraries / patterns without ADR.

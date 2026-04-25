# Change register

Per PAFM-SD Ch 17.3. Every approved scope change, deferral, or
clarification that materially affects the roadmap lands here.

| Change ID | Date raised | Description | Category | Impact summary | Decision | Date decided | Implementation sprint |
|---|---|---|---|---|---|---|---|
| CR-001 | 2026-04-24 | Sessions 1–2 shipped an ISO 19650 filename validator (`CimsApp/Services/Iso19650/`) that belongs to Sprint 8 module (PAFM Appendix F.9). Work was out of roadmap sequence. | brought-forward (retrospective) | Scope: 12 filename-check stubs on `master`. Time: zero lost (work is preserved), but S8 estimate now reduced. Risk: divergent reference data between `Services/Iso19650/Iso19650ReferenceData.cs` and `Core/Iso19650Codes.cs` noted in Session 3 — must be reconciled at S8 kickoff. | approved — **park in place** until Sprint 8 (no extension, no integration, no revert). | 2026-04-24 | S8 |
| CR-002 | 2026-04-24 | Security review SR-S0-01 (Critical) and SR-S0-02 (High) identified two cross-tenant bypasses (anonymous registration accepts attacker-supplied OrganisationId; project creation accepts attacker-supplied AppointingPartyId). Both undermine the Sprint 0 tenant-isolation DoD. | clarification (hardening within existing F.1 DoD) | Scope: add Invitation-token registration flow (new entity, endpoint, migration) closing SR-S0-01; add caller's-org / SuperAdmin-bypass check on project creation closing SR-S0-02. Time: ~4h estimated, folds into Sprint 0 remaining capacity. Risk: registration flow is user-facing — bootstrap flow must be solved (first OrgAdmin chicken-and-egg) without creating a different anonymous bypass. **Breaking change to `RegisterRequest` DTO: `OrganisationId` removed, `InvitationToken` added** — only impacts pre-existing client code; the dev deployment had a single registered user, no automated client. | approved — landed within S0. SR-S0-02 closed 2026-04-24 (`c83a8a9`, ADR-0012). SR-S0-01 closed 2026-04-25 (`9b40a8d`, `b528897`, `634705a`, `3f95241`, `3839468`, ADR-0011). | 2026-04-25 | S0 |

## Entry template (PAFM-SD Ch 17.6)

```
CR-XXX — [Short title]
Date raised: YYYY-MM-DD
Raised by: Developer-self
Description: [What changes]
Rationale: [Why — tied to a v1.0 success criterion if possible]
Impact — scope: [what is added/removed]
Impact — time: [sprints affected, estimate]
Impact — risk: [new risks introduced or avoided]
Alternatives considered: [what else was considered]
Decision: [Approved / Rejected / Deferred]
Date decided: YYYY-MM-DD
Decided by: Sponsor-self
Implementation plan: [sprint, tasks, DoD updates]
```

## CR-001 — Park Sessions 1–2 ISO 19650 validator until Sprint 8

**Date raised:** 2026-04-24
**Raised by:** Developer-self

**Description.** Sessions 1 and 2 (pre-Sprint-0, on `master`) shipped
an ISO 19650 filename validator under `CimsApp/Services/Iso19650/`
with 12 check stubs and accompanying reference data. The work is
functional, tested, and green in CI, but it belongs to the Sprint 8
module (ISO 19650 / MIDP) per PAFM Appendix F.9, not to Sprint 0's
multi-tenant foundation. The work was done without a Chapter 17
change record at the time.

**Rationale.** Tied to success criteria #1 and #2 (CIMS v1.0 must
support daily use on a real project). The validator is a required
capability for Sprint 8. Discarding working code to restore formal
sequence would contradict PAFM-SD Ch 0 ("refactor-and-harden, do not
rewrite"). Keeping the code while freezing further extension
preserves the effort without letting it widen Sprint 0.

**Impact — scope.** No change to Sprint 0 scope. Sprint 8 scope
gains a pre-existing codebase to reconcile rather than to build from
scratch. Reference-data divergence between
`Services/Iso19650/Iso19650ReferenceData.cs` and
`Core/Iso19650Codes.cs` must be resolved at S8 kickoff.

**Impact — time.** S0: none — the work is parked, not extended.
S8: estimate reduced (exact adjustment determined at S8 planning);
add a reconciliation task for the duplicate reference data.

**Impact — risk.** New risks: (1) code drift between `master` and
the eventual S8 branch if the surrounding domain evolves before S8
starts; (2) forgetting the park decision and accidentally extending
the validator during S1–S7. Mitigations: (a) this change record; (b)
`project_roadmap_position.md` memory flags the park; (c) ADR-0008
records the park architecturally; (d) sprint kickoff checklist to
check the change register.

Risks avoided: losing working validator code; rewriting it from
scratch at S8 against a contaminated mental model of the PAFM
Appendix F.9 spec.

**Alternatives considered.**
- *Revert the Sessions 1–2 commits.* Rejected — destroys working
  code; contradicts PAFM-SD Ch 0.
- *Extend the validator through S0–S7 as "it's already there".*
  Rejected — that is exactly the drift the Chapter 17 process is
  meant to prevent. `project_roadmap_position.md` and ADR-0008
  codify the "no extension until S8" rule.
- *Move the code to a `/staging` directory to match the
  standalone-zip pattern in PAFM-SD Ch 0.2.* Rejected — the code
  builds against the main `CimsApp` assembly and is referenced by
  tests; moving it risks breakage for no discipline gain.

**Decision.** Approved — park in place until Sprint 8. Treated as
a brought-forward change with formal approval recorded retroactively
and no waiver required because the work is not extended.

**Date decided:** 2026-04-24
**Decided by:** Sponsor-self

**Implementation plan.**
- S0–S7: no touches to `CimsApp/Services/Iso19650/` or
  `CimsApp.Tests/Iso19650/` except critical-bug fixes with a
  separate change record. Enforced by the `project_roadmap_position.md`
  memory and by ADR-0008.
- S8 kickoff: reconciliation task added to the S8 sprint log —
  unify `Services/Iso19650/Iso19650ReferenceData.cs` with
  `Core/Iso19650Codes.cs`; integrate the 12 check stubs with the
  MIDP / TIDP / CDE naming wizard flow per PAFM Appendix F.9.

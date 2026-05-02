# Change register

Per PAFM-SD Ch 17.3. Every approved scope change, deferral, or
clarification that materially affects the roadmap lands here.

| Change ID | Date raised | Description | Category | Impact summary | Decision | Date decided | Implementation sprint |
|---|---|---|---|---|---|---|---|
| CR-001 | 2026-04-24 | Sessions 1–2 shipped an ISO 19650 filename validator (`CimsApp/Services/Iso19650/`) that belongs to Sprint 8 module (PAFM Appendix F.9). Work was out of roadmap sequence. | brought-forward (retrospective) | Scope: 12 filename-check stubs on `master`. Time: zero lost (work is preserved), but S8 estimate now reduced. Risk: divergent reference data between `Services/Iso19650/Iso19650ReferenceData.cs` and `Core/Iso19650Codes.cs` noted in Session 3 — must be reconciled at S8 kickoff. | approved — **park in place** until Sprint 8 (no extension, no integration, no revert). | 2026-04-24 | S8 |
| CR-003 | 2026-04-25 | S1 task decomposition against the real PAFM-SD `.docx` Appendix F.2 totals 84h × 1.5 = 126h, against a formal sprint capacity of 72h (6h × 15 working days × 80%). Per PAFM-SD Appendix E sprint-kickoff checklist, scope cut required before T-S1-02 starts. | clarification (capacity-driven scope cut within F.2) | Scope: defer four items from S1. (1) T-S1-10 Construction Act notices → B-014 (S1 fast-follow). (2) T-S1-12 final account schedule → B-015 (v1.0 late or v1.1). (3) T-S1-14 MudBlazor 7 upgrade → B-013 (v1.1). (4) T-S1-08 variations workflow trimmed from 6 states to 3 (raise → approve → reject); intermediate states (assess / instruct / value / agree) → B-016 (v1.1). Time impact: estimate 84h → 64h, total with PAFM-SD ×1.5 = 96h, still over the 72h formal but inside S0-velocity-adjusted reach. Risk: Construction Act notice deferral is the only compliance-shaped cut — see B-014 — and is acceptable for v1.0 internal pilot, not for external customer onboarding. | approved — option A from `docs/sprint-log/s1.md#scope-decision-required`. | 2026-04-25 | S1 (deferred items per backlog entries) |
| CR-004 | 2026-05-01 | S2 Risk & Opportunities task decomposition against PAFM-SD `.docx` Appendix F.3 totals 65h × 1.5 = 98h, against a formal sprint capacity of 72h (6h × 15 working days × 80%). Per PAFM-SD Appendix E sprint-kickoff checklist, scope cut required before T-S2-02 starts. Same shape as CR-003 (S1 capacity cut). | clarification (capacity-driven scope cut within F.3) | Scope: defer three items from S2. (1) T-S2-08 trimmed: cost-side Monte Carlo only in v1.0; schedule-side → B-028 (v1.1, also dependent on S4 CPM data). (2) T-S2-09 trimmed: drawdown amounts only in v1.0; cross-module link to commitments / actuals → B-030 (v1.1). (3) T-S2-11 deferred entirely: opportunity register → B-029 (v1.1, mirror of Risk). Time impact: estimate 65h → 50h, total with PAFM-SD ×1.5 = 75h, only 3h over the 72h formal — well inside S0-velocity-adjusted reach and S1-actual cadence. Risk: B-028 schedule-MC has a real upstream dependency on S4; deferring is honest, not arbitrary. B-030 cross-module link is the only construction-domain cut that affects forensic traceability — drawdown numbers still recorded honestly, just not tied to specific consumers. | approved — option A from `docs/sprint-log/s2.md#scope-decision--resolved-2026-05-01`. | 2026-05-01 | S2 (deferred items per backlog entries) |
| CR-002 | 2026-04-24 | Security review SR-S0-01 (Critical) and SR-S0-02 (High) identified two cross-tenant bypasses (anonymous registration accepts attacker-supplied OrganisationId; project creation accepts attacker-supplied AppointingPartyId). Both undermine the Sprint 0 tenant-isolation DoD. | clarification (hardening within existing F.1 DoD) | Scope: add Invitation-token registration flow (new entity, endpoint, migration) closing SR-S0-01; add caller's-org / SuperAdmin-bypass check on project creation closing SR-S0-02. Time: ~4h estimated, folds into Sprint 0 remaining capacity. Risk: registration flow is user-facing — bootstrap flow must be solved (first OrgAdmin chicken-and-egg) without creating a different anonymous bypass. **Breaking change to `RegisterRequest` DTO: `OrganisationId` removed, `InvitationToken` added** — only impacts pre-existing client code; the dev deployment had a single registered user, no automated client. | approved — landed within S0. SR-S0-02 closed 2026-04-24 (`c83a8a9`, ADR-0012). SR-S0-01 closed 2026-04-25 (`9b40a8d`, `b528897`, `634705a`, `3f95241`, `3839468`, ADR-0011). | 2026-04-25 | S0 |
| CR-005 | 2026-05-01 | S4 Schedule & Programme task decomposition against PAFM-SD `.docx` Appendix F.5 totals 63h × 1.5 = 94.5h, against a formal sprint capacity of 72h (6h × 15 working days × 80%). Per PAFM-SD Appendix E sprint-kickoff checklist, scope cut required before T-S4-02 starts. Same shape as CR-003 (S1) and CR-004 (S2). | clarification (capacity-driven scope cut within F.5) | Scope: defer three items from S4. (1) T-S4-10 MS Project XML export → B-031 (v1.1; import is the dominant v1.0 use case, export round-trips back to MSP which most v1.0 users won't need on day 1). (2) T-S4-08 Takt planning → B-032 (v1.1; specialised lean-construction optimisation for repetitive-work projects, not used on typical UK mid-rise / commercial / residential / fit-out projects). (3) T-S4-11 trimmed: Gantt only in v1.0; network-view → B-033 (v1.1; Gantt is the dominant UK schedule visualisation, network diagrams are a PMBOK-textbook pedagogical tool). Time impact: estimate 63h → ~46h, total with PAFM-SD ×1.5 = ~68h, within the 72h formal capacity for the first time since S0. Risk: B-031 export-vs-import asymmetry is the only interop-shaped cut — users with existing MSP workflows will need to re-export manually until v1.1. B-032 Takt is a real feature loss for high-rise / infrastructure projects, so v1.0 pilot project type matters (Genera Systems pilot is mid-rise commercial — not affected). | approved — option A from `docs/sprint-log/s4.md#cr-005-—-proposed-deferrals`. | 2026-05-01 | S4 (deferred items per backlog entries B-031 / B-032 / B-033) |

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

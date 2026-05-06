# Kaizen / Lessons Learned

S12 module — continuous-improvement record-keeping with
Plan-Do-Check-Act cycle, cross-project lessons library, and
opportunity-to-improve cross-references.

## Improvement Register

Per-project `ImprovementRegisterEntry` — PDCA-cycle workflow:
**Plan → Do → Check → Act → Closed**. Act transitions to either
Plan (next iteration / cycle-back) or Closed (terminal).
State-machine in `Core/PdcaWorkflow.cs`.

Per-entry: Title, Description, Hypothesis, ExpectedOutcome,
ActualOutcome (populated at Check), DecisionOnAct.

## Lessons Library

`LessonLearned` is **tenant-scoped** (not project-scoped) — the
library accumulates across the organisation's projects so future
projects can search past experience. Per-row: Title, Context,
Recommendation, Tags. Full-text search over the lessons library
is v1.1 / B-082.

## Opportunity to Improve

`OpportunityToImprove` cross-references opportunities surfaced
from any module — Risk, RFI, Inspection finding, NCR. The link
is captured via `SourceEntityId` (JSON metadata pointer) without
per-domain join tables. Polymorphic FK is v1.1 / B-083 once a
real workflow demands navigability.

## Common gotchas

- **PDCA cycle history per Improvement** is v1.1 / B-081 —
  v1.0 records the most recent cycle only.
- **Improvement-register dashboard / KPI integration** is
  v1.1 / B-084.
- **Lessons library template seed** is v1.1 / B-085 — v1.0
  starts empty; pilot fills the library by recording lessons
  as they surface.

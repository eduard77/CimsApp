# Quality / Inspection Activities

S13 module (PAFM F.13 Option A scope cut) — per-project
inspection workflow capturing scheduled / in-progress /
completed inspections with optional outcome notes.

## Inspection activities

`InspectionActivity` workflow: **Scheduled → InProgress →
(Completed | Cancelled)**. Cancelled also reachable from
Scheduled directly when an inspection is dropped before it
starts. State-machine in `Core/InspectionActivityWorkflow.cs`.

Per-project sequential `INSP-NNNN` numbering. Each inspection
carries:

- Title, Description, Location
- ScheduledDate, ActualStartDate, ActualEndDate
- ResponsibleParty (free-text — typically a subcontractor name
  or "Site Supervisor")
- Outcome notes (populated at Completion)

## Role gates

- **Scheduling / starting / completing**: any project member.
- **Cancelling**: PM-and-up (formal decision).

## Genera Systems integration

PAFM F.13 originally specified Genera Systems QA / HSE bidirectional
integration (REST API + webhooks + identity sync). v1.0 ships the
inspection-activity entity scoped as "Option A" — manual entry
flow. The integration plumbing carries forward as v1.1
B-086..B-089:

- B-086 Genera Systems REST API endpoints
- B-087 Webhook subscription for QA / HSE events
- B-088 Identity sync / shared SSO with Genera
- B-089 Bidirectional sync of inspection activities to CIMS
  quality records

These will land alongside the **PAFM Ch 47 paste** (the
canonical Genera Systems QA, HS&E integration specification)
which is the standing-request item from the S13 retro.

## Common gotchas

- **Inspections are distinct from NCRs** (S20) — an inspection
  observes; an NCR records a non-conformance. A failed
  inspection might lead to an NCR being raised, but they're
  separate entities with separate workflows.

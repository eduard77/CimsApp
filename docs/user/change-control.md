# Change Control

S5 module — formal change-request workflow with construction-site
categorisation and BSA HRB tagging.

## Change requests

5-state workflow: **Raised → Assessed → (Approved | Rejected) →
Implemented → Closed**. Reject is allowed from Raised or Assessed
only; once Approved the only forward path is Implemented →
Closed. State-machine enforcement in `Core/ChangeWorkflow.cs`.

Per-project sequential Number (`CR-NNNN`). Each CR carries:

- Title, Description, Raised-by user
- Category: Scope / Time / Cost / Quality
- BSA HRB category tag: NotApplicable / A / B / C
  (per Building Safety Regulator guidance — non-HRB projects
  default to NotApplicable; configurable per-tenant inference
  rules in v1.1 / B-072).
- AssessmentNotes / ApprovalNotes / RejectionNotes / ImplementationNotes
  populated as the CR moves through states.

## Role gates

- **Raise**: any project member.
- **Assess / Approve / Reject**: PM-and-up.
- **Implement / Close**: any project member who's the assignee
  or PM-and-up.

## Common gotchas

- The CR module is **distinct from** Variations (S1 cost domain)
  and from Compensation Events (S6 procurement / NEC4). A scope
  change might cascade — "site change → CR Raised → ...
  Approved → Variation Raised → ... Approved → Payment Cert
  Issued" — but each entity tracks its own lifecycle.
- **Per-tenant configurable HRB inference** is v1.1 / B-072.

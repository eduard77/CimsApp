# RFIs and Actions

Two project-scoped work-tracking surfaces sharing similar shapes.

## RFIs (`/rfis`)

Request-For-Information lifecycle: **Draft → Open → UnderReview →
Responded → Closed** (plus Cancelled). Per-project sequential
`RFI-NNNN` numbering. Raise from any project member; respond
typically PM or IM-level.

- `Subject` is the headline; `Description` is the full prompt.
- `RaisedById` populated from current user; `RespondedById` set
  when the response is recorded.
- Audit-twin events: `rfi.created`, `rfi.responded`.

## Actions (`/actions`)

General-purpose action tracker: **Open → InProgress → Closed**
(plus Cancelled). Per-project actions for tasks that don't fit
RFI / NCR / change-request shapes — meeting follow-ups, snags,
admin todos. Filter by status + "overdue" flag (DueDate in the
past + not Closed).

- Audit-twin events: `action.created`, `action.updated`.
- Ownership check (B-005 / B-006): only the action's
  `CreatedById` or `AssignedToId` can mutate; PM+ can override.

## Common gotchas

- The RFI **respond** flow is a one-shot — once responded the RFI
  cannot be re-edited; raise a follow-up RFI if needed.
- Action **due dates** are stored as UTC `DateTime`; the UI
  renders in local time. The "overdue" badge fires when
  `DueDate < UtcNow AND Status != Closed`.

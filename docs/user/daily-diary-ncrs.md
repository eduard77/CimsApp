# Daily Diary and NCRs

S20 module — site-user mobile workflows. Two distinct surfaces.

## Daily Diary (`/diary`)

Per-project, per-day, per-user diary entries. Multi-author per
day permitted (e.g. site supervisor + safety officer + foreman
each capture their own day). Service enforces (ProjectId,
DiaryDate, AuthorId) uniqueness.

Six free-text fields: Weather, Manpower, Plant, Deliveries,
Incidents, ProgressNotes. All optional individually; the entry
must have at least one of them populated to be useful.

`DiaryDate` is `DateOnly` (timezone-safe — service accepts UTC
date only, UI converts to project-local at render).

Audit-twin events: `dailydiary.created`, `dailydiary.updated`.
Update window: free at v1.0 (no time-bound; audit captures
every edit).

## NCRs (`/ncrs`)

Non-Conformance Report raising flow — distinct from the
`DocumentType.Ncr` enum (which is just a typing tag on
Document). 5-state linear workflow:

**Raised → Assigned → InProgress → Resolved → Closed**

Plus `Cancelled` from Raised / Assigned only — once work is
In-Progress the issue is "real" and proceeds to Resolved or
Closed.

Per-project sequential `NCR-NNNN` numbering. Each NCR carries:

- Title, Description, Severity (Low / Medium / High / Critical)
- Location (free-text)
- RaisedById, AssignedToId
- ResolutionNotes (populated at Resolved)
- PhotoStorageKey — placeholder for an external blob reference
  (v1.0 doesn't ship inline upload; v1.1 / B-120)

State-machine + role gates in `Core/NcrWorkflow.cs`:
- Assign: IM-and-up.
- Start work: TaskTeamMember-and-up.
- Mark resolved: TaskTeamMember-and-up.
- Close: IM-and-up.
- Cancel: PM-and-up.

## Common gotchas

- **NCR vs DocumentType.Ncr** — the enum tags an existing
  Document; the entity is the structured raise-to-close flow.
  Different surfaces, different uses.
- **Photo upload deferred** — `PhotoStorageKey` is a string
  placeholder for an external blob URL.
- **Full assign-to-user dialog** is v1.0 fast-follower / B-123
  — currently the assignment goes via the API; the UI shows
  transition buttons only.

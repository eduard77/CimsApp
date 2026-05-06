# Documents (CDE)

ISO 19650-aligned Common Data Environment. Documents flow through
four states: **Work in Progress → Shared → Published → Archived**
(plus Voided as a terminal off-ramp). State transitions are
gated by role + workflow rules in `Core/CdeStateMachine`.

## Pages

- `/documents` — register view: paged table with state filter,
  search, and inline transition buttons.
- `/projects/{id}/templates` — per-project PMBOK template
  library (Provision button creates 28 starter docs across
  PMBOK knowledge areas: Charter, Integration, Scope, Schedule,
  Cost, Quality, Communications, Risk, Procurement, Stakeholder,
  Construction Safety, Environmental, Financial, Claims, BIM
  Deliverables, Commissioning, Closeout/Archive).

## Creating a document

InformationManager-and-up registers. The Document Number is
ISO 19650 compliant — `{Project}-{Originator}-{Volume}-{Level}-{Type}-{Role}-{Number}`.
The Iso19650FilenameValidator enforces the field shapes; see
`docs/user/iso-19650.md` for the wider naming convention.

## State transitions

Each transition has a role gate (per
`CimsApp/Core/Core.cs:CdeStateMachine.TransitionRoles`). Common
patterns:

- **WIP → Shared**: TTM-and-up.
- **Shared → Published**: PM-and-up.
- **Anything → Voided**: PM-and-up; reaching Published requires
  OrgAdmin.

The transition button row on each row only shows transitions
the current user is permitted to execute. Failed transitions
return 403 with a human-readable reason.

## Common gotchas

- **Document Number uniqueness is per-project** (post-S1 fix —
  was global which caused cross-tenant correctness issues).
- **PMBOK template provisioning is one-time per project** —
  re-running is allowed (admin-only `POST /provision`) but
  preserves any edits made to existing files.

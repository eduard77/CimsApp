# Projects

The **Projects** page (`/projects`) lists every project visible to
the current user (own tenant for OrgAdmin / regular users; every
tenant for SuperAdmin). For each row: name, code, location,
budget, sector, current status (Initiation → Planning → Execution
→ Monitoring → Closeout → Completed; plus Suspended / Cancelled).

## Creating a project

OrgAdmin and SuperAdmin can create. The form takes:

- Project name (required)
- Project code (required, uppercased, unique within tenant)
- Location, sector (optional metadata)
- Budget value + currency (default GBP)
- Client organisation — defaults to the caller's tenant; SuperAdmin
  can pick another tenant per ADR-0012's caller's-org-default
  + platform-reading rule.

After create:
- Per-project PMBOK template folder is **NOT** auto-provisioned.
  Visit `/projects/{id}/templates` and click "Provision templates
  now" to copy 28 PMBOK starter docs into per-project storage.
  See `docs/user/documents-cde.md`.
- The creator becomes the project's first Project Manager
  member automatically.

## Members

Project membership grants access to the project-scoped surfaces.
Roles: ProjectManager (PM), InformationManager (IM),
TaskTeamMember (TTM), ClientRep, Viewer. Add members via
`POST /projects/{id}/members`; the AddMember endpoint enforces
that the target user belongs to the project's organisation
(closes B-023 / SR-S0-05).

## HRB metadata (Building Safety Act 2022)

Project-Manager-and-up can set the project's HRB (Higher-Risk
Building) flag + category (A / B / C) on the project detail
page. HRB tagging drives the BSA 2022 Golden Thread surfaces;
see `docs/user/bsa-2022-golden-thread.md`.

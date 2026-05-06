# Audit Trail and Audit & Compliance Support

Two related-but-distinct surfaces:

## Project audit trail

`/audit` — project-scoped audit log. Every business mutation
produces a structured audit row in the same transaction as the
underlying entity write (audit-twin atomicity, PR #33). Per-row:
Time, User, Action (e.g. `rfi.responded`, `payment_certificate.issued`,
`evidence.added`), Entity + EntityId, Before/After JSON snapshot.

Read access: any project member with InformationManager-and-up.

## Tenant-wide audit (Admin Console)

`/admin/audit` — cross-project tenant-wide log. Same data, wider
scope. Filterable by date range, action contains, entity exact
match, userId. OrgAdmin sees only their tenant's rows;
SuperAdmin sees every tenant's via IgnoreQueryFilters.

## Evidence library (S18)

`/evidence` — compliance artefact collection per area. Each row
tagged with `ComplianceArea` (UkGdpr / Bsa2022 / Iso19650 / Nec4
/ QualityHse / Other) and a discriminated FK to one of:

- A Document
- An RFI
- An AuditLog row
- An InspectionActivity

…or all-null with a Description for bare-description evidence
(e.g. an external letter referenced by URL). Service-layer
validates exactly-one-FK or bare-description.

## External Auditor role (S18)

A new `UserRole.ExternalAuditor` role intentionally outside the
mutation-role hierarchy — every `HasMinimumRole(ExternalAuditor,
X)` returns false, so mutation gates auto-reject. Per-project
read access via `AuditorProjectAssignment` rows (UserId,
ProjectId, ScopeAreas, ExpiresAt). Soft-revoke flips IsRevoked +
bumps the user's TokenInvalidationCutoff (kills in-flight
sessions immediately per ADR-0014).

OrgAdmin / SuperAdmin manage assignments at
`/admin/auditor-assignments`.

## Audit export (S18)

`GET /api/v1/projects/{id}/audit-export?from=&to=&areas=` returns
a ZIP bundle:

- `manifest.json` — project + window + counts
- `evidence/<id>.json` per evidence row, with embedded
  source-entity snapshot
- `audit-log.json` — audit rows for the time window

Default window: last 90 days. Read access: any project member
with the standard membership check (ExternalAuditor role passes
on assigned projects).

## Common gotchas

- **Project audit page is project-scoped**; the admin one is
  tenant-wide. Different filters are appropriate.
- **PDF export** is v1.1 / B-110 — v1.0 ships ZIP-of-JSON.
- **Cross-tenant ExternalAuditor scoping** (auditor's User in
  their own auditing-firm tenant) is v1.1 / B-109.
- **Audit viewer optimisation** (materialised projection) kicks
  in at v1.1 / B-100 if pilot scale exposes latency on large
  tenants.

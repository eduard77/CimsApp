# ADR-0012 — Project tenancy semantics: caller's-org default, SuperAdmin bypass

**Status:** Accepted (2026-04-24, Sprint S0, closes SR-S0-02)
**Supersedes:** —

## Context

Security review finding SR-S0-02 (2026-04-24) identified that
`ProjectsService.CreateAsync` wrote `req.AppointingPartyId` verbatim.
The tenant query filter for `Project` is
`p.AppointingPartyId == _tenant.OrganisationId` (see
`CimsApp/Data/CimsDbContext.cs:165`). An attacker with `OrgAdmin`
authority on organisation A could therefore set
`req.AppointingPartyId = orgB.Id` and plant a project visible only
to orgB. Cross-tenant write primitive.

Three models were on the table during the review discussion with the
product owner (Eduard Szigeti, solo dev):

1. **In-house PMO.** `AppointingPartyId` always equals the caller's
   organisation. No multi-org semantics.
2. **B2B multi-tenant.** Another organisation may appoint the caller's
   organisation; `ProjectAppointment` rows pre-authorise
   org-A-appoints-orgB relationships; create-project is permitted only
   when such a row exists.
3. **Platform-level.** Default rule: caller's organisation is forced
   as `AppointingPartyId`. `SuperAdmin` is the single exception, for
   platform-wide administrative actions.

Option 3 was chosen.

## Decision

At project creation, `AppointingPartyId` is determined by the
caller's global role:

- **Non-`SuperAdmin`.** `Project.AppointingPartyId` is set to
  `ITenantContext.OrganisationId`. If `req.AppointingPartyId`
  disagrees with the caller's organisation, the request is rejected
  with `ForbiddenException` so the API contract stays honest — the
  caller sees that the value they sent was not accepted, rather than
  being silently overridden.
- **`SuperAdmin`.** `req.AppointingPartyId` is honoured, on the
  understanding that a SuperAdmin acts platform-level. The audit
  record distinguishes this case with
  `Action = "project.created.superadmin_bypass"` so cross-tenant
  platform actions are trivially filterable in compliance exports.

The check lives in `ProjectsService.CreateAsync`, not in the
controller, because the role-based branch is policy and belongs with
the write it governs. `ITenantContext` is injected into
`ProjectsService` to read `GlobalRole`, `OrganisationId`, and
`IsSuperAdmin`.

## Alternatives considered

**Option 1 (In-house PMO).** Simpler. Rejected because the CIMS data
model already carries `ProjectAppointment` with per-org per-project
roles — the schema already anticipates multi-org projects.
Flattening to single-org would abandon that structure without
justification.

**Option 2 (B2B multi-tenant via `ProjectAppointment`).** More
expressive but larger surface. Rejected for Sprint 0 because there
is no flow today to seed `ProjectAppointment` rows, so the check
would always fall through to the default "caller's-org only" case
anyway. Revisit at the sprint that introduces inter-org appointments
(candidate: S6 Procurement, S12 Genera Systems QA integration).

**Silent override (caller's `OrganisationId` replaces whatever was
sent).** Rejected because it hides the user's error behind a
success response and makes API misuse invisible in logs.

## Consequences

**Positive:**
- Closes SR-S0-02 cross-tenant write primitive.
- Keeps Sprint 0 scope contained; no schema change, no new tables.
- `SuperAdmin` retains a legitimate platform-level capability,
  audited distinctly.

**Negative:**
- In-house PMO setups with multiple affiliated organisations cannot
  currently model "org A appoints org B's PM to run a project" at
  the create-project step. This is acceptable for v1.0; the
  `ProjectAppointment` table can be extended later (future ADR) to
  pre-authorise such pairings and relax the check for non-SuperAdmin
  callers.
- `SuperAdmin` now has a behavioural fork in an ordinary service. If
  the `SuperAdmin` check grows tentacles across many services,
  consider moving the bypass to an `IAuthorizationPolicy` or similar
  abstraction. Revisit if more than three services branch on
  `IsSuperAdmin`.

## Related

- ADR-0003 (row-level multi-tenancy via query filter).
- ADR-0010 (two-tier role authorization model — ADR-0012 is the
  first application of the "global-role bypass" pattern).
- Security review SR-S0-02
  (`docs/security/s0-review-2026-04-24.md#sr-s0-02--project-creation-accepts-attacker-controlled-appointingpartyid-high`).
- CR-002 (change register entry for both SR-S0-01 and SR-S0-02
  fixes).
- `CimsApp/Services/Services.cs#ProjectsService.CreateAsync` — the
  implementation.

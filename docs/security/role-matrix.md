# Endpoint role matrix

**Scope:** every HTTP endpoint in `CimsApp/Controllers/` as of 2026-04-24
(post-T-S0-08).
**Authorization model:** ADR-0010 two-tier (global + per-project).
**Role hierarchy** (low → high):
`Viewer < ClientRep < TaskTeamMember < InformationManager <
ProjectManager < OrgAdmin < SuperAdmin`.

## Legend

- **Global role** gates are `[Authorize(Roles = "...")]` attributes;
  the JWT's `cims:role` claim is read via ASP.NET role mapping.
- **Project role** gates use `HasMinimumRole(await
  GetProjectRoleAsync(db, projectId), UserRole.X)` inside the action
  body. The role is the caller's `ProjectMember.Role` on that specific
  project; any non-member is rejected with `ForbiddenException`
  regardless of global role.
- **Membership only** means `GetProjectRoleAsync` is called (which
  throws if the caller is not a project member) but no minimum role
  is required beyond membership.
- "—" in the Global or Project column means no gate of that tier
  applies at the endpoint level; deeper service-layer checks may still
  exist (noted in Comment).

## Auth

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| POST | `/api/v1/auth/register` | anonymous + invitation token | — | Sign-up. T-S0-11 closed SR-S0-01: caller-supplied `OrganisationId` removed from request, replaced with `InvitationToken`. Tenant is server-derived from the invitation. See ADR-0011. |
| POST | `/api/v1/auth/login` | anonymous | — | Issues JWT |
| POST | `/api/v1/auth/refresh` | anonymous | — | Refresh-token-bearer auth |
| POST | `/api/v1/auth/logout` | anonymous | — | Revokes refresh token |
| GET  | `/api/v1/auth/me` | authenticated | — | Profile self-read |

## Organisations

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/organisations` | authenticated | — | Tenant query filter scopes the list |
| POST | `/api/v1/organisations` | anonymous | — | Sign-up flow creates an org **and** mints a 24h bootstrap invitation token in the response. The first registrant who consumes the bootstrap token becomes the org's first OrgAdmin. ADR-0011, commit `3839468`. |
| POST | `/api/v1/organisations/{orgId}/invitations` | `OrgAdmin`, `SuperAdmin` | — | Mint a 7-day invitation token (max 30) for a future user. OrgAdmin can only mint for their own organisation; SuperAdmin can mint for any (mirrors ADR-0012). Body: `{ email?, expiresInDays? }`. ADR-0011, commit `3839468`. |

## Projects

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects` | authenticated | — | Service filters to caller's memberships |
| POST | `/api/v1/projects` | `OrgAdmin`, `SuperAdmin` | — | Admin-only (T-S0-08, commit `37013fc`). `AppointingPartyId` locked to caller's organisation; `SuperAdmin` may create under any org (audited with `project.created.superadmin_bypass`) — see ADR-0012 and commit `c83a8a9`. |
| GET  | `/api/v1/projects/{projectId}` | authenticated | — | Service enforces membership |
| POST | `/api/v1/projects/{projectId}/members` | authenticated | `ProjectManager+` | |

## CDE

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/cde/containers` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/cde/containers` | authenticated | `InformationManager+` | |

## Cost & Commercial

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| POST | `/api/v1/projects/{projectId}/cbs/import` | authenticated | `ProjectManager+` | T-S1-03. Multipart `file` (CSV). Header: `Code,Name,ParentCode,Description,SortOrder`. Import-into-empty only — re-import / merge deferred. Audit: `cbs.imported`. |
| PUT  | `/api/v1/projects/{projectId}/cbs/{itemId}/budget` | authenticated | `ProjectManager+` | T-S1-04. Body `{ "budget": <decimal\|null> }`. Sets / clears the planned budget on a single CBS line. `decimal(18,2)`; currency follows `Project.Currency`. Negative values rejected. Audit: `cbs.line_budget_set` with `previous` / `current` in detail. |

## Documents

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/documents` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/documents` | authenticated | `TaskTeamMember+` | T-S0-08 (commit `c26d451`) |
| GET  | `/api/v1/projects/{projectId}/documents/{documentId}` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/documents/{documentId}/transition` | authenticated | CDE state machine | Service validates `CanTransition(from, to, role)` per `CdeStateMachine` |

## RFIs

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/rfis` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/rfis` | authenticated | `TaskTeamMember+` | T-S0-08 |
| POST | `/api/v1/projects/{projectId}/rfis/{rfiId}/respond` | authenticated | `TaskTeamMember+` | T-S0-08 |

## Actions

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/actions` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/actions` | authenticated | `TaskTeamMember+` | T-S0-08 |
| PATCH | `/api/v1/projects/{projectId}/actions/{actionId}` | authenticated | `TaskTeamMember+` | T-S0-08; assignee ownership check deferred |

## Audit

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/audit` | authenticated | `InformationManager+` | |

## Project Templates

Routes under `/api/projects/{projectId}/templates` (note: unversioned
prefix, predates the `api/v1/` convention).

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/projects/{projectId}/templates` | authenticated | membership | T-S0-08 added the membership check (commit `7bda674`) |
| GET  | `/api/projects/{projectId}/templates/{templateId}/content` | authenticated | membership | T-S0-08 |
| PUT  | `/api/projects/{projectId}/templates/{templateId}/content` | authenticated | `InformationManager+` | T-S0-08 |
| POST | `/api/projects/{projectId}/provision` | `OrgAdmin`, `SuperAdmin` | — | T-S0-08; admin-only re-provisioning |

## Known-deferred checks

These are *not* bugs per current ADR-0010 scope, but likely ADR or
hardening candidates in future sprints.

- `PATCH actions/{actionId}` does not verify the caller is the
  action's assignee. `TaskTeamMember+` covers the minimum floor;
  service-level assignee check is deferred.
- `POST rfis/{rfiId}/respond` does not verify the caller is the
  intended responder (if any). Same reasoning.
- `GET /api/v1/organisations` returns all organisations visible
  through the tenant query filter; for ordinary users this is their
  own org, for `SuperAdmin` this is all orgs. Acceptable by ADR-0003
  but worth revisiting if an admin UI is built.
- No rate limiting on `POST /auth/login` or `/auth/refresh`; out of
  S0 scope, candidate for a later hardening sprint.

## Update protocol

When a new endpoint is added or an existing gate is changed, update
this file **in the same commit** as the code change. Drift between
code and this matrix should be caught by code review and by T-S0-04
/ T-S0-06b-style behavioural tests against each gate.

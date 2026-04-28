# Post-S1 role-matrix audit — 2026-04-28

**Reviewer:** AI assistant (self-review, no external audit).
**Scope:** every row in `docs/security/role-matrix.md`, verified
against the actual controller / service code on master at the
time of the audit. Triggered by the **B-007 finding** (the
matrix row for `GET /api/v1/organisations` claimed "Tenant query
filter scopes the list" but `Organisation` was intentionally
unfiltered per ADR-0003 — wrong-premise framing exposed an
org-enumeration leak). Worth re-reading every row to find any
other matrix-vs-code drift of the same shape.

## Findings summary

| ID | Severity | Title |
|---|---|---|
| RM-01 | Low | `POST /api/v1/auth/logout` was missing the per-IP rate limit applied to the other anonymous endpoints |

That's it. Every other row in the matrix was verified honest:
the global-role and project-role gates the matrix claims are
the gates the code applies. The B-007-style wrong-premise
framing did not appear elsewhere.

## Audit method

For each row in the matrix:

1. Locate the controller method via the route claim.
2. Verify the `[Authorize(...)]` attribute (or its absence)
   matches the Global role column.
3. For "membership" entries, verify a
   `GetProjectRoleAsync(db, projectId)` call exists (which
   throws `ForbiddenException` for non-members).
4. For `<role>+` entries, verify a
   `HasMinimumRole(role, UserRole.<role>)` check follows the
   `GetProjectRoleAsync` call.
5. For "Service enforces ..." entries, follow the call into the
   service and verify the claim there.
6. For rate-limit / scoping / audit-event mentions, locate the
   relevant attribute / code path.

## Findings

### RM-01 — `POST /auth/logout` missing rate limit **(Low)**

**Location.** `CimsApp/Controllers/Controllers.cs` —
`AuthController.Logout`.

**Observation.** The endpoint is `[AllowAnonymous]` (inherited
from the controller's `[AllowAnonymous]` declaration) and does
a DB lookup per call (`db.RefreshTokens.IgnoreQueryFilters()
.FirstOrDefaultAsync(r => r.Token == token)`). The other
anonymous DB-touching endpoints (`/auth/register`,
`/auth/login`, `/auth/refresh`, anonymous `POST /organisations`)
all have `[EnableRateLimiting("anon-default" or "anon-login")]`
applied via B-002. Logout did not.

**Severity.** Low. An attacker has no way to obtain a refresh
token they don't already own; spamming logout with random
tokens just produces a stream of `FirstOrDefaultAsync` lookups
that miss. The DB load is real but the cardinality is bounded
by ASP.NET Core's overall request-handling capacity, which is
already gated by infrastructure.

**Why this is still worth fixing.** B-002's intent was to
rate-limit every anonymous DB-touching endpoint. Logout was a
miss in the original scope (the SR-S0-06 description explicitly
listed register / login / refresh / org-create and skipped
logout). Closing the gap completes the B-002 spirit and removes
a "why isn't this one rate-limited?" reviewer question.

**Fix.** Add `EnableRateLimiting("anon-default")` (10 / min /
IP, same as register / refresh / org-create) to
`AuthController.Logout`. Matrix row updated to reflect the new
behaviour.

## Verified clean

For each row I walked through the code and confirmed the
matrix claim. The rows that warrant explicit mention because
the audit answered a non-trivial question:

- **`GET /api/v1/projects`** — "Service filters to caller's
  memberships". Verified: `ProjectsService.ListAsync` does
  `Where(p => p.IsActive && p.Members.Any(m => m.UserId ==
  userId && m.IsActive))`. The membership filter is in the
  service, not the controller.
- **`GET /api/v1/projects/{projectId}`** — "Service enforces
  membership". Verified: `ProjectsService.GetByIdAsync` includes
  the same `Members.Any(...)` predicate; non-member request
  returns 404 (NotFoundException), which is the correct
  existence-not-leaked semantic.
- **`POST /api/v1/projects/{projectId}/members`** —
  "ProjectManager+". Verified the controller calls
  `GetProjectRoleAsync` then `HasMinimumRole(role,
  UserRole.ProjectManager)` before invoking the service.
- **`POST /documents/{documentId}/transition`** — "CDE state
  machine". Verified: controller calls `GetProjectRoleAsync`
  and passes the role into the service; service calls
  `CdeStateMachine.IsValidTransition(from, to)` and
  `CdeStateMachine.CanTransition(from, to, role)` — both
  return false on illegal transitions and the service throws
  appropriately.
- **`POST /api/v1/organisations/{orgId}/invitations`** —
  "OrgAdmin can only mint for their own organisation". Verified:
  the controller has `[Authorize(Roles = "OrgAdmin,SuperAdmin")]`
  AND an inline check `if (!tenant.IsSuperAdmin &&
  tenant.OrganisationId != orgId) throw new
  ForbiddenException(...)`. Cross-org OrgAdmin attempts are
  blocked before the service is invoked.
- **All Project Templates rows** — verified against
  `ProjectTemplatesController`. The notable one
  (`POST /api/projects/{projectId}/provision`) uses
  `[Authorize(Roles = "OrgAdmin,SuperAdmin")]` exactly as
  claimed.

## Conclusions

The matrix is otherwise an accurate description of the
authorization surface as of master at the time of audit. The
single finding (RM-01) is closed in the same commit as this
audit doc; no items promoted to the v1.1 backlog.

The B-007 wrong-premise framing that triggered this pass was
the genuine outlier — it propagated for as long as it did
because the matrix entry sounded plausible and no test exercised
the "ordinary user enumerates other orgs" case. Adding the
missing test (now in `OrganisationsListScopingTests`) closes
the regression risk too.

**Process note for future audits.** When a matrix entry says
"Tenant query filter scopes ...", check whether the entity
in question is in the `IntentionallyUnfiltered` list in
`CimsDbContextTenantFilterTests.cs`. If yes, the matrix is
making a wrong-premise claim and the controller needs an
explicit Where clause.

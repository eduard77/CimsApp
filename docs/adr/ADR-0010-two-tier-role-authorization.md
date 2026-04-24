# ADR-0010 — Two-tier role authorization model

**Status:** Accepted (2026-04-24, Sprint S0, T-S0-08)
**Supersedes:** —

## Context

T-S0-08 required a role-enforcement audit across every existing
controller and the addition of missing role gates. The S0 task table
phrased the remedy as "add `[Authorize(Roles=…)]` where missing". In
practice, CIMS has two distinct role concepts:

1. **Global role** (`User.GlobalRole`) — takes values `SuperAdmin`,
   `OrgAdmin`, and the other `UserRole` enum members. Emitted on the
   JWT as the custom claim `cims:role` by `AuthService`. Governs
   cross-organisation capability (e.g., admin actions).
2. **Per-project role** (`ProjectMember.Role`) — held on the join row
   between a `User` and a `Project`. Governs what a user can do
   *inside* a specific project. A user can be `ProjectManager` on
   project A and `Viewer` on project B simultaneously.

ASP.NET Core's `[Authorize(Roles = "...")]` attribute only inspects
the authenticated principal's role claims. It cannot express
"require `ProjectManager` role **on this project**", because that
lookup depends on the route parameter and a database query.

The codebase already mixes the two approaches: before T-S0-08, CDE
container creation, audit log reads, and member-addition endpoints
used imperative `HasMinimumRole(await GetProjectRoleAsync(...), ...)`
checks; other endpoints had no role gating at all. The audit had to
decide whether to (a) migrate everything to attributes and flatten
roles into the JWT, (b) migrate everything to imperative checks, or
(c) keep both and apply each where it fits.

## Decision

Adopt a **two-tier authorization model** as the project standard:

- **Attribute-based `[Authorize(Roles = "...")]`** is used for
  endpoints gated on *global role only* — where the decision does
  not depend on which project the request targets. Typical targets:
  org-wide admin actions (project creation, template re-provisioning,
  future billing / user-management endpoints).
- **Imperative `HasMinimumRole(await GetProjectRoleAsync(db, projectId),
  minimum)`** is used for endpoints gated on *per-project role* —
  membership lookup is required. Typical targets: project-scoped
  writes (documents, RFIs, actions, CDE containers, audit reads).

To make attribute-based gates work, `Program.cs` sets
`TokenValidationParameters.RoleClaimType` to
`HttpTenantContext.GlobalRoleClaimType` (`"cims:role"`), so the JWT's
custom role claim is recognised by ASP.NET's authorization
middleware.

## Alternatives considered

**Attribute-only model (flatten per-project role into the JWT).**
Rejected. Per-project role is route-dependent; it cannot be baked
into a JWT issued at login. Embedding a map of `{projectId: role}`
into the token is unbounded in size and stale the moment membership
changes.

**Imperative-only model (no attribute gates).** Rejected. Global-admin
endpoints have no `projectId` route parameter and no project-level
role to check; forcing them through the per-project helper would
require a synthetic "org admin" pseudo-membership, which is indirect
and obscures intent.

**Custom `[AuthorizeProjectRole]` attribute with an
`IAuthorizationHandler`.** Deferred. It would remove the remaining
line of imperative code per endpoint, but introduces an authorization
handler, requires `DbContext` access inside the handler, and pays a
complexity cost for syntactic sugar only. Revisit if role checks
multiply beyond the current ~15 sites.

## Consequences

**Positive:**
- Clear rule of thumb: attribute for global, imperative for
  per-project. Removes ambiguity about which to reach for.
- Matches the existing imperative pattern (`CdeController`,
  `AuthController.AddMember`) without inventing new infrastructure.
- JWT stays small and stable across project-membership changes.

**Negative:**
- Two patterns to learn rather than one. Must include this ADR in
  onboarding material when the team grows.
- Imperative checks are not visible in route listings or in
  `Microsoft.AspNetCore.Authorization` policy introspection. Tests
  (T-S0-04, T-S0-06b, and future controller tests) must exercise
  each per-project gate directly.

**Neutral:**
- `RoleClaimType` configuration is one line in `Program.cs`. If JWT
  issuer changes in future, the role-claim mapping must move with it.

## Related

- PAFM-SD Ch 24 (architecture decisions require ADR).
- PAFM Appendix F.1 (module-level DoD — role enforcement).
- ADR-0003 (row-level multi-tenancy) — feeds the tenant scope that
  per-project role queries run against.
- `docs/sprint-log/s0.md` T-S0-08 — delivery record.
- `docs/security/role-matrix.md` — the canonical endpoint → role
  table that this ADR's rule produced.

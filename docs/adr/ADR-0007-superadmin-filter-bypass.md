# ADR-0007 — SuperAdmin tenant-filter bypass via explicit `IgnoreQueryFilters`

**Status:** Accepted (retroactive — the implementation landed at
T-S0-07 / commit `b2a5781` during S0; this ADR formalises the
policy and resolves the dangling `ADR-0007` references in the
role-matrix, v1.1 backlog, and service code that have been
citing this decision since its first use).
**Supersedes:** —
**Related:** ADR-0003 (tenant isolation via global query filters),
ADR-0010 (two-tier role authorization), ADR-0012 (project tenancy
semantics — establishes SuperAdmin's cross-tenant create path).

## Context

ADR-0003 installs a global EF query filter on every tenant-scoped
entity:

```csharp
m.Entity<Project>().HasQueryFilter(p => p.AppointingPartyId == _tenant.OrganisationId);
```

For ordinary callers this is exactly right: every read against
a tenant-scoped entity is automatically restricted to the
caller's organisation, no per-call filtering needed. The
`Organisation` entity is the deliberate exception (used during
pre-auth flows like sign-up and login).

But CIMS also has a **SuperAdmin** GlobalRole — a platform-wide
administrator able to operate across tenants. Examples:

- Creating projects under another organisation as a platform
  admin (ADR-0012's bypass path; audited as
  `project.created.superadmin_bypass`).
- Revoking another tenant's user's tokens
  (B-001 / ADR-0014's admin path).
- Listing every active organisation
  (`GET /api/v1/organisations`, B-007 close).

For these paths the global query filter is wrong: it would scope
the SuperAdmin to their nominal `tenant.OrganisationId` even
though the whole point is to act outside it.

We needed a uniform mechanism that:

1. Keeps the default-safe behaviour (every read filtered) for
   ordinary code paths, including code paths that have a
   SuperAdmin caller but are not intentionally cross-tenant.
2. Makes the cross-tenant intent explicit at the call site.
3. Is auditable — a reviewer can grep for the bypass marker.
4. Doesn't require a parallel "unfiltered" set of DbContext
   methods or a second DbContext type.

## Decision

**SuperAdmin code paths that intend cross-tenant access call
`IgnoreQueryFilters()` explicitly on the relevant DbSet
query.** The tenant filter remains globally installed; the
bypass is opt-in per call site.

The branch is gated on `tenant.IsSuperAdmin`:

```csharp
var query = tenant.IsSuperAdmin
    ? db.Users.IgnoreQueryFilters()
    : db.Users.AsQueryable();
```

Or, where the entire endpoint is admin-only:

```csharp
[Authorize(Roles = "OrgAdmin,SuperAdmin")]
public async Task<IActionResult> RevokeTokens(Guid userId)
{
    // OrgAdmin tenant-scoped (filter applies); SuperAdmin
    // bypass via the IsSuperAdmin branch in the service.
    await svc.RevokeUserTokensAsync(userId, tenant);
    return Ok(new { success = true });
}
```

Pre-auth code paths (sign-up, login, refresh) also use
`IgnoreQueryFilters()` because at that moment there is no
established `_tenant.OrganisationId` to filter against — that's
a separate justification but it uses the same mechanism. This
ADR governs the SuperAdmin path; the pre-auth path is
documented inline in the relevant `AuthService` methods.

## Consequences

- **Code reviews must check** every `IgnoreQueryFilters()` call
  for intent. The grep-for-bypass-marker invariant is the
  primary defence: if a non-SuperAdmin code path calls it
  without the pre-auth justification, that's a regression
  candidate.
- **Audit-on-bypass.** When SuperAdmin actions cross tenant
  boundaries the audit row carries the structured action name
  for forensic discoverability:
  - `project.created.superadmin_bypass` (ADR-0012).
  - `auth.user_admin_revoke` with `targetUserId` in detail
    (B-001 / ADR-0014).
  - `auth.user_deactivated` likewise.
  Reviewers can search audit logs for `*.superadmin_bypass` or
  for the `auth.user_admin_*` action names to enumerate every
  cross-tenant event.
- **SuperAdmin role is rare by policy.** The role is set at
  invitation-token consumption time (bootstrap tokens promote
  to OrgAdmin only; SuperAdmin must be set explicitly via DB
  update during platform provisioning). No public endpoint
  promotes a user to SuperAdmin.
- **Rejected alternatives.**
  - A parallel `IUnfilteredDbContext` interface or a second
    DbContext type — adds infrastructure for a small per-call
    decision; the explicit `IgnoreQueryFilters()` is one method
    and stays at the read site where the intent is local.
  - Stripping the tenant filter when `IsSuperAdmin` at filter-
    install time — would make every SuperAdmin read implicitly
    cross-tenant, defeating the default-safe property and
    losing the audit-call-site marker.
  - A claims-transformer that sets `_tenant.OrganisationId` to
    null for SuperAdmin — null-tenant query filter has well-known
    failure modes (filter evaluates to `p.AppointingPartyId == null`,
    which silently returns nothing rather than everything).

## Compliance with this ADR

- The pattern appears in `ProjectsService.CreateAsync`
  (creates under any org for SuperAdmin per ADR-0012).
- `AuthService.RevokeUserTokensAsync(userId, tenant)` and
  `DeactivateUserAsync` (B-001 / ADR-0014) — `IsSuperAdmin`
  branch uses `IgnoreQueryFilters()`.
- `OrganisationsController.List` (B-007) — non-SuperAdmin
  scoped via controller-level `Where`; SuperAdmin sees the
  unfiltered `Organisations` set (Organisation has no global
  filter per ADR-0003).
- Pre-auth methods on `AuthService` (`LoginAsync`,
  `RegisterAsync`, `RefreshAsync`, `LogoutAsync`,
  `RevokeOwnTokensAsync`) — also use `IgnoreQueryFilters()`
  but the justification is "no tenant context yet" rather
  than SuperAdmin bypass.

## Notes on numbering

ADR numbers 0001, 0002, 0004, 0005, 0006 are intentionally
left unused. The numbering jumped to 0003 for the foundational
tenant-isolation decision; 0007 was reserved for the
SuperAdmin-bypass policy and cited by code at first use, but
the document was never written until now. Future ADRs continue
from 0014 (`docs/adr/ADR-0014-access-token-residual-authority-sla.md`)
without backfilling the gaps.

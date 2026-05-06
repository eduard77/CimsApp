# Admin Console

S16 module — `/admin/*` surfaces for tenant administration. All
endpoints gated `[Authorize(Roles = "OrgAdmin,SuperAdmin")]`;
regular users hitting these pages see "Insufficient permissions".

## Pages

### `/admin/users`

Paged user table. Search by email or name. Per-row:

- Inline GlobalRole select — null / OrgAdmin / SuperAdmin (only
  SuperAdmin may grant SuperAdmin; cannot self-target).
- Deactivate — sets `IsActive = false` + bumps token cutoff
  + sweeps refresh tokens (ADR-0014). User cannot log in again
  unless re-activated.
- Revoke tokens — keeps the user active but kills every in-flight
  session immediately.

OrgAdmin sees only their tenant's users; SuperAdmin sees every
tenant's via IgnoreQueryFilters.

### `/admin/organisations`

SuperAdmin: paged list of every organisation (with user + project
counts, search by name/code, include-inactive toggle).
OrgAdmin: a single-row view of their own org.

Edit — Name + Country only. **Code is intentionally immutable**
(it's the canonical lookup key for storage paths and the unique
index — renaming would orphan the on-disk template folder).

Deactivate — SuperAdmin only. Sets `IsActive = false` on the org;
downstream entities keep their flags.

### `/admin/invitations`

List of active (unconsumed AND unexpired) invitations across the
tenant (or all tenants for SuperAdmin). Filter by organisationId.
Revoke action expires the invitation immediately
(`ExpiresAt = UtcNow`). Already-consumed invitations cannot be
revoked — the right action there is to deactivate the resulting
user.

Mint via the existing `POST /organisations/{id}/invitations`
endpoint (separate flow, not on the admin page).

### `/admin/audit`

See [audit-and-compliance.md](audit-and-compliance.md) — the
tenant-wide audit log surface.

### `/admin/auditor-assignments`

See [audit-and-compliance.md](audit-and-compliance.md) — the
External Auditor role lifecycle.

## Common gotchas

- **OrgAdmin scope**: tenant-only via the standard query filter.
  Cross-tenant attempts return 404 (no existence leak).
- **SuperAdmin scope**: cross-tenant via IgnoreQueryFilters per
  ADR-0007.
- **Self-target role-set is rejected** — an OrgAdmin cannot
  promote or demote themselves (lockout / privilege escalation
  guard).
- **Org Deactivate is SuperAdmin-only** — OrgAdmin self-deactivation
  is a footgun (their next request would 401); deferred to
  v1.1 / B-099.

# ADR-0014 — Access-token residual-authority SLA

**Status:** Accepted (2026-04-28, post-S1, B-001 closeout)
**Supersedes:** —
**Related:** B-001 (Access-token lifetime + role-change revocation),
ADR-0010 (two-tier role authorization), ADR-0011 (invitation-token
user provisioning).

## Context

CIMS issues bearer JWT access tokens with a 60-minute lifetime
(`Jwt:AccessExpiresMinutes` in `appsettings.json`, default 60). The
access token carries the caller's `cims:role` GlobalRole claim and
their tenant `cims:org` claim baked in at issue time. After issue,
no claims can be amended without re-issue.

Three security-sensitive User mutations interact with this design:

1. **Role demotion** (e.g. revoking `OrgAdmin` from a user).
   Without action, the user retains the demoted role's authority
   for up to 60 minutes via their existing access token.
2. **User deactivation** (`User.IsActive = false`).
   Same residual-authority window: the access token has no
   liveness signal back to the User row, so the deactivated user
   keeps acting until the token expires.
3. **Password reset / credential rotation.**
   The same window.

B-001 surfaced this risk during S0 review (SR-S0-04). The B-001
partial close on 2026-04-28 shipped the revocation primitive
(`User.TokenInvalidationCutoff` column, `TokenRevocation.IsRevoked`
helper, `JwtBearerEvents.OnTokenValidated` hook,
`AuthService.RevokeUserTokensAsync`) which collapses the window to
**zero** when the revocation path is invoked. This ADR documents
the policy that governs that path and the residual exposure when
it is *not* invoked.

## Decision

CIMS adopts the following residual-authority SLA for v1.0:

### 1. AccessMinutes is a security parameter, not a UX parameter

The access-token lifetime (`Jwt:AccessExpiresMinutes`, currently 60)
is the **upper bound on residual authority** when no explicit
revocation occurs. It is governed as a security parameter:

- Default value: **60 minutes.** Industry-typical bearer-token
  lifetime; balances refresh-load against residual exposure.
- Range: **5 — 60 minutes.** Below 5, refresh churn outweighs
  the marginal exposure reduction; above 60, residual exposure
  exceeds the threshold this ADR is willing to accept by default.
- Changes outside this range are an ADR-amending decision, not a
  configuration change.

### 2. All security-sensitive User mutations MUST call the revocation primitive

The following mutations are required to invoke
`AuthService.RevokeUserTokensAsync(userId, tenant)` (or the
self-service equivalent) atomically with the mutation:

- **Role demotion / promotion** (any `User.GlobalRole` change).
- **User deactivation** (`User.IsActive = false`).
- **Password reset** when implemented (post-v1.0).
- **Manual revoke** triggered by an admin or by self-service
  ("log out everywhere").

When the call is made, the residual-authority window collapses to
zero — the mutated user's existing JWT is rejected at the next
authenticated request via the `OnTokenValidated` hook.

### 3. Refresh tokens are NOT in scope of this SLA

`TokenInvalidationCutoff` does not affect refresh tokens directly.
Refresh tokens have their own `RevokedAt` field per
`RefreshToken` entity, and `AuthService.LogoutAsync` handles the
single-token revoke. A "log out everywhere" implementation that
revokes ALL of a user's refresh tokens is a v1.1 hardening item
(see B-019 / future entry); the access-token cutoff is sufficient
for v1.0 since access tokens are short-lived.

### 4. The `IsActive` short-circuit is permanent

`User.IsActive == false` is checked at every authenticated request
via `TokenRevocation.IsRevoked`. This is independent of the cutoff
field and survives any cutoff race — a deactivated user is rejected
*regardless* of when their token was issued. The cutoff field is
the role-demotion / explicit-revoke mechanism; the IsActive check
is the deactivation mechanism. They are belt-and-braces.

## Consequences

- The 60-minute residual-authority window is bounded *and*
  documented. A reviewer can read this ADR and form a clear view
  of the residual risk between an operationally normal revoke
  (no extra work; collapses to zero) and a forgotten revoke
  (60 minutes max).
- Future code that mutates `GlobalRole`, `IsActive`, or password
  hashes is implicitly required by this ADR to call the revocation
  primitive. The audit-twin pattern from S1 makes the absence of
  such a call visible during code review (a User mutation without
  a corresponding cutoff bump is a tell).
- The 5-minute lower bound on `AccessMinutes` informs any future
  "high-security tenant" feature: a tenant cannot ratchet down to
  60-second tokens without an ADR amendment.
- Refresh-token revocation remains per-token (logout) or full
  by manual DB update; "log out all sessions" is a v1.1 candidate
  flagged in this ADR's section 3.
- The IsActive permanent-reject path makes "deactivate user" the
  recommended emergency response — a single column flip kills
  every active session in addition to bumping the cutoff.

## Alternatives considered

- **Reduce `AccessMinutes` to 5 min as the default.** Rejected:
  refresh-token churn would 12× and the marginal exposure window
  reduction (55 min) does not justify the operational cost in v1.0
  where the deployment is internal.
- **Per-tenant configurable `AccessMinutes`.** Rejected: adds
  configuration surface area and a "shortest-prevails" question
  when a user belongs to multiple tenants. Promotable to v1.1+ if
  a high-security tenant requirement materialises.
- **Revoke-on-every-mutation (cutoff bump for any User update).**
  Rejected: noisy. Profile changes (FirstName, JobTitle, Phone)
  do not warrant token revocation — they don't elevate authority.
  The list in section 2 is the deliberate "security-sensitive"
  shortlist.
- **Move the cutoff onto the JWT itself (e.g. as a versioned
  signing key per user).** Rejected: would require per-user
  signing-key infrastructure for marginal benefit over the
  per-request DB lookup in `OnTokenValidated`.

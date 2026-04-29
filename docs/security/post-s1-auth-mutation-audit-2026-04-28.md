# Post-S1 auth-mutation audit — 2026-04-28

**Reviewer:** AI assistant (self-review, no external audit).
**Scope:** the auth surface as it stands after the v1.1-backlog
hardening run on 2026-04-28. Specifically: every code path that
mutates `User.GlobalRole`, `User.IsActive`, or `User.PasswordHash`,
plus the JWT validation pipeline and refresh-token lifecycle.
**Trigger:** the B-019 close (PR #11) revealed a "log out
everywhere" gap in the original ADR-0014 §3 reasoning. That
discovery made it worth a second pass — if the original revoke
design missed refresh tokens, what else might it miss?
**Commits under review:**

```
9548ee2 Merge pull request #11 from eduard77/feature/b-019-refresh-token-bulk-revoke
af02bf3 Merge pull request #10 from eduard77/feature/b-002-progressive-backoff-and-b-003-obsolete
8e454f7 Merge pull request #9  from eduard77/feature/b-001-close-revoke-endpoints
2227675 Merge pull request #8  from eduard77/feature/b-001-token-revocation
b022de4 Merge pull request #7  from eduard77/fix/login-nre-on-missing-user
```

## Findings summary

| ID | Severity | Title |
|---|---|---|
| AM-01 | None | `User.GlobalRole` is set only at `RegisterAsync`; no demotion endpoint exists, so the role-change residual-authority path captured by ADR-0014 has no current consumer that could forget the revoke call |
| AM-02 | None | `User.IsActive = false` happens only in `DeactivateUserAsync`, which already calls the cutoff bump and `SweepActiveRefreshTokensAsync` |
| AM-03 | None | `User.PasswordHash` is set only at `RegisterAsync`; no password-reset endpoint exists |
| AM-04 | Info | The auth domain does not emit structured `AuditService.WriteAsync` events on revoke / deactivate / sweep — the audit-twin pattern from S1 cost domain isn't applied. The `AuditInterceptor` per-row Update audit captures Before/After on User and RefreshToken rows, which is forensically adequate, but lacks the semantic action names (`auth.user_admin_revoke`, `auth.refresh_tokens_swept`) that an audit-log reviewer would naturally search for. Promoted to backlog as **B-021** |
| AM-05 | Info | `JwtBearerEvents.OnTokenValidated` does a DB lookup per authenticated request to load the User and run `TokenRevocation.IsRevoked`. Acceptable for the v1.0 single-instance deployment but a per-request DB hit becomes a hot path under load. No fix required for v1.0; promoted to backlog as **B-022** |

## Audit method

For each finding:

1. `grep -E '\.GlobalRole\s*=|\.IsActive\s*=|\.PasswordHash\s*='`
   across `CimsApp/`. Two hits inside the `CimsApp/` source tree;
   one in `Services.cs:209` (`DeactivateUserAsync`) and one in
   `Services.cs:407` (`AddMemberAsync`, which mutates
   `ProjectMember.IsActive`, not `User.IsActive` — false positive).
2. Inspect each hit for whether it is followed by a call into the
   revoke primitive
   (`SweepActiveRefreshTokensAsync` / cutoff bump).
3. Check construction sites separately: any `new User { ... }`
   that sets `GlobalRole`, `IsActive`, or `PasswordHash`. Only
   `RegisterAsync` constructs `User`. `IsActive = true` at
   construction is fine — no token exists yet, no revocation
   possible or required.

The grep returned exhaustive coverage because the codebase is
small enough that no User-mutation can hide. The single mutation
site (`DeactivateUserAsync`) already calls the revoke primitive.
**The user-mutation surface is currently clean.**

## Findings

### AM-01 — `User.GlobalRole` has no demotion endpoint **(None)**

**Status.** Information only.

The codebase has no endpoint that mutates `User.GlobalRole` after
construction. The role is set once at `RegisterAsync`
(bootstrap invitation → `OrgAdmin`; otherwise `null`) and never
changes. Promotion / demotion paths anticipated by ADR-0014 do
not currently exist.

**What this means.** The "demote OrgAdmin to TaskTeamMember while
they hold a valid token" scenario captured in ADR-0014 §2 has no
current consumer that could forget the `RevokeUserTokensAsync`
call. The risk is dormant, not present.

**Future obligation.** When a role-change endpoint is added
(natural home: S14 admin console), the developer is required by
ADR-0014 §2 to call `RevokeUserTokensAsync(userId, tenant)`
atomically with the role change. The audit-twin pattern in S1
makes the absence of such a call visible at code review (a User
mutation without a corresponding cutoff bump is a tell).

### AM-02 — `User.IsActive` flip is properly accompanied by revoke **(None)**

**Status.** Information only — the path is correct as-is.

`Services.cs:209` is the only place `User.IsActive = false` is
written. It's inside `DeactivateUserAsync`, which immediately
afterwards calls `SweepActiveRefreshTokensAsync` and bumps
`TokenInvalidationCutoff` before `SaveChangesAsync`. The
behavioural test
`AuthServiceInputValidationTests.DeactivateUser_sets_IsActive_false_and_bumps_cutoff`
plus `RefreshTokenSweepTests.DeactivateUser_sweeps_target_users_refresh_tokens`
cover this end to end.

There is no other write site; in particular there is no
"reactivate" endpoint that would set `IsActive = true`. If
reactivation is added, ADR-0014 §4 (the IsActive permanent-reject
rule) implies the cutoff stays in place — a previously revoked
user shouldn't get implicitly un-revoked by reactivation.

### AM-03 — `User.PasswordHash` has no reset endpoint **(None)**

**Status.** Information only.

The hash is set once at `RegisterAsync` via
`BCrypt.Net.BCrypt.HashPassword(req.Password)`. No password-reset
or change-password endpoint exists.

**Future obligation.** ADR-0014 §2 requires a password reset to
call the revocation primitive — anyone holding tokens minted
under the old password should lose them at reset time. When a
password-reset endpoint is added (no current sprint home; v1.1
candidate), the developer must call `RevokeUserTokensAsync` (or
`RevokeOwnTokensAsync` for self-service) atomically with the
hash update.

### AM-04 — Auth domain lacks structured `AuditService` events **(Info)**

**Status.** Refinement, not a defect. Logged as **B-021** for
v1.1.

The S1 cost domain established the audit-twin pattern: every
security-sensitive mutation emits both (a) an `AuditInterceptor`
per-row Insert/Update/Delete audit, AND (b) a structured
`AuditService.WriteAsync(actorId, "domain.action", ...)` event
with semantic action name and structured detail.

The auth domain currently relies on (a) only. The
`AuditInterceptor` captures the User row Update (Before/After on
`IsActive` and `TokenInvalidationCutoff`) and the RefreshToken
row Updates (Before/After on `RevokedAt`), which is forensically
adequate — an investigator can reconstruct what happened from
the audit log. It is, however, less *discoverable*: a search for
"who deactivated user X" would require a query like
`AuditLog.Action == 'Update' AND Entity == 'User' AND EntityId
== X` plus inspection of the AfterValue JSON for IsActive
toggling. A structured `auth.user_deactivated` event would let
the same query be `Action == 'auth.user_deactivated'`.

**Why deferred.** Adding `AuditService` to `AuthService`'s ctor
+ four call sites + tests is a small but non-zero refactor;
forensic adequacy is in place; a v1.0 internal pilot does not
exercise the audit log at the volume where action-name
discoverability would matter.

### AM-05 — Per-request DB lookup in `OnTokenValidated` **(Info)**

**Status.** Acceptable for v1.0; logged as **B-022** for the
horizontal-scale follow-on alongside B-018.

`Program.cs` configures
`JwtBearerEvents.OnTokenValidated` to load the User row via
`db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id ==
userId)` on every authenticated request. For the v1.0
single-instance deployment with the expected internal-pilot
traffic this is fine — the lookup is by primary key, fully
indexed.

At horizontal scale (B-018 territory) this becomes a hot path
that could benefit from a short TTL cache (e.g. 30 seconds, with
explicit cache invalidation on `RevokeUserTokensAsync`) or a
per-user version stamp denormalised onto the JWT itself.

**Why deferred.** Premature optimisation for v1.0. Promote to
sprint when production traffic data shows it.

## Conclusions

The User-mutation surface is currently clean: the only write
site that matters (`DeactivateUserAsync`) already calls the
revoke primitive, and the other security-sensitive mutations
(`GlobalRole`, `PasswordHash`) have no live consumers — the
risk is dormant pending future endpoints (role-demote, password
reset, both natural fits for v1.1 / S14).

**Follow-on finding 1 (2026-04-29).** A defense-in-depth gap
was identified in the AuditInterceptor itself: `User.PasswordHash`
and `Invitation.TokenHash` were being serialised verbatim into
the audit `BeforeValue` / `AfterValue` JSON. Bcrypt'd hashes
and SHA-256 token hashes are not plaintext but should not leak
into the audit log's wider blast radius. Closed by adding a
`SkippedFieldNames` set to `AuditInterceptor.SerialiseState`
that filters those property names out of every audited entity,
plus regression tests in `AuditInterceptorBehaviourTests` that
seed Users with a recognisable bcrypt prefix and assert it
does not appear in the audit JSON.

**Follow-on finding 2 (2026-04-29).** `RegisterAsync` performed
two separate SaveChanges calls — first to insert the User row,
then `InvitationService.MarkConsumedAsync` (which uses
`ExecuteUpdateAsync`, fired as a separate SQL command). A
process crash between the two operations would leave a User
created (visible to login) but the Invitation still
consumable, allowing a second registration with a different
email to mint a second User from the same token — violating
"one invitation, one user". Closed by wrapping both writes in
`db.Database.BeginTransactionAsync()` on RegisterAsync so SQL
Server serialises them into a single atomic unit. Order
preserved: User save first, so an FK / duplicate-index failure
leaves the Invitation available for retry rather than burning
it. EF in-memory provider treats the transaction as a no-op
(no real isolation semantics) but the code shape stays correct
for production. Finding had no live exploit path during v1.0
internal pilot — the crash window is sub-millisecond and the
single-developer flow doesn't race — but the defensive wrap is
correct for any deployment with concurrent registers or
infrastructure flakiness.

Two refinements identified and promoted to backlog as B-021
(structured auth-domain audit events) and B-022 (per-request
DB lookup performance). Neither is a defect; both are quality
improvements for a future scale-out.

The B-019 omission that triggered this audit (refresh-token
sweep) is itself the example of why this pass was worth doing —
the auth surface is small but each mutation has multiple
revocation knobs (cutoff, refresh-token sweep, IsActive flip),
and the original ADR-0014 §3 deferral missed that the knobs
needed to be coordinated. Future auth-surface changes should be
reviewed against ADR-0014 §2's mandatory-revoke list as a
checklist.

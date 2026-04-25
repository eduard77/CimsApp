# ADR-0011 — Invitation-token user provisioning

**Status:** Accepted (2026-04-25, Sprint S0, closes SR-S0-01 / T-S0-11)
**Supersedes:** —

## Context

Security review SR-S0-01 (2026-04-24) identified a critical
tenant-isolation bypass at the registration boundary.
`RegisterRequest` carried a caller-supplied
`Guid OrganisationId`; `AuthService.RegisterAsync` wrote it
verbatim onto the new `User` row; `POST /api/v1/auth/register`
was `[AllowAnonymous]`. Any anonymous attacker who knew an
organisation's Guid (trivially obtainable since
`GET /api/v1/organisations` returned all orgs to any
authenticated user) could register themselves into that tenant
and inherit full read access via the query filters shipped in
T-S0-03.

Three remediation paths were considered (recorded in
`docs/security/s0-review-2026-04-24.md#sr-s0-01`):

1. **Closed registration via invitation tokens** — OrgAdmin
   mints a token; registration validates and consumes it.
   Tenant scope is server-derived from the token.
2. **Domain-bound registration** — Organisation has an allowed
   email-domain list and Register only succeeds on a domain
   match.
3. **Approval queue** — Register creates a pending row and an
   OrgAdmin approves before the user can log in.

Option 1 was chosen by the product owner on 2026-04-24.

## Decision

Adopt invitation-token-only registration:

- `RegisterRequest` no longer carries `OrganisationId`. It now
  carries `string InvitationToken` instead.
- `Invitation` is a new tenant-scoped entity (table
  `Invitations`). Each row stores: `OrganisationId`,
  `TokenHash` (SHA-256 of the plaintext, unique-indexed),
  optional `Email` bind, `IsBootstrap`, `ExpiresAt`,
  `ConsumedAt`/`ConsumedByUserId`, `CreatedById`. Plaintext
  tokens are returned exactly once at creation and never
  persisted.
- `InvitationService.ValidateAsync` looks the token up by hash
  with `IgnoreQueryFilters()` (pre-auth path; same pattern
  `AuthService` uses for User/RefreshToken lookups), then
  rejects consumed, expired, and email-mismatched tokens.
- `InvitationService.MarkConsumedAsync` uses `ExecuteUpdateAsync`
  with a `Where(ConsumedAt == null)` clause to atomically claim
  the token, so two concurrent register calls cannot
  double-consume the same invitation.
- `AuthService.RegisterAsync` validates the token first, derives
  `OrganisationId` from the invitation rather than the request
  body, creates the `User`, and only then marks the invitation
  consumed. Splitting validate-then-consume around `SaveChanges`
  ensures a transient failure on user creation leaves the token
  available for retry rather than burning it.
- `User.GlobalRole = UserRole.OrgAdmin` is set if and only if
  `Invitation.IsBootstrap` is true. Non-bootstrap registrations
  never elevate to a global role.

Two mint surfaces are exposed:

- **Bootstrap.** `POST /api/v1/organisations` is anonymous and
  creates an organisation. Its response now includes a
  single-use 24-hour bootstrap invitation token. The first
  registrant becomes the organisation's first OrgAdmin and can
  mint further invitations.
- **OrgAdmin invite.** `POST /api/v1/organisations/{orgId}/invitations`
  is gated `[Authorize(Roles="OrgAdmin,SuperAdmin")]`. OrgAdmin
  callers may only mint invitations for their own organisation;
  SuperAdmin may mint for any (mirrors ADR-0012's
  caller's-org-default / SuperAdmin-bypass rule). Body accepts
  optional `Email` (binds the invitation to a specific recipient)
  and `ExpiresInDays` (default 7, bounded to [1,30]).

Token format: 32 cryptographically random bytes via
`RandomNumberGenerator.GetBytes`, base64url-encoded.

## Alternatives considered

**Option 2 — Domain-bound.** Rejected because it provides no
defence against an attacker who controls a single matching email
(the domain check authorises the entire mailbox space rather
than a specific person). Useful as a *layer* on top of
invitations later, but not as a replacement.

**Option 3 — Approval queue.** Rejected for v1.0. It adds a
state machine (pending → approved → active), an OrgAdmin UI to
review pending users, and an audit trail for the approval event,
none of which exist today. It is also strictly more work than
invitations to deliver an equivalent isolation guarantee.

**Stateless signed tokens (no `Invitation` table).** Rejected.
Single-use semantics require server-side tracking of consumed
tokens. A pure HMAC-signed token cannot enforce one-time use
without a per-token revocation row, which is the table this ADR
ships anyway.

**Email delivery in v1.0.** Rejected as out of scope. OrgAdmins
share tokens out-of-band for now (Slack, email, paste) until a
notification module lands in Sprint 13. The plaintext token is
returned in the response so the OrgAdmin can copy and forward it.

## Consequences

**Positive:**
- Closes SR-S0-01. Registration is no longer a cross-tenant
  primitive.
- Tenant scope is now server-derived for every code path that
  touches it (query filters for read, invitation tokens for
  user creation, ADR-0012 for project creation).
- Single-use enforcement is atomic; the consumed-marker race is
  closed at the database layer.

**Negative:**
- Anonymous `POST /api/v1/organisations` still has no rate limit
  or CAPTCHA, so the bootstrap-token mint is a spam vector. This
  is acknowledged and tracked in v1.1 backlog item B-002. Until
  B-002 lands, the dev deployment relies on running on
  `localhost` with no public exposure.
- Lost plaintext tokens are unrecoverable. The OrgAdmin must
  re-mint. This is a deliberate trade-off — recoverable tokens
  would mean storing plaintext, which would let a DB-read
  compromise enumerate every outstanding invitation.
- Existing clients of `POST /api/v1/auth/register` that sent
  `OrganisationId` are now broken (HTTP 400 — the field no
  longer exists on the DTO). The dev deployment had only one
  registered user and no automated client, so this breakage is
  contained.

**Neutral:**
- `RegisterRequest` shape change is recorded as CR-002 in the
  change register so the breakage is auditable.

## Related

- PAFM Appendix F.1 — module-level DoD ("Users are scoped to
  organisations"). The new flow is the first implementation that
  honours the spec functionally rather than syntactically.
- PAFM-SD Ch 24 — architecture decisions require ADR.
- ADR-0003 — row-level multi-tenancy via query filter. Filters
  protect reads; invitations protect user creation.
- ADR-0010 — two-tier role authorisation model. The
  `[Authorize(Roles="OrgAdmin,SuperAdmin")]` gate on
  `POST /organisations/{id}/invitations` is an instance of the
  global-role attribute pattern.
- ADR-0012 — caller's-org-default / SuperAdmin-bypass rule.
  `POST /organisations/{id}/invitations` reuses the rule for
  cross-tenant minting.
- CR-002 in `docs/change-register.md` — change record for the
  registration breaking change and the SR-S0-02 sibling fix.
- SR-S0-01 in `docs/security/s0-review-2026-04-24.md` — review
  finding this ADR closes.
- v1.1 backlog item B-002 (rate limiting) — outstanding
  hardening on the anonymous mint surface.

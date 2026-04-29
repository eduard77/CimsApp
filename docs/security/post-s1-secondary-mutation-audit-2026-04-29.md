# Post-S1 secondary-mutation audit — 2026-04-29

**Reviewer:** AI assistant (self-review, no external audit).
**Scope:** mutation surface for security-sensitive non-User
entities — `Organisation`, `Project`, `ProjectMember`. Companion
to the auth-mutation audit
(`docs/security/post-s1-auth-mutation-audit-2026-04-28.md`).
**Trigger:** the auth-mutation audit's "look at every mutation
site for security-sensitive flags" pattern surfaced two real
issues (PasswordHash leak, register transaction wrap). Worth a
parallel pass over Organisation / Project / membership flags
to find any analogous gaps before they bite.

## Findings summary

| ID | Severity | Title |
|---|---|---|
| SM-01 | None | `Organisation.IsActive` has no mutation site; `LoginAsync` / `RefreshAsync` / `OnTokenValidated` do not check it — the dormant "deactivate org but users keep logging in" gap is captured as a future obligation, not a present defect |
| SM-02 | None | `Project.IsActive` has no mutation site; `Project.AppointingPartyId` is set only at `CreateAsync` and never changes — both flags are write-once for v1.0 |
| SM-03 | None | `ProjectMember` has no remove / role-change endpoint; `AddMemberAsync` is the only mutation site (re-activation path covered by B-023's same-org guard, audited via `project.member_added`) |
| SM-04 | Info | No entity has an optimistic-concurrency token (rowversion / `IsConcurrencyToken`). Acceptable for the v1.0 single-user-per-project pilot but a real concern when concurrent edits land. Promoted to backlog as **B-024** |

## Audit method

For each entity:

1. Identify the security-sensitive mutable fields (`IsActive`,
   tenant pointers, role pointers).
2. `grep -E '<entity>\.(<field>)\s*='` across `CimsApp/` to
   enumerate every mutation site.
3. For each site, verify (a) which authorization gate it sits
   behind, (b) whether the mutation has an audit-twin event,
   and (c) whether downstream services that depend on the
   field's truth at request-time actually re-read it.
4. For fields with no mutation site, capture the dormant
   risk as a future obligation rather than closing it as
   "no work needed" — when an endpoint is later added, the
   developer must know which downstream services to wire.

## Findings

### SM-01 — `Organisation.IsActive` enforcement is dormant **(None)**

**Status.** Information only. No present consumer.

`Organisation.IsActive` exists on the entity (`Entities.cs:13`)
and is checked in exactly one place: `OrganisationsController.List`
filters `Where(o => o.IsActive)` so deactivated organisations
are hidden from the org-picker. It is **not** checked in:

- `AuthService.LoginAsync` — only `User.IsActive` is checked.
- `AuthService.RefreshAsync` — only the refresh token's own
  `IsActive` (computed from RevokedAt / ExpiresAt).
- `JwtBearerEvents.OnTokenValidated` in `Program.cs` —
  `TokenRevocation.IsRevoked(user, iat)` covers User.IsActive
  and the per-user cutoff but not the org's IsActive.

**Why this is dormant.** No code path in the v1.0 codebase
mutates `Organisation.IsActive` after construction (it
defaults to `true`). The risk is:

> If a SuperAdmin deactivates an entire organisation, every user
> in that organisation continues to log in successfully and
> operate normally until their User-level state changes.

But there is no SuperAdmin "deactivate organisation" endpoint
right now, so the risk has no consumer.

**Future obligation.** When a `DeactivateOrganisationAsync`
endpoint is added (natural home: S14 admin console), the
developer must:

1. Add `User.Organisation.IsActive == true` to
   `LoginAsync`'s eligibility predicate (or check it after
   the user lookup — a single `if (!user.Organisation.IsActive)`
   throwing `INVALID_CREDENTIALS` keeps the existence
   non-leaking semantic).
2. Bump `User.TokenInvalidationCutoff = UtcNow` for every
   active user in the org (analogous to
   `RevokeUserTokensAsync` but bulk — likely worth a new
   `RevokeOrganisationTokensAsync` helper).
3. Sweep `RefreshToken`s for every user in the org
   (analogous to B-019's `SweepActiveRefreshTokensAsync` but
   bulk).
4. Add a structured audit event `auth.organisation_deactivated`
   with the org Id and the user/refresh sweep counts in detail.

The shape of the work mirrors `DeactivateUserAsync`
exactly — escalated by one level (org rather than user).

### SM-02 — `Project.IsActive` and `Project.AppointingPartyId` are write-once **(None)**

**Status.** Information only.

`Project.IsActive` is set to `true` at construction (entity
default) and never assigned elsewhere in `CimsApp/`. Same
for `Project.AppointingPartyId` — set in `ProjectsService.CreateAsync`
and never reassigned.

The downstream services rely on `Project.IsActive` for
visibility filtering: `ProjectsService.ListAsync` and
`GetByIdAsync` both filter on `p.IsActive`. So a future
`DeactivateProjectAsync` endpoint would correctly hide the
project from list views without further plumbing.

**Future obligations** when a deactivation / transfer
endpoint is added:

- **Project deactivation.** Fields and reads: same as today.
  Behavioural decision: should existing audit / cost data
  remain readable to project members after deactivation?
  Likely yes (project archives must be readable to satisfy
  retention requirements). Add a structured
  `project.deactivated` event so the audit log distinguishes
  "deactivated" from "members removed".
- **Project transfer (change `AppointingPartyId`).** This is
  a *very* security-sensitive mutation — every CBS line,
  document, RFI, audit row that scopes via
  `Project.AppointingPartyId` would suddenly cross tenants.
  Recommend not building a transfer endpoint without a
  deeper architectural review (likely needs explicit
  cross-tenant data export / import rather than an
  in-place pointer flip). When the requirement comes up,
  treat as ADR territory.

### SM-03 — `ProjectMember` has only an Add path **(None)**

**Status.** Information only.

The only `ProjectMember` mutation site is
`ProjectsService.AddMemberAsync`, which:

- Validates the new member's org against the project's
  `AppointingPartyId` (B-023 close).
- Re-activates and updates the role on an existing
  membership (idempotent re-add).
- Emits `project.member_added` structured audit event.

There is no `RemoveMemberAsync`, no `UpdateMemberRoleAsync`,
no `ProjectMembers.Remove(...)` call anywhere in production
code.

**Future obligation.** When member removal / role change
arrives, the developer must:

1. Call `RevokeUserTokensAsync(userId, tenant)` — a member
   whose project access changes mid-session shouldn't keep
   their old project-role claim. (The project role is
   loaded via `GetProjectRoleAsync` per request, not
   embedded in the JWT, so this is *belt-and-braces*; the
   per-request lookup will give the correct new role on the
   next call. But bumping the cutoff forces a fresh token
   issue and prevents any race where an old request lands
   after the change.) Actually, on reflection: the
   per-request lookup is already authoritative; adding a
   cutoff bump on member-role change is overkill for v1.0.
   This obligation should be reconsidered in light of the
   actual project-role caching strategy at v1.1 time.
2. Emit a structured audit event (`project.member_removed` /
   `project.member_role_changed`) with before/after
   role and an actor reference.

Marking the role-bump obligation as **soft** rather than
**hard** because the v1.0 design re-reads the project role
per request — there is no JWT-cached project role to
invalidate. If B-022's "JWT-embed per-user version stamp"
fix lands and the project role is denormalised onto the JWT,
the obligation becomes hard.

### SM-04 — No optimistic concurrency control **(Info)**

**Status.** Refinement, not a defect. Logged as **B-024**
for v1.1 / scale-out.

No entity in `CimsApp/Models/Entities.cs` has a
`[Timestamp]` rowversion column or an EF-configured
`IsConcurrencyToken()`. Two scenarios this matters for:

1. **Two PMs editing the same project budget.** PM A reads
   project at 14:00, opens edit form. PM B reads at 14:01,
   opens edit form. PM B saves at 14:02 with new budget X.
   PM A saves at 14:03 with new budget Y, oblivious to B's
   write — last-write-wins, B's change lost silently.
2. **Two assessors revising the same draft Payment
   Certificate.** Same shape — the second `UpdateAsync` call
   overwrites the first's `CumulativeValuation` /
   `RetentionPercent` without warning.

The `AuditInterceptor` would record both Updates (so the
loss is forensically reconstructable), but the user-facing
experience is silent data loss.

**Why deferred.** v1.0 internal pilot is single-tenant, low
concurrency. A rowversion column on every mutable entity
needs:

- Migration + entity decoration on Project, ProjectMember,
  CdeContainer, Document, DocumentRevision, Rfi, ActionItem,
  ProjectTemplate, CostBreakdownItem, Commitment, CostPeriod,
  ActualCost, Variation, PaymentCertificate (every entity
  with a UI edit path).
- ETag header round-trip in API responses + `If-Match`
  request handling.
- 409 mapping in `ErrorHandlingMiddleware` for
  `DbUpdateConcurrencyException`.
- UI-side conflict-resolution flow (browser-cached value
  vs server's current — at minimum a "your changes
  conflict, reload?" dialog).

**Estimate.** ~12-16h in a sprint. Premature for the v1.0
internal pilot; correct shape for v1.1 once concurrent users
become real.

## Conclusions

The secondary-mutation surface (Organisation, Project,
ProjectMember) is even smaller than the auth-mutation surface.
The only actual mutation site outside construction is
`AddMemberAsync`, which has the B-023 same-org guard and the
audit-twin event already.

Three dormant gaps captured as future obligations
(SM-01, SM-02, SM-03) — none is a present defect, all are
future-developer notes for the natural v1.1 admin-console
expansion. One refinement (SM-04) promoted to backlog as
B-024.

Combined with the auth-mutation audit, the v1.0 mutation
surface for security-sensitive entities is now fully
characterised:

- **User.GlobalRole / IsActive / PasswordHash** — see auth-mutation
  audit AM-01..AM-03.
- **Organisation.IsActive** — see SM-01.
- **Project.IsActive / AppointingPartyId** — see SM-02.
- **ProjectMember.Role / IsActive** — see SM-03.

Future endpoints touching any of these must check this pair
of audit docs as a checklist; the pattern of "mutate flag X
without informing downstream service Y" is exactly what the
audit framework is designed to catch.

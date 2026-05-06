# T-S22-04 — Synthetic UAT — 2026-05-06

The agent cannot run a 2-week human UAT, but it can drive a
**synthetic UAT** by walking the three training scripts via
PowerShell against the running dev server. Same pattern as the
post-S1 smoke walks (PRs #41-#44 era) that caught four real
latent bugs.

This session's synthetic UAT exercised:

## Walkthroughs run

1. **SuperAdmin walkthrough** (`docs/training/superadmin-walkthrough.md`)
   — bootstrap a new tenant + register first user + verify
   tenant isolation + audit log spot.
2. **Project Manager walkthrough**
   (`docs/training/projectmanager-walkthrough.md`) —
   create a project + register a document + transition
   WIP → Shared → Published + raise + respond to an RFI +
   audit trail review.
3. **Site User walkthrough** (`docs/training/siteuser-walkthrough.md`)
   — log a daily diary + raise an NCR. (Mobile-viewport
   verification skipped — the agent doesn't drive a real
   browser; the desktop API exercise covers the underlying
   contracts.)

Plus the S18 audit-and-compliance surfaces tested:

- Evidence library: bare-description add succeeded
- Audit export ZIP bundle: 3263 bytes, ZIP magic `PK`
  confirmed at the response.

## Findings

### FIXED IN THIS SESSION — Critical

**FINDING: DTO validation gap on bootstrap-org POST and other
public endpoints.** Over-long input to a `string` field with no
`[StringLength]` attribute on the DTO became a 500 (SQL "string
or binary data would be truncated", error 2628) instead of a
clean 400 ValidationException.

- **Reproducer**: `POST /api/v1/organisations` with
  `{"code": "UATACME9999"}` (11 chars; `Organisation.Code` is
  `[MaxLength(10)]`).
- **Pre-fix**: 500 with EF Core SqlException stack trace.
- **Post-fix**: 400 with body
  `{"success":false,"error":{"code":"VALIDATION_ERROR",...}}`.
- **Fix**: Added `[StringLength]` attributes to the public
  Create / Register / Login DTOs in `CimsApp/DTOs/Dtos.cs`
  matching the underlying entity `[MaxLength]` constraints.
  Coverage: `RegisterRequest`, `LoginRequest`, `CreateOrgRequest`,
  `CreateInvitationRequest`, `CreateProjectRequest`. Other DTOs
  deeper in the API surface should follow the same pattern in
  v1.1 if a similar finding surfaces — flagged as **B-127**.

This was a 100% latent bug in v1.0 ship territory — no test
covered it because unit tests construct DTOs directly (no
model-binder validation pipeline) and end-to-end tests didn't
exercise the over-long-field edge case. A hostile client (or
naive operator) could have produced 500s at will against the
anonymous bootstrap endpoint.

### NOT bugs (false positives surfaced during the walk)

1. **`Z1` Volume code rejected** by ISO 19650 validator —
   working as intended. Annex A-whitelist allows `ZZ`, `XX`,
   `01-05`, `A-E`. Adjusted test inputs.
2. **PowerShell 5.1 limitations** on binary-stream downloads
   (`Invoke-WebRequest` non-interactive mode quirk) —
   surfaced as test-script failures, not API failures. Used
   raw `[System.Net.WebRequest]` to confirm audit-export ZIP
   downloads cleanly (3263 bytes, magic = `PK`).

## What worked end-to-end (no findings)

- Bootstrap → Register → Login flow
- Tenant isolation (cross-tenant Org Update returned 404 with
  no existence leak; the post-S1 "no 403 reveals existence"
  pattern holds)
- SuperAdmin sees all 18 orgs; new OrgAdmin sees only their 1
- Project create + auto-assigned PM membership
- Document workflow: WIP → Shared → Published with state-machine
  role gates
- RFI raise + respond
- NCR raise (sequential `NCR-0001`)
- Daily Diary create + same-user-same-date 409 conflict
- Evidence library bare-description add
- Audit export ZIP bundle with manifest + per-row JSON

## State of v1.0 after synthetic UAT

The synthetic UAT is **NOT** a substitute for the formal 2-week
human UAT against a real pilot project — that's still a S22
external-execution gate. But it caught a real bug that would
have surfaced in the human UAT anyway, before it bit the
pilot operator.

**Recommendation:** ship the DTO validation fast-follower fix
to master before the human UAT begins. Done in this session
via PR (the regular sprint-branch flow).

## Open follow-ups for v1.1

- **B-127 — DTO validation sweep across remaining public
  endpoints.** The same pattern (`[StringLength]` matching
  entity `[MaxLength]`) should be applied to every Create*
  request DTO in the codebase, not just the five fixed in
  this session. ~10 more DTOs to audit.

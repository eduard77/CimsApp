# ADR-0016 — Blazor cookie auth alongside JWT bearer

**Status:** Accepted (2026-05-08, mid-Phase-3 smoke test).
**Supersedes:** —
**Related:** ADR-0010 (two-tier role authorization),
ADR-0014 (access-token residual-authority SLA), B-001
(token revocation), BUG-001 / BUG-007
(`docs/test-log/smoke-test-findings.md`),
`docs/diagnostics/render-mode-audit-2026-05-08.md`,
`docs/decisions/auth-persistence-implementation-plan-2026-05-08.md`.

## Context

CIMS shipped Phase 1–2 with Blazor Server's authentication state held
in a per-circuit scoped `UiStateService` populated by `Login.razor`
calling `AuthService.LoginAsync` and stashing the JWT in memory. The
Blazor side had no cookie scheme, no `AuthenticationStateProvider`, no
`<CascadingAuthenticationState>`. The JWT bearer scheme was wired only
for `/api/*` and `/hubs/*`.

The Phase-3 smoke test (`docs/test-log/smoke-test-findings.md`) exposed
the consequences:

- **BUG-001** — sidebar disappears on page reload.
- **BUG-007** — Client Organisation dropdown becomes non-functional
  after page reload.
- Intermittent "Uncaught Error: No interop methods are registered
  for renderer 1" cascade.

Root cause (audit §5): every full reload destroys the circuit; the
new circuit's scoped `UiStateService.AccessToken == null`; the layout
falls into the `!IsLoggedIn` branch; downstream API calls go out
unauthenticated and silently return `[]`. Identity is lost on every
F5.

## Decision

CIMS adopts a **cookie auth scheme for the Blazor request path**
alongside the existing JWT bearer scheme for the API and hub paths.
Identity becomes durable across circuits; the JWT becomes ephemeral
and re-mintable on demand.

### 1. Two schemes, selected by request path

- **JWT bearer scheme** — unchanged. Default for `/api/*` and
  `/hubs/*`. Token validation rules unchanged; the
  `OnTokenValidated` revocation hook (B-001 / ADR-0014 §2)
  continues to govern residual authority for API consumers.
- **Cookie scheme `CimsAuth`** — added. Default for everything
  else (the Blazor request surface). Cookie name `cims_auth`,
  attributes `HttpOnly`, `Secure`, `SameSite=Strict`,
  `IsEssential=true`. Lifetime: **8 hours sliding** (matches a
  working day; revalidation closes the "logged out everywhere"
  gap within the revalidation interval — see §3).

Scheme selection is path-based at the policy layer so that a single
HttpContext picks exactly one scheme per request and the two never
overlap. JWT-side endpoints never see the cookie; Blazor-side
requests never see the bearer.

### 2. Cookie carries the lean claim shape

The cookie's `ClaimsPrincipal` carries:

- `NameIdentifier` (`sub`) — user id.
- `cims:org` — organisation id.
- `cims:role` — global role (omitted when null, mirroring the JWT).

Display fields (email, first/last name, organisation name) are
**not** in the cookie. They come from a once-per-circuit
`AuthService.GetUserAsync(sub)` call. Rationale: matches the JWT
claim shape (one source of truth for "who is this user"); keeps
cookie size small; avoids stale display data after a name/role
change.

### 3. Custom AuthenticationStateProvider with revalidation

Blazor authentication state comes from a custom subclass of
`RevalidatingServerAuthenticationStateProvider`:

- `RevalidationInterval = 5 minutes`.
- `ValidateAuthenticationStateAsync` predicate mirrors the JWT
  validation rule in `Program.cs:89`: load `User` by `sub`;
  reject if `!IsActive` or if the cookie's `iat` is strictly less
  than `User.TokenInvalidationCutoff`.

This puts cookie sessions on the same residual-authority SLA as
JWT sessions (ADR-0014 §1: 5–60 minute upper bound). The 5-minute
interval is the lower bound and matches the policy ceiling on
"how long is a revoked session allowed to keep working."

### 4. JWT delivery to BlazorApiClient — server re-issues on demand

`BlazorApiClient` calls `/api/*` from inside the same process. To
keep the API surface contract (`Authorization: Bearer <jwt>`)
stable, the Blazor side mints a **short-lived (5 min) JWT on
demand** from the cookie principal's `sub`, via
`AuthService.GenerateAccess`. The JWT never leaves the server
process; it lives only on the outbound `HttpRequestMessage` from
`BlazorApiClient` and is reconstructed per call burst.

A scoped `IAccessTokenProvider` mediates: it caches the freshly
minted JWT for the circuit until the JWT is near expiry, then
re-mints. Token-revocation guarantees from ADR-0014 §1 still
apply because the JWT is short and the `OnTokenValidated` hook
runs on every API call.

### 5. Refresh tokens dropped from the Blazor flow only

The cookie *is* the long-lived credential for Blazor; access JWTs
are ephemeral and re-mintable on demand from the cookie principal.
There is no need for the Blazor flow to hold or rotate a refresh
token.

The `/api/v1/auth/refresh` endpoint and its tests
(`CimsApp.Tests/Services/Auth/RefreshTokenAuthTests.cs`) are
**unchanged**. Refresh tokens remain available for non-Blazor API
consumers (Swagger explorer, future external integrations,
federated-SSO scaffolding per ADR-0010).

### 6. Capture HttpContext principal once per circuit (C1)

`IHttpContextAccessor.HttpContext` is `null` after the Blazor
circuit attaches to SignalR — there is no HTTP request once the
circuit is running. The cookie principal must therefore be
**captured exactly once during the initial circuit connect** (via
a `CircuitHandler` or equivalent attach hook) and cached for the
circuit's lifetime. Freshness comes from the revalidation hook
hitting the database (§3), **not** from re-reading
`IHttpContextAccessor` on later calls. Anyone touching the
provider implementation should preserve this invariant.

## Consequences

- **BUG-001 and BUG-007 resolved.** Reload no longer destroys
  identity; sidebar persists; API calls retain authentication
  through the re-mint path.
- **One single source of truth for identity** — the cookie
  principal. `UiStateService` is demoted to ephemeral UI state
  only (`CurrentProject` and cosmetic preferences).
- **Revocation continues to work.** Revalidation predicate
  (§3) and `OnTokenValidated` (existing) both check the same
  `IsActive` / `TokenInvalidationCutoff` rules. ADR-0014's SLA
  upper bound of 60 minutes for residual authority still holds;
  the cookie path adds a 5-minute upper bound courtesy of the
  revalidation interval.
- **Cookie-based session adds a new revocable surface.**
  `LogoutAsync` and `LogoutEverywhere` now sign out the
  `CimsAuth` cookie via `HttpContext.SignOutAsync` in addition
  to their existing refresh-token sweeps. A logged-out cookie
  cannot be replayed (revocation predicate rejects on cutoff
  bump).
- **Browser cookie hardening is not optional.** `Secure` requires
  HTTPS in dev as well as prod (the existing `app.UseHttpsRedirection`
  guarantees this). `SameSite=Strict` prevents cross-site CSRF on
  the auth surface. `HttpOnly` blocks JS access (no XSS exfil
  vector).
- **No new top-level NuGet dependency.** The cookie handler
  (`Microsoft.AspNetCore.Authentication.Cookies`) is part of the
  ASP.NET Core shared framework already referenced by the project.
- **Federated SSO (ADR-0010 future)** is unaffected. An external
  IdP would issue tokens that the existing JWT scheme already
  handles; the cookie scheme added here is the local-credential
  path.

## Alternatives considered

- **Store the JWT in a second cookie** (Decision 1B in the
  implementation plan). Rejected: keeps a long-lived JWT in the
  browser (lifetime drift between cookie and JWT; revocation
  needs to clear the cookie too) for no real win — re-minting
  is cheap and revocation is cleaner.
- **Move BlazorApiClient to in-process service calls and drop
  the self-HTTP loop entirely.** Rejected for this ADR: bigger
  refactor (changes audit/logging behaviour, removes the HTTP
  middleware from the Blazor → API path). Tracked as a possible
  v1.1 cleanup.
- **Wide claim shape on the cookie** (Decision 3B in the plan).
  Rejected: cookie size grows; display fields go stale after a
  name change. The DB call per circuit-creation is cheap.
- **Keep prerender enabled** (Decision 4B). Rejected: SEO and
  perceived-load benefits are negligible for an authenticated
  internal app; the audit traced a class of races to the
  prerender-vs-circuit-attach window. Re-enable per page if a
  public marketing surface lands.
- **Drop refresh tokens system-wide** (Decision 2A/B). Rejected:
  the API surface still has non-Blazor consumers; removing
  refresh tokens there would require an unrelated breaking
  change to API clients.

## Amendment log

— (none yet)

# Auth-persistence implementation plan — 2026-05-08

Companion to `docs/diagnostics/render-mode-audit-2026-05-08.md`. Planning
only — no code changes in this session.

## 1. Summary

The audit's §6 fix shape is the only fix shape pursued: a cookie auth
scheme registered alongside JWT bearer, a custom
`AuthenticationStateProvider` hydrated from the cookie's
`ClaimsPrincipal`, `<CascadingAuthenticationState>` wrapping `<Routes>`,
and `UiStateService` demoted to non-auth UI state only. JWT delivery to
`BlazorApiClient` and refresh-token mechanics each have a real choice
to make — surfaced as Decisions 1 and 2 below — and prerender is
normalised once those land. Total effort ~L (one focused sprint slot);
no new top-level NuGet dependency required.

## 2. Implementation plan

Tasks listed in execution order. "Files touched" lists exact paths;
"why" is traceable to the audit unless noted.

### 2.1 — Record the decision (ADR)
- **Files:** new `docs/adr/ADR-0016-blazor-cookie-auth-alongside-jwt.md`.
- **Changes:** Capture: cookie scheme registered alongside JWT bearer,
  scheme selection by request path, `AuthenticationStateProvider`
  shape, claim shape, lifetime, what is *not* done (no FE refresh
  cookie if Decisions 1A/2C land).
- **Why:** CLAUDE.md "no new top-level dependencies without an ADR."
  Cookie auth is a new authentication scheme even though the assembly
  ships with ASP.NET Core.
- **Risk:** none.
- **Tests:** none.

### 2.2 — Register cookie auth scheme + DI plumbing
- **Files:** `CimsApp/Program.cs`.
- **Changes:** Chain a cookie scheme onto `AddAuthentication` (or
  switch the default scheme handler so JWT bearer applies to
  `/api/*` and `/hubs/*` and cookie applies to everything else).
  Add `services.AddCascadingAuthenticationState();` and register the
  custom `AuthenticationStateProvider` from §2.3 as scoped.
  Cookie attributes: `HttpOnly`, `Secure`, `SameSite=Strict`,
  `IsEssential=true`, lifetime per Decision 4.
- **Why:** Audit §6 — cookie scheme is what makes identity survive
  reloads; Blazor requires `AddCascadingAuthenticationState` plus a
  registered provider to flow `AuthenticationState`.
- **Risk:** medium-high — scheme selection is the most error-prone
  part. A wrong default scheme can either break API auth (JWT no
  longer evaluated) or open Blazor pages to anonymous traffic.
- **Tests:** add an integration test that exercises both: a `GET
  /api/v1/auth/me` with bearer token still returns 200; a Blazor
  request without a bearer but with a cookie returns the rendered
  page. Existing `RefreshTokenAuthTests` must still pass.

### 2.3 — Custom `AuthenticationStateProvider`
- **Files:** new `CimsApp/UI/CookieAuthenticationStateProvider.cs`
  (subclass `RevalidatingServerAuthenticationStateProvider`).
- **Changes:** On `GetAuthenticationStateAsync`, return the
  `ClaimsPrincipal` from the cookie via `IHttpContextAccessor`.
  Override `RevalidationInterval` (e.g. 5 min) and
  `ValidateAuthenticationStateAsync` to: load the user by `sub`,
  reject if `!IsActive` or if `iat < TokenInvalidationCutoff`
  (mirrors the existing JWT rule in `Program.cs:89` /
  ADR-0014).
- **Why:** Audit §6 — Blazor needs a provider to get a principal;
  revalidation closes the "I logged out everywhere on another tab"
  gap for long-lived circuits.
- **Risk:** medium — the revalidation hook holds a DB connection per
  circuit per interval; choose interval and shape carefully.
- **Tests:** unit test the revalidation predicate with three cases:
  active user, inactive user, user with cutoff after the cookie's
  iat.

### 2.4 — `AuthController.Login` issues the cookie
- **Files:** `CimsApp/Controllers/Controllers.cs` (the
  `Login` action at L71-73).
- **Changes:** After `svc.LoginAsync(...)` succeeds, build a
  `ClaimsPrincipal` with claims per Decision 3 and call
  `HttpContext.SignInAsync(<cookie scheme>, principal,
  authProps)`. Auth cookie carries identity only; whether the
  JWT also lives in a cookie depends on Decision 1.
- **Why:** Audit §6 — login is the only existing place where the
  durable identity gets seeded.
- **Risk:** medium — claim drift between cookie and JWT means the
  two views of the user diverge. Mitigate by deriving both from one
  source (the `User` row).
- **Tests:** new controller test asserts the response carries a
  `Set-Cookie: <cookie name>=…; HttpOnly; Secure; SameSite=Strict`
  header on a successful login, and does not on a 401.

### 2.5 — `AuthController.Logout` and `LogoutEverywhere` clear the cookie
- **Files:** `CimsApp/Controllers/Controllers.cs` (`Logout` at
  L79-81 and `LogoutEverywhere` at L98-104).
- **Changes:** Call `HttpContext.SignOutAsync(<cookie scheme>)` in
  both. `LogoutEverywhere` retains its existing cutoff bump.
- **Why:** Symmetry — without sign-out the cookie outlives the user's
  intent and the next reload silently logs them back in.
- **Risk:** low.
- **Tests:** controller tests assert the response carries a
  `Set-Cookie: <cookie name>=; expires=…past…` clearing header.

### 2.6 — `App.razor` cascading auth + `HeadOutlet`
- **Files:** `CimsApp/Components/App.razor`.
- **Changes:** Wrap `<Routes>` in `<CascadingAuthenticationState>`.
  Add `<HeadOutlet @rendermode="@RenderMode.InteractiveServer" />`
  inside `<head>`.
- **Why:** Audit §6 (cascade) and §3 mismatch #3 (`HeadOutlet`
  missing).
- **Risk:** low.
- **Tests:** none (behavioural).

### 2.7 — `Routes.razor` becomes auth-aware
- **Files:** `CimsApp/Components/Routes.razor`.
- **Changes:** Replace `<RouteView>` with `<AuthorizeRouteView>`.
  Add `<NotAuthorized>` template that triggers a navigation to `/`.
- **Why:** Without this, an unauthenticated request to e.g.
  `/projects` still tries to render the page; AuthorizeRouteView
  short-circuits to login before the page's `OnInitializedAsync`
  fires its API calls.
- **Risk:** medium — `Login.razor` uses `EmptyLayout`; verify the
  NotAuthorized redirect path doesn't render the wrong layout.
- **Tests:** smoke test step 1 in §6 below covers this.

### 2.8 — `MainLayout.razor` reads identity from the cascade
- **Files:** `CimsApp/Components/Layout/MainLayout.razor`.
- **Changes:** Replace `@if (!UI.IsLoggedIn)` with an
  `<AuthorizeView>` (or `Task<AuthenticationState>`-cascaded
  parameter). User name / initials / org come from claims (cookie
  carries them per Decision 3, or are looked up once via
  `AuthService.GetUserAsync` and cached for the circuit).
- **Why:** Audit §5 — sidebar visibility is the primary user-visible
  symptom of state loss.
- **Risk:** low-medium — many cosmetic touch-points (initials,
  name, org name).
- **Tests:** behavioural; bUnit-style component test optional.

### 2.9 — `Login.razor` triggers full-page navigation post-login
- **Files:** `CimsApp/Components/Pages/Login.razor`.
- **Changes:** Drop the `UI.SetLogin(...)` call. After
  `AuthSvc.LoginAsync(...)` succeeds, call
  `Nav.NavigateTo("/dashboard", forceLoad: true)`. Drop the
  `OnInitialized` `IsLoggedIn` redirect (becomes
  cascade-based).
- **Why:** The cookie is set on the HTTP response of the login POST.
  A SPA navigation does not start a new request, so the next
  circuit will not see the cookie until something forces a real
  HTTP round-trip. `forceLoad: true` is the simplest guarantee.
- **Risk:** medium — UX flash on login. Acceptable for v1; revisit
  if it grates.
- **Tests:** behavioural.

### 2.10 — `BlazorApiClient` gets JWT from a durable source
- **Files:** `CimsApp/UI/BlazorApiClient.cs`.
- **Changes:** Replace the `state.AccessToken` read in `Http()` with
  whatever Decision 1 chooses. If 1A (server re-issue): inject a
  scoped `IAccessTokenProvider` that mints a short-lived JWT on
  demand from the cookie principal's `sub` via `AuthService`.
  If 1B (JWT in second cookie): read the cookie via
  `IHttpContextAccessor`.
- **Why:** Audit §6 — `UiStateService.AccessToken` stops being the
  source of truth.
- **Risk:** medium — token-lifecycle bugs are subtle (expired token
  used silently, no refresh path).
- **Tests:** unit test on the `Http()` factory: returns a header on
  authenticated state, returns no header (or 401-routing
  appropriately) on unauthenticated state.

### 2.11 — `UiStateService` demoted to UI state
- **Files:** `CimsApp/UI/UiStateService.cs`, plus call sites in
  every `*.razor` that reads `UI.UserFullName` / `UI.UserInitials` /
  `UI.OrgName` / `UI.IsLoggedIn` / `UI.AccessToken` (audit-flagged
  sites: `MainLayout`, `Dashboard`, `Login`, `Audit`, `Actions`,
  `Rfis`, `Templates`).
- **Changes:** Remove `AccessToken`, `UserId`, `UserFullName`,
  `UserInitials`, `OrgName`, `IsLoggedIn`, `SetLogin`, `Logout`.
  Keep `CurrentProject`, `OnChange`, `SetProject`. Update call
  sites: identity reads come from the cascaded
  `AuthenticationState`; logout becomes a `Nav.NavigateTo` to a
  POST `/logout` form that signs out and redirects.
- **Why:** Audit §6 — single source of truth.
- **Risk:** medium — call-site sweep is wide. Many cosmetic strings
  to re-source.
- **Tests:** existing `UiStateService` tests pruned; new minimal
  test covers `CurrentProject` only.

### 2.12 — Normalise prerender
- **Files:** `CimsApp/Components/App.razor` (Routes mount), and
  remove now-redundant `@rendermode` from
  `CimsApp/Components/Pages/Login.razor`,
  `CimsApp/Components/Pages/Documents.razor`,
  `CimsApp/Components/Pages/DocumentRegister.razor`,
  `CimsApp/Components/Pages/Projects.razor`,
  `CimsApp/Components/Pages/Tools/Iso19650Validator.razor`.
- **Changes:** Apply Decision 4's chosen setting once at the Routes
  mount; remove all per-page `@rendermode` lines so there is one
  declared setting in the codebase.
- **Why:** Audit §3 mismatches #1, #2 and §5's "compounding it"
  paragraph.
- **Risk:** low. Order matters — do this after §2.6 / §2.10 so
  cookie-driven hydration is in place before prerender behaviour
  changes.
- **Tests:** behavioural via §6 smoke steps.

### 2.13 — Test sweep
- **Files:** `CimsApp.Tests/Services/Auth/*` (existing),
  new tests for the `AuthenticationStateProvider` revalidation,
  new controller tests for cookie set/clear.
- **Changes:** Update any `LoginAsync` test that asserts response
  shape; existing tests in `RefreshTokenAuthTests` and
  `AuthServiceInputValidationTests` should not regress.
- **Risk:** low-medium.
- **Tests:** the tests themselves.

## 3. Decisions required

Four decisions. Recommended option named on the same line.

### Decision 1 — How does `BlazorApiClient` get a JWT to call `/api/*`?
- **A — Server re-issues JWT on demand.** A scoped
  `IAccessTokenProvider` mints a short-lived (~5 min) JWT from the
  cookie principal's `sub` via `AuthService.GenerateAccess`. JWT
  never lives in the browser. Trade-off: extra mint per access
  burst, but matches existing revocation flow.
- **B — Store JWT in a second HttpOnly cookie at login.**
  `BlazorApiClient` reads it via `IHttpContextAccessor`. Trade-off:
  no extra mint, but the JWT now has the same lifetime as a cookie
  (or expires mid-session) and revocation requires clearing the
  cookie too.
- **Recommendation: A.** Smaller browser attack surface; revocation
  via existing `TokenInvalidationCutoff` mechanic just works; no
  cookie-vs-JWT lifetime drift to reason about. The mint cost is
  trivial against the existing self-HTTP overhead.

### Decision 2 — Refresh-token mechanics for the Blazor flow
- **A — Set refresh token in a third HttpOnly cookie at login.**
  `/auth/refresh` consumes the cookie, mints a new pair, sets the
  new cookie. Browser-driven; Blazor unaware. Trade-off: three
  cookies, browser-side complexity.
- **B — Keep refresh tokens server-side for the Blazor flow,
  hydrate from cookie principal's `sub`.** Trade-off: works, but
  duplicates the refresh-token state machine for one client.
- **C — Drop refresh tokens from the Blazor flow entirely.** The
  cookie *is* the long-lived credential; access JWTs are short and
  re-mintable on demand from the cookie principal. Refresh tokens
  remain for the API surface (Swagger, future non-Blazor clients).
  Trade-off: behavioural divergence between Blazor and API
  consumers.
- **Recommendation: C.** Combines cleanly with Decision 1A: the
  cookie is the long-lived identity, JWTs are ephemeral and
  generated per-call, refresh tokens stay in the API for clients
  that need them. Net reduction in moving parts for the FE.

### Decision 3 — Claim shape on the auth cookie
- **A — Mirror the JWT.** `sub`, `cims:org`, `cims:role` only.
  Display fields (name, email, org name) come from a one-shot
  `AuthService.GetUserAsync` per circuit.
- **B — Wide claim set.** Add `email`, `given_name`, `family_name`,
  org name to the cookie. No DB call needed for sidebar render.
  Trade-off: cookie size grows; display fields go stale until
  next login.
- **Recommendation: A.** Matches the existing JWT shape (one
  source of truth for "who is this user"), keeps cookie small,
  and `GetUserAsync` is already paid for by `/auth/me` consumers.
  The DB call per circuit-creation (not per render) is cheap.

### Decision 4 — Prerender disposition
- **A — Disable globally.** Set `prerender: false` once at the
  Routes mount; remove every per-page `@rendermode`.
  Trade-off: no SEO benefit; first paint is the loading shell.
- **B — Keep prerender on, make `AuthenticationStateProvider`
  prerender-safe.** All pages prerender consistently; the provider
  reads the cookie during prerender and renders authenticated
  output server-side. Trade-off: more careful provider
  implementation; SEO and first-paint benefits.
- **Recommendation: A.** This is an authenticated internal app —
  prerender's perceived-load benefit is negligible vs the class
  of races we just diagnosed. Re-enable per-page later if a
  public marketing surface lands.

## 4. Out of scope

Explicitly deferred to other prompts:

- **BUG-006 — `POST /api/v1/projects` returns 403.** Cause is the
  seed user lacks `OrgAdmin` / `SuperAdmin` (audit §4). Fix is
  either a seed-data change or a policy loosen. Audit §6's last
  bullet calls this out as separate.
- **BUG-003 — Guid.Empty placeholder.** Per task prompt.
- **BUG-005 — Sector free text → controlled list.** Per task
  prompt.
- **Federated SSO.** ADR-0010 / B-009 already noted as future
  work; the cookie scheme designed here is compatible but no SSO
  wiring is added now.
- **Removal of refresh tokens system-wide.** If Decision 2C is
  taken, refresh tokens are dropped only from the Blazor flow;
  the `/auth/refresh` endpoint and its tests stay.
- **HTTPS dev cert / HSTS hardening.** Cookie's `Secure` flag
  assumes the existing dev-cert setup works. If the smoke-test
  user runs the app over plain HTTP for any reason, raise
  separately.
- **Migration of existing in-flight sessions.** No live users
  yet; no migration plan needed.
- **Smoke-test findings log Resolution blocks for BUG-001 and
  BUG-002 etc.** Bookkeeping; separate session.

## 5. Effort estimate

| Task                                            | Size |
|-------------------------------------------------|------|
| 2.1  ADR                                        | XS   |
| 2.2  Program.cs auth scheme + DI                | M    |
| 2.3  AuthenticationStateProvider                | M    |
| 2.4  AuthController.Login cookie issue          | S    |
| 2.5  AuthController.Logout cookie clear         | XS   |
| 2.6  App.razor cascading + HeadOutlet           | XS   |
| 2.7  Routes.razor AuthorizeRouteView            | S    |
| 2.8  MainLayout.razor identity from cascade     | S    |
| 2.9  Login.razor forceLoad                      | XS   |
| 2.10 BlazorApiClient JWT source change          | M    |
| 2.11 UiStateService demote + call-site sweep    | S    |
| 2.12 Normalise prerender                        | XS   |
| 2.13 Test sweep                                 | M    |

**Total: L** — one focused sprint slot, sized for a solo developer.
The medium-sized items cluster around scheme registration
(2.2, 2.3, 2.10) — those three carry the implementation risk and
should be sequenced first.

## 6. Verification steps (post-implementation)

Run after the branch builds and tests are green. All steps assume
fresh login as Eduard / `admin@adi.com`.

1. Navigate to `https://localhost:55069/projects` from a logged-out
   browser — expected: redirect to `/`. (Currently: empty page,
   no sidebar.)
2. Log in — expected: land on `/dashboard` with sidebar visible
   and user initials/name/org rendered.
3. Press F5 on `/dashboard` — expected: sidebar still present,
   identity unchanged. (BUG-001.)
4. Navigate to `/projects`, press F5 — expected: sidebar present,
   projects list populated. (BUG-001 reload-on-list-page.)
5. After step 4, click "+ New Project" — expected: dialog opens,
   Client Organisation dropdown lists organisations, selection
   works. (BUG-007.)
6. Open DevTools, toggle "Preserve log" on the Network tab,
   press F5 again, repeat step 5 — expected: still works, no
   "renderer 1" console error.
7. Click Sign out — expected: land on `/`. Press F5 on `/` —
   expected: still on `/`, no auto-login (cookie cleared).
8. With DevTools, manually delete the auth cookie and reload
   `/projects` — expected: redirect to `/`.

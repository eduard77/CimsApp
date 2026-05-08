# Render-mode and circuit-lifecycle audit — 2026-05-08

Diagnostic-only. Read-only. No code edits in this session.

Scope of inspection: `App.razor`, `Routes.razor`, `_Imports.razor`,
`Components/Layout/MainLayout.razor`, `Components/Layout/EmptyLayout.razor`,
all `Components/Pages/*.razor`, `Program.cs`, `UI/UiStateService.cs`,
`UI/BlazorApiClient.cs`, and the `[Authorize]` attribute on
`POST /api/v1/projects` (`Controllers/Controllers.cs`). No JS-interop usages
exist in any component (`grep` for `OnAfterRenderAsync` / `IJSRuntime` /
`InvokeVoidAsync` / `InvokeAsync` returns only post-load `InvokeAsync`
re-renders, no direct JS calls).

---

## 1. Render-mode topology

`App.razor` mounts `<Routes>` with `@rendermode="@RenderMode.InteractiveServer"`
(default = prerender **true**). There is no `<HeadOutlet>`. Page-level
`@rendermode` directives override the Routes-level one for that page.

| Component / page                    | File                                                   | Declared `@rendermode`                                | Effective prerender |
|-------------------------------------|--------------------------------------------------------|-------------------------------------------------------|---------------------|
| `App` (host)                        | `Components/App.razor`                                 | n/a (static host)                                     | n/a                 |
| `Routes`                            | `Components/App.razor` (mount), `Components/Routes.razor` (def) | `RenderMode.InteractiveServer` (default prerender=true) at mount | true (default)      |
| `MainLayout`                        | `Components/Layout/MainLayout.razor`                   | inherited                                             | inherits Routes     |
| `EmptyLayout`                       | `Components/Layout/EmptyLayout.razor`                  | inherited                                             | inherits Routes     |
| `Login` (`/`)                       | `Components/Pages/Login.razor`                         | `new InteractiveServerRenderMode(prerender: false)`   | **false**           |
| `Dashboard` (`/dashboard`)          | `Components/Pages/Dashboard.razor`                     | inherited                                             | true                |
| `Projects` (`/projects`)            | `Components/Pages/Projects.razor`                      | `RenderMode.InteractiveServer` (page-level)           | true                |
| `Templates` (`/projects/{id}/templates`) | `Components/Pages/Templates.razor`                | inherited                                             | true                |
| `Documents` (`/documents`)          | `Components/Pages/Documents.razor`                     | `new InteractiveServerRenderMode(prerender: false)`   | **false**           |
| `DocumentRegister` (`/documents/register`) | `Components/Pages/DocumentRegister.razor`       | `new InteractiveServerRenderMode(prerender: false)`   | **false**           |
| `Rfis` (`/rfis`)                    | `Components/Pages/Rfis.razor`                          | inherited                                             | true                |
| `Actions` (`/actions`)              | `Components/Pages/Actions.razor`                       | inherited                                             | true                |
| `Audit` (`/audit`)                  | `Components/Pages/Audit.razor`                         | inherited                                             | true                |
| `Iso19650Validator` (`/tools/iso19650-validator`) | `Components/Pages/Tools/Iso19650Validator.razor` | `new InteractiveServerRenderMode()` (default prerender=true) | true |

No `[StreamRendering]`, no `[RenderMode]` C# attributes, no `<HeadOutlet>`,
no `<CascadingAuthenticationState>`, no `AuthenticationStateProvider`,
no `PersistentComponentState`, no global `@rendermode` in `_Imports.razor`.

---

## 2. `Program.cs` configuration

Render-mode services and pipeline (line numbers from `CimsApp/Program.cs`):

- L150–151 — `builder.Services.AddRazorComponents().AddInteractiveServerComponents();`
- L152 — `builder.Services.AddMudServices();`
- L287–288 — `app.MapRazorComponents<App>().AddInteractiveServerRenderMode();`
- L277 — `app.UseAntiforgery();` (in pipeline, before `MapRazorComponents`)

**Not present:**

- `AddInteractiveWebAssemblyComponents` — none.
- `AddInteractiveWebAssemblyRenderMode` — none.
- `AddAuthenticationStateProvider` (or any `IAuthenticationStateProvider` registration) — none.
- Cookie auth scheme — none. `AddAuthentication` registers JWT bearer only (L28–95).
- `AddCascadingAuthenticationState` — none.
- `app.UseStaticFiles` is present (L276) — informational.

No commented-out render-mode lines.

`UiStateService` is registered **scoped** (L252):
`builder.Services.AddScoped<UiStateService>();`. It holds `AccessToken`,
`UserId`, `UserFullName`, `OrgName`, `CurrentProject` purely in memory.
There is no persistence (no cookie, no localStorage, no
`PersistentComponentState`).

`BlazorApiClient` (also scoped, L253) reads `UiStateService.AccessToken`
on every call and only attaches the `Authorization: Bearer …` header if
the token is non-null (`UI/BlazorApiClient.cs:15`).

---

## 3. Mismatches

| # | Location                                   | Mismatch                                                                                                                          |
|---|--------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|
| 1 | `Components/Pages/Projects.razor:6`        | Page-level `@rendermode RenderMode.InteractiveServer` is redundant — Routes already declares the same mode. Same boundary, no real harm, but it adds a nested rendermode token. |
| 2 | Routes vs Login/Documents/DocumentRegister | Mixed prerender setting. Routes (and inherited pages) prerender; three pages disable prerender. Inconsistent — see §5.            |
| 3 | `Components/App.razor`                     | No `<HeadOutlet>`. Any `<PageTitle>` / `<HeadContent>` from a page is silently dropped. Cosmetic only, but a topology gap.        |
| 4 | `Components/App.razor`                     | `_content/MudBlazor/MudBlazor.min.js` is placed **after** `_framework/blazor.web.js` in `<body>`. Order is correct; noted only because it is the boundary at which MudBlazor's interop registers against the Blazor renderer. Any race between the script load and the first interactive render lands here. |
| 5 | `UI/UiStateService.cs` + `Program.cs:252` (scoped) | The single source of truth for `IsLoggedIn` and the JWT is per-circuit. A circuit ends on every full page reload, navigation that breaks SignalR (e.g. forced reload with DevTools toggling network), and on connection loss past the reconnect window. There is no static or persistent ancestor — interactive components with auth-dependent rendering sit inside what is effectively a static-on-cold-load DI graph. |
| 6 | All pages except Login                     | Pages that depend on `UI.IsLoggedIn` / `UI.AccessToken` to render correctly (sidebar in `MainLayout`, dropdowns in `Projects.razor`) prerender with no token, then re-render with no token, every time a circuit is created. |

There is no static-rendered ancestor wrapping an interactive descendant
in the strict Blazor sense (everything inherits Interactive Server). The
mismatch that matters is not topological — it is **temporal**: scoped
auth state vs the actual lifetime of the user's logical session.

---

## 4. Auth interaction with circuit creation

Blazor itself is not authenticated. The pipeline has:

- JWT bearer auth scheme on `/api/*` and `/hubs/*` only.
- `app.UseAuthentication(); app.UseAuthorization();` in the pipeline,
  but no `AuthenticationStateProvider` is wired to Blazor's
  authorization system.
- No cookie scheme, no `[Authorize]` on any Razor component, no
  `<CascadingAuthenticationState>` in `Routes.razor`.

The user's identity becomes available to a circuit only after this
sequence:

1. User loads `/` → circuit A starts with prerender disabled (Login.razor).
2. User submits credentials → `AuthService.LoginAsync` posts to
   `/api/v1/auth/login` and receives a JWT.
3. `UI.SetLogin(...)` stores the JWT in **circuit A's** `UiStateService`.
4. SPA navigation to `/dashboard` reuses circuit A — token survives.

On page reload from any path:

1. Browser sends a fresh HTTP request with no cookies (none are set).
2. Server prerenders the requested page in a *new* DI scope (call this
   scope P). `UiStateService(P).AccessToken == null`.
3. Blazor attaches a SignalR circuit (circuit B) with another *new*
   DI scope. `UiStateService(B).AccessToken == null`.
4. `MainLayout` evaluates `UI.IsLoggedIn` → `false` →
   the `else` branch with the sidebar is not rendered; only `@Body`
   shows. The page below renders, then its `OnInitializedAsync`
   calls `BlazorApiClient` with no Authorization header → API returns
   401, the catch block in `BlazorApiClient` returns `[]`, the page
   shows empty data.
5. `MudSelect` bound to `_f.OrgId` (a `Guid`, default `Guid.Empty`) with
   an empty `_orgs` list has nothing to display, and no popover opens
   when the user clicks — exactly BUG-007's signature.

So: **identity is not available to the circuit on first connect, and
nothing in the system makes it available later** without the user
manually navigating back to `/` and signing in again.

`POST /api/v1/projects` carries `[Authorize(Roles = "OrgAdmin,SuperAdmin")]`
(`Controllers/Controllers.cs:240`). The role claim is read from `cims:role`
(`Program.cs:49`). BUG-006's ranked cause #1 in the findings log — "seed
dev user lacks required role" — is consistent with this and is the most
likely explanation; it is a separate issue from render-mode topology and
would not be cured by anything in §5/§6.

---

## 5. Likely root cause

Authentication state is held only in a per-circuit scoped service
(`UiStateService`) with no persistence — no cookie, no localStorage, no
`PersistentComponentState`, no `AuthenticationStateProvider` — so every
full page reload kills the circuit, the new circuit starts with
`AccessToken == null`, and the layout's `IsLoggedIn` branch falls back
to no-sidebar while every API call goes out unauthenticated; this
single fact explains BUG-001 (sidebar lost on reload), the reload-related
half of BUG-007 (dropdown can't open because `GetOrgsAsync` silently
returns `[]` on a 401 caught in the catch-all), and the cascade
"renderer 1" / `ObjectDisposedException` symptoms when the dead circuit's
DOM and a new circuit's renderer briefly coexist. Compounding it,
prerender is **on by default for Routes** but disabled on three pages
(`Login`, `Documents`, `DocumentRegister`), so every other page's
interactive lifecycle is "prerender in scope P → mount circuit in scope
B," doubling the windows in which auth-dependent rendering races against
state initialisation. BUG-006 is a separate authorization-data issue
(seed user lacks `OrgAdmin` / `SuperAdmin` global role required by the
Create endpoint) and is unaffected by render-mode topology.

---

## 6. Proposed fix sketch (one fix only)

Make authentication state durable across circuits, then normalise
prerender as a follow-on of that work:

- **Issue an authentication cookie on successful login** alongside the
  JWT response. Cookie carries `sub` (user id) and the global role claim
  (`cims:role`); marked `HttpOnly`, `Secure`, `SameSite=Strict`. Lifetime
  matches or is shorter than the JWT.
- **Register a cookie auth scheme** in `Program.cs` (in addition to
  JWT bearer), wired only for the Blazor request path — JWT bearer
  remains the scheme on `/api/*` and `/hubs/*`.
- **Add a custom `AuthenticationStateProvider`** that reads the cookie
  via `IHttpContextAccessor` on first attach and exposes the resulting
  `ClaimsPrincipal` to Blazor. Register it scoped (per-circuit, but
  hydrated from a cross-request source).
- **Wrap `<Routes>` in `<CascadingAuthenticationState>`** in
  `Components/App.razor` and add `<HeadOutlet @rendermode="…" />` while
  there.
- **Persist (or rebuild) the JWT for `BlazorApiClient`.** Two viable
  shapes (decide separately): (a) re-issue a fresh short-lived JWT to
  the new circuit from a server-side store keyed on the cookie's user
  id, or (b) keep the JWT in a second cookie and read it in the
  `BlazorApiClient.Http()` factory. Either works; the point is that
  `UiStateService` stops being the source of truth.
- **`UiStateService` becomes ephemeral UI state only** (selected project,
  cosmetic preferences) — no auth fields. `MainLayout` reads
  `IsLoggedIn` from the cascaded auth state, not from `UiStateService`.
- **Normalise prerender across pages.** Pick one setting — almost
  certainly `prerender: false` until a public/anonymous surface needs
  prerender — and apply it once (either on `<Routes>` or via
  `_Imports.razor` `@rendermode`). Drop the page-level redundant
  `@rendermode` on `Projects.razor`.
- **Confirm BUG-006 separately** by inspecting the seed data: give the
  smoke-test user the `OrgAdmin` (or `SuperAdmin`) global role, OR
  loosen the policy on `POST /api/v1/projects`. This work is
  unrelated to the auth-persistence fix and should be sized
  independently.

No code is changed in this session. The above is a sketch for the
follow-up planning prompt.

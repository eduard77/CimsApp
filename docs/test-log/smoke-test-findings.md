# Smoke Test Findings

## Findings

### BUG-003 — Client Organisation dropdown displays Guid.Empty as selected value

- **Phase / Step:** Phase 3, Step 17 (New Project dialog inspection, pre-fill)
- **Severity:** medium (cosmetic but confusing; suggests broken default-state binding)
- **Status:** open
- **Area:** projects, dialog, forms

**Repro:**
1. Click + New Project on /projects.
2. Observe Client Organisation field shows "00000000-0000-0000-0000-000000000000".

**Expected:** Empty placeholder text (e.g. "Select client organisation") until user makes a choice.
**Actual:** Raw Guid.Empty rendered as if it were a valid selection.

**Likely cause:** Field bound to Guid value with no null-handling in display template, or default initialised to Guid.Empty with no placeholder fallback.

**Acceptance criteria:**
- Empty/unset state renders placeholder text, never a Guid.
- Required-field validation on submit rejects Guid.Empty as a non-selection.
- UI test asserts placeholder visible on initial render.

---

### BUG-005 — Sector is a free-text field; should be a controlled list

- **Phase / Step:** Phase 3, Step 17
- **Severity:** medium (data quality; not a functional defect today)
- **Status:** open
- **Area:** projects, taxonomy

**Detail:** Sector accepts arbitrary free text. Per PAFM v2.0 and the future
public-sector reporting / framework-application requirements, sector must be
a controlled vocabulary so projects can be aggregated and filtered consistently.

**Acceptance criteria:**
- Sector field becomes a dropdown sourced from a Sectors lookup table.
- Initial seed list (proposal): Healthcare, Education, Defence, Pharmaceutical,
  Life Sciences, Residential, Commercial, Industrial, Civils / Infrastructure,
  Energy, Heritage. Final list to be confirmed.
- Existing free-text values migrated where possible; unmatched flagged for review.
- Admin Console (Sprint 14) edits the list; no per-project additions.

---

### BUG-006 — Project creation returns HTTP 403 Forbidden

- **Phase / Step:** Phase 3, Step 17 (Test B)
- **Severity:** critical — blocks Phase 3 onwards
- **Status:** investigating
- **Area:** projects, api, authorization

**Endpoint:** POST https://localhost:55069/api/v1/projects
**Response:** 403 in ~35ms (no exception thrown server-side; rejected by auth pipeline)

**Repro:** Fill New Project form with valid data (Project Name, Project Code,
Location, Sector, Budget Value, Client Organisation = Genera Systems); click Create.

**Expected:** 201 Created, project appears in /projects list.
**Actual:** 403 Forbidden. Toast "Failed to create project". Client-side
JsonReaderException is downstream symptom of empty 403 body, not a separate defect.

**Diagnostic data still to collect:**
- JWT payload claims (roles, tenant_id, permissions) — decode at jwt.io.
- ProjectsController [Authorize(...)] attribute on the Create action.
- Response headers (any WWW-Authenticate / error detail).

**Likely causes (ranked):**
1. Seed dev user lacks required role (e.g. ProjectManager / TenantAdmin).
2. JWT missing tenant_id or other required claim.
3. Anti-forgery / policy mismatch on POST.

**Acceptance criteria:**
- Authenticated dev user (Eduard, on Genera Systems tenant) can create a project end-to-end.
- Authorisation requirements documented in code (XML doc on the action) and in the PAFM role matrix.
- Negative test: a user without the required role receives 403, not 500, and a clear error body.
- Integration test covering the happy path and the role-denied path.

---

### BUG-007 — Client Organisation dropdown becomes non-functional after page reload

- **Phase / Step:** Phase 3, Step 17 (Test B retry with DevTools open)
- **Severity:** high (intermittent — blocks project creation when triggered)
- **Status:** open
- **Area:** projects, dialog, dropdowns, state

**Repro:**
1. Open + New Project dialog. Confirm dropdown works.
2. Cancel or interact with the dialog.
3. Reload the page (F5 / Ctrl+R) with DevTools Network tab open.
4. Open + New Project dialog again.
5. Try to select Genera Systems (GENERA) from Client Organisation.

**Expected:** Dropdown opens, options load, selection works as on first open.
**Actual:** Dropdown shows the Guid.Empty placeholder and does not open
or allow selection. Submit then fails (separately also blocked by BUG-006).

**Possible causes (unconfirmed):**
- Client organisations list loaded once on app boot; reload destroys
  client-side state but server cache not repopulating per-circuit.
- DevTools throttling/preserve-log toggling the WebSocket lifecycle.
- Disposed-circuit residue from earlier failures (ObjectDisposedException
  observed previously in BUG-002 cascade).

**Diagnostic data still to collect:**
- VS Output: any errors when the dropdown fails to open.
- Network tab: is there a GET for organisations on dialog open? Status code?
- Does Ctrl+Shift+R (hard refresh) restore it, or only an app restart?

**Acceptance criteria:**
- Dropdown loads and allows selection on every dialog open, regardless of
  how many page reloads have occurred.
- Reference data (organisations, sectors, contract forms) cached at the
  appropriate scope and refreshed on circuit creation.
- UI test asserts dropdown is interactive after a forced reload sequence.

---

### BUG-008 — Client throws JsonReaderException on non-JSON 403/401 responses

- **Severity:** low
- **Status:** open — defer to error-handling polish sprint
- **Area:** client API error handler
- **Reproduction:** Trigger any 403 or 401 from the API. Console shows
  System.Text.Json.JsonReaderException; UI shows generic "Failed to create
  project" toast instead of the actual reason.
- **Fix sketch:** In the API client wrapper, check response.Content.Headers.ContentType
  before deserializing. If not application/json, surface response.StatusCode and
  ReasonPhrase to the toast instead of attempting JSON parse.

> **Numbering note:** Logged as BUG-008 (the user-supplied entry header
> said "BUG-007" but BUG-007 is already taken by the post-reload
> dropdown defect above; assumed typo, not supersession).

---

### TASK-003 — New Project form missing PAFM v2.0 initiation fields

- **Phase / Step:** Phase 3, Step 17
- **Severity:** n/a (scope gap, not a defect)
- **Status:** open — scope decision needed before deciding sprint placement
- **Area:** projects, initiation

**Missing fields vs PAFM v2.0:**
- Contract form (NEC4 / JCT / D&B / etc.) — controlled list
- IPA gateway alignment — controlled list (Gate 0 through Gate 5, plus N/A)
- Project board / sponsor — text or person picker
- Start date
- Target completion date

**Decision needed:**
- Add to initiation form now (one sprint), or
- Capture later in a separate "Project Setup" step post-creation, or
- Defer to Admin Console / project template (Sprint 14) so the template
  pre-populates these for all new projects of a type.

**Initial preference:** Start date, target completion, and contract form
in the initiation dialog (these aren't optional in any UK PM context).
Sponsor and IPA gateway move to a "Governance" tab that opens immediately
after project creation. Decide before this gets built.

---

### OBS-002 — BUG-006 verification step 9B deferred (no non-admin seed user)

- **Phase / Step:** Auth-persistence verification, step 9 part B
- **Severity:** n/a (test-data gap, not a defect)
- **Status:** open — to verify during BUG-006 fix
- **Area:** seed data, testing

**Detail:** Step 9A (unauthenticated → 401) verified clean. Step 9B
(authenticated non-admin → 403) skipped because no non-admin user is
seeded. To be verified when BUG-006 is fixed and the seed is broadened
to include at least one user without OrgAdmin/SuperAdmin.

# CIMS Smoke Test — Findings Log

**Test session:** 2026-05-08
**Build under test:** _add commit hash_
**Tester:** Eduard
**Environment:** Visual Studio dev run, `localhost:55069`, .NET 8 / Blazor Server / MudBlazor / EF Core / SQL Server
**Tenant:** Genera Systems
**Test plan:** 10-phase end-to-end walkthrough (initiation → closing + cross-cutting)

---

## How to use this file

- One entry per finding. Append in order.
- IDs: `BUG-NNN` for defects, `TASK-NNN` for missing features / backlog items, `OBS-NNN` for observations that aren't bugs but are worth recording.
- Severity: `critical` (blocks testing) / `high` (blocks a workflow) / `medium` (workflow works with workaround) / `low` (cosmetic).
- Status: `open` / `investigating` / `fixed` / `wontfix` / `promoted-to-issue:#NN`.
- At the end of the smoke test, promote `open` items to GitHub Issues and record the issue number in Status.

---

## Phase progress

| Phase | Title | Status |
|---|---|---|
| 0 | Environment sanity | ☐ |
| 1 | Tenant & auth | ☐ |
| 2 | Admin Console | ⏭ Skipped — Sprint 14 not built (see TASK-001) |
| 3 | Project initiation | ☐ |
| 4 | ISO 19650 naming wizard | ☐ |
| 5 | Schedule module | ☐ |
| 6 | Cost / Commercial | ☐ |
| 7 | Quality / ITPs / Snags / Permits | ☐ |
| 8 | Reporting / dashboards | ☐ |
| 9 | Closing | ☐ |
| 10 | Cross-cutting | ☐ |

---

## Findings

### BUG-001 — Sidebar disappears after 404 + back navigation

- **Phase / Step:** Pre-Phase 2, ad-hoc admin URL probe
- **Severity:** medium
- **Status:** open
- **Area:** layout, routing

**Repro:**
1. Log in as dev user on Genera Systems tenant.
2. Confirm sidebar renders on `/dashboard`.
3. Manually enter `https://localhost:55069/dashboard/admin`.
4. Observe 404 / "page is missing" error.
5. Press browser Back.

**Expected:** MainLayout re-renders fully, including NavMenu.
**Actual:** Sidebar is gone. Main content renders without nav shell. App must be restarted to recover (hard refresh recovery untested).

**Diagnostic data still to collect:**
- [ ] Browser console output at the broken state (red errors? circuit/SignalR?)
- [ ] `_blazor` WebSocket connection status in Network tab
- [ ] Whether Ctrl+Shift+R restores the sidebar (recovery vs hard break)

**Suspected causes:**
- Blazor circuit disconnecting on the 404 and not reconnecting cleanly
- 404 page using a different layout (or `null`); back-navigation leaves layout state inconsistent
- NavMenu depending on state cleared by error boundary

**Acceptance criteria:**
- Hitting a 404 and pressing Back restores the prior page **with** the sidebar intact
- No browser console errors after recovery
- Add a UI test (bUnit or Playwright) covering this navigation pattern

---

### TASK-001 — Admin Console (Sprint 14) not built

- **Phase / Step:** Phase 2, all steps
- **Severity:** n/a (planned scope, not a defect)
- **Status:** open — backlog item, planned for Sprint 14
- **Area:** admin

**Description:**
No `/admin` route or admin UI exists in the current build. Confirmed by:
- No admin link in the nav.
- Find-in-files for `@page "/admin` returns nothing.

**Implications for this smoke test:**
- Phase 2 (Admin Console) skipped entirely.
- Phase 3 onwards uses whatever defaults the system ships with — naming standards, approval matrices, role definitions, etc. cannot be configured per tenant, only via code or seed data.
- Negative test in Phase 7 step 47 (HSE Lead and QA holder same person) cannot be enforced via configuration; relies on hard-coded rules if present.

**Acceptance criteria for Sprint 14:**
- Per the original plan: roles, naming standards, approval matrices, project templates, integrations, audit/retention policy.
- Per PAFM v2.0: tenant-level rules with explicit, recorded, time-limited project-level overrides only.

---

## Template — copy below for new findings

```
### BUG-NNN — [short title]

- **Phase / Step:** Phase X, Step Y
- **Severity:** critical / high / medium / low
- **Status:** open
- **Area:** [module]

**Repro:**
1.
2.
3.

**Expected:**
**Actual:**

**Console / log evidence:**
```
[paste console output, network errors, server log lines]
```

**Acceptance criteria:**
-
```

---

## End-of-session checklist

- [ ] All findings have repro steps
- [ ] All findings have expected vs actual
- [ ] Severities assigned
- [ ] Critical/high findings have console/log evidence attached
- [ ] Promoted to GitHub Issues; status updated with `promoted-to-issue:#NN`
- [ ] File committed to repo at `docs/test-log/smoke-test-findings.md`

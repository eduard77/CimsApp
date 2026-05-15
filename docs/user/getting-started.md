# Getting started

## Logging in

Genera PM authenticates with email + password. After successful login
you receive a JWT access token (60-minute lifetime) plus an opaque
refresh token (7-day lifetime). The refresh token rotates on every
use; the access token can be revoked by an admin or by you (sign
out of all sessions via `/auth/logout-everywhere`).

**Trouble logging in?**
- "Invalid credentials" — check email + password. Five failed
  attempts from the same IP in one minute will rate-limit further
  attempts (HTTP 429); wait a minute and try again.
- "Token revoked" — an admin has revoked your sessions OR your
  account has been deactivated. Contact your OrgAdmin.
- Stored credentials don't work after switching dev machines —
  see `docs/admin/operations.md` for the bootstrap-org-on-new-DB
  ritual; this doesn't apply to production.

## Project selection

The sidebar's "Active Project" indicator shows which project the
project-scoped pages (Documents / RFIs / Actions / etc.) are
operating on. Select via the **Projects** page in the Main nav
section. Some pages (Dashboard, Admin) are not project-scoped
and ignore the selection.

## Sidebar navigation

- **Main** — Dashboard, Projects.
- **Project** — every project-scoped surface (Documents, RFIs,
  Actions, Audit Trail, Evidence, Daily Diary, NCRs).
- **Admin** — visible to all users but server-side gated; regular
  users see "Insufficient permissions" inside the page.

On mobile (< 600px viewport) the drawer collapses behind a
hamburger toggle (top-left). Tap to open, tap a nav entry to
navigate (drawer closes automatically).

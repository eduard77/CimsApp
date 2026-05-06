# Training walkthrough — Site User

**Audience:** the field operative recording day-to-day site
observations.
**Goal:** log a daily diary entry, raise an NCR, and verify
both flow into the audit trail. Mostly mobile-shaped (phone
or tablet) since site users typically work from a handset.
**Duration:** ~10 minutes.
**Prerequisites:** an account at TaskTeamMember role, added to
a project as a member.

---

## Step 1 — Sign in (mobile)

1. Open `https://<your-host>/` on a phone (or browser dev
   tools at 375px width).
2. Sign in.
3. Tap the hamburger button (top-left).

**Expected:** the sidebar drawer slides in over the page with
an overlay. Tap a nav item — the page navigates and the drawer
auto-closes.

## Step 2 — Select your project

1. Tap **Projects** (Main nav).
2. Tap your project's row.

**Expected:** sidebar's "Active Project" indicator updates to
show your project's name.

## Step 3 — Log a daily diary entry

1. Tap **Daily Diary** (Project nav).
2. Tap **Add today's entry** (top-right).
3. Fill in:
   - Date (defaults to today)
   - Weather (e.g. "Clear, 18°C")
   - Manpower (e.g. "12 trades, 2 supervisors")
   - Plant (e.g. "1 telehandler, 2 dumpers")
   - Deliveries (e.g. "Brick 2 pallets, mortar 1 pallet")
   - Incidents (e.g. "None" or describe)
   - Progress notes (free-text)
4. Tap **Save**.

**Expected:** entry appears at the top of the list. Audit row
`dailydiary.created` fires.

5. Try to add a SECOND entry for the same date.

**Expected:** server rejects with a 409 conflict —
"A diary entry already exists for this user on this date".
Multi-author is allowed (different user, same date) but a single
user gets one entry per day.

## Step 4 — Raise an NCR

1. Tap **NCRs** (Project nav).
2. Tap **Raise NCR**.
3. Fill in:
   - Title (e.g. "Defective brickwork, Block A north elevation")
   - Description (free-text — what's wrong, scope)
   - Severity: Medium (or High / Critical for safety-critical)
   - Location (e.g. "Block A, level 2, gridline C-D")
4. Tap **Raise**.

**Expected:** NCR appears in the list at status **Raised**,
numbered `NCR-0001` (or next sequential). Audit row
`ncr.created` fires.

## Step 5 — Walk through the workflow

The NCR workflow is **Raised → Assigned → InProgress →
Resolved → Closed**. v1.0 doesn't have a full assignee picker
in the UI (B-123 fast-follower) — assignment goes via API.

For the walkthrough, sign in as PM / IM and:

1. Use the API to assign:
   `POST /api/v1/projects/{id}/ncrs/{ncrId}/assign`
   with `{"assignedToId": "<a-project-member-id>"}`.

**Expected:** NCR moves to **Assigned**. Audit row
`ncr.assigned`.

2. As the assignee (sign in as them; or just use the API as
   the same PM): tap the **InProgress** transition button on
   the row.

**Expected:** NCR moves to **InProgress**. Audit row
`ncr.transitioned`.

3. Tap **Resolved**. Capture resolution notes in the dialog.

**Expected:** NCR moves to **Resolved**; ResolvedAt timestamp
populated.

4. As IM-or-up, tap **Closed**.

**Expected:** NCR moves to **Closed**; ClosedAt populated.

## Step 6 — Verify on mobile + desktop

Switch to desktop width (>= 900px). Re-visit `/diary` and
`/ncrs`.

**Expected:** the same data renders, with extra columns visible
(per the S17 responsive sweep — mobile hides columns under
breakpoints; desktop shows the full table).

## Step 7 — Sign-off check

The site user should be able to:
- ✅ Sign in on mobile and access the project surfaces
- ✅ Log a daily diary in under 2 minutes
- ✅ Raise an NCR in under 2 minutes
- ✅ See the same data they entered when they re-open the page

If any of these takes substantially longer or fails, capture in
post-training notes.

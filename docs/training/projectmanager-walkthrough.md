# Training walkthrough — Project Manager

**Audience:** the operator running a single project end-to-end.
**Goal:** create a project, set up the team, drive a CDE document
through to Published, raise + respond to an RFI, and review the
audit trail.
**Duration:** ~20 minutes.
**Prerequisites:** an OrgAdmin account; the tenant is bootstrapped.

---

## Step 1 — Create a project

1. Sign in as OrgAdmin (or SuperAdmin).
2. Navigate to **Projects** in the Main nav.
3. Click **New project**. Fill in:
   - Name: "Demo Refurb"
   - Code: "DEMO" (uppercased; unique within tenant)
   - Location, Sector, Budget (optional)
   - Client organisation: defaults to your org.
4. Click Save.

**Expected:** the new project appears in the list. The audit
trail (`/audit`, project-scoped to this project) shows
`project.created`. You're auto-assigned as the project's first
ProjectManager member.

## Step 2 — Provision the PMBOK template library

1. Open the project. Navigate to **Templates** in the project's
   sub-nav (or directly via `/projects/{id}/templates`).
2. Click **Provision templates now** (visible only to
   OrgAdmin / SuperAdmin).
3. Wait ~5 seconds.

**Expected:** 28 PMBOK starter docs appear, grouped by knowledge
area (Charter, Integration, Scope, Schedule, Cost, etc.). Each is
a markdown file with placeholder values substituted (project
code, name, currency, budget).

## Step 3 — Add a team member

1. Navigate to **Admin → Invitations** (OrgAdmin).
2. Mint an invitation for a TaskTeamMember-shaped user via the
   API (no minting UI in v1.0):
   `POST /api/v1/organisations/{orgId}/invitations`
   with `{"email": "tm@example.com", "expiresInDays": 7}`.
3. The new user registers via `POST /api/v1/auth/register`.
4. As PM, add the user to your project as TaskTeamMember:
   `POST /api/v1/projects/{projectId}/members` with
   `{"userId": "...", "role": "TaskTeamMember"}`.

**Expected:** the new user can now access the project's
sidebar surfaces.

## Step 4 — Document workflow

1. Open **Documents (CDE)** in the project sub-nav.
2. Click **Register Document**. Fill in:
   - Project code (auto-fills from project)
   - Originator (e.g. "ARC" for architect)
   - Volume, Level, Type ("DR" for drawing), Role, Number
   - Title, Description
3. Save. The full ISO 19650 DocumentNumber is computed:
   `DEMO-ARC-Z1-XX-DR-A-0001`.

**Expected:** the document appears in the register at state
**Work in Progress (WIP)**.

4. Click the **Shared** transition button.

**Expected:** state flips to **Shared**. Audit row
`document.state_transition` fires.

5. Click **Published**.

**Expected:** state flips to **Published**.
ATOMIC: the latest revision's PublishedAt + ApprovedById +
Suitability fields update in the same transaction (PR #34).

## Step 5 — RFI workflow

1. Navigate to **RFIs** in the project sub-nav.
2. Click **New RFI**. Fill in: Subject, Description.
3. Submit.

**Expected:** RFI appears at status **Open**, numbered
`RFI-0001`. Audit row `rfi.created` fires.

4. Click the RFI to open the detail / response dialog.
5. Enter a response. Click Respond.

**Expected:** RFI moves to **Responded**. Audit row
`rfi.responded` fires. The dialog locks the response — RFIs
respond once-only.

## Step 6 — Risk register

1. Navigate to **Risk** sub-nav.
2. Click **New Risk**. Fill in: Title, Description, Probability
   (1-5), Impact (1-5), Response Strategy.
3. Save.

**Expected:** risk appears in the register at status
**Identified**. Heatmap reflects probability × impact.

4. Click the risk; transition through **Assessed → Active →
   Mitigated**.

**Expected:** each transition fires its audit row + updates
the heatmap colour-coding.

## Step 7 — Audit trail review

Navigate to **Audit Trail** sub-nav.

**Expected:** every action you've taken appears as a row,
ordered by time. Filter by Action contains "rfi" — expected
2 rows (`rfi.created` + `rfi.responded`). Filter by Action
contains "document" — expected 5+ rows (create + each
transition + the audit-twin events).

Sign out via **Sign out** (bottom of sidebar). Verify your
session ends cleanly.

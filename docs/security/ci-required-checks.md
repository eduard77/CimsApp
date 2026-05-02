# CI required checks — branch protection rules

**Status:** v1.0 pre-pilot. Owner: repo administrator.
**Source task:** T-S8-05 (sprint/s8-ci-hardening).
**Related:** B-027, ADR-0015.

## Why this document exists

The CI workflow (`.github/workflows/ci.yml`) defines three jobs
that must pass for a PR to be safe to merge to `master`:

1. **Build and test** — `dotnet build` + `dotnet test` on
   ubuntu-latest. Catches compile errors, lint warnings (when
   `-warnaserror` lands per T-S8-06), and unit-test failures.
2. **SQL Server smoke (B-027)** — boots the app against a real
   `mssql/server` Docker service container, applies every
   migration, runs the five smoke endpoints listed in B-027.
   Catches the FK-permissive class of bugs that motivated B-027
   (PR #41 incident).
3. **Migration round-trip (Up / Down to 0 / Up)** — exercises
   every migration's `Down()` method via a full revert to migration
   0 followed by a re-apply. Catches latent broken `Down()`
   methods that have never been exercised in production.

The CI workflow itself **does not block merges**. GitHub's branch
protection rules do that, and they have to be configured in the
repo settings UI by an administrator (the GitHub Actions runner
cannot flip them programmatically without a privileged token).

## One-time setup steps

Apply once per repository. Re-apply if the job names below
change.

### 1. Open branch protection settings

Navigate to: **GitHub repo → Settings → Branches → Branch
protection rules → Add classic branch protection rule**
(URL: `https://github.com/eduard77/CimsApp/settings/branches`).

### 2. Configure the rule

- **Branch name pattern:** `master`
- **Tick:** "Require a pull request before merging"
  - Tick: "Require approvals" (set to 0 for solo dev, 1+ for team)
  - Tick: "Dismiss stale pull request approvals when new commits
    are pushed"
- **Tick:** "Require status checks to pass before merging"
  - Tick: "Require branches to be up to date before merging"
  - In the search box add the following required checks:
    - `Build and test`
    - `SQL Server smoke (B-027)`
    - `Migration round-trip (Up / Down to 0 / Up)`
- **Tick:** "Require conversation resolution before merging"
- **Do not tick:** "Allow force pushes" / "Allow deletions"
- **Tick:** "Restrict who can push to matching branches" (admins
  only) — keeps the existing solo-dev pattern but blocks accidents.

Click **Create** (or **Save changes** when editing).

### 3. Verify

Open any PR. The PR view should now show the three required
checks at the bottom under "All checks have passed" / "Some
checks were not successful". The "Merge pull request" button
is greyed out until all three are green.

If a required check is missing from the PR view, GitHub has
not yet seen a workflow run produce that check. Push any
commit to the PR branch to trigger the workflow; once it
finishes, the check appears.

## Adjusting the rule when CI changes

If a job is renamed, deleted, or split in `.github/workflows/
ci.yml`, the corresponding required-check entry needs to be
updated in the branch protection rule. GitHub does not
auto-rename — a job called `Build and test` that gets renamed
to `Build, test, lint` will leave the old entry as a stale
required check that **never passes**, blocking every PR.

Discipline rule: any PR that renames a CI job in
`ci.yml` MUST also update this document AND remind the
reviewer to update the branch protection rule before merge.

## Status of S8 sprint

After the S8 PR merges:
- [ ] Step 1 — branch protection rule created on `master`.
- [ ] Step 2 — three required checks added.
- [ ] Step 3 — verified on a follow-up PR that the checks block
      merge until green.

These ticks land at the end of T-S8-07 (sprint close), after
the S8 PR itself merges and the workflow has produced enough
runs to populate the GitHub Actions check-name index.

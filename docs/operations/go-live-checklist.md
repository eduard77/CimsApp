# v1.0 Go-Live Checklist

Pre-deploy → deploy → post-deploy procedure for the Genera PM v1.0
internal-pilot ship. Every step has a sign-off line; complete in
order. Skip with reason if not applicable; do NOT proceed past a
failed step without an ADR or formal waiver.

Operator: ____________________
Sign-off date: ____________________

---

## Phase 1 — Pre-deploy verification (T-7 days)

- [ ] **CI green on master.** Latest commit on `master` shows all
      three checks pass (Build and test, Migration round-trip,
      SQL Server smoke). Sign-off: ________
- [ ] **985+ tests pass in CI.** No failing or skipped suite.
      Sign-off: ________
- [ ] **Zero vulnerable packages.**
      `dotnet list package --vulnerable --include-transitive`
      reports no findings. Sign-off: ________
- [ ] **OWASP Top 10 review SIGNED OFF.** See
      `docs/security/owasp-top10-review-2026-05-05.md`. All
      categories PASS or PARTIAL with tracked v1.1 entry.
      Sign-off: ________
- [ ] **External pen test PASSED.** Report archived in
      `docs/security/pen-test-report-YYYY-MM-DD.md`. Zero open
      Critical findings; written mitigation for any High.
      Sign-off: ________
- [ ] **Load test PASSED.** k6 run at 100 VU × 5 min against the
      staging instance achieved p95 < 1000 ms and error rate <
      0.1%. Result documented. Sign-off: ________
- [ ] **UAT PASSED.** All Critical UAT findings closed; High
      findings have a written mitigation; Medium / Low triaged
      to v1.1 backlog. Sign-off: ________
- [ ] **Branch protection landed on master.** T-S8-05 follow-up
      agent fired (scheduled for 2026-05-18 09:00 UTC); direct
      pushes to master are blocked. Sign-off: ________

## Phase 2 — Pre-deploy environment (T-3 days)

- [ ] **Production host provisioned.** .NET 8.0 runtime
      installed; reverse proxy (Nginx / IIS / cloud) configured
      for HTTPS termination + Host / X-Forwarded-Proto headers.
      Sign-off: ________
- [ ] **Production SQL Server provisioned.** Reachable from app
      host; Genera PM DB user created with appropriate permissions
      (db_ddladmin for first migration, downgradable after).
      Sign-off: ________
- [ ] **Required env vars set** on the production host
      (per `docs/admin/setup.md` § Environment variables):
      - [ ] `Jwt__AccessSecret` (≥ 32 chars random)
      - [ ] `Jwt__Issuer`
      - [ ] `Jwt__Audience`
      - [ ] `ConnectionStrings__DefaultConnection`
      - [ ] (if email enabled) `Email__Smtp__Host`,
            `Email__Smtp__Port`, `Email__Smtp__UserName`,
            `Email__Smtp__Password`, `Email__From`
      Sign-off: ________
- [ ] **HTTPS certificate in place** on the reverse proxy.
      Verify chain validates from a clean client; verify
      hostname matches the deployed-URL config in `Jwt:Issuer`
      / `Jwt:Audience`. Sign-off: ________
- [ ] **SMTP relay reachable** (if `Email:Enabled = true`).
      Verify from the app host with a manual test. Sign-off:
      ________
- [ ] **Backups configured** for the production SQL Server
      (per the DB layer's backup strategy — Genera PM doesn't
      prescribe). At least: daily full + transaction log
      backups; restore tested at least once. Sign-off: ________
- [ ] **`storage/projects/` filesystem path** exists, writable
      by the app process, included in the host backup.
      Sign-off: ________

## Phase 3 — Release candidate (T-1 day)

- [ ] **Tag `v1.0`** on the release-candidate commit on master.
      Annotated tag with message referencing this checklist.
      Push tag to origin.

      ```bash
      git tag -a v1.0 -m "v1.0 internal pilot release.
      OWASP Top 10 signed off, zero vulnerable packages,
      pen test PASSED, load test PASSED, UAT PASSED."
      git push origin v1.0
      ```

      Sign-off: ________

- [ ] **Build the release artefact** from the tagged commit.
      `dotnet publish GeneraPm/GeneraPm.csproj -c Release -o
      out/v1.0` (or your preferred publish target). Verify the
      output runs locally with the production env vars.
      Sign-off: ________
- [ ] **CHANGELOG date stamp.** Edit `CHANGELOG.md` to replace
      the v1.0 entry's `2026-MM-DD (release candidate)` placeholder
      with the actual go-live date. Commit on a docs branch +
      PR + merge. Sign-off: ________

## Phase 4 — Deploy (T-zero)

- [ ] **App host stopped.** Existing process (if any) shut down
      cleanly. Sign-off: ________
- [ ] **EF migrations applied** to production DB:
      `dotnet ef database update --project GeneraPm` from a
      machine with the production connection string.
      Sign-off: ________
- [ ] **Release artefact deployed** to the app host. File
      permissions correct; `appsettings.Production.json` in
      place (non-secret config only).
      Sign-off: ________
- [ ] **App started.** Process running; startup logs show
      "Application started"; "Now listening on https://...:..."
      lines visible. Sign-off: ________
- [ ] **Health check.** HTTP request to the app's base URL
      returns 200 (or expected redirect). The /health endpoint
      (if exposed) returns `{"status": "ok"}`. Sign-off: ________

## Phase 5 — Post-deploy verification (T+1 hour)

- [ ] **Bootstrap the first SuperAdmin** via the documented
      ritual (`docs/admin/setup.md` § First-run sequence steps
      4-5). Sign-off: ________
- [ ] **Sign in via the UI.** Verify sidebar nav renders, Admin
      section is reachable, no console errors in the browser
      devtools network panel. Sign-off: ________
- [ ] **Spot-check security headers.** `curl -I` against the
      base URL returns:
      - `X-Content-Type-Options: nosniff`
      - `X-Frame-Options: DENY`
      - `Referrer-Policy: no-referrer`
      - `Content-Security-Policy: ...`
      - `Strict-Transport-Security: max-age=...` (HSTS)
      Sign-off: ________
- [ ] **Spot-check audit-twin firing.** Create a project (any
      OrgAdmin); query `AuditLogs` for the
      `project.created` row. The audit row + the Project row
      should both exist. Sign-off: ________
- [ ] **Spot-check tenant isolation.** Attempt cross-tenant GET
      from an OrgAdmin in tenant A against tenant B's project
      ID. Expected: 404 (NOT_FOUND). Sign-off: ________

## Phase 6 — Pilot enablement (T+1 day)

- [ ] **Provision pilot users.** Mint invitations
      (`/admin/invitations`); send to pilot users with the
      training walkthrough scripts (`docs/training/*.md`).
      Sign-off: ________
- [ ] **Communicate to operator.** Pilot operator receives:
      - URL of the deployed instance
      - SuperAdmin credentials (rotate after first login)
      - Links to `docs/user/`, `docs/admin/`, `docs/training/`
      - Escalation path (who to contact for issues)
      Sign-off: ________
- [ ] **Schedule first pilot review.** 1-week and 4-week
      review meetings; capture feedback for v1.1 backlog
      promotion. Sign-off: ________

## Phase 7 — Watch (T+2 weeks)

- [ ] **First-week issue triage.** Any bugs surfaced by the
      pilot triaged within 24 hours. Critical → hot-fix
      release; High → next minor release; Medium / Low →
      v1.1 backlog. Sign-off: ________
- [ ] **`master` branch protection enforcement.** Verify
      direct push to master is blocked; every change goes
      through PR. Sign-off: ________
- [ ] **Weekly OWASP-style review** on the deployed instance:
      - `dotnet list package --vulnerable` reports no findings
      - SQL Server query log shows no unexpected access patterns
      - Audit log shows expected mutation volumes
      Sign-off: ________

---

## When something goes wrong

If a step fails, **STOP** and capture:

1. The failed step name + sign-off line
2. The actual observed behaviour vs expected
3. The remediation taken (if any)
4. A go / no-go decision on continuing the deploy

Common failure modes are covered in
`docs/admin/troubleshooting.md`.

For any unrecoverable failure mid-deploy, the rollback procedure
is:
1. Stop the app process
2. Restore the database from the most recent pre-deploy backup
3. Redeploy the previous release artefact (the pre-v1.0 build)
4. Restart the app
5. Spot-check that the previous behaviour is restored

A formal rollback ADR + post-incident review follows any
mid-deploy rollback.

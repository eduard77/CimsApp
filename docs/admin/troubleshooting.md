# Troubleshooting

Common errors and remediation. Setup procedure is in
[setup.md](setup.md); routine operations are in
[operations.md](operations.md).

## App fails to start

### "Configuration not found: Jwt:AccessSecret"

The required `Jwt__AccessSecret` env var is missing. See
[setup.md § Environment variables](setup.md). The other
required keys fail with similar messages.

### "Cannot connect to SQL Server"

Check `ConnectionStrings__DefaultConnection`. Common issues:
- Firewall not open between app host and DB host
- SQL Server not configured to allow remote connections
- TLS / certificate mismatch (try `TrustServerCertificate=True`
  if the certificate doesn't match the connection string's
  hostname)
- DB user lacks permissions on the target database

### "Database X is not present"

Run migrations:
`dotnet ef database update --project GeneraPm`

## App starts but every request fails authentication

### "401 Unauthorized" on every authenticated endpoint

Three common causes:
1. The user's `TokenInvalidationCutoff` is in the future and
   their access token was issued before — they need to re-login.
2. JWT signing key was rotated — every existing access token
   is invalid. Users must re-login.
3. Clock skew between app host and JWT issuer — verify NTP is
   syncing on the host.

### "Token revoked" with a valid-looking token

The user has been deactivated OR their cutoff is set in the
future. Check `Users` table for `IsActive` and
`TokenInvalidationCutoff`.

## App responds but Admin Console is empty

### No users / orgs / invitations visible

Three common causes:
1. The signed-in user is OrgAdmin in a different tenant than
   expected. Check `User.OrganisationId` matches the rows
   you'd expect to see.
2. The user is not OrgAdmin / SuperAdmin — check `GlobalRole`.
   The Admin pages render "Insufficient permissions" for
   regular users. (Server-side gate at controller level;
   client never receives the data.)
3. SuperAdmin should see everything; if not,
   `IgnoreQueryFilters` in the service is misbehaving — likely
   a code bug, not config.

## Email isn't being sent

Check:
- `Email:Enabled = true` in config.
- `Email__Smtp__Host` / `Port` / `UserName` / `Password`
  populated.
- Outbound port 587 (or 25) reachable from the app host.
- `EmailDispatcherHostedService` is running (visible in startup
  logs).
- The `EmailQueue` channel is draining — if it's not, the
  dispatcher is stuck (check exception logs).

In-flight emails are LOST on app restart (in-memory queue;
v1.1 / B-091 adds persistent queue).

## CDE state transitions rejected

### "Insufficient role for this transition"

Per `Core/Core.cs:CdeStateMachine.TransitionRoles`, transitions
have role gates. Common missing-role cases:

- Anything → Voided needs OrgAdmin (Published → Voided
  specifically).
- Shared → Published needs ProjectManager.
- The error message names the required minimum role.

### "Invalid transition: X → Y"

The state machine doesn't permit that pair. See
`Core/CdeStateMachine.IsValidTransition`. Document state can
only progress in the Work in Progress → Shared → Published →
Archived chain (or hop sideways to Voided from any of them).

## NCR / Change Request / Inspection state transitions

Each has its own `Core/<X>Workflow.cs` with the same shape —
`IsValidTransition` for state-pair check, `CanTransition` for
role-pair check. Error messages name the failing rule.

## "OrganisationsController.Update returns 404 for an existing org"

Per the S16 fix-forward: cross-tenant Org Update returns 404
intentionally to avoid existence leak. If you're an OrgAdmin
in Org A trying to update Org B, you'll see 404 even though
Org B exists. Use SuperAdmin role for cross-tenant Org Updates.

## CI smoke test failures

### "Docker pull failed: pull access denied for mcr.microsoft.com/mssql/server"

Microsoft Container Registry transient block. Rerun the failed
job (`gh run rerun {id} --failed`); it usually clears within
minutes.

### "Migration round-trip failed at Down step"

A migration's `Down()` method is incomplete. EF migrations are
required to be reversible (the round-trip check forces this).
Fix the `Down()` and re-push.

## Performance

### Search is slow

Per S15, search uses `EF.Functions.Like` against indexed columns.
At >1000 rows in a searchable table, latency becomes user-visible.
Trigger v1.1 / B-095 — SQL Server full-text catalogues —
which has been pre-flagged with this exact unblock condition.

### Audit page is slow

Per S16, the tenant audit query is a direct EF query on
`AuditLogs` with paging + filters. At >10000 rows in a tenant,
the page-load may exceed 1s. Trigger v1.1 / B-100 —
materialised AuditView projection.

## When in doubt

- Check `docs/security/role-matrix.md` for who-can-do-what.
- Check `docs/adr/ADR-XXXX-*.md` for why a behaviour is the way
  it is.
- The structured audit-twin events in `AuditLogs` are
  forensic-adequate; if a mutation surprises you, look for the
  matching `<entity>.<verb>` row in the log.

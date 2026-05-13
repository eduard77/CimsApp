# Operations

Day-2 operational procedures. Setup is in
[setup.md](setup.md); error remediation is in
[troubleshooting.md](troubleshooting.md).

## User lifecycle

### Inviting a new user

1. Sign in as OrgAdmin / SuperAdmin.
2. Mint an invitation:
   `POST /api/v1/organisations/{orgId}/invitations`
   with body `{"email": "newuser@example.com", "expiresInDays": 7}`.
3. Send the returned `token` to the new user — the plaintext is
   shown ONCE; only the SHA-256 hash is persisted.
4. New user calls `POST /api/v1/auth/register` with the token.

The Admin Console `/admin/invitations` page lists active
invitations and lets you revoke any pending ones.

### Promoting an existing user to SuperAdmin

Only an existing SuperAdmin can grant SuperAdmin role. Via the
UI: `/admin/users` → row-level GlobalRole select → SuperAdmin.
The user's `TokenInvalidationCutoff` is bumped automatically;
the user must re-login to pick up the new role.

For initial bootstrap (when no SuperAdmin yet exists), apply the
standing SQL UPDATE:

```sql
UPDATE Users SET GlobalRole = 0
WHERE Email = 'eduard@genera-systems.com';
```

(`GlobalRole = 0` is `UserRole.SuperAdmin`. This is the only
moment this kind of direct SQL is needed.)

### Revoking access

Two patterns:

- **Revoke tokens** (`/admin/users` row → "Revoke tokens"):
  user remains active, but every in-flight session is killed
  immediately. They must log in again. Use when a session is
  suspected compromised but the user themselves should still
  have access.
- **Deactivate** (`/admin/users` row → "Deactivate"): sets
  `IsActive = false` AND bumps token cutoff AND sweeps refresh
  tokens. The user cannot log in again unless reactivated. Use
  when the user is leaving the org or no longer authorized.

## External Auditor lifecycle

Per S18:

1. Mint an invitation for the auditor's email
   (standard `POST /admin/invitations` flow).
2. Auditor registers with the token. They land as a regular
   user with no role.
3. Create an `AuditorProjectAssignment` via
   `POST /admin/auditor-assignments`:
   ```json
   {
     "userId": "...",
     "projectId": "...",
     "scopeAreas": "UkGdpr,Bsa2022",
     "expiresAt": "2026-06-30T00:00:00Z"
   }
   ```
   The service flips the user's GlobalRole to ExternalAuditor
   and bumps the token cutoff.
4. Auditor logs in fresh; can read evidence + audit logs +
   trigger audit export on the assigned project.
5. At engagement end: `POST /admin/auditor-assignments/{id}/revoke`.
   Cuts off access immediately.

## Backups

- **SQL Server**: standard backup pattern. Genera PM doesn't
  prescribe — your DB layer's existing backup strategy applies.
  Tenants share one database; restore is necessarily all-tenants.
- **Storage** (`storage/projects/{code}/`): per-project PMBOK
  template files. File-system backup; no app-level coordination
  needed (templates are static once provisioned).

## Log inspection

ASP.NET Core's default `ILogger` writes to stdout (and the host
log target). Audit-trail data is in the `AuditLogs` table —
queryable via `/admin/audit` or directly via SQL.

For SIEM shipping (Application Insights / Seq / Splunk), see
v1.1 / B-118.

## Token / secret rotation

JWT signing key rotation:

1. Generate new `Jwt__AccessSecret` value (≥ 32 chars random).
2. Deploy with the new env var; restart.
3. Every existing access token will fail signature validation
   on the next request — users get 401 and must re-login.
   (Refresh tokens are opaque DB rows, not signed; they survive
   rotation. Users with valid refresh tokens get a new access
   token automatically.)

DB connection-string rotation: redeploy with the new value;
restart. Brief outage during the restart.

SMTP credential rotation: redeploy with new
`Email__Smtp__Password`; restart. In-flight emails in the
in-memory queue are lost on restart (acceptable; v1.1 / B-091
adds persistent queue).

## Branch protection

Master branch protection is scheduled to land 2026-05-18 09:00
UTC via the T-S8-05 follow-up agent. After that, direct pushes
to master are blocked even for SuperAdmins; every change goes
through PR + CI.

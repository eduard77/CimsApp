# Setup

Production-grade deployment of Genera PM v1.0. Development setup is in
`README.md`; this doc is for operator-facing first-run on a clean
host.

## Prerequisites

- **.NET 8.0 SDK or runtime** — production hosts can install just
  the runtime (`dotnet-runtime-8.0`); developer hosts need the
  full SDK.
- **SQL Server** — 2019 or 2022. LocalDB is dev-only; production
  uses a real SQL Server instance reachable from the app host.
  Latency to the DB is hot-path (every request).
- **HTTPS-terminating reverse proxy** (Nginx / IIS / Azure Front
  Door / Cloudflare). The app issues HSTS in production
  (`UseHsts()` in `Program.cs`); the proxy must terminate TLS
  and forward Host / X-Forwarded-Proto correctly.
- **SMTP relay** (optional — `Email:Enabled = true` only). Any
  RFC-compliant SMTP server with auth.

## Environment variables

Per ADR-0016, **secrets live in environment variables only.**
Never commit them to git or to `appsettings.json`. ASP.NET Core
maps `:` → `__` in env var names.

Required for startup:

| Env var | Purpose |
|---|---|
| `Jwt__AccessSecret` | HS256 access-token signing key. Use ≥ 32 chars random. |
| `Jwt__Issuer` | JWT iss claim. |
| `Jwt__Audience` | JWT aud claim. |
| `ConnectionStrings__DefaultConnection` | SQL Server connection. |

Optional (only if `Email:Enabled = true`):

| Env var | Purpose |
|---|---|
| `Email__Smtp__Host`, `__Port`, `__UserName`, `__Password` | SMTP relay credentials. |
| `Email__From` | From-address for outgoing mail. |

Other configuration keys are non-secret and live in
`appsettings.Production.json`:

- `Jwt:AccessExpiresMinutes` (default 60)
- `Jwt:RefreshExpiresDays` (default 7)
- `Email:Enabled` (default false)
- `Logging:LogLevel:*`

## First-run sequence

1. Create the SQL Server database (the app's user needs
   `db_ddladmin` for the first migration; can be downgraded
   to `db_datareader + db_datawriter` after).
2. Apply EF migrations: `dotnet ef database update --project GeneraPm`
   (or run the migration bundle binary if you produced one).
3. Set the env vars above.
4. Start the app. The first request will fail authentication —
   expected; nobody exists yet.
5. **Bootstrap the first organisation + SuperAdmin user**:
   - `POST /api/v1/organisations` (anonymous-allowed) returns a
     bootstrap invitation token.
   - `POST /api/v1/auth/register` with the bootstrap token
     creates the first User as OrgAdmin.
   - Apply the standing post-S0 SQL UPDATE to promote to
     SuperAdmin: `UPDATE Users SET GlobalRole = 0 WHERE Email = '...'`.
     (Documented in `memory/project_dev_machines.md`; PowerShell
     equivalent in `docs/admin/operations.md`.)
6. Log in via the UI; verify the Admin Console pages render.

## Verification

- `https://<host>/swagger` (development only — disabled in
  production unless explicitly enabled). Verifies the API
  surface.
- `https://<host>/api/v1/admin/users` — should list at least
  the SuperAdmin you just bootstrapped (after login).
- Response headers should include `X-Content-Type-Options:
  nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy:
  no-referrer`, `Content-Security-Policy: default-src 'self';
  ...`, and `Strict-Transport-Security` (HSTS) on production
  hosts.

## Known constraints

- **Single-instance scale-out is v1.1**: the LoginAttemptTracker
  (B-018) and OnTokenValidated DB lookup cache (B-022) are
  single-instance for v1.0. Multi-instance deployment requires
  these to land first.
- **Optimistic concurrency control** (B-024 — rowversion / ETag)
  is v1.1; v1.0 has last-write-wins on every mutable entity.

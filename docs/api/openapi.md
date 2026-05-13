# API Documentation

Genera PM exposes a REST API rooted at `/api/v1/`. Authentication is
JWT bearer; payloads are JSON; OpenAPI 3.0 is published via
Swagger UI.

## Swagger UI

`/swagger` — interactive OpenAPI explorer. **Development only**
in v1.0 (per `Program.cs`'s `if (app.Environment.IsDevelopment())`
guard). Production deployments don't expose the UI by default to
keep the surface tight.

To enable Swagger in production temporarily (e.g. for an external
auditor), set `ASPNETCORE_ENVIRONMENT=Development` for the duration
— but be aware this also flips other dev-only behaviours
(`UseHsts()` is non-Dev-only, error page detail, etc.).

The Swagger document at `/swagger/v1/swagger.json` is the
canonical OpenAPI spec; download it for offline reference or
client-codegen.

## Authentication pattern

Every authenticated endpoint expects a `Authorization: Bearer
<jwt>` header. Acquire via:

```
POST /api/v1/auth/login
{
  "email": "user@example.com",
  "password": "..."
}
```

Response:
```
{
  "success": true,
  "data": {
    "accessToken": "eyJ...",   // 60-min HS256 JWT
    "refreshToken": "abc...",  // 7-day opaque hex (Guid×2)
    "user": { ... }
  }
}
```

Refresh:
```
POST /api/v1/auth/refresh
{ "refreshToken": "..." }
```

Refresh tokens rotate on every use. Old refresh tokens become
invalid immediately.

## SignalR (real-time notifications)

`/hubs/notifications` — JWT bearer is passed as `?access_token=`
query param at hub-connect time (the standard SignalR-over-WebSocket
pattern; see `Program.cs` `OnMessageReceived`).

## Standard response shape

Every endpoint returns a wrapped envelope:

```json
{ "success": true, "data": { ... } }
```

or on error:

```json
{
  "success": false,
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "Validation failed",
    "details": ["Name is required"]
  }
}
```

Error codes used:

- `VALIDATION_ERROR` (400)
- `NOT_FOUND` (404)
- `FORBIDDEN` (403)
- `CONFLICT` (409)
- `INVALID_CREDENTIALS` (401)
- `TOKEN_REVOKED` (401)
- `INVALID_REFRESH` (401)

## Audit-twin contract

Every business mutation produces a structured audit-trail row in
the **same database transaction** as the underlying entity write
(audit-twin atomicity, PR #33). Downstream API consumers can rely
on: if a mutation succeeds, its audit row exists. If a mutation's
audit row is absent, the mutation didn't commit.

Action names follow `<entity>.<verb>` (e.g. `rfi.responded`,
`payment_certificate.issued`, `evidence.added`,
`auditor.assigned`). Read access via
`/api/v1/projects/{id}/audit` (project-scoped) or
`/api/v1/admin/audit` (tenant-wide).

## Rate limiting

Three anonymous endpoints are rate-limited per IP:

- `/auth/login` — 5 requests / minute (anon-login policy)
- `/auth/register`, `/auth/refresh`, `/auth/logout`,
  `/organisations` (POST) — 10 requests / minute (anon-default)

Authenticated routes are NOT rate-limited at the application
layer (cross-cutting per-user throttle is v1.1 deferral).

## Endpoint surface (high level)

~80 endpoints across 18 sprints. Top-level groupings:

- `/api/v1/auth/*` — register, login, refresh, logout, logout-everywhere, me
- `/api/v1/users/*` — admin user mutations
- `/api/v1/organisations/*` — org bootstrap + listing + invitations mint
- `/api/v1/admin/*` — Admin Console (users, organisations,
  invitations, audit, auditor-assignments)
- `/api/v1/projects/*` — project create + list + members + HRB
- `/api/v1/projects/{id}/*` — project-scoped surfaces:
  CDE / documents / RFIs / actions / cost / schedule / risk /
  procurement / change requests / ISO 19650 / Golden Thread /
  GDPR / Kaizen / inspections / notifications / search /
  evidence / audit / audit-export / diary / NCRs

The full enumeration is in the Swagger spec.

## Tenant isolation contract

All tenant-scoped data is filtered by EF Core query filters
keyed on `Project.AppointingPartyId == _tenant.OrganisationId`
(or `OrganisationId` directly for non-project-scoped entities).
Cross-tenant attempts return **404 (NOT_FOUND), never 403
(FORBIDDEN)** — this is intentional to avoid existence-leak
(post-S1 audit pattern). The exception is mutations that
explicitly want to surface a permission failure with the
existence already known (e.g. role-set after the lookup
succeeds) — those return 403.

## Versioning

`/api/v1/` is the only API version v1.0 ships. v2 will be
introduced at the next breaking-change point; until then v1
is the canonical contract.

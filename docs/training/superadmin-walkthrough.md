# Training walkthrough — SuperAdmin / OrgAdmin

**Audience:** the operator commissioning Genera PM for a tenant.
**Goal:** sign in, bootstrap an organisation, invite the first
project manager, and verify the tenant is correctly isolated.
**Duration:** ~15 minutes.
**Prerequisites:** a deployed Genera PM instance with env vars set
(see `docs/admin/setup.md`), a SuperAdmin account already
provisioned via the bootstrap-and-promote ritual.

---

## Step 1 — Sign in

1. Open `https://<your-host>/`.
2. Enter SuperAdmin email + password. Click **Sign in**.
3. Expected: sidebar nav appears with Main / Project / Admin
   sections. Top-right shows your avatar + organisation.

**Screenshot here:** sidebar layout post-login.

## Step 2 — Bootstrap a new organisation

A new tenant is created via the anonymous
`POST /api/v1/organisations` endpoint. From the SuperAdmin
console, the cleanest path is via PowerShell or curl — there's
no admin UI for tenant creation (yet — v1.1 / B-099 territory).

```powershell
$resp = Invoke-RestMethod -Uri "https://<host>/api/v1/organisations" `
    -Method POST -ContentType "application/json" `
    -Body (@{
        name = "Acme Construction Ltd"
        code = "ACME"
        country = "United Kingdom"
    } | ConvertTo-Json)

# Capture the bootstrap invitation token (shown ONCE).
$resp.data.bootstrap.token
```

**Expected:** the response contains a new organisation row + a
24-hour bootstrap invitation. The token is plaintext in the
response; only the SHA-256 hash is persisted.

## Step 3 — Register the first user

Hand the bootstrap token to the new tenant's first user. They
register via `POST /api/v1/auth/register`:

```powershell
Invoke-RestMethod -Uri "https://<host>/api/v1/auth/register" `
    -Method POST -ContentType "application/json" `
    -Body (@{
        email           = "pm@acme.example.com"
        password        = "MinimumEightChars1!"
        firstName       = "Project"
        lastName        = "Manager"
        invitationToken = "<the-bootstrap-token>"
    } | ConvertTo-Json)
```

**Expected:** the new user is created + auto-assigned
`GlobalRole = OrgAdmin` (because the invitation was a bootstrap
one). The user can now log in.

**Screenshot here:** the new user logging in for the first
time, sidebar showing the Admin section.

## Step 4 — Verify tenant isolation

Sign back in as SuperAdmin. Navigate to `/admin/organisations`.

**Expected:** the new tenant ("Acme Construction Ltd") appears
in the list alongside any existing tenants.

Sign in as the new OrgAdmin. Navigate to `/admin/organisations`.

**Expected:** ONLY their own tenant ("Acme Construction Ltd")
appears. They cannot see any other tenant.

Try (as the OrgAdmin) to PUT against a different tenant's id:

```powershell
Invoke-RestMethod -Uri "https://<host>/api/v1/admin/organisations/$otherTenantId" `
    -Method PUT -Headers @{ Authorization = "Bearer $orgAdminToken" } `
    -Body (@{ name = "Hacked"; country = "UK" } | ConvertTo-Json) `
    -ContentType "application/json"
```

**Expected:** HTTP **404** (NOT_FOUND), not 403. Existence-leak
prevention pattern from the S16 OrganisationAdminService fix.

## Step 5 — Invite a project manager (alternative path)

If the tenant is already running and you want to invite a new
PM-level user:

1. Go to `/admin/invitations` (must be OrgAdmin / SuperAdmin).
2. (No mint UI in v1.0 — use the API:)
   `POST /api/v1/organisations/{orgId}/invitations`
   with body `{"email": "newpm@acme.example.com",
   "expiresInDays": 7}`.
3. Send the returned token to the new user.
4. Verify the invitation appears at `/admin/invitations`.
5. After the user registers, verify the invitation no longer
   appears (it's consumed).

## Step 6 — Sanity check the audit trail

Navigate to `/admin/audit`. Filter by `action contains
"organisation.created"`.

**Expected:** the recent tenant bootstrap appears as an audit
row. The `userId` is `null` (anonymous bootstrap flow per the
PR #41 semantic).

Filter by `action contains "auth.user_global_role_set"` —
expected: zero rows so far.

## Step 7 — Take a C-11 (sign-off check)

Verify your operational runbook contains:
- Where the env vars are set on the production host
- How to rotate the JWT signing key (see
  `docs/admin/operations.md`)
- Where the SQL Server backups land
- The escalation path for a security finding

If any are missing, that's the finding for this walkthrough.
Capture in the post-training notes.

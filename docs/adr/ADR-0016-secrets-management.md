# ADR-0016 — Secrets management

**Status:** Accepted (2026-05-05, S19 / PAFM F.18 fourth bullet).
**Supersedes:** —
**Related:** ADR-0014 (access-token residual-authority SLA),
B-001 (token revocation), B-114 (Azure Key Vault — v1.1).

## Context

CIMS handles four classes of secret material:

1. **JWT signing keys** — `Jwt:AccessSecret` (HS256 access-token
   key) and `Jwt:RefreshSecret` (currently unused after PR #43,
   retained for forward compatibility). Compromise of the access
   secret would allow token forgery; rotation must invalidate
   in-flight access tokens.
2. **Database connection string** — `ConnectionStrings:DefaultConnection`.
   Compromise would allow bulk read of every tenant's data.
3. **SMTP credentials** (S14 email pipeline) — `Email:Smtp:UserName`
   / `Password`. Compromise would let the attacker send mail as
   the application.
4. **Per-tenant signing or API keys** — none currently. v1.1
   territory if Genera Systems integration (B-086..B-089) lands
   with API keys.

The PAFM F.18 fourth bullet says "Secrets management in
production." This ADR captures the v1.0 decision and the
v1.1 migration path.

## Decision

**v1.0 production uses environment variables exclusively for
secret material.** ASP.NET Core's default configuration
provider chain reads from env vars at startup; the same key
shape (`Jwt:AccessSecret`, `Jwt:RefreshSecret`,
`ConnectionStrings:DefaultConnection`, `Email:Smtp:Password`,
etc.) applies, with `:` mapped to `__` in env var names per
the standard provider rules
(e.g. `ConnectionStrings__DefaultConnection`).

Per-developer local override uses `dotnet user-secrets` (already
supported by the .NET tooling — secrets are stored outside the
repo in the user profile, never committed).

`appsettings.Development.json` is the only file that may carry
non-secret development defaults; production secrets MUST NOT
appear there. The current `appsettings.json` contains
placeholder Jwt keys and a LocalDB connection string suitable
for development; production deployments override every secret
key via env var.

The deployment runbook (`docs/operations/deployment-checklist.md`,
to be written at S22 UAT & Release) enumerates which env vars
are required for production startup; missing-env-var diagnostics
fail loudly at startup rather than degrading silently.

## Alternatives considered

- **Option A (chosen): Environment variables.** Built-in
  ASP.NET configuration provider; zero new dependencies;
  matches the .NET deployment defaults. Rotation is a redeploy
  with new env values. Pro: simple, free, auditable via
  deployment platform's existing env-var management. Con:
  rotation requires app restart; secret history is opaque
  (no built-in versioning).
- **Option B: Azure Key Vault.** Cloud-managed secrets with
  versioning, RBAC, audit logging, and hot-rotation support.
  Adds an Azure dependency, managed-identity auth flow, and
  per-deployment Key Vault provisioning. Pro: gold-standard
  for production secrets; rotation without redeploy. Con:
  cloud lock-in; non-Azure deployments need a parallel
  decision (AWS Secrets Manager / HashiCorp Vault); auth
  setup is a learning curve.
- **Option C: Sealed secrets in git** (Bitnami SealedSecrets,
  SOPS, etc.). Encrypted secrets committed to the repo,
  decrypted at deploy time with a deployment-only key. Pro:
  GitOps-friendly; full version history. Con: still needs a
  decryption-key management story (chicken-and-egg); adds
  pre-deploy ceremony.

## Rationale

Option A wins for v1.0 because:

- **The internal pilot is single-deployment.** v1.0 ships to one
  customer's environment; cloud-managed secrets are over-design
  for a single-deployment scenario.
- **Rotation tempo is low for v1.0.** Pilot duration is weeks,
  not years; manual env-var rotation at deploy time is
  acceptable.
- **Deployment platform agnosticism.** Env vars work on Azure
  App Service, Docker containers, bare-metal, IIS, anywhere.
  v1.0 hasn't fixed the deployment target yet; cloud-locked
  secrets management would foreclose options.
- **The .NET configuration provider chain handles env vars
  natively.** No new code, no new package reference, no new
  failure mode beyond "env var missing → app fails to start
  with a clear message".

Option B is the v1.1 migration target the moment the pilot
exits internal mode (multi-tenant SaaS) OR a deployment requires
cross-region failover with rotation-without-restart.

## Consequences

**Positive:**

- Zero new dependencies to ship v1.0.
- Deployment is "set the env vars, run the binary."
- Secrets are never in git, never in a config file at rest in
  the repo, never logged (the configuration provider doesn't
  log secret keys).
- Trivial to rotate JWT signing keys: deploy with a new value,
  restart. ADR-0014's `TokenInvalidationCutoff` machinery means
  every existing token issued under the old key is rejected on
  the next request as the signature check fails (legitimately),
  exactly the same cascade as a deactivation.

**Negative:**

- Secret rotation requires app restart (no hot-rotation).
- Env vars are visible to anyone with shell access on the host;
  production hosts must be locked down accordingly.
- No built-in version history of secret values; auditing relies
  on the deployment platform's own logs.
- The 5+ env vars that must be set correctly at startup are a
  deployment-runbook concern; a missing or typo'd one fails the
  startup with a (sometimes obscure) error.

**Neutral / follow-up:**

- B-114 v1.1 — Azure Key Vault (or AWS Secrets Manager) for
  cloud-managed rotation + auditing. Unblock conditions documented
  in B-114.
- Deployment runbook at S22 UAT & Release must enumerate the
  env vars and the failure mode for each.
- Production startup should fail loudly on a missing required
  env var (validate at composition root, not on first use).

## Required env vars (v1.0)

| Name | Purpose | Failure mode if missing |
|---|---|---|
| `Jwt__AccessSecret` | HS256 access-token signing key | App startup fails — auth pipeline can't validate |
| `Jwt__Issuer` | JWT iss claim | App startup fails — token validator misconfigured |
| `Jwt__Audience` | JWT aud claim | Same |
| `ConnectionStrings__DefaultConnection` | SQL Server connection | App starts but every DB call fails |
| `Email__Smtp__Host` (if `Email:Enabled = true`) | SMTP host | Email send fails silently → notification queue grows |
| `Email__Smtp__Password` (if Smtp host set) | SMTP password | Same |

All other configuration keys are non-secret and live in
`appsettings.json` / `appsettings.Production.json`.

## Related

- PAFM-CIMS-SD-001 Ch 28 (deployment).
- PAFM-CIMS-SD-001 Appendix F.18 fourth bullet.
- ADR-0014 (rotation cascade behaviour).
- B-114 (v1.1 Azure Key Vault migration).
- Sprint log: S19 entry 2026-05-05.

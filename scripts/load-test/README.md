# Load test (T-S19-05 / PAFM F.18)

100 concurrent virtual users for 5 minutes against the
v1.0-critical paths. Pass criteria: p95 < 1s, error rate < 0.1%.

## Prerequisites

- `k6` CLI installed (`choco install k6` on Windows;
  `brew install k6` on macOS; or download from
  https://k6.io/docs/get-started/installation/).
- A deployed Genera PM instance reachable from the machine running
  k6 (HTTPS or HTTP). Local dev server works for sanity
  checking; a deployed staging instance is the realistic test.
- A long-lived bearer token for a pre-created LoadTest user.

## Provisioning the seed token

```powershell
# 1. Bootstrap an org + register the LoadTest user (see
#    docs/scope-reconciliation-2026-05-05.md for the bootstrap
#    flow, or use an existing /admin/invitations mint).
# 2. Login as LoadTest user; capture the access token.

$resp = Invoke-RestMethod -Uri "$BaseUrl/api/v1/auth/login" `
    -Method POST -ContentType "application/json" `
    -Body (@{ Email = "loadtest@example.com"; Password = "..." } | ConvertTo-Json)

$resp.data.accessToken | Set-Content .seed-token
```

## Running

```bash
k6 run \
    --env BASE_URL=https://staging.example.com \
    --env SEED_TOKEN=$(cat .seed-token) \
    --env PROJECT_ID=00000000-0000-0000-0000-000000000000 \
    k6-100-vu.js
```

`PROJECT_ID` is optional. When unset the project-scoped scenarios
are skipped and only the admin / project-list paths run; provide
a real project id from the seed data to exercise the full
hot-path set including evidence write contention.

## Output

k6 prints a summary at completion. The two thresholds
(`http_req_duration: p(95)<1000` and `errors: rate<0.001`)
return non-zero exit code if the run fails them. CI integration
uses that exit code as the pass/fail gate.

## Scaling beyond v1.0 internal pilot

Geographic-distribution-realistic load (multiple regions,
multiple latency profiles) is v1.1 / B-113 territory. v1.0 ships
with this single-machine local k6 run as the load-test
acceptance.

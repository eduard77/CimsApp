#!/usr/bin/env bash
# T-S8-03 — CI SQL Server smoke walk (B-027).
#
# Runs the five smoke endpoints from docs/v1.1-backlog.md B-027
# against an already-running CIMS app. Caller is responsible for
# starting the app (workflow does this in the previous step).
#
# Each smoke step asserts:
#   - HTTP status code matches the expected,
#   - Response body satisfies a basic shape probe (jq query
#     returns non-null / non-empty).
#
# Exits non-zero on any mismatch so the GitHub Actions job fails
# and merge is blocked.
#
# Inputs (env):
#   CIMS_BASE_URL   default "http://localhost:5000"
#   CIMS_RUN_ID     default $RANDOM (used to suffix org code +
#                   user email so concurrent runs cannot collide
#                   even on a hypothetical shared DB).
set -euo pipefail

BASE_URL="${CIMS_BASE_URL:-http://localhost:5000}"
# Organisation.Code MaxLength is 10. Keep RUN_ID short enough that
# "CI<run_id>" fits inside the column. GitHub Actions provides
# GITHUB_RUN_ID (numeric, 10-12 digits) which is unique across CI
# runs; locally we fall back to a 5-digit random seed. Both are
# truncated to 6 chars so "CI<run_id>" is at most 8.
DEFAULT_RUN_ID="${GITHUB_RUN_ID:-$RANDOM}"
RUN_ID="${CIMS_RUN_ID:-${DEFAULT_RUN_ID:0:6}}"

ORG_CODE="CI${RUN_ID}"
USER_EMAIL="ci+${RUN_ID}@example.com"
USER_PASSWORD='SmokeTest@Passw0rd!'

red()   { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }
step()  { printf '\n=== %s ===\n' "$*"; }

assert_status() {
  local expected="$1" actual="$2" url="$3"
  if [[ "$actual" != "$expected" ]]; then
    red "FAIL: $url returned $actual, expected $expected"
    exit 1
  fi
  green "OK   $url -> $actual"
}

assert_jq_nonempty() {
  local body="$1" path="$2" label="$3"
  local v
  v=$(echo "$body" | jq -r "$path")
  if [[ -z "$v" || "$v" == "null" ]]; then
    red "FAIL: $label: jq path $path was empty/null" >&2
    echo "Body: $body" | head -c 500 >&2
    exit 1
  fi
  # Visual log to stderr so command substitution captures only the
  # value via stdout.
  echo "      $label = ${v:0:40}..." >&2
  printf '%s' "$v"
}

# ── 1. Bootstrap organisation (anonymous) ─────────────────────
step "1/5  POST /api/v1/organisations  (anonymous bootstrap)"
ORG_RESP_FILE=$(mktemp)
ORG_STATUS=$(curl -s -o "$ORG_RESP_FILE" -w '%{http_code}' \
  -X POST "$BASE_URL/api/v1/organisations" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"CI Smoke Org $RUN_ID\",\"code\":\"$ORG_CODE\",\"country\":\"GB\"}")
ORG_BODY=$(cat "$ORG_RESP_FILE"); rm -f "$ORG_RESP_FILE"
assert_status 201 "$ORG_STATUS" "POST /organisations"
ORG_ID=$(assert_jq_nonempty "$ORG_BODY" '.data.organisation.id' "OrgId")
INVITATION_TOKEN=$(assert_jq_nonempty "$ORG_BODY" '.data.bootstrap.token' "BootstrapToken")
echo

# ── 2. Register first user (uses bootstrap token) ─────────────
step "2/5  POST /api/v1/auth/register  (bootstrap registrant)"
REG_RESP_FILE=$(mktemp)
REG_STATUS=$(curl -s -o "$REG_RESP_FILE" -w '%{http_code}' \
  -X POST "$BASE_URL/api/v1/auth/register" \
  -H 'Content-Type: application/json' \
  -d "{
    \"email\":\"$USER_EMAIL\",
    \"password\":\"$USER_PASSWORD\",
    \"firstName\":\"CI\",
    \"lastName\":\"Smoke\",
    \"jobTitle\":null,
    \"invitationToken\":\"$INVITATION_TOKEN\"
  }")
REG_BODY=$(cat "$REG_RESP_FILE"); rm -f "$REG_RESP_FILE"
assert_status 201 "$REG_STATUS" "POST /auth/register"
# RegisterAsync returns UserSummaryDto (no token); login follows
# in step 3 to mint the JWT. Just assert the user shape here.
assert_jq_nonempty "$REG_BODY" '.data.id' "RegisteredUserId" >/dev/null
echo

# ── 3. Login (full SQL Server auth path) ──────────────────────
step "3/5  POST /api/v1/auth/login  (login with new credentials)"
LOGIN_RESP_FILE=$(mktemp)
LOGIN_STATUS=$(curl -s -o "$LOGIN_RESP_FILE" -w '%{http_code}' \
  -X POST "$BASE_URL/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"$USER_EMAIL\",\"password\":\"$USER_PASSWORD\"}")
LOGIN_BODY=$(cat "$LOGIN_RESP_FILE"); rm -f "$LOGIN_RESP_FILE"
assert_status 200 "$LOGIN_STATUS" "POST /auth/login"
ACCESS_TOKEN=$(assert_jq_nonempty "$LOGIN_BODY" '.data.accessToken' "AccessToken")
echo

# ── 4. Authenticated read (tenant filter active) ──────────────
step "4/5  GET /api/v1/projects  (authenticated, tenant filter active)"
LIST_RESP_FILE=$(mktemp)
LIST_STATUS=$(curl -s -o "$LIST_RESP_FILE" -w '%{http_code}' \
  -X GET "$BASE_URL/api/v1/projects" \
  -H "Authorization: Bearer $ACCESS_TOKEN")
LIST_BODY=$(cat "$LIST_RESP_FILE"); rm -f "$LIST_RESP_FILE"
assert_status 200 "$LIST_STATUS" "GET /projects"
# Empty list is the expected shape for a fresh org; just verify
# the success envelope.
SUCCESS_FLAG=$(echo "$LIST_BODY" | jq -r '.success')
if [[ "$SUCCESS_FLAG" != "true" ]]; then
  red "FAIL: GET /projects success flag was '$SUCCESS_FLAG'"
  exit 1
fi
green "      success=true (empty data array expected)"
echo

# ── 5. Authenticated write (audit-twin atomicity) ─────────────
step "5/5  POST /api/v1/projects  (authenticated write, audit-twin)"
PROJ_RESP_FILE=$(mktemp)
PROJ_STATUS=$(curl -s -o "$PROJ_RESP_FILE" -w '%{http_code}' \
  -X POST "$BASE_URL/api/v1/projects" \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{
    \"name\":\"CI Smoke Project $RUN_ID\",
    \"code\":\"P-$RUN_ID\",
    \"description\":null,
    \"appointingPartyId\":\"$ORG_ID\",
    \"startDate\":null,
    \"endDate\":null,
    \"location\":null,
    \"country\":null,
    \"currency\":\"GBP\",
    \"budgetValue\":null,
    \"sector\":null,
    \"sponsor\":null,
    \"eirRef\":null
  }")
PROJ_BODY=$(cat "$PROJ_RESP_FILE"); rm -f "$PROJ_RESP_FILE"
assert_status 201 "$PROJ_STATUS" "POST /projects"
assert_jq_nonempty "$PROJ_BODY" '.data.id' "ProjectId" >/dev/null
echo

green "All 5 smoke steps passed."

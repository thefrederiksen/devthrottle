#!/usr/bin/env bash
# Refuse to deploy onto a production Gateway that is already down (issue #4515).
#
# Called by .github/workflows/deploy-hosted-gateway.yml, step "Refuse to deploy onto a production site
# that is already unhealthy", before anything in that job changes production. It can be run anywhere:
#
#   scripts/check-production-healthy.sh <base url>
#
# THE PASS CONDITION IS A PRESENCE. <base url>/healthz answers HTTP 200, the body is JSON, and the JSON
# carries a non-empty "commit". Everything else fails: no connection (curl reports 000), a 502, no answer
# inside the per-request timeout, an empty body, a body that is not JSON, or JSON with no commit.
#
# WHY THE TIMEOUT IS SHORT AND THE ATTEMPTS ARE FEW. When the app behind it is dead, the Azure front end
# accepts the connection, completes TLS, and then holds the request for about 224 seconds before it
# answers 502. "No answer" is therefore the normal shape of a dead production, and it is a failure, not
# something to wait out. A few short attempts ride out one dropped packet; they never add up to minutes.
#
# WHY IT MATTERS. On 2026-10-26 a deploy pinned a second image onto a production site that the deploy
# before it had already left dead, and its outgoing record read commit "unknown" and named the broken image
# as the image to go back to. When this check refuses, the next action is recovery, not a deploy.
#
# THE ESCAPE HATCH. DEPLOY_TO_UNHEALTHY_PRODUCTION=true still probes and still reports what /healthz
# returned, but lets the deploy go on, and says so loudly in the run summary. The fix for a broken
# production may itself be a deploy, and this check must never be what blocks that recovery.
#
# Settings, all optional:
#   HEALTH_TIMEOUT_SECONDS          per-request timeout (default 10)
#   HEALTH_ATTEMPTS                 attempts before refusing (default 3)
#   HEALTH_RETRY_DELAY_SECONDS      pause between attempts (default 2)
#   DEPLOY_TO_UNHEALTHY_PRODUCTION  "true" skips the refusal (default false)
#   GITHUB_STEP_SUMMARY             when set, the verdict is appended to it
#
# The proof is scripts/check-production-healthy.test.sh, run by the web job of .github/workflows/ci.yml.
# Run it by hand after every change to this script: the C# guard only reads this file's text.
#
# Exit codes: 0 production is healthy, or the escape hatch was used; 1 refused; 2 bad usage.

set -uo pipefail

BASE_URL=${1:-}
if [ -z "$BASE_URL" ]; then
  echo "usage: $0 <base url>    (probes <base url>/healthz)" >&2
  exit 2
fi
BASE_URL=${BASE_URL%/}
HEALTH_URL="${BASE_URL}/healthz"
TIMEOUT=${HEALTH_TIMEOUT_SECONDS:-10}
ATTEMPTS=${HEALTH_ATTEMPTS:-3}
DELAY=${HEALTH_RETRY_DELAY_SECONDS:-2}
OVERRIDE=${DEPLOY_TO_UNHEALTHY_PRODUCTION:-false}
ROLLBACK_WORKFLOW="rollback-hosted-gateway.yml"

summary() {
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '%s\n' "$@" >> "$GITHUB_STEP_SUMMARY"
  fi
}

BODY_FILE=$(mktemp)
trap 'rm -f "$BODY_FILE"' EXIT

# One probe. Sets OBSERVED (what /healthz returned, in words) and LIVE_COMMIT (empty unless healthy).
# Returns 0 only when the pass condition is present.
probe() {
  LIVE_COMMIT=""
  : > "$BODY_FILE"
  local code curl_exit
  LAST_CODE=""
  code=$(curl -sS -m "$TIMEOUT" -o "$BODY_FILE" -w '%{http_code}' "$HEALTH_URL" 2>/dev/null)
  curl_exit=$?
  if [ "$curl_exit" -eq 28 ]; then
    OBSERVED="no answer within ${TIMEOUT}s (timed out; HTTP 000)"
    return 1
  fi
  if [ "$curl_exit" -ne 0 ] || [ -z "$code" ] || [ "$code" = "000" ]; then
    OBSERVED="no HTTP response (connection failed, curl exit ${curl_exit}; HTTP 000)"
    return 1
  fi
  LAST_CODE=$code
  local snippet
  snippet=$(head -c 200 "$BODY_FILE" | tr -d '\r' | tr '\n' ' ')
  if [ "$code" != "200" ]; then
    OBSERVED="HTTP ${code}${snippet:+, body: ${snippet}}"
    return 1
  fi
  if [ ! -s "$BODY_FILE" ]; then
    OBSERVED="HTTP 200 with an empty body"
    return 1
  fi
  if ! jq -e . "$BODY_FILE" >/dev/null 2>&1; then
    OBSERVED="HTTP 200 but the body is not JSON: ${snippet}"
    return 1
  fi
  LIVE_COMMIT=$(jq -r 'if type == "object" then (.commit // "") else "" end | tostring' "$BODY_FILE" 2>/dev/null)
  if [ -z "$LIVE_COMMIT" ] || [ "$LIVE_COMMIT" = "null" ]; then
    LIVE_COMMIT=""
    OBSERVED="HTTP 200 but the body carries no commit: ${snippet}"
    return 1
  fi
  OBSERVED="HTTP 200, commit ${LIVE_COMMIT}"
  return 0
}

healthy=false
for attempt in $(seq 1 "$ATTEMPTS"); do
  if probe; then
    healthy=true
    echo "Attempt ${attempt}/${ATTEMPTS}: ${HEALTH_URL} -> ${OBSERVED}"
    break
  fi
  echo "Attempt ${attempt}/${ATTEMPTS}: ${HEALTH_URL} -> ${OBSERVED}"
  if [ "$attempt" -lt "$ATTEMPTS" ]; then sleep "$DELAY"; fi
done

if [ "$healthy" = true ]; then
  echo "Production health gate passed: production is healthy on its own commit ${LIVE_COMMIT}."
  if [ "$OVERRIDE" = "true" ]; then
    echo "NOTE: deploy_to_unhealthy_production was set, but production is healthy, so it changed nothing."
    summary "### Production health gate: passed (deploy_to_unhealthy_production was set but not needed)" \
            "Production was healthy before the deploy: ${OBSERVED}."
  fi
  exit 0
fi

if [ "$OVERRIDE" = "true" ]; then
  echo "WARNING: PRODUCTION IS ALREADY UNHEALTHY, AND THIS DEPLOY IS GOING AHEAD ANYWAY."
  echo "         deploy_to_unhealthy_production=true was set on this run. /healthz returned: ${OBSERVED}."
  echo "         The outgoing commit and image this run records are NOT a trustworthy rollback target."
  summary "### WARNING: deployed onto UNHEALTHY production - the production health gate was SKIPPED" \
          "This run was started with **deploy_to_unhealthy_production=true**. Before the deploy, ${HEALTH_URL} returned: **${OBSERVED}**." \
          "The outgoing commit and image recorded by this run are NOT a trustworthy rollback target; roll back to last-known-good if this deploy does not recover production."
  exit 0
fi

echo "REFUSED: production is already unhealthy, so nothing will be deployed on top of it."
echo "  ${HEALTH_URL} returned: ${OBSERVED} (after ${ATTEMPTS} attempts, ${TIMEOUT}s each)."
if [ "$LAST_CODE" = "503" ] && grep -q '"starting"' "$BODY_FILE" 2>/dev/null; then
  echo "  The Gateway answered 503 \"starting\": it may be part way through a restart. Wait a minute and run"
  echo "  this deploy again; if it still refuses, production is down and needs recovery."
  summary "The Gateway answered 503 \"starting\", so it may be part way through a restart: wait a minute and run the deploy again before recovering."
fi
echo "  The next action is recovery, not a deploy: run the rollback workflow, for example"
echo "    gh workflow run ${ROLLBACK_WORKFLOW} --ref main -f commit=last-known-good"
echo "  If this deploy IS the fix for the broken production, start it again with"
echo "    -f deploy_to_unhealthy_production=true"
summary "### Deploy REFUSED: production is already unhealthy" \
        "Before anything was changed, ${HEALTH_URL} returned: **${OBSERVED}** (after ${ATTEMPTS} attempts, ${TIMEOUT}s each)." \
        "Recover first with the rollback workflow: \`gh workflow run ${ROLLBACK_WORKFLOW} --ref main -f commit=last-known-good\`." \
        "If this deploy is itself the fix, start it again with \`-f deploy_to_unhealthy_production=true\`."
exit 1

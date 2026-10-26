#!/usr/bin/env bash
# Put production back on the last-known-good image when a deploy's new image never came up (issue #4514).
#
# Called by .github/workflows/deploy-hosted-gateway.yml, in the failure branch of step "Deploy the new
# image in place and measure the outage", after production failed to serve a sustained 200 on the new
# commit for 600 seconds. The run is red whatever happens here; this script only decides what production
# is left running when it ends.
#
# THE TARGET IS THE IMAGE TAGGED last-known-good, AND NOTHING ELSE. Never the outgoing image the deploy
# recorded, never "whatever was live before me", never a second guess when the first one fails:
#   * last-known-good only moves when a deploy passes its ENTIRE watch, and the step that moves it
#     refuses when production is no longer running that image. It is the one record that stays
#     trustworthy once production is down.
#   * The outgoing record does not. On 2026-10-26 the 20:41 deploy recorded its outgoing image as the
#     already-broken image from the 20:38 deploy, and its outgoing commit as "unknown" because production
#     was dead and /healthz could not report one. Going back to it would have restored a broken image.
# So this script is not even handed the outgoing record. Its test sets the outgoing values to that exact
# 2026-10-26 shape and proves they change nothing.
#
# The target is resolved by scripts/resolve-gateway-image.sh, the same resolution the manual rollback
# workflow uses. It cannot dispatch that workflow: the deploy job already holds the
# hosted-gateway-production concurrency group, and a dispatched rollback would wait for the deploy that
# was waiting for it.
#
# ONE TARGET, ONE PIN, ONE WAIT. If last-known-good cannot be resolved, is the very image that just
# failed, cannot be pinned, or is pinned and does not come up, the script says so plainly and stops. Two
# images failing in a row means the fault is very unlikely to be in the code, and pinning more images in
# the hope that one starts only makes it harder to read what happened.
#
# The wait is the deploy's own rule - 3 consecutive 200s, 3 seconds apart, inside 600 seconds - on the
# last-known-good commit, never finishing before ROLLBACK_SETTLE_SECONDS have passed since the pin (the
# manual rollback's rule: right after a pin the old container can still be answering).
#
# It then opens, or comments on, the one alert issue (title below), naming the commit that failed and
# the commit production now serves.
#
# Environment, required:
#   GATEWAY_URL  ACR  IMAGE_NAME  AZURE_RG  APP   as in the deploy workflow
#   FAILED_COMMIT                                 the short commit this deploy shipped
#   FAILED_DIGEST                                 the sha256 digest this deploy pinned
# Environment, optional:
#   RUN_URL                       the deploy run, linked from the issue
#   GH_TOKEN, GH_REPO             for the alert issue (the token needs issues: write)
#   GITHUB_STEP_SUMMARY           the verdict is appended to it
#   GITHUB_OUTPUT                 auto_rollback=<outcome> and serving_commit_after_rollback=<commit>
#   ROLLBACK_DEADLINE_SECONDS     default 600
#   ROLLBACK_SETTLE_SECONDS       default 150
#   ROLLBACK_POLL_SECONDS         default 3
#   ROLLBACK_REQUEST_TIMEOUT      per /healthz request, default 10
#
# Outcomes (auto_rollback): recovered | not-recovered | pin-failed | unresolved | same-as-failed
# Exit codes: 0 production recovered on last-known-good; 1 the pin failed or did not come up;
#             3 no rollback was attempted (last-known-good unresolved, or it is the failed image);
#             2 bad usage.
#
# The proof is scripts/rollback-to-last-known-good.test.sh, against a fake az, a fake gh and a fake
# /healthz. Run it by hand after every change to this script: the C# guard only reads this file's text.

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
RESOLVER="$HERE/resolve-gateway-image.sh"
ISSUE_TITLE="Hosted Gateway deploy failed and was rolled back automatically"

for v in GATEWAY_URL ACR IMAGE_NAME AZURE_RG APP FAILED_COMMIT FAILED_DIGEST; do
  if [ -z "${!v:-}" ]; then
    echo "usage: $v is not set. Required: GATEWAY_URL ACR IMAGE_NAME AZURE_RG APP FAILED_COMMIT FAILED_DIGEST" >&2
    exit 2
  fi
done
GATEWAY_URL=${GATEWAY_URL%/}
DEADLINE_SECONDS=${ROLLBACK_DEADLINE_SECONDS:-600}
SETTLE_SECONDS=${ROLLBACK_SETTLE_SECONDS:-150}
POLL_SECONDS=${ROLLBACK_POLL_SECONDS:-3}
REQUEST_TIMEOUT=${ROLLBACK_REQUEST_TIMEOUT:-10}

LKG_DIGEST=""
LKG_COMMIT=""
LKG_REF=""
SERVING_COMMIT=""
DETAIL=""

serving_commit() {
  curl -s -m "$REQUEST_TIMEOUT" "${GATEWAY_URL}/healthz" 2>/dev/null | jq -r '.commit // empty' 2>/dev/null || true
}

set_output() {
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      echo "auto_rollback=$1"
      echo "serving_commit_after_rollback=$SERVING_COMMIT"
    } >> "$GITHUB_OUTPUT"
  fi
}

# The alert issue. Its title is the lookup key - an open issue with it gets a comment, so repeated
# failures gather in one place instead of a new issue each. Failing to write it is reported loudly but
# never changes the outcome: production's state is the thing that matters, and it is already decided.
raise_issue() {
  local headline=$1
  local body
  body="$(printf '%s\n' \
    "## ${headline}" \
    "" \
    "| | |" \
    "|---|---|" \
    "| Commit that failed to come up | \`${FAILED_COMMIT}\` (\`${FAILED_DIGEST}\`) |" \
    "| Rollback target | last-known-good: commit \`${LKG_COMMIT:-unknown}\` (\`${LKG_DIGEST:-not resolved}\`) |" \
    "| Commit production serves now | \`${SERVING_COMMIT:-none - /healthz reported no commit}\` |" \
    "| Deploy run | ${RUN_URL:-not given} |" \
    "" \
    "${DETAIL}" \
    "" \
    "The deploy run is red. An automatic rollback is a recovery, never a successful deploy." \
    "The failing container's own startup output is in the platform container log for \`${APP}\`." \
    "Do not redeploy \`${FAILED_COMMIT}\` until the reason it did not start is named.")"
  if ! command -v gh >/dev/null 2>&1; then
    echo "WARNING: the gh tool is not available, so the alert issue was NOT written. Its text:"
    printf '%s\n' "$body"
    return 0
  fi
  local open
  open=$(gh issue list --state open --search "\"${ISSUE_TITLE}\" in:title" --json number --jq '.[0].number // empty' 2>/dev/null)
  if [ -n "$open" ]; then
    if gh issue comment "$open" --body "$body" >/dev/null; then
      echo "Alert written: commented on the open issue #${open} (\"${ISSUE_TITLE}\")."
      return 0
    fi
  else
    local made
    if made=$(gh issue create --title "$ISSUE_TITLE" --body "$body"); then
      echo "Alert written: opened ${made} (\"${ISSUE_TITLE}\")."
      return 0
    fi
  fi
  echo "WARNING: writing the alert issue FAILED. Nobody has been told by issue. Its text:"
  printf '%s\n' "$body"
  return 0
}

summary() {
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '%s\n' "$@" >> "$GITHUB_STEP_SUMMARY"
  fi
}

# finish <outcome> <exit code> <headline> - the one way out, so every outcome writes its output, its
# summary and its issue.
finish() {
  local outcome=$1 code=$2 headline=$3
  echo ""
  echo "AUTOMATIC ROLLBACK: ${headline}"
  [ -n "$DETAIL" ] && printf '%s\n' "$DETAIL"
  echo "This deploy is still a FAILURE: the run ends red."
  set_output "$outcome"
  summary "### Deploy FAILED - automatic rollback: ${outcome}" \
          "${headline}" \
          "" \
          "Commit that failed: \`${FAILED_COMMIT}\`. Commit production serves now: \`${SERVING_COMMIT:-none}\`." \
          "" \
          "${DETAIL}"
  raise_issue "$headline"
  exit "$code"
}

echo "AUTOMATIC ROLLBACK: commit ${FAILED_COMMIT} never came up. Putting production back on last-known-good."
echo "  The target is the image tagged last-known-good, and only that image (issue #4514)."

# 1. RESOLVE. Before anything is changed, so a missing tag leaves production exactly as it is.
if ! resolved=$(bash "$RESOLVER" last-known-good); then
  SERVING_COMMIT=$(serving_commit)
  DETAIL="The registry has no image tagged last-known-good, so there is nothing trustworthy to go back to. NO ROLLBACK WAS ATTEMPTED and production is still pinned to the failed image ${FAILED_DIGEST}. PRODUCTION IS PROBABLY STILL DOWN. A person must choose a commit and run the rollback workflow (rollback-hosted-gateway.yml) by hand."
  finish unresolved 3 "last-known-good could not be resolved; production was NOT rolled back."
fi
LKG_DIGEST=$(printf '%s\n' "$resolved" | sed -n 's/^digest=//p')
LKG_COMMIT=$(printf '%s\n' "$resolved" | sed -n 's/^commit=//p')
LKG_REF="${ACR}.azurecr.io/${IMAGE_NAME}@${LKG_DIGEST}"

# 2. NEVER RE-PIN THE IMAGE THAT JUST FAILED. It would only restart the same failure.
if [ "$LKG_DIGEST" = "$FAILED_DIGEST" ]; then
  SERVING_COMMIT=$(serving_commit)
  DETAIL="last-known-good is the very image this deploy pinned (${FAILED_DIGEST}), so re-pinning it would only restart the same failure. NO ROLLBACK WAS ATTEMPTED. PRODUCTION IS PROBABLY STILL DOWN. A person must choose an older commit and run the rollback workflow (rollback-hosted-gateway.yml) by hand."
  finish same-as-failed 3 "last-known-good is the image that just failed; production was NOT rolled back."
fi

echo "  last-known-good = commit ${LKG_COMMIT:-<unknown: no ci- tag left on it>} (${LKG_DIGEST})"

# 3. PIN, ONCE.
started=$(date +%s)
if ! az webapp config container set --resource-group "$AZURE_RG" --name "$APP" \
       --docker-custom-image-name "$LKG_REF" --output none; then
  SERVING_COMMIT=$(serving_commit)
  DETAIL="Pinning ${LKG_REF} was refused by Azure, so production is still pinned to the failed image ${FAILED_DIGEST}. PRODUCTION IS PROBABLY STILL DOWN. No other image was tried."
  finish pin-failed 1 "the re-pin of last-known-good failed; production was NOT rolled back."
fi
echo "Production pinned to last-known-good ${LKG_REF} at $(date -u +%H:%M:%S); waiting for a sustained 200..."

# 4. WAIT, ONCE: the deploy's rule (3 in a row, POLL_SECONDS apart, inside DEADLINE_SECONDS), never
# finishing before SETTLE_SECONDS since the pin. With the commit known, only that commit counts. With it
# unknown (the ci- tag was purged), any reported commit except the failed one counts - the failed
# container answering is not a recovery - and the result says the commit was not checked.
recovered=no
streak=0
deadline=$(( started + DEADLINE_SECONDS ))
while [ "$(date +%s)" -lt "$deadline" ]; do
  body=$(curl -s -m "$REQUEST_TIMEOUT" -w '\n%{http_code}' "${GATEWAY_URL}/healthz" 2>/dev/null || printf '\n000')
  code=$(printf '%s' "$body" | tail -n1)
  commit=$(printf '%s' "$body" | sed '$d' | jq -r '.commit // empty' 2>/dev/null || echo "")
  elapsed=$(( $(date +%s) - started ))
  good=no
  if [ "$code" = "200" ]; then
    if [ -n "$LKG_COMMIT" ]; then
      [ "$commit" = "$LKG_COMMIT" ] && good=yes
    else
      [ -n "$commit" ] && [ "$commit" != "$FAILED_COMMIT" ] && good=yes
    fi
  fi
  if [ "$good" = "yes" ]; then
    streak=$(( streak + 1 ))
    echo "production 200 commit=${commit} (streak $streak, ${elapsed}s since the rollback pin)"
    if [ "$streak" -ge 3 ] && [ "$elapsed" -ge "$SETTLE_SECONDS" ]; then recovered=yes; break; fi
  else
    streak=0
    echo "production HTTP $code commit=${commit:-<none>} (rolling back, ${elapsed}s since the pin)"
  fi
  sleep "$POLL_SECONDS"
done

SERVING_COMMIT=$(serving_commit)
if [ "$recovered" = "yes" ]; then
  if [ -n "$LKG_COMMIT" ]; then
    DETAIL="Production is serving last-known-good commit ${LKG_COMMIT} again: 3 consecutive 200s on it, ${SETTLE_SECONDS}s or more after the rollback pin."
  else
    DETAIL="Production is serving again on commit ${SERVING_COMMIT:-unknown}: 3 consecutive 200s, ${SETTLE_SECONDS}s or more after the rollback pin. last-known-good carries no ci- tag any more, so the commit could not be checked against the image - only that it is not the failed one."
  fi
  finish recovered 0 "production was put back on last-known-good and came up."
fi

DETAIL="Production is pinned to last-known-good ${LKG_REF}, and it did NOT serve a sustained 200${LKG_COMMIT:+ on commit ${LKG_COMMIT}} within ${DEADLINE_SECONDS}s. PRODUCTION IS PROBABLY DOWN. Two images failed to come up in a row, so the fault is very unlikely to be the code: look at the platform container log for ${APP}, and at whether the site itself was stopped. No other image was tried, and none should be pinned in the hope that it starts."
finish not-recovered 1 "last-known-good was pinned but production did NOT come back."

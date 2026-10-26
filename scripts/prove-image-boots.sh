#!/usr/bin/env bash
# Prove a hosted Gateway image BOOTS and SERVES, before production is ever asked to run it (issue #4513).
#
#   scripts/prove-image-boots.sh <image> <expected-commit>
#
# The deploy workflow calls this on the exact digest it is about to pin, after the digest is read and
# before "Deploy the new image in place and measure the outage". The same script runs on any machine with
# Docker against any image, so a failing deploy can be reproduced locally with the same code.
#
# WHY. The only other pre-deploy image check proves the NEGATIVE: with the hosted contract blank, every
# entry refuses to start. Nothing asked whether the image starts WITH the contract, so production asked it
# first - on 2026-10-26 commit 86b37ace5 (#4501) was pinned and never answered HTTP (#4516).
#
# WHAT IT DOES. Starts a throwaway PostgreSQL, then the image with the hosted contract satisfied
# (src/CcDirector.Gateway/HostedStartupContract.cs): CC_GATEWAY_HOSTED=1, auth left on (neither
# CC_GATEWAY_NO_AUTH nor CC_GATEWAY_AUTH is set), CC_GATEWAY_PUBLIC_URL=https://smoke.invalid, and
# CC_GATEWAY_DB_CONNECTION pointing at that database. It then polls /healthz.
#
# THE PASS CONDITION IS A PRESENCE: three consecutive HTTP 200 answers on /healthz, three seconds apart,
# each with a body that parses as a JSON object whose "commit" equals <expected-commit>, all inside
# BOOT_TIMEOUT_SECONDS. An empty body, a body that does not parse, a missing or different commit, a
# non-200 answer or no answer at all breaks the run of passes and it starts again from zero. A container
# that exits fails at once. Anything short of three passes by the deadline fails.
#
# ON FAILURE it prints the container's stdout and stderr (the output that otherwise exists only in the
# Azure platform log) and exits 1. Both containers and the network are removed whatever happens.
#
# Its behaviour is proved against fake Gateway images by scripts/prove-image-boots.test.sh, run by the web job of
# .github/workflows/ci.yml.
#
# Environment (all optional):
#   BOOT_TIMEOUT_SECONDS  the bounded wait for three passes, counted from the Gateway container's start (default 180)
#   BOOT_NAME_SUFFIX      appended to the container and network names, so two runs never collide (default: process id)
set -euo pipefail

if [ "$#" -ne 2 ] || [ -z "$1" ] || [ -z "$2" ]; then
  echo "usage: $0 <image> <expected-commit>" >&2
  exit 2
fi

IMAGE="$1"
EXPECTED_COMMIT="$2"
BOOT_TIMEOUT_SECONDS="${BOOT_TIMEOUT_SECONDS:-180}"
SUFFIX="${BOOT_NAME_SUFFIX:-$$}"
REQUIRED_PASSES=3
PASS_INTERVAL_SECONDS=3
DB_WAIT_SECONDS=60

NET="boot-proof-net-$SUFFIX"
DB="boot-proof-db-$SUFFIX"
GW="boot-proof-gw-$SUFFIX"
DB_PASSWORD="boot-proof-$SUFFIX"

for tool in docker curl jq; do
  command -v "$tool" >/dev/null || { echo "ERROR: $tool is required and is not on PATH." >&2; exit 2; }
done

cleanup() {
  docker rm -f "$GW" "$DB" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

print_gateway_output() {
  echo "----- the Gateway container's state -----"
  docker inspect -f 'running={{.State.Running}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}} error={{.State.Error}}' "$GW" 2>&1 || true
  echo "----- the Gateway container's stdout and stderr -----"
  docker logs "$GW" 2>&1 || true
  echo "----- end of the Gateway container's output -----"
}

fail() {
  echo "BOOT PROOF FAILED: $1"
  print_gateway_output
  exit 1
}

echo "Boot proof: image $IMAGE, expected commit $EXPECTED_COMMIT, bounded wait ${BOOT_TIMEOUT_SECONDS}s."

docker network create "$NET" >/dev/null

# The throwaway database. Ready means it accepts TCP connections, which the official image only does after
# its first-start initialisation has finished (during it the server listens on the local socket alone).
docker run -d --name "$DB" --network "$NET" \
  -e POSTGRES_PASSWORD="$DB_PASSWORD" -e POSTGRES_DB=gateway \
  postgres:16 >/dev/null
db_deadline=$(( $(date +%s) + DB_WAIT_SECONDS ))
until docker exec "$DB" pg_isready -h 127.0.0.1 -U postgres -d gateway >/dev/null 2>&1; do
  if [ "$(date +%s)" -ge "$db_deadline" ]; then
    echo "ERROR: the throwaway PostgreSQL did not accept connections within ${DB_WAIT_SECONDS}s. This is the proof's own"
    echo "       database, not the image under test, so it says nothing about the image."
    docker logs "$DB" 2>&1 || true
    exit 1
  fi
  sleep 1
done
echo "Throwaway PostgreSQL is accepting connections."

# The image under test, with the hosted contract satisfied and nothing else added: its own baked
# environment (CC_GATEWAY_HOSTED=1, COCKPIT_COMMIT, the entrypoint and port) is what production runs.
# CC_GATEWAY_HOSTED=1 is passed as well so the proof does not depend on the image still baking it.
docker run -d --name "$GW" --network "$NET" -p 127.0.0.1::7878 \
  -e CC_GATEWAY_HOSTED=1 \
  -e CC_GATEWAY_PUBLIC_URL=https://smoke.invalid \
  -e CC_GATEWAY_DB_CONNECTION="Host=$DB;Port=5432;Database=gateway;Username=postgres;Password=$DB_PASSWORD" \
  "$IMAGE" >/dev/null
started=$(date +%s)
deadline=$(( started + BOOT_TIMEOUT_SECONDS ))

HOST_PORT=$(docker port "$GW" 7878/tcp | head -n1 | sed 's/.*://')
[ -n "$HOST_PORT" ] || fail "docker did not publish the container's port 7878."
URL="http://127.0.0.1:$HOST_PORT/healthz"
echo "Polling $URL"

passes=0
while :; do
  if [ "$(docker inspect -f '{{.State.Running}}' "$GW" 2>/dev/null)" != "true" ]; then
    fail "the Gateway container exited after $(( $(date +%s) - started ))s instead of serving."
  fi

  response=$(curl -s -m 5 -w '\n%{http_code}' "$URL" 2>/dev/null || printf '\n000')
  code=$(printf '%s' "$response" | tail -n1)
  body=$(printf '%s' "$response" | sed '$d')
  elapsed=$(( $(date +%s) - started ))

  verdict=""
  if [ "$code" != "200" ]; then
    verdict="HTTP $code"
  elif [ -z "$body" ]; then
    verdict="HTTP 200 with an EMPTY body"
  elif ! printf '%s' "$body" | jq -e 'type == "object"' >/dev/null 2>&1; then
    verdict="HTTP 200 with a body that is not a JSON object"
  else
    commit=$(printf '%s' "$body" | jq -r '.commit // empty')
    if [ "$commit" != "$EXPECTED_COMMIT" ]; then
      verdict="HTTP 200 carrying commit '${commit:-<none>}', not $EXPECTED_COMMIT"
    fi
  fi

  if [ -z "$verdict" ]; then
    passes=$(( passes + 1 ))
    echo "  ${elapsed}s: pass $passes of $REQUIRED_PASSES - HTTP 200, commit $EXPECTED_COMMIT, status $(printf '%s' "$body" | jq -r '.status // "<none>"')"
    if [ "$passes" -ge "$REQUIRED_PASSES" ]; then
      break
    fi
  else
    [ "$passes" -gt 0 ] && echo "  ${elapsed}s: the run of passes is broken, starting again from zero"
    passes=0
    echo "  ${elapsed}s: not yet - $verdict"
  fi

  if [ "$(( $(date +%s) + PASS_INTERVAL_SECONDS ))" -gt "$deadline" ]; then
    fail "no $REQUIRED_PASSES consecutive healthy answers within ${BOOT_TIMEOUT_SECONDS}s (last: ${verdict:-pass $passes})."
  fi
  sleep "$PASS_INTERVAL_SECONDS"
done

echo "BOOT PROOF PASSED: $IMAGE served $REQUIRED_PASSES consecutive healthy /healthz answers on commit $EXPECTED_COMMIT, ${PASS_INTERVAL_SECONDS}s apart, $(( $(date +%s) - started ))s after it started."
echo "----- the last lines the Gateway printed -----"
docker logs --tail 30 "$GW" 2>&1 || true

#!/usr/bin/env bash
# Proves scripts/check-production-healthy.sh against a local fake /healthz (issue #4515).
#
# Nobody can make the real production unhealthy on purpose, so every shape it can take is served here
# by a small Python server on 127.0.0.1, one path prefix per shape, and the probe is pointed at each.
# Each case checks the exit code AND the words printed, so a probe that refuses for the wrong reason
# fails here too. Needs bash, curl, jq and python3. Run from anywhere:
#
#   bash scripts/check-production-healthy.test.sh

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
PROBE="$HERE/check-production-healthy.sh"
WORK=$(mktemp -d)
SERVER_PID=""
cleanup() {
  if [ -n "$SERVER_PID" ]; then kill "$SERVER_PID" 2>/dev/null; wait "$SERVER_PID" 2>/dev/null; fi
  rm -rf "$WORK"
}
trap cleanup EXIT

cat > "$WORK/fake_healthz.py" <<'PY'
import sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        shape = self.path.strip("/").split("/")[0]
        if shape == "healthy":
            self.reply(200, b'{"status":"ok","commit":"abc1234"}')
        elif shape == "bad-gateway":
            self.reply(502, b"502 - Web server received an invalid response while acting as a gateway")
        elif shape == "hang":
            time.sleep(30)
            self.reply(200, b'{"commit":"too-late"}')
        elif shape == "empty":
            self.reply(200, b"")
        elif shape == "no-commit":
            self.reply(200, b'{"status":"ok"}')
        elif shape == "empty-commit":
            self.reply(200, b'{"status":"ok","commit":""}')
        elif shape == "not-json":
            self.reply(200, b"<html>Service Unavailable</html>")
        else:
            self.reply(404, b"unknown shape")

    def reply(self, code, body):
        try:
            self.send_response(code)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, *args):
        pass

server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
server.daemon_threads = True
print(server.server_address[1], flush=True)
server.serve_forever()
PY

python3 "$WORK/fake_healthz.py" > "$WORK/port" &
SERVER_PID=$!
for _ in $(seq 1 50); do [ -s "$WORK/port" ] && break; sleep 0.1; done
PORT=$(cat "$WORK/port")
if [ -z "$PORT" ]; then echo "FAIL: the fake /healthz server did not start"; exit 1; fi
BASE="http://127.0.0.1:${PORT}"

# A port nothing listens on: bind one, read its number, close it.
REFUSED_PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')

export HEALTH_TIMEOUT_SECONDS=2 HEALTH_ATTEMPTS=2 HEALTH_RETRY_DELAY_SECONDS=0
failures=0
# run_case <name> <url> <expected exit> <expected words> [override] [expected summary words]
run_case() {
  local name=$1 url=$2 want_exit=$3 want_text=$4 override=${5:-false} want_summary=${6:-}
  local summary_file="$WORK/summary-$name" output_file="$WORK/output-$name"
  : > "$summary_file"; : > "$output_file"
  local start end got_exit
  start=$(date +%s)
  GITHUB_STEP_SUMMARY="$summary_file" GITHUB_OUTPUT="$output_file" DEPLOY_TO_UNHEALTHY_PRODUCTION="$override" \
    bash "$PROBE" "$url" > "$WORK/stdout-$name" 2>&1
  got_exit=$?
  end=$(date +%s)
  local ok=true
  [ "$got_exit" -eq "$want_exit" ] || ok=false
  grep -qF -- "$want_text" "$WORK/stdout-$name" || ok=false
  if [ -n "$want_summary" ]; then grep -qF -- "$want_summary" "$summary_file" || ok=false; fi
  if [ "$ok" = true ]; then
    echo "PASS  $name (exit $got_exit, $((end - start))s)"
  else
    echo "FAIL  $name: wanted exit $want_exit with '$want_text'${want_summary:+ and summary '$want_summary'}, got exit $got_exit"
    failures=$((failures + 1))
  fi
  sed 's/^/        /' "$WORK/stdout-$name"
  if [ -s "$summary_file" ]; then echo "        -- run summary --"; sed 's/^/        /' "$summary_file"; fi
}

run_case healthy        "$BASE/healthy"                 0 "Production health gate passed: production is healthy on its own commit abc1234."
grep -qx "commit=abc1234" "$WORK/output-healthy" \
  && echo "PASS  healthy writes commit=abc1234 to the step output" \
  || { echo "FAIL  healthy did not write commit=abc1234 to the step output"; failures=$((failures + 1)); }

run_case bad-gateway    "$BASE/bad-gateway"             1 "returned: HTTP 502"
run_case refused        "http://127.0.0.1:${REFUSED_PORT}" 1 "returned: no HTTP response (connection failed"
run_case hang           "$BASE/hang"                    1 "returned: no answer within 2s (timed out; HTTP 000)"
run_case empty-body     "$BASE/empty"                   1 "returned: HTTP 200 with an empty body"
run_case no-commit      "$BASE/no-commit"               1 "returned: HTTP 200 but the body carries no commit"
run_case empty-commit   "$BASE/empty-commit"            1 "returned: HTTP 200 but the body carries no commit"
run_case not-json       "$BASE/not-json"                1 "returned: HTTP 200 but the body is not JSON"

# Every refusal says production is already unhealthy and points at the rollback workflow.
run_case refusal-words  "$BASE/bad-gateway"             1 "REFUSED: production is already unhealthy" false \
  "gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good"

# The escape hatch: the deploy goes on, and the run summary says so loudly, naming what /healthz returned.
run_case escape-hatch   "$BASE/bad-gateway"             0 "PRODUCTION IS ALREADY UNHEALTHY, AND THIS DEPLOY IS GOING AHEAD ANYWAY" true \
  "### WARNING: deployed onto UNHEALTHY production - the production health gate was SKIPPED"
grep -qF "returned: **HTTP 502" "$WORK/summary-escape-hatch" \
  && echo "PASS  escape-hatch summary names what /healthz returned" \
  || { echo "FAIL  escape-hatch summary does not name what /healthz returned"; failures=$((failures + 1)); }
run_case escape-hatch-healthy "$BASE/healthy"           0 "it changed nothing" true \
  "deploy_to_unhealthy_production was set but not needed"

# The gate never waits for minutes: a hang costs at most attempts x timeout plus the delays.
HEALTH_ATTEMPTS=3 HEALTH_TIMEOUT_SECONDS=2 run_case hang-is-bounded "$BASE/hang" 1 "(after 3 attempts, 2s each)"

echo
if [ "$failures" -eq 0 ]; then
  echo "ALL CASES PASSED"
  exit 0
fi
echo "$failures CASE(S) FAILED"
exit 1

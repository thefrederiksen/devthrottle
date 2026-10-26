#!/usr/bin/env bash
# Proves scripts/prove-image-boots.sh against fake Gateway images (issue #4513).
#
# The C# guard (HostedImageBootsBeforeEveryDeployTests) only reads the script's text, so an `exit 0` at its
# top would leave that guard green. This runs the script for real. A tiny Python server in a container
# stands in for the Gateway on port 7878 and answers /healthz in one shape per case (chosen by the SHAPE
# environment variable baked into each case's image). Each case checks the exit code AND the words
# printed, so a script that fails for the wrong reason fails here too. Needs bash, docker, curl and jq.
# The web job of .github/workflows/ci.yml runs it; run it by hand too after any change to the script:
#
#   bash scripts/prove-image-boots.test.sh

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
PROOF="$HERE/prove-image-boots.sh"
WORK=$(mktemp -d)
TAG="boot-proof-fake-$$"
cleanup_tags=()
cleanup() {
  for t in "${cleanup_tags[@]}"; do docker image rm -f "$t" >/dev/null 2>&1 || true; done
  rm -rf "$WORK"
}
trap cleanup EXIT

cat > "$WORK/fake_gateway.py" <<'PY'
import os, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SHAPE = os.environ["SHAPE"]
print(f"fake gateway starting, shape={SHAPE}", flush=True)
if SHAPE == "crash":
    print("FAKE GATEWAY CRASHED: the startup threw", file=sys.stderr, flush=True)
    sys.exit(3)
if SHAPE == "silent":
    time.sleep(3600)

count = 0

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        global count
        count += 1
        good = b'{"status":"ok","commit":"abc1234"}'
        if SHAPE == "healthy":
            self.reply(200, good)
        elif SHAPE == "starting-then-healthy":
            self.reply(503, b'{"status":"starting","commit":"abc1234"}') if count <= 2 else self.reply(200, good)
        elif SHAPE == "flapping":
            self.reply(503, b'{"status":"starting"}') if count % 3 == 0 else self.reply(200, good)
        elif SHAPE == "empty":
            self.reply(200, b"")
        elif SHAPE == "not-json":
            self.reply(200, b"<html>Service Unavailable</html>")
        elif SHAPE == "array":
            self.reply(200, b'[{"commit":"abc1234"}]')
        elif SHAPE == "no-commit":
            self.reply(200, b'{"status":"ok"}')
        elif SHAPE == "wrong-commit":
            self.reply(200, b'{"status":"ok","commit":"fff0000"}')
        else:
            self.reply(404, b"unknown shape")

    def reply(self, code, body):
        self.send_response(code)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass

ThreadingHTTPServer(("0.0.0.0", 7878), Handler).serve_forever()
PY

cat > "$WORK/Dockerfile" <<'DOCKERFILE'
FROM python:3.12-alpine
COPY fake_gateway.py /fake_gateway.py
ENTRYPOINT ["python3", "-u", "/fake_gateway.py"]
DOCKERFILE

if ! docker build -q -t "$TAG:base" "$WORK" >/dev/null; then
  echo "FAIL: could not build the fake Gateway image"
  exit 1
fi
cleanup_tags+=("$TAG:base")

export BOOT_TIMEOUT_SECONDS=15
failures=0
# run_case <shape> <expected exit> <expected words> [more expected words]
run_case() {
  local shape=$1 want_exit=$2 want_text=$3 want_more=${4:-}
  local image="$TAG:$shape"
  printf 'FROM %s\nENV SHAPE=%s\n' "$TAG:base" "$shape" | docker build -q -t "$image" - >/dev/null
  cleanup_tags+=("$image")
  local start end got_exit
  start=$(date +%s)
  BOOT_NAME_SUFFIX="test-$$-$shape" bash "$PROOF" "$image" abc1234 > "$WORK/out-$shape" 2>&1
  got_exit=$?
  end=$(date +%s)
  local ok=true
  [ "$got_exit" -eq "$want_exit" ] || ok=false
  grep -qF -- "$want_text" "$WORK/out-$shape" || ok=false
  if [ -n "$want_more" ]; then grep -qF -- "$want_more" "$WORK/out-$shape" || ok=false; fi
  # Nothing is left behind, pass or fail.
  if [ -n "$(docker ps -aq --filter "name=boot-proof-.*-test-$$-$shape")" ] \
     || [ -n "$(docker network ls -q --filter "name=boot-proof-net-test-$$-$shape")" ]; then
    echo "        a container or network was left behind"
    ok=false
  fi
  if [ "$ok" = true ]; then
    echo "PASS  $shape (exit $got_exit, $((end - start))s)"
  else
    echo "FAIL  $shape: wanted exit $want_exit with '$want_text'${want_more:+ and '$want_more'}, got exit $got_exit"
    failures=$((failures + 1))
  fi
  sed 's/^/        /' "$WORK/out-$shape"
}

run_case healthy              0 "BOOT PROOF PASSED"
run_case starting-then-healthy 0 "BOOT PROOF PASSED" "not yet - HTTP 503"

# Every failure prints what the container itself said.
run_case crash                1 "the Gateway container exited after" "FAKE GATEWAY CRASHED: the startup threw"
run_case silent               1 "no 3 consecutive healthy answers within 15s (last: HTTP 000)" "fake gateway starting, shape=silent"
run_case empty                1 "HTTP 200 with an EMPTY body" "fake gateway starting, shape=empty"
run_case not-json             1 "HTTP 200 with a body that is not a JSON object"
run_case array                1 "HTTP 200 with a body that is not a JSON object"
run_case no-commit            1 "HTTP 200 carrying commit '<none>', not abc1234"
run_case wrong-commit         1 "HTTP 200 carrying commit 'fff0000', not abc1234"
run_case flapping             1 "the run of passes is broken, starting again from zero"

if [ "$failures" -ne 0 ]; then
  echo "$failures case(s) FAILED"
  exit 1
fi
echo "All boot proof cases passed."

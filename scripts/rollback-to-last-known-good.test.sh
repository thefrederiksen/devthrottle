#!/usr/bin/env bash
# Proves scripts/rollback-to-last-known-good.sh and scripts/resolve-gateway-image.sh against fakes
# (issue #4514). This is the dry run of the automatic rollback: nobody can break production on purpose,
# so the registry, the production site and the issue tracker are all played here.
#
#   * a fake `az` on PATH reads a tag table and records every image pin it is asked for;
#   * a fake /healthz on 127.0.0.1 answers for whatever image the fake `az` last pinned;
#   * a fake `gh` records the alert issue it is asked to open or comment on.
#
# Every case checks the exit code, the words printed, and what was PINNED, so a rollback that picks the
# wrong image fails here even when it "recovers". Needs bash, curl, jq and python3. The web job of
# .github/workflows/ci.yml runs it; run it by hand too after any change to either script:
#
#   bash scripts/rollback-to-last-known-good.test.sh

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
ROLLBACK="$HERE/rollback-to-last-known-good.sh"
RESOLVER="$HERE/resolve-gateway-image.sh"
WORK=$(mktemp -d)
SERVER_PID=""
cleanup() {
  if [ -n "$SERVER_PID" ]; then kill "$SERVER_PID" 2>/dev/null; wait "$SERVER_PID" 2>/dev/null; fi
  rm -rf "$WORK"
}
trap cleanup EXIT

D_BAD="sha256:ee646d181b87000000000000000000000000000000000000000000000000bad1"
D_LKG="sha256:1111111111110000000000000000000000000000000000000000000000000001"
D_OLD="sha256:2222222222220000000000000000000000000000000000000000000000000002"
FAILED=bad5678
LKG=lkg1234

mkdir -p "$WORK/bin"
# ---- fake az ------------------------------------------------------------------------------------------
# State lives in $FAKE: `tags` (lines "<tag> <digest>"), `pins` (every image pinned, in order),
# `live` (the digest production is pinned to), and `pin-fails` (present = the pin command fails).
cat > "$WORK/bin/az" <<'AZ'
#!/usr/bin/env bash
args="$*"
arg_after() { local want=$1; shift; while [ $# -gt 0 ]; do [ "$1" = "$want" ] && { echo "$2"; return; }; shift; done; }
case "$args" in
  "acr repository show "*)
    ref=$(arg_after --image "$@")
    case "$ref" in
      *@sha256:*) d=${ref#*@}; grep -q " ${d}\$" "$FAKE/tags" && { echo "$d"; exit 0; }; exit 1 ;;
      *:*) t=${ref#*:}; d=$(awk -v t="$t" '$1==t {print $2; exit}' "$FAKE/tags"); [ -n "$d" ] && { echo "$d"; exit 0; }; exit 1 ;;
    esac
    exit 1 ;;
  "acr repository show-tags "*)
    awk '{print $1}' "$FAKE/tags" ;;
  "acr manifest list-metadata "*)
    q=$(arg_after --query "$@"); d=$(printf '%s' "$q" | sed -n "s/.*digest=='\([^']*\)'.*/\1/p")
    awk -v d="$d" '$2==d {print $1}' "$FAKE/tags" ;;
  "webapp config container set "*)
    ref=$(arg_after --docker-custom-image-name "$@")
    echo "$ref" >> "$FAKE/pins"
    [ -f "$FAKE/pin-fails" ] && { echo "ERROR: (fake) the site refused the image" >&2; exit 1; }
    echo "${ref#*@}" > "$FAKE/live" ;;
  *)
    echo "fake az: unexpected call: $args" >&2; echo "$args" >> "$FAKE/unexpected"; exit 1 ;;
esac
AZ
# ---- fake gh ------------------------------------------------------------------------------------------
# `open-issue` holds the number of an already-open alert issue, if any; `gh-fails` makes every write fail.
cat > "$WORK/bin/gh" <<'GH'
#!/usr/bin/env bash
case "$1 $2" in
  "issue list") cat "$FAKE/open-issue" 2>/dev/null; exit 0 ;;
  "issue create")
    [ -f "$FAKE/gh-fails" ] && exit 1
    printf '%s\n' "$@" > "$FAKE/issue-created"; echo "https://github.com/example/repo/issues/999" ;;
  "issue comment")
    [ -f "$FAKE/gh-fails" ] && exit 1
    printf '%s\n' "$@" > "$FAKE/issue-commented" ;;
  *) echo "fake gh: unexpected call: $*" >&2; echo "gh $*" >> "$FAKE/unexpected"; exit 1 ;;
esac
GH
chmod +x "$WORK/bin/az" "$WORK/bin/gh"

# ---- fake /healthz ------------------------------------------------------------------------------------
# Answers from the CURRENT case's `serving` table (lines "<digest> <http code> <commit or ->") for the
# digest in its `live` file. A digest not in the table answers 502, like a dead app behind Azure's front.
cat > "$WORK/fake_healthz.py" <<'PY'
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CASE_FILE = os.environ["CASE_FILE"]

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        fake = open(CASE_FILE).read().strip()
        try:
            live = open(os.path.join(fake, "live")).read().strip()
            table = [l.split() for l in open(os.path.join(fake, "serving")) if l.strip()]
        except OSError:
            live, table = "", []
        for digest, code, commit in table:
            if digest == live:
                body = b'{"status":"ok"}' if commit == "-" else ('{"status":"ok","commit":"%s"}' % commit).encode()
                return self.reply(int(code), body)
        self.reply(502, b"502 - Web server received an invalid response while acting as a gateway")

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

echo "" > "$WORK/case"
CASE_FILE="$WORK/case" python3 "$WORK/fake_healthz.py" > "$WORK/port" &
SERVER_PID=$!
for _ in $(seq 1 50); do [ -s "$WORK/port" ] && break; sleep 0.1; done
PORT=$(cat "$WORK/port")
if [ -z "$PORT" ]; then echo "FAIL: the fake /healthz server did not start"; exit 1; fi

export PATH="$WORK/bin:$PATH"
export GATEWAY_URL="http://127.0.0.1:${PORT}" ACR=crfake IMAGE_NAME=devthrottle-gateway AZURE_RG=rg-fake APP=app-fake
export ROLLBACK_DEADLINE_SECONDS=4 ROLLBACK_SETTLE_SECONDS=0 ROLLBACK_POLL_SECONDS=0.2 ROLLBACK_REQUEST_TIMEOUT=2
export RUN_URL="https://github.com/example/repo/actions/runs/1"

failures=0
pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; failures=$((failures + 1)); }

# new_case <name>: a fresh fake world, production pinned to the failed image (the state after a deploy
# whose new image never came up). The default registry: last-known-good on D_LKG with its ci- tag, the
# failed image, and one OLDER image that is neither.
new_case() {
  FAKE="$WORK/$1"; export FAKE; mkdir -p "$FAKE"
  echo "$FAKE" > "$WORK/case"
  printf '%s\n' "ci-${FAILED} ${D_BAD}" "live ${D_BAD}" "ci-old9999 ${D_OLD}" "last-known-good ${D_LKG}" "ci-${LKG} ${D_LKG}" > "$FAKE/tags"
  printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 200 ${LKG}" "${D_OLD} 200 old9999" > "$FAKE/serving"
  echo "$D_BAD" > "$FAKE/live"
  : > "$FAKE/pins"
}

# run_rollback <name>: runs the script in the current fake world; sets GOT_EXIT and OUT.
run_rollback() {
  GITHUB_STEP_SUMMARY="$FAKE/summary" GITHUB_OUTPUT="$FAKE/output" \
    FAILED_COMMIT="$FAILED" FAILED_DIGEST="$D_BAD" bash "$ROLLBACK" > "$FAKE/stdout" 2>&1
  GOT_EXIT=$?
  OUT="$FAKE/stdout"
  sed 's/^/        /' "$OUT"
}

expect_exit()   { [ "$GOT_EXIT" -eq "$2" ] && pass "$1: exit $2" || fail "$1: wanted exit $2, got $GOT_EXIT"; }
expect_text()   { grep -qF -- "$3" "$2" && pass "$1: says '$3'" || fail "$1: '$2' does not say '$3'"; }
expect_pins()   {
  local got; got=$(cat "$FAKE/pins")
  [ "$got" = "$2" ] && pass "$1: pinned exactly [${2:-nothing}]" || fail "$1: wanted pins [${2:-nothing}], got [$got]"
}
expect_never_pinned() {
  grep -qF -- "$2" "$FAKE/pins" && fail "$1: pinned $2, which must never be a rollback target" || pass "$1: never pinned $2"
}
expect_output() { grep -qxF -- "$2" "$FAKE/output" && pass "$1: output $2" || fail "$1: GITHUB_OUTPUT lacks '$2'"; }
expect_no_unexpected() { [ ! -s "$FAKE/unexpected" ] && pass "$1: made no unexpected az or gh call" || fail "$1: unexpected calls: $(cat "$FAKE/unexpected")"; }

LKG_REF="crfake.azurecr.io/devthrottle-gateway@${D_LKG}"

echo "== target selection =="

# THE 2026-10-26 SHAPE. The outgoing commit read "unknown" and the outgoing image was the broken image
# itself. They are set here in every name the workflow has for them, to prove the script never reads them.
new_case replay-2026-10-26
OUTGOING=unknown OUTGOING_COMMIT=unknown OUTGOING_IMAGE="crfake.azurecr.io/devthrottle-gateway@${D_BAD}" run_rollback
expect_exit replay-2026-10-26 0
expect_pins replay-2026-10-26 "$LKG_REF"
expect_never_pinned replay-2026-10-26 "$D_BAD"
expect_text replay-2026-10-26 "$OUT" "AUTOMATIC ROLLBACK: production was put back on last-known-good and came up."
expect_text replay-2026-10-26 "$OUT" "This deploy is still a FAILURE: the run ends red."
expect_output replay-2026-10-26 "auto_rollback=recovered"
expect_output replay-2026-10-26 "serving_commit_after_rollback=${LKG}"
expect_text replay-2026-10-26 "$FAKE/issue-created" "Hosted Gateway deploy failed and was rolled back automatically"
expect_text replay-2026-10-26 "$FAKE/issue-created" "| Commit that failed to come up | \`${FAILED}\`"
expect_text replay-2026-10-26 "$FAKE/issue-created" "| Commit production serves now | \`${LKG}\` |"
expect_text replay-2026-10-26 "$FAKE/summary" "### Deploy FAILED - automatic rollback: recovered"
expect_no_unexpected replay-2026-10-26

# The outgoing image is a third, healthy-looking image that is NOT last-known-good: still not chosen.
new_case outgoing-is-another-image
OUTGOING=old9999 OUTGOING_COMMIT=old9999 OUTGOING_IMAGE="crfake.azurecr.io/devthrottle-gateway@${D_OLD}" run_rollback
expect_exit outgoing-is-another-image 0
expect_pins outgoing-is-another-image "$LKG_REF"
expect_never_pinned outgoing-is-another-image "$D_OLD"

# last-known-good has lost its ci- tag to the purge: it is still the target, and the result says the
# commit could not be checked.
new_case lkg-without-ci-tag
sed -i "/^ci-${LKG} /d" "$FAKE/tags"
run_rollback
expect_exit lkg-without-ci-tag 0
expect_pins lkg-without-ci-tag "$LKG_REF"
expect_text lkg-without-ci-tag "$OUT" "carries no ci-<commit> tag"
expect_text lkg-without-ci-tag "$OUT" "the commit could not be checked against the image"

# With no commit to check, the FAILED commit answering 200 is not a recovery.
new_case lkg-without-ci-tag-serves-failed-commit
sed -i "/^ci-${LKG} /d" "$FAKE/tags"
printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 200 ${FAILED}" > "$FAKE/serving"
run_rollback
expect_exit lkg-without-ci-tag-serves-failed-commit 1
expect_output lkg-without-ci-tag-serves-failed-commit "auto_rollback=not-recovered"

# last-known-good answers 200 but on a commit that is not its own: not a recovery.
new_case serves-wrong-commit
printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 200 zzz0000" > "$FAKE/serving"
run_rollback
expect_exit serves-wrong-commit 1
expect_output serves-wrong-commit "auto_rollback=not-recovered"

echo "== no target, or a target that does not come up: say so and stop =="

new_case lkg-missing
sed -i '/^last-known-good /d' "$FAKE/tags"
run_rollback
expect_exit lkg-missing 3
expect_pins lkg-missing ""
expect_output lkg-missing "auto_rollback=unresolved"
expect_text lkg-missing "$OUT" "last-known-good could not be resolved; production was NOT rolled back."
expect_text lkg-missing "$OUT" "PRODUCTION IS PROBABLY STILL DOWN"
expect_text lkg-missing "$FAKE/issue-created" "last-known-good could not be resolved"

new_case lkg-is-the-failed-image
sed -i "s/^last-known-good .*/last-known-good ${D_BAD}/" "$FAKE/tags"
run_rollback
expect_exit lkg-is-the-failed-image 3
expect_pins lkg-is-the-failed-image ""
expect_output lkg-is-the-failed-image "auto_rollback=same-as-failed"

new_case repin-does-not-come-up
printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 502 -" > "$FAKE/serving"
run_rollback
expect_exit repin-does-not-come-up 1
expect_pins repin-does-not-come-up "$LKG_REF"
expect_output repin-does-not-come-up "auto_rollback=not-recovered"
expect_text repin-does-not-come-up "$OUT" "last-known-good was pinned but production did NOT come back."
expect_text repin-does-not-come-up "$OUT" "No other image was tried"
expect_text repin-does-not-come-up "$FAKE/issue-created" "| Commit production serves now | \`none - /healthz reported no commit\` |"

new_case pin-refused
touch "$FAKE/pin-fails"
run_rollback
expect_exit pin-refused 1
expect_pins pin-refused "$LKG_REF"
expect_output pin-refused "auto_rollback=pin-failed"

echo "== the wait follows the deploy's rule =="

# Two good answers then a failure then good: the streak restarts, so it still needs three in a row. The
# fake answers from the table, so flip it part way through with a background writer.
new_case streak-must-be-unbroken
printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 502 -" > "$FAKE/serving"
( sleep 1; printf '%s\n' "${D_BAD} 502 -" "${D_LKG} 200 ${LKG}" > "$FAKE/serving" ) &
WRITER=$!
run_rollback
wait "$WRITER"
expect_exit streak-must-be-unbroken 0
grep -q "streak 3" "$OUT" && pass "streak-must-be-unbroken: reached streak 3" || fail "streak-must-be-unbroken: never reached streak 3"

# The settle time holds the verdict back even when the target answers at once.
new_case settle-is-respected
start=$(date +%s)
ROLLBACK_SETTLE_SECONDS=2 run_rollback
took=$(( $(date +%s) - start ))
expect_exit settle-is-respected 0
[ "$took" -ge 2 ] && pass "settle-is-respected: waited ${took}s >= 2s" || fail "settle-is-respected: finished after ${took}s, before the settle time"

echo "== the alert issue =="

new_case issue-already-open
echo 4600 > "$FAKE/open-issue"
run_rollback
expect_exit issue-already-open 0
[ -f "$FAKE/issue-commented" ] && [ ! -f "$FAKE/issue-created" ] && pass "issue-already-open: commented, did not open a second" || fail "issue-already-open: wanted a comment on #4600 and no new issue"
expect_text issue-already-open "$FAKE/issue-commented" "4600"

new_case issue-write-fails
touch "$FAKE/gh-fails"
run_rollback
expect_exit issue-write-fails 0
expect_text issue-write-fails "$OUT" "WARNING: writing the alert issue FAILED"

echo "== the workflow's own failure branch, run the way GitHub runs it =="

# The C# guard reads the wiring; this RUNS it. The failure branch after the image pin is cut out of
# deploy-hosted-gateway.yml as text, its two expressions filled in, and run under `bash -e` (GitHub's step
# shell) against the same fakes: production must end on last-known-good AND the branch must exit 1.
new_case workflow-branch
python3 - "$HERE/../.github/workflows/deploy-hosted-gateway.yml" "$D_BAD" > "$FAKE/branch.sh" <<'PY'
import sys
text = open(sys.argv[1]).read().replace("\r\n", "\n")
step = text[text.index("- name: Deploy the new image in place and measure the outage"):]
step = step[step.index("\n          az webapp config container set"):]
start = step.index('          if [ "$streak" -lt 3 ]; then\n')
end = step.index("\n          fi\n", start) + len("\n          fi\n")
branch = "\n".join(l[10:] for l in step[start:end].split("\n"))
branch = branch.replace("${{ steps.digest.outputs.digest }}", sys.argv[2]).replace("${{ github.token }}", "fake-token")
print('streak=0; TARGET=bad5678; APP=app-fake')
print(branch)
PY
(cd "$HERE/.." && GITHUB_OUTPUT="$FAKE/output" GITHUB_STEP_SUMMARY="$FAKE/summary" \
  GITHUB_SERVER_URL=https://github.com GITHUB_REPOSITORY=example/repo GITHUB_RUN_ID=1 \
  bash -e "$FAKE/branch.sh") > "$FAKE/stdout" 2>&1
GOT_EXIT=$?
OUT="$FAKE/stdout"
sed 's/^/        /' "$OUT"
expect_exit workflow-branch 1
expect_pins workflow-branch "$LKG_REF"
expect_output workflow-branch "outcome=failed"
expect_output workflow-branch "auto_rollback=recovered"
expect_text workflow-branch "$OUT" "The automatic rollback exited 0. This deploy FAILED; the run is red on purpose."
expect_text workflow-branch "$FAKE/issue-created" "| Commit that failed to come up | \`${FAILED}\`"

# And when the rollback itself fails, `bash -e` must not cut the branch short before it says so.
new_case workflow-branch-rollback-fails
sed -i '/^last-known-good /d' "$FAKE/tags"
(cd "$HERE/.." && GITHUB_OUTPUT="$FAKE/output" GITHUB_STEP_SUMMARY="$FAKE/summary" \
  GITHUB_SERVER_URL=https://github.com GITHUB_REPOSITORY=example/repo GITHUB_RUN_ID=1 \
  bash -e "$WORK/workflow-branch/branch.sh") > "$FAKE/stdout" 2>&1
GOT_EXIT=$?
OUT="$FAKE/stdout"
expect_exit workflow-branch-rollback-fails 1
expect_pins workflow-branch-rollback-fails ""
expect_text workflow-branch-rollback-fails "$OUT" "The automatic rollback exited 3. This deploy FAILED; the run is red on purpose."

echo "== the shared resolver =="

new_case resolver
ACR=crfake IMAGE_NAME=devthrottle-gateway bash "$RESOLVER" "$LKG" > "$FAKE/r1" 2>/dev/null
[ "$(cat "$FAKE/r1")" = "$(printf 'digest=%s\ncommit=%s' "$D_LKG" "$LKG")" ] && pass "resolver: a short commit resolves through ci-<commit>" || fail "resolver: short commit gave [$(cat "$FAKE/r1")]"
bash "$RESOLVER" last-known-good > "$FAKE/r2" 2>/dev/null
[ "$(cat "$FAKE/r2")" = "$(printf 'digest=%s\ncommit=%s' "$D_LKG" "$LKG")" ] && pass "resolver: last-known-good resolves with its commit" || fail "resolver: last-known-good gave [$(cat "$FAKE/r2")]"
bash "$RESOLVER" "$D_OLD" > "$FAKE/r3" 2>/dev/null
[ "$(cat "$FAKE/r3")" = "$(printf 'digest=%s\ncommit=%s' "$D_OLD" "old9999")" ] && pass "resolver: a digest resolves with its commit" || fail "resolver: digest gave [$(cat "$FAKE/r3")]"
bash "$RESOLVER" nosuch1 > "$FAKE/r4" 2> "$FAKE/r4err"; r4=$?
[ "$r4" -eq 1 ] && [ ! -s "$FAKE/r4" ] && pass "resolver: an unknown commit exits 1 and prints no output lines" || fail "resolver: unknown commit exit $r4, stdout [$(cat "$FAKE/r4")]"
expect_text resolver "$FAKE/r4err" "ERROR: no image 'devthrottle-gateway:ci-nosuch1' in registry crfake."
expect_text resolver "$FAKE/r4err" "last-known-good"

echo
if [ "$failures" -eq 0 ]; then
  echo "ALL CASES PASSED"
  exit 0
fi
echo "$failures CHECK(S) FAILED"
exit 1

#!/usr/bin/env bash
# Proves scripts/watch-site-state.sh - the site-state half of the deploy watch - against a fake az.
#
# Deploy run 42752825924 (2026-11-10) failed because one `az webapp show` read failed between two Running
# reads and was recorded as the state "Unknown". These cases hold the three rules that replace that:
#   * a failed read alone passes, and is reported as a failed read with the az error text kept;
#   * failed reads that leave the state unmeasured past the bound fail the run as NOT MEASURED;
#   * a real state that is not Running (Stopped) fails exactly as before.
#
# The fake az plays a script of answers, one per call: "Running", "Stopped", or "FAIL" (exit 1 with an
# error on standard error). The verdict cases use written logs with chosen timestamps, so the bound is
# tested at its real 120 seconds without waiting for it. Needs bash and awk. The web job of
# .github/workflows/ci.yml runs it; run it by hand too after any change to the script:
#
#   bash scripts/watch-site-state.test.sh

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
SUT="$HERE/watch-site-state.sh"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/bin"
cat > "$WORK/bin/az" <<'AZ'
#!/usr/bin/env bash
# Answers from $FAKE/answers, one line per call; past the end, the last line repeats.
n=$(cat "$FAKE/calls" 2>/dev/null || echo 0); n=$((n + 1)); echo "$n" > "$FAKE/calls"
total=$(wc -l < "$FAKE/answers")
[ "$n" -le "$total" ] && line=$n || line=$total
answer=$(sed -n "${line}p" "$FAKE/answers")
if [ "$answer" = "FAIL" ]; then
  echo "ERROR: (ExpiredAuthenticationToken) The access token expiry UTC time is earlier than current UTC time." >&2
  exit 1
fi
echo "$answer"
AZ
chmod +x "$WORK/bin/az"

pass=0; failures=0
ok()   { echo "PASS: $1"; pass=$((pass + 1)); }
bad()  { echo "FAIL: $1"; failures=$((failures + 1)); }
expect_value() { # <case> <result-file> <key> <expected>
  local got; got=$(grep "^$3=" "$2" | cut -d= -f2-)
  if [ "$got" = "$4" ]; then ok "$1: $3=$4"; else bad "$1: $3 was '$got', expected '$4'"; fi
}
expect_fail_contains() { # <case> <result-file> <text>
  local got; got=$(grep "^state_fail=" "$2" | cut -d= -f2-)
  case "$got" in *"$3"*) ok "$1: fails with '$3'" ;; *) bad "$1: state_fail was '$got', expected it to contain '$3'" ;; esac
}

# Runs the sampler against the fake for a short watch, then the verdict. Sets $case_dir.
run_sampled() { # <name> <watch-seconds> <answers...>
  local name=$1 secs=$2; shift 2
  case_dir="$WORK/$name"; mkdir -p "$case_dir"
  printf '%s\n' "$@" > "$case_dir/answers"
  local start; start=$(date +%s)
  FAKE="$case_dir" PATH="$WORK/bin:$PATH" AZURE_RG=rg APP=app \
    SAMPLE_INTERVAL_SECONDS=0 RETRY_DELAY_SECONDS=0 READ_ATTEMPTS="${READ_ATTEMPTS:-3}" \
    WATCH_DEADLINE=$((start + secs)) bash "$SUT" sample "$case_dir/state.log" > "$case_dir/sample.out"
  local end; end=$(date +%s)
  bash "$SUT" verdict "$case_dir/state.log" "$start" "$end" "$case_dir/result" > "$case_dir/verdict.out"
}

# ---- 1. A failed read is retried, and the retry's answer is the sample -----------------------------
run_sampled retried 1 FAIL Running
if head -n1 "$WORK/retried/state.log" | grep -q " Running attempts=2$"; then
  ok "retried: the first sample is Running after 2 attempts"
else
  bad "retried: first sample was '$(head -n1 "$WORK/retried/state.log")'"
fi
expect_value retried "$WORK/retried/result" site_state_left_running no
expect_value retried "$WORK/retried/result" state_fail ""

# ---- 2. A sample whose every attempt fails is a FAILED READ with the error kept, never a state ------
READ_ATTEMPTS=2 run_sampled failed-read 1 FAIL FAIL Running
first=$(head -n1 "$WORK/failed-read/state.log")
case "$first" in
  *" READ-FAILED attempts=2 az exited 1: ERROR: (ExpiredAuthenticationToken)"*) ok "failed-read: recorded as READ-FAILED with the az error text" ;;
  *) bad "failed-read: first sample was '$first'" ;;
esac
if grep -q "Unknown\|SITE STATE LEFT" "$WORK/failed-read/state.log" "$WORK/failed-read/sample.out"; then
  bad "failed-read: the failed read was reported as a state"
else
  ok "failed-read: the failed read was not reported as leaving Running"
fi
expect_value failed-read "$WORK/failed-read/result" site_state_left_running no
expect_value failed-read "$WORK/failed-read/result" state_fail ""
if grep -q "ExpiredAuthenticationToken" "$WORK/failed-read/verdict.out"; then
  ok "failed-read: the verdict reports the failed read and its error"
else
  bad "failed-read: the verdict did not report the failed read"
fi

# ---- 3. Run 42752825924, replayed: Running, one failed read, Running -> PASS ------------------------
# Real timestamps from the run (04:06:50 is 1794283610), four samples kept around the failure.
cat > "$WORK/run.log" <<'LOG'
1794283627 04:07:07 Running attempts=1
1794283662 04:07:42 Running attempts=1
1794283685 04:08:05 READ-FAILED attempts=3 az exited 1: ERROR: The request timed out.
1794283702 04:08:22 Running attempts=1
1794283720 04:08:40 Running attempts=1
LOG
bash "$SUT" verdict "$WORK/run.log" 1794283610 1794284210 "$WORK/run.result" > "$WORK/run.out"
expect_value "run 42752825924" "$WORK/run.result" state_fail ""
expect_value "run 42752825924" "$WORK/run.result" site_state_left_running no
expect_value "run 42752825924" "$WORK/run.result" site_state_failed_reads 1
expect_value "run 42752825924" "$WORK/run.result" site_state_longest_unmeasured_seconds 40

# ---- 4. Failed reads past the bound -> FAIL as NOT MEASURED ----------------------------------------
# Running at t=0, then failed reads from t=17 to t=140, Running again at t=158: 158s unmeasured.
{
  echo "1000 00:00:00 Running attempts=1"
  for t in 17 35 52 70 87 105 122 140; do echo "$((1000 + t)) 00:00:00 READ-FAILED attempts=3 az exited 1: ERROR: The request timed out."; done
  echo "1158 00:00:00 Running attempts=1"
} > "$WORK/long.log"
bash "$SUT" verdict "$WORK/long.log" 1000 1600 "$WORK/long.result" > "$WORK/long.out"
expect_fail_contains "unmeasured past the bound" "$WORK/long.result" "NOT MEASURED for 158s, longer than the 120s bound"
expect_value "unmeasured past the bound" "$WORK/long.result" site_state_left_running not-measured

# The same stretch held just inside the bound passes: the line is at 120 seconds, not somewhere vaguer.
{
  echo "1000 00:00:00 Running attempts=1"
  echo "1060 00:00:00 READ-FAILED attempts=3 az exited 1: ERROR: The request timed out."
  echo "1120 00:00:00 Running attempts=1"
} > "$WORK/edge.log"
bash "$SUT" verdict "$WORK/edge.log" 1000 1600 "$WORK/edge.result" > /dev/null
expect_value "unmeasured for exactly 120s" "$WORK/edge.result" state_fail ""

# Failures that run to the END of the watch count up to the end: never closed off as a zero.
{
  echo "1000 00:00:00 Running attempts=1"
  echo "1450 00:00:00 Running attempts=1"
  echo "1470 00:00:00 READ-FAILED attempts=3 az exited 1: ERROR: The request timed out."
  echo "1590 00:00:00 READ-FAILED attempts=3 az exited 1: ERROR: The request timed out."
} > "$WORK/tail.log"
bash "$SUT" verdict "$WORK/tail.log" 1000 1600 "$WORK/tail.result" > /dev/null
expect_fail_contains "unmeasured to the end of the watch" "$WORK/tail.result" "NOT MEASURED for 150s"

# Every read failing, through the real sampler -> FAIL, whatever the bound.
run_sampled all-failed 1 FAIL
expect_fail_contains "every read failed" "$WORK/all-failed/result" "none of the"
expect_value "every read failed" "$WORK/all-failed/result" site_state_left_running not-measured

# No samples at all (the sampler never ran) -> FAIL, never a pass by absence.
: > "$WORK/empty.log"
bash "$SUT" verdict "$WORK/empty.log" 1000 1600 "$WORK/empty.result" > /dev/null
expect_fail_contains "no samples at all" "$WORK/empty.result" "NOT MEASURED"

# ---- 5. A real Stopped state fails exactly as before ------------------------------------------------
run_sampled stopped 1 Running Stopped Running
expect_fail_contains stopped "$WORK/stopped/result" "the App Service site left the Running state (saw 'Stopped')"
expect_value stopped "$WORK/stopped/result" site_state_left_running Stopped
if grep -q "SITE STATE LEFT Running: Stopped" "$WORK/stopped/sample.out"; then
  ok "stopped: the sampler announced it live"
else
  bad "stopped: the sampler did not announce the Stopped state"
fi

# A Stopped read among failed reads is still a Stopped site: the failure verdict is about the state.
printf '%s\n' "1000 00:00:00 READ-FAILED attempts=3 az exited 1: x" "1017 00:00:00 Stopped attempts=1" > "$WORK/mixed.log"
bash "$SUT" verdict "$WORK/mixed.log" 1000 1030 "$WORK/mixed.result" > /dev/null
expect_fail_contains "stopped among failed reads" "$WORK/mixed.result" "saw 'Stopped'"

echo
echo "${pass} passed, ${failures} failed."
[ "$failures" -eq 0 ]

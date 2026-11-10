#!/usr/bin/env bash
# The site-state half of the deploy watch in .github/workflows/deploy-hosted-gateway.yml: sample the
# App Service state with `az webapp show --query state`, and judge the samples. It only READS Azure.
#
#   watch-site-state.sh sample  <state-log>
#       Samples until the epoch in WATCH_DEADLINE, one sample every SAMPLE_INTERVAL_SECONDS (default 15),
#       appending one line per sample to <state-log>. Needs AZURE_RG and APP.
#
#   watch-site-state.sh verdict <state-log> <watch-start-epoch> <watch-end-epoch> <result-file>
#       Prints the judgement in words and writes key=value lines to <result-file>:
#         site_state_left_running        the real state seen that was not Running, "no", or "not-measured"
#         site_state_failed_reads        how many samples could not read the state at all
#         site_state_longest_unmeasured_seconds   the longest stretch with no successful read in it
#         state_fail                     why the run must fail on site state, empty when it must not
#
# A FAILED READ IS NOT A STATE (deploy run 42752825924, 2026-11-10). The sample used to be
#   st=$(az webapp show ... 2>/dev/null || echo "Unknown")
# so one read that failed between two Running reads - /healthz answering 200 on the new commit the whole
# time, a measured outage of 2.3 seconds - was recorded as the site LEAVING Running, and failed the run.
# A read that fails says nothing about the site. It is now retried, and if every attempt fails it is
# recorded as READ-FAILED with the az error text kept, never as a state.
#
# BUT A FAILED READ IS NOT A CLEAN READ EITHER. Ignoring failures entirely would fail OPEN: an expired
# login or a renamed site makes every read fail, and a watch that discards them all would pass a run in
# which the state was never measured. So the verdict measures how long the state went unmeasured - from
# the last successful read (or the start of the watch) to the next successful read (or the end of the
# watch) - and fails the run as NOT MEASURED when that stretch exceeds UNMEASURED_LIMIT_SECONDS, or when
# no read succeeded at all.
#
# WHY 120 SECONDS. It is the outage budget this deploy is already judged on (MAX_ACCEPTABLE_OUTAGE_SECONDS,
# owner ruling 2026-09-20), so the state is never blind for longer than the outage we have agreed to
# tolerate. It sits well clear of the blip it must forgive: one failed sample at the 15-second cadence
# leaves the state unmeasured for about 40 seconds (04:07:42 to 04:08:22 in the run above), and two in a
# row for about 60. It must not be much larger, because this sample exists to catch a DELIBERATE stop
# (`--query state` reports the desired state, not whether a container is up - see issue #2383), and a
# stop that is undone inside an unmeasured stretch is invisible.
#
# A REAL STATE THAT IS NOT Running FAILS EXACTLY AS BEFORE. Only a read that produced no answer is a
# failed read; any state az actually returns (Stopped, or anything else) is the site's state.
#
# Proved against a fake az by scripts/watch-site-state.test.sh.

set -uo pipefail

usage() { echo "usage: $0 sample <state-log> | verdict <state-log> <start-epoch> <end-epoch> <result-file>" >&2; exit 2; }

# One sample: up to READ_ATTEMPTS reads, RETRY_DELAY_SECONDS apart. Prints the log line.
#   <epoch> <HH:MM:SS> <state> attempts=<n>
#   <epoch> <HH:MM:SS> READ-FAILED attempts=<n> <az error text of the last attempt, on one line>
read_state_once() {
  local attempts=${READ_ATTEMPTS:-3} delay=${RETRY_DELAY_SECONDS:-3} n=0 out rc err
  local errf; errf=$(mktemp)
  while [ "$n" -lt "$attempts" ]; do
    n=$((n + 1))
    out=$(az webapp show --resource-group "$AZURE_RG" --name "$APP" --query state --output tsv 2>"$errf"); rc=$?
    out=$(printf '%s' "$out" | tr -d '\r' | awk 'NF { print $1; exit }')
    if [ "$rc" -eq 0 ] && [ -n "$out" ]; then
      rm -f "$errf"
      echo "$(date +%s) $(date -u +%H:%M:%S) $out attempts=$n"
      return 0
    fi
    err=$(tr '\r\n\t' '   ' < "$errf" | sed 's/  */ /g; s/^ //; s/ $//' | cut -c1-400)
    if [ -z "$err" ]; then
      if [ "$rc" -eq 0 ]; then err="az exited 0 with an empty answer"; else err="az exited $rc with no error text"; fi
    else
      err="az exited $rc: $err"
    fi
    [ "$n" -lt "$attempts" ] && sleep "$delay"
  done
  rm -f "$errf"
  echo "$(date +%s) $(date -u +%H:%M:%S) READ-FAILED attempts=$n $err"
}

cmd_sample() {
  local log=$1
  : "${WATCH_DEADLINE:?WATCH_DEADLINE (epoch seconds) is required}"
  : "${AZURE_RG:?AZURE_RG is required}"; : "${APP:?APP is required}"
  local interval=${SAMPLE_INTERVAL_SECONDS:-15} line st
  while [ "$(date +%s)" -lt "$WATCH_DEADLINE" ]; do
    line=$(read_state_once)
    echo "$line" >> "$log"
    st=$(echo "$line" | awk '{ print $3 }')
    if [ "$st" = "READ-FAILED" ]; then
      echo "SITE STATE NOT READ at $(echo "$line" | awk '{ print $2 }') (a failed read, not a state): $(echo "$line" | cut -d' ' -f5-)"
    elif [ "$st" != "Running" ]; then
      echo "SITE STATE LEFT Running: $st at $(echo "$line" | awk '{ print $2 }')"
    fi
    sleep "$interval"
  done
}

cmd_verdict() {
  local log=$1 start=$2 end=$3 result=$4
  local limit=${UNMEASURED_LIMIT_SECONDS:-120}
  [ -f "$log" ] || : > "$log"
  local summary
  summary=$(awk -v start="$start" -v end="$end" '
    BEGIN { last_ok = start; in_gap = 0; longest = 0 }
    $3 == "READ-FAILED" { failed++; if (!in_gap) { in_gap = 1; gap_from = last_ok }; next }
    NF >= 3 {
      ok++
      if ($3 != "Running") bad = $3
      if (in_gap) { d = $1 - gap_from; if (d > longest) longest = d; in_gap = 0 }
      last_ok = $1
    }
    END {
      if (in_gap) { d = end - gap_from; if (d > longest) longest = d }
      if (ok == 0) { d = end - start; if (d > longest) longest = d }
      printf "%d %d %d %s", ok + 0, failed + 0, longest, (bad == "" ? "-" : bad)
    }
  ' "$log")
  local ok failed longest bad
  read -r ok failed longest bad <<< "$summary"

  local left fail=""
  if [ "$bad" != "-" ]; then
    left="$bad"
    fail="the App Service site left the Running state (saw '$bad')"
  elif [ "$ok" -eq 0 ]; then
    left="not-measured"
    fail="the App Service site state was NOT MEASURED: none of the ${failed} read(s) succeeded"
  elif [ "$longest" -gt "$limit" ]; then
    left="not-measured"
    fail="the App Service site state was NOT MEASURED for ${longest}s, longer than the ${limit}s bound (${failed} failed read(s))"
  else
    left="no"
  fi

  echo "Site state: ${ok} successful read(s), ${failed} failed read(s), longest stretch with no successful read ${longest}s (bound ${limit}s)."
  if [ "$failed" -gt 0 ]; then
    echo "Failed reads (a failed read is not a state and is not counted as leaving Running):"
    awk '$3 == "READ-FAILED" { print "  " $0 }' "$log"
    if [ -z "$fail" ]; then
      echo "NOTE: ${failed} state read(s) failed, but the state was never unmeasured for longer than ${limit}s,"
      echo "      so they do not fail the run."
    fi
  fi

  {
    echo "site_state_left_running=${left}"
    echo "site_state_failed_reads=${failed}"
    echo "site_state_longest_unmeasured_seconds=${longest}"
    echo "state_fail=${fail}"
  } > "$result"
}

case "${1:-}" in
  sample)  [ $# -eq 2 ] || usage; cmd_sample "$2" ;;
  verdict) [ $# -eq 5 ] || usage; cmd_verdict "$2" "$3" "$4" "$5" ;;
  *) usage ;;
esac

#!/usr/bin/env bash
# Stop the TEST Director cleanly with its own named shutdown signal. On Linux that is a request file,
# <root>/config/lifecycle-signals/cc-director-shutdown-<directorId>.request, holding an ISO 8601 time and NO trailing
# newline (anything the Director cannot parse as a time is deleted silently and the Director keeps running).
# The id comes from the test Director's own log line "[LifecycleSignal] Listen: name=cc-director-shutdown-<id>".
# This script never kills anything. If the Director has not exited after 30 seconds it says so and stops.
set -euo pipefail
H=${RIG_DIR:?set RIG_DIR to the scratch folder}
EXE=$H/build/cc-director-linux5
pids() { for p in /proc/[0-9]*; do [ "$(readlink $p/exe 2>/dev/null)" = "$EXE" ] && echo ${p#/proc/}; done; true; }
running=$(pids)
[ -n "$running" ] || { echo "no test Director is running from $EXE"; exit 0; }
log=$(ls -t $(find $H/home $H/root -name 'director-*.log' 2>/dev/null) 2>/dev/null | head -1)
[ -n "$log" ] || { echo "no test Director log found under $H" >&2; exit 1; }
id=$(grep -o 'Listen: name=cc-director-shutdown-[0-9a-f-]*' "$log" | tail -1 | sed 's/.*cc-director-shutdown-//')
[ -n "$id" ] || { echo "no shutdown signal name in $log" >&2; exit 1; }
dir=$H/root/config/lifecycle-signals
mkdir -p $dir
printf '%s' "$(date -u +%FT%TZ)" > $dir/cc-director-shutdown-$id.request
echo "stop requested: director $id, process(es) $running, log $log"
for i in $(seq 1 60); do [ -z "$(pids)" ] && { echo "test Director exited cleanly after $((i/2)) seconds"; exit 0; }; sleep 0.5; done
echo "test Director (process $(pids)) has NOT exited 30 seconds after the stop request. Not killed. Decide by hand." >&2
exit 2

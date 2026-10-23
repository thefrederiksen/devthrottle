#!/usr/bin/env bash
# Stop the TEST Director cleanly with its own named shutdown signal - only the one director.sh started from this scratch
# folder, and only while that very process is still running this build on this scratch root.
#
# Which process: $RIG_DIR/director.pid, written by director.sh. It is signalled only if that process is alive, its
# executable is $RIG_DIR/build/cc-director-linux5, and it was started with CC_DIRECTOR_ROOT=$RIG_DIR/root. Anything else
# is refused, so a later Director started from the same build is never stopped by mistake.
# Which signal: the Director id from that process's own registration,
# $RIG_DIR/root/instances/<instance>/config/director/instances/<directorId>.json, the one whose "Pid" is that process.
# On Linux the signal is a request file, $RIG_DIR/root/config/lifecycle-signals/cc-director-shutdown-<directorId>.request,
# holding an ISO 8601 time and NO trailing newline (anything the Director cannot parse as a time is deleted silently and
# the Director keeps running).
# This script never kills anything. If the Director has not exited after 30 seconds it says so and stops.
# After the stop it compares ~/.local/bin with the listing director.sh saved before the start.
set -euo pipefail
H=${RIG_DIR:?set RIG_DIR to the scratch folder}
EXE=$H/build/cc-director-linux5
[ -f "$H/director.pid" ] || { echo "no $H/director.pid: nothing was started from here by director.sh" >&2; exit 1; }
pid=$(cat "$H/director.pid")
[[ "$pid" =~ ^[0-9]+$ ]] || { echo "$H/director.pid does not hold a process id" >&2; exit 1; }
if [ ! -d /proc/$pid ]; then echo "the test Director (process $pid) is not running"; rm -f "$H/director.pid"; exit 0; fi
[ "$(readlink /proc/$pid/exe 2>/dev/null)" = "$EXE" ] || { echo "refusing: process $pid is not $EXE" >&2; exit 1; }
root=$(tr '\0' '\n' < /proc/$pid/environ | sed -n 's/^CC_DIRECTOR_ROOT=//p')
[ "$root" = "$H/root" ] || { echo "refusing: process $pid was not started on $H/root (it has CC_DIRECTOR_ROOT=$root)" >&2; exit 1; }
id=$(python3 - "$H/root" "$pid" <<'EOF'
import glob, json, sys
root, pid = sys.argv[1], int(sys.argv[2])
ids = []
for f in glob.glob(f"{root}/instances/*/config/director/instances/*.json"):
    try: j = json.load(open(f))
    except (OSError, ValueError): continue
    if j.get("Pid") == pid and j.get("DirectorId"): ids.append(j["DirectorId"])
print(ids[0] if len(ids) == 1 else "")
EOF
)
[ -n "$id" ] || { echo "refusing: no single registration under $H/root names process $pid" >&2; exit 1; }
dir=$H/root/config/lifecycle-signals
mkdir -p $dir
printf '%s' "$(date -u +%FT%TZ)" > $dir/cc-director-shutdown-$id.request
echo "stop requested: director $id, process $pid"
stopped=false
for i in $(seq 1 60); do [ -d /proc/$pid ] || { stopped=true; break; }; sleep 0.5; done
if ! $stopped; then
  echo "test Director (process $pid) has NOT exited 30 seconds after the stop request. Not killed. Decide by hand." >&2; exit 2
fi
echo "test Director exited cleanly after $((i/2)) seconds"
rm -f "$H/director.pid"
if [ -f "$H/local-bin-before.txt" ]; then
  if ls -la "$HOME/.local/bin" | diff "$H/local-bin-before.txt" -; then echo "~/.local/bin unchanged since the start"
  else echo "~/.local/bin CHANGED since the start (the diff is above). Do not repair it; tell the Tech Lead." >&2; exit 3; fi
fi

#!/usr/bin/env bash
# Start the TEST Director: the slot-5 build, on its own scratch root AND its own scratch home, on the existing X display.
# Never run this without both CC_DIRECTOR_ROOT and HOME pointing into the scratch folder: a Director on a fresh root
# links its cc-* tools into $HOME/.local/bin, which must not be the real one.
#
# Before it starts it writes two files that stop-director.sh relies on:
#   $RIG_DIR/director.pid           the process id of THIS start (exec keeps the shell's process id, so $$ is the Director)
#   $RIG_DIR/local-bin-before.txt   'ls -la ~/.local/bin' as it was just before this start
H=${RIG_DIR:?set RIG_DIR to the scratch folder holding build/, root/ and home/}
# The Gateway this Director reports to as its "hosted" Gateway. A Director sends its first-open report and, before it
# has a key, its error reports to the hosted Gateway; unset, that is PRODUCTION (gateway.devthrottle.com). So it must be
# named on every start: the local Gateway (http://127.0.0.1:7996) for a local run, production only on purpose.
GW=${TARGET_GATEWAY:?set TARGET_GATEWAY to the Gateway this test Director uses, e.g. http://127.0.0.1:7996}
EXE=$H/build/cc-director-linux5
[ -x "$EXE" ] || { echo "no test build at $EXE" >&2; exit 1; }
case "$H" in "$HOME/.local/share/cc-director"*) echo "refusing: RIG_DIR is the installed Director's folder" >&2; exit 1;; esac
if [ -f "$H/director.pid" ]; then
  old=$(cat "$H/director.pid")
  if [ "$(readlink /proc/$old/exe 2>/dev/null)" = "$EXE" ]; then
    echo "refusing: the test Director started from here is still running (process $old); stop it with stop-director.sh first" >&2; exit 1
  fi
fi
mkdir -p $H/root $H/home
ls -la "$HOME/.local/bin" > "$H/local-bin-before.txt"
printf '%s\n' "$$" > "$H/director.pid"
cd $H/build
# The session variables of the agent that runs this are removed, so nothing in the test Director acts as this session.
exec env -u CC_GATEWAY_URL -u CC_GATEWAY_SESSION_KEY -u CC_SESSION_ID -u CC_DIRECTOR_INSTANCE \
  DEVTHROTTLE_HOSTED_GATEWAY_URL=$GW \
  CC_DIRECTOR_ROOT=$H/root HOME=$H/home \
  DISPLAY=${DISPLAY:-:0} XAUTHORITY=${XAUTHORITY:-/run/user/$(id -u)/gdm/Xauthority} \
  DOTNET_USE_POLLING_FILE_WATCHER=1 \
  "$EXE" "$@"

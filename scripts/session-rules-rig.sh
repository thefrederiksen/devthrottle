#!/usr/bin/env bash
#
# session-rules-rig.sh - the Session Rules live rig on Linux: a Gateway of its own and a test Director
# of its own, both built from one worktree, so the phase 1 live check (mission section 7, rows 1 and 2,
# and the September usage-limit screen rule) can be run by anyone with this one script.
#
# WHAT "up" STANDS. Everything under one rig folder (RIG_DIR), nothing anywhere else:
#
#   <rig>/builds/gateway/CcDirector.Gateway      the self-host console Gateway, published from the worktree.
#                                                On Linux this IS the desktop Gateway: the tray build
#                                                (CcDirector.GatewayApp) is Windows only.
#   <rig>/builds/director/cc-director-linux<N>   the test Director, slot N (5 or higher), from the worktree
#   <rig>/builds/tool/                           the worktree's own cc-devthrottle, run with its Python
#   <rig>/root/                                  CC_DIRECTOR_ROOT for both: the Gateway's data, its token,
#                                                its key vault, the Director's instance, rules files,
#                                                evaluation logs and decision drops
#   <rig>/home/                                  HOME for both. A Director on a fresh root repairs its
#                                                cc-* tools at start-up and links them into ~/.local/bin
#                                                whatever CC_DIRECTOR_ROOT says; a HOME of its own keeps
#                                                that away from the owner's real tools folder.
#   <rig>/logs/                                  standard output and error of both processes
#   <rig>/repos/                                 repo A and repo B, made by "make-repos"
#
# THE OWNER'S FLEET IS UNREACHABLE BY CONSTRUCTION. The Gateway runs on its own port (never 7878) with
# auth ON (CC_GATEWAY_NO_AUTH and CC_GATEWAY_AUTH are removed from its environment), on its own root.
# The Director is given only the rig Gateway's address and token. Both start with an environment built
# from nothing (env -i) plus a short allow-list, so no CC_GATEWAY_URL, session key, session id or
# Director root of the calling session can leak into them. Nothing here signals, stops or reads the
# owner's running Directors: every process this script acts on is found by its executable path under
# <rig>, never by name.
#
# DETACHED, NOT HIDDEN. The Gateway and the Director are started with setsid and nohup so they outlive
# the shell that ran "up" (a Director spawned inside an agent's console loses its sessions to
# nested-console detection). Their process ids are printed by "up" and "status", and both write logs
# under <rig>/logs.
#
# HOW IT STOPS. "down" asks the Director to stop with its OWN named shutdown signal - on Linux a request
# file, <root>/config/lifecycle-signals/cc-director-shutdown-<directorId>.request, which the Director
# polls - and then stops the Gateway, which has no shutdown request, with the ordinary terminate signal
# (the host shuts down gracefully). Both are aimed at one process id confirmed by its executable path.
# Nothing is ever force-killed; if a process does not exit, "down" says so and leaves it.
#
# THE CLAUDE CODE SIGN-IN. A Claude Code session started under the rig's HOME has no sign-in, so
# "spawn-session" copies the owner's ~/.claude/.credentials.json and ~/.claude.json (without its
# per-project history) into <rig>/home. A copy that refreshed its token could rotate the owner's own
# sign-in out from under the owner's real file, so the copy is refused when it has less than
# RIG_MIN_SIGNIN_MINUTES (default 120) left, and "down" deletes it. This is the precedent set by
# ScratchClaudeConfig in src/CcDirector.DeliveryQualification/Rig.cs.
#
# THE SCREEN RULE'S MODEL. The Gateway's screen evaluator asks a model at
# https://devthrottle.com/api/v1 with the key DEVTHROTTLE_API_KEY from the Gateway's key vault
# (<root>/keyvault.json). The Gateway seeds that vault once from its own environment at start-up. "up"
# passes the key ONLY when it is supplied, in one of two ways, and never writes it into the repository:
#   - DEVTHROTTLE_API_KEY already in the environment of the shell running "up", or
#   - RIG_SCREEN_KEY_SECRET=<cc-secrets entry name>: "up" starts the Gateway under
#     "cc-secrets run <name> --via env --env-name DEVTHROTTLE_API_KEY", so the key goes from the
#     owner's secret store into the Gateway's environment and is never printed.
# Without either, the rig still runs hook rules (they need no model); screen rules record a refusal.
#
# Usage:
#   scripts/session-rules-rig.sh build [<worktree>]        publish the Gateway, the Director and the tool
#   scripts/session-rules-rig.sh up                        start the Gateway, then the Director; wait for both
#   scripts/session-rules-rig.sh status                    what runs from the rig, and what its Gateway sees
#   scripts/session-rules-rig.sh make-repos                repo A and repo B, two git repos, different origins
#   scripts/session-rules-rig.sh spawn-session <dir> [--agent ClaudeCode|RawCli] [--command bash] [--name N]
#   scripts/session-rules-rig.sh prompt <session> <text>   send a prompt to a rig session
#   scripts/session-rules-rig.sh cli <cc-devthrottle arguments...>   the worktree's tool, against the rig
#   scripts/session-rules-rig.sh rules <list|get|create|create-hook-probe|create-usage-limit|promote|delete|firings|all-firings|effective|evidence> ...
#   scripts/session-rules-rig.sh commands [<filter>]       each shell command a rig Claude Code session ran, with its call id
#   scripts/session-rules-rig.sh down                      stop the Director and the Gateway, delete the sign-in copy
#   scripts/session-rules-rig.sh reset                     down, then delete the rig folder (only one this script made)
#
# Settings (environment):
#   RIG_DIR    the rig folder.  Default: ${XDG_STATE_HOME:-$HOME/.local/state}/session-rules-rig
#   RIG_PORT   the Gateway port. Default 7931. 7878 (the live Gateway's) is refused.
#   RIG_SLOT   the Director slot. Default 6. Below 5 is refused.
#   RIG_SCREEN_KEY_SECRET   see THE SCREEN RULE'S MODEL above.
#   RIG_MIN_SIGNIN_MINUTES  see THE CLAUDE CODE SIGN-IN above.
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
DEFAULT_WORKTREE="$(cd "$SCRIPT_DIR/.." && pwd)"

RIG_DIR="${RIG_DIR:-${XDG_STATE_HOME:-$HOME/.local/state}/session-rules-rig}"
RIG_PORT="${RIG_PORT:-7931}"
RIG_SLOT="${RIG_SLOT:-6}"
RIG_MIN_SIGNIN_MINUTES="${RIG_MIN_SIGNIN_MINUTES:-120}"

RIG_MARKER="session-rules-rig"
SENTINEL_NAME=".session-rules-rig-owns-this-directory"
REAL_HOME="$HOME"
REAL_MACHINE_ROOT="$REAL_HOME/.local/share/cc-director"

say()  { echo "[session-rules-rig] $*"; }
fail() { echo "[session-rules-rig] ERROR: $*" >&2; exit 1; }

# ------------------------------------------------------------------------------------------------
# The rig folder is an ALLOW-LIST: a path this script may create, fill and delete only if it carries
# the marker in its leaf name, is not the owner's machine root or anything around it, is not the home
# folder itself, and either does not exist yet or carries this script's sentinel.
# ------------------------------------------------------------------------------------------------
RIG_DIR="$(realpath -m "$RIG_DIR")"
assert_rig_dir() {
    local p="$RIG_DIR"
    [[ "$p" == /* ]] || fail "RIG_DIR must be absolute: $p"
    [[ "$(basename "$p")" == *"$RIG_MARKER"* ]] || fail "REFUSING: $p does not carry '$RIG_MARKER' in its leaf name."
    [[ "$p" != "$REAL_HOME" && "$p" != "/" ]] || fail "REFUSING: $p is the home folder or the file system root."
    case "$p/" in
        "$REAL_MACHINE_ROOT/"*) fail "REFUSING: $p is inside the owner's machine root $REAL_MACHINE_ROOT." ;;
    esac
    case "$REAL_MACHINE_ROOT/" in
        "$p/"*) fail "REFUSING: $p is an ancestor of the owner's machine root." ;;
    esac
    if [[ -e "$p" && ! -f "$p/$SENTINEL_NAME" ]]; then
        fail "REFUSING: $p exists but carries no $SENTINEL_NAME, so this script did not create it."
    fi
}
assert_rig_dir
[[ "$RIG_PORT" =~ ^[0-9]+$ ]] || fail "RIG_PORT must be a number."
[[ "$RIG_PORT" != "7878" ]] || fail "REFUSING: 7878 is the live Gateway's port."
[[ "$RIG_SLOT" =~ ^[0-9]+$ && "$RIG_SLOT" -ge 5 ]] || fail "RIG_SLOT must be 5 or higher (slots 1-4 and the installed app are the owner's)."

BUILDS="$RIG_DIR/builds"
GATEWAY_DIR="$BUILDS/gateway"
GATEWAY_EXE="$GATEWAY_DIR/CcDirector.Gateway"
DIRECTOR_DIR="$BUILDS/director"
DIRECTOR_EXE="$DIRECTOR_DIR/cc-director-linux$RIG_SLOT"
TOOL_DIR="$BUILDS/tool"
BUILT_FROM="$BUILDS/built-from.txt"
ROOT="$RIG_DIR/root"
RIG_HOME="$RIG_DIR/home"
LOGS="$RIG_DIR/logs"
REPOS="$RIG_DIR/repos"
STATE="$RIG_DIR/state"
TOKEN_FILE="$ROOT/config/director/gateway-token.txt"
INSTANCE_HOME="$ROOT/instances/default"
INSTANCE_CONFIG="$INSTANCE_HOME/config/config.json"
REGISTRATION_DIR="$INSTANCE_HOME/config/director/instances"
SIGNAL_DIR="$ROOT/config/lifecycle-signals"
GATEWAY_URL="http://127.0.0.1:$RIG_PORT"
MACHINE="$(hostname)"

DOTNET="dotnet"
[[ -x "$HOME/.dotnet/dotnet" ]] && DOTNET="$HOME/.dotnet/dotnet"
DOTNET_HOME="$(dirname "$(realpath "$(command -v "$DOTNET")")")"
TOOL_PYTHON="${RIG_PYTHON:-$REAL_MACHINE_ROOT/pyenv/bin/python3}"

ensure_rig_dir() {
    mkdir -p "$RIG_DIR"
    [[ -f "$RIG_DIR/$SENTINEL_NAME" ]] || echo "created by scripts/session-rules-rig.sh on $(date -u +%FT%TZ)" > "$RIG_DIR/$SENTINEL_NAME"
    mkdir -p "$BUILDS" "$ROOT" "$RIG_HOME" "$LOGS" "$REPOS" "$STATE"
}

# ------------------------------------------------------------------------------------------------
# Processes, found only by the executable path under the rig.
# ------------------------------------------------------------------------------------------------
exe_of() { readlink "/proc/$1/exe" 2>/dev/null || true; }
pid_runs_exe() { local pid="$1" exe="$2"; [[ -n "$pid" && -d "/proc/$pid" && "$(exe_of "$pid")" == "$exe" ]]; }

# Every process whose executable lives under the rig folder. A framework-dependent .NET program runs as
# its own apphost, so its /proc exe is the rig path, never the shared dotnet host.
rig_processes() {
    local d exe
    for d in /proc/[0-9]*; do
        exe="$(readlink "$d/exe" 2>/dev/null || true)"
        [[ "$exe" == "$RIG_DIR/"* ]] && echo "${d#/proc/} $exe"
    done
    return 0
}

gateway_pid() {
    local pid
    pid="$(cat "$STATE/gateway.pid" 2>/dev/null || true)"
    pid_runs_exe "$pid" "$GATEWAY_EXE" && echo "$pid" || true
}

# The Director's identity comes from the registration the running process writes; its Pid is that process.
# A Director whose services failed before it registered still listens for its shutdown signal, and names
# that signal in its own log ("Listen: name=cc-director-shutdown-<id>"), so a rig Director process with
# no registration is found by its executable path and identified from the log written under its process id.
director_registration() {
    local f seen=""
    for f in "$REGISTRATION_DIR"/*.json; do
        [[ -f "$f" ]] || continue
        seen="$seen $(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("Pid", 0))' "$f")"
    done
    local pid exe log id
    while read -r pid exe; do
        [[ "$exe" == "$DIRECTOR_EXE" ]] || continue
        [[ " $seen " == *" $pid "* ]] && continue
        log="$(ls "$INSTANCE_HOME"/logs/director/director-*-"$pid".log 2>/dev/null | tail -1)"
        id="$(grep -oE 'Listen: name=cc-director-shutdown-[0-9a-f-]+' "$log" 2>/dev/null | head -1 | sed 's/.*shutdown-//')"
        [[ -n "$id" ]] && echo "$id $pid alive"
    done < <(rig_processes)
    for f in "$REGISTRATION_DIR"/*.json; do
        [[ -f "$f" ]] || continue
        python3 - "$f" "$DIRECTOR_EXE" <<'EOF'
import json, os, sys
r = json.load(open(sys.argv[1]))
pid = int(r.get("Pid") or 0)
exe = ""
try:
    exe = os.readlink(f"/proc/{pid}/exe")
except OSError:
    pass
alive = "alive" if exe == sys.argv[2] else "gone"
print(str(r.get("DirectorId", "")).lower(), pid, alive)
EOF
    done
}

wait_for() {  # wait_for <seconds> <description> <command...>
    local seconds="$1" what="$2"; shift 2
    local deadline=$((SECONDS + seconds))
    while (( SECONDS < deadline )); do
        if "$@" >/dev/null 2>&1; then return 0; fi
        sleep 1
    done
    say "TIMEOUT after ${seconds}s waiting for: $what"
    return 1
}

token() { [[ -s "$TOKEN_FILE" ]] || fail "no rig token at $TOKEN_FILE - run 'up' first."; tr -d '\r\n' < "$TOKEN_FILE"; }

# One Gateway call. The token goes in a header read from a file, so it never appears in the process list.
gw() {  # gw <METHOD> <path> [json body]
    local method="$1" path="$2" body="${3:-}"
    local hdr; hdr="$(mktemp)"
    printf 'Authorization: Bearer %s\n' "$(token)" > "$hdr"
    local args=(-sS -X "$method" -H "@$hdr" -w '\n[http %{http_code}]\n' "$GATEWAY_URL$path")
    if [[ -n "$body" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "$body"); fi
    local rc=0
    curl "${args[@]}" || rc=$?
    rm -f "$hdr"
    return $rc
}

# The allow-list every rig process starts from. Nothing of the calling session's CC_* environment passes.
base_env() {
    local vars=(
        "HOME=$RIG_HOME" "USER=${USER:-}" "LOGNAME=${LOGNAME:-}" "SHELL=${SHELL:-/bin/bash}" "LANG=${LANG:-C.UTF-8}"
        "PATH=$RIG_HOME/.local/bin:$REAL_HOME/.local/bin:$DOTNET_HOME:/usr/local/bin:/usr/bin:/bin"
        "DOTNET_ROOT=$DOTNET_HOME" "DOTNET_CLI_TELEMETRY_OPTOUT=1" "DOTNET_USE_POLLING_FILE_WATCHER=1"
        "CC_DIRECTOR_ROOT=$ROOT" "CC_AUTOUPDATE=0" "CC_GATEWAY_NO_TAILSCALE=1"
        "TERM=xterm-256color"
    )
    local v
    for v in DISPLAY XAUTHORITY XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS WAYLAND_DISPLAY; do
        [[ -n "${!v:-}" ]] && vars+=("$v=${!v}")
    done
    printf '%s\n' "${vars[@]}"
}

# ------------------------------------------------------------------------------------------------
cmd_build() {
    local worktree="${1:-$DEFAULT_WORKTREE}"
    worktree="$(cd "$worktree" && pwd)"
    [[ -f "$worktree/src/CcDirector.Gateway/CcDirector.Gateway.csproj" ]] || fail "$worktree is not a DevThrottle worktree."
    [[ -x "$worktree/scripts/local-build-linux.sh" || -f "$worktree/scripts/local-build-linux.sh" ]] || fail "no scripts/local-build-linux.sh in $worktree"
    if [[ -n "$(rig_processes)" ]]; then fail "the rig is running - 'down' first, so a build never replaces a running executable."; fi
    ensure_rig_dir
    local commit; commit="$(git -C "$worktree" rev-parse HEAD)"
    local branch; branch="$(git -C "$worktree" rev-parse --abbrev-ref HEAD)"
    local dirty=""; [[ -n "$(git -C "$worktree" status --porcelain --untracked-files=no)" ]] && dirty=" (with uncommitted changes)"
    say "building from $worktree, $branch at $commit$dirty"

    # No web shells and no web typecheck: the rig is driven through the Gateway's routes, phase 1 has no
    # page, and both would need Node.js. Skipping them is a stated choice here, not a silent one.
    say "publishing the Gateway -> $GATEWAY_DIR (no Cockpit, no mobile app, no web typecheck)"
    mkdir -p "$GATEWAY_DIR"
    "$DOTNET" publish "$worktree/src/CcDirector.Gateway/CcDirector.Gateway.csproj" -c Release \
        -p:RunMobileBuild=false -p:RunCockpitBuild=false -p:RunWorkspaceTypecheck=false \
        -o "$GATEWAY_DIR" --nologo -v q
    [[ -x "$GATEWAY_EXE" ]] || fail "the Gateway publish did not produce $GATEWAY_EXE"

    say "building the slot-$RIG_SLOT Director with the worktree's own scripts/local-build-linux.sh"
    bash "$worktree/scripts/local-build-linux.sh" --slot "$RIG_SLOT" --framework-dependent
    mkdir -p "$DIRECTOR_DIR"
    cp -f "$worktree/local_builds/linux/cc-director-linux$RIG_SLOT" "$DIRECTOR_EXE"
    chmod +x "$DIRECTOR_EXE"

    say "copying the worktree's cc-devthrottle -> $TOOL_DIR"
    mkdir -p "$TOOL_DIR"
    rm -rf "${TOOL_DIR:?}/cc_devthrottle" "${TOOL_DIR:?}/cc_shared"
    cp -r "$worktree/tools/cc-devthrottle/src" "$TOOL_DIR/cc_devthrottle"
    cp -r "$worktree/tools/cc_shared" "$TOOL_DIR/cc_shared"

    {
        echo "worktree: $worktree"
        echo "branch:   $branch"
        echo "commit:   $commit$dirty"
        echo "built:    $(date -u +%FT%TZ)"
        echo "gateway:  $GATEWAY_EXE  sha256 $(sha256sum "$GATEWAY_DIR/CcDirector.Gateway.dll" | cut -d' ' -f1) (dll)"
        echo "director: $DIRECTOR_EXE  sha256 $(sha256sum "$DIRECTOR_EXE" | cut -d' ' -f1)"
    } > "$BUILT_FROM"
    cat "$BUILT_FROM"
}

# ------------------------------------------------------------------------------------------------
write_director_config() {
    local claude_path; claude_path="$(command -v claude || echo "$REAL_HOME/.local/bin/claude")"
    mkdir -p "$(dirname "$INSTANCE_CONFIG")"
    python3 - "$INSTANCE_CONFIG" "$GATEWAY_URL" "$(token)" "$claude_path" <<'EOF'
import json, sys
path, url, token, claude = sys.argv[1:5]
config = {
    "onboarding": {"completed": True},
    "autoUpdate": {"enabled": False},
    "gateway": {"url": url, "token": token, "streamMode": True},
    "agent": {"entries": [
        {"id": "00000000-0000-0000-0000-00000000c1a0", "display_name": "Claude Code", "type": "ClaudeCode",
         "enabled": True, "executable_path": claude, "preset_id": "Skip permissions", "default_model": "",
         "args_override": "", "launch_mode": "Guided"},
        {"id": "00000000-0000-0000-0000-00000000ba5e", "display_name": "Shell", "type": "RawCli",
         "enabled": True, "executable_path": "/bin/bash", "preset_id": "Standard", "default_model": "",
         "args_override": "", "launch_mode": "Guided"},
    ]},
}
with open(path, "w") as f:
    json.dump(config, f, indent=2)
EOF
    chmod 600 "$INSTANCE_CONFIG"
}

# Internal: start the Gateway detached. Run by "up", directly or under cc-secrets so the screen-rule key
# reaches the Gateway's environment without being printed.
cmd__start_gateway() {
    local envs=(); mapfile -t envs < <(base_env)
    [[ -n "${DEVTHROTTLE_API_KEY:-}" ]] && envs+=("DEVTHROTTLE_API_KEY=$DEVTHROTTLE_API_KEY")
    cd "$GATEWAY_DIR"
    nohup setsid env -i "${envs[@]}" "$GATEWAY_EXE" --port "$RIG_PORT" \
        > "$LOGS/gateway.out" 2> "$LOGS/gateway.err" < /dev/null &
    echo $! > "$STATE/gateway.pid"
}

# The Director watches files with inotify, and Linux allows each user a fixed number of inotify instances
# (fs.inotify.max_user_instances, 128 by default). Every Claude Code session and every Director the user
# runs holds some. When none are left the Director starts its window but its services - the Gateway
# connection among them - fail ("Director services failed to start: The configured user limit (128) on
# the number of inotify instances has been reached"), and it never registers. So "up" counts first.
RIG_MIN_FREE_INOTIFY="${RIG_MIN_FREE_INOTIFY:-24}"
check_inotify_headroom() {
    local max used=0 d n
    max="$(cat /proc/sys/fs/inotify/max_user_instances)"
    for d in /proc/[0-9]*; do
        [[ -O "$d" ]] || continue
        # A process can end, or refuse the read, between the listing and the find; that counts as none.
        n="$( (find "$d/fd" -maxdepth 1 -lname 'anon_inode:inotify' 2>/dev/null || true) | wc -l)"
        used=$((used + n))
    done
    say "inotify instances: $used of $max in use by $(id -un); the rig needs about $RIG_MIN_FREE_INOTIFY free"
    if (( max - used < RIG_MIN_FREE_INOTIFY )); then
        fail "only $((max - used)) inotify instances are free, so the rig Director's services cannot start. Raise the limit (needs the machine's owner, sudo): sudo sysctl fs.inotify.max_user_instances=1024 - and to keep it after a restart: echo fs.inotify.max_user_instances=1024 | sudo tee /etc/sysctl.d/60-inotify-instances.conf"
    fi
}

director_failed_to_start() {
    local log
    log="$(ls -t "$INSTANCE_HOME"/logs/director/director-*.log 2>/dev/null | head -1)"
    [[ -n "$log" ]] && grep -m1 'Director services failed to start' "$log"
}

cmd_up() {
    [[ -x "$GATEWAY_EXE" && -x "$DIRECTOR_EXE" ]] || fail "not built - run 'build' first."
    check_inotify_headroom
    if [[ -n "$(rig_processes)" ]]; then rig_processes; fail "something already runs from $RIG_DIR - run 'status' or 'down'."; fi
    if curl -s -o /dev/null --max-time 2 "$GATEWAY_URL/healthz"; then fail "something already answers on port $RIG_PORT."; fi
    ensure_rig_dir
    cat "$BUILT_FROM" 2>/dev/null || true

    say "starting the rig Gateway on $GATEWAY_URL (root $ROOT)"
    if [[ -n "${DEVTHROTTLE_API_KEY:-}" ]]; then
        say "  screen-rule model key: from this shell's DEVTHROTTLE_API_KEY"
        cmd__start_gateway
    elif [[ -n "${RIG_SCREEN_KEY_SECRET:-}" ]]; then
        say "  screen-rule model key: from the secret store entry '$RIG_SCREEN_KEY_SECRET' (never printed)"
        PATH="$REAL_HOME/.local/bin:$PATH" cc-secrets run "$RIG_SCREEN_KEY_SECRET" --via env --env-name DEVTHROTTLE_API_KEY \
            --timeout 120 -- bash "$SCRIPT_PATH" _start-gateway
    else
        say "  screen-rule model key: NONE supplied - hook rules work; a screen rule will record that the model could not be asked"
        cmd__start_gateway
    fi
    local gpid; gpid="$(cat "$STATE/gateway.pid")"
    wait_for 120 "the Gateway's token file" test -s "$TOKEN_FILE" || { tail -30 "$LOGS/gateway.err" "$LOGS/gateway.out"; fail "the Gateway never wrote its token"; }
    wait_for 120 "the Gateway to answer /sessions with the token" gw_ok GET /sessions || { tail -30 "$LOGS/gateway.out"; fail "the Gateway never became ready"; }
    pid_runs_exe "$gpid" "$GATEWAY_EXE" || fail "process $gpid is not $GATEWAY_EXE"
    chmod 600 "$ROOT/keyvault.json" 2>/dev/null || true
    say "Gateway ready: process $gpid, $(curl -s "$GATEWAY_URL/healthz")"
    local unauth; unauth="$(curl -s -o /dev/null -w '%{http_code}' "$GATEWAY_URL/sessions")"
    say "auth check: /sessions WITHOUT the token answers $unauth (must not be 200)"
    [[ "$unauth" != "200" ]] || fail "the rig Gateway answered without a token - auth is OFF; refusing to go on."

    write_director_config
    say "starting the slot-$RIG_SLOT test Director (HOME=$RIG_HOME)"
    local envs=(); mapfile -t envs < <(base_env)
    (
        cd "$DIRECTOR_DIR"
        nohup setsid env -i "${envs[@]}" "$DIRECTOR_EXE" > "$LOGS/director.out" 2> "$LOGS/director.err" < /dev/null &
        echo $! > "$STATE/director.pid"
    )
    wait_for 180 "the Director's instance registration" registered_or_failed || true
    local failed; failed="$(director_failed_to_start || true)"
    if [[ -n "$failed" ]]; then
        say "the Director reports: ${failed#* \[CcDirector\] }"
        fail "the rig Director's services did not start. The Director process is still up (its window opened); run 'down' to stop it with its named signal."
    fi
    has_live_registration || { tail -30 "$LOGS/director.err"; fail "the Director never registered; run 'down' to stop it."; }
    local reg id pid
    reg="$(director_registration | awk '$3=="alive"' | head -1)"
    id="$(echo "$reg" | awk '{print $1}')"; pid="$(echo "$reg" | awk '{print $2}')"
    echo "$id" > "$STATE/director.id"
    wait_for 120 "the rig Gateway to list Director $id" director_joined "$id" || fail "Director $id never joined the rig Gateway"
    say ""
    say "RIG IS UP"
    say "  gateway:   $GATEWAY_URL  process $gpid  (token in $TOKEN_FILE)"
    say "  director:  $id  process $pid  slot $RIG_SLOT  machine $MACHINE"
    say "  root:      $ROOT"
    say "  logs:      $LOGS and $INSTANCE_HOME/logs"
    say "  ~/.local/bin check: $(readlink "$REAL_HOME/.local/bin/cc-devthrottle")"
}

gw_ok() { gw "$@" 2>/dev/null | tail -1 | grep -q '\[http 200\]'; }
has_live_registration() { compgen -G "$REGISTRATION_DIR/*.json" > /dev/null && director_registration | grep -q ' alive$'; }
registered_or_failed() { has_live_registration || [[ -n "$(director_failed_to_start || true)" ]]; }
director_joined() { gw GET /directors 2>/dev/null | grep -qi "\"$1\""; }

# ------------------------------------------------------------------------------------------------
cmd_status() {
    say "rig: $RIG_DIR"
    [[ -f "$BUILT_FROM" ]] && sed 's/^/  /' "$BUILT_FROM"
    local procs; procs="$(rig_processes)"
    if [[ -z "$procs" ]]; then say "nothing runs from the rig"; else echo "$procs" | while read -r p e; do say "running: process $p  $e"; done; fi
    director_registration | while read -r id pid alive; do say "Director registration: $id process $pid ($alive)"; done
    if [[ -f "$INSTANCE_HOME/claude-hooks/cc-rule-hook" ]]; then
        say "hook evaluator: $INSTANCE_HOME/claude-hooks/cc-rule-hook is present"
    else
        say "hook evaluator: NOT present in $INSTANCE_HOME/claude-hooks (written at the first session launch by a Director that carries it; a build without it registers no PreToolUse hook)"
    fi
    if curl -s -o /dev/null --max-time 2 "$GATEWAY_URL/healthz"; then
        say "Gateway /healthz: $(curl -s "$GATEWAY_URL/healthz")"
        [[ -s "$TOKEN_FILE" ]] && { say "Gateway /directors:"; gw GET /directors; }
    else
        say "nothing answers on $GATEWAY_URL"
    fi
    say "~/.local/bin/cc-devthrottle -> $(readlink "$REAL_HOME/.local/bin/cc-devthrottle")"
}

# ------------------------------------------------------------------------------------------------
# The worktree's own cc-devthrottle, acting with the rig Gateway's token and as no session.
cmd_cli() {
    [[ -d "$TOOL_DIR/cc_devthrottle" ]] || fail "no tool under $TOOL_DIR - run 'build' first."
    [[ -x "$TOOL_PYTHON" ]] || fail "no Python at $TOOL_PYTHON for the tool; set RIG_PYTHON."
    local key; key="$(token)"
    (
        cd "$TOOL_DIR"
        env -u CC_SESSION_ID -u CC_DIRECTOR_ID -u CC_DIRECTOR_ROOT -u CC_SESSION_POINTER_FILE -u CC_SESSION_PREAMBLE_FILE \
            PYTHONPATH="$TOOL_DIR" CC_GATEWAY_URL="$GATEWAY_URL" CC_GATEWAY_SESSION_KEY="$key" \
            "$TOOL_PYTHON" -c 'import sys; sys.argv[0] = "cc-devthrottle"; from cc_devthrottle.cli import tool_main; tool_main()' "$@"
    )
}

# ------------------------------------------------------------------------------------------------
# The Claude Code sign-in copy (see the header).
copy_signin() {
    local src="$REAL_HOME/.claude/.credentials.json"
    [[ -f "$src" ]] || fail "no Claude Code sign-in at $src to copy."
    python3 - "$src" "$RIG_MIN_SIGNIN_MINUTES" <<'EOF' || exit 1
import json, sys, time
o = json.load(open(sys.argv[1]))["claudeAiOauth"]
left = (o["expiresAt"] / 1000 - time.time()) / 60
if left < float(sys.argv[2]):
    sys.stderr.write(f"[session-rules-rig] ERROR: the Claude Code sign-in has {left:.0f} minutes left; a copy could refresh it and rotate the owner's own out. Run again after Claude Code has refreshed it.\n")
    sys.exit(1)
print(f"[session-rules-rig] Claude Code sign-in copied into the rig HOME ({left:.0f} minutes left on it)")
EOF
    mkdir -p "$RIG_HOME/.claude"
    install -m 600 "$src" "$RIG_HOME/.claude/.credentials.json"
    python3 - "$REAL_HOME/.claude.json" "$RIG_HOME/.claude.json" <<'EOF'
import json, os, sys
src, dst = sys.argv[1:3]
c = json.load(open(src)) if os.path.exists(src) else {}
# The owner's onboarding state, without the owner's per-folder history and trust.
c.pop("projects", None)
c.pop("githubRepoPaths", None)
if os.path.exists(dst):
    old = json.load(open(dst))
    if "projects" in old:
        c["projects"] = old["projects"]
c["hasCompletedOnboarding"] = True
with open(dst, "w") as f:
    json.dump(c, f)
os.chmod(dst, 0o600)
EOF
}

cmd_spawn_session() {
    [[ $# -ge 1 ]] || fail "usage: spawn-session <dir> [--agent ClaudeCode|RawCli] [--command bash] [--name NAME]"
    local dir; dir="$(cd "$1" && pwd)"; shift
    local agent="ClaudeCode" command="" name=""
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --agent) agent="$2"; shift 2 ;;
            --command) command="$2"; shift 2 ;;
            --name) name="$2"; shift 2 ;;
            *) fail "unknown spawn-session option $1" ;;
        esac
    done
    local id; id="$(cat "$STATE/director.id" 2>/dev/null || true)"
    [[ -n "$id" ]] || fail "no rig Director - run 'up' first."
    [[ "$agent" == "ClaudeCode" ]] && copy_signin
    [[ -n "$name" ]] || name="Session Rules rig - $agent - $(basename "$dir")"
    local args=(session spawn "$dir" --agent "$agent" --director "$id" --standalone --name "$name")
    [[ -n "$command" ]] && args+=(--command "$command")
    cmd_cli "${args[@]}"
}

cmd_prompt() {
    [[ $# -eq 2 ]] || fail "usage: prompt <session> <text>"
    cmd_cli session prompt "$1" "$2"
}

# ------------------------------------------------------------------------------------------------
cmd_make_repos() {
    ensure_rig_dir
    local name origin
    for pair in "repo-a https://github.com/session-rules-rig/repo-a.git" "repo-b git@github.com:session-rules-rig/repo-b.git"; do
        name="${pair%% *}"; origin="${pair#* }"
        if [[ ! -d "$REPOS/$name/.git" ]]; then
            mkdir -p "$REPOS/$name"
            git -C "$REPOS/$name" init -q -b main
            git -C "$REPOS/$name" remote add origin "$origin"
            echo "# $name - a scratch repository for the Session Rules live rig" > "$REPOS/$name/README.md"
            git -C "$REPOS/$name" add README.md
            git -C "$REPOS/$name" -c user.name=rig -c user.email=rig@invalid commit -q -m "rig scratch repository"
        fi
        say "$REPOS/$name  origin $(git -C "$REPOS/$name" remote get-url origin)"
    done
    say "normalised (ruling P6): github.com/session-rules-rig/repo-a and github.com/session-rules-rig/repo-b"
}

# ------------------------------------------------------------------------------------------------
# rules: a thin wrapper over the rig Gateway's /gateway/rules routes.
#
# THE HOOK-RULE FIELD NAMES in create-hook-probe are taken from phase-1-plan.md rulings P2, P4 and P7
# (kind, appliesTo, event, tool, handler, match, result). Developer A owns the wire; if the merged
# branch names a field differently, the Gateway refuses the write with its own reason, and this one
# function is the place to change.
# ------------------------------------------------------------------------------------------------
cmd_rules() {
    local sub="${1:-}"; shift || true
    case "$sub" in
        list)      gw GET /gateway/rules ;;
        get)       gw GET "/gateway/rules/$1" ;;
        firings)   gw GET "/gateway/rules/$1/firings" ;;
        delete)    gw DELETE "/gateway/rules/$1" ;;
        create)    # create <json file>
                   [[ -f "${1:-}" ]] || fail "usage: rules create <json file>"
                   gw POST /gateway/rules "$(cat "$1")" ;;
        promote)   gw POST "/gateway/rules/$1/promote" '{"acknowledgement":"I have read this rule'"'"'s dry-run record and I am making it live."}' ;;
        all-firings)
                   local ids; ids="$(gw GET /gateway/rules | python3 -c 'import json,sys; t=sys.stdin.read(); t=t[:t.rfind("[http")]; [print(r["id"]) for r in json.loads(t)["rules"]]')"
                   for id in $ids; do echo "== rule $id"; gw GET "/gateway/rules/$id/firings"; done ;;
        effective) # effective <machine> [<normalised repo>]   (W1)
                   gw GET "/gateway/rules/effective?kind=hook&machine=$(urlencode "${1:-$MACHINE}")&repo=$(urlencode "${2:-}")" ;;
        create-hook-probe)
                   # create-hook-probe <everywhere|machine|repo|repo_on_machine> [--repo R] [--machine M] [--except-machine M] [--match TEXT] [--result deny|allow]
                   gw POST /gateway/rules "$(hook_probe_body "$@")" ;;
        create-usage-limit)
                   # The September usage-limit screen rule (qa-report.md of the 2026-09-02 mission), scoped
                   # to the rig's plain-shell sessions.
                   gw POST /gateway/rules "$(usage_limit_body)" ;;
        evidence)  cmd_evidence "$@" ;;
        *) fail "rules: list | get <id> | firings <id> | all-firings | create <json file> | create-hook-probe <scope> [...] | create-usage-limit | promote <id> | delete <id> | effective [<machine> [<repo>]] | evidence [<session>]" ;;
    esac
}

urlencode() { python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }

hook_probe_body() {
    local kind="${1:-}"; shift || true
    local repo="" machines=() except=() match="echo RULE-PROBE" result="deny"
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --repo) repo="$2"; shift 2 ;;
            --machine) machines+=("$2"); shift 2 ;;
            --except-machine) except+=("$2"); shift 2 ;;
            --match) match="$2"; shift 2 ;;
            --result) result="$2"; shift 2 ;;
            *) fail "unknown create-hook-probe option $1" ;;
        esac
    done
    [[ "$kind" =~ ^(everywhere|machine|repo|repo_on_machine)$ ]] || fail "create-hook-probe <everywhere|machine|repo|repo_on_machine> ..."
    python3 - "$kind" "$repo" "$match" "$result" "${machines[*]:-}" "${except[*]:-}" <<'EOF'
import json, sys
kind, repo, match, result, machines, except_ = sys.argv[1:7]
applies = {"kind": kind}
if machines: applies["machines"] = machines.split()
if except_: applies["exceptMachines"] = except_.split()
if repo: applies["repo"] = repo
print(json.dumps({
    "kind": "hook",
    "instruction": f"Live rig probe: a shell command containing '{match}' is {'refused' if result == 'deny' else 'allowed'} ({kind}).",
    "appliesTo": applies,
    "event": "before_command",
    "tool": "shell",
    "handler": "match",
    "match": [match],
    "result": result,
}))
EOF
}

usage_limit_body() {
    python3 - <<'EOF'
import json
print(json.dumps({
    "instruction": "If a session's screen says it has run out of its model allowance, type the command that shows me what is left.",
    "screenDescription": "A session that has stopped on a notice from its model provider saying its allowance for the current model is used up.",
    "triggerWords": ["limit", "usage-credits", "allowance", "out of credits"],
    "checks": [{"name": "matches_any", "arguments": {"text": "<screen_text>", "terms": ["limit", "allowance"]}}],
    "scope": {"agent": "RawCli"},
    "cooldownSeconds": 20,
    "dailyCap": 5,
}))
EOF
}

# What the hook wrote on this machine: the per-session rules files (W2), the evaluation log lines (W4)
# and any decision files not yet forwarded (W3). Read straight from the rig root.
cmd_evidence() {
    local filter="${1:-}"
    local f
    say "rules files (W2) under $ROOT:"
    find "$ROOT" -path '*/session-rules/*.json' -name "*${filter}*" 2>/dev/null | while read -r f; do echo "--- $f"; cat "$f"; echo; done
    say "evaluation logs (W4):"
    find "$ROOT" -path '*/session-rules/*' -name "*${filter}*.eval.log" 2>/dev/null | while read -r f; do echo "--- $f"; tail -50 "$f"; done
    say "decision files waiting to be forwarded (W3):"
    find "$ROOT" -path '*/decisions/*' -name '*.json' 2>/dev/null | grep -- "$filter" || echo "  (none waiting)"
}

# Every shell command a rig Claude Code session ran, with its call id, read from Claude Code's own
# transcripts under the rig HOME. The evaluation log (W4) ends each line with that call id (ruling P14),
# so a "no_match" line is tied to exactly one command here - the negative control becomes a presence.
cmd_commands() {
    local filter="${1:-}"
    python3 - "$RIG_HOME/.claude/projects" "$filter" <<'EOF'
import glob, json, os, sys
root, filter_ = sys.argv[1:3]
files = sorted(glob.glob(os.path.join(root, "*", "*.jsonl")), key=os.path.getmtime)
if not files:
    print(f"(no Claude Code transcripts under {root})")
for path in files:
    if filter_ and filter_ not in path:
        continue
    print(f"--- {path}")
    for line in open(path, errors="replace"):
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        content = (entry.get("message") or {}).get("content")
        if not isinstance(content, list):
            continue
        for part in content:
            if isinstance(part, dict) and part.get("type") == "tool_use" and part.get("name") in ("Bash", "PowerShell"):
                print(f"{entry.get('timestamp', '')}  {part.get('id')}  {part.get('name')}: {(part.get('input') or {}).get('command', '')}")
EOF
}

# ------------------------------------------------------------------------------------------------
cmd_down() {
    local reg id pid alive
    while read -r id pid alive; do
        [[ -n "$id" ]] || continue
        if [[ "$alive" != "alive" ]]; then say "Director $id: registration names process $pid, which is not the rig Director - nothing to stop"; continue; fi
        mkdir -p "$SIGNAL_DIR"
        date -u +%FT%T.%NZ > "$SIGNAL_DIR/cc-director-shutdown-$id.request"
        say "Director $id: named shutdown signal sent ($SIGNAL_DIR/cc-director-shutdown-$id.request); waiting for process $pid"
        if wait_for 90 "Director process $pid to exit" bash -c "! test -d /proc/$pid"; then
            say "Director process $pid exited on its signal"
        else
            say "Director process $pid is STILL RUNNING after its signal - NOT forced; stop it by hand only if it is genuinely stuck"
        fi
    done < <(director_registration)

    local gpid; gpid="$(gateway_pid)"
    if [[ -n "$gpid" ]]; then
        say "Gateway process $gpid ($GATEWAY_EXE): terminate signal"
        /bin/kill -TERM "$gpid"
        if wait_for 60 "Gateway process $gpid to exit" bash -c "! test -d /proc/$gpid"; then
            say "Gateway process $gpid exited"
        else
            say "Gateway process $gpid is STILL RUNNING - NOT forced"
        fi
    else
        say "Gateway: no rig Gateway process is running"
    fi

    if [[ -f "$RIG_HOME/.claude/.credentials.json" ]]; then
        rm -f "$RIG_HOME/.claude/.credentials.json"
        say "deleted the rig's copy of the Claude Code sign-in"
    fi

    local left; left="$(rig_processes)"
    if [[ -n "$left" ]]; then
        echo "$left" | while read -r p e; do say "STILL RUNNING: process $p $e"; done
        say "nothing was force-killed."
    else
        say "rig is down: nothing runs from $RIG_DIR"
    fi
    say "~/.local/bin/cc-devthrottle -> $(readlink "$REAL_HOME/.local/bin/cc-devthrottle")"
}

cmd_reset() {
    cmd_down
    [[ -z "$(rig_processes)" ]] || fail "refusing to delete $RIG_DIR while something still runs from it"
    if [[ -d "$RIG_DIR" ]]; then
        assert_rig_dir
        rm -rf -- "${RIG_DIR:?}"
        say "deleted $RIG_DIR"
    fi
}

# ------------------------------------------------------------------------------------------------
verb="${1:-}"; shift || true
case "$verb" in
    build)          cmd_build "$@" ;;
    up)             cmd_up ;;
    status)         cmd_status ;;
    make-repos)     cmd_make_repos ;;
    spawn-session)  cmd_spawn_session "$@" ;;
    prompt)         cmd_prompt "$@" ;;
    cli)            cmd_cli "$@" ;;
    rules)          cmd_rules "$@" ;;
    commands)       cmd_commands "$@" ;;
    down)           cmd_down ;;
    reset)          cmd_reset ;;
    _start-gateway) cmd__start_gateway ;;
    -h|--help|"")   awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$SCRIPT_PATH" ;;
    *)              fail "unknown verb '$verb' - run with --help" ;;
esac

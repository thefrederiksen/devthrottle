#!/usr/bin/env bash
#
# measure-rule-hook.sh - How much time does the Session Rules hook add to every shell command?
#
# Claude Code runs cc-rule-hook before EVERY shell command of every Director session (Session Rules part
# 2, phase 1, ruling P9), so its cost is paid per command. The Director registers the hook in EXEC form
# (ClaudeHookInstaller: the absolute path as the command, an empty args list), so Claude Code starts the
# program itself with no shell in front of it. This script does the same - it starts the evaluator
# directly, with no shell - with a real Claude Code PreToolUse event on standard input, many times, and
# prints the median and the 95th percentile for:
#
#   baseline        a plain program started directly (/bin/true) - what any exec-form hook costs before it
#                   does anything, starting one program, as every hook row does
#   shell only      /bin/sh -c /bin/true, for comparison: the shell start the earlier SHELL-form
#                   registration paid on top of the program. Not paid since the change to exec form.
#   no-session      the hook outside a Director session (no CC_SESSION_RULES_FILE): it does nothing
#   no-rules-file   a Director session whose rules file is not there yet
#   0 rules         a rules file with an empty rules list
#   1 rule          one rule, which matches, so a decision file is written every run
#   50 rules        fifty rules; the last matches, so all fifty are checked and a decision is written
#
# The hook's ADDED time is its row minus the baseline row (not the shell-only row). Everything is written under a scratch folder
# that is deleted at the end; nothing touches a real session. Run from inside a Director session, the
# session's own two variables are removed for every row, and the script fails if that session's evaluation
# log or decision drop changed.
#
# Usage:
#   scripts/measure-rule-hook.sh                       # publishes the hook first if it is not built
#   scripts/measure-rule-hook.sh --hook /path/to/cc-rule-hook --runs 500
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
RUNS=300
HOOK=""
EVENT="$REPO_ROOT/src/CcDirector.RuleHook.Tests/Fixtures/claude-code-2.1.302-pretooluse-bash.json"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --hook) HOOK="$2"; shift 2 ;;
        --runs) RUNS="$2"; shift 2 ;;
        --event) EVENT="$2"; shift 2 ;;
        -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 1 ;;
    esac
done

if [[ -z "${EPOCHREALTIME:-}" ]]; then
    echo "ERROR: this needs bash 5 or later (EPOCHREALTIME)." >&2
    exit 1
fi

case "$(uname -m)" in
    x86_64|amd64)  RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) echo "ERROR: unsupported architecture '$(uname -m)'" >&2; exit 1 ;;
esac

if [[ -z "$HOOK" ]]; then
    HOOK="$REPO_ROOT/src/CcDirector.RuleHook/bin/Release/net10.0/$RID/publish/cc-rule-hook"
    if [[ ! -x "$HOOK" ]]; then
        DOTNET="dotnet"
        [[ -x "$HOME/.dotnet/dotnet" ]] && DOTNET="$HOME/.dotnet/dotnet"
        echo "Publishing cc-rule-hook for $RID..."
        "$DOTNET" publish "$REPO_ROOT/src/CcDirector.RuleHook/CcDirector.RuleHook.csproj" -c Release -r "$RID" --nologo -v q
    fi
fi
if [[ ! -x "$HOOK" ]]; then
    echo "ERROR: no executable hook at $HOOK" >&2
    exit 1
fi
if [[ ! -f "$EVENT" ]]; then
    echo "ERROR: no event file at $EVENT" >&2
    exit 1
fi

SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT
mkdir -p "$SCRATCH/session-rules" "$SCRATCH/decisions"

# One rule in the W2 shape. $1 id, $2 the literal match text.
rule_json() {
    printf '{"id":"%s","rank":3,"appliesTo":"repo","event":"before_command","tool":"shell","match":["%s"],"result":"deny","reason":"measurement rule","state":"dry_run"}' "$1" "$2"
}

# A rules file with $1 rules; when $1 > 0 the LAST one matches the event's command (echo RULE-PROBE).
write_rules() {
    local count="$1" path="$2" rules="" i
    for (( i = 1; i <= count; i++ )); do
        local text="never-matches-$i"
        (( i == count )) && text="echo RULE-PROBE"
        [[ -n "$rules" ]] && rules+=","
        rules+="$(rule_json "rule-$i" "$text")"
    done
    printf '{"version":1,"sessionId":"measure","machine":"%s","repo":"github.com/example/measure","generatedUtc":"2026-10-18T00:00:00.0000000Z","rules":[%s]}' \
        "$(hostname)" "$rules" > "$path"
}

# Run "$@" RUNS times with the event on standard input; print median and 95th percentile in microseconds.
measure() {
    local label="$1"; shift
    local samples=() i start end
    # Two warm-up runs so the first row is not paying for a cold file cache.
    "$@" < "$EVENT" > /dev/null; "$@" < "$EVENT" > /dev/null
    for (( i = 0; i < RUNS; i++ )); do
        start="${EPOCHREALTIME/[.,]/}"
        "$@" < "$EVENT" > /dev/null
        end="${EPOCHREALTIME/[.,]/}"
        samples+=( $(( end - start )) )
    done
    local sorted
    sorted="$(printf '%s\n' "${samples[@]}" | sort -n)"
    local median p95
    median="$(sed -n "$(( (RUNS + 1) / 2 ))p" <<< "$sorted")"
    p95="$(sed -n "$(( (RUNS * 95 + 99) / 100 ))p" <<< "$sorted")"
    printf '%-16s median %7.2f ms   95th percentile %7.2f ms\n' "$label" \
        "$(awk "BEGIN { print $median / 1000 }")" "$(awk "BEGIN { print $p95 / 1000 }")"
}

# Outside a Director session: BOTH session variables removed, whatever the operator's shell carries.
run_hook() {
    env -u CC_SESSION_RULES_FILE -u CC_SESSION_DECISION_DIR "$HOOK"
}

run_hook_with() {
    CC_SESSION_RULES_FILE="$1" CC_SESSION_DECISION_DIR="$SCRATCH/decisions" "$HOOK"
}

# The operator's own session, if this runs inside one: what it holds now, to prove afterwards that the
# measurement never wrote into it.
OWN_LOG=""; OWN_LOG_BEFORE=0; OWN_DROP_BEFORE=0
if [[ -n "${CC_SESSION_RULES_FILE:-}" ]]; then
    OWN_LOG="${CC_SESSION_RULES_FILE%.json}.eval.log"
    [[ -f "$OWN_LOG" ]] && OWN_LOG_BEFORE="$(wc -l < "$OWN_LOG")"
fi
if [[ -n "${CC_SESSION_DECISION_DIR:-}" && -d "${CC_SESSION_DECISION_DIR}" ]]; then
    OWN_DROP_BEFORE="$(find "$CC_SESSION_DECISION_DIR" -maxdepth 1 -name '*.json' | wc -l)"
fi

echo "cc-rule-hook: $HOOK ($(stat -c%s "$HOOK") bytes)"
echo "event: $EVENT"
echo "runs per row: $RUNS, on $(hostname), $(uname -sr), $(nproc) processors"
# Every row is a wall-clock time, so a busy machine inflates all of them. Say how busy it was.
echo "load average at start: $(cut -d' ' -f1-3 /proc/loadavg)"
echo ""

measure "baseline" /bin/true
measure "shell only" /bin/sh -c /bin/true
measure "no-session" run_hook
measure "no-rules-file" run_hook_with "$SCRATCH/session-rules/absent.json"
write_rules 0 "$SCRATCH/session-rules/zero.json"
measure "0 rules" run_hook_with "$SCRATCH/session-rules/zero.json"
write_rules 1 "$SCRATCH/session-rules/one.json"
measure "1 rule" run_hook_with "$SCRATCH/session-rules/one.json"
write_rules 50 "$SCRATCH/session-rules/fifty.json"
measure "50 rules" run_hook_with "$SCRATCH/session-rules/fifty.json"

# A presence check, so a row cannot have measured a hook that silently did nothing: the matching rows
# must have written decisions, and every session row must have logged its evaluations.
decisions="$(find "$SCRATCH/decisions" -name '*.json' | wc -l)"
expected=$(( 2 * (RUNS + 2) ))
echo "load average at end:   $(cut -d' ' -f1-3 /proc/loadavg)"
echo ""
echo "decision files written: $decisions (expected $expected from the 1-rule and 50-rule rows)"
for name in absent zero one fifty; do
    lines="$(wc -l < "$SCRATCH/session-rules/$name.eval.log")"
    echo "evaluation log $name: $lines lines, last: $(tail -n 1 "$SCRATCH/session-rules/$name.eval.log")"
done
if (( decisions != expected )); then
    echo "ERROR: the hook did not record what it was measured doing." >&2
    exit 1
fi
if [[ -n "$OWN_LOG" ]]; then
    own_log_after=0
    [[ -f "$OWN_LOG" ]] && own_log_after="$(wc -l < "$OWN_LOG")"
    own_drop_after=0
    [[ -n "${CC_SESSION_DECISION_DIR:-}" && -d "$CC_SESSION_DECISION_DIR" ]] \
        && own_drop_after="$(find "$CC_SESSION_DECISION_DIR" -maxdepth 1 -name '*.json' | wc -l)"
    echo "this session's own evaluation log: $OWN_LOG_BEFORE lines before, $own_log_after after; its decision drop: $OWN_DROP_BEFORE files before, $own_drop_after after"
    # Other shell commands of this session can add lines while the script runs, but only a handful; the
    # measurement would add hundreds.
    if (( own_log_after - OWN_LOG_BEFORE > 20 || own_drop_after - OWN_DROP_BEFORE > 20 )); then
        echo "ERROR: the measurement wrote into this session's own evaluation log or decision drop." >&2
        exit 1
    fi
fi

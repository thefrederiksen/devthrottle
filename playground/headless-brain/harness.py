"""Headless-brain test harness for issue #172.

Drives a Claude Code session running inside a CC Director purely over the
Director's Control API (REST) - no keyboard, no visible window interaction.
This is the spike for the always-on warm brain session under the Gateway:

    create  - spawn a fresh headless session and wait until it is ready
    send    - send a prompt, wait for the turn to end, print reply + latency
    clear   - send /clear, prove the context actually reset (token counts)
    status  - activity state + token usage snapshot
    buffer  - tail of the terminal buffer (what a human would see)
    bench   - warm session vs cold `claude -p` latency comparison
    kill    - terminate the session

Usage:
    python harness.py create [--port 7886] [--repo PATH]
    python harness.py send "your prompt here" [--timeout 300]
    python harness.py clear
    python harness.py status
    python harness.py buffer [--lines 40]
    python harness.py bench "prompt used for both paths"
    python harness.py kill

State (port + session id) persists in state.json next to this script, so
`create` once and then keep calling send/clear/status from any shell.

Pure stdlib. ASCII-only output.
"""

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

# The PTY buffer contains box-drawing characters; the Windows console default
# codepage (cp1252) cannot encode them. Force utf-8 and never crash on output.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
STATE_FILE = os.path.join(HERE, "state.json")
DEFAULT_SANDBOX = os.path.join(HERE, "sandbox")
TOKEN_FILE = os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "cc-director", "config", "director", "gateway-token.txt"
)

POLL_INTERVAL = 0.2          # seconds between activity-state polls
LEAVE_IDLE_TIMEOUT = 20.0    # max seconds to wait for the session to START working
DONE_STATES = ("Idle", "WaitingForInput", "WaitingForPerm")


# ---------------------------------------------------------------- plumbing

def load_state():
    if not os.path.exists(STATE_FILE):
        return {}
    with open(STATE_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


def save_state(state):
    with open(STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)


def bearer_token():
    try:
        with open(TOKEN_FILE, "r", encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return None


def api(port, method, path, body=None, timeout=30):
    url = f"http://127.0.0.1:{port}{path}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    tok = bearer_token()
    if tok:
        req.add_header("Authorization", f"Bearer {tok}")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, {"error": raw}


def require_session(state):
    if not state.get("sid") or not state.get("port"):
        print("ERROR: no active session. Run `python harness.py create` first.")
        sys.exit(1)
    return state["port"], state["sid"]


def get_session(port, sid):
    code, dto = api(port, "GET", f"/sessions/{sid}")
    if code != 200:
        print(f"ERROR: GET /sessions/{sid} -> {code}: {dto}")
        sys.exit(1)
    return dto


def get_usage(port, sid):
    code, dto = api(port, "GET", f"/sessions/{sid}/usage")
    return dto if code == 200 else None


def get_summary(port, sid):
    code, dto = api(port, "GET", f"/sessions/{sid}/summary")
    return dto if code == 200 else None


# ---------------------------------------------------------- turn detection

def wait_until_ready(port, sid, timeout=120):
    """Wait for a freshly created session to reach a prompt-accepting state."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        dto = get_session(port, sid)
        state = dto["activityState"]
        if state in DONE_STATES:
            return dto
        if state == "Exited":
            print("ERROR: session exited during startup. Check the director log.")
            sys.exit(1)
        time.sleep(0.5)
    print(f"ERROR: session not ready after {timeout}s")
    sys.exit(1)


def wait_turn_end(port, sid, timeout=300):
    """After a prompt was sent: wait for the turn to complete.

    Phase 1: wait for the session to leave Idle (turn picked up).
    Phase 2: wait for it to come back to Idle / WaitingForInput (turn over).
    Returns (final_state, turn_seconds, started_working).
    """
    t0 = time.monotonic()
    started_working = False
    leave_deadline = t0 + LEAVE_IDLE_TIMEOUT
    while time.monotonic() < leave_deadline:
        state = get_session(port, sid)["activityState"]
        if state == "Working":
            started_working = True
            break
        if state == "Exited":
            return "Exited", time.monotonic() - t0, started_working
        time.sleep(POLL_INTERVAL)

    deadline = t0 + timeout
    while time.monotonic() < deadline:
        state = get_session(port, sid)["activityState"]
        if state in DONE_STATES and (started_working or time.monotonic() - t0 > LEAVE_IDLE_TIMEOUT):
            return state, time.monotonic() - t0, started_working
        if state == "Exited":
            return "Exited", time.monotonic() - t0, started_working
        time.sleep(POLL_INTERVAL)
    return "TIMEOUT", time.monotonic() - t0, started_working


# ----------------------------------------------------------------- verbs

def cmd_create(args):
    repo = os.path.abspath(args.repo)
    if not os.path.isdir(repo):
        os.makedirs(repo)
        with open(os.path.join(repo, "README.md"), "w", encoding="utf-8") as f:
            f.write("# headless-brain sandbox\n\nScratch working directory for the issue #172 spike.\n")
        print(f"[create] made sandbox dir {repo}")

    body = {"repoPath": repo, "agent": "ClaudeCode", "wingmanEnabled": False}
    code, dto = api(args.port, "POST", "/sessions", body)
    if code != 201:
        print(f"ERROR: POST /sessions -> {code}: {dto}")
        sys.exit(1)
    sid = dto["sessionId"]
    print(f"[create] session {sid} starting (state={dto['activityState']})")

    t0 = time.monotonic()
    ready = wait_until_ready(args.port, sid)
    print(f"[create] READY in {time.monotonic() - t0:.1f}s (state={ready['activityState']})")
    save_state({"port": args.port, "sid": sid, "repo": repo})
    print(f"[create] state saved to {STATE_FILE}")


def wait_for_quiet(port, sid, quiet=2.0, timeout=30.0):
    """Wait until the terminal has been byte-silent for `quiet` seconds.

    Sending a prompt while claude-code is still repainting (e.g. right after
    /clear) can get the trailing Enter swallowed by the redraw, leaving the
    text sitting unsubmitted in the composer. The Director serves the idle
    clock (`idleSeconds`), so we gate on real byte-silence, not a blind sleep.
    """
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        dto = get_session(port, sid)
        if dto["activityState"] in DONE_STATES and dto["idleSeconds"] >= quiet:
            return
        time.sleep(POLL_INTERVAL)
    print(f"WARN: terminal never went quiet for {quiet}s within {timeout}s, sending anyway")


def send_and_wait(port, sid, text, timeout):
    wait_for_quiet(port, sid)
    code, resp = api(port, "POST", f"/sessions/{sid}/prompt", {"text": text, "appendEnter": True})
    if code != 200:
        print(f"ERROR: POST prompt -> {code}: {resp}")
        sys.exit(1)
    return wait_turn_end(port, sid, timeout)


def cmd_send(args):
    port, sid = require_session(load_state())
    pre = get_session(port, sid)
    if pre["activityState"] not in DONE_STATES:
        print(f"WARN: session is {pre['activityState']}, sending anyway")

    print(f"[send] -> {args.text[:120]}")
    state, secs, worked = send_and_wait(port, sid, args.text, args.timeout)
    print(f"[send] turn ended: state={state}, latency={secs:.1f}s, sawWorking={worked}")

    summary = get_summary(port, sid)
    if summary and summary.get("status") == "ok":
        reply = summary.get("lastAssistantText") or "(no assistant text)"
        print("[send] reply:")
        print("  " + reply.replace("\n", "\n  "))
    else:
        print(f"[send] summary unavailable: {summary.get('status') if summary else 'no response'}")

    usage = get_usage(port, sid)
    if usage:
        print(f"[send] context={usage['contextTokens']} tokens, "
              f"turns={len(usage.get('turns', []))}, "
              f"totalOut={usage['outputTokens']}")


def claude_projects_dir(repo):
    """Claude Code's transcript directory for a repo: every char outside
    [a-zA-Z0-9-] in the absolute path becomes '-'."""
    mangled = "".join(c if c.isalnum() or c == "-" else "-" for c in os.path.abspath(repo))
    return os.path.join(os.path.expanduser("~"), ".claude", "projects", mangled)


def newest_claude_session_id(repo):
    d = claude_projects_dir(repo)
    if not os.path.isdir(d):
        return None
    jsonls = [f for f in os.listdir(d) if f.endswith(".jsonl")]
    if not jsonls:
        return None
    newest = max(jsonls, key=lambda f: os.path.getmtime(os.path.join(d, f)))
    return newest[:-len(".jsonl")]


def relink_to_newest(port, sid, repo):
    """After /clear, claude starts a NEW internal session id. The director keeps
    serving /summary and /usage from the OLD jsonl until relinked. Point it at
    the newest transcript so the spike's verification reads the right file."""
    new_id = newest_claude_session_id(repo)
    if not new_id:
        print("[clear] relink: no jsonl found, skipping")
        return
    code, resp = api(port, "POST", f"/sessions/{sid}/relink", {"claudeSessionId": new_id})
    print(f"[clear] relink -> {code}: claudeSessionId={new_id}")


def cmd_clear(args):
    port, sid = require_session(load_state())
    repo = load_state().get("repo", DEFAULT_SANDBOX)

    before = get_usage(port, sid)
    if before:
        print(f"[clear] BEFORE: context={before['contextTokens']} tokens, "
              f"assistantMsgs={before['assistantMessageCount']}")
    else:
        print("[clear] BEFORE: usage unavailable (no claude session id yet?)")

    print("[clear] sending /clear")
    state, secs, worked = send_and_wait(port, sid, "/clear", args.timeout)
    print(f"[clear] settled: state={state} after {secs:.1f}s")

    print("[clear] verification prompt: asking for context recall...")
    state, secs, worked = send_and_wait(
        port, sid,
        "Without using any tools: what did we talk about before this message? "
        "If you have no prior conversation in context, reply exactly: CONTEXT-EMPTY",
        args.timeout)
    print(f"[clear] verification turn: state={state}, latency={secs:.1f}s")

    relink_to_newest(port, sid, repo)

    summary = get_summary(port, sid)
    if summary and summary.get("status") == "ok":
        reply = summary.get("lastAssistantText") or ""
        print("[clear] reply: " + reply.strip()[:300])
        verdict = "PASS (context empty)" if "CONTEXT-EMPTY" in reply else "CHECK MANUALLY"
        print(f"[clear] verdict: {verdict}")
    else:
        print(f"[clear] summary status: {summary.get('status') if summary else 'none'} "
              f"- the director may still point at the PRE-clear jsonl (relink gap, spike finding)")

    after = get_usage(port, sid)
    if after:
        print(f"[clear] AFTER: context={after['contextTokens']} tokens, "
              f"assistantMsgs={after['assistantMessageCount']}")
        if before and after["contextTokens"] < before["contextTokens"]:
            print("[clear] token count dropped: context reset CONFIRMED")
    else:
        print("[clear] AFTER: usage unavailable")


def cmd_status(args):
    port, sid = require_session(load_state())
    dto = get_session(port, sid)
    print(f"session   : {sid}")
    print(f"status    : {dto['status']}")
    print(f"activity  : {dto['activityState']}")
    print(f"idle      : {dto['idleSeconds']:.1f}s (quiet threshold {dto['quietThresholdSeconds']}s)")
    print(f"buffer    : {dto['totalBufferBytes']} bytes")
    usage = get_usage(port, sid)
    if usage:
        print(f"context   : {usage['contextTokens']} tokens")
        print(f"turns     : {len(usage.get('turns', []))}")
        print(f"totals    : in={usage['inputTokens']} out={usage['outputTokens']} "
              f"cacheRead={usage['cacheReadTokens']} cacheCreate={usage['cacheCreationTokens']}")
    else:
        print("usage     : unavailable")


def cmd_buffer(args):
    port, sid = require_session(load_state())
    code, dto = api(port, "GET", f"/sessions/{sid}/buffer?lines={args.lines}")
    if code != 200:
        print(f"ERROR: GET buffer -> {code}: {dto}")
        sys.exit(1)
    print(dto.get("text") or "(empty)")


def cmd_bench(args):
    port, sid = require_session(load_state())
    state = load_state()

    print("[bench] WARM: sending through the persistent session...")
    wait_for_quiet(port, sid)
    usage_before = get_usage(port, sid)
    msgs_before = usage_before["assistantMessageCount"] if usage_before else 0

    t0 = time.monotonic()
    code, resp = api(port, "POST", f"/sessions/{sid}/prompt", {"text": args.text, "appendEnter": True})
    if code != 200:
        print(f"ERROR: POST prompt -> {code}: {resp}")
        sys.exit(1)

    # Reply-availability latency: how fast the answer lands in the JSONL.
    # This is what a gateway brief agent would actually wait for - it does not
    # need the terminal-state detector's quiet threshold to pass.
    reply_secs = None
    deadline = t0 + args.timeout
    while time.monotonic() < deadline:
        usage = get_usage(port, sid)
        if usage and usage["assistantMessageCount"] > msgs_before:
            reply_secs = time.monotonic() - t0
            break
        time.sleep(POLL_INTERVAL)

    final, warm_secs, worked = wait_turn_end(port, sid, args.timeout)
    warm_secs = time.monotonic() - t0
    reply_str = f"{reply_secs:.1f}s" if reply_secs is not None else "not observed"
    print(f"[bench] WARM reply-in-jsonl: {reply_str}; turn-end (detector): {warm_secs:.1f}s (state={final})")

    print("[bench] COLD: spawning `claude -p` for the same prompt...")
    t0 = time.monotonic()
    proc = subprocess.run(
        ["claude", "-p", args.text],
        capture_output=True, text=True,
        cwd=state.get("repo", DEFAULT_SANDBOX), timeout=args.timeout)
    cold_secs = time.monotonic() - t0
    print(f"[bench] COLD `claude -p`: {cold_secs:.1f}s (exit={proc.returncode})")
    if proc.returncode != 0:
        print("[bench] COLD stderr: " + (proc.stderr or "").strip()[:300])

    print()
    warm_eff = reply_secs if reply_secs is not None else warm_secs
    print(f"[bench] RESULT: warm reply={warm_eff:.1f}s  warm turn-end={warm_secs:.1f}s  "
          f"cold claude -p={cold_secs:.1f}s")


def cmd_kill(args):
    port, sid = require_session(load_state())
    code, dto = api(port, "DELETE", f"/sessions/{sid}")
    print(f"[kill] DELETE /sessions/{sid} -> {code}")
    if code == 200 and os.path.exists(STATE_FILE):
        os.remove(STATE_FILE)
        print("[kill] state.json removed")


# ------------------------------------------------------------------ main

def main():
    p = argparse.ArgumentParser(description="Headless-brain harness (issue #172)")
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("create", help="spawn a headless session")
    c.add_argument("--port", type=int, default=7886)
    c.add_argument("--repo", default=DEFAULT_SANDBOX)
    c.set_defaults(fn=cmd_create)

    s = sub.add_parser("send", help="send a prompt, wait for the reply")
    s.add_argument("text")
    s.add_argument("--timeout", type=int, default=300)
    s.set_defaults(fn=cmd_send)

    cl = sub.add_parser("clear", help="send /clear and verify the context reset")
    cl.add_argument("--timeout", type=int, default=120)
    cl.set_defaults(fn=cmd_clear)

    st = sub.add_parser("status", help="activity + token usage snapshot")
    st.set_defaults(fn=cmd_status)

    b = sub.add_parser("buffer", help="tail the terminal buffer")
    b.add_argument("--lines", type=int, default=40)
    b.set_defaults(fn=cmd_buffer)

    be = sub.add_parser("bench", help="warm session vs cold claude -p")
    be.add_argument("text")
    be.add_argument("--timeout", type=int, default=300)
    be.set_defaults(fn=cmd_bench)

    k = sub.add_parser("kill", help="terminate the session")
    k.set_defaults(fn=cmd_kill)

    args = p.parse_args()
    args.fn(args)


if __name__ == "__main__":
    main()

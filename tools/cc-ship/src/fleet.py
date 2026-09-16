"""Fleet access for cc-ship: spawn tracked sessions and wait for them to finish.

Every call goes through the cc-devthrottle command line, so cc-ship talks to the
fleet exactly the way any other session does and needs no credentials of its own.

A spawned session has FINISHED only when BOTH are true:
  - the output file it was told to write exists, and
  - the session has said it is done: it was SEEN flagged done (pendingDeletion), or it
    wrote its done marker (<output>.done), which the brief has it write only after its
    `session done` command succeeded. The marker matters because the Director reaps a
    flagged session 30 seconds later, and nobody may be polling in that window (the
    author runs `cc-ship wait` when it chooses). A session that vanishes with neither
    is a crash, output or not.
A session that leaves the fleet list without the flag, or is reported crashed, has
FAILED. A session that was seen working and then sits idle with no output
and no done flag has STALLED - for example it stopped on an error it could not get
past - and has also failed. Nothing else counts as finished.
"""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path

CLI = "cc-devthrottle"
_ID_LINE = re.compile(r"^id:\s*([0-9a-fA-F-]{36})\s*$", re.MULTILINE)

FINISHED = "finished"
CRASHED = "crashed"
STALLED = "stalled"
TIMED_OUT = "timed-out"

# How long a session may sit idle, after it has been seen working, with no output and
# no done flag before it counts as stalled (it ended its turn without doing the job).
STALL_SECONDS = 90

# A session that has not been seen working this long after it was spawned never got
# going (for example the agent stopped at once on a usage-limit screen) and is stalled
# too. Prompt delivery takes seconds; five minutes is far beyond it.
NEVER_WORKED_SECONDS = 300


class FleetError(RuntimeError):
    """A fleet command failed; the message carries the command's own output."""


# cmd.exe re-reads the arguments of a .cmd file. A quote, a percent sign (expanded even
# inside quotes) or a line break is never safe. & | < > ^ are safe only inside quotes,
# and Python quotes an argument only when it contains a space or a tab, so C:\work\R&D
# would reach cmd.exe bare and split the command.
_NEVER_SAFE_FOR_CMD = set('"%\n\r')
_SAFE_ONLY_QUOTED_FOR_CMD = set("&|<>^")


def _cmd_misreads(arg: str) -> bool:
    if _NEVER_SAFE_FOR_CMD & set(arg):
        return True
    quoted = arg == "" or " " in arg or "\t" in arg
    return not quoted and bool(_SAFE_ONLY_QUOTED_FOR_CMD & set(arg))


def command(args: list[str]) -> list[str]:
    """The full command line for cc-devthrottle.

    On Windows cc-devthrottle is a .cmd file, and Windows only finds a bare name that
    ends in .exe, so the full path is looked up first (issue 2961). A .cmd file's
    arguments pass through cmd.exe, so any argument it would misread is refused.
    """
    path = shutil.which(CLI)
    if path is None:
        raise FleetError(f"{CLI} is not on PATH; cc-ship reaches the fleet through it.")
    if path.lower().endswith((".cmd", ".bat")):
        bad = [a for a in args if _cmd_misreads(a)]
        if bad:
            raise FleetError(f"cannot pass {bad[0]!r} safely to {path}: cmd.exe would misread "
                             "it (a quote, a percent sign, a line break, or & | < > ^ in text "
                             "without a space). Rename the folder or branch so it has none.")
    return [path, *args]


def _run(args: list[str], timeout: int = 120) -> str:
    proc = subprocess.run(
        command(args), capture_output=True, text=True, timeout=timeout,
        encoding="utf-8", errors="replace",
    )
    if proc.returncode != 0:
        raise FleetError(
            f"{CLI} {args[0]} {args[1] if len(args) > 1 else ''} failed "
            f"(exit {proc.returncode}): {(proc.stderr or proc.stdout).strip()}"
        )
    return proc.stdout


def spawn_session(
    repo: Path, agent: str, controlled_by: str, name: str, brief: Path
) -> str:
    """Open a tracked session whose whole task is the brief FILE. Returns its id.

    The prompt is one short line pointing at the brief: long spawn prompts can
    fail to arrive, and fleet text truncates at the first newline.
    """
    prompt = f"Read the file {brief} and follow it exactly. It is your whole task."
    try:
        out = _run([
            "session", "spawn", str(repo),
            "--agent", agent,
            "--controlled-by", controlled_by,
            "--name", name,
            "--prompt", prompt,
        ])
    except FleetError as exc:
        # A Gateway timeout does not mean the spawn failed: the session may exist
        # and be working. Settle the ambiguity by name before reporting failure,
        # so no reviewer is ever left running with nobody watching it.
        if "did not answer in time" not in str(exc):
            raise
        time.sleep(15)
        landed = [r for r in list_sessions() if r["name"] == name]
        if len(landed) != 1:
            raise FleetError(
                f"{exc} -- and {len(landed)} sessions named {name!r} exist afterwards, "
                "so whether the spawn landed is unknown"
            ) from exc
        return landed[0]["sessionId"]
    match = _ID_LINE.search(out)
    if match is None:
        raise FleetError(f"session spawn printed no session id: {out.strip()}")
    return match.group(1)


def list_sessions() -> list[dict]:
    data = json.loads(_run(["session", "list", "--json"]))
    return data if isinstance(data, list) else data["sessions"]


def find_session(session_id: str) -> dict | None:
    for row in list_sessions():
        if row["sessionId"] == session_id:
            return row
    return None


def prompt_session(session_id: str, text: str) -> str:
    """Send one line into a session, as if typed. Fleet text stops at a newline."""
    if "\n" in text:
        raise ValueError("a session prompt must be one line; put detail in a brief file")
    return _run(["session", "prompt", session_id, text])


def clear_done_flag(session_id: str) -> str:
    """Take a session's done flag back off so it survives a correction turn."""
    return _run(["session", "done", session_id, "--undo"])


def session_screen(session_id: str) -> str:
    return _run(["session", "buffer", session_id])


def stop_session(session_id: str, reason: str) -> str:
    return _run(["session", "stop", session_id, "--reason", reason])


def done_marker(output: Path) -> Path:
    """The file a session writes after `session done` succeeded (see the module note)."""
    return output.with_name(output.name + ".done")


@dataclass
class WaitResult:
    outcome: str  # FINISHED, CRASHED, STALLED or TIMED_OUT
    reason: str
    observations: list[dict] = field(default_factory=list)


def classify(output_exists: bool, row: dict | None, seen_done: bool) -> tuple[str | None, str]:
    """One observation -> (outcome or None while still working, reason).

    seen_done: the done flag was observed on this or an earlier poll of this wait.
    """
    if row is not None and row.get("crashed"):
        return CRASHED, "session reported crashed"
    flagged_done = seen_done or bool(row and row.get("pendingDeletion"))
    if row is None:
        if output_exists and flagged_done:
            return FINISHED, "output written and session flagged itself done, then was reaped"
        return CRASHED, "session left the fleet list without flagging itself done"
    if output_exists and flagged_done:
        return FINISHED, "output written and session flagged itself done"
    if output_exists:
        return None, "output written, waiting for the session to flag itself done"
    return None, f"working (status={row['status']}, activity={row['activityState']})"


def wait_for_output(
    session_id: str,
    output: Path,
    timeout_seconds: float,
    poll_seconds: float = 10,
    written_after: float | None = None,
    watch: dict | None = None,
) -> WaitResult:
    """Block until the session finishes, crashes, or the timeout passes.

    written_after (a time.time() value) makes an older output file count as absent,
    so a correction turn is only finished once the file has been rewritten.

    watch carries what has been observed across separate calls (cc-ship waits in
    slices of a few minutes, in separate processes): seen_working, seen_done,
    idle_since and started (time.time() values; started defaults to the first call). It is updated in place; pass the same dict
    back on the next call, or a stalled session would never be recognised.
    """

    def fresh(path: Path) -> bool:
        if not path.exists():
            return False
        return written_after is None or path.stat().st_mtime > written_after

    def output_ready() -> bool:
        return fresh(output)

    observations: list[dict] = []
    deadline = time.monotonic() + timeout_seconds
    if watch is None:
        watch = {}
    watch.setdefault("seen_working", False)
    watch.setdefault("seen_done", False)
    watch.setdefault("idle_since", None)
    watch.setdefault("started", time.time())
    while True:
        row = find_session(session_id)
        ready = output_ready()
        watch["seen_done"] = (watch["seen_done"] or fresh(done_marker(output))
                              or bool(row and row.get("pendingDeletion")))
        outcome, reason = classify(ready, row, watch["seen_done"])
        if outcome is None and row is not None:
            if row["activityState"] == "Working":
                watch["seen_working"], watch["idle_since"] = True, None
            elif (not watch["seen_working"] and not ready and not row.get("pendingDeletion")
                  and time.time() - watch["started"] >= NEVER_WORKED_SECONDS):
                screen = session_screen(session_id)[-800:]
                outcome = STALLED
                reason = (f"session was never seen working in {NEVER_WORKED_SECONDS}s after it "
                          f"was spawned; its screen ends: {screen}")
            elif watch["seen_working"] and not ready and not row.get("pendingDeletion"):
                watch["idle_since"] = watch["idle_since"] or time.time()
                if time.time() - watch["idle_since"] >= STALL_SECONDS:
                    screen = session_screen(session_id)[-800:]
                    outcome = STALLED
                    reason = (f"session stopped working {STALL_SECONDS}s ago without writing "
                              f"its output or flagging itself done; its screen ends: {screen}")
        observations.append({
            "at": time.strftime("%H:%M:%S"),
            "output_exists": ready,
            "in_list": row is not None,
            "pendingDeletion": row.get("pendingDeletion") if row else None,
            "crashed": row.get("crashed") if row else None,
            "status": row.get("status") if row else None,
            "activity": row.get("activityState") if row else None,
            "reason": reason,
        })
        if outcome is not None:
            return WaitResult(outcome, reason, observations)
        if time.monotonic() >= deadline:
            return WaitResult(TIMED_OUT, reason, observations)
        time.sleep(poll_seconds)

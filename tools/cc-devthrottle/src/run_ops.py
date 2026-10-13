"""`cc-devthrottle run` - how a scheduled run went (Factory Control, step 1).

- `run result ok` / `run result problem "<one line why>"` - called by a scheduled run's own session at its end.
  The Gateway finds the run from the calling session's key; a session no schedule started is refused. After ok
  the Gateway says to close, and this command closes the session the same way `session done` does; after a
  problem the session stays open for someone to look at.
- `run resolve <run> "<one line reason>"` - resolve a problem; the reason and who resolved it are kept.
- `run problems [--factory <id>] [--all] [--json]` - the account's problems, folded once on the Gateway.

Nothing here decides anything. Whether to close, which runs are problems, and every word is the Gateway's.
"""

from __future__ import annotations

import json
import os
import sys
import urllib.parse
from pathlib import Path
from typing import Any, Dict, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

RESULT_ROUTE = "cron/runs/result"
PROBLEMS_ROUTE = "cron/problems"

#: What a 404 with no words of its own means on these routes: a Gateway older than this command.
OLD_GATEWAY = "the Gateway does not know run results yet (it answered 404 with no reason) - it is older than this command."

#: The reason a run's session gives when it closes itself after an ok result.
CLOSE_REASON = "reported ok"

_PROBLEMS = "cc-devthrottle run problems"
_RESOLVE = 'cc-devthrottle run resolve <run> "<one line reason>"'
_FIELDS = ["run", "schedule", "factory", "shift", "problem", "reason", "session", "state"]


def _resolve_route(run_id: str) -> str:
    return f"cron/runs/{urllib.parse.quote(run_id, safe='')}/resolve"


def _text(value: Any) -> str:
    return axi_cli.ascii_text(str(value)) if value is not None else ""


def _reason(exc: gateway.GatewayError) -> str:
    text = str(exc)
    return OLD_GATEWAY if exc.status == 404 and not text.strip() else text


# ---------- run result ----------


def _close_this_session() -> Dict[str, Any]:
    """Flag this session for deletion, exactly as `session done` does, after the Gateway said to close."""
    sid = os.environ.get("CC_SESSION_ID", "").strip()
    if not sid:
        axi_cli.fail(
            "the result was recorded, but this session cannot close itself: CC_SESSION_ID is not set, so this "
            "command does not know which session it is running in.",
            ["cc-devthrottle session done <session-id>"],
        )
    try:
        resp = gateway.post_json(f"sessions/{sid}/request-deletion", {"reason": CLOSE_REASON})
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"the result was recorded, but session {sid} could not be flagged to close: {err}",
            ["cc-devthrottle session done"],
        )
    pending = resp.get("pendingDeletion", resp.get("PendingDeletion")) if isinstance(resp, dict) else None
    if pending is not True:
        axi_cli.fail(
            f"the result was recorded, but the Gateway's answer to closing session {sid} did not say it is now "
            "flagged to close, so it may still be open.",
            ["cc-devthrottle session done"],
        )
    return resp


def result(outcome: str, reason: Optional[str], json_output: bool) -> None:
    """Report how this scheduled run went, and close the session after ok."""
    wanted = (outcome or "").strip().lower()
    if wanted not in ("ok", "problem"):
        axi_cli.usage_error(f"'{outcome}' is not a result. Use: cc-devthrottle run result ok, or "
                            'cc-devthrottle run result problem "<one line why>".')
    if wanted == "problem" and not (reason or "").strip():
        axi_cli.usage_error('a problem needs its one line why: cc-devthrottle run result problem "<one line why>". '
                            "Nothing was recorded.")

    body: Dict[str, Any] = {"result": wanted}
    if reason and reason.strip():
        body["reason"] = reason.strip()
    try:
        answer = gateway.post_json(RESULT_ROUTE, body)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [_PROBLEMS])

    if not isinstance(answer, dict) or not isinstance(answer.get("run"), dict):
        axi_cli.fail("the Gateway answered with no run, so it cannot be told the result was recorded.",
                     [axi_cli.CHECK_GATEWAY])
    close = answer.get("closeSession") is True
    closed = _close_this_session() if close else None

    if json_output:
        print(json.dumps({"result": answer, "closed": closed is not None}, indent=2))
        return
    run = answer["run"]
    lines = [
        _text(answer.get("text")),
        f"run: {_text(run.get('runId'))}",
        f"schedule: {_text(answer.get('jobName'))} ({_text(answer.get('jobId'))})",
        f"result: {_text(run.get('result'))}" + (f" - {_text(run.get('problem'))}" if run.get("problem") else ""),
    ]
    if run.get("resultReason"):
        lines.append(f"reason: {_text(run.get('resultReason'))}")
    lines.append("session: flagged to close" if closed is not None else "session: stays open")
    next_commands = ["cc-devthrottle session done --undo"] if closed is not None else [_PROBLEMS]
    axi_output.write_blocks(sys.stdout, "\n".join(lines), axi_output.format_help(next_commands))


# ---------- run resolve ----------


def resolve(run_id: str, reason: str, json_output: bool) -> None:
    """Resolve a run's problem with a one-line reason."""
    if not (reason or "").strip():
        axi_cli.usage_error('resolving a problem needs a one-line reason: cc-devthrottle run resolve <run> "<reason>". '
                            "Nothing was changed.")
    try:
        answer = gateway.post_json(_resolve_route(run_id.strip()), {"reason": reason.strip()})
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [_PROBLEMS])

    if not isinstance(answer, dict) or answer.get("state") != "resolved":
        axi_cli.fail("the Gateway's answer did not say the problem is now resolved, so it may still be open.",
                     [_PROBLEMS])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    lines = [
        f"Resolved the problem on run {_text(answer.get('runId'))}.",
        f"schedule: {_text(answer.get('jobName'))} ({_text(answer.get('jobId'))})",
        f"problem: {_text(answer.get('kindText'))} - {_text(answer.get('reason'))}",
        f"resolved by: {_text(answer.get('resolvedBy'))}",
        f"reason: {_text(answer.get('resolvedReason'))}",
    ]
    axi_output.write_blocks(sys.stdout, "\n".join(lines), axi_output.format_help([_PROBLEMS]))


# ---------- run problems ----------


def problems(factory: Optional[str], include_closed: bool, json_output: bool) -> None:
    """The account's scheduled-run problems: open ones, or every one with --all."""
    query: Dict[str, str] = {"state": "all" if include_closed else "open"}
    if factory and factory.strip():
        query["factory"] = factory.strip()
    try:
        answer = gateway.get_json(f"{PROBLEMS_ROUTE}?{urllib.parse.urlencode(query)}")
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    rows = answer.get("problems") if isinstance(answer, dict) else None
    if not isinstance(rows, list) or not isinstance(answer.get("count"), int):
        # Absent is not empty: an answer with no list must never read as "no problems".
        axi_cli.fail("the Gateway answered with no list of problems; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {
            "run": r.get("runId"),
            "schedule": r.get("jobName"),
            "factory": r.get("factory") or "-",
            "shift": r.get("shiftText"),
            "problem": r.get("kindText"),
            "reason": r.get("reason"),
            "session": r.get("sessionId") or "-",
            "state": r.get("state") if r.get("state") == "open" else f"{r.get('state')}: {r.get('stateText')}",
        }
        for r in rows
    ]
    blocks = [axi_output.format_count(len(records))]
    if records:
        blocks.append(axi_output.render_list("problems", _FIELDS, records))
    next_commands = [_RESOLVE, "cc-devthrottle session buffer <session>"] if records else [f"{_PROBLEMS} --all"]
    blocks.append(axi_output.format_help(next_commands))
    axi_output.write_blocks(sys.stdout, *blocks)

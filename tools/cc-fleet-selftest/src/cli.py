"""CLI for cc-fleet-selftest - prove fleet session messaging works end to end.

Spawns two throwaway sessions on the local Director, then exercises the fleet messaging
machinery the cc-* tools wrap (GET /fleet/sessions, POST /fleet/send, POST /fleet/ask) and
asserts each step, then tears the sessions down. Exits 0 when every step passes, non-zero
otherwise. Deterministic: the "responder" session runs cmd with a custom prompt marker, so the
ask step always has a known token to look for (a plain cmd target is not a natural answerer).
"""

import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich.console import Console

# Share the one tools venv: make cc_shared importable when run from source (issue #722).
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

from . import __version__  # noqa: E402

console = Console()

MARKER = "FLEETPONG"


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-fleet-selftest v{__version__}")
        raise typer.Exit()


def _spawn(repo: str, command_args: str, name: str) -> str:
    resp = director.post_json(
        "sessions",
        {"repoPath": repo, "agent": "RawCli", "command": "cmd", "commandArgs": command_args},
    )
    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        raise director.DirectorError("the Director did not return a session id when spawning.")
    # Name it so the run is legible in cc-sessions; a rename failure is not fatal to the test.
    try:
        director.patch_json(f"sessions/{sid}", {"name": name})
    except director.DirectorError:
        pass
    return sid


def _fleet_ids() -> List[str]:
    sessions = director.get_json("fleet/sessions") or []
    return [director.field(s, "sessionId", "SessionId") for s in sessions]


def _run(
    timeout_ms: int = typer.Option(
        25000, "--timeout-ms", help="How long the ask step waits for the responder."
    ),
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Run the fleet messaging self-test against the local Director and report PASS/FAIL."""
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    responder: Optional[str] = None
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {step}{('  - ' + detail) if detail else ''}")

    try:
        # 1. Spawn the two throwaway sessions: a responder (cmd with a marker prompt) and a recipient.
        responder = _spawn(repo, f"/k prompt {MARKER}$G", "selftest-responder")
        recipient = _spawn(repo, "/k", "selftest-recipient")
        record("spawn two sessions", True, f"responder={director.short_id(responder)} recipient={director.short_id(recipient)}")
        time.sleep(2)

        # 2. List the fleet and assert both appear.
        ids = _fleet_ids()
        listed = responder in ids and recipient in ids
        record("cc-sessions lists both", listed)

        # 3. Send a message to the recipient and assert it was accepted for delivery.
        send = director.post_json(
            "fleet/send",
            {"toSessionId": recipient, "text": "fleet self-test message", "fromSessionId": responder},
        )
        accepted = bool(isinstance(send, dict) and send.get("accepted", send.get("Accepted", False)))
        record("cc-send delivers", accepted, str(director.field(send, "error", "Error") or ""))

        # 4. Ask the responder and assert its known marker comes back.
        ask = director.post_json(
            "fleet/ask",
            {"toSessionId": responder, "question": "selftest ping", "fromSessionId": recipient, "timeoutMs": timeout_ms},
            timeout=timeout_ms / 1000.0 + 15.0,
        )
        answer = director.field(ask, "answer", "Answer") if isinstance(ask, dict) else ""
        got_marker = MARKER in answer
        record("cc-ask returns the answer", got_marker, "marker found" if got_marker else f"status={director.field(ask, 'status', 'Status')}")

    except director.DirectorError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        # 5. Tear the throwaway sessions down (best effort), then verify none leaked.
        for sid in (responder, recipient):
            if sid:
                try:
                    director.delete(f"sessions/{sid}")
                except director.DirectorError:
                    pass
        try:
            time.sleep(1)
            remaining = _fleet_ids()
            leaked = [s for s in (responder, recipient) if s and s in remaining]
            record("throwaway sessions cleaned up", not leaked,
                   "" if not leaked else f"leaked {len(leaked)}")
        except director.DirectorError as err:
            record("throwaway sessions cleaned up", False, str(err))

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        raise typer.Exit(0)
    console.print(f"[red]FAIL[/red] - fleet messaging self-test: {passed}/{total} checks passed.")
    raise typer.Exit(1)


def app() -> None:
    """Console-script entry point."""
    typer.run(_run)


if __name__ == "__main__":
    app()

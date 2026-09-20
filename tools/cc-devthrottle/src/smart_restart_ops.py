"""Empty a Director nicely and restart it, and read what it emptied before - from the command line.

The mission "Smart Director Restart", section 5.3 item 12. The Director's own File menu is the first
door onto this; these two commands are the second, so that ONE BROKEN WINDOW CAN NEVER AGAIN LEAVE A
DIRECTOR IMPOSSIBLE TO EMPTY. That is the whole reason they exist, and it is why they go through the
same engine rather than doing anything of their own.

NOTHING HERE DECIDES WHAT ANYTHING MEANS. Every sentence printed below - the phase, each session's
state, the count, why a start was refused, what became of a record - is computed on the Director by
the engine the window calls and is rendered here exactly as it arrived (critical rule 7 in
CLAUDE.md). This file chooses LAYOUT: which line a sentence goes on, and when to ask again. A new
session state is one edit in the engine's own word list and no new branch here.

WHO MAY DO WHICH. Starting is the owner's, exactly as the direct restart route is: a session's key is
refused by the Gateway, which names the request command a session may use instead. Reading - how a
run is going, and the history - is open to a session, because it reads the same account's own records
that `gateway/workspaces` already serves.
"""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich.console import Console

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402

from . import axi_cli  # noqa: E402
from . import machine_ops  # noqa: E402

# soft_wrap: a sentence from the engine is never broken across lines at the console width. These
# sentences are the whole answer, and a half-word at column 80 is a sentence a reader has to
# reassemble before acting on it.
console = Console(soft_wrap=True)

#: How often the run is asked where it stands while watching. The engine raises a change at least
#: once per ten-second poll of its own, so asking more often than this only prints the same snapshot
#: again, and asking much less often would step over a whole phase.
POLL_SECONDS = 5.0

#: How long a start waits before it stops asking, when the run neither ends nor the Director goes
#: away. It is longer than the longest time the engine allows (sixty minutes) plus the restart ask,
#: so a watch that gives up has genuinely outlived the run it was watching.
WATCH_PATIENCE_SECONDS = 75 * 60

#: The exit code of a start that was accepted and not watched to the end, or of a watch that could no
#: longer read the Director. Neither success (nothing is known to have finished) nor failure (nothing
#: is known to have failed). The same code and the same meaning as `director restore` uses.
EXIT_ACCEPTED_NOT_WAITED = machine_ops.EXIT_ACCEPTED_NOT_WAITED

#: The one outcome that means the Director was emptied AND its launcher took the restart.
RESTART_ACCEPTED = "RestartAccepted"


def _director_path(director_id: str, rest: str) -> str:
    return f"directors/{gateway.path_segment(director_id)}/{rest}"


def resolve_director(director: Optional[str], machine: Optional[str]) -> str:
    """The Director these commands act on: the one named, or the one this session belongs to.

    Resolved by the tool's ONE resolver - the same one `session spawn --director` uses - so an id, an
    exact name and an id prefix mean here exactly what they mean there, and an ambiguous name is
    refused rather than guessed. A second resolver would be a second set of rules for one idea.
    """
    from .session_ops import _my_director, _resolve_director_id

    if director and director.strip():
        try:
            return _resolve_director_id(director, machine or "")
        except gateway.GatewayError as err:
            axi_cli.fail(str(err), ["cc-devthrottle director list"])
    try:
        return _my_director()
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"{err} Name the Director to act on with --director.",
            ["cc-devthrottle director list"],
        )


# --- starting one -------------------------------------------------------------------------------


def smart_restart(director: Optional[str], machine: Optional[str], minutes: int, reason: Optional[str],
                  watch: bool, json_output: bool) -> None:
    """Start a smart shutdown with the restart purpose, and watch it to the end.

    The Gateway answers as soon as the run is TAKEN, because the run lasts as long as the owner
    allowed - up to an hour - and no request stays open that long. So the answer is an acceptance,
    and where it got to is read back from the same door until it ends.
    """
    director_id = resolve_director(director, machine)
    body: Dict[str, Any] = {"minutes": minutes}
    if reason and reason.strip():
        body["reason"] = reason.strip()

    try:
        payload = gateway.post_json(_director_path(director_id, "smart-restart"), body)
    except gateway.GatewayError as err:
        # The Gateway's own words, including the guard's refusal of a session key, which names the
        # command a session may run instead. Nothing is rewritten here.
        axi_cli.fail(
            f"the smart restart of Director {director_id} did not start: {err}",
            [
                f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')}",
                "cc-devthrottle director list",
            ],
        )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    detail = gateway.field(payload, "detail", "Detail") if isinstance(payload, dict) else ""
    console.print(f"[green]STARTED[/] smart restart of Director {axi_cli.shown(director_id)}")
    if detail:
        console.print(f"  {axi_cli.shown(detail)}")
    console.print("")

    if not watch:
        axi_cli.write_lines(
            f"{machine_ops.ACCEPTED_NOT_WAITED}: --no-watch was asked for, so nothing here knows how it ended."
        )
        axi_cli.print_next([
            f"cc-devthrottle director smart-restart-status --director {axi_cli.bare(director_id, '<director-id>')}",
        ])
        raise _exit(EXIT_ACCEPTED_NOT_WAITED)

    _watch(director_id)


def smart_restart_status(director: Optional[str], machine: Optional[str], json_output: bool) -> None:
    """Where the smart restart on one Director stands, asked once. It changes nothing."""
    director_id = resolve_director(director, machine)
    payload = _read_progress(director_id)
    if json_output:
        print(json.dumps(payload, indent=2))
        return
    _print_progress(payload, header=True)
    axi_cli.print_next([
        f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')}",
        "cc-devthrottle director list",
    ])


def _read_progress(director_id: str) -> Dict[str, Any]:
    """One reading of the run. An answer that is not an object is refused, never read as "nothing"."""
    try:
        payload = gateway.get_json(_director_path(director_id, "smart-restart"))
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not read where the smart restart of Director {director_id} stands: {err}",
            ["cc-devthrottle director list"],
        )
    if not isinstance(payload, dict):
        axi_cli.fail(
            f"the Gateway's answer for Director {director_id} was not an object, so where the smart "
            "restart stands is unknown.",
            ["cc-devthrottle director list", axi_cli.CHECK_GATEWAY],
        )
    return payload


def _rows(payload: Dict[str, Any]) -> List[Dict[str, Any]]:
    rows = payload.get("sessions")
    return [r for r in rows if isinstance(r, dict)] if isinstance(rows, list) else []


def _row_states(payload: Dict[str, Any]) -> Dict[str, Tuple[str, str, str]]:
    """Each session's (name, state label, detail) by id - what a change is measured against."""
    return {
        str(gateway.field(r, "sessionId", "SessionId")): (
            str(gateway.field(r, "name", "Name")),
            str(gateway.field(r, "stateLabel", "StateLabel")),
            str(gateway.field(r, "detail", "Detail")),
        )
        for r in _rows(payload)
    }


def _print_progress(payload: Dict[str, Any], header: bool) -> None:
    """One whole reading, printed: the phase, the count, then one line per session."""
    if not payload.get("started"):
        console.print(axi_cli.shown(gateway.field(payload, "detail", "Detail")))
        return
    if header:
        console.print(f"{axi_cli.shown(gateway.field(payload, 'phaseLabel', 'PhaseLabel'))}")
        console.print(f"  {axi_cli.shown(gateway.field(payload, 'countLabel', 'CountLabel'))}")
    for row in _rows(payload):
        _print_row(row)


def _print_row(row: Dict[str, Any]) -> None:
    name = gateway.field(row, "name", "Name")
    label = gateway.field(row, "stateLabel", "StateLabel")
    detail = gateway.field(row, "detail", "Detail")
    console.print(f"  {axi_cli.shown(name)}: {axi_cli.shown(label)}")
    if detail:
        console.print(f"    {axi_cli.shown(detail)}")


def _watch(director_id: str) -> None:
    """Ask where the run stands until it ends, printing every change as it happens.

    WHAT A SILENCE MEANS IS NOT GUESSED. When the launcher takes the restart it stops this very
    Director, so the door being shut is the expected end of a run that worked - and it is
    indistinguishable, from here, from a Director that died. So the silence is reported as exactly
    what was seen, with the last phase that WAS seen, and the command that answers the rest.
    """
    phase = ""
    seen: Dict[str, Tuple[str, str, str]] = {}
    started = _monotonic()
    while True:
        try:
            payload = gateway.get_json(_director_path(director_id, "smart-restart"))
        except gateway.GatewayError as err:
            _stopped_answering(director_id, phase, err)
        if not isinstance(payload, dict):
            axi_cli.fail(
                f"the Gateway's answer for Director {director_id} was not an object, so where the "
                "smart restart stands is unknown.",
                ["cc-devthrottle director list", axi_cli.CHECK_GATEWAY],
            )

        new_phase = str(gateway.field(payload, "phaseLabel", "PhaseLabel"))
        if new_phase and new_phase != phase:
            phase = new_phase
            console.print(f"[cyan]{axi_cli.shown(phase)}[/] - {axi_cli.shown(gateway.field(payload, 'countLabel', 'CountLabel'))}")

        now = _row_states(payload)
        for sid, state in now.items():
            if seen.get(sid) != state:
                _print_row({"sessionId": sid, "name": state[0], "stateLabel": state[1], "detail": state[2]})
        seen = now

        if not payload.get("running"):
            _ended(director_id, payload)

        if _monotonic() - started > WATCH_PATIENCE_SECONDS:
            axi_cli.write_lines(
                f"{machine_ops.ACCEPTED_NOT_WAITED}: the run on Director {director_id} has outlived "
                f"{WATCH_PATIENCE_SECONDS // 60} minutes of watching, which is longer than the longest "
                "time a smart shutdown may be given. It is still going; this command stopped watching."
            )
            axi_cli.print_next([
                f"cc-devthrottle director smart-restart-status --director {axi_cli.bare(director_id, '<director-id>')}",
            ])
            raise _exit(EXIT_ACCEPTED_NOT_WAITED)

        _sleep(POLL_SECONDS)


def _ended(director_id: str, payload: Dict[str, Any]) -> None:
    """How it ended, in the engine's own words, and the exit code that matches."""
    outcome = str(gateway.field(payload, "outcome", "Outcome"))
    detail = str(gateway.field(payload, "detail", "Detail"))
    workspace = str(gateway.field(payload, "workspaceId", "WorkspaceId"))
    console.print("")
    if outcome == RESTART_ACCEPTED:
        console.print(f"[green]{axi_cli.shown(outcome)}[/] Director {axi_cli.shown(director_id)}")
        console.print(f"  {axi_cli.shown(detail)}")
        if workspace:
            console.print(f"  Record: workspace {axi_cli.shown(workspace)}")
        axi_cli.print_next([
            "cc-devthrottle director list",
            f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')}",
        ])
        raise _exit(0)

    # Everything else is a restart that did not happen. The engine's sentence says which and why, and
    # it is not re-worded here; a record that stands is named so it can be brought back.
    lines = [f"{outcome or 'UNKNOWN'} Director {director_id}: {detail}"]
    if workspace:
        lines.append(f"Record: workspace {workspace}")
    axi_cli.fail(
        " ".join(lines),
        [
            f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')}",
            "cc-devthrottle director list",
        ],
    )


def _stopped_answering(director_id: str, phase: str, err: gateway.GatewayError) -> None:
    where = f" It was last seen: {phase}." if phase else ""
    axi_cli.write_lines(
        f"{machine_ops.ACCEPTED_NOT_WAITED}: Director {director_id} stopped answering while the smart "
        f"restart was running.{where} That is also what a restart looks like from here - the launcher "
        "stops this very Director - so whether it restarted is not known from this command. "
        f"The Gateway said: {err}"
    )
    axi_cli.print_next([
        "cc-devthrottle director list",
        f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')}",
    ])
    raise _exit(EXIT_ACCEPTED_NOT_WAITED)


# --- the history --------------------------------------------------------------------------------


def restart_history(director: Optional[str], machine: Optional[str], count: int, json_output: bool) -> None:
    """Every restart record this Director wrote, newest first, with what came back and what did not."""
    if count < 1:
        axi_cli.usage_error(
            f"--count must be at least 1, not {count}. Pass a whole number, for example --count 10."
        )
    director_id = resolve_director(director, machine)
    try:
        payload = gateway.get_json(_director_path(director_id, "restart-history"))
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not read the restart history of Director {director_id}: {err}",
            ["cc-devthrottle director list"],
        )
    if not isinstance(payload, dict):
        axi_cli.fail(
            f"the Gateway's answer for Director {director_id} was not an object, so its restart "
            "history is unknown. An empty history is never reported as one that could not be read.",
            ["cc-devthrottle director list", axi_cli.CHECK_GATEWAY],
        )

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    # A history that could not be read is a FAILURE, never an empty list. The two look identical on a
    # screen and call for opposite next steps.
    if payload.get("refused"):
        axi_cli.fail(
            str(gateway.field(payload, "message", "Message")),
            ["cc-devthrottle director list", axi_cli.CHECK_GATEWAY],
        )

    entries = payload.get("entries")
    entries = [e for e in entries if isinstance(e, dict)] if isinstance(entries, list) else []
    shown = entries[:count]
    axi_cli.write_lines(axi_output.format_count(len(shown), total=len(entries)))
    console.print(axi_cli.shown(gateway.field(payload, "message", "Message")))
    for entry in shown:
        console.print("")
        _print_entry(entry)
    axi_cli.print_next([
        f"cc-devthrottle director restart-history --director {axi_cli.bare(director_id, '<director-id>')} --json",
        "cc-devthrottle director list",
    ])


def _print_entry(entry: Dict[str, Any]) -> None:
    workspace = gateway.field(entry, "workspaceId", "WorkspaceId")
    console.print(
        f"{axi_cli.shown(gateway.field(entry, 'whenLabel', 'WhenLabel'))} - workspace {axi_cli.shown(workspace)}"
    )
    for key in ("kindLabel", "reasonLabel", "outcomeLabel", "seatsOwedLabel"):
        value = gateway.field(entry, key)
        if value:
            console.print(f"  {axi_cli.shown(value)}")
    seats = entry.get("seats")
    for seat in seats if isinstance(seats, list) else []:
        if not isinstance(seat, dict):
            continue
        console.print(
            f"    {axi_cli.shown(gateway.field(seat, 'name', 'Name'))}: "
            f"{axi_cli.shown(gateway.field(seat, 'outcome', 'Outcome'))}"
        )


def _monotonic() -> float:
    """The clock the watch measures its own patience against. A seam, so a test can run an hour of
    watching in no time at all rather than really waiting one."""
    return time.monotonic()


def _sleep(seconds: float) -> None:
    """The wait between two readings. A seam, for the same reason as the clock above."""
    time.sleep(seconds)


def _exit(code: int) -> typer.Exit:
    """The tool's own exit, built here and RAISED by the caller so no line can run past it."""
    return typer.Exit(code)

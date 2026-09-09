"""Fleet/session operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich import box
from rich.console import Console
from rich.markup import escape
from rich.table import Table


# --- ASCII-only output (project house rule): Rich truncates an overflowing table cell with the
# Unicode ellipsis U+2026; emit ASCII "..." instead. Patched once at import. cc-devthrottle's cli
# imports this module eagerly, so the global patch is in place before any table renders. ---
def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return
    def _truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    _truncate._ascii_ellipsis = True
    rich.text.Text.truncate = _truncate


_install_ascii_truncation()


# --- ASCII-only output: Typer renders its --help and error panels with Rich's default ROUNDED
# (Unicode) box; force them to the ASCII box so panels stay ASCII even when the console is UTF-8.
# Guarded so a non-Typer environment is a harmless no-op. ---
def _install_ascii_typer_panels():
    try:
        import typer.rich_utils as _tru
        from rich import box as _rbox
    except Exception:
        return
    if getattr(_tru.Panel, "_ascii_box", False):
        return
    _OrigPanel = _tru.Panel

    class _AsciiPanel(_OrigPanel):
        _ascii_box = True

        def __init__(self, *args, **kwargs):
            kwargs.setdefault("box", _rbox.ASCII)
            super().__init__(*args, **kwargs)

    _tru.Panel = _AsciiPanel


_install_ascii_typer_panels()

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()
SELFTEST_MARKER = "FLEETPONG"


def _repo_name(repo: str) -> str:
    return repo.replace("\\", "/").rstrip("/").split("/")[-1] if repo else "-"


def _model_text(s: Dict[str, Any]) -> str:
    """The MODEL column (issue devthrottle_internal#1340): which model this session is actually running.

    It exists because an agent driving the fleet had to parse the JSON to learn the one fact that
    drives both cost and quality, while a human reading the same table could not learn it at all.

    RENDER, NEVER RULE. The Gateway folds this - the full recorded id when there is one, and the words
    for WHICH of the two absences applies when there is not ("no model yet" for a session that has not
    finished a turn, "model not reported" for an agent that can never report one). This prints the id
    in full rather than the fold's shortened badge text, because a table has the width a rail does not
    and a truncated id is not a name anything else will match.

    A row with no folded verdict at all is a Gateway too old to have stamped one. Then, and only then,
    the raw recorded model stands in - and when there is no model either, the cell reads "(unknown)":
    deliberately NOT one of the fold's two sentences, because this is a third case. We were told
    nothing, which is not the same as being told there is no model YET or that there never will be.
    An empty cell would have quietly claimed the second of those.
    """
    display = s.get("modelDisplay", s.get("ModelDisplay"))
    if isinstance(display, dict):
        model_id = (display.get("modelId") or display.get("ModelId") or "").strip()
        if model_id:
            return model_id
        text = (display.get("text") or display.get("Text") or "").strip()
        if text:
            return text
    raw = (gateway.field(s, "currentModel", "CurrentModel") or "").strip()
    return raw if raw else "(unknown)"


def _get_fleet() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str], Optional[str]]:
    """The fleet roster and BOTH folded cautions, with this tool's error posture (issue #1051).

    The fetch itself is shared (gateway.get_fleet) so the four tools that resolve a target against
    this roster cannot drift; only the "print and exit" behaviour is local. The fourth value is the
    negative-answer caution and is printed ONLY where this tool's own answer came back empty.
    """
    try:
        return gateway.get_fleet()
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)


def _roster_caveat(complete: Optional[bool], reason: Optional[str]) -> str:
    return gateway.roster_caveat(complete, reason)


def _resolve_target(target: str, *, command_name: str) -> Dict[str, Any]:
    sessions, complete, reason, stale_caution = _get_fleet()
    # Issue #821: the shared resolver now understands the three-digit session number (#820) as a
    # first-class target, preferring it over id-prefix / name matching, so message send / ask and
    # session rename all address a session by its number through this one call.
    matches = gateway.resolve_target(sessions, target)
    if not matches:
        # Issue #1051: this is where a dropped Director does its real damage. "No session matches"
        # reads as "that session does not exist", and for a session on a machine the Gateway could
        # not reach that is simply false - the roster we searched never contained it. Say which we
        # mean, because the two call for opposite next steps: give up, or go and look at machine B.
        console.print(
            f"[red]No session matches '{target}'.[/red] "
            "Run cc-devthrottle session list to see the fleet."
        )
        caveat = _roster_caveat(complete, reason)
        if caveat:
            console.print(f"[yellow]The fleet list searched may be incomplete.[/yellow] {caveat}")
        # THE negative answer the second caution exists for. A machine whose tunnel is up but whose
        # pushes are late can be hiding the very session being addressed, and every target-resolving
        # verb comes through here - message send, message ask, rename, done, hold, compact. Printed on
        # this path only, so it stays rare enough to be read.
        if stale_caution:
            console.print(f"[yellow]{stale_caution}[/yellow]")
        raise typer.Exit(1)
    if len(matches) > 1:
        _refuse_ambiguous_target(target, matches, command_name=command_name)
    return matches[0]


def _refuse_ambiguous_target(
    target: str, matches: List[Dict[str, Any]], *, command_name: str
) -> None:
    """Print the several sessions a target matched, and exit - it is always an error, never a verdict.

    Lifted out of _resolve_target so that `session stop`, which deliberately does NOT go through that
    resolver (see _stop_target), still refuses an ambiguous target in exactly these words. Two
    wordings for one refusal is how the tool comes to answer the same question two ways.
    """
    console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches:[/yellow]")
    for s in matches:
        sid = gateway.field(s, "sessionId", "SessionId")
        name = gateway.field(s, "name", "Name") or "(unnamed)"
        machine = gateway.field(s, "machineName", "MachineName") or "-"
        console.print(f"  {gateway.short_id(sid)}  {name}  ({machine})")
    console.print(f"Re-run {command_name} with a longer id prefix.")
    raise typer.Exit(1)


def resolve_session(target: str, *, command_name: str) -> Dict[str, Any]:
    """Resolve one session from the fleet roster, or print why it could not and exit.

    The public door onto the shared resolver above, for sibling command modules (mission attach and
    detach) that address a session exactly the way the session verbs do - by number, id prefix, or
    name. It exists so those commands cannot grow a second, subtly different way to name a session:
    one resolver means one answer to "which session did you mean", including the caveats about a
    roster that may be incomplete.
    """
    return _resolve_target(target, command_name=command_name)


def fleet_or_exit() -> Tuple[List[Dict[str, Any]], Optional[bool], Optional[str], Optional[str]]:
    """The fleet roster, or a printed error and exit - the public door onto the shared fetch."""
    return _get_fleet()


def resolve_target_or_current(target: Optional[str]) -> str:
    """Return the requested session id, defaulting to this session."""
    if target is None or not target.strip():
        sid = gateway.session_id()
        if not sid:
            console.print(
                "[red]Error:[/red] no target was provided and CC_SESSION_ID is not set."
            )
            raise typer.Exit(1)
        return sid

    chosen = _resolve_target(target, command_name="cc-devthrottle session rename")
    return gateway.field(chosen, "sessionId", "SessionId")


def list_sessions(json_output: bool) -> None:
    """List every session running across the fleet."""
    sessions, complete, reason, stale_caution = _get_fleet()
    caveat = _roster_caveat(complete, reason)

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and
        # injects newlines into long values, producing invalid JSON for agents/pipes.
        print(json.dumps(sessions, indent=2))
        # Issue #1051: the caveat goes to STDERR, never stdout. The shape of this output is depended
        # on by agents and pipes, so it stays a bare array - but a caller acting on a partial roster
        # still has to be told, and stderr reaches a human without corrupting the parse.
        if caveat:
            print(f"WARNING: the fleet list may be incomplete. {caveat}", file=sys.stderr)
        # An EMPTY machine-readable roster is a negative answer too, and the agent parsing it is the
        # reader most likely to act on "nothing is running" as a fact. stderr, for the same reason the
        # caveat goes there: the array shape is depended on.
        if not sessions and stale_caution:
            print(f"WARNING: {stale_caution}", file=sys.stderr)
        return

    if not sessions:
        # Issue #1051, the worst sentence in the tool. "No sessions are running in the fleet" is a
        # claim about the WHOLE FLEET, and an empty roster with an unreachable Director does not
        # support it - the sessions may be running perfectly well on the machine we could not read.
        # Absent is not empty, and only one of the two is worth saying out loud.
        if caveat:
            console.print(f"[yellow]No sessions were returned, but this is not the whole fleet.[/yellow] {caveat}")
        elif stale_caution:
            # Connected, so the roster is COMPLETE and the offline caveat is silent - and the answer is
            # still empty while a machine's rows are known to be stale. That is precisely the case where
            # "no sessions are running in the fleet" is a claim the list cannot support.
            console.print("[yellow]No sessions were returned, but this is not the whole fleet.[/yellow]")
        else:
            console.print("No sessions are running in the fleet.")
        # Both cautions can be live at once - one Director offline, another connected but quiet - and on
        # an empty answer they say different things. Printed after, never instead of, the other.
        if stale_caution:
            console.print(f"[yellow]{stale_caution}[/yellow]")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NO.")
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("MACHINE")
    table.add_column("REPOSITORY")
    # Issue devthrottle_internal#1340. ONE new column, not two: an AGENT column beside it was tried and
    # cost more than it gave. Rich lays this table out at 80 columns when stdout is a pipe (which is how
    # every agent reads it), and the eighth column pushed STATUS far enough to elide "Exited (crashed)" -
    # trading a fact nobody could read anywhere for one already implied by the model id and available in
    # --json. The model is the fact that was invisible; it gets the width.
    table.add_column("MODEL")
    table.add_column("STATUS")

    me = gateway.session_id()
    for s in sessions:
        sid = gateway.field(s, "sessionId", "SessionId")
        number = gateway.field(s, "number", "Number")
        number_text = str(number) if number is not None else "-"
        name = gateway.field(s, "name", "Name") or "(unnamed)"
        machine = gateway.field(s, "machineName", "MachineName") or "-"
        repo = gateway.field(s, "repoPath", "RepoPath")
        status = gateway.field(s, "activityState", "ActivityState") or "-"
        # Issue #1019: a session the Director is still holding now appears here even after its process is
        # gone, which is what makes a dead row nameable and therefore reapable. A crash was never modelled
        # as its own activity state - it reads "Exited", byte-identical to a session that finished on
        # purpose - so say it, from the RAW crash fact the Director puts on the wire. We render that fact,
        # we do not rule on it: deciding what a state MEANS belongs to the Gateway fold, never to a client.
        # Read the boolean DIRECTLY, not through gateway.field: field stringifies, and the string
        # "False" is truthy, so every row would be reported as a crash. Absent is not true either - a
        # roster row carrying no crash fact is not a crash.
        if s.get("crashed", s.get("Crashed")) is True:
            status = f"{status} (crashed)"
        marker = " (you)" if me and sid.lower() == me.lower() else ""
        table.add_row(
            number_text,
            gateway.short_id(sid) + marker,
            name,
            machine,
            _repo_name(repo),
            _model_text(s),
            status,
        )

    console.print(table)
    # Issue #1051: printed AFTER the table, so the rows the reader can trust come first and the
    # qualification lands on what they have just read rather than being scrolled past above it.
    if caveat:
        console.print(f"[yellow]This is not the whole fleet.[/yellow] {caveat}")


def whoami() -> None:
    """Show this session's own fleet identity."""
    sid = gateway.session_id()
    if not sid:
        console.print(
            "[red]Error:[/red] CC_SESSION_ID is not set. "
            "cc-devthrottle session whoami only works inside a DevThrottle session."
        )
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    # Completeness is deliberately ignored here: whoami looks up THIS session, which lives on the
    # Director being asked, and a Director always reports its own sessions (issue #1019). An
    # unreachable Director elsewhere cannot hide the caller from itself.
    sessions, _, _, _ = _get_fleet()
    me = next(
        (s for s in sessions if gateway.field(s, "sessionId", "SessionId").lower() == sid.lower()),
        None,
    )
    if me is None:
        console.print(f"You are session {short} (id {sid}).")
    else:
        name = gateway.field(me, "name", "Name") or "(unnamed)"
        machine = gateway.field(me, "machineName", "MachineName") or "this machine"
        repo = gateway.field(me, "repoPath", "RepoPath")
        number = gateway.field(me, "number", "Number")
        number_text = f"number {number}, " if number is not None else ""
        console.print(f'You are session {number_text}{short} ("{name}") on {machine}, repo {_repo_name(repo)}.')

    console.print('To message another session:  cc-devthrottle message send <id> "<message>"')
    console.print('To message everyone:         cc-devthrottle message send all "<message>"')
    console.print("To see all sessions:         cc-devthrottle session list")


def rename_session(target: Optional[str], new_name: str) -> Dict[str, Any]:
    """Rename a target session, defaulting to the current session."""
    name = new_name.strip()
    if not name:
        console.print("[red]Error:[/red] the new session name cannot be blank.")
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        # The Gateway renames a session anywhere in the account and answers with the updated row.
        resp = gateway.patch_json(f"sessions/{sid}", {"name": name})
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Gateway did not return the renamed session.")
        raise typer.Exit(1)

    actual = gateway.field(resp, "name", "Name") or name
    actual_sid = gateway.field(resp, "sessionId", "SessionId") or sid
    console.print(f'[green]Renamed[/green] {gateway.short_id(actual_sid)} to "{actual}".')
    return resp


def prompt_session(target: str, text: str, no_submit: bool = False) -> Dict[str, Any]:
    """Send raw text into a session - what a human typing into it would produce.

    Unlike `message send`, this does NOT frame the text with a sender. Restores the old
    POST /sessions/{sid}/prompt.
    """
    if not text.strip():
        console.print("[red]Error:[/red] the prompt text cannot be blank.")
        raise typer.Exit(1)
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.post_json(
            f"sessions/{sid}/prompt", {"text": text, "appendEnter": not no_submit}
        )
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    console.print(f"[green]Sent[/green] prompt to {gateway.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def interrupt_session(target: Optional[str]) -> Dict[str, Any]:
    """Stop what a session is currently doing. Restores the old POST /sessions/{sid}/interrupt."""
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.post_json(f"sessions/{sid}/interrupt")
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    console.print(f"[green]Interrupted[/green] {gateway.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def hold_session(target: Optional[str], release: bool = False, minutes: Optional[int] = None) -> Dict[str, Any]:
    """Park a session, or release it. Restores the old POST /sessions/{sid}/hold.

    A hold asked for while the session is still working is DEFERRED: it applies when the turn
    settles, and the response's pending flag says so. A held session that starts working again
    always takes itself off hold.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {"onHold": not release}
    if minutes is not None:
        body["snoozeMinutes"] = minutes
    try:
        resp = gateway.post_json(f"sessions/{sid}/hold", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    if release:
        console.print(f"[green]Released[/green] {short} - no longer held.")
    elif isinstance(resp, dict) and gateway.field(resp, "pending", "Pending"):
        console.print(f"[green]Hold queued[/green] {short} is still working; it parks when it finishes.")
    else:
        for_text = f" for {minutes} minutes" if minutes else ""
        console.print(f"[green]Held[/green] {short}{for_text}.")
    return resp if isinstance(resp, dict) else {}


def raise_hand(reason: Optional[str], target: Optional[str] = None, clear: bool = False) -> Dict[str, Any]:
    """Put your hand up to the session that is driving you, or take it back down.

    A supervised session (a worker with a live supervisor, or a scheduled run) is quiet toward the
    owner BY CONSTRUCTION - it is parked on every screen the moment it stops working, and it has no
    channel to him. This is the channel it has instead: it tells the session that started it, in its
    own words, what decision it is blocked on. An ARCHITECT is NOT supervised (the owner's ruling,
    2026-09-06: "the architect is always the session i talk to"), so it reaches him directly.

    Your hand LOWERS ITSELF when you stop working. That is not a shortcut - stopping already tells
    your supervisor to look at you, so a flag that outlived the turn would just be noise nobody
    cleared. Raise it when you are mid-turn and cannot go on without an answer.
    """
    sid = resolve_target_or_current(target)
    if clear:
        body: Dict[str, Any] = {"raised": False}
    else:
        text = (reason or "").strip()
        if not text:
            console.print(
                "[red]Error:[/red] say what you need. A raised hand with no words is a 'notice me' "
                "ping - your supervisor would have to open you to find out what for, which is the "
                "work this is meant to save."
            )
            raise typer.Exit(1)
        body = {"raised": True, "reason": text}

    try:
        resp = gateway.post_json(f"sessions/{sid}/needs-manager", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    if clear:
        console.print(f"[green]Hand down[/green] {short}.")
    else:
        console.print(
            f"[green]Hand up[/green] {short}. Your supervisor sees it on the roster while you keep "
            "working; it lowers itself when your turn ends."
        )
    return resp if isinstance(resp, dict) else {}


def list_my_workers(target: Optional[str] = None) -> None:
    """Show the sessions THIS session is driving, and which of them have their hand up.

    The manager's half of the supervised rule. Workers never reach the owner, so a manager is the
    only one who can see a blocked one - and the design says a manager learns by READING its workers,
    not by being messaged 'notice me'. This is that read, in one line instead of one session at a time.
    """
    me = resolve_target_or_current(target)
    try:
        rows = gateway.get_json("sessions")
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sessions = rows.get("sessions") if isinstance(rows, dict) else rows
    if not isinstance(sessions, list):
        console.print("[red]Error:[/red] the Gateway did not return a session list.")
        raise typer.Exit(1)

    mine = [
        x for x in sessions
        if str(gateway.field(x, "controllerSessionId", "ControllerSessionId") or "").lower() == me.lower()
    ]
    if not mine:
        console.print("You are not driving any sessions.")
        return

    table = Table(title=f"Sessions driven by {gateway.short_id(me)}")
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("STATE")
    table.add_column("HAND UP - WHAT THEY NEED")
    for x in mine:
        sid = str(gateway.field(x, "sessionId", "SessionId") or "")
        raised = bool(gateway.field(x, "needsManager", "NeedsManager"))
        reason = str(gateway.field(x, "needsManagerReason", "NeedsManagerReason") or "")
        table.add_row(
            gateway.short_id(sid),
            str(gateway.field(x, "name", "Name") or ""),
            str(gateway.field(x, "stateLabel", "StateLabel") or ""),
            f"[yellow]{reason}[/yellow]" if raised else "",
        )
    console.print(table)
    # A session that has STOPPED has its hand lowered by the fold, so an empty column does not mean
    # "nothing needs you" - it means nothing needs you MID-TURN. Say so rather than let the table imply it.
    console.print(
        "[dim]A hand lowers itself when its session stops working. A stopped worker has finished or is "
        "stuck - read it to find out which.[/dim]"
    )


def compact_session(target: Optional[str], continue_prompt: Optional[str]) -> Dict[str, Any]:
    """Compact a session's context and, unless asked not to, continue it. Issue #2150.

    A full session cannot read anything sent to it, so this is the only rescue that works from
    outside. The call BLOCKS until the tool reports the compaction finished - which is why the
    timeout here is generous - and the follow-up is sent at that moment, never on a guessed delay.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {}
    if continue_prompt:
        body["continuePrompt"] = continue_prompt
    try:
        # Outermost bound of three: this waits longer than the Gateway waits for the Director, which
        # waits longer than the Director waits for the tool. The innermost one fires first and says
        # what actually failed.
        resp = gateway.post_json(f"sessions/{sid}/compact-context", body, timeout=300)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    body = resp if isinstance(resp, dict) else {}
    detail = gateway.field(body, "detail", "Detail")
    # Read the flag as a BOOLEAN, not through gateway.field: that helper stringifies, and str(False) is
    # "False" - a truthy string. Routed through it, a compaction nobody watched would be announced as
    # "Compacted", which is the one thing this line must never say without evidence.
    observed = bool(body.get("compactionObserved", body.get("CompactionObserved", False)))
    label = "[green]Compacted[/green]" if observed else "[yellow]Compaction submitted[/yellow]"
    console.print(f"{label} {short}. {escape(str(detail or ''))}")
    return resp if isinstance(resp, dict) else {}


def read_session_buffer(target: Optional[str]) -> None:
    """Print what a session's terminal is showing. Restores the old GET /sessions/{sid}/buffer."""
    sid = resolve_target_or_current(target)
    try:
        resp = gateway.get_json(f"sessions/{sid}/buffer")
    except gateway.GatewayError as err:
        # escape(): the error text comes from the server, so it is no more ours to trust than the buffer
        # itself - it can quote a path or a fragment of the session's own output. Interpolated raw, a
        # token like [/tmp/x] raises the very MarkupError this verb was crashing on, from the branch whose
        # job is to REPORT a failure. The "Error:" label is ours, so it keeps its markup.
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)

    # The buffer verb returns the terminal text under one of a couple of shapes depending on the
    # path it came back through; print whichever carries the text rather than guessing one.
    text = None
    if isinstance(resp, dict):
        text = gateway.field(resp, "text", "Text") or gateway.field(resp, "buffer", "Buffer")
    elif isinstance(resp, str):
        text = resp
    if text is None:
        console.print("[red]Error:[/red] the Gateway did not return the session's buffer.")
        raise typer.Exit(1)
    # Plain print, not console.print, for the same reason as list_sessions above. This is raw terminal
    # text from another session, so it is arbitrary and nobody controls its shape: Rich reads a token
    # like [/tmp/x] as a closing tag and raises MarkupError - an uncaught traceback out of a read-only
    # verb - eats style-shaped tokens like [bold], and rewraps every line at 80 columns when stdout is
    # not a TTY, which is exactly how an agent or a pipe calls this.
    #
    # What this does and does not promise: the text is not INTERPRETED - not parsed as markup, not
    # rewrapped, not truncated, nothing added or removed in the middle. It is not a byte-for-byte
    # guarantee, and claiming one would be a lie the next reader would rely on: print appends a trailing
    # newline, the text layer translates newlines on the way out, and this module reconfigures stdout
    # with errors="replace" (line 73), so a character the console encoding cannot represent still
    # becomes a replacement character. Those three are the whole of the difference.
    print(text)


def set_session_role(target: Optional[str], role: Optional[str]) -> Dict[str, Any]:
    """Declare a session's explicit role, defaulting to the current session.

    Restores the set-role verb the tunnel-only cut removed with POST /sessions/{sid}/role, which
    left a running session stuck with the role it was born with. Architect cannot be derived from
    the spawn graph, so this is the only way to make one after birth. An empty role clears the
    explicit role and reverts the session to auto-derivation.
    """
    sid = resolve_target_or_current(target)
    wanted = (role or "").strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/role", {"role": wanted})
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Gateway did not return the session's role.")
        raise typer.Exit(1)

    actual_sid = gateway.field(resp, "sessionId", "SessionId") or sid
    explicit = gateway.field(resp, "explicitRole", "ExplicitRole")
    short = gateway.short_id(actual_sid)
    # Only the explicit role is reported: Worker/Manager derivation needs the fleet-wide spawn graph, which
    # lives in the Gateway, so the effective role is read from `session list`, not returned here.
    if explicit:
        console.print(f"[green]Role set[/green] {short} is now explicitly {explicit}.")
    else:
        console.print(f"[green]Role cleared[/green] {short} reverts to automatic role derivation.")
    return resp


def mark_done(target: Optional[str], reason: Optional[str]) -> Dict[str, Any]:
    """Flag a session for deletion, defaulting to the current session.

    The session is not killed synchronously - it is flagged, and the owning Director's
    deletion reaper removes it within about a minute, once a short grace has elapsed and the
    session is no longer working. This is how an unattended run tears ITSELF down when it has
    nothing left for the user, instead of lingering as a dead session in the fleet.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {}
    if reason and reason.strip():
        body["reason"] = reason.strip()
    try:
        resp = gateway.post_json(f"sessions/{sid}/request-deletion", body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    console.print(
        f"[green]Marked[/green] {gateway.short_id(sid)} for deletion; "
        "the Director will reap it shortly."
    )
    return resp if isinstance(resp, dict) else {}


def undo_done(target: Optional[str], reason: Optional[str] = None) -> Dict[str, Any]:
    """Take a pending deletion back off a session, defaulting to the current session.

    The opposite of mark_done, and the cure for having asked politely about the wrong session. Being
    able to stop a session outright is not a substitute: making the only remedy for a mistyped target
    the most destructive act the product has would have it exactly backwards.

    NO REASON IS ASKED FOR, DELIBERATELY. A stop carries a reason because it is destructive and is
    recorded; clearing a flag is the safe direction and there is nothing to justify. A reason passed
    here is therefore refused rather than dropped - see below for why refusing is the honest answer.
    """
    if reason is not None:
        # --undo with --reason is a contradiction, and both ways of resolving it quietly are worse
        # than refusing. Dropping the reason lets the caller walk away believing something was
        # recorded that never was. Recording it would attach a justification to the one session
        # operation that needs none, and would put a sentence in the trail for an act that is not an
        # intervention. So say the two do not go together, and let the caller choose.
        console.print(
            "[red]Nothing was changed:[/red] --undo and --reason cannot be used together. "
            "--reason is shown while a session winds down, and --undo is what cancels that "
            "wind-down, so there is nothing left for the reason to be shown on. Re-run "
            "cc-devthrottle session done --undo on its own, or drop --undo to flag the session."
        )
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        resp = gateway.delete(f"sessions/{sid}/request-deletion")
    except gateway.GatewayError as err:
        # escape(): the server's sentence can quote a path or a fragment of another session's output,
        # and a token shaped like [/tmp/x] raises MarkupError out of the branch whose only job is to
        # report a failure.
        console.print(f"[red]Error:[/red] {escape(str(err))}")
        raise typer.Exit(1)

    console.print(
        f"[green]Cleared[/green] {gateway.short_id(sid)} is no longer marked for deletion."
    )
    return resp if isinstance(resp, dict) else {}


#: The one thing `session stop` knows that the Gateway cannot: how to type its own flag. Everything
#: else an operator reads about a stop is written on the Gateway and printed here verbatim.
STOP_REASON_FLAG_HINT = (
    'Re-run with --reason "why you are stopping it" (-r says the same thing).'
)


def _stop_target(target: str) -> str:
    """The session id `session stop` addresses - and the raw target when nothing on the roster matches.

    THIS DELIBERATELY DOES NOT GO THROUGH _resolve_target, and that is the whole point of it.
    _resolve_target prints "No session matches" and exits 1 when the roster has nothing for a target.
    For a stop that is the exact defect Ruling 3 exists to prevent: an operator who stops a session
    twice would get an error the second time and read it as "it is still alive". The Gateway answers
    a target it cannot find with a 200 and the verdict notOnFleet, in words that say no machine was
    asked - and that careful answer never gets a chance to be read if the client exits first.

    So an unmatched target is sent on exactly as it was typed, and the GATEWAY rules on it. That is
    Ruling 5 applied to the one case where a client is most tempted to rule for itself.

    An AMBIGUOUS target is different and stays an error. "Which of these three did you mean" is a
    question about what the caller typed, not a verdict about any session's state, and guessing one
    of three sessions to end would be the worst possible way to be helpful.
    """
    wanted = target.strip()
    sessions, _complete, _reason, _stale = _get_fleet()
    matches = gateway.resolve_target(sessions, wanted)
    if len(matches) > 1:
        _refuse_ambiguous_target(wanted, matches, command_name="cc-devthrottle session stop")
    if matches:
        return gateway.field(matches[0], "sessionId", "SessionId")
    return wanted


def stop_session(target: str, reason: Optional[str], json_output: bool = False) -> Dict[str, Any]:
    """End a session now, and print what the Gateway says actually happened to it.

    THE CLIENT IS DUMB (Ruling 5). The Gateway folds the whole answer - the verdict, the one-line
    headline an operator reads, and any further lines the case needs - and this prints the headline
    and then each detail line, in the order they were given, and nothing else. It does not compose a
    sentence, it does not decide what a verdict means, and it does not re-word anything. A new state
    is then one edit on the Gateway rather than a new branch in three clients.

    EXIT ZERO FOR ALL THREE VERDICTS (Ruling 3) - stopped, alreadyStopped and notOnFleet. A stop
    never fails because there is nothing left to stop. The failure being designed out is a second run
    returning an error, which an operator reads as "it is still alive".

    Non-zero is for three things only, and each says which one it was: no reason was given (refused
    here, before the call); the Director could not be reached (the shared client writes that
    sentence, and it names the machine rather than the Gateway when the Gateway stamped the fault as
    a Director-side one); and the process would not die (the Gateway writes that sentence, and it is
    printed as written).
    """
    if reason is None or not reason.strip():
        # Refused before the round trip - there is no point asking the Gateway to tell us what we
        # already know. The Gateway refuses a missing reason too, in its own words, and that refusal
        # is what the failure branch below prints; this sentence is ours because no call was made to
        # answer it.
        console.print(
            "[red]Not stopped:[/red] a reason is required to stop a session, and none was given. "
            + STOP_REASON_FLAG_HINT
        )
        raise typer.Exit(1)

    sid = _stop_target(target)
    try:
        resp = gateway.post_json(f"sessions/{sid}/stop", {"reason": reason.strip()})
    except gateway.GatewayError as err:
        # The server's sentence survives INTACT. gateway._error_message has already lifted it out of
        # the body, so re-wording it here would throw away the one sentence written for this failure -
        # and for a Director-side fault it is the sentence that names the machine rather than the
        # Gateway. escape() for the reason it is used everywhere else in this module: it is text from
        # somewhere else, and a token shaped like [/tmp/x] raises MarkupError.
        text = str(err)
        console.print(f"[red]Not stopped:[/red] {escape(text)}")
        # The one thing only this command knows. Added AFTER the server's words, never instead of
        # them. The test is textual because the shared client raises one exception type and does not
        # carry the status code, so a 400 about the reason cannot be told apart from a 500 by any
        # other means here; a sentence that does not mention a reason simply gets no hint, which is
        # the harmless direction to be wrong in.
        if "reason" in text.lower():
            console.print(STOP_REASON_FLAG_HINT)
        raise typer.Exit(1)

    body = resp if isinstance(resp, dict) else {}
    headline = gateway.field(body, "headline", "Headline")
    if not headline:
        # A BROKEN INSTRUMENT, NOT A FOURTH VERDICT. Every answer this route gives carries a headline;
        # one that does not is a Gateway that did not understand the request, and printing nothing
        # while exiting 0 would be the "button that accepts a click and says nothing" this mission
        # exists to remove. Said as ignorance rather than as an outcome: we do not know whether it
        # stopped, and neither does anybody reading this.
        console.print(
            "[red]No answer:[/red] the Gateway returned nothing that says what happened to "
            f"{gateway.short_id(sid)}, so this cannot report whether it was stopped. "
            "Run cc-devthrottle session list to see whether it is still there."
        )
        raise typer.Exit(1)

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and injects
        # newlines into long values, producing invalid JSON. The same reason `session list --json` does it.
        # This is the shape an AGENT reads, and Ruling 4 makes an agent the ordinary caller of a stop - so
        # the alternative was every agent parsing sentences that re-wrap with the console width.
        # The WHOLE answer is printed, verbatim, exactly as the Gateway folded it: this client no more
        # edits the JSON than it edits the sentences.
        print(json.dumps(body, indent=2))
        return body

    console.print(escape(headline))
    details = body.get("details", body.get("Details"))
    if isinstance(details, list):
        # In the order the Gateway gave them. The order is part of the answer - the worktree line
        # before the reason line - and sorting or filtering here would be this client deciding what
        # matters, which is the one thing it must never do.
        for line in details:
            if isinstance(line, str) and line.strip():
                console.print(escape(line))
    return body


def _report_delivery(resp: Any, who: str) -> None:
    """Report a delivery from either of the Gateway's two answer shapes.

    A message to ONE session answers with the prompt result - accepted plus an error - and a broadcast
    answers with the fan-out: a per-recipient result row each, a refusal, or a note that there was
    nobody to send to. Both are read here rather than at the two call sites so the sentence the user
    reads cannot drift between "message send" and "message send all".

    The counting is the part worth being careful about. A fan-out row with no error was delivered; a
    row with one was not, and counting rows rather than successes would report a storm of failures as
    a successful broadcast. A refusal is an error even though it arrives with a 200 - the Hub answers
    scope refusals in the body, not the status code.
    """
    accepted = False
    count = 0
    err: Optional[str] = None
    warning: Optional[str] = None
    if isinstance(resp, dict):
        warning = resp.get("warning") or resp.get("Warning")
        results = resp.get("results", resp.get("Results"))
        if bool(resp.get("denied", resp.get("Denied", False))):
            err = resp.get("deniedReason") or resp.get("DeniedReason") or "the broadcast was refused"
        elif isinstance(results, list):
            count = sum(1 for r in results if isinstance(r, dict) and not (r.get("error") or r.get("Error")))
            failed = [r for r in results if isinstance(r, dict) and (r.get("error") or r.get("Error"))]
            accepted = True
            if failed and not warning:
                warning = (f"{len(failed)} of {len(results)} recipients did not receive it: "
                           + "; ".join(str(r.get("error") or r.get("Error")) for r in failed[:3]))
        else:
            accepted = bool(resp.get("accepted", resp.get("Accepted", False)))
            count = 1 if accepted else 0
            err = resp.get("error") or resp.get("Error")
    if accepted:
        console.print(f"[green]Delivered[/green] to {who} ({count} session(s)).")
        if warning:
            console.print(f"[yellow]Note:[/yellow] {warning}")
    else:
        console.print(f"[red]Not delivered:[/red] {err or 'unknown error'}")
        raise typer.Exit(1)


def send_message(
    target: str,
    message: str,
    everyone: bool = False,
    reason: str | None = None,
    grant: str | None = None,
) -> None:
    """Send a message to one session, or broadcast with target 'all'.

    A plain 'all' reaches only the sender's team (its Mission, or - solo - the same repository on the
    same machine). --everyone asks to reach the whole fleet, which the Gateway Hub gates on a human
    grant plus a reason (issue #1229)."""
    me = gateway.session_id()

    if target.strip().lower() == "all":
        # No sender field: the Gateway takes it from the session key that authenticated the call, so
        # the team it resolves and the message it frames are about the same session by construction.
        body = {"text": message}
        if everyone:
            body["everyone"] = True
            if reason:
                body["reason"] = reason
            if grant:
                body["grantId"] = grant
        try:
            resp = gateway.post_json("fleet/broadcast", body)
        except gateway.GatewayError as err:
            console.print(f"[red]Error:[/red] {err}")
            raise typer.Exit(1)
        _report_delivery(resp, "the whole fleet" if everyone else "your team")
        return

    chosen = _resolve_target(target, command_name="cc-devthrottle message send")
    target_sid = gateway.field(chosen, "sessionId", "SessionId")
    try:
        resp = gateway.post_json(f"sessions/{target_sid}/message", {"text": message})
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    name = gateway.field(chosen, "name", "Name") or gateway.short_id(target_sid)
    _report_delivery(resp, f'{name} ({gateway.short_id(target_sid)})')


def ask_session(target: str, question: str, timeout_ms: int) -> None:
    """Ask one session a question and print its answer."""
    if target.strip().lower() == "all":
        console.print(
            "[red]message ask targets a single session.[/red] "
            "Use cc-devthrottle message send all for a broadcast."
        )
        raise typer.Exit(1)

    me = gateway.session_id()
    chosen = _resolve_target(target, command_name="cc-devthrottle message ask")
    target_sid = gateway.field(chosen, "sessionId", "SessionId")

    http_timeout = max(30.0, timeout_ms / 1000.0 + 15.0)
    try:
        # An ask is a message that WAITS. waitForIdle also drops the reply hint from the frame: the
        # asker is already holding the line and reads the answer from the target's own output, so a
        # "reply with this command" line would make the recipient answer into a channel nobody reads.
        resp = gateway.post_json(
            f"sessions/{target_sid}/message",
            {"text": question, "waitForIdle": True, "timeoutMs": timeout_ms},
            timeout=http_timeout,
        )
    except gateway.GatewayError as err:
        console.print(f"[red]{err}[/red]")
        raise typer.Exit(1)

    answer = (gateway.field(resp, "output", "Output") if isinstance(resp, dict) else "").strip()
    name = gateway.field(chosen, "name", "Name") or gateway.short_id(target_sid)
    console.print(f"[dim]-- answer from {name} ({gateway.short_id(target_sid)}) --[/dim]")
    console.print(answer if answer else "(the target produced no output)")


def _controller_mission(controller_session_id: str) -> Optional[Dict[str, Any]]:
    """The controlling session's roster row, when it exists AND carries a mission; else None.

    Returns the whole row rather than the id so the caller can name the session the mission came
    from. Missing controller, or a controller attached to nothing, is a plain None - those are
    ordinary and there is nothing to report.

    A roster this process cannot READ is different, and is reported rather than swallowed: the
    spawn proceeds unattached (refusing to open a session because an optional grouping could not be
    looked up would be worse than the ungrouped session), but the human is told, in that order, so
    the missing mission is never a mystery. One 'mission attach' fixes it afterwards - which is the
    whole point of this issue existing.
    """
    try:
        sessions, _, _, _ = gateway.get_fleet()
    except gateway.GatewayError as err:
        console.print(
            "[yellow]Warning:[/yellow] could not read the fleet list to inherit the controlling "
            f"session's mission, so the new session starts attached to no mission: {err}"
        )
        return None

    wanted = controller_session_id.strip().lower()
    for s in sessions:
        if gateway.field(s, "sessionId", "SessionId").lower() != wanted:
            continue
        return s if gateway.field(s, "missionId", "MissionId") else None
    return None


def spawn_session(
    repo: str,
    agent: str,
    prompt: Optional[str],
    name: Optional[str],
    purpose: Optional[str],
    command: Optional[str],
    command_args: Optional[str],
    controlled_by: Optional[str] = None,
    args: Optional[str] = None,
    standalone: bool = False,
    role: Optional[str] = None,
    machine: Optional[str] = None,
    mission: Optional[str] = None,
    workflow_run: Optional[str] = None,
    # NOT named `director`: this module's Director-client is imported under that name and a parameter
    # would shadow it, breaking every gateway.post_json call in here.
    director_target: Optional[str] = None,
) -> None:
    """Open a new session here, on another computer (--machine), or on one named Director (--director)."""
    # Automatic roles: a SESSION-initiated spawn (CC_SESSION_ID present) DEFAULTS to a Worker controlled by
    # the spawner, so it stays quiet and reports to its manager instead of nagging the human. The opt-out
    # (guard 1) is --standalone / --controlled-by none: a deliberate human-facing PEER with no controller. A
    # human/desktop spawn (no CC_SESSION_ID) is unaffected. An explicit --controlled-by <id> or 'self' wins.
    # (The handover / move-session flow does NOT come through here - it uses POST /handover, which never sets
    # a controller - so a moved session keeps its red visible to the human, guard 2 by construction.)
    controller_session_id: Optional[str] = None
    cc_session = os.environ.get("CC_SESSION_ID")
    opt_out = standalone or (controlled_by is not None and controlled_by.strip().lower() == "none")
    if opt_out:
        controller_session_id = None
    elif controlled_by:
        if controlled_by.strip().lower() == "self":
            controller_session_id = cc_session
            if not controller_session_id:
                console.print(
                    "[red]Error:[/red] --controlled-by self requires CC_SESSION_ID to be set, but it "
                    "is not. Run this from inside a session, or pass an explicit controlling session id."
                )
                raise typer.Exit(1)
        else:
            controller_session_id = controlled_by
    elif cc_session:
        controller_session_id = cc_session
    # Issue #800: always name your session. On this fleet many sessions run in the same
    # checkout, so a session with neither a name nor a purpose still gets an auto-composed
    # name from the Director, but it reads better when you describe what it is FOR.
    if not name and not purpose:
        console.print(
            "[yellow]Warning:[/yellow] no --name or --purpose given; the session will get an "
            "auto-composed name. Pass --purpose \"<what it is for>\" so it is easy to tell apart."
        )

    body: Dict[str, Any] = {"repoPath": repo, "agent": agent}
    # Issue #1017: with no --args, the Director applies the SAME default agent settings (permission
    # mode preset, default model) the desktop New Session dialog uses, so a spawned session is
    # usable for unattended work without hand-fixing permissions. Passing --args overrides that
    # default with an explicit command line for this session only.
    if args is not None:
        body["args"] = args
    if name:
        body["name"] = name
    if purpose:
        body["purpose"] = purpose
    if prompt:
        body["prePrompt"] = prompt
    if command:
        body["command"] = command
    if command_args:
        body["commandArgs"] = command_args
    if controller_session_id:
        body["controllerSessionId"] = controller_session_id
    # Session origin and lineage (devthrottle_internal issue #982). This process is the only place
    # that can tell a session-initiated spawn from a human one: CC_SESSION_ID is injected into a
    # session's environment at birth and is absent from a human's own shell, so its presence IS the
    # answer. Stated here rather than inferred at the Director, which sees an identical HTTP request
    # either way and would have to guess.
    #
    # NOT the same as controllerSessionId above, and deliberately sent separately. That one asks for a
    # live supervision relationship and is dropped by --standalone; this one records who made the call
    # and survives it. A session spawning a deliberate human-facing peer is exactly the case where the
    # two must differ - it is still an agent starting a session, which is the thing being counted.
    if cc_session:
        body["origin"] = "agent"
        body["parentSessionId"] = cc_session
    else:
        body["origin"] = "human"
    body["originSurface"] = "cli"
    # Automatic session roles: forward an explicit --role VERBATIM to the Director, which validates it
    # against Standalone/Manager/Worker/Architect and rejects an unknown value (never a silent drop).
    if role:
        body["role"] = role
    # Mission attach at spawn: forward the Mission id ALONE. The GATEWAY resolves and validates it against
    # its own store - the only one that holds missions - and sends the Director the resolved name alongside
    # the id; an unknown mission is a 400 from the Gateway. This tool deliberately does not look a mission
    # up itself: a second copy of that rule here would be a second thing to get wrong (issue #2629, where
    # one of the two Gateway spawn routes forwarded the id without the name and the Director, asked to
    # resolve something it does not hold, called a live mission unknown).
    #
    # INHERITANCE (issue #2387). With no --mission, a session that has a CONTROLLER inherits that
    # controller's mission. Default ON, because the fleet already records the relationship and the case
    # that found this gap - a release push that grew from one seat to about a dozen in a day - would have
    # been grouped for free: every one of those sessions was spawned by a seat that already belonged to
    # the mission. Making it opt-in would mean the grouping only ever happens when somebody remembers,
    # which is the same failure as attach-at-birth in a different coat.
    #
    # Three things keep it honest. An explicit --mission always WINS (it is stated intent, not a
    # default). --mission none is the OPT-OUT, spelled the same way --controlled-by none is, for the
    # deliberate case of a child that is not part of its controller's work. And it is never SILENT: the
    # inheritance is printed, naming the mission and the session it came from, so a wrong inheritance is
    # visible immediately and one 'mission detach' away.
    inherited_from: Optional[Dict[str, Any]] = None
    mission_opt_out = mission is not None and mission.strip().lower() == "none"
    if mission and not mission_opt_out:
        body["missionId"] = mission
    elif not mission_opt_out and controller_session_id:
        inherited_from = _controller_mission(controller_session_id)
        if inherited_from:
            body["missionId"] = gateway.field(inherited_from, "missionId", "MissionId")
    # Workflow seat at spawn (Workflows phase 5b): forward the run id; the Gateway validates it and
    # stamps the workflow id + pinned version, and the seated session's preamble tells the agent to
    # fetch its conduct at exactly that version. A mission spawn auto-seats without this flag.
    if workflow_run:
        body["workflowRunId"] = workflow_run

    # "Start a session on some computer." Every spawn - including one on this very machine - goes to the
    # Gateway's POST /machines/{machine}/sessions, which picks a Director on that machine (auto-launching
    # one if none is running) and creates the session there. An off or unreachable machine fails loudly,
    # with NO local path to fall back to: that is the whole point of the Remove-the-network-port mission,
    # and a spawn that quietly landed somewhere else would be the second door in its worst form.
    #
    # THE MACHINE MUST NOW BE NAMED, where the Director floor could leave it blank and mean "here". With
    # the Director out of the path there is no "here" to infer, so an unqualified spawn resolves THIS
    # session's own machine from the roster - the Gateway's own view of where this session runs, not a
    # hostname read off the operating system, which is a different string on a different day.
    #
    # --director names ONE Director instead of "some Director on that computer", the only way to be
    # specific on a machine running several named instances. Passed through verbatim - resolving a
    # Director name here would mean this tool holding a second copy of a rule the Gateway already applies.
    target_machine = machine.strip() if machine else ""
    target_director = director_target.strip() if director_target else ""

    if target_director:
        # ONE named Director. The Gateway's machine route picks "some Director on that computer" and
        # has no way to be told which, so naming one has to be addressed to it BY ID - which is what
        # /directors/{id}/sessions is. Resolving the typed name against the Director list is the same
        # class of lookup `session list` already does for a session id or name; what a name MATCHES is
        # a client's job, what may be DONE with the result is the Gateway's.
        path = f"directors/{_resolve_director_id(target_director, target_machine)}/sessions"
    elif target_machine:
        # "Some Director on that computer", launching one if none is running.
        path = f"machines/{target_machine}/sessions"
    else:
        # HERE. This session's own Director, named from what the session was told at launch - no
        # roster lookup, no hostname read off the operating system, and no round trip to work out
        # something the session already knows.
        path = f"directors/{_my_director()}/sessions"

    try:
        resp = gateway.post_json(path, body)
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sid = gateway.field(resp, "sessionId", "SessionId")
    if not sid:
        console.print("[red]Error:[/red] the Gateway did not return a session id.")
        raise typer.Exit(1)

    short = gateway.short_id(sid)
    # The Director names the session at birth (issue #800), so the response carries the final name.
    label = gateway.field(resp, "name", "Name") or name or short
    console.print(f"[green]Opened[/green] session {short} ({label}).")
    console.print(f"id: {sid}")
    if inherited_from is not None:
        # Never silent. An inherited mission the caller did not ask for is only safe if they can see
        # it happened, so name the mission AND the session it came from, and say how to undo it.
        mission_label = (
            gateway.field(inherited_from, "missionName", "MissionName")
            or gateway.short_id(gateway.field(inherited_from, "missionId", "MissionId"))
        )
        controller_label = (
            gateway.field(inherited_from, "name", "Name")
            or gateway.short_id(gateway.field(inherited_from, "sessionId", "SessionId"))
        )
        console.print(
            f"Attached to mission [bold]{mission_label}[/bold], inherited from its controlling "
            f"session {controller_label}. Undo with: cc-devthrottle mission detach {short}"
        )
    console.print(
        f'Message it:  cc-devthrottle message send {short} "<message>"'
        f'   |   Ask it:  cc-devthrottle message ask {short} "<question>"'
    )


def _my_director() -> str:
    """The Director THIS session belongs to, from what the session was told at launch.

    This is how "here" is named once the loopback port is gone. Before the Remove-the-network-port
    mission it needed no name at all: the command line called its own Director directly, so "here" was
    wherever the call landed. Going through the Gateway makes it something that has to be said, and
    the session is told it at launch - so it costs nothing to ask and cannot disagree with itself.
    """
    import os
    director_id = (os.environ.get("CC_DIRECTOR_ID") or "").strip()
    if not director_id:
        raise gateway.GatewayError(
            "CC_DIRECTOR_ID is not set, so this process cannot say where 'here' is. "
            "Name the machine explicitly with --machine, or run this inside a DevThrottle session."
        )
    return director_id


def _resolve_director_id(name: str, machine: str) -> str:
    """Resolve a user-typed Director name to its id, optionally narrowed to one machine.

    Matched the way every other target in this tool is: an exact id wins outright, then an exact
    (case-insensitive) display name, then an id prefix. An ambiguous name is refused rather than
    guessed - picking one of two Directors called the same thing is how a session lands on the wrong
    computer and nobody notices until they go looking for it.
    """
    try:
        rows = gateway.get_json("directors") or []
    except gateway.GatewayError as err:
        raise gateway.GatewayError(f"Cannot list this account's Directors to resolve '{name}': {err}") from err

    wanted = name.strip().lower()
    if machine:
        rows = [d for d in rows
                if gateway.field(d, "machineName", "MachineName").lower() == machine.strip().lower()]

    exact = [d for d in rows if gateway.field(d, "directorId", "DirectorId").lower() == wanted]
    if not exact:
        exact = [d for d in rows if gateway.field(d, "displayName", "DisplayName").lower() == wanted]
    if not exact:
        exact = [d for d in rows if gateway.field(d, "directorId", "DirectorId").lower().startswith(wanted)]

    if not exact:
        where = f" on machine '{machine}'" if machine else ""
        raise gateway.GatewayError(
            f"No Director matches '{name}'{where}. Run cc-devthrottle machine directors to see them."
        )
    if len(exact) > 1:
        raise gateway.GatewayError(
            f"'{name}' matches {len(exact)} Directors. Name it more precisely, or add --machine."
        )
    return gateway.field(exact[0], "directorId", "DirectorId")



def _spawn_selftest(repo: str, command_args: str, name: str) -> str:
    resp = gateway.post_json(
        f"directors/{_my_director()}/sessions",
        {"repoPath": repo, "agent": "RawCli", "command": "cmd", "commandArgs": command_args},
    )
    sid = gateway.field(resp, "sessionId", "SessionId")
    if not sid:
        raise gateway.GatewayError("the Gateway did not return a session id when spawning.")
    try:
        gateway.patch_json(f"sessions/{sid}", {"name": name})
    except gateway.GatewayError:
        pass
    return sid


def _fleet_ids() -> List[str]:
    # Goes through the one shared fetch so the selftest reads the same roster every verb does. The
    # sessions it checks for are the ones it just spawned on THIS Director, which always reports its
    # own (issue #1019), so completeness cannot hide them - but reading a different route than the
    # rest of the tool is how a selftest ends up passing on a roster nobody else sees.
    sessions, _, _, _ = gateway.get_fleet()
    return [gateway.field(s, "sessionId", "SessionId") for s in sessions]


def selftest(timeout_ms: int) -> None:
    """Run the fleet messaging self-test against the local Director."""
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    responder: Optional[str] = None
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {step}{('  - ' + detail) if detail else ''}")

    try:
        responder = _spawn_selftest(repo, f"/k prompt {SELFTEST_MARKER}$G", "selftest-responder")
        recipient = _spawn_selftest(repo, "/k", "selftest-recipient")
        record(
            "spawn two sessions",
            True,
            f"responder={gateway.short_id(responder)} recipient={gateway.short_id(recipient)}",
        )
        time.sleep(2)

        ids = _fleet_ids()
        listed = responder in ids and recipient in ids
        record("session list includes both", listed)

        # The self-test's messages are sent AS THIS SESSION, not as the throwaway it spawned: the
        # Gateway takes the sender from the key that authenticated the call, and this process holds
        # its own session's key, not the throwaways'. What is under test is that a message reaches a
        # session and that an ask comes back with its answer, and both still are.
        send = gateway.post_json(
            f"sessions/{recipient}/message", {"text": "fleet self-test message"},
        )
        accepted = bool(isinstance(send, dict) and send.get("accepted", send.get("Accepted", False)))
        record("message send delivers", accepted, str(gateway.field(send, "error", "Error") or ""))

        ask = gateway.post_json(
            f"sessions/{responder}/message",
            {"text": "selftest ping", "waitForIdle": True, "timeoutMs": timeout_ms},
            timeout=timeout_ms / 1000.0 + 15.0,
        )
        answer = gateway.field(ask, "output", "Output") if isinstance(ask, dict) else ""
        got_marker = SELFTEST_MARKER in answer
        record(
            "message ask returns the answer",
            got_marker,
            "marker found" if got_marker else f"status={gateway.field(ask, 'waitStatus', 'WaitStatus')}",
        )

    except gateway.GatewayError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        for sid in (responder, recipient):
            if sid:
                try:
                    # request-deletion, not a hard DELETE: that is the verb an agent credential may
                    # call, and it is what `session done` uses. The reaper removes the session within
                    # about a minute, which is why the check below allows for a grace period.
                    gateway.post_json(f"sessions/{sid}/request-deletion", {})
                except gateway.GatewayError:
                    pass
        try:
            time.sleep(1)
            remaining = _fleet_ids()
            leaked = [s for s in (responder, recipient) if s and s in remaining]
            record("throwaway sessions cleaned up", not leaked, "" if not leaked else f"leaked {len(leaked)}")
        except gateway.GatewayError as err:
            record("throwaway sessions cleaned up", False, str(err))

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        raise typer.Exit(0)
    console.print(f"[red]FAIL[/red] - fleet messaging self-test: {passed}/{total} checks passed.")
    raise typer.Exit(1)

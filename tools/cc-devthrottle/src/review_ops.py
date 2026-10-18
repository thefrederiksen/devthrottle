"""`cc-devthrottle session review`: start a reviewer, and move to the next seat by itself when one cannot work.

THE REVIEWER IS A SEAT ORDER, NOT ONE AGENT (owner's ruling, 28 September 2026, Opus added 11 October): Codex,
else Claude Code on Fable, else Claude Code on Opus. Until this command the order lived only in a written rule,
and a rule is followed by someone noticing. On 17 October 2026 the Codex seat sat on its own update menu and then
on "model not supported" while the session that spawned it waited, because a seat that cannot work looks exactly
like a seat that is thinking. A usage limit looks the same: the session reads as plainly Waiting.

So this opens the first seat, reads its screen for the first minute, and decides from what is actually ON the
screen:

- A BLOCKER on screen - a usage or spend limit, a refused model, a signed-out agent, an empty credit balance - means
  the seat cannot work. It is stopped, with the reason, and the next seat is opened with the same prompt.
- The agent's own WORKING marker, "esc to interrupt", seen at least once and no blocker by the end of the minute,
  means the seat has started the review. That seat is the reviewer.
- Neither by the end of the minute means the seat never started - a swallowed first prompt, a question nobody
  answers, a seat that died at birth. It is stopped and the next one opened, the same as a blocker.

A seat is accepted only on that PRESENCE. "No blocker seen" alone would certify a seat that never did anything,
which is the failure this command exists to end.

The blocker texts are the ones the agents print, matched the same way the Gateway's fault classifier matches them
(TerminatingFaultClassifier, including the full stop that keeps Codex's notice apart from prose quoting it), plus
Claude Code's limit menu as UsageLimitDialog recognises it. A seat that only QUOTES one of them - its own first
prompt is on its screen - is protected by removing the prompt's text before the check.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass
from typing import Callable, List, Optional, Tuple

from cc_shared import gateway

from . import axi_cli
from . import session_ops


@dataclass(frozen=True)
class Seat:
    """One place a review can run: the agent and the command line it is opened with."""

    name: str
    agent: str
    args: Optional[str]


#: Every seat this command knows, by the name `--seats` takes.
SEATS = {
    "codex": Seat("codex", "Codex", None),
    "fable": Seat("fable", "ClaudeCode", "--model claude-fable-5-1 --dangerously-skip-permissions"),
    "opus": Seat("opus", "ClaudeCode", "--model claude-opus-5-5 --dangerously-skip-permissions"),
}

#: The order the owner ruled. `--seats` changes it for one call.
DEFAULT_SEATS = "codex,fable,opus"

#: The marker both Codex and Claude Code draw while a turn is running (DoorbellSafety.WorkingMarker).
WORKING_MARKER = "esc to interrupt"

#: What a seat that cannot work prints, and the words this command says about it. Lower case, matched against the
#: screen with its whitespace collapsed. Only BLOCKS: a warning such as "you've used 94% of your weekly limit" is
#: printed by seats that are still working and must never be here.
BLOCKERS: Tuple[Tuple[str, str], ...] = (
    ("hit your usage limit.", "hit its usage limit"),
    ("hit your weekly limit", "hit its weekly limit"),
    ("hit your free usage limit", "hit its free usage limit"),
    ("is not supported when using codex", "refused the model it was given"),
    ("credit balance is too low", "has no credit left"),
    ("out of credits", "has no credit left"),
    ("please run /login", "is signed out"),
    ("oauth token has expired", "is signed out"),
)

#: Claude Code's usage and spend limit menu, by its first option, squashed to letters and digits because the real
#: screens draw it with its spaces dropped (UsageLimitDialog.FirstOption).
LIMIT_MENU_SQUASHED = "1stopandwaitforlimittoreset"
LIMIT_MENU_REASON = "is showing its usage or spend limit menu"

#: How often the screen is read while a seat is watched.
POLL_SECONDS = 3.0


def _collapsed(text: str) -> str:
    return " ".join(text.lower().split())


def _squashed(text: str) -> str:
    return re.sub(r"[^a-z0-9]", "", text.lower())


def find_blocker(screen: str, prompt: str) -> Optional[str]:
    """What stops this seat from working, in words, or None when its screen shows no blocker.

    The seat's own first prompt is on its screen, so its text is taken out first: a review prompt that quotes a
    usage-limit notice must not read as a seat that hit one.
    """
    collapsed = _collapsed(screen)
    quoted = _collapsed(prompt)
    if quoted:
        collapsed = collapsed.replace(quoted, " ")
    for signature, reason in BLOCKERS:
        if signature in collapsed:
            return reason
    squashed = _squashed(screen)
    squashed_prompt = _squashed(prompt)
    if squashed_prompt:
        squashed = squashed.replace(squashed_prompt, "")
    if LIMIT_MENU_SQUASHED in squashed:
        return LIMIT_MENU_REASON
    return None


def parse_seats(value: str) -> List[Seat]:
    """The seats named in `--seats`, in order. An unknown or repeated name is a usage error, never skipped."""
    names = [n.strip().lower() for n in value.split(",") if n.strip()]
    if not names:
        axi_cli.usage_error(f"--seats names no seat. Name one or more of: {', '.join(SEATS)}.")
    unknown = [n for n in names if n not in SEATS]
    if unknown:
        axi_cli.usage_error(
            f"--seats names {', '.join(unknown)}, which is not a reviewer seat. The seats are: {', '.join(SEATS)}."
        )
    if len(set(names)) != len(names):
        axi_cli.usage_error("--seats names a seat twice. Name each seat once, in the order to try them.")
    return [SEATS[n] for n in names]


def _screen(seat: Seat, sid: str) -> str:
    try:
        resp = gateway.get_json(f"sessions/{gateway.path_segment(sid)}/buffer")
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not read the screen of the {seat.name} seat, session {sid}, so whether it can review is "
            f"unknown. It is still open and no other seat was started: {err}",
            [f"cc-devthrottle session buffer {sid}", f'cc-devthrottle session stop {sid} --reason "<why>"'],
        )
    if isinstance(resp, dict):
        for key in ("text", "Text", "buffer", "Buffer"):
            if isinstance(resp.get(key), str):
                return resp[key]
    if isinstance(resp, str):
        return resp
    axi_cli.fail(
        f"the Gateway's answer for the {seat.name} seat, session {sid}, carried no screen text, so whether it can "
        "review is unknown. It is still open and no other seat was started.",
        [f"cc-devthrottle session buffer {sid}", f'cc-devthrottle session stop {sid} --reason "<why>"'],
    )


def watch_seat(
    seat: Seat,
    sid: str,
    prompt: str,
    watch_seconds: float,
    clock: Optional[Callable[[], float]] = None,
    sleep: Optional[Callable[[float], None]] = None,
) -> Optional[str]:
    """Read the seat's screen until the watch ends. None means it is reviewing; otherwise why it cannot.

    A blocker ends the watch at once. Otherwise the whole watch runs, because a limit notice is printed AFTER the
    agent starts its turn: a seat that showed its working marker in the first seconds can still be refused.
    """
    clock = clock or time.monotonic
    sleep = sleep or time.sleep
    deadline = clock() + watch_seconds
    worked = False
    while True:
        screen = _screen(seat, sid)
        blocker = find_blocker(screen, prompt)
        if blocker:
            return blocker
        if WORKING_MARKER in _collapsed(screen):
            worked = True
        if clock() >= deadline:
            return None if worked else f"showed no sign of working in {int(watch_seconds)} seconds"
        sleep(POLL_SECONDS)


def _stop(seat: Seat, sid: str, why: str) -> None:
    reason = f"Reviewer seat {seat.name} {why}; moving the review to the next seat."
    try:
        resp = gateway.post_json(f"sessions/{gateway.path_segment(sid)}/stop", {"reason": reason})
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"the {seat.name} seat {why}, but stopping it failed, so no other seat was started (two reviewers "
            f"could otherwise run on one change). Session {sid}: {err}",
            ["cc-devthrottle session list", f'cc-devthrottle session stop {sid} --reason "{reason}"'],
        )
    headline = gateway.field(resp, "headline", "Headline") if isinstance(resp, dict) else ""
    if not headline:
        # Every answer the stop route gives carries a headline; one without it says nothing about whether the
        # seat stopped, and starting the next seat on that would risk two reviewers on one change.
        axi_cli.fail(
            f"the {seat.name} seat {why}, but the Gateway's answer to stopping it, session {sid}, says nothing "
            "about whether it stopped, so no other seat was started.",
            ["cc-devthrottle session list", f'cc-devthrottle session stop {sid} --reason "{reason}"'],
        )
    axi_cli.write_lines(f"The {seat.name} seat {why}. {headline}")


def review(repo: str, prompt: str, name: Optional[str], seats: str, watch_seconds: int) -> str:
    """Open the first reviewer seat that can work, and answer its session id."""
    if not prompt.strip():
        axi_cli.usage_error("--prompt is required: a reviewer with nothing to review would only wait.")
    if watch_seconds < 10:
        axi_cli.usage_error("--watch-seconds must be at least 10: a seat needs that long to show it started.")
    order = parse_seats(seats)
    skipped: List[str] = []
    for seat in order:
        sid = session_ops.spawn_session(
            repo, seat.agent, prompt, name, None, None, None,
            controlled_by="self", args=seat.args, next_steps=False,
        )
        why = watch_seat(seat, sid, prompt, watch_seconds)
        if why is None:
            said = f"{'; '.join(skipped)}, so the " if skipped else "The "
            axi_cli.write_lines(f"{said}{seat.name} seat is reviewing, in session {sid}.")
            axi_cli.print_next([
                f"cc-devthrottle session buffer {sid}",
                f'cc-devthrottle message send {sid} "<message>"',
            ])
            return sid
        _stop(seat, sid, why)
        skipped.append(f"the {seat.name} seat {why}")
    axi_cli.fail(
        f"no reviewer seat could start: {'; '.join(skipped)}.",
        ["cc-devthrottle session list", f"cc-devthrottle session review {repo} --prompt \"<what to review>\" --seats <seats>"],
    )

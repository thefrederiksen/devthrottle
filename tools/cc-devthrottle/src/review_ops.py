"""`cc-devthrottle session review`: start a reviewer, and move to the next seat by itself when one cannot work.

THE REVIEWER IS A SEAT ORDER, NOT ONE AGENT (owner's ruling, 28 September 2026, Opus added 11 October): Codex,
else Claude Code on Fable, else Claude Code on Opus. Until this command the order lived only in a written rule,
and a rule is followed by someone noticing. On 17 October 2026 the Codex seat sat on its own update menu and then
on "model not supported" while the session that spawned it waited, because a seat that cannot work looks exactly
like a seat that is thinking. A usage limit looks the same: the session reads as plainly Waiting.

So this opens the first seat and watches it in two steps.

1. THE PROMPT ARRIVES. The Director types the first prompt on its own, after the agent proves it reads input, and
   that can take minutes on a loaded machine. So the clock for step 2 does not start at the spawn answer: it starts
   when the prompt is seen in the seat's output, or the seat is seen working. A seat that shows neither within
   DELIVERY_SECONDS never got its prompt. A blocker during this step ends it at once - Codex's own update menu is
   one, and it is exactly what swallows a first prompt.
2. THE SEAT WORKS. For --watch-seconds after delivery:
   - a BLOCKER at the bottom of the screen while the seat is NOT working - a usage or spend limit, a refused model, a
     signed-out agent, an empty credit balance - means the seat cannot work. It is stopped, with the reason, and the
     next opened;
   - the seat seen WORKING at least once and no blocker by the end means it is reviewing;
   - neither by the end means the seat stopped without starting. It is replaced, the same as a blocker.

WHETHER A SEAT IS WORKING IS THE GATEWAY'S RULING, NOT THE SCREEN'S. The session's activityState is read from
GET /sessions/{id}, the same fold every surface shows. The screen text cannot answer it: the buffer route keeps every
byte the session printed, so the footer an agent drew while working - "esc to interrupt" - is still in its last lines
long after the turn ended. A reviewer seen with that footer twelve minutes after it finished, on 18 October 2026,
reported WaitingForInput.

A seat is accepted only on that PRESENCE. "No blocker seen" alone would certify a seat that never did anything,
which is the failure this command exists to end.

WHERE A BLOCKER MAY BE, which is what keeps a healthy reviewer from being stopped. The buffer route answers every
byte the session printed since it started, and a reviewer reading a file that quotes a limit notice - this one, for
a start - prints the notice too. So a blocker counts only in the last TAIL_LINES lines, the part of the screen where
an agent leaves a notice when it stops, and only while those lines show no working marker: an agent that hit a
limit has stopped working. That is the shape the Gateway's TerminatingFaultClassifier uses (the last content lines
of a stopped turn), and its texts are the ones matched here, including the full stop that keeps Codex's notice
apart from prose quoting it. Claude Code's limit menu is matched as UsageLimitDialog matches it: its numbered first
option above the cancel hint.

THE SEAT'S OWN PROMPT IS ON ITS SCREEN, and a prompt may quote a notice. Any match whose surroundings are text of
the prompt does not count.

THE SCREEN IS MATCHED WITH ITS SPACES REMOVED. The real buffer draws Codex's update menu as
"2.Skip3.Skipuntilnextversion" and a Claude Code footer as "esctointerrupt": the cleaned terminal output drops
spaces wherever the agent positioned the cursor instead of printing one. Every text is compared lower case with all
whitespace removed, so the two spellings are one.
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

#: The Gateway's activityState for a seat whose agent is running a turn.
WORKING_STATE = "Working"

#: What a seat that cannot work prints, and the words this command says about it. Only BLOCKS: a warning such as
#: "you've used 94% of your weekly limit" is printed by seats that are still working and must never be here.
BLOCKERS: Tuple[Tuple[str, str], ...] = (
    ("hit your usage limit.", "hit its usage limit"),
    ("hit your weekly limit", "hit its weekly limit"),
    ("hit your free usage limit", "hit its free usage limit"),
    ("is not supported when using codex", "refused the model it was given"),
    ("credit balance is too low", "has no credit left"),
    ("out of credits", "has no credit left"),
    ("please run /login", "is signed out"),
    ("oauth token has expired", "is signed out"),
    # Codex's start-up update question: "1. Update now ... 2. Skip 3. Skip until next version". It swallows the
    # first prompt, so a seat showing it never starts the review.
    ("skip until next version", "is waiting on its own update menu"),
)

#: Claude Code's usage and spend limit menu (UsageLimitDialog): the numbered first option, then the cancel hint.
LIMIT_MENU_OPTION = "1. stop and wait for limit to reset"
LIMIT_MENU_HINT = "esc to cancel"
LIMIT_MENU_REASON = "is showing its usage or spend limit menu"

#: How much of the bottom of the screen a blocker or the working marker is read from.
TAIL_LINES = 12

#: How long a seat may take to receive its first prompt. The Director waits up to ten minutes for an agent to show
#: it reads input before typing it (FirstPromptGate.MinimumWait), so less would stop seats the Director still serves.
DELIVERY_SECONDS = 600

#: How often the screen is read while a seat is watched.
POLL_SECONDS = 3.0

#: How much of the prompt has to be seen in the output to count as delivered. Enough to be unmistakable, and short
#: enough to sit inside the first wrapped row of any prompt.
ECHO_CHARACTERS = 40

#: How much text on each side of a match is compared with the prompt to tell a quote in the prompt from a notice.
CONTEXT_CHARACTERS = 20


def _despaced(text: str) -> str:
    return re.sub(r"\s+", "", text.lower())


def _outside_prompt(text: str, needle: str, prompt: str) -> bool:
    """Whether `needle` occurs in `text` anywhere that is not part of an echo of `prompt` (all despaced)."""
    start = text.find(needle)
    while start >= 0:
        end = start + len(needle)
        before = text[max(0, start - CONTEXT_CHARACTERS):end]
        after = text[start:end + CONTEXT_CHARACTERS]
        if not prompt or not (before in prompt or after in prompt):
            return True
        start = text.find(needle, start + 1)
    return False


def find_blocker(tail: str, prompt: str) -> Optional[str]:
    """What stops this seat from working, in words, or None. `tail` is the bottom of the screen.

    Asked only while the seat is not working: a seat that is working is not blocked, whatever it prints.
    """
    text = _despaced(tail)
    quoted = _despaced(prompt)
    for signature, reason in BLOCKERS:
        if _outside_prompt(text, _despaced(signature), quoted):
            return reason
    option = text.find(_despaced(LIMIT_MENU_OPTION))
    if option >= 0 and _outside_prompt(text, _despaced(LIMIT_MENU_OPTION), quoted) \
            and text.find(_despaced(LIMIT_MENU_HINT), option) > option:
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


def _read(seat: Seat, sid: str, query: str) -> dict:
    """One buffer read: the answer with its text, or a failure that says the seat is still open."""
    next_steps = [f"cc-devthrottle session buffer {sid}", f'cc-devthrottle session stop {sid} --reason "<why>"']
    try:
        resp = gateway.get_json(f"sessions/{gateway.path_segment(sid)}/buffer?{query}")
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not read the screen of the {seat.name} seat, session {sid}, so whether it can review is "
            f"unknown. It is still open and no other seat was started: {err}",
            next_steps,
        )
    text = None
    if isinstance(resp, dict):
        for key in ("text", "Text"):
            if isinstance(resp.get(key), str):
                text = resp[key]
                break
    if text is None:
        axi_cli.fail(
            f"the Gateway's answer for the {seat.name} seat, session {sid}, carried no screen text, so whether it "
            "can review is unknown. It is still open and no other seat was started.",
            next_steps,
        )
    cursor = resp.get("newCursor", resp.get("NewCursor"))
    return {"text": text, "cursor": cursor if isinstance(cursor, int) else None}


def _working(seat: Seat, sid: str) -> bool:
    """Whether the Gateway says this seat is running a turn now."""
    next_steps = ["cc-devthrottle session list", f'cc-devthrottle session stop {sid} --reason "<why>"']
    try:
        resp = gateway.get_json(f"sessions/{gateway.path_segment(sid)}")
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"could not read the state of the {seat.name} seat, session {sid}, so whether it can review is "
            f"unknown. It is still open and no other seat was started: {err}",
            next_steps,
        )
    state = resp.get("activityState", resp.get("ActivityState")) if isinstance(resp, dict) else None
    if not isinstance(state, str) or not state:
        axi_cli.fail(
            f"the Gateway's answer for the {seat.name} seat, session {sid}, carried no activity state, so whether "
            "it is working is unknown. It is still open and no other seat was started.",
            next_steps,
        )
    return state == WORKING_STATE


def _blocker_now(seat: Seat, sid: str, prompt: str) -> Tuple[bool, Optional[str]]:
    """(working, blocker): the Gateway's state, and the blocker on screen when the seat is not working."""
    if _working(seat, sid):
        return True, None
    return False, find_blocker(_read(seat, sid, f"lines={TAIL_LINES}")["text"], prompt)


def watch_seat(
    seat: Seat,
    sid: str,
    prompt: str,
    watch_seconds: float,
    clock: Optional[Callable[[], float]] = None,
    sleep: Optional[Callable[[float], None]] = None,
) -> Optional[str]:
    """Watch the seat until it is known to review or not. None means it is reviewing; otherwise why it cannot."""
    clock = clock or time.monotonic
    sleep = sleep or time.sleep
    echo = _despaced(prompt)[:ECHO_CHARACTERS]

    # Step 1: wait for the prompt to arrive, reading only the output written since the last read.
    deadline = clock() + DELIVERY_SECONDS
    cursor = 0
    seen = ""
    worked = False
    while True:
        working, blocker = _blocker_now(seat, sid, prompt)
        if blocker:
            return blocker
        if working:
            worked = True
            break
        new = _read(seat, sid, f"since={cursor}")
        if new["cursor"] is None:
            axi_cli.fail(
                f"the Gateway's answer for the {seat.name} seat, session {sid}, carried no cursor, so whether its "
                "prompt arrived cannot be followed. It is still open and no other seat was started.",
                [f"cc-devthrottle session buffer {sid}", f'cc-devthrottle session stop {sid} --reason "<why>"'],
            )
        cursor = new["cursor"]
        # Kept short: only enough of what came before to find an echo split across two reads.
        seen = (seen + _despaced(new["text"]))[-(4 * ECHO_CHARACTERS + 4096):]
        if echo in seen:
            break
        if clock() >= deadline:
            return f"never received its prompt in {DELIVERY_SECONDS // 60} minutes"
        sleep(POLL_SECONDS)

    # Step 2: the prompt is in. Watch the bottom of the screen for the whole watch: a limit notice is printed after
    # the agent starts its turn, so a working marker in the first seconds settles nothing.
    deadline = clock() + watch_seconds
    while True:
        working, blocker = _blocker_now(seat, sid, prompt)
        if blocker:
            return blocker
        if working:
            worked = True
        if clock() >= deadline:
            return None if worked else f"showed no sign of working in {int(watch_seconds)} seconds after its prompt"
        sleep(POLL_SECONDS)


def _stop(seat: Seat, sid: str, why: str) -> None:
    reason = f"Reviewer seat {seat.name} {why}; moving the review to the next seat."
    next_steps = ["cc-devthrottle session list", f'cc-devthrottle session stop {sid} --reason "{reason}"']
    try:
        resp = gateway.post_json(f"sessions/{gateway.path_segment(sid)}/stop", {"reason": reason})
    except gateway.GatewayError as err:
        axi_cli.fail(
            f"the {seat.name} seat {why}, but stopping it failed, so no other seat was started (two reviewers "
            f"could otherwise run on one change). Session {sid}: {err}",
            next_steps,
        )
    headline = gateway.field(resp, "headline", "Headline") if isinstance(resp, dict) else ""
    if not headline:
        # Every answer the stop route gives carries a headline; one without it says nothing about whether the
        # seat stopped, and starting the next seat on that would risk two reviewers on one change.
        axi_cli.fail(
            f"the {seat.name} seat {why}, but the Gateway's answer to stopping it, session {sid}, says nothing "
            "about whether it stopped, so no other seat was started.",
            next_steps,
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

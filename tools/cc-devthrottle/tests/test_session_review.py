"""Tests for `cc-devthrottle session review`: the reviewer moves to the next seat by itself.

The seat order is the owner's (Codex, then Claude Code on Fable, then on Opus). These tests drive the real
command with the Gateway replaced: each spawn is answered with a new session id, and each buffer read is answered
the way the real route answers it - the WHOLE output since the session started, cut to its last lines for `lines=`
and to the bytes after a cursor for `since=` - and each state read with the Gateway's activityState for that moment. The screens are built like real ones: a banner, the echoed prompt,
whatever the agent printed, and the footer, with the spaces the cleaned buffer drops already dropped. The clock moves
one poll per sleep, so a minute's watch runs instantly and nothing reaches the network.
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import review_ops, session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

PROMPT = "Review the change in this worktree and answer APPROVE or CHANGES with numbered findings."

BANNER = "ClaudeCodev2.1.302\nFable5.1 - ClaudeMax\nD:\\ReposFred\\wt\n"
BOOTING = BANNER + "\n> Try \"how do I log an error?\"\n? for shortcuts"
ECHO = f"> {PROMPT}\n"
# The footer as the real buffer of a working Claude Code seat draws it: spaces dropped around the marker.
WORKING_FOOTER = "\n--------\n>\n--------\nbypasspermissionson (shift+tabtocycle) - PR#4199 -esctointerrupt- 1agent"
IDLE_FOOTER = "\n--------\n>\n--------\n? for shortcuts"

WORKING = BANNER + ECHO + "Reading review_ops.py\n" + WORKING_FOOTER
STOPPED_AFTER_WORK = BANNER + ECHO + "Reading review_ops.py\nDone." + IDLE_FOOTER
CODEX_LIMIT = BANNER + ECHO + "You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage." + IDLE_FOOTER
CODEX_MODEL = (BANNER + ECHO + "The 'gpt-5.9' model is not supported when using Codex with a ChatGPT account."
               + IDLE_FOOTER)
# Codex's start-up question, as the real buffer showed it on 17 October 2026: the prompt never arrives.
CODEX_UPDATE_MENU = (
    "Updat available - 0.160.1 -> 0.163.0\n> 1. Update now (runs `powershell ...`)\n"
    "2.Skip3.Skipuntilnextversionenter continue - esc skip"
)
CLAUDE_LIMIT_MENU = (
    BANNER + ECHO + "What do you want to do?\n 1. Stopandwaitforlimittoreset\n 2. Addfundstocontinuewithusagecredits\n"
    " Enter to confirm - Esc to cancel"
)
# A reviewer printing the very file under review, which quotes every notice - while it works.
FILE_WITH_NOTICES = "\n".join([
    '    ("hit your usage limit.", "hit its usage limit"),',
    '    ("out of credits", "has no credit left"),',
    '    ("please run /login", "is signed out"),',
    '    LIMIT_MENU_OPTION = "1. stop and wait for limit to reset"',
    '    LIMIT_MENU_HINT = "esc to cancel"',
])
READING_THE_NOTICES = BANNER + ECHO + FILE_WITH_NOTICES + WORKING_FOOTER


class FakeGateway:
    """The Gateway as this command sees it: spawn, state, buffer, stop. `screens` holds, per seat in spawn order, one
    moment per state read - the last one repeats. A moment is the whole output, or (output, activityState); a bare
    output is Working when it ends in the working footer, as a live seat's does. Buffer reads answer from the moment
    of the state read before them."""

    def __init__(self, screens, stop_fails=False):
        self.screens = screens
        self.stop_fails = stop_fails
        self.spawned = []  # (session id, posted body)
        self.stopped = []  # (session id, reason)
        self.tail_reads = {}

    def post_json(self, path, body):
        if path.endswith("/stop"):
            sid = path.split("/")[1]
            if self.stop_fails:
                raise session_ops.gateway.GatewayError("the Director did not answer in time", status=504)
            self.stopped.append((sid, body["reason"]))
            return {"verdict": "stopped", "headline": f"Stopped {sid}."}
        sid = f"seat-{len(self.spawned)}"
        self.spawned.append((sid, dict(body)))
        return {"sessionId": sid, "name": body.get("name", "")}

    def _moment(self, sid, advance):
        index = int(sid.split("-")[1])
        n = self.tail_reads.get(sid, 0)
        if advance:
            self.tail_reads[sid] = n + 1
        else:
            n = max(0, n - 1)
        script = self.screens[index]
        moment = script[min(n, len(script) - 1)]
        if isinstance(moment, tuple):
            return moment
        return moment, ("Working" if moment.endswith(WORKING_FOOTER) else "WaitingForInput")

    def get_json(self, path):
        if "?" not in path:
            sid = path.split("/")[1]
            return {"sessionId": sid, "activityState": self._moment(sid, advance=True)[1]}
        route, query = path.split("?", 1)
        sid = route.split("/")[1]
        key, value = query.split("=", 1)
        text = self._moment(sid, advance=False)[0]
        if key == "lines":
            return {"text": "\n".join(text.split("\n")[-int(value):]), "newCursor": len(text)}
        return {"text": text[int(value):], "newCursor": len(text)}


class FakeTime:
    """One poll costs exactly POLL_SECONDS, so a watch ends after a fixed number of reads."""

    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


@pytest.fixture
def fake(monkeypatch):
    def make(screens, stop_fails=False):
        gw = FakeGateway(screens, stop_fails)
        monkeypatch.setenv("CC_SESSION_ID", "the-author")
        monkeypatch.setenv("CC_DIRECTOR_ID", "my-director")
        monkeypatch.setattr(session_ops.gateway, "post_json", gw.post_json)
        monkeypatch.setattr(session_ops.gateway, "get_json", gw.get_json)
        monkeypatch.setattr(session_ops.gateway, "get_fleet", lambda: ([], True, None, None))
        monkeypatch.setattr(review_ops, "time", FakeTime())
        return gw
    return make


def _review(*extra, prompt=PROMPT):
    return runner.invoke(
        app, ["session", "review", "C:/wt", "--prompt", prompt, "--name", "Fix - Reviewer"] + list(extra),
    )


def _said(result):
    return " ".join(result.output.split())


def test_a_codex_seat_on_its_usage_limit_is_stopped_and_fable_reviews(fake):
    gw = fake([[BOOTING, CODEX_LIMIT], [BOOTING, WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert [b["agent"] for _, b in gw.spawned] == ["Codex", "ClaudeCode"]
    assert "--model claude-fable-5-1" in gw.spawned[1][1]["args"]
    # The stuck seat is stopped, with the reason, before the next one opens - never two reviewers at once.
    assert [sid for sid, _ in gw.stopped] == ["seat-0"]
    assert "hit its usage limit" in gw.stopped[0][1]
    # Both seats are the author's and both carry the same prompt.
    assert all(b["controllerSessionId"] == "the-author" for _, b in gw.spawned)
    assert all(b["prePrompt"] == PROMPT for _, b in gw.spawned)
    assert "the codex seat hit its usage limit, so the fable seat is reviewing, in session seat-1." in _said(result)


def test_codex_on_its_update_menu_is_moved_on_without_waiting_for_a_prompt_that_never_comes(fake):
    gw = fake([[CODEX_UPDATE_MENU], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "is waiting on its own update menu" in gw.stopped[0][1]
    assert gw.tail_reads["seat-0"] == 1


def test_codex_refusing_its_model_moves_the_review_on(fake):
    gw = fake([[CODEX_MODEL], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "refused the model it was given" in gw.stopped[0][1]


def test_a_seat_slow_to_receive_its_prompt_is_waited_for_not_stopped(fake):
    # Two minutes of booting - longer than the whole watch - then the prompt arrives and the seat works.
    gw = fake([[BOOTING] * 40 + [BANNER + ECHO + IDLE_FOOTER, WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert gw.stopped == []
    assert "The codex seat is reviewing, in session seat-0." in result.output


def test_a_seat_whose_prompt_never_arrives_is_replaced_after_the_delivery_wait(fake):
    gw = fake([[BOOTING], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "never received its prompt in 10 minutes" in gw.stopped[0][1]


def test_a_seat_that_got_its_prompt_but_never_works_is_replaced(fake):
    gw = fake([[BANNER + ECHO + IDLE_FOOTER], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "showed no sign of working in 60 seconds after its prompt" in gw.stopped[0][1]


def test_a_limit_printed_after_the_seat_started_working_still_moves_it(fake):
    # The notice arrives after the agent begins its turn, so an early working marker settles nothing.
    gw = fake([[WORKING, WORKING, CODEX_LIMIT], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert [sid for sid, _ in gw.stopped] == ["seat-0"]


def test_a_reviewer_printing_a_file_that_quotes_every_notice_is_not_stopped(fake):
    gw = fake([[WORKING, READING_THE_NOTICES]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert gw.stopped == []


def test_a_notice_far_above_the_bottom_of_the_screen_is_not_a_blocker(fake):
    # The buffer is everything since birth; an old notice scrolled away is not the seat's state now.
    old = BANNER + ECHO + "You've hit your usage limit. Upgrade.\n" + "\n".join(f"line {i}" for i in range(30))
    gw = fake([[old + WORKING_FOOTER, old + IDLE_FOOTER]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert gw.stopped == []


def test_a_working_footer_left_on_an_idle_seat_does_not_count_as_reviewing(fake):
    # The buffer keeps every byte, so the footer drawn while working outlives the turn. The Gateway's state rules.
    gw = fake([[(BANNER + ECHO + WORKING_FOOTER, "WaitingForInput")], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "showed no sign of working" in gw.stopped[0][1]


def test_a_limit_printed_under_a_stale_working_footer_still_moves_the_seat(fake):
    limit_under_old_footer = BANNER + ECHO + WORKING_FOOTER + "\nYou've hit your usage limit. Upgrade to Pro."
    gw = fake([[WORKING, (limit_under_old_footer, "WaitingForInput")], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "hit its usage limit" in gw.stopped[0][1]


def test_a_prompt_that_mentions_the_working_marker_does_not_make_an_idle_seat_count_as_reviewing(fake):
    prompt = "Review DoorbellSafety.cs, which keys the working state on esc to interrupt in the footer."
    gw = fake([[BANNER + f"> {prompt}\n" + IDLE_FOOTER], [WORKING]])

    result = _review(prompt=prompt)

    assert result.exit_code == 0, result.output
    assert "showed no sign of working" in gw.stopped[0][1]


def test_claude_codes_limit_menu_moves_fable_on_to_opus(fake):
    gw = fake([[CODEX_LIMIT], [CLAUDE_LIMIT_MENU], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "--model claude-opus-5-5" in gw.spawned[2][1]["args"]
    assert "usage or spend limit menu" in gw.stopped[1][1]
    assert (
        "the codex seat hit its usage limit; the fable seat is showing its usage or spend limit menu, "
        "so the opus seat is reviewing, in session seat-2." in _said(result)
    )


def test_a_working_codex_seat_is_the_reviewer_and_nothing_else_opens(fake):
    gw = fake([[BOOTING, WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert len(gw.spawned) == 1 and gw.stopped == []
    assert "The codex seat is reviewing, in session seat-0." in result.output


def test_a_seat_that_worked_and_stopped_without_a_blocker_is_the_reviewer(fake):
    # A short review can finish inside the watch; it worked, and nothing says it was refused.
    gw = fake([[WORKING, STOPPED_AFTER_WORK]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert gw.stopped == []


def test_when_no_seat_can_start_it_fails_and_says_why_for_each(fake):
    gw = fake([[CODEX_LIMIT], [CLAUDE_LIMIT_MENU], [BANNER + ECHO + IDLE_FOOTER]])

    result = _review()

    assert result.exit_code == 1
    assert len(gw.stopped) == 3
    assert "no reviewer seat could start" in _said(result)
    assert "the opus seat showed no sign of working" in _said(result)


def test_a_stop_that_fails_opens_no_second_reviewer(fake):
    gw = fake([[CODEX_LIMIT], [WORKING]], stop_fails=True)

    result = _review()

    assert result.exit_code == 1
    assert len(gw.spawned) == 1
    assert "no other seat was started" in _said(result)


def test_a_stop_answer_that_says_nothing_opens_no_second_reviewer(fake, monkeypatch):
    gw = fake([[CODEX_LIMIT], [WORKING]])
    real_post = gw.post_json
    monkeypatch.setattr(
        session_ops.gateway, "post_json", lambda path, body: {} if path.endswith("/stop") else real_post(path, body)
    )

    result = _review()

    assert result.exit_code == 1
    assert len(gw.spawned) == 1
    assert "says nothing about whether it stopped, so no other seat was started" in _said(result)


def test_seats_sets_the_order(fake):
    gw = fake([[WORKING]])

    result = _review("--seats", "opus,codex")

    assert result.exit_code == 0, result.output
    assert "--model claude-opus-5-5" in gw.spawned[0][1]["args"]


@pytest.mark.parametrize("seats", ["codex,grok", "codex,codex", " , "])
def test_an_unknown_repeated_or_empty_seat_list_is_refused_before_anything_opens(fake, seats):
    gw = fake([[WORKING]])

    result = _review("--seats", seats)

    assert result.exit_code != 0
    assert gw.spawned == []


def test_a_prompt_that_quotes_a_limit_notice_is_not_a_blocker():
    prompt = "Check that \"You've hit your usage limit.\" is matched only at the bottom of the screen"
    tail = f"> {prompt}" + IDLE_FOOTER

    assert review_ops.find_blocker(tail, prompt) is None
    assert review_ops.find_blocker(tail + "\nYou've hit your usage limit. Upgrade.", prompt) == "hit its usage limit"


def test_a_limit_warning_is_not_a_blocker():
    # Seats still working print these; treating one as a block would stop a healthy reviewer.
    assert review_ops.find_blocker("You've used 94% of your weekly limit", "") is None
    assert review_ops.find_blocker("less than 25% of your weekly limit left", "") is None


def test_the_limit_menu_needs_its_cancel_hint_below_the_option():
    # The option's words in prose, with no open menu under them, are not the menu.
    assert review_ops.find_blocker("1. Stop and wait for limit to reset is the first option.", "") is None
    assert review_ops.find_blocker("1. Stop and wait for limit to reset\nEsc to cancel", "") == review_ops.LIMIT_MENU_REASON

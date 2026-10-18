"""Tests for `cc-devthrottle session review`: the reviewer moves to the next seat by itself.

The seat order is the owner's (Codex, then Claude Code on Fable, then on Opus). These tests drive the real
command with the Gateway replaced: each spawn is answered with a new session id, each screen read returns what
the test scripted for that session, and the clock moves one poll per read - so a minute's watch runs instantly
and nothing reaches the network.
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

CODEX_LIMIT = "You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits."
CODEX_MODEL = "The 'gpt-5.9' model is not supported when using Codex with a ChatGPT account."
WORKING = "Reading the diff\n  Working (4s - esc to interrupt)"
IDLE = "> Review the change in this worktree\n\n? for shortcuts"
CLAUDE_LIMIT_MENU = (
    "What do you want to do?\n 1. Stopandwaitforlimittoreset\n 2. Addfundstocontinuewithusagecredits\n"
    " Enter to confirm - Esc to cancel"
)


class FakeGateway:
    """The Gateway as this command sees it: spawn, screen, stop. `screens` holds, per seat in spawn order, the
    screens its reads return - the last one repeats."""

    def __init__(self, screens, stop_fails=False):
        self.screens = screens
        self.stop_fails = stop_fails
        self.spawned = []  # (session id, posted body)
        self.stopped = []  # (session id, reason)
        self.reads = {}

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

    def get_json(self, path):
        sid = path.split("/")[1]
        index = int(sid.split("-")[1])
        n = self.reads.get(sid, 0)
        self.reads[sid] = n + 1
        script = self.screens[index]
        return {"text": script[min(n, len(script) - 1)]}


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


def _review(*extra):
    return runner.invoke(
        app,
        ["session", "review", "C:/wt", "--prompt", "Review the change in this worktree", "--name", "Fix - Reviewer"]
        + list(extra),
    )


def test_a_codex_seat_on_its_usage_limit_is_stopped_and_fable_reviews(fake):
    gw = fake([[IDLE, CODEX_LIMIT], [IDLE, WORKING, WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert [b["agent"] for _, b in gw.spawned] == ["Codex", "ClaudeCode"]
    assert "--model claude-fable-5-1" in gw.spawned[1][1]["args"]
    # The stuck seat is stopped, with the reason, before the next one opens - never two reviewers at once.
    assert [sid for sid, _ in gw.stopped] == ["seat-0"]
    assert "hit its usage limit" in gw.stopped[0][1]
    # Both seats are the author's and both carry the same prompt.
    assert all(b["controllerSessionId"] == "the-author" for _, b in gw.spawned)
    assert all(b["prePrompt"] == "Review the change in this worktree" for _, b in gw.spawned)
    assert "the codex seat hit its usage limit, so the fable seat is reviewing, in session seat-1." in result.output


def test_codex_refusing_its_model_moves_the_review_on(fake):
    gw = fake([[CODEX_MODEL], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "refused the model it was given" in gw.stopped[0][1]
    assert "fable seat is reviewing" in result.output


def test_a_seat_that_never_shows_it_is_working_is_not_accepted(fake):
    # No blocker is not the same as working: a swallowed first prompt shows neither.
    gw = fake([[IDLE], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "showed no sign of working in 60 seconds" in gw.stopped[0][1]
    assert "fable seat is reviewing" in result.output


def test_a_limit_printed_after_the_seat_started_working_still_moves_it(fake):
    # The notice arrives after the agent begins its turn, so an early working marker settles nothing.
    gw = fake([[WORKING, WORKING, CODEX_LIMIT], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert [sid for sid, _ in gw.stopped] == ["seat-0"]


def test_claude_codes_limit_menu_moves_fable_on_to_opus(fake):
    gw = fake([[CODEX_LIMIT], [CLAUDE_LIMIT_MENU], [WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert "--model claude-opus-5-5" in gw.spawned[2][1]["args"]
    assert "usage or spend limit menu" in gw.stopped[1][1]
    assert (
        "the codex seat hit its usage limit; the fable seat is showing its usage or spend limit menu, "
        "so the opus seat is reviewing, in session seat-2." in result.output
    )


def test_a_working_codex_seat_is_the_reviewer_and_nothing_else_opens(fake):
    gw = fake([[IDLE, WORKING]])

    result = _review()

    assert result.exit_code == 0, result.output
    assert len(gw.spawned) == 1 and gw.stopped == []
    assert "The codex seat is reviewing, in session seat-0." in result.output


def test_when_no_seat_can_start_it_fails_and_says_why_for_each(fake):
    gw = fake([[CODEX_LIMIT], [CLAUDE_LIMIT_MENU], [IDLE]])

    result = _review()

    assert result.exit_code == 1
    assert len(gw.stopped) == 3
    assert "no reviewer seat could start" in result.output
    assert "the opus seat showed no sign of working" in result.output


def test_a_stop_that_fails_opens_no_second_reviewer(fake):
    gw = fake([[CODEX_LIMIT], [WORKING]], stop_fails=True)

    result = _review()

    assert result.exit_code == 1
    assert len(gw.spawned) == 1
    assert "no other seat was started" in result.output


def test_a_stop_answer_that_says_nothing_opens_no_second_reviewer(fake, monkeypatch):
    gw = fake([[CODEX_LIMIT], [WORKING]])
    real_post = gw.post_json
    monkeypatch.setattr(
        session_ops.gateway, "post_json", lambda path, body: {} if path.endswith("/stop") else real_post(path, body)
    )

    result = _review()

    assert result.exit_code == 1
    assert len(gw.spawned) == 1
    assert "says nothing about whether it stopped, so no other seat was started" in " ".join(result.output.split())


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
    prompt = f'Check the text "{CODEX_LIMIT}" is matched'
    screen = f"> {prompt}\n{WORKING}"

    assert review_ops.find_blocker(screen, prompt) is None
    assert review_ops.find_blocker(f"{screen}\n{CODEX_LIMIT}", prompt) == "hit its usage limit"


def test_a_limit_warning_is_not_a_blocker():
    # Seats still working print these; treating one as a block would stop a healthy reviewer.
    assert review_ops.find_blocker("You've used 94% of your weekly limit", "") is None
    assert review_ops.find_blocker("less than 25% of your weekly limit left", "") is None

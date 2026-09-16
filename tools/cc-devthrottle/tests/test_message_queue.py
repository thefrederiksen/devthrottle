"""Tests for the queued fleet message on the command line (the Message Load mission, slice 1).

A message is a record in the recipient's inbox, not keystrokes typed into it. What these assert:

  * `message send` says QUEUED, never delivered, and prints the Gateway's refusal sentence word for
    word when it is refused - an agent that is told only "403" does not learn to put it in its report.
  * A dropped duplicate is not a failure: the message it repeats is already waiting.
  * `message send all` counts by OUTCOME. A broadcast where every copy was refused queued nothing and
    must exit non-zero rather than print "sent to 3 sessions".
  * `message inbox` prints every unread message IN FULL. Reading marks it read, so a truncated body
    would be text the recipient can never see again.
  * `message ask` is gone - from the commands and from the action catalogue.
"""

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402
from src.cli import app, _ACTIONS  # noqa: E402

runner = CliRunner()

ME = "11111111-1111-1111-1111-111111111111"
WORKER = "33333333-3333-3333-3333-333333333333"
WORKER_ROW = {"sessionId": WORKER, "name": "Mission - Worker - tests"}


@pytest.fixture
def posted(monkeypatch):
    """Record every POST and answer with whatever the test sets in `answer`."""
    calls = []
    state = {"answer": None}

    def fake_post_json(path, body=None, timeout=30):
        calls.append({"path": path, "body": body})
        answer = state["answer"]
        if isinstance(answer, Exception):
            raise answer
        return answer

    monkeypatch.setenv("CC_SESSION_ID", ME)
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    monkeypatch.setattr(session_ops, "_resolve_target", lambda t, command_name=None: WORKER_ROW)
    return calls, state


# --- message send -----------------------------------------------------------------------------------


def test_send_says_queued_and_never_delivered(posted, plain):
    calls, state = posted
    state["answer"] = {"status": "queued", "messageId": "a" * 32, "recipientSessionId": WORKER}

    result = runner.invoke(app, ["message", "send", "worker", "line one\nline two"])

    assert result.exit_code == 0
    assert calls[0]["path"] == f"sessions/{WORKER}/message"
    # The text travels whole, newlines included: it is read from the inbox, never typed.
    assert calls[0]["body"] == {"text": "line one\nline two"}
    out = plain(result.output)
    assert "Queued" in out
    assert "a" * 32 in out
    assert "delivered" not in out.lower()


def test_a_refusal_prints_the_gateways_own_sentence_and_fails(posted, plain):
    calls, state = posted
    sentence = ("You may message only the session that started you and the sessions you started. "
                "33333333 is neither, so nothing was queued.")
    state["answer"] = session_ops.gateway.GatewayError(sentence, status=403)

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 1
    out = " ".join(plain(result.output).split())
    assert "Not queued:" in out
    assert "the sessions you started" in out


def test_a_refusal_in_a_200_body_still_fails(posted, plain):
    _, state = posted
    state["answer"] = {"status": "refused", "error": "the limit is 6"}

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 1
    assert "the limit is 6" in plain(result.output)


def test_an_answer_this_tool_does_not_understand_is_a_failure_not_a_success(posted, plain):
    # The OLD Gateway answered {"accepted": true}. A new command line talking to an old Gateway must not
    # read that as queued - it was typed into the recipient, which is the thing this mission removed.
    _, state = posted
    state["answer"] = {"accepted": True}

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 1
    assert "Not queued" in plain(result.output)


def test_a_dropped_duplicate_is_not_a_failure(posted, plain):
    _, state = posted
    state["answer"] = {"status": "duplicate", "note": "33333333 has not yet read an identical message from you"}

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 0
    out = plain(result.output)
    assert "Not queued again" in out
    assert "not yet read an identical message" in out


# --- message send all -------------------------------------------------------------------------------


def _row(sid, status, **extra):
    return {"recipientSessionId": sid, "status": status, **extra}


def test_send_all_goes_to_the_broadcast_route_and_counts_outcomes(posted, plain):
    calls, state = posted
    state["answer"] = {"results": [
        _row("aaaaaaaa-0000", "queued", messageId="1" * 32),
        _row("bbbbbbbb-0000", "refused", error="You messaged bbbbbbbb 2 minutes ago"),
        _row("cccccccc-0000", "queued", messageId="2" * 32),
    ]}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 0
    assert calls[0]["path"] == "fleet/broadcast"
    assert calls[0]["body"] == {"text": "stand up"}
    out = " ".join(plain(result.output).split())
    assert "Queued for 2 of 3" in out
    assert "bbbbbbbb not queued" in out
    assert "2 minutes ago" in out


def test_send_all_where_every_copy_was_refused_fails(posted, plain):
    _, state = posted
    state["answer"] = {"results": [
        _row("aaaaaaaa-0000", "refused", error="limit"),
        _row("bbbbbbbb-0000", "refused", error="limit"),
    ]}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 1
    assert "Queued for 0 of 2" in " ".join(plain(result.output).split())


def test_send_all_with_no_workers_says_so_and_succeeds(posted, plain):
    _, state = posted
    state["answer"] = {"results": [], "warning": "You have no workers to message."}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 0
    assert "You have no workers to message." in plain(result.output)


def test_a_denied_broadcast_fails_with_its_reason(posted, plain):
    _, state = posted
    state["answer"] = {"denied": True, "deniedReason": "needs a --reason and a valid human-issued --grant"}

    result = runner.invoke(app, ["message", "send", "all", "hi", "--everyone"])

    assert result.exit_code == 1
    assert "human-issued --grant" in plain(result.output)


# --- message inbox ----------------------------------------------------------------------------------


@pytest.fixture
def inbox(monkeypatch):
    got = {}

    def fake_get_json(path, timeout=30):
        got["path"] = path
        return got["answer"]

    monkeypatch.setattr(session_ops.gateway, "get_json", fake_get_json)
    return got


LONG = "Step one.\n" + ("x" * 3000) + "\nLast line of the message."


def _msg(mid, text, sender=WORKER, name="Mission - Worker - tests", kind="report"):
    return {
        "messageId": mid, "fromSessionId": sender, "fromName": name, "fromMachine": "mac-mini",
        "kind": kind, "text": text, "sentAtUtc": "2026-09-16T19:00:00Z", "readAtUtc": "2026-09-16T19:05:00Z",
    }


def test_inbox_prints_every_unread_message_in_full(inbox, capsys):
    inbox["answer"] = {"sessionId": ME, "unreadCount": 2,
                       "unread": [_msg("m1", LONG), _msg("m2", "short", sender=None, name=None, kind="system")],
                       "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 0, result.output
    assert inbox["path"] == "fleet/inbox"
    out = result.output
    assert out.startswith("count: 2 unread (now marked read)")
    # Not a character of the body is lost: reading it marked it read.
    assert "x" * 3000 in out
    assert "Last line of the message." in out
    assert "from: Mission - Worker - tests (" + WORKER + ") on mac-mini" in out
    assert "from: the Gateway" in out
    assert "message 2 of 2" in out
    assert "help[" in out


def test_an_empty_inbox_says_count_zero(inbox):
    inbox["answer"] = {"sessionId": ME, "unreadCount": 0, "unread": [], "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 0
    assert result.output.startswith("count: 0 unread\n")


def test_inbox_all_asks_for_the_read_ones_and_shows_them(inbox):
    inbox["answer"] = {"sessionId": ME, "unreadCount": 0, "unread": [], "recent": [_msg("old", "earlier words")]}

    result = runner.invoke(app, ["message", "inbox", "--all"])

    assert result.exit_code == 0
    assert inbox["path"] == "fleet/inbox?all=true"
    assert "earlier: 1 read before" in result.output
    assert "earlier words" in result.output


def test_inbox_output_is_ascii_with_the_rest_escaped(inbox):
    inbox["answer"] = {"sessionId": ME, "unreadCount": 1, "unread": [_msg("m1", "café → done")], "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 0
    assert result.output.isascii()
    assert "caf\\u00e9" in result.output


def test_inbox_json_is_the_gateways_answer_unchanged(inbox):
    answer = {"sessionId": ME, "unreadCount": 1, "unread": [_msg("m1", "hello")], "recent": []}
    inbox["answer"] = answer

    result = runner.invoke(app, ["message", "inbox", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.output) == answer


# --- message ask is gone ----------------------------------------------------------------------------


def test_message_ask_no_longer_exists():
    result = runner.invoke(app, ["message", "ask", "worker", "are you done?"])

    assert result.exit_code == 2
    assert not hasattr(session_ops, "ask_session")


def test_the_action_catalogue_lists_inbox_and_not_ask():
    ids = {a["id"] for a in _ACTIONS}
    assert "message-ask" not in ids
    assert "message-inbox" in ids
    assert not any("message ask" in a["command"] for a in _ACTIONS)
    send = next(a for a in _ACTIONS if a["id"] == "message-send")
    assert "queued" in send["description"]


def test_inbox_all_help_says_it_returns_the_last_24_hours_and_why(plain):
    # Inspection 1, ruling 4: the Gateway returns every message read in the last 24 hours, so a read whose
    # answer was lost can be recovered. The help must say both halves.
    result = runner.invoke(app, ["message", "inbox", "--help"])

    assert result.exit_code == 0
    # The help is drawn in a bordered box, so a sentence wraps across border characters; drop them first.
    out = " ".join(plain(result.output).replace("|", " ").replace("\u2502", " ").split())
    assert "every message you read in the last 24 hours" in out
    assert "read was lost" in out
    inbox = next(a for a in _ACTIONS if a["id"] == "message-inbox")
    assert "last 24 hours" in inbox["description"]

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
    assert "bbbbbbbb-0000 not queued" in out
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


def test_send_all_with_no_workers_says_so_and_fails(posted, plain):
    # Inspection 2, ruling 4: nothing was queued and nothing was waiting, so the exit code is 1 - the
    # same rule as every other broadcast, with no exception for an empty recipient list.
    _, state = posted
    state["answer"] = {"results": [], "warning": "You have no workers to message."}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 1
    words = " ".join(plain(result.output).split())
    assert "no workers to send to" in words
    assert "You have no workers to message." in words


def test_send_all_with_no_workers_and_no_warning_still_says_so(posted, plain):
    _, state = posted
    state["answer"] = {"results": []}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 1
    assert "no workers to send to" in plain(result.output)


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


def test_inbox_all_says_when_the_gateway_truncated_the_read_ones(inbox):
    recent = [_msg(f"old{i}", f"earlier {i}") for i in range(200)]
    inbox["answer"] = {"sessionId": ME, "unreadCount": 0, "unread": [], "recent": recent,
                       "recentTotal": 257, "truncated": True}

    result = runner.invoke(app, ["message", "inbox", "--all"])

    assert result.exit_code == 0
    assert "earlier: showing 200 of 257 read in the last 24 hours" in result.output
    assert "read before" not in result.output


def test_inbox_all_does_not_claim_truncation_when_the_gateway_did_not(inbox):
    recent = [_msg(f"old{i}", f"earlier {i}") for i in range(200)]
    inbox["answer"] = {"sessionId": ME, "unreadCount": 0, "unread": [], "recent": recent,
                       "recentTotal": 200, "truncated": False}

    result = runner.invoke(app, ["message", "inbox", "--all"])

    assert result.exit_code == 0
    assert "earlier: 200 read before" in result.output
    assert "showing" not in result.output


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
    # Inspection 1, ruling 4: the Gateway returns the messages read in the last 24 hours, so a read whose
    # answer was lost can be recovered. Inspection 2, rulings 1 and 2: at most 200 of them, and the loss
    # interval (marked read before the text arrives) is an accepted gap the help must state.
    result = runner.invoke(app, ["message", "inbox", "--help"])

    assert result.exit_code == 0
    # The help is drawn in a bordered box, so a sentence wraps across border characters; drop them first.
    out = " ".join(plain(result.output).replace("|", " ").replace("\u2502", " ").split())
    assert "the messages you read in the last 24 hours, newest first, at most 200" in out
    assert "marks messages read before their text reaches you" in out
    assert "only for 24 hours after that read" in out
    inbox = next(a for a in _ACTIONS if a["id"] == "message-inbox")
    assert "last 24 hours" in inbox["description"]


# --- the broadcast exit code (inspection 1, ruling 6) ------------------------------------------------

EXIT_RULE = (
    "Exit code: 0 when the message was queued or an identical one is already waiting unread - for 'all', "
    "when that is true of at least one worker - and 1 when nothing was queued and nothing was waiting."
)


def test_an_all_duplicate_broadcast_exits_zero_like_a_duplicate_single_send(posted, plain):
    _, state = posted
    state["answer"] = {"results": [
        _row("aaaaaaaa-0000", "duplicate", note="already waiting unread"),
        _row("bbbbbbbb-0000", "duplicate", note="already waiting unread"),
    ]}

    result = runner.invoke(app, ["message", "send", "all", "stand up"])

    assert result.exit_code == 0
    out = " ".join(plain(result.output).split())
    assert "Queued for 0 of 2" in out
    assert "aaaaaaaa-0000 not queued again" in out


def test_the_broadcast_exit_rule_is_stated_in_the_same_words_in_help_and_code(plain):
    # The help and the code comment carry one sentence, so neither can drift from the behaviour alone.
    result = runner.invoke(app, ["message", "send", "--help"])
    out = " ".join(plain(result.output).replace("|", " ").replace("│", " ").split())
    assert EXIT_RULE in out
    assert EXIT_RULE in " ".join(session_ops._report_broadcast.__doc__.split())


# --- the Gateway's sentences are quoted verbatim ----------------------------------------------------
#
# A refusal, a note and a warning are the Gateway's words. They are asserted on the RAW output, on a colour
# terminal and on a plain console (the either_console fixture): with highlighting on, the console coloured
# the numbers inside them, and the continuous integration run - which forces colour - read
# "the limit is <colour>6<reset>" where the Gateway had written "the limit is 6".

LIMIT = "You have sent 6 messages in the last hour; the limit is 6 [per hour] at /fleet/inbox."


def test_a_refusal_error_is_printed_verbatim(posted, either_console):
    _, state = posted
    state["answer"] = session_ops.gateway.GatewayError(LIMIT, status=429)

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 1
    assert LIMIT in result.output


def test_a_refusal_in_a_body_is_printed_verbatim(posted, either_console):
    _, state = posted
    state["answer"] = {"status": "refused", "error": LIMIT}

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 1
    assert LIMIT in result.output


def test_a_duplicate_note_is_printed_verbatim(posted, either_console):
    _, state = posted
    note = "33333333 has 1 identical message from you, unread since 12:04."
    state["answer"] = {"status": "duplicate", "note": note}

    result = runner.invoke(app, ["message", "send", "worker", "hello"])

    assert result.exit_code == 0
    assert note in result.output


def test_broadcast_sentences_are_printed_verbatim(posted, either_console):
    _, state = posted
    state["answer"] = {"results": [_row(WORKER, "refused", error=LIMIT)]}
    result = runner.invoke(app, ["message", "send", "all", "hello"])
    assert result.exit_code == 1
    assert LIMIT in result.output

    state["answer"] = {"denied": True, "deniedReason": LIMIT}
    result = runner.invoke(app, ["message", "send", "all", "hello"])
    assert result.exit_code == 1
    assert LIMIT in result.output

    state["answer"] = {"results": [], "warning": LIMIT}
    result = runner.invoke(app, ["message", "send", "all", "hello"])
    assert result.exit_code == 1
    assert "Not queued: no workers to send to. " + LIMIT in result.output


def test_an_inbox_read_error_is_printed_verbatim(monkeypatch, either_console):
    def refuse(path):
        raise session_ops.gateway.GatewayError(LIMIT, status=429)

    monkeypatch.setattr(session_ops.gateway, "get_json", refuse)

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 1
    assert LIMIT in result.output


# --- replies without blocking (slice 3) -----------------------------------------------------------------

CORRELATION = "c" * 32
QUESTION_ID = "q" * 32


def test_send_reply_wanted_sends_the_ask_and_prints_the_correlation_id(posted, plain):
    calls, state = posted
    state["answer"] = {"status": "queued", "messageId": "a" * 32, "recipientSessionId": WORKER,
                       "correlationId": CORRELATION, "replyByUtc": "2026-09-17T13:15:00Z"}

    result = runner.invoke(app, ["message", "send", "worker", "which branch?", "--reply-wanted", "--reply-by", "15"])

    assert result.exit_code == 0, result.output
    assert calls[0]["path"] == f"sessions/{WORKER}/message"
    assert calls[0]["body"] == {"text": "which branch?", "replyWanted": True, "replyByMinutes": 15}
    out = " ".join(plain(result.stdout).split())
    assert f"correlation id: {CORRELATION} (reply wanted by 2026-09-17T13:15:00Z)" in out
    assert "Do not wait for it" in out
    assert "wait for" not in out.replace("Do not wait for it", "")


def test_send_reply_wanted_without_a_deadline_leaves_the_default_to_the_gateway(posted):
    calls, state = posted
    state["answer"] = {"status": "queued", "messageId": "a" * 32, "correlationId": CORRELATION}

    result = runner.invoke(app, ["message", "send", "worker", "ok?", "--reply-wanted"])

    assert result.exit_code == 0, result.output
    assert calls[0]["body"] == {"text": "ok?", "replyWanted": True}


def test_send_without_reply_wanted_sends_no_reply_fields_and_prints_no_correlation(posted, plain):
    calls, state = posted
    state["answer"] = {"status": "queued", "messageId": "a" * 32}

    result = runner.invoke(app, ["message", "send", "worker", "fyi"])

    assert calls[0]["body"] == {"text": "fyi"}
    assert "correlation" not in plain(result.stdout)


def test_a_duplicate_question_prints_the_waiting_copys_correlation_id(posted, plain):
    _, state = posted
    state["answer"] = {"status": "duplicate", "note": "already waiting", "messageId": "a" * 32,
                       "correlationId": CORRELATION}

    result = runner.invoke(app, ["message", "send", "worker", "which branch?", "--reply-wanted"])

    assert result.exit_code == 0
    assert f"correlation id: {CORRELATION}" in plain(result.stdout)


def test_reply_posts_the_id_and_text_and_says_queued(posted, plain):
    calls, state = posted
    state["answer"] = {"status": "queued", "messageId": "r" * 32, "recipientSessionId": WORKER,
                       "inReplyToMessageId": QUESTION_ID, "correlationId": CORRELATION}

    result = runner.invoke(app, ["message", "reply", f" {CORRELATION} ", "line one\nline two"])

    assert result.exit_code == 0, result.output
    assert calls == [{"path": "fleet/reply", "body": {"id": CORRELATION, "text": "line one\nline two"}}]
    out = " ".join(plain(result.stdout).split())
    assert f"Reply queued for {WORKER} (reply {'r' * 32}, answering message {QUESTION_ID})" in out
    assert "delivered" not in out.lower()


def test_a_refused_reply_prints_the_gateways_sentence_on_standard_error(posted):
    _, state = posted
    sentence = f"Only the session message {QUESTION_ID} was sent to may reply to it. Nothing was queued."
    state["answer"] = session_ops.gateway.GatewayError(sentence, status=403)

    result = runner.invoke(app, ["message", "reply", CORRELATION, "mine"])

    assert result.exit_code == 1
    assert result.stdout == ""
    assert sentence in " ".join(result.stderr.split())


def test_a_duplicate_reply_is_not_a_failure(posted, plain):
    _, state = posted
    state["answer"] = {"status": "duplicate", "note": "already waiting unread"}

    result = runner.invoke(app, ["message", "reply", CORRELATION, "same"])

    assert result.exit_code == 0
    assert "Not queued again: already waiting unread" in plain(result.stdout)


def _question(**extra):
    return dict({"messageId": QUESTION_ID, "correlationId": CORRELATION, "toSessionId": WORKER,
                 "text": "which branch?\nand which commit?", "sentAtUtc": "2026-09-17T12:00:00Z",
                 "replyByUtc": "2026-09-17T13:00:00Z", "late": False}, **extra)


def test_inbox_shows_a_question_with_how_to_answer_it(inbox):
    m = dict(_msg("m1", "which branch?", kind="message"), replyWanted=True, correlationId=CORRELATION,
             replyByUtc="2026-09-17T13:00:00Z",
             replyHint=f'cc-devthrottle message reply {CORRELATION} "<your answer>"')
    inbox["answer"] = {"unread": [m], "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 0, result.output
    out = result.stdout
    assert "message 1 of 1" in out
    assert "  reply wanted by: 2026-09-17T13:00:00Z" in out
    assert f"  correlation id: {CORRELATION}" in out
    assert f'  to answer: cc-devthrottle message reply {CORRELATION} "<your answer>"' in out
    assert "question:" not in out


def test_inbox_shows_a_reply_with_the_question_it_answers(inbox):
    m = dict(_msg("m1", "main\nabc123", kind="reply"), correlationId=CORRELATION, inReplyTo=_question(late=True))
    inbox["answer"] = {"unread": [m], "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    assert result.exit_code == 0, result.output
    out = result.stdout
    assert "reply 1 of 1" in out
    assert "message 1 of 1" not in out
    assert f"  answers: message {QUESTION_ID} (correlation id {CORRELATION})" in out
    assert f"  asked of: {WORKER}" in out
    assert "  late: yes" in out
    assert "  question:\n    which branch?\n    and which commit?\n" in out
    assert "  text:\n    main\n    abc123" in out
    assert "reply wanted by" not in out


def test_inbox_shows_a_no_reply_notice_as_one(inbox):
    m = dict(_msg("m1", "No reply to your message ...", sender=None, name=None, kind="system"),
             notice="no-reply", correlationId=CORRELATION, inReplyTo=_question())
    inbox["answer"] = {"unread": [m], "recent": []}

    result = runner.invoke(app, ["message", "inbox"])

    out = result.stdout
    assert "no-reply notice 1 of 1" in out
    assert "  notice: no-reply" in out
    assert "  from: the Gateway" in out
    assert f"  about: message {QUESTION_ID} (correlation id {CORRELATION})" in out
    assert "  deadline: 2026-09-17T13:00:00Z" in out
    assert "late:" not in out
    assert "  question:\n    which branch?" in out


def test_a_system_notice_without_the_no_reply_label_is_a_plain_message(inbox):
    # The label is the Gateway's to give; this tool does not guess it from the kind.
    inbox["answer"] = {"unread": [_msg("m1", "stuck", sender=None, name=None, kind="system")], "recent": []}

    out = runner.invoke(app, ["message", "inbox"]).stdout

    assert "message 1 of 1" in out
    assert "notice" not in out


def test_a_reply_whose_question_is_no_longer_kept_says_so(inbox):
    m = dict(_msg("m1", "late answer", kind="reply"),
             inReplyTo={"messageId": QUESTION_ID, "correlationId": CORRELATION, "text": None, "late": False})
    inbox["answer"] = {"unread": [m], "recent": []}

    out = runner.invoke(app, ["message", "inbox"]).stdout

    assert "  question: (no longer kept)" in out
    assert "asked of" not in out


def test_the_action_catalogue_lists_reply_and_the_send_flags():
    ids = {a["id"]: a for a in _ACTIONS}
    assert "message-reply" in ids
    assert ids["message-reply"]["command"] == 'cc-devthrottle message reply <id> "<answer>"'
    assert ids["message-reply"]["mutatesState"] is True
    send = ids["message-send"]
    assert "--reply-wanted" in send["command"] and "--reply-by <minutes>" in send["command"]
    assert {"reply-wanted", "reply-by"} <= {a["name"] for a in send["args"]}


def test_reply_help_says_who_may_answer_and_that_nothing_waits(plain):
    result = runner.invoke(app, ["message", "reply", "--help"])
    out = " ".join(plain(result.output).split())
    assert result.exit_code == 0
    assert "Answer a message that asked for a reply" in out
    assert "Only the session the question was sent to may answer it, once." in out

    send = " ".join(plain(runner.invoke(app, ["message", "send", "--help"]).output).split())
    assert "--reply-wanted" in send
    assert "1 to 1440, 60 when omitted" in send
    assert "Nothing waits for it" in send

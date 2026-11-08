"""Tests for an agent using the account's inbox like email (the owner's decision of 7 November 2026).

`cc-devthrottle inbox list | read | delete | restore | mark | reply` and `factory inbox send`. These pin what each
command sends and prints: routes and bodies are written out as literals, never read from the module, so a changed route
turns a test red. No HTTP happens: the Gateway calls are stubbed. Who the agent is, and what each act records, is the
Gateway's ruling and is proven there; here, each command must call its own session route, print the Gateway's words,
apply every filter to --json, and say that delete is archive and that reading does not mark read.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import inbox_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

ME = "a1000000-0000-4000-8000-000000000001"
MID = "m0000000-0000-4000-8000-000000000001"
LINE = 'Archived by an agent: session 114 "Factory Inbox - Delivery Lead", 2026-11-07 14:02 UTC'


def _message(state="unread", label="Unread", agent=None, **extra):
    m = {
        "id": MID, "subject": "v2.25 notes ready", "body": "Read them\nwhen you can.", "state": state,
        "stateLabel": label, "from": {"kind": "session", "label": "412 Release - Developer"},
        "links": [{"kind": "session", "label": "Open 412 Release - Developer", "target": "x"}],
        "replyTo": {"target": "session", "label": "Reply to 412 Release - Developer"},
        "sentAtUtc": "2026-11-07T13:00:00Z", "replies": [],
        "agentActions": [] if agent is None else [{"act": "archived", "line": agent, "atUtc": "2026-11-07T14:02:00Z"}],
        "agentLine": agent,
    }
    m.update(extra)
    return m


def _list_answer(messages=None, count=None, view="inbox"):
    messages = [_message()] if messages is None else messages
    return {"view": view, "count": len(messages) if count is None else count, "unread": 1, "unreadLabel": "1 unread",
            "filters": [], "empty": "Nothing for you.", "messages": messages}


def _stub(monkeypatch, get=None, post=None, refuse=None, session=ME):
    calls = []
    gw = inbox_ops.gateway

    def fake_get_json(path, timeout=30, **_):
        calls.append(("GET", path, None))
        if refuse:
            raise refuse
        return get

    def fake_post_json(path, body=None, timeout=30, **_):
        calls.append(("POST", path, body))
        if refuse:
            raise refuse
        return post

    monkeypatch.setattr(gw, "get_json", fake_get_json)
    monkeypatch.setattr(gw, "post_json", fake_post_json)
    if session is None:
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
    else:
        monkeypatch.setenv("CC_SESSION_ID", session)
    return calls


# ---------- list ----------

def test_list_reads_this_sessions_route_and_prints_every_id_whole(monkeypatch, plain):
    calls = _stub(monkeypatch, get=_list_answer())

    result = runner.invoke(app, ["inbox", "list"])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", f"sessions/{ME}/inbox", None)]
    out = plain(result.output)
    assert "view: inbox" in out
    assert "count: 1 of 1 total" in out
    assert MID in out
    assert "412 Release - Developer" in out
    assert "cc-devthrottle inbox read <id>" in out


def test_list_view_and_count_go_to_the_gateway_so_they_apply_to_json(monkeypatch):
    answer = _list_answer(view="archived")
    calls = _stub(monkeypatch, get=answer)

    result = runner.invoke(app, ["inbox", "list", "--view", "archived", "--count", "5", "--json"])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", f"sessions/{ME}/inbox?view=archived&limit=5", None)]
    assert json.loads(result.output) == answer


def test_list_an_empty_view_says_so(monkeypatch, plain):
    _stub(monkeypatch, get=_list_answer(messages=[]))

    result = runner.invoke(app, ["inbox", "list"])

    assert result.exit_code == 0, result.output
    out = plain(result.output)
    assert "count: 0" in out
    assert "Nothing for you." in out


def test_list_agent_field_prints_the_gateways_line(monkeypatch, plain):
    _stub(monkeypatch, get=_list_answer(messages=[_message("archived", "Archived", LINE)]))

    result = runner.invoke(app, ["inbox", "list", "--fields", "id,agent"])

    assert result.exit_code == 0, result.output
    assert "Archived by an agent: session 114" in plain(result.output)


def test_list_bad_view_count_or_field_is_a_usage_error(monkeypatch):
    calls = _stub(monkeypatch, get=_list_answer())

    assert runner.invoke(app, ["inbox", "list", "--view", "spam"]).exit_code == 2
    assert runner.invoke(app, ["inbox", "list", "--count", "0"]).exit_code == 2
    assert runner.invoke(app, ["inbox", "list", "--fields", "nonsense"]).exit_code == 2
    assert runner.invoke(app, ["inbox", "list", "--fields", "id", "--json"]).exit_code == 2
    assert calls == []


def test_list_outside_a_session_fails_without_calling_the_gateway(monkeypatch):
    calls = _stub(monkeypatch, get=_list_answer(), session=None)

    result = runner.invoke(app, ["inbox", "list"])

    assert result.exit_code == 1
    assert calls == []
    assert "CC_SESSION_ID is not set" in result.output


# ---------- read ----------

def test_read_prints_the_whole_message_and_says_it_did_not_mark_it_read(monkeypatch, plain):
    calls = _stub(monkeypatch, get={"message": _message(agent="Read by an agent: session 114, 2026-11-07 14:02 UTC"),
                                    "replyGoesTo": "Reply to 412 Release - Developer (gateway)",
                                    "note": "Reading did not mark it read (gateway)."})

    result = runner.invoke(app, ["inbox", "read", MID])

    assert result.exit_code == 0, result.output
    assert calls == [("GET", f"sessions/{ME}/inbox/{MID}", None)]
    out = plain(result.output)
    assert "subject: v2.25 notes ready" in out
    assert "state: Unread" in out
    assert "when you can." in out
    assert "Read by an agent: session 114" in out
    assert "Reading did not mark it read (gateway)." in out
    assert "reply goes to: Reply to 412 Release - Developer (gateway)" in out
    assert f"cc-devthrottle inbox mark {MID} --read" in out


def test_read_json_prints_the_gateways_answer(monkeypatch):
    answer = {"message": _message()}
    _stub(monkeypatch, get=answer)

    result = runner.invoke(app, ["inbox", "read", MID, "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == answer


def test_read_a_refusal_reaches_the_caller_in_words(monkeypatch):
    _stub(monkeypatch, refuse=inbox_ops.gateway.GatewayError("There is no message x in your inbox.", 404, {"code": "message_not_found"}))

    result = runner.invoke(app, ["inbox", "read", "x"])

    assert result.exit_code == 1
    assert "There is no message x in your inbox." in result.output


# ---------- delete, restore, mark ----------

def test_delete_archives_and_says_so(monkeypatch, plain):
    calls = _stub(monkeypatch, post={"message": _message("archived", "Archived", LINE), "done": "Archived. Delete is archive: the message left the inbox and is under Archived, where it can be restored. Nothing was deleted."})

    result = runner.invoke(app, ["inbox", "delete", MID])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/inbox/{MID}/state", {"state": "archived"})]
    out = plain(result.output)
    assert "Delete is archive" in out
    assert "Nothing was deleted." in out
    assert f"cc-devthrottle inbox restore {MID}" in out
    assert LINE in out


def test_delete_help_says_it_archives():
    result = runner.invoke(app, ["inbox", "delete", "--help"])

    assert result.exit_code == 0
    assert "ARCHIVES" in result.output


def test_restore_sends_restore(monkeypatch):
    calls = _stub(monkeypatch, post={"message": _message(), "done": "Moved back to the inbox, unread (gateway)."})

    result = runner.invoke(app, ["inbox", "restore", MID])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/inbox/{MID}/state", {"state": "restore"})]
    assert "Moved back to the inbox, unread (gateway)." in result.output


def test_mark_read_and_unread(monkeypatch):
    calls = _stub(monkeypatch, post={"message": _message("read", "Read"), "done": "Marked (gateway)."})

    assert runner.invoke(app, ["inbox", "mark", MID, "--read"]).exit_code == 0
    assert runner.invoke(app, ["inbox", "mark", MID, "--unread"]).exit_code == 0

    assert [c[2] for c in calls] == [{"state": "read"}, {"state": "unread"}]


def test_mark_needs_exactly_one_flag(monkeypatch):
    calls = _stub(monkeypatch, post={"message": _message()})

    assert runner.invoke(app, ["inbox", "mark", MID]).exit_code == 2
    assert runner.invoke(app, ["inbox", "mark", MID, "--read", "--unread"]).exit_code == 2
    assert calls == []


def test_a_state_answer_without_the_message_never_reads_as_done(monkeypatch):
    _stub(monkeypatch, post={})

    result = runner.invoke(app, ["inbox", "delete", MID])

    assert result.exit_code == 1
    assert "Archived" not in result.output


def test_a_state_answer_without_the_gateways_sentence_is_not_reported_in_words_of_its_own(monkeypatch):
    _stub(monkeypatch, post={"message": _message("archived", "Archived")})

    result = runner.invoke(app, ["inbox", "delete", MID])

    assert result.exit_code == 1
    assert "without saying what it did" in result.output


def test_mark_help_says_marking_an_archived_message_puts_it_back():
    result = runner.invoke(app, ["inbox", "mark", "--help"], env={"COLUMNS": "200"})

    assert result.exit_code == 0
    assert "puts it back in the inbox" in " ".join(result.output.split())


# ---------- reply ----------

def test_reply_posts_the_text_and_prints_where_it_went(monkeypatch, plain):
    answer = {"reply": {"id": "r-1", "label": "Sent to 412 Release - Developer. Written by an agent: session 114"},
              "message": _message(), "done": "Replied, as this session (gateway)."}
    calls = _stub(monkeypatch, post=answer)

    result = runner.invoke(app, ["inbox", "reply", MID, "Yes, tag it."])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/inbox/{MID}/reply", {"text": "Yes, tag it."})]
    assert "Written by an agent: session 114" in plain(result.output)
    assert "Replied, as this session (gateway)." in plain(result.output)


def test_reply_to_factory_sends_to_factory(monkeypatch):
    calls = _stub(monkeypatch, post={"reply": {"id": "r-1", "label": "Left"}, "message": _message()})

    result = runner.invoke(app, ["inbox", "reply", MID, "Hello", "--to-factory"])

    assert result.exit_code == 0, result.output
    assert calls[0][2] == {"text": "Hello", "to": "factory"}


def test_reply_to_an_ended_session_prints_the_offers(monkeypatch, plain):
    refusal = inbox_ops.gateway.GatewayError(
        "250 Website - Writer has ended, so it cannot read a reply.", 409,
        {"code": "session_ended", "offers": [{"kind": "factory", "label": "Leave this reply in the Factory inbox of Booking"}]})
    _stub(monkeypatch, refuse=refusal)

    result = runner.invoke(app, ["inbox", "reply", MID, "Make it shorter."])

    assert result.exit_code == 1
    out = plain(result.output)
    assert "has ended" in out
    assert "offer: Leave this reply in the Factory inbox of Booking" in out
    assert "--to-factory" in out


def test_reply_empty_is_a_usage_error(monkeypatch):
    calls = _stub(monkeypatch, post={})

    assert runner.invoke(app, ["inbox", "reply", MID, "  "]).exit_code == 2
    assert calls == []


# ---------- factory inbox send ----------

def test_factory_send_posts_to_this_sessions_factory_route(monkeypatch, plain):
    answer = {"message": {
        "id": "f-1", "factory": "booking", "text": "Check Friday.", "stateLabel": "Sent", "surface": "session",
        "fromLabel": 'From an agent: session 114 "Factory Inbox - Delivery Lead"',
        "trail": [{"step": "sent", "label": 'Left by an agent: session 114 "Factory Inbox - Delivery Lead", 2026-11-07 14:02 UTC'}],
    }}
    calls = _stub(monkeypatch, post=answer)

    result = runner.invoke(app, ["factory", "inbox", "send", "booking", "Check Friday."])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/factories/booking/inbox", {"text": "Check Friday."})]
    out = plain(result.output)
    assert "From an agent: session 114" in out
    assert "cc-devthrottle factory inbox thread booking" in out


def test_factory_send_refused_for_its_own_seat_says_why(monkeypatch):
    _stub(monkeypatch, refuse=inbox_ops.gateway.GatewayError(
        "This session is the boss seat of Booking, and a factory's own seats do not leave messages in its own Factory inbox.",
        403, {"code": "own_factory"}))

    result = runner.invoke(app, ["factory", "inbox", "send", "booking", "Loop"])

    assert result.exit_code == 1
    assert "own seats do not leave messages" in result.output


def test_factory_send_empty_text_or_no_session_sends_nothing(monkeypatch):
    calls = _stub(monkeypatch, post={})
    assert runner.invoke(app, ["factory", "inbox", "send", "booking", " "]).exit_code == 2

    calls = _stub(monkeypatch, post={}, session=None)
    result = runner.invoke(app, ["factory", "inbox", "send", "booking", "Hi"])
    assert result.exit_code == 1
    assert calls == []


# ---------- --body-file (the Windows shim cuts a multi-line argument at its first line break) ----------

MULTI = "First line.\nSecond line.\n\nFourth line, after a blank one.\n"


def _sent_answer():
    return {"message": {"id": "m-1", "subject": "s", "stateLabel": "Unread", "from": {"label": "x"}, "links": []},
            "reply": {"id": "r-1", "label": "Sent"}}


def test_send_body_file_carries_every_line(monkeypatch, tmp_path):
    body = tmp_path / "body.txt"
    body.write_text(MULTI, encoding="utf-8")
    calls = _stub(monkeypatch, post=_sent_answer())

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body-file", str(body)])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/inbox", {"subject": "s", "body": MULTI})]


def test_send_body_file_dash_reads_standard_input(monkeypatch):
    calls = _stub(monkeypatch, post=_sent_answer())

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body-file", "-"], input=MULTI)

    assert result.exit_code == 0, result.output
    assert calls[0][2]["body"] == MULTI


def test_reply_body_file_carries_every_line(monkeypatch, tmp_path):
    body = tmp_path / "reply.txt"
    body.write_text(MULTI, encoding="utf-8")
    calls = _stub(monkeypatch, post=_sent_answer())

    result = runner.invoke(app, ["inbox", "reply", MID, "--body-file", str(body)])

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/inbox/{MID}/reply", {"text": MULTI})]


def test_factory_send_body_file_dash_reads_standard_input(monkeypatch):
    answer = {"message": {"id": "f-1", "factory": "booking", "stateLabel": "Sent", "fromLabel": "From an agent", "trail": []}}
    calls = _stub(monkeypatch, post=answer)

    result = runner.invoke(app, ["factory", "inbox", "send", "booking", "--body-file", "-"], input=MULTI)

    assert result.exit_code == 0, result.output
    assert calls == [("POST", f"sessions/{ME}/factories/booking/inbox", {"text": MULTI})]


def test_body_file_and_inline_text_together_or_neither_is_a_usage_error(monkeypatch, tmp_path):
    body = tmp_path / "b.txt"
    body.write_text("x", encoding="utf-8")
    calls = _stub(monkeypatch, post=_sent_answer())

    assert runner.invoke(app, ["inbox", "send", "--subject", "s", "--body", "b", "--body-file", str(body)]).exit_code == 2
    assert runner.invoke(app, ["inbox", "send", "--subject", "s"]).exit_code == 2
    assert runner.invoke(app, ["inbox", "reply", MID, "hi", "--body-file", str(body)]).exit_code == 2
    assert runner.invoke(app, ["inbox", "reply", MID]).exit_code == 2
    assert runner.invoke(app, ["factory", "inbox", "send", "booking", "hi", "--body-file", str(body)]).exit_code == 2
    assert runner.invoke(app, ["factory", "inbox", "send", "booking"]).exit_code == 2
    assert calls == []


def test_body_file_that_does_not_exist_is_a_usage_error_that_names_it(monkeypatch, tmp_path):
    calls = _stub(monkeypatch, post=_sent_answer())
    missing = tmp_path / "nope.txt"

    result = runner.invoke(app, ["inbox", "send", "--subject", "s", "--body-file", str(missing)])

    assert result.exit_code == 2
    assert "nope.txt" in result.output
    assert calls == []


def test_help_tells_a_script_to_use_body_file_for_more_than_one_line(plain):
    for command in (["inbox", "send"], ["inbox", "reply"], ["factory", "inbox", "send"]):
        result = runner.invoke(app, [*command, "--help"], env={"COLUMNS": "200"})
        assert result.exit_code == 0
        help_text = plain(result.output)
        assert "--body-file" in help_text
        assert "first line break" in " ".join(help_text.split())

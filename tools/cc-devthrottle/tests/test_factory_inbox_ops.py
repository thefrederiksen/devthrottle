"""`cc-devthrottle factory inbox` (Factory Inbox mission, phase 1, Task B).

These answer from a REAL local HTTP server, so the shared transport and its error mapping run as they do against a
Gateway. The server answers per route, so a test proves which route was called, with which body, and that a refusal
made on this side never reaches the Gateway at all. The answers are in the FINAL JSON contract of the phase 1 plan.

What matters, from docs/axi-standard.md and the phase 1 plan:
- `--json` prints the Gateway's answer unchanged, and every filter (`--since`) reaches the Gateway in `--json` too.
- An empty list says `count: 0`; an answer WITHOUT the list is an error, never "none".
- Ids and factory names are never cut; long text is cut with a hint, and `--full` shows it whole.
- A flag that would be ignored is refused (exit 2) before any call: `--session` off `delegated`, `--why` off the
  three that need it, ids to `bundle` without `--hand`, a merge naming a message not being received.
- The seat commands act as THIS session (CC_SESSION_ID) and never name the factory.
- A change is reported from what the Gateway answered, never from what was asked.
"""

import json
import sys
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()

SID = "be49930d-2fb8-4fec-be56-e083659f98bb"
BOSS_CHILD = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5"
LONG_ID = "a" * 36 + "-factory-inbox-message-id-that-is-long"
SESSION_ROUTE = f"/sessions/{SID}/factory-inbox"


class _Gateway:
    """A local HTTP server answering each (method, path) from a table, keeping every call. A route not in the table
    answers 404 with no body - what a Gateway without the route says."""

    def __init__(self, routes):
        self.routes = routes
        self.calls = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                parsed = urllib.parse.urlsplit(self.path)
                owner.calls.append({
                    "method": self.command,
                    "path": parsed.path,
                    "query": urllib.parse.parse_qs(parsed.query),
                    "body": json.loads(raw) if raw else None,
                })
                status, body = owner.routes.get((self.command, parsed.path), (404, None))
                payload = json.dumps(body).encode("utf-8") if body is not None else b""
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = _answer
            do_POST = _answer

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def gateway(monkeypatch):
    servers = []
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "test-key")
    monkeypatch.setenv("CC_SESSION_ID", SID)

    def start(routes):
        server = _Gateway(routes)
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        return server

    yield start
    for server in servers:
        server.close()


def _run(*args):
    return runner.invoke(app, ["factory", "inbox", *args])


def _message(mid="m1", text="Add a dark mode to the settings page.", state="Received", decision=None):
    """A message in the final contract's shape."""
    return {
        "id": mid, "factory": "warmforward", "text": text, "rawText": None, "note": "bad", "noteLabel": "Bad",
        "source": "typed", "surface": "phone", "sentAtUtc": "2026-11-04T09:00:00Z", "state": state.lower(),
        "stateLabel": state, "mergedInto": None, "links": [], "bundleId": None, "decision": decision,
        "trail": [{"step": "sent", "label": "Sent 09:00", "atUtc": "2026-11-04T09:00:00Z"},
                  {"step": state.lower(), "label": f"{state} 09:01", "atUtc": "2026-11-04T09:01:00Z"}],
    }


def _decision(kind, label, why=None):
    return {"kind": kind, "label": label, "why": why, "sessionId": None, "todoId": None,
            "decidedBySessionId": SID, "decidedAtUtc": "2026-11-04T09:10:00Z"}


def _bundle(bid, items):
    return {"id": bid, "factory": "warmforward", "handedBySessionId": SID, "handedAtUtc": "2026-11-04T09:05:00Z",
            "count": len(items), "items": items}


def _block(output, name, end_marker):
    return output[output.index(f"{name}["):output.index(end_marker)].strip()


# ---------------------------------------------------------------------------------------------------
# list
# ---------------------------------------------------------------------------------------------------

LIST = {"count": 3, "factories": [
    {"factory": "warmforward", "title": "WarmForward", "waiting": 2,
     "lastMessage": _message("m1", "Ship the summary", "With the Boss")},
    {"factory": "website-factory-with-a-long-name", "title": "Website, the factory", "waiting": 1,
     "lastMessage": _message("m2", "x" * 200, "Sent")},
    {"factory": "quiet", "title": "Quiet", "waiting": 0, "lastMessage": None},
]}


def test_list_json_is_the_gateway_answer_unchanged(gateway):
    server = gateway({("GET", "/gateway/factory/inbox"): (200, LIST)})
    result = _run("list", "--json")
    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == LIST
    assert [c["path"] for c in server.calls] == ["/gateway/factory/inbox"]


def test_list_plain_reads_back_every_factory_whole_with_counts(gateway):
    gateway({("GET", "/gateway/factory/inbox"): (200, LIST)})
    result = _run("list")
    assert result.exit_code == 0, result.output
    assert "count: 3" in result.output
    assert "waiting: 3" in result.output
    fields, rows = axi_output.parse_list(_block(result.output, "factories", "lastText cut"), "factories")
    assert fields == ["factory", "waiting", "lastState", "lastText"]
    assert [r["factory"] for r in rows] == ["warmforward", "website-factory-with-a-long-name", "quiet"]
    assert rows[0]["lastState"] == "With the Boss"
    assert rows[2]["lastState"] is None and rows[2]["lastText"] is None


def test_list_title_with_a_comma_reads_back_exactly(gateway):
    gateway({("GET", "/gateway/factory/inbox"): (200, LIST)})
    result = _run("list", "--fields", "factory,title")
    assert result.exit_code == 0, result.output
    _, rows = axi_output.parse_list(_block(result.output, "factories", "help["), "factories")
    assert rows[1]["title"] == "Website, the factory"


def test_list_cuts_long_text_with_a_hint_and_full_shows_it_whole(gateway):
    gateway({("GET", "/gateway/factory/inbox"): (200, LIST)})
    cut = _run("list", "--fields", "factory,lastText")
    assert cut.exit_code == 0, cut.output
    assert "x" * 200 not in cut.output
    assert "(200 chars)" in cut.output
    assert "lastText cut at 80 characters in 1 rows - use --full" in cut.output
    whole = _run("list", "--fields", "factory,lastText", "--full")
    assert "x" * 200 in whole.output
    assert "use --full" not in whole.output


def test_list_empty_says_count_zero(gateway):
    gateway({("GET", "/gateway/factory/inbox"): (200, {"count": 0, "factories": []})})
    result = _run("list")
    assert result.exit_code == 0, result.output
    assert "count: 0" in result.output


def test_list_answer_without_the_list_is_an_error_not_none(gateway):
    gateway({("GET", "/gateway/factory/inbox"): (200, {"count": 0})})
    result = _run("list")
    assert result.exit_code == 1
    assert "will not report that as none" in result.output
    assert "count: 0" not in result.output


def test_list_unknown_field_and_fields_with_json_are_usage_errors(gateway):
    server = gateway({("GET", "/gateway/factory/inbox"): (200, LIST)})
    unknown = _run("list", "--fields", "factory,unread")
    assert unknown.exit_code == 2
    assert "unread" in unknown.output and "Valid fields" in unknown.output
    both = _run("list", "--fields", "factory", "--json")
    assert both.exit_code == 2
    assert server.calls == []


def test_a_gateway_without_the_route_is_named_as_older(gateway):
    gateway({})
    result = _run("list")
    assert result.exit_code == 1
    assert "older than this command" in result.output


def test_an_unknown_flag_fails_loudly(gateway):
    server = gateway({})
    result = _run("list", "--unread-only")
    assert result.exit_code == 2
    assert "--unread-only" in result.output
    assert server.calls == []


# ---------------------------------------------------------------------------------------------------
# thread, summary, todo
# ---------------------------------------------------------------------------------------------------

def test_thread_prints_the_gateway_trail_and_whole_ids(gateway):
    answer = {"factory": "warmforward", "title": "WarmForward", "count": 2,
              "messages": [_message(LONG_ID, "y" * 150, "Delegated"), _message("m2")]}
    server = gateway({("GET", "/gateway/factory/warmforward/inbox"): (200, answer)})
    result = _run("thread", "warmforward")
    assert result.exit_code == 0, result.output
    assert "factory: warmforward" in result.output
    _, rows = axi_output.parse_list(_block(result.output, "messages", "text cut"), "messages")
    assert rows[0]["id"] == LONG_ID
    assert rows[0]["state"] == "Delegated"
    assert rows[0]["trail"] == "Sent 09:00 - Delegated 09:01"
    assert "text cut at 80 characters in 1 rows - use --full" in result.output
    assert server.calls[0]["path"] == "/gateway/factory/warmforward/inbox"


def test_thread_fields_show_the_decision_and_note_labels(gateway):
    decided = _message("m1", "Rewrite it", "Decided", _decision("declined", "Declined", "Too big for one go."))
    gateway({("GET", "/gateway/factory/warmforward/inbox"): (200, {"factory": "warmforward", "title": "W",
                                                                   "count": 1, "messages": [decided]})})
    result = _run("thread", "warmforward", "--fields", "id,note,decision,why")
    assert result.exit_code == 0, result.output
    _, rows = axi_output.parse_list(_block(result.output, "messages", "help["), "messages")
    assert rows == [{"id": "m1", "note": "Bad", "decision": "Declined", "why": "Too big for one go."}]


def test_thread_json_unchanged_and_names_are_path_encoded(gateway):
    answer = {"factory": "a b", "title": "A B", "count": 0, "messages": []}
    server = gateway({("GET", "/gateway/factory/a%20b/inbox"): (200, answer)})
    result = _run("thread", "a b", "--json")
    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == answer
    assert server.calls[0]["path"] == "/gateway/factory/a%20b/inbox"


def test_thread_refusal_carries_the_gateway_sentence(gateway):
    gateway({("GET", "/gateway/factory/old/inbox"): (409, {"error": "Factory 'old' is archived; it has no inbox.",
                                                           "code": "factory-archived"})})
    result = _run("thread", "old")
    assert result.exit_code == 1
    assert "Factory 'old' is archived; it has no inbox." in result.output


SUMMARY = {"factory": "warmforward", "sinceUtc": "2026-11-04T08:00:00Z", "untilUtc": "2026-11-04T12:00:00Z",
           "count": 2, "line": "Two messages came in; one delegated, one declined.",
           "byDecision": [{"kind": "delegated", "label": "Delegated", "count": 1},
                          {"kind": "declined", "label": "Declined", "count": 1}],
           "items": [_message("m1", "Add dark mode", "Decided", _decision("delegated", "Delegated")),
                     _message("m2", "Rewrite it all", "Decided",
                              _decision("declined", "Declined", "Too big for one go."))]}


def test_summary_since_reaches_the_gateway_in_json_too(gateway):
    server = gateway({("GET", "/gateway/factory/warmforward/inbox/summary"): (200, SUMMARY)})
    result = _run("summary", "warmforward", "--since", "2026-11-04T08:00:00Z", "--json")
    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == SUMMARY
    assert server.calls[0]["query"] == {"since": ["2026-11-04T08:00:00Z"]}


def test_summary_plain_shows_the_gateway_line_counts_and_decisions(gateway):
    gateway({("GET", "/gateway/factory/warmforward/inbox/summary"): (200, SUMMARY)})
    result = _run("summary", "warmforward")
    assert result.exit_code == 0, result.output
    assert "from: 2026-11-04T08:00:00Z to: 2026-11-04T12:00:00Z" in result.output
    assert "Two messages came in; one delegated, one declined." in result.output
    assert "by decision: Delegated 1, Declined 1" in result.output
    assert "count: 2" in result.output
    assert "m1,Decided,Delegated,,Add dark mode" in result.output
    assert "m2,Decided,Declined,Too big for one go.,Rewrite it all" in result.output


def test_summary_without_the_decision_counts_is_an_error(gateway):
    broken = {k: v for k, v in SUMMARY.items() if k != "byDecision"}
    gateway({("GET", "/gateway/factory/warmforward/inbox/summary"): (200, broken)})
    result = _run("summary", "warmforward")
    assert result.exit_code == 1


def test_summary_blank_since_is_a_usage_error(gateway):
    server = gateway({})
    result = _run("summary", "warmforward", "--since", " ")
    assert result.exit_code == 2
    assert server.calls == []


def test_todo_lists_items_and_empty_says_count_zero(gateway):
    gateway({("GET", "/gateway/factory/warmforward/todo"): (200, {"factory": "warmforward", "count": 1, "items": [
        {"id": "t1", "text": "Look at the summary layout", "fromMessageId": "m1", "addedBySessionId": SID,
         "addedAtUtc": "2026-11-04T10:00:00Z"}]}),
        ("GET", "/gateway/factory/empty/todo"): (200, {"factory": "empty", "count": 0, "items": []})})
    one = _run("todo", "warmforward")
    assert one.exit_code == 0, one.output
    assert "t1,2026-11-04T10:00:00Z,m1,Look at the summary layout" in one.output
    none = _run("todo", "empty")
    assert none.exit_code == 0
    assert "count: 0" in none.output


# ---------------------------------------------------------------------------------------------------
# Seat commands: new, receive, bundle
# ---------------------------------------------------------------------------------------------------

def test_new_acts_as_this_session_and_never_names_the_factory(gateway):
    answer = {"factory": "warmforward", "count": 1, "messages": [_message("m1", state="Sent")]}
    server = gateway({("GET", SESSION_ROUTE): (200, answer)})
    result = _run("new")
    assert result.exit_code == 0, result.output
    assert server.calls[0]["path"] == SESSION_ROUTE
    assert server.calls[0]["query"] == {}
    assert "receive <id>" in result.output


def test_new_with_nothing_new_says_count_zero_and_offers_session_done(gateway):
    gateway({("GET", SESSION_ROUTE): (200, {"factory": "warmforward", "count": 0, "messages": []})})
    result = _run("new")
    assert result.exit_code == 0, result.output
    assert "count: 0" in result.output
    assert "cc-devthrottle session done" in result.output


def test_seat_command_outside_a_session_refuses_without_a_call(gateway, monkeypatch):
    server = gateway({("GET", SESSION_ROUTE): (200, {"factory": "warmforward", "count": 0, "messages": []})})
    monkeypatch.delenv("CC_SESSION_ID")
    result = _run("new")
    assert result.exit_code == 1
    assert "CC_SESSION_ID is not set" in result.output
    assert server.calls == []


def test_receive_sends_ids_merges_and_links_in_the_agreed_shape(gateway):
    answer = {"factory": "warmforward", "count": 2, "received": [_message("m1"), _message("m2")]}
    server = gateway({("POST", f"{SESSION_ROUTE}/receive"): (200, answer)})
    result = _run("receive", "m1", "m2", "--merge", "m2:m1", "--link", "m1:run:https://example.test/run/7")
    assert result.exit_code == 0, result.output
    assert server.calls[0]["body"] == {
        "ids": ["m1", "m2"],
        "merges": [{"id": "m2", "into": "m1"}],
        "links": [{"id": "m1", "kind": "run", "ref": "https://example.test/run/7"}],
    }
    assert "Received." in result.output and "count: 2" in result.output


def test_receive_answer_without_the_received_list_is_an_error(gateway):
    gateway({("POST", f"{SESSION_ROUTE}/receive"): (200, {"factory": "warmforward", "count": 1})})
    result = _run("receive", "m1")
    assert result.exit_code == 1
    assert "Received." not in result.output


@pytest.mark.parametrize("args", [
    ["m1", "--merge", "m2:m1"],            # m2 is not being received
    ["m1", "m2", "--merge", "m1:m1"],      # into itself
    ["m1", "--merge", "m1"],               # not a pair
    ["m1", "--link", "m9:run:7"],          # m9 is not being received
    ["m1", "--link", "m1:run"],            # no ref
    ["m1", "m1"],                          # named twice
])
def test_receive_refuses_a_bad_merge_or_link_before_any_call(gateway, args):
    server = gateway({("POST", f"{SESSION_ROUTE}/receive"): (200, {"factory": "w", "count": 0, "received": []})})
    result = _run("receive", *args)
    assert result.exit_code == 2, result.output
    assert server.calls == []


def test_bundle_read_with_none_open_says_so(gateway):
    gateway({("GET", f"{SESSION_ROUTE}/bundle"): (200, {"factory": "warmforward", "count": 0, "bundles": []})})
    result = _run("bundle")
    assert result.exit_code == 0, result.output
    assert "open bundles: 0" in result.output
    assert "cc-devthrottle session done" in result.output


def test_bundle_read_answer_without_the_list_is_an_error(gateway):
    gateway({("GET", f"{SESSION_ROUTE}/bundle"): (200, {"factory": "warmforward", "count": 0})})
    result = _run("bundle")
    assert result.exit_code == 1
    assert "open bundles: 0" not in result.output


def test_bundle_read_shows_every_bundle_whole_and_the_decide_commands(gateway):
    answer = {"factory": "warmforward", "count": 2, "bundles": [
        _bundle("b1", [_message("m1", "z" * 150, "With the Boss")]),
        _bundle("b2", [_message("m2", "Second", "With the Boss"), _message("m3", "Third", "With the Boss")])]}
    gateway({("GET", f"{SESSION_ROUTE}/bundle"): (200, answer)})
    result = _run("bundle")
    assert result.exit_code == 0, result.output
    assert "open bundles: 2" in result.output
    assert "bundle: b1" in result.output and "bundle: b2" in result.output
    assert "z" * 150 in result.output
    assert "count: 1" in result.output and "count: 2" in result.output
    assert "decide <id> delegated --session <session-id>" in result.output


def test_bundle_hand_posts_the_ids_and_reports_the_answered_bundle(gateway):
    answer = {"bundle": _bundle("b7", [_message("m1"), _message("m2")])}
    server = gateway({("POST", f"{SESSION_ROUTE}/bundle"): (200, answer)})
    result = _run("bundle", "--hand", "m1", "m2")
    assert result.exit_code == 0, result.output
    assert server.calls[0]["body"] == {"ids": ["m1", "m2"]}
    assert "Handed to the Boss: bundle b7." in result.output


def test_bundle_hand_answer_without_a_bundle_id_is_not_reported_as_handed(gateway):
    gateway({("POST", f"{SESSION_ROUTE}/bundle"): (200, {"bundle": {"count": 1, "items": [_message()]}})})
    result = _run("bundle", "--hand", "m1")
    assert result.exit_code == 1
    assert "Handed" not in result.output


def test_bundle_ids_without_hand_are_refused(gateway):
    server = gateway({})
    result = _run("bundle", "m1")
    assert result.exit_code == 2
    assert "--hand" in result.output
    assert server.calls == []


def test_bundle_hand_without_ids_is_refused(gateway):
    server = gateway({})
    result = _run("bundle", "--hand")
    assert result.exit_code == 2
    assert server.calls == []


# ---------------------------------------------------------------------------------------------------
# decide
# ---------------------------------------------------------------------------------------------------

def _decided(label="Delegated", todo=None):
    answer = {"message": _message("m1", state="Decided", decision=_decision(label.lower(), label))}
    if todo is not None:
        answer["todo"] = todo
    return answer


@pytest.mark.parametrize("args,body", [
    (["delegated", "--session", BOSS_CHILD], {"kind": "delegated", "session": BOSS_CHILD}),
    (["noted"], {"kind": "noted"}),
    (["declined", "--why", "It would break the phone layout."], {"kind": "declined", "why": "It would break the phone layout."}),
    (["cannot", "--why", "No Director on that computer."], {"kind": "cannot", "why": "No Director on that computer."}),
    (["PushBack", "--why", "Already done last week."], {"kind": "pushback", "why": "Already done last week."}),
])
def test_decide_sends_the_agreed_body(gateway, args, body):
    server = gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, _decided())})
    result = _run("decide", "m1", *args)
    assert result.exit_code == 0, result.output
    assert server.calls[0]["body"] == body
    assert "Decided: message m1 is Delegated." in result.output


def test_decide_todo_reports_the_item_the_gateway_added(gateway):
    todo = {"id": "t9", "text": "Add dark mode", "fromMessageId": "m1", "addedBySessionId": SID,
            "addedAtUtc": "2026-11-04T09:10:00Z"}
    server = gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, _decided("To do", todo))})
    result = _run("decide", "m1", "todo")
    assert result.exit_code == 0, result.output
    assert server.calls[0]["body"] == {"kind": "todo"}
    assert "To-do item t9 added." in result.output


def test_decide_todo_without_the_item_is_not_reported_as_added(gateway):
    gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, _decided("To do"))})
    result = _run("decide", "m1", "todo")
    assert result.exit_code == 1
    assert "added." not in result.output


@pytest.mark.parametrize("args,words", [
    (["delegated"], "--session"),
    (["declined"], "--why"),
    (["cannot", "--why", "  "], "--why"),
    (["pushback"], "--why"),
    (["todo", "--session", BOSS_CHILD], "--session applies only to delegated"),
    (["noted", "--why", "because"], "--why applies only to"),
    (["delegated", "--session", BOSS_CHILD, "--why", "x"], "--why applies only to"),
    (["did-it-myself"], "Valid decisions"),
])
def test_decide_refuses_a_missing_or_ignored_flag_before_any_call(gateway, args, words):
    server = gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, _decided())})
    result = _run("decide", "m1", *args)
    assert result.exit_code == 2, result.output
    assert words in result.output
    assert server.calls == []


def test_decide_refusal_carries_the_gateway_sentence(gateway):
    sentence = "Session 6f1c2d3e was not started by this Boss, so it cannot be the delegation."
    gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (409, {"error": sentence, "code": "not-started-by-boss"})})
    result = _run("decide", "m1", "delegated", "--session", BOSS_CHILD)
    assert result.exit_code == 1
    assert sentence in result.output
    assert "Decided" not in result.output


def test_decide_answer_without_a_decision_label_is_not_reported_as_decided(gateway):
    gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, {"message": _message("m1", state="Decided")})})
    result = _run("decide", "m1", "noted")
    assert result.exit_code == 1
    assert "Decided:" not in result.output


def test_decide_json_is_the_gateway_answer_unchanged(gateway):
    gateway({("POST", f"{SESSION_ROUTE}/m1/decide"): (200, _decided("Noted"))})
    result = _run("decide", "m1", "noted", "--json")
    assert result.exit_code == 0, result.output
    assert json.loads(result.output) == _decided("Noted")


def test_there_is_no_send_command(gateway):
    result = _run("send", "warmforward", "hello")
    assert result.exit_code == 2

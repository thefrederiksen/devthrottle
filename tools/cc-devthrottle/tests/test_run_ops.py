"""`cc-devthrottle run result | resolve | problems` (Factory Control, step 1).

A scheduled run's session ends by saying how the run went. What these tests pin is the command's side of it: it
sends what was said, it closes the session only when the Gateway says to (after ok, never after a problem), it
refuses a problem with no reason before calling anything, and the problems list is the Gateway's - with `count: 0`
when there are none, and an answer with no list never read as "no problems". They answer from a real local HTTP
server, like the other command tests, so the whole path runs as it does against a Gateway.
"""

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402

runner = CliRunner()

SESSION = "71000000-0000-4000-8000-000000000001"
RUN = "6a000000-0000-4000-8000-000000000001"


class _Gateway:
    """A local HTTP server answering each path (without its query) from a table of (status, body), keeping every call."""

    def __init__(self, routes):
        self.routes = routes
        self.calls = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                owner.calls.append({"method": self.command, "path": self.path, "body": json.loads(raw) if raw else None})
                status, body = owner.routes.get(self.path.split("?")[0], (404, {"error": "no such route"}))
                payload = json.dumps(body).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = _answer
            do_POST = _answer

            def log_message(self, *_):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def paths(self):
        return [c["path"].split("?")[0] for c in self.calls]

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def gateway(monkeypatch):
    servers = []

    def start(routes):
        server = _Gateway(routes)
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        monkeypatch.setenv("CC_SESSION_ID", SESSION)
        return server

    yield start
    for server in servers:
        server.close()


def _result_answer(result, close, problem=None, reason=None):
    return {
        "run": {"runId": RUN, "result": result, "problem": problem, "resultReason": reason},
        "jobId": "cj_mail", "jobName": "Mail Desk", "closeSession": close,
        "text": "Recorded: the run of Mail Desk went ok. This session now closes itself." if close
        else "Recorded the problem on the run of Mail Desk. This session stays open for someone to look at.",
    }


DONE = f"/sessions/{SESSION}/request-deletion"


# ---------------------------------------------------------------------------------------------------
# run result
# ---------------------------------------------------------------------------------------------------


def test_result_ok_records_it_and_then_closes_this_session_like_session_done(gateway):
    gw = gateway({"/cron/runs/result": (200, _result_answer("ok", True)), DONE: (200, {"pendingDeletion": True})})

    out = runner.invoke(app, ["run", "result", "ok"])

    assert out.exit_code == 0, out.output
    assert gw.paths() == ["/cron/runs/result", DONE]
    assert gw.calls[0]["body"] == {"result": "ok"}
    assert gw.calls[1]["body"] == {"reason": "reported ok"}
    assert "session: flagged to close" in out.output


def test_result_problem_records_its_reason_and_leaves_the_session_open(gateway):
    gw = gateway({"/cron/runs/result": (200, _result_answer("problem", False, "reported", "the inbox would not load"))})

    out = runner.invoke(app, ["run", "result", "problem", "the inbox would not load"])

    assert out.exit_code == 0, out.output
    assert gw.paths() == ["/cron/runs/result"]  # no request-deletion: a problem stays open
    assert gw.calls[0]["body"] == {"result": "problem", "reason": "the inbox would not load"}
    assert "session: stays open" in out.output


def test_result_problem_without_a_reason_is_refused_before_anything_is_sent(gateway):
    gw = gateway({})

    out = runner.invoke(app, ["run", "result", "problem"])

    assert out.exit_code != 0
    assert gw.calls == []
    assert "one line why" in out.output


def test_result_an_unknown_word_is_refused(gateway):
    gw = gateway({})

    out = runner.invoke(app, ["run", "result", "fine"])

    assert out.exit_code != 0
    assert gw.calls == []


def test_result_from_a_session_no_schedule_started_reports_the_gateways_refusal_and_does_not_close(gateway):
    refusal = f"Session {SESSION} was not started by a schedule, so it has no run to report on."
    gw = gateway({"/cron/runs/result": (404, {"error": refusal})})

    out = runner.invoke(app, ["run", "result", "ok"])

    assert out.exit_code != 0
    assert "was not started by a schedule" in out.output
    assert DONE not in gw.paths()


def test_result_json_prints_the_answer_and_whether_it_closed(gateway):
    gateway({"/cron/runs/result": (200, _result_answer("ok", True)), DONE: (200, {"pendingDeletion": True})})

    out = runner.invoke(app, ["run", "result", "ok", "--json"])

    assert out.exit_code == 0, out.output
    parsed = json.loads(out.output)
    assert parsed["closed"] is True
    assert parsed["result"]["run"]["runId"] == RUN


def test_result_ok_whose_close_is_not_confirmed_fails_loudly(gateway):
    gateway({"/cron/runs/result": (200, _result_answer("ok", True)), DONE: (200, {})})

    out = runner.invoke(app, ["run", "result", "ok"])

    assert out.exit_code != 0
    assert "may still be open" in out.output


# ---------------------------------------------------------------------------------------------------
# run resolve
# ---------------------------------------------------------------------------------------------------


RESOLVED = {"runId": RUN, "jobId": "cj_mail", "jobName": "Mail Desk", "kindText": "reported a problem",
            "reason": "the inbox would not load", "state": "resolved", "resolvedBy": "you",
            "resolvedReason": "the host was down; it is back"}


def test_resolve_sends_the_reason_and_shows_who_resolved_it(gateway):
    gw = gateway({f"/cron/runs/{RUN}/resolve": (200, RESOLVED)})

    out = runner.invoke(app, ["run", "resolve", RUN, "the host was down; it is back"])

    assert out.exit_code == 0, out.output
    assert gw.calls[0]["body"] == {"reason": "the host was down; it is back"}
    assert "resolved by: you" in out.output
    assert RUN in out.output  # the full id, never cut short


def test_resolve_with_an_empty_reason_is_refused_before_anything_is_sent(gateway):
    gw = gateway({})

    out = runner.invoke(app, ["run", "resolve", RUN, "  "])

    assert out.exit_code != 0
    assert gw.calls == []


def test_resolve_reports_the_gateways_refusal(gateway):
    gateway({f"/cron/runs/{RUN}/resolve": (403, {"error": "a session of factory 'web' may not act on a run of 'mail'"})})

    out = runner.invoke(app, ["run", "resolve", RUN, "why"])

    assert out.exit_code != 0
    assert "may not act on a run" in out.output


def test_resolve_json_prints_the_gateways_answer(gateway):
    gateway({f"/cron/runs/{RUN}/resolve": (200, RESOLVED)})

    out = runner.invoke(app, ["run", "resolve", RUN, "why", "--json"])

    assert json.loads(out.output) == RESOLVED


# ---------------------------------------------------------------------------------------------------
# run problems
# ---------------------------------------------------------------------------------------------------


PROBLEM = {"runId": RUN, "jobId": "cj_mail", "jobName": "Mail Desk", "factory": "mail", "seat": "mail-desk",
           "shift": "night", "shiftText": "night shift, Sat 10 Oct, 00:00-08:00", "kind": "did-not-report",
           "kindText": "did not report", "reason": "its session ended without reporting how the run went",
           "sessionId": SESSION, "state": "open"}


def test_problems_lists_each_with_its_run_shift_and_session(gateway):
    gw = gateway({"/cron/problems": (200, {"count": 1, "state": "open", "problems": [PROBLEM]})})

    out = runner.invoke(app, ["run", "problems"])

    assert out.exit_code == 0, out.output
    assert "count: 1" in out.output
    assert RUN in out.output and SESSION in out.output
    assert "night shift, Sat 10 Oct, 00:00-08:00" in out.output
    assert "state=open" in gw.calls[0]["path"]


def test_problems_none_says_count_zero(gateway):
    gateway({"/cron/problems": (200, {"count": 0, "state": "open", "problems": []})})

    out = runner.invoke(app, ["run", "problems"])

    assert out.exit_code == 0, out.output
    assert "count: 0" in out.output


def test_problems_filters_reach_the_gateway_and_apply_to_json_too(gateway):
    answer = {"count": 0, "state": "all", "factory": "mail", "problems": []}
    gw = gateway({"/cron/problems": (200, answer)})

    out = runner.invoke(app, ["run", "problems", "--factory", "mail", "--all", "--json"])

    assert out.exit_code == 0, out.output
    assert json.loads(out.output) == answer
    assert "factory=mail" in gw.calls[0]["path"] and "state=all" in gw.calls[0]["path"]


def test_problems_an_answer_with_no_list_is_never_read_as_none(gateway):
    gateway({"/cron/problems": (200, {})})

    out = runner.invoke(app, ["run", "problems"])

    assert out.exit_code != 0
    assert "will not report that as none" in out.output


def test_problems_an_unknown_flag_fails(gateway):
    gateway({})

    out = runner.invoke(app, ["run", "problems", "--state", "open"])

    assert out.exit_code != 0

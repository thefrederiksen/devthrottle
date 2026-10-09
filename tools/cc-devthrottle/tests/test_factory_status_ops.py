"""`cc-devthrottle factory status` (issue #3685): the Factories screen as the owner sees it.

The one defect that matters here is a word of our own: the status word, the items and their row ids must
be the Gateway's, verbatim, so a boss and the owner read the same thing. These tests answer from a real
local HTTP server, like the other factory tests, so the whole path runs as it does against a Gateway.
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


class _Gateway:
    """A local HTTP server that answers every request with one fixed status and body, and keeps each call."""

    def __init__(self, status, body):
        self.status = status
        self.body = body
        self.calls = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                owner.calls.append({"method": self.command, "path": self.path, "body": json.loads(raw) if raw else None})
                payload = json.dumps(owner.body).encode("utf-8") if owner.body is not None else b""
                self.send_response(owner.status)
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

    def close(self):
        self.server.shutdown()
        self.server.server_close()

FAILED_ROW = "6f1c2b9e-0000-4000-8000-00000000abcd"
ASKED_ROW = "0a1b2c3d-0000-4000-8000-0000000000ee"

LIST = {
    "title": "Factories",
    "columns": ["Factory", "Waiting on you", "Status"],
    "rows": [
        {"id": "website-business", "title": "Website Business", "statusWord": "FAILING", "statusTone": "red",
         "statusReason": "Sender failed today 12:02: keep.page returned 500.",
         "statusLine": "Sender failed today 12:02: keep.page returned 500.", "waitingText": "1 question"},
        {"id": "warmforward", "title": "WarmForward", "statusWord": "RUNNING", "statusTone": "ok",
         "statusReason": "Nothing failed and nothing is waiting on you.", "statusLine": None, "waitingText": "-"},
    ],
    "truncatedText": None,
}

PAGE = {
    "id": "website-business", "title": "Website Business", "statusWord": "FAILING", "statusTone": "red",
    "statusReason": "Sender failed today 12:02: keep.page returned 500.",
    "failures": {
        "heading": "Failing",
        "items": [
            {"id": FAILED_ROW, "subject": "keep.page", "what": "keep.page returned 500.", "by": "Sender, today 12:02",
             "link": "https://example.test/run/1", "linkLabel": "Evidence", "handledLabel": "Handled", "note": None},
            {"id": None, "subject": None, "what": "The schedule could not start its run.", "by": "Mail Watcher, today 09:00",
             "link": None, "handledLabel": None, "note": "Not a row: it clears when the schedule next starts a run."},
        ],
    },
    "waiting": {
        "heading": "Waiting on you",
        "items": [
            {"id": ASKED_ROW, "word": "asked", "subject": "Spend", "what": "May I buy a second domain?",
             "by": "Boss, today 06:20", "link": None},
        ],
        "emptyText": None,
    },
    "truncatedText": None,
}

RUNNING_PAGE = {
    "id": "warmforward", "title": "WarmForward", "statusWord": "RUNNING", "statusTone": "ok",
    "statusReason": "Nothing failed and nothing is waiting on you.",
    "failures": None, "waiting": {"heading": "Waiting on you", "items": [], "emptyText": "Nothing is waiting on you."},
    "truncatedText": None,
}


@pytest.fixture
def gateway_answering(monkeypatch):
    servers = []

    def start(status, body):
        server = _Gateway(status, body)
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        return server

    yield start
    for server in servers:
        server.close()


# ---------------------------------------------------------------------------------------------------
# the list
# ---------------------------------------------------------------------------------------------------


def test_status_ReadsTheScreensListRoute_AndPrintsEveryFactorysWord(gateway_answering):
    server = gateway_answering(200, LIST)

    result = runner.invoke(app, ["factory", "status"])

    assert result.exit_code == 0, result.output
    assert server.calls[0]["method"] == "GET" and server.calls[0]["path"] == "/gateway/factories"
    assert "count: 2" in result.output
    assert "website-business,FAILING,1 question," in result.output
    assert "keep.page returned 500." in result.output
    assert "warmforward,RUNNING,-," in result.output
    assert "cc-devthrottle factory status --factory <id>" in result.output


def test_status_Json_IsTheGatewaysListUnchanged(gateway_answering):
    gateway_answering(200, LIST)

    result = runner.invoke(app, ["factory", "status", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.output) == LIST


def test_status_SwitchOff_SaysTheFeatureIsOff(gateway_answering):
    gateway_answering(404, {"error": "not found"})

    result = runner.invoke(app, ["factory", "status"])

    assert result.exit_code == 1
    assert "switched off" in result.stderr


def test_status_AnswerWithNoRows_IsAnErrorNotAllRunning(gateway_answering):
    gateway_answering(200, {"title": "Factories"})

    result = runner.invoke(app, ["factory", "status"])

    assert result.exit_code == 1
    assert "no list of factories" in result.stderr
    assert "RUNNING" not in result.output


# ---------------------------------------------------------------------------------------------------
# one factory's page
# ---------------------------------------------------------------------------------------------------


def test_status_Factory_ReadsThePageRoute_AndPrintsTheWordTheReasonAndEveryItemWithItsRowId(gateway_answering):
    server = gateway_answering(200, PAGE)

    result = runner.invoke(app, ["factory", "status", "--factory", "website-business"])

    assert result.exit_code == 0, result.output
    assert server.calls[0]["method"] == "GET" and server.calls[0]["path"] == "/gateway/factories/website-business"
    out = result.output
    assert "status: FAILING" in out
    assert "reason: Sender failed today 12:02: keep.page returned 500." in out
    assert "waiting on the owner: 1" in out
    assert "failing[2]" in out
    assert f"  {FAILED_ROW} | Sender, today 12:02 | keep.page | keep.page returned 500. | https://example.test/run/1" in out
    # A schedule that could not start is not a row: no id to correct, and the Gateway's note says how it clears.
    assert "  (no row) | Mail Watcher, today 09:00 | The schedule could not start its run. | Not a row: it clears when the schedule next starts a run." in out
    assert "waiting[1]" in out
    assert f"  {ASKED_ROW} | asked | Boss, today 06:20 | Spend | May I buy a second domain?" in out
    # The way to mark one handled is the record command with --corrects, right there in the next steps.
    assert "--outcome done --corrects <row id> --what" in out
    assert "cc-devthrottle factory status --factory website-business" in out


def test_status_Factory_Running_SaysNoItems_AndOffersTheList(gateway_answering):
    gateway_answering(200, RUNNING_PAGE)

    result = runner.invoke(app, ["factory", "status", "--factory", "warmforward"])

    assert result.exit_code == 0, result.output
    assert "status: RUNNING" in result.output
    assert "failing[0]" in result.output and "waiting[0]" in result.output
    assert "--corrects" not in result.output
    assert "cc-devthrottle factory status" in result.output


def test_status_Factory_Json_IsTheGatewaysPageUnchanged(gateway_answering):
    gateway_answering(200, PAGE)

    result = runner.invoke(app, ["factory", "status", "--factory", "website-business", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.output) == PAGE


def test_status_Factory_NotRegistered_ExitsNonZeroWithTheGatewaysSentence(gateway_answering):
    gateway_answering(404, {"error": "There is no registered factory 'nope'. A factory is registered with cc-devthrottle factory register."})

    result = runner.invoke(app, ["factory", "status", "--factory", "nope"])

    assert result.exit_code == 1
    assert "no registered factory 'nope'" in result.stderr
    assert "cc-devthrottle factory list" in result.stderr


def test_status_Factory_AnswerWithNoWord_IsAnErrorNotRunning(gateway_answering):
    gateway_answering(200, {"id": "website-business", "title": "Website Business"})

    result = runner.invoke(app, ["factory", "status", "--factory", "website-business"])

    assert result.exit_code == 1
    assert "no status word" in result.stderr
    assert "RUNNING" not in result.output


def test_status_Factory_IsUrlEscaped(gateway_answering):
    server = gateway_answering(200, RUNNING_PAGE)

    runner.invoke(app, ["factory", "status", "--factory", "a b/c"])

    assert server.calls[0]["path"] == "/gateway/factories/a%20b%2Fc"


def test_actions_ListFactoryStatus():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    actions = {a["id"]: a for a in json.loads(result.output)["actions"]}
    assert "factory-status" in actions
    assert actions["factory-status"]["mutatesState"] is False

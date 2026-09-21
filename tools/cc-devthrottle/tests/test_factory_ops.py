"""`cc-devthrottle factory record` and `factory activity` (Website Business Factory, product track).

THE EXIT CODE IS THE POINT. A business tool calls `factory record` before it acts and obeys its exit code,
so a zero exit when the row was not written is the defect that matters most. These tests answer from a
REAL local HTTP server, so the whole path - the shared transport, its error mapping, and this command's
handling - runs as it does against a Gateway. Nothing between the command and the socket is replaced.
"""

import json
import socket
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

ROW_ID = "6f1c2b9e-0000-4000-8000-00000000abcd"
RECORD = [
    "factory", "record", "--factory", "website-business", "--agent", "front-desk",
    "--outcome", "done", "--what", "Added an address to the remove-me list.",
]


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
                owner.calls.append({
                    "method": self.command,
                    "path": self.path,
                    "auth": self.headers.get("Authorization"),
                    "body": json.loads(raw) if raw else None,
                })
                payload = json.dumps(owner.body).encode("utf-8") if owner.body is not None else b""
                self.send_response(owner.status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = _answer
            do_POST = _answer

            def log_message(self, *args):
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
# factory record: a written row exits 0 and prints its id.
# ---------------------------------------------------------------------------------------------------

def test_record_Written_ExitsZeroAndPrintsTheId(gateway_answering):
    gw = gateway_answering(201, {"id": ROW_ID, "outcome": "done"})

    result = runner.invoke(app, RECORD + ["--subject", "Pine Valley Plumbing", "--corrects", "abc"])

    assert result.exit_code == 0, result.output
    assert result.stdout.splitlines()[0] == ROW_ID
    call = gw.calls[0]
    assert call["method"] == "POST"
    assert call["path"] == "/gateway/factory/activity"
    assert call["auth"] == "Bearer test-session-key"
    assert call["body"] == {
        "factory": "website-business", "factoryAgent": "front-desk", "outcome": "done",
        "what": "Added an address to the remove-me list.", "subject": "Pine Valley Plumbing",
        "correctsId": "abc",
    }


def test_record_Json_PrintsTheGatewaysRow(gateway_answering):
    gateway_answering(201, {"id": ROW_ID, "outcome": "done"})

    result = runner.invoke(app, RECORD + ["--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout)["id"] == ROW_ID


# ---------------------------------------------------------------------------------------------------
# factory record: every way the row is NOT written exits non-zero with the reason.
# ---------------------------------------------------------------------------------------------------

def test_record_GatewayRefusesTheRow_ExitsNonZeroWithItsSentence(gateway_answering):
    refusal = "'succeeded' is not a factory activity outcome. Allowed: started, allowed, asked."
    gateway_answering(400, {"error": refusal})

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "Not recorded:" in result.stderr
    assert refusal in result.stderr
    assert ROW_ID not in result.stdout


def test_record_SwitchOff_ExitsNonZeroAndSaysTheFeatureIsOff(gateway_answering):
    gateway_answering(404, None)

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "Not recorded:" in result.stderr
    assert "factory agents are switched off" in result.stderr
    assert "factoryAgents" in result.stderr


def test_record_GatewayFails_ExitsNonZero(gateway_answering):
    gateway_answering(500, {"error": "database unavailable"})

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "database unavailable" in result.stderr


def test_record_SuccessWithNoId_IsNotReportedAsWritten(gateway_answering):
    gateway_answering(201, {"outcome": "done"})

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "Not recorded:" in result.stderr


def test_record_GatewayUnreachable_ExitsNonZero(monkeypatch):
    # A port nothing listens on: bound, then closed, so the connection is refused.
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    monkeypatch.setenv("CC_GATEWAY_URL", f"http://127.0.0.1:{port}")

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "Not recorded:" in result.stderr
    assert "Cannot reach the Gateway" in result.stderr


def test_record_NoSessionKey_ExitsNonZero(gateway_answering, monkeypatch):
    gw = gateway_answering(201, {"id": ROW_ID})
    monkeypatch.delenv("CC_GATEWAY_SESSION_KEY")

    result = runner.invoke(app, RECORD)

    assert result.exit_code != 0
    assert "CC_GATEWAY_SESSION_KEY" in result.stderr
    assert gw.calls == []


# ---------------------------------------------------------------------------------------------------
# factory activity.
# ---------------------------------------------------------------------------------------------------

def test_activity_ReadsRowsWithTheFiltersInTheQuery(gateway_answering):
    gw = gateway_answering(200, {"rows": [{
        "id": ROW_ID, "factory": "website-business", "factoryAgent": "front-desk", "outcome": "blocked",
        "what": "Did not send: the address is on the remove-me list.", "subject": "Pine Valley Plumbing",
        "occurredUtc": "2026-09-21T08:00:00Z",
    }], "offset": 0, "limit": 5, "hasMore": False})

    result = runner.invoke(app, ["factory", "activity", "--factory", "website-business", "--outcome", "blocked",
                                 "--oldest-first", "-n", "5"])

    assert result.exit_code == 0, result.output
    assert "Did not send: the address is on the remove-me list." in result.stdout
    assert ROW_ID in result.stdout
    path = gw.calls[0]["path"]
    assert path.startswith("/gateway/factory/activity?")
    for part in ("factory=website-business", "outcome=blocked", "order=oldest", "limit=5"):
        assert part in path


def test_activity_SwitchOff_ExitsNonZeroAndSaysTheFeatureIsOff(gateway_answering):
    gateway_answering(404, None)

    result = runner.invoke(app, ["factory", "activity"])

    assert result.exit_code != 0
    assert "factory agents are switched off" in result.stderr


def test_activity_AnswerWithNoRows_IsAnErrorNotAnEmptyRecord(gateway_answering):
    gateway_answering(200, {"page": []})

    result = runner.invoke(app, ["factory", "activity"])

    assert result.exit_code != 0
    assert "no list of rows" in result.stderr


def test_actions_ListTheFactoryVerbs():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    ids = {action["id"] for action in json.loads(result.output)["actions"]}
    assert {"factory-record", "factory-activity"}.issubset(ids)

"""`cc-devthrottle factory email status|on|off|list|show` (Factory Control, step 4).

These answer from a REAL local HTTP server, so the shared transport and its error mapping run as they do against a
Gateway. What matters: the switch is only ever reported from a boolean the Gateway gave, a switch the Gateway did
not confirm never exits 0, filters reach the Gateway in --json too, and an empty list says count: 0.
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
    """A local HTTP server answering every request with one fixed status and body, keeping each call."""

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
            do_PUT = _answer

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


KEPT = {
    "id": "4b0f0c6e-0000-4000-8000-000000000001", "slotKey": "2026-10-11T08", "slot": "08:00", "kind": "morning-report",
    "boundaryUtc": "2026-10-11T12:00:00Z", "state": "sent", "sentUtc": "2026-10-11T12:05:02Z",
    "title": "Factories: 1 problem still open - Sunday 11 October", "factories": ["outreach", "research"],
    "problemCount": 1, "detail": None, "html": None,
}


def test_status_ReadsTheSwitch(gateway_answering):
    gw = gateway_answering(200, {"enabled": False})

    result = runner.invoke(app, ["factory", "email", "status"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["method"] == "GET" and gw.calls[0]["path"] == "/gateway/factory-email"
    assert "factory email: off" in result.stdout
    assert "cc-devthrottle factory email on" in result.stdout


def test_status_AnAnswerWithoutABoolean_Fails(gateway_answering):
    gateway_answering(200, {"enabled": "yes"})

    result = runner.invoke(app, ["factory", "email", "status"])

    assert result.exit_code != 0
    assert "will not guess" in result.output


def test_on_PutsEnabledTrue_AndSaysItIsOn(gateway_answering):
    gw = gateway_answering(200, {"enabled": True})

    result = runner.invoke(app, ["factory", "email", "on"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["method"] == "PUT"
    assert gw.calls[0]["body"] == {"enabled": True}
    assert "Switched the factory email on." in result.stdout


def test_off_TheGatewaySaysStillOn_FailsAndSaysSo(gateway_answering):
    gateway_answering(200, {"enabled": True})

    result = runner.invoke(app, ["factory", "email", "off", "--json"])

    assert result.exit_code != 0
    assert "not switched" in result.output


def test_on_AnOlderGateway_SaysSo(gateway_answering):
    gateway_answering(404, None)

    result = runner.invoke(app, ["factory", "email", "on"])

    assert result.exit_code != 0
    assert "older than this command" in result.output


def test_list_Empty_SaysCountZero(gateway_answering):
    gateway_answering(200, {"count": 0, "factory": None, "emails": []})

    result = runner.invoke(app, ["factory", "email", "list"])

    assert result.exit_code == 0, result.output
    assert "count: 0" in result.stdout


def test_list_FiltersReachTheGatewayInJson(gateway_answering):
    answer = {"count": 1, "factory": "research", "emails": [KEPT]}
    gw = gateway_answering(200, answer)

    result = runner.invoke(app, ["factory", "email", "list", "--factory", "research", "--kind", "morning-report",
                                 "-n", "5", "--all", "--json"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/gateway/factory-emails?factory=research&kind=morning-report&limit=5&state=all"
    assert json.loads(result.stdout) == answer


def test_list_ShowsTheWholeIdAndTheFactoriesCovered(gateway_answering):
    gateway_answering(200, {"count": 1, "factory": None, "emails": [KEPT]})

    result = runner.invoke(app, ["factory", "email", "list"])

    assert result.exit_code == 0, result.output
    assert "4b0f0c6e-0000-4000-8000-000000000001" in result.stdout
    assert "outreach,research" in result.stdout
    assert "Factories: 1 problem still open - Sunday 11 October" in result.stdout


def test_list_AnAnswerWithNoList_FailsRatherThanSayingNone(gateway_answering):
    gateway_answering(200, {"count": 0})

    result = runner.invoke(app, ["factory", "email", "list"])

    assert result.exit_code != 0


@pytest.mark.parametrize("args", [["--kind", "weekly"], ["-n", "0"], ["--count", "101"], ["--nope"]])
def test_list_BadArguments_AreRefusedBeforeAnyCall(gateway_answering, args):
    gw = gateway_answering(200, {"count": 0, "emails": []})

    result = runner.invoke(app, ["factory", "email", "list", *args])

    assert result.exit_code != 0
    assert gw.calls == []


def test_show_GetsOneEmailById_AndPrintsItsHtml(gateway_answering):
    gw = gateway_answering(200, dict(KEPT, html="<!doctype html><p>report</p>"))

    result = runner.invoke(app, ["factory", "email", "show", "4b0f0c6e-0000-4000-8000-000000000001"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/gateway/factory-emails/4b0f0c6e-0000-4000-8000-000000000001"
    assert "<!doctype html><p>report</p>" in result.stdout


def test_show_AnAnswerWithoutTheBody_Fails(gateway_answering):
    gateway_answering(200, KEPT)

    result = runner.invoke(app, ["factory", "email", "show", "4b0f0c6e-0000-4000-8000-000000000001"])

    assert result.exit_code != 0


def test_actions_ListTheFactoryEmailVerbs():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    ids = {action["id"] for action in json.loads(result.output)["actions"]}
    assert {"factory-email-status", "factory-email-on", "factory-email-off", "factory-email-list", "factory-email-show"} <= ids

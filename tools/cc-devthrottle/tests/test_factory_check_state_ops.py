"""`cc-devthrottle factory check-state show|reset` (Factory Definition mission, phase 5b).

What these tests are about: the route each verb calls and what it sends; that `reset` names the start by the owner key
the Gateway listed for that seat and number - never one it made up; that a seat with several checked starts needs
--start; that a session's refusal is printed as the Gateway wrote it with a non-zero exit; and that an empty list says
`count: 0` rather than printing nothing.

They answer from a real local HTTP server, as the definition command's tests do, so the whole path - the shared
transport, its error mapping, and this command's handling - runs as it does against a Gateway.
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

FACTORY = "warmforward"
ONLY_A_PERSON = ("Only a person resets a check's state. A session of the factory may read it with "
                 "'cc-devthrottle factory check-state show'; ask the owner to reset it.")


def start(seat, number, owner, content="", reset=False, name=None):
    return {"ownerKey": owner, "seat": seat, "start": number, "name": name or f"{FACTORY}-{seat}",
            "checkPath": "checks/new_mail.py", "content": content,
            "writtenUtc": "2026-10-15T08:00:00Z" if content or reset else None,
            "writtenBy": ("person soren" if reset else "dir-1") if content or reset else None,
            "runRef": None, "reset": reset}


LISTED = {"factory": FACTORY, "count": 3, "states": [
    start("frontdesk", 1, "trigger:11111111-1111-1111-1111-111111111111", content='{"last": "m-41"}'),
    start("sender", 1, "trigger:22222222-2222-2222-2222-222222222222", name="warmforward-sender"),
    start("sender", 2, "trigger:33333333-3333-3333-3333-333333333333", name="warmforward-sender-2"),
]}


class _Gateway:
    """A local HTTP server that answers each request with the next scripted (status, body), and keeps each call."""

    def __init__(self, answers):
        self.answers = list(answers)
        self.calls = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                owner.calls.append({"method": self.command, "path": self.path,
                                    "body": json.loads(raw) if raw else None})
                status, body = owner.answers.pop(0) if owner.answers else (500, {"error": "no answer scripted"})
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
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def gateway(monkeypatch):
    servers = []

    def begin(*answers):
        server = _Gateway(answers)
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        return server

    yield begin
    for server in servers:
        server.close()


def run(*args):
    return runner.invoke(app, ["factory", "check-state", *args])


def test_show_ListsEveryCheckedStart_WithItsState(gateway):
    gw = gateway((200, LISTED))

    result = run("show", FACTORY)

    assert result.exit_code == 0, result.stderr
    assert (gw.calls[0]["method"], gw.calls[0]["path"]) == ("GET", "/factory-definitions/warmforward/check-state")
    assert "count: 3" in result.stdout
    assert "frontdesk,1,warmforward-frontdesk,checks/new_mail.py" in result.stdout
    assert '"{\\"last\\": \\"m-41\\"}"' in result.stdout
    assert "(empty)" in result.stdout


def test_show_OneSeatAndStart_FiltersTheJsonToo(gateway):
    gateway((200, LISTED))

    result = run("show", FACTORY, "sender", "--start", "2", "--json")

    assert result.exit_code == 0, result.stderr
    answer = json.loads(result.stdout)
    assert answer["count"] == 1
    assert [s["ownerKey"] for s in answer["states"]] == ["trigger:33333333-3333-3333-3333-333333333333"]


def test_show_NoCheckedStart_SaysCountZero(gateway):
    gateway((200, {"factory": FACTORY, "count": 0, "states": []}))

    result = run("show", FACTORY)

    assert result.exit_code == 0, result.stderr
    assert "count: 0. warmforward has no checked start in its published definition." in result.stdout


def test_reset_TheSeatsOnlyStart_PostsItsOwnerKey(gateway):
    gw = gateway((200, LISTED), (200, start("frontdesk", 1, "trigger:11111111-1111-1111-1111-111111111111", reset=True)))

    result = run("reset", FACTORY, "frontdesk")

    assert result.exit_code == 0, result.stderr
    assert (gw.calls[1]["method"], gw.calls[1]["path"], gw.calls[1]["body"]) == (
        "POST", "/factory-definitions/warmforward/check-state/reset",
        {"ownerKey": "trigger:11111111-1111-1111-1111-111111111111"})
    assert "is now empty" in result.stdout


def test_reset_ASeatWithTwoCheckedStarts_NeedsStart_AndPostsNothing(gateway):
    gw = gateway((200, LISTED))

    result = run("reset", FACTORY, "sender")

    assert result.exit_code == 1
    assert "sender has 2 checked starts (1, 2); name one with --start." in result.stderr
    assert len(gw.calls) == 1


def test_reset_TheSecondStart_PostsThatStartsOwnerKey(gateway):
    gw = gateway((200, LISTED), (200, start("sender", 2, "trigger:33333333-3333-3333-3333-333333333333", reset=True)))

    result = run("reset", FACTORY, "sender", "--start", "2")

    assert result.exit_code == 0, result.stderr
    assert gw.calls[1]["body"] == {"ownerKey": "trigger:33333333-3333-3333-3333-333333333333"}


def test_reset_FromASession_IsRefusedWithTheGatewaysSentence_AndExitsNonZero(gateway):
    gateway((200, LISTED), (403, {"error": ONLY_A_PERSON, "outcome": "OnlyAPersonPublishes"}))

    result = run("reset", FACTORY, "frontdesk")

    assert result.exit_code == 1
    assert ONLY_A_PERSON in result.stderr
    assert "is now empty" not in result.stdout

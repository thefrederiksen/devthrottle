"""`cc-devthrottle factory memory` (Factory Memory mission, phase 3a, section 5.4).

TWO WRITERS ON ONE NOTE is what these tests are about. `set` must send the version this session last READ - taken
from the notes folder the Director put in place ($CC_FACTORY_MEMORY_DIR) and kept current by every read and write -
and on a stale write it must print the current text, say plainly that another writer got there first, and leave the
next `set` ready to be the merge.

They answer from a REAL local HTTP server, so the whole path - the shared transport, its error mapping, and this
command's handling - runs as it does against a Gateway. The versions record is the file the Director writes, in the
shape it writes it.
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

FACTORY = "website-factory"


def note(name, version, text, deleted=False, author="7f0c1a2b"):
    return {
        "factory": FACTORY, "name": name, "version": version, "text": None if deleted else text,
        "deleted": deleted, "authorKind": "session", "authorId": author, "writtenAtUtc": "2026-09-28T12:00:00Z",
    }


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
                owner.calls.append({
                    "method": self.command,
                    "path": self.path,
                    "auth": self.headers.get("Authorization"),
                    "body": json.loads(raw) if raw else None,
                })
                status, body = owner.answers.pop(0) if owner.answers else (500, {"error": "no answer scripted"})
                payload = json.dumps(body).encode("utf-8") if body is not None else b""
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = _answer
            do_PUT = _answer
            do_DELETE = _answer
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

    def start(*answers):
        server = _Gateway(answers)
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        return server

    yield start
    for server in servers:
        server.close()


@pytest.fixture
def memory_folder(tmp_path, monkeypatch):
    """The folder the Director put in place, with the versions record in the exact shape it writes."""

    def make(versions):
        folder = tmp_path / "factory-memory" / "session-1"
        folder.mkdir(parents=True)
        (folder / ".read-versions.json").write_text(
            json.dumps({"factory": FACTORY, "versions": versions}, indent=2), encoding="utf-8")
        monkeypatch.setenv("CC_FACTORY_MEMORY_DIR", str(folder))
        return folder

    return make


def recorded(folder):
    return json.loads((folder / ".read-versions.json").read_text(encoding="utf-8"))["versions"]


# ---------------------------------------------------------------------------------------------------
# set sends the version this session last read.
# ---------------------------------------------------------------------------------------------------

def test_set_SendsTheVersionTheDirectorDownloaded_AndRecordsTheNewOne(gateway, memory_folder):
    folder = memory_folder({"domains": 3})
    gw = gateway((200, note("domains", 4, "this registry wants a telephone number")))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "this registry wants a telephone number"])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert call["method"] == "PUT"
    assert call["path"] == "/factory-memory/notes/domains"
    assert call["auth"] == "Bearer test-session-key"
    assert call["body"] == {"text": "this registry wants a telephone number", "expectedVersion": 3}
    assert "Written: 'domains' is now version 4." in result.stdout
    assert recorded(folder)["domains"] == 4


def test_set_ANoteThisSessionNeverRead_SendsZero(gateway, memory_folder):
    memory_folder({"domains": 3})
    gw = gateway((200, note("deliverability", 1, "fix DMARC first")))

    result = runner.invoke(app, ["factory", "memory", "set", "deliverability", "fix DMARC first"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["expectedVersion"] == 0


def test_set_AfterAGet_SendsTheVersionTheGetShowed(gateway, memory_folder):
    folder = memory_folder({"domains": 3})
    gw = gateway((200, note("domains", 7, "newer text")), (200, note("domains", 8, "merged")))

    got = runner.invoke(app, ["factory", "memory", "get", "domains"])
    wrote = runner.invoke(app, ["factory", "memory", "set", "domains", "merged"])

    assert got.exit_code == 0, got.output
    assert "newer text" in got.stdout
    assert wrote.exit_code == 0, wrote.output
    assert gw.calls[1]["body"]["expectedVersion"] == 7
    assert recorded(folder)["domains"] == 8


def test_set_ExpectedVersionGivenByHand_WinsOverTheRecord(gateway, memory_folder):
    memory_folder({"domains": 3})
    gw = gateway((200, note("domains", 6, "x")))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "x", "--expected-version", "5"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["expectedVersion"] == 5


def test_set_TextFromStandardInput(gateway, memory_folder):
    memory_folder({})
    gw = gateway((200, note("domains", 1, "line one\nline two")))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "-"], input="line one\nline two")

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["text"] == "line one\nline two"


# ---------------------------------------------------------------------------------------------------
# A stale write: print the current text, say another writer got there first, and make the next set the merge.
# ---------------------------------------------------------------------------------------------------

STALE = {
    "error": "'deliverability' is at version 5, and this write was based on version 3",
    "outcome": "Stale",
    "current": note("deliverability", 5, "start, and the Scout's lesson\nsecond line", author="scout-01"),
}


def test_set_Stale_PrintsTheCurrentTextAndSaysAnotherWriterGotThereFirst(gateway, memory_folder):
    folder = memory_folder({"deliverability": 3})
    gateway((409, STALE))

    result = runner.invoke(app, ["factory", "memory", "set", "deliverability", "start, and the Sender's lesson"])

    assert result.exit_code == 1
    err = result.stderr
    assert "Not written: another writer got there first." in err
    assert "'deliverability' is now version 5; you last read version 3." in err
    assert "start, and the Scout's lesson" in err
    assert "second line" in err
    assert "by session scout-01" in err
    assert "Merge what you learned into that text and write again" in err
    # The version just SHOWN is now the one this session has read, so the next set is the merge.
    assert recorded(folder)["deliverability"] == 5


def test_set_Stale_ThenTheMergedWrite_SendsTheVersionItWasShown(gateway, memory_folder):
    memory_folder({"deliverability": 3})
    gw = gateway((409, STALE), (200, note("deliverability", 6, "merged")))

    first = runner.invoke(app, ["factory", "memory", "set", "deliverability", "the Sender's lesson"])
    second = runner.invoke(app, ["factory", "memory", "set", "deliverability", "merged"])

    assert first.exit_code == 1
    assert second.exit_code == 0, second.output
    assert [c["body"]["expectedVersion"] for c in gw.calls] == [3, 5]


def test_set_StaleOnADeletedNote_SaysItWasDeleted(gateway, memory_folder):
    memory_folder({"domains": 1})
    gateway((409, {"error": "stale", "outcome": "Stale", "current": note("domains", 2, None, deleted=True)}))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "x"])

    assert result.exit_code == 1
    assert "Version 2 deleted the note" in result.stderr


def test_set_ACapRefusal_IsNotCalledAnotherWriter(gateway, memory_folder):
    memory_folder({"domains": 1})
    gateway((409, {"error": "a note takes at most 16384 bytes", "outcome": "NoteTooLarge", "current": None}))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "x"])

    assert result.exit_code == 1
    assert "a note takes at most 16384 bytes" in result.stderr
    assert "another writer" not in result.stderr


# ---------------------------------------------------------------------------------------------------
# No folder: this command will not guess which version you read.
# ---------------------------------------------------------------------------------------------------

def test_set_NoFolderAndNoExpectedVersion_RefusesWithoutCallingTheGateway(gateway, monkeypatch):
    monkeypatch.delenv("CC_FACTORY_MEMORY_DIR", raising=False)
    gw = gateway()

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "x"])

    assert result.exit_code == 1
    assert "CC_FACTORY_MEMORY_DIR is not set" in result.stderr
    assert "--expected-version" in result.stderr
    assert gw.calls == []


def test_set_NoFolderButAnExpectedVersion_Writes(gateway, monkeypatch):
    monkeypatch.delenv("CC_FACTORY_MEMORY_DIR", raising=False)
    gw = gateway((200, note("domains", 2, "x")))

    result = runner.invoke(app, ["factory", "memory", "set", "domains", "x", "--expected-version", "1"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["expectedVersion"] == 1


# ---------------------------------------------------------------------------------------------------
# list, get, delete, history.
# ---------------------------------------------------------------------------------------------------

def test_list_PrintsTheNotes_AndRECORDSNOTHING_becauseItShowsOnlyOneLineOfEach(gateway, memory_folder):
    """THE REVIEW'S FINDING 2, PINNED. A plain listing shows one line of each note, cut at 70 characters, so it
    must NOT record those versions as read: doing so made the next `set` claim to have read a version nobody had
    seen, and the Gateway then replaced it - the lost lesson this mission exists to end, reached by two ordinary
    commands. The recorded versions must be exactly what they were before the listing ran."""
    folder = memory_folder({"domains": 1})
    gw = gateway((200, {
        "factory": FACTORY, "bytes": 30, "maxBytes": 524288, "maxNotes": 100,
        "notes": [note("domains", 4, "telephone number"), note("deliverability", 2, "fix DMARC first")],
    }))

    result = runner.invoke(app, ["factory", "memory", "list"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["method"] == "GET"
    assert gw.calls[0]["path"] == "/factory-memory/notes"
    assert "Factory website-factory: 2 notes, 30 of 524288 bytes" in result.stdout
    assert "domains  v4" in result.stdout
    assert str(folder) in result.stdout
    assert "does not count as having read them" in result.stdout
    assert recorded(folder) == {"domains": 1}


def test_listJson_RecordsEveryVersion_becauseItShowsTheWholeText(gateway, memory_folder):
    """--json prints the answer whole, so it IS a read."""
    folder = memory_folder({"domains": 1})
    gateway((200, {
        "factory": FACTORY, "bytes": 30, "maxBytes": 524288, "maxNotes": 100,
        "notes": [note("domains", 4, "telephone number"), note("deliverability", 2, "fix DMARC first")],
    }))

    result = runner.invoke(app, ["factory", "memory", "list", "--json"])

    assert result.exit_code == 0, result.output
    assert recorded(folder) == {"domains": 4, "deliverability": 2}


def test_list_AnAnswerWithNoNotesList_IsNotReportedAsEmpty(gateway, memory_folder):
    memory_folder({})
    gateway((200, {"factory": FACTORY}))

    result = runner.invoke(app, ["factory", "memory", "list"])

    assert result.exit_code == 1
    assert "no list of notes" in result.stderr


def test_list_ASessionInNoFactory_ShowsTheGatewaysRefusal(gateway, monkeypatch):
    monkeypatch.delenv("CC_FACTORY_MEMORY_DIR", raising=False)
    gateway((403, {"error": "this session is in no factory, so it has no memory to read or write"}))

    result = runner.invoke(app, ["factory", "memory", "list"])

    assert result.exit_code == 1
    assert "this session is in no factory, so it has no memory to read or write" in result.stderr


def test_get_ADeletedNote_SaysWhoDeletedItAndRecordsTheVersion(gateway, memory_folder):
    folder = memory_folder({"domains": 1})
    gateway((200, note("domains", 2, None, deleted=True)))

    result = runner.invoke(app, ["factory", "memory", "get", "domains"])

    assert result.exit_code == 0, result.output
    assert "'domains' was deleted in version 2 by session 7f0c1a2b" in result.stdout
    assert recorded(folder)["domains"] == 2


def test_get_TextThatIsNotAscii_IsEscapedNotLost(gateway, memory_folder):
    memory_folder({})
    gateway((200, note("domains", 1, "caf\u00e9 registry")))

    result = runner.invoke(app, ["factory", "memory", "get", "domains"])

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()
    assert "caf" in result.stdout and "registry" in result.stdout


def test_delete_SendsTheVersionLastReadInTheBody(gateway, memory_folder):
    folder = memory_folder({"domains": 4})
    gw = gateway((200, note("domains", 5, None, deleted=True)))

    result = runner.invoke(app, ["factory", "memory", "delete", "domains"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["method"] == "DELETE"
    assert gw.calls[0]["path"] == "/factory-memory/notes/domains"
    assert gw.calls[0]["body"] == {"expectedVersion": 4}
    assert "Deleted: 'domains' (version 5)" in result.stdout
    assert recorded(folder)["domains"] == 5


def test_delete_Stale_PrintsTheCurrentText(gateway, memory_folder):
    memory_folder({"deliverability": 3})
    gateway((409, STALE))

    result = runner.invoke(app, ["factory", "memory", "delete", "deliverability"])

    assert result.exit_code == 1
    assert "Not deleted: another writer got there first." in result.stderr
    assert "start, and the Scout's lesson" in result.stderr


def test_history_PrintsEveryVersionNewestFirst(gateway, memory_folder):
    memory_folder({})
    gw = gateway((200, {"factory": FACTORY, "name": "domains", "versions": [
        note("domains", 2, None, deleted=True), note("domains", 1, "first text")]}))

    result = runner.invoke(app, ["factory", "memory", "history", "domains"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-memory/notes/domains/history"
    lines = result.stdout.splitlines()
    assert lines[0].startswith("v2") and "(deleted)" in lines[0]
    assert lines[1].startswith("v1") and "first text" in lines[1]


def test_aNoteNameIsKeptAsOnePathSegment(gateway, memory_folder):
    memory_folder({})
    gw = gateway((200, note("a/b c", 1, "x")))

    result = runner.invoke(app, ["factory", "memory", "set", "a/b c", "x"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-memory/notes/a%2Fb%20c"

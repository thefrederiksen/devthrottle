"""`cc-devthrottle factory definition get|history|save|publish|restore|diff` (Factory Definition mission, phase 1).

What these tests are about: the route each verb calls and what it sends; that `save` sends every file under the
folder by its relative path with forward slashes, exactly as it is on disk, and the version its caller states - never
one it looked up for itself; that every refusal and every finding of the check is printed as the Gateway wrote it,
`file:line: message`, with a non-zero exit; that "what changes" is printed as the Gateway composed it; and that
`--json` keeps its shape and still honours `--file`.

They answer from a REAL local HTTP server, as the brief command's tests do, so the whole path - the shared transport,
its error mapping, and this command's handling - runs as it does against a Gateway.
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
ONLY_A_PERSON = ("Only a person publishes a factory's definition. A session of the factory may save a draft; "
                 "the owner reads it and publishes it.")

FILES = {
    "factory.yaml": "factory: warmforward\ntitle: WarmForward\ndefaults:\n  machine: SOREN_NORTH\n  agent: ClaudeCode\n",
    "seats/boss.yaml": "seat: boss\nname: Boss\nrole: Boss\nbrief: Run the factory.\n",
}


def version(n, state="draft", newest=None, published=None, files=None, author_kind="person", author="soren",
            note=None, restored_from=None, findings=None, changes=None):
    body = {
        "factory": FACTORY, "version": n, "state": state, "authorKind": author_kind, "authorId": author,
        "changeNote": note, "restoredFrom": restored_from, "writtenAtUtc": "2026-10-14T12:00:00Z",
        "publishedAtUtc": "2026-10-14T12:05:00Z" if state == "published" else None, "publishedBy": None,
        "contentBytes": 150, "fileCount": 2,
        "newestVersion": newest if newest is not None else n, "publishedVersion": published,
        "files": files, "findings": findings or [],
    }
    if changes is not None:
        body["changes"] = changes
    return body


CHANGES = {
    "summary": "1 seat changed",
    "nothingChanges": False,
    "firstPublish": False,
    "factory": [],
    "seats": [{"seat": "boss", "change": "changed", "sentence": "Seat Boss (boss) changed.",
               "details": ["machine SOREN_NORTH -> devlinux"],
               "briefDiff": {"added": 1, "removed": 1, "linesNotShown": 0, "note": None,
                             "lines": [{"kind": "removed", "marker": "-", "text": "Run the factory."},
                                       {"kind": "added", "marker": "+", "text": "Run the factory well."}]}}],
    "starts": [], "rules": [], "grants": [], "files": [],
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
def folder(tmp_path):
    root = tmp_path / "warmforward"
    for path, text in FILES.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(text.encode("utf-8"))
    return root


def run(*args):
    return runner.invoke(app, ["factory", "definition", *args])


# ---------------------------------------------------------------------------------------------------
# get
# ---------------------------------------------------------------------------------------------------

def test_get_ReadsThePublishedVersion_AndListsEveryFileWithItsSize(gateway):
    gw = gateway((200, version(3, state="published", published=3, files=FILES)))

    result = run("get", FACTORY)

    assert result.exit_code == 0, result.output
    assert (gw.calls[0]["method"], gw.calls[0]["path"]) == ("GET", "/factory-definitions/warmforward")
    assert "warmforward  version 3  published  written by person soren" in result.stdout
    assert "files[2]{path,bytes}:" in result.stdout
    assert f"  factory.yaml,{len(FILES['factory.yaml'])}" in result.stdout
    assert f"  seats/boss.yaml,{len(FILES['seats/boss.yaml'])}" in result.stdout
    # The summary never prints a file's text.
    assert "Run the factory." not in result.stdout


def test_get_Draft_And_Version_ReadTheirOwnRoutes(gateway):
    gw = gateway((200, version(4, newest=4, published=3, files=FILES)), (200, version(2, newest=4, published=3, files=FILES)))

    assert run("get", FACTORY, "--draft").exit_code == 0
    assert run("get", FACTORY, "--version", "2").exit_code == 0

    assert [c["path"] for c in gw.calls] == ["/factory-definitions/warmforward/draft",
                                             "/factory-definitions/warmforward/versions/2"]


def test_get_SaysWhenANewerDraftIsWaiting(gateway):
    gateway((200, version(3, state="published", newest=5, published=3, files=FILES)))

    result = run("get", FACTORY)

    assert "A newer version, 5, is waiting and is not published." in result.stdout


def test_get_File_PrintsThatOneFilesText(gateway):
    gateway((200, version(3, state="published", published=3, files=FILES)))

    result = run("get", FACTORY, "--file", "seats/boss.yaml")

    assert result.exit_code == 0, result.output
    assert result.stdout == FILES["seats/boss.yaml"]


def test_get_File_WithJson_PrintsOnlyThatFile_BecauseTheFilterAppliesToJsonToo(gateway):
    gateway((200, version(3, state="published", published=3, files=FILES)))

    result = run("get", FACTORY, "--file", "seats/boss.yaml", "--json")

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == {"factory": FACTORY, "version": 3, "path": "seats/boss.yaml",
                                         "text": FILES["seats/boss.yaml"]}


def test_get_AFileTheVersionDoesNotHold_ExitsNonZero_AndSaysHowManyItHolds(gateway):
    gateway((200, version(3, state="published", published=3, files=FILES)))

    result = run("get", FACTORY, "--file", "seats/nobody.yaml")

    assert result.exit_code == 1
    assert "version 3 of the definition of warmforward holds no file 'seats/nobody.yaml'; it holds 2 files" in result.stderr


def test_get_Json_PrintsTheGatewaysAnswerUnchanged(gateway):
    answer = version(3, state="published", published=3, files=FILES)
    gateway((200, answer))

    result = run("get", FACTORY, "--json")

    assert json.loads(result.stdout) == answer


def test_get_DraftAndVersionTogether_IsAUsageError_AndCallsNothing(gateway):
    gw = gateway()

    result = run("get", FACTORY, "--draft", "--version", "4")

    assert result.exit_code == 2
    assert gw.calls == []


def test_get_AFactoryWithNoDefinition_ExitsNonZeroWithTheGatewaysSentence(gateway):
    gateway((404, {"error": "warmforward has no definition on the Gateway", "newestVersion": 0}))

    result = run("get", FACTORY)

    assert result.exit_code == 1
    assert "warmforward has no definition on the Gateway" in result.stderr
    assert "factory definition save warmforward --dir <folder> --expected-version 0" in result.stderr


def test_get_AnAnswerWithNoFiles_IsNeverReportedAsAnEmptyDefinition(gateway):
    gateway((200, version(3, state="published", published=3, files=None)))

    result = run("get", FACTORY)

    assert result.exit_code == 1
    assert "carried no definition" in result.stderr


def test_get_AFactoryNameWithASlash_StaysOneSegmentOfThePath(gateway):
    gw = gateway((404, {"error": "no"}))

    run("get", "warm/forward")

    assert gw.calls[0]["path"] == "/factory-definitions/warm%2Fforward"


# ---------------------------------------------------------------------------------------------------
# history
# ---------------------------------------------------------------------------------------------------

def _page(versions, total=None, published=None, next_before=None):
    return {"factory": FACTORY, "publishedVersion": published, "newestVersion": versions[0]["version"] if versions else 0,
            "total": total if total is not None else len(versions), "versions": versions, "nextBefore": next_before}


def _row(n, state="draft", note=None, restored_from=None):
    v = version(n, state=state, note=note, restored_from=restored_from)
    for key in ("factory", "files", "findings", "newestVersion", "publishedVersion"):
        v.pop(key)
    return v


def test_history_ListsOnePage_MarksThePublishedOne_AndShowsNotesAndRestores(gateway):
    gw = gateway((200, _page([_row(3, "published", restored_from=1), _row(2, "published", note="second"), _row(1, "published")],
                             published=3)))

    result = run("history", FACTORY)

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-definitions/warmforward/history"
    assert "count: 3 of 3. Version 3 is the published definition." in result.stdout
    assert "v3,PUBLISHED NOW," in result.stdout
    assert '"restored from version 1."' in result.stdout
    assert '"second"' in result.stdout


def test_history_SendsThePageSizeAndTheStartingVersion_AndNamesTheNextPage(gateway):
    gw = gateway((200, _page([_row(7), _row(6)], total=12, next_before=6)))

    result = run("history", FACTORY, "--count", "2", "--before", "8")

    assert gw.calls[0]["path"] == "/factory-definitions/warmforward/history?limit=2&before=8"
    assert "Older versions follow, below version 6." in result.stdout
    assert "factory definition history warmforward --before 6" in result.stdout


def test_history_OfAFactoryWithNoDefinition_SaysCountZero(gateway):
    gateway((200, _page([])))

    result = run("history", FACTORY)

    assert result.exit_code == 0
    assert "count: 0. warmforward has no definition on the Gateway." in result.stdout


def test_history_Json_PrintsTheGatewaysPageUnchanged(gateway):
    page = _page([_row(1)])
    gateway((200, page))

    result = run("history", FACTORY, "--json")

    assert json.loads(result.stdout) == page


# ---------------------------------------------------------------------------------------------------
# save
# ---------------------------------------------------------------------------------------------------

def test_save_SendsEveryFileByItsRelativePath_ExactlyAsOnDisk_AndTheVersionAndNoteTheCallerStated(gateway, folder):
    (folder / "files").mkdir()
    (folder / "files" / "note.md").write_bytes(b"line one\r\nline two\r\n")
    gw = gateway((200, version(5, newest=5, published=4)))

    result = run("save", FACTORY, "--dir", str(folder), "--expected-version", "4", "--note", "move the boss")

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert (call["method"], call["path"]) == ("PUT", "/factory-definitions/warmforward/draft")
    assert call["body"]["expectedVersion"] == 4
    assert call["body"]["note"] == "move the boss"
    # Forward slashes on every platform, and the file's own line endings kept.
    assert call["body"]["content"] == {**FILES, "files/note.md": "line one\r\nline two\r\n"}
    assert "Saved: version 5 of the definition of warmforward is a draft" in result.stdout


def test_save_NeedsTheVersionTheCallerRead_AndNeverLooksItUp(gateway, folder):
    gw = gateway()

    result = run("save", FACTORY, "--dir", str(folder))

    assert result.exit_code == 2
    assert gw.calls == []


def test_save_RefusedByTheCheck_PrintsEveryFindingAsFileLineMessage_AndExitsNonZero(gateway, folder):
    gateway((422, {
        "error": "The definition was not saved: the check found 2 errors in the definition of warmforward. "
                 "Fix each one where it is and try again.",
        "outcome": "CheckFailed",
        "findings": [
            {"severity": "error", "file": "seats/boss.yaml", "line": 3, "message": "'colour' is not a key a seat takes."},
            {"severity": "error", "file": "seats/", "line": 0, "message": "A definition needs at least one seat."},
            {"severity": "notice", "file": "factory.yaml", "line": 4, "message": "no Director on SOREN_NORTH is connected now."},
        ],
    }))

    result = run("save", FACTORY, "--dir", str(folder), "--expected-version", "0")

    assert result.exit_code == 1
    assert "Error: The definition was not saved: the check found 2 errors" in result.stderr
    assert "errors[2]:" in result.stderr
    assert "  seats/boss.yaml:3: 'colour' is not a key a seat takes." in result.stderr
    assert "  seats/: A definition needs at least one seat." in result.stderr
    assert "notices[1]:" in result.stderr
    assert "  factory.yaml:4: no Director on SOREN_NORTH is connected now." in result.stderr
    assert "Saved" not in result.stdout


def test_save_Stale_IsNotSaved_AndSaysWhichVersionToReadAndSendNext(gateway, folder):
    gateway((409, {"error": "the definition of warmforward is at version 3, not 2; read it again, merge, and save once more",
                   "outcome": "Stale", "current": version(3)}))

    result = run("save", FACTORY, "--dir", str(folder), "--expected-version", "2")

    assert result.exit_code == 1
    assert "the definition of warmforward is at version 3, not 2" in result.stderr
    assert "Version 3 was written by person soren." in result.stderr
    assert "factory definition get warmforward --draft" in result.stderr
    assert "--expected-version 3" in result.stderr


def test_save_PrintsTheNoticesThatRodeAlongWithTheSuccess(gateway, folder):
    gateway((200, version(1, findings=[{"severity": "notice", "file": "seats/boss.yaml", "line": 0,
                                       "message": "whether SOREN_NORTH holds 'smtp' is not checked yet."}])))

    result = run("save", FACTORY, "--dir", str(folder), "--expected-version", "0")

    assert result.exit_code == 0, result.output
    assert "notices[1]:" in result.stdout
    assert "  seats/boss.yaml: whether SOREN_NORTH holds 'smtp' is not checked yet." in result.stdout


def test_save_AFolderThatIsNotThere_CallsNothing(gateway, tmp_path):
    gw = gateway()

    result = run("save", FACTORY, "--dir", str(tmp_path / "nowhere"), "--expected-version", "0")

    assert result.exit_code == 1
    assert "there is no folder at" in result.stderr
    assert gw.calls == []


def test_save_AFileThatIsNotText_IsNamed_AndNothingIsSent(gateway, folder):
    (folder / "files").mkdir()
    (folder / "files" / "logo.png").write_bytes(b"\x89PNG\r\n\x1a\n\xff\xfe")
    gw = gateway()

    result = run("save", FACTORY, "--dir", str(folder), "--expected-version", "0")

    assert result.exit_code == 1
    assert "the file files/logo.png could not be read as UTF-8 text" in result.stderr
    assert gw.calls == []


# ---------------------------------------------------------------------------------------------------
# publish and restore: a person's acts
# ---------------------------------------------------------------------------------------------------

def test_publish_FromASession_IsRefusedWithTheGatewaysSentence_AndExitsNonZero(gateway):
    gw = gateway((403, {"error": ONLY_A_PERSON, "outcome": "OnlyAPersonPublishes", "current": None}))

    result = run("publish", FACTORY, "--version", "4")

    assert result.exit_code == 1
    assert (gw.calls[0]["method"], gw.calls[0]["path"], gw.calls[0]["body"]) == (
        "POST", "/factory-definitions/warmforward/publish", {"version": 4})
    assert ONLY_A_PERSON in result.stderr
    assert "factory definition save warmforward --dir <folder>" in result.stderr
    assert "Published" not in result.stdout


def test_publish_PrintsWhatChanges_AsTheGatewayComposedIt(gateway):
    gateway((200, version(4, state="published", published=4, changes=CHANGES)))

    result = run("publish", FACTORY, "--version", "4")

    assert result.exit_code == 0, result.output
    assert "Published: version 4 of the definition of warmforward. Version 4 is now the published definition." in result.stdout
    assert "what changes: 1 seat changed" in result.stdout
    assert "  Seat Boss (boss) changed." in result.stdout
    assert "    machine SOREN_NORTH -> devlinux" in result.stdout
    assert "      - Run the factory." in result.stdout
    assert "      + Run the factory well." in result.stdout


def test_publish_RefusedByTheCheck_PrintsEveryFinding(gateway):
    gateway((422, {"error": "Version 2 was not published: the check found 1 error in the definition of warmforward. "
                            "Fix each one where it is and try again.",
                   "outcome": "CheckFailed",
                   "findings": [{"severity": "error", "file": "seats/boss.yaml", "line": 9,
                                 "message": "'browsers' is not a skill in this account's skill store."}]}))

    result = run("publish", FACTORY, "--version", "2")

    assert result.exit_code == 1
    assert "  seats/boss.yaml:9: 'browsers' is not a skill in this account's skill store." in result.stderr


def test_restore_SendsTheVersionAndTheVersionTheCallerRead(gateway):
    gw = gateway((200, version(6, state="published", published=6, restored_from=1, changes=CHANGES)))

    result = run("restore", FACTORY, "--version", "1", "--expected-version", "5")

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert (call["method"], call["path"], call["body"]) == (
        "POST", "/factory-definitions/warmforward/restore", {"version": 1, "expectedVersion": 5})
    assert "Restored: version 1 of the definition of warmforward. Version 6 is now the published definition." in result.stdout


def test_restore_FromASession_IsRefusedWithTheGatewaysSentence(gateway):
    sentence = ("Only a person publishes a factory's definition, and restoring an older version publishes a copy of it. "
                "A session of the factory may save the content it wants as a new draft; the owner publishes it.")
    gateway((403, {"error": sentence, "outcome": "OnlyAPersonPublishes", "current": None}))

    result = run("restore", FACTORY, "--version", "1")

    assert result.exit_code == 1
    assert sentence in result.stderr
    assert "Restored" not in result.stdout


# ---------------------------------------------------------------------------------------------------
# diff
# ---------------------------------------------------------------------------------------------------

def test_diff_SendsFromAndTo_AndPrintsWhatChangesVerbatim(gateway):
    gw = gateway((200, {"factory": FACTORY, "from": 2, "to": 5, "changes": CHANGES}))

    result = run("diff", FACTORY, "--from", "2", "--to", "5")

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-definitions/warmforward/diff?from=2&to=5"
    assert "warmforward: from version 2 to version 5." in result.stdout
    assert "what changes: 1 seat changed" in result.stdout
    assert "  Seat Boss (boss) changed." in result.stdout


def test_diff_WithNothingPublished_SaysSo(gateway):
    gw = gateway((200, {"factory": FACTORY, "from": None, "to": 1,
                        "changes": {**CHANGES, "summary": "Everything is new.", "seats": [], "firstPublish": True}}))

    result = run("diff", FACTORY)

    assert gw.calls[0]["path"] == "/factory-definitions/warmforward/diff"
    assert "warmforward: from nothing published to version 1." in result.stdout
    assert "what changes: Everything is new." in result.stdout


def test_diff_Json_PrintsTheGatewaysAnswerUnchanged(gateway):
    answer = {"factory": FACTORY, "from": 1, "to": 2, "changes": CHANGES}
    gateway((200, answer))

    result = run("diff", FACTORY, "--json")

    assert json.loads(result.stdout) == answer


def test_diff_AnAnswerWithNoChanges_IsNeverReportedAsNothingChanging(gateway):
    gateway((200, {"factory": FACTORY, "from": 1, "to": 2}))

    result = run("diff", FACTORY)

    assert result.exit_code == 1
    assert "carried no difference" in result.stderr


# ---------------------------------------------------------------------------------------------------
# every command carries this session's key, and an unknown flag fails
# ---------------------------------------------------------------------------------------------------

def test_everyCall_CarriesThisSessionsKey(gateway):
    gw = gateway((200, version(1, state="published", published=1, files=FILES)))

    run("get", FACTORY)

    assert gw.calls[0]["auth"] == "Bearer test-session-key"


@pytest.mark.parametrize("verb, required", [
    ("get", []), ("history", []), ("diff", []),
    # Every required option is passed, so the unknown flag is the only reason the command can refuse.
    ("save", ["--dir", "<folder>", "--expected-version", "0"]),
    ("publish", ["--version", "1"]),
    ("restore", ["--version", "1"]),
])
def test_anUnknownFlag_Fails_AndCallsNothing(gateway, folder, verb, required):
    gw = gateway()
    required = [str(folder) if a == "<folder>" else a for a in required]

    result = run(verb, FACTORY, *required, "--no-such-flag")

    assert result.exit_code == 2
    assert "No such option" in result.output
    assert "--no-such-flag" in result.output
    assert gw.calls == []

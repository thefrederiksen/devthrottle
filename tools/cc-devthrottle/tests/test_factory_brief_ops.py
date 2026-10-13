"""`cc-devthrottle factory brief get|history|set|publish|restore` (Factory Seats mission, phase 2a).

What these tests are about: the route each verb calls and what it sends; that `set` sends the version its caller
states and never one it looked up for itself; that a stale save says which version is newest and how to read it; and
that a session's publish is answered with the Gateway's own sentence and a non-zero exit, never a success.

They answer from a REAL local HTTP server, so the whole path - the shared transport, its error mapping, and this
command's handling - runs as it does against a Gateway.
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
SEAT = "boss"
WHERE = ["--factory", FACTORY, "--seat", SEAT]
ONLY_A_PERSON = ("Only a person publishes a seat's brief. A session of the factory may save a draft; "
                 "the owner reads it and publishes it.")


def brief(version, text, state="draft", newest=None, published=None, author_kind="session", author="7f0c1a2b"):
    return {
        "factory": FACTORY, "seat": SEAT, "version": version, "text": text, "state": state,
        "authorKind": author_kind, "authorId": author, "writtenAtUtc": "2026-10-13T12:00:00Z",
        "publishedAtUtc": "2026-10-13T12:05:00Z" if state == "published" else None, "publishedBy": None,
        "newestVersion": newest if newest is not None else version, "publishedVersion": published,
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
def brief_file(tmp_path):
    def make(text):
        path = tmp_path / "boss.yaml"
        path.write_text(text, encoding="utf-8")
        return str(path)

    return make


# ---------------------------------------------------------------------------------------------------
# get
# ---------------------------------------------------------------------------------------------------

def test_get_ReadsThePublishedBrief_AndPrintsItsWholeText(gateway):
    gw = gateway((200, brief(3, "You are the boss.\nPost the goal number every run.", state="published", published=3)))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["method"] == "GET"
    assert gw.calls[0]["path"] == "/factory-briefs/warmforward/boss"
    assert "warmforward/boss  version 3  published" in result.stdout
    assert "You are the boss." in result.stdout
    assert "Post the goal number every run." in result.stdout


def test_get_SaysWhenANewerDraftIsWaiting(gateway):
    gateway((200, brief(3, "published text", state="published", newest=5, published=3)))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE])

    assert result.exit_code == 0, result.output
    assert "A newer version, 5, is waiting and is not published." in result.stdout
    # The next save must state the newest version, not the one shown.
    assert "--expected-version 5" in result.stdout


def test_get_Draft_ReadsTheNewestVersion_AndSaysItIsNotWhatARunIsGiven(gateway):
    gw = gateway((200, brief(4, "a draft", state="draft", published=3)))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE, "--draft"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-briefs/warmforward/boss/draft"
    assert "version 4  draft" in result.stdout
    assert "A talk or a run is given version 3 until a person publishes this one." in result.stdout


def test_get_Json_PrintsTheGatewaysAnswerUnchanged(gateway):
    answer = brief(3, "exact text with a tab\there", state="published", published=3)
    gateway((200, answer))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE, "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == answer


def test_get_ASeatWithNoBriefOnTheGateway_ExitsNonZeroWithTheGatewaysSentence(gateway):
    gateway((404, {"error": "'boss' of warmforward has no brief on the Gateway", "newestVersion": 0}))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE])

    assert result.exit_code == 1
    assert "'boss' of warmforward has no brief on the Gateway" in result.stderr
    assert "factory brief set --factory warmforward --seat boss --file <path>" in result.stderr
    assert result.stdout == ""


def test_get_AnAnswerWithNoBrief_IsNeverReportedAsAnEmptyBrief(gateway):
    gateway((200, {}))

    result = runner.invoke(app, ["factory", "brief", "get", *WHERE])

    assert result.exit_code == 1
    assert "carried no brief" in result.stderr


def test_get_AFactoryNameWithASlash_StaysOneSegmentOfThePath(gateway):
    gw = gateway((404, {"error": "a factory is lower-case letters and digits joined by single hyphens"}))

    runner.invoke(app, ["factory", "brief", "get", "--factory", "warm/forward", "--seat", SEAT])

    assert gw.calls[0]["path"] == "/factory-briefs/warm%2Fforward/boss"


# ---------------------------------------------------------------------------------------------------
# history
# ---------------------------------------------------------------------------------------------------

def test_history_ListsEveryVersion_AndMarksThePublishedOne(gateway):
    gw = gateway((200, {"factory": FACTORY, "seat": SEAT, "publishedVersion": 2, "versions": [
        brief(3, "a newer draft", state="draft", newest=3, published=2),
        brief(2, "the published text", state="published", newest=3, published=2),
        brief(1, "the first text", state="published", newest=3, published=2),
    ]}))

    result = runner.invoke(app, ["factory", "brief", "history", *WHERE])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-briefs/warmforward/boss/history"
    assert "count: 3. Version 2 is the published brief." in result.stdout
    assert "v3  draft" in result.stdout
    assert "v2  PUBLISHED NOW" in result.stdout
    assert "v1  published" in result.stdout


def test_history_OfASeatWithNoBrief_SaysCountZero(gateway):
    gateway((200, {"factory": FACTORY, "seat": SEAT, "publishedVersion": None, "versions": []}))

    result = runner.invoke(app, ["factory", "brief", "history", *WHERE])

    assert result.exit_code == 0, result.output
    assert "count: 0." in result.stdout


def test_history_AnAnswerWithNoList_IsNeverReportedAsNoVersions(gateway):
    gateway((200, {"factory": FACTORY, "seat": SEAT}))

    result = runner.invoke(app, ["factory", "brief", "history", *WHERE])

    assert result.exit_code == 1
    assert "no list of versions" in result.stderr


# ---------------------------------------------------------------------------------------------------
# set
# ---------------------------------------------------------------------------------------------------

def test_set_SendsTheFilesExactText_AndTheVersionTheCallerStated(gateway, brief_file):
    text = "name: Boss\nrole: |\n  You are the boss of WarmForward.\n"
    gw = gateway((200, brief(5, text, state="draft", published=3)))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", brief_file(text), "--expected-version", "4"])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert (call["method"], call["path"]) == ("PUT", "/factory-briefs/warmforward/boss/draft")
    assert call["body"] == {"text": text, "expectedVersion": 4}
    assert "Saved: version 5 of the brief of 'boss' is a draft." in result.stdout
    assert "A talk or a run is given version 3 until a person publishes this draft." in result.stdout


def test_set_WithNoVersionStated_SendsZero_AndNeverLooksTheVersionUpItself(gateway, brief_file):
    # A version nobody was shown is not a version anybody read. If this command fetched the newest version to send
    # it, it would replace text its caller never saw - so it makes exactly one call, and that call says 0.
    gw = gateway((200, brief(1, "the first brief")))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", brief_file("the first brief")])

    assert result.exit_code == 0, result.output
    assert len(gw.calls) == 1
    assert gw.calls[0]["body"]["expectedVersion"] == 0
    assert "A talk or a run is given nothing from the Gateway until a person publishes this draft." in result.stdout


def test_set_AStaleSave_IsNotSaved_AndSaysWhichVersionToReadAndSendNext(gateway, brief_file):
    gw = gateway((409, {
        "error": "the brief of 'boss' is at version 6, not 4; read it again, merge, and save once more",
        "outcome": "Stale",
        "current": brief(6, "what another writer saved", author="9d2e4c11"),
    }))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", brief_file("mine"), "--expected-version", "4"])

    assert result.exit_code == 1
    assert len(gw.calls) == 1
    assert "Not saved: another writer got there first." in result.stderr
    assert "now at version 6 (by session 9d2e4c11); you sent version 4" in result.stderr
    assert "factory brief get --factory warmforward --seat boss --draft" in result.stderr
    assert "--expected-version 6" in result.stderr
    assert "Saved" not in result.stdout


def test_set_ExpectingAVersionOfASeatWithNoBrief_SaysToExpectZero(gateway, brief_file):
    gateway((409, {
        "error": "'boss' of warmforward has no brief on the Gateway yet, so save it expecting version 0",
        "outcome": "Stale", "current": None,
    }))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", brief_file("text"), "--expected-version", "2"])

    assert result.exit_code == 1
    assert "has no brief on the Gateway yet" in result.stderr
    assert "--expected-version 0" in result.stderr


def test_set_ASeatThatIsNotRegistered_ExitsNonZeroWithTheGatewaysSentence(gateway, brief_file):
    gateway((404, {"error": "'night-watch' is not a seat of warmforward. Its seats are: boss, scout.",
                   "outcome": "SeatNotRegistered", "current": None}))

    result = runner.invoke(app, ["factory", "brief", "set", "--factory", FACTORY, "--seat", "night-watch",
                                 "--file", brief_file("text")])

    assert result.exit_code == 1
    assert "'night-watch' is not a seat of warmforward" in result.stderr


def test_set_AFileThatIsNotThere_CallsNothing(gateway, tmp_path):
    gw = gateway()

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", str(tmp_path / "missing.yaml")])

    assert result.exit_code == 1
    assert "there is no file at" in result.stderr
    assert gw.calls == []


def test_set_ReadsStandardInput_WhenTheFileIsADash(gateway):
    gw = gateway((200, brief(1, "from standard input")))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", "-"], input="from standard input")

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["text"] == "from standard input"


def test_set_AnAnswerWithoutTheSavedVersion_IsNotReportedAsSaved(gateway, brief_file):
    gateway((200, {}))

    result = runner.invoke(app, ["factory", "brief", "set", *WHERE, "--file", brief_file("text")])

    assert result.exit_code == 1
    assert "cannot show the draft was saved" in result.stderr


# ---------------------------------------------------------------------------------------------------
# publish and restore: a person's acts
# ---------------------------------------------------------------------------------------------------

def test_publish_FromASession_IsRefusedWithTheGatewaysSentence_AndExitsNonZero(gateway):
    gw = gateway((403, {"error": ONLY_A_PERSON, "outcome": "OnlyAPersonPublishes", "current": None}))

    result = runner.invoke(app, ["factory", "brief", "publish", *WHERE, "--version", "4"])

    assert result.exit_code == 1
    call = gw.calls[0]
    assert (call["method"], call["path"], call["body"]) == ("POST", "/factory-briefs/warmforward/boss/publish", {"version": 4})
    assert ONLY_A_PERSON in result.stderr
    # What a session may do instead.
    assert "factory brief set --factory warmforward --seat boss --file <path>" in result.stderr
    assert "Published" not in result.stdout


def test_publish_WhenTheGatewayPublishes_SaysWhichVersionARunIsNowGiven(gateway):
    gateway((200, brief(4, "the text", state="published", published=4, author_kind="person", author=None)))

    result = runner.invoke(app, ["factory", "brief", "publish", *WHERE, "--version", "4"])

    assert result.exit_code == 0, result.output
    assert "Published: version 4 of the brief of 'boss'. Version 4 is now the brief a talk or a run is given." in result.stdout


def test_publish_NeedsAVersion(gateway):
    gw = gateway()

    result = runner.invoke(app, ["factory", "brief", "publish", *WHERE])

    assert result.exit_code != 0
    assert gw.calls == []


def test_restore_SendsTheVersionToBringBack_AndTheVersionTheCallerRead(gateway):
    gw = gateway((200, brief(6, "the first text", state="published", published=6, author_kind="person", author=None)))

    result = runner.invoke(app, ["factory", "brief", "restore", *WHERE, "--version", "1", "--expected-version", "5"])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert (call["method"], call["path"]) == ("POST", "/factory-briefs/warmforward/boss/restore")
    assert call["body"] == {"version": 1, "expectedVersion": 5}
    assert "Brought back: version 1 of the brief of 'boss'. Version 6 is now the brief a talk or a run is given." in result.stdout


def test_restore_FromASession_IsRefusedWithTheGatewaysSentence(gateway):
    sentence = ("Only a person publishes a seat's brief, and bringing an older version back publishes a copy of it. "
                "A session of the factory may save the text it wants as a new draft; the owner publishes it.")
    gateway((403, {"error": sentence, "outcome": "OnlyAPersonPublishes", "current": None}))

    result = runner.invoke(app, ["factory", "brief", "restore", *WHERE, "--version", "1"])

    assert result.exit_code == 1
    assert sentence in result.stderr
    assert "Brought back" not in result.stdout


def test_publish_Backwards_IsRefused_AndPointsAtTheHistory(gateway):
    gateway((409, {"error": "version 1 is older than the published version 2. Publishing does not go backwards: "
                            "bring version 1 back, which publishes a copy of it as the newest version",
                   "outcome": "OlderThanPublished", "current": brief(2, "v2", state="published", published=2)}))

    result = runner.invoke(app, ["factory", "brief", "publish", *WHERE, "--version", "1"])

    assert result.exit_code == 1
    assert "older than the published version 2" in result.stderr
    assert "factory brief history --factory warmforward --seat boss" in result.stderr


# ---------------------------------------------------------------------------------------------------
# every command carries this session's key, and an unknown flag fails
# ---------------------------------------------------------------------------------------------------

def test_everyCall_CarriesThisSessionsKey(gateway):
    gw = gateway((200, brief(1, "text", state="published", published=1)))

    runner.invoke(app, ["factory", "brief", "get", *WHERE])

    assert gw.calls[0]["auth"] == "Bearer test-session-key"


@pytest.mark.parametrize("verb", ["get", "history", "set", "publish", "restore"])
def test_anUnknownFlag_Fails_AndCallsNothing(gateway, verb):
    gw = gateway()

    result = runner.invoke(app, ["factory", "brief", verb, *WHERE, "--no-such-flag"])

    assert result.exit_code != 0
    assert gw.calls == []

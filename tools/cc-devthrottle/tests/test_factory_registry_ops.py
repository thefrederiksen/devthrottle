"""`cc-devthrottle factory register|list` and `factory goal-number post|show` (Factories screen mission, phase A).

These answer from a REAL local HTTP server, so the whole path - the shared transport, its error mapping and these
commands' handling - runs as it does against a Gateway. What matters most: the manifest's goal file is read and its
TEXT is sent, an unknown manifest key is refused rather than dropped, a number that was not kept never exits 0,
and an empty answer is said out loud rather than printed as nothing.
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

from cc_shared import axi_output  # noqa: E402
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
            do_PUT = _answer

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


def _factory_folder(tmp_path, goal="A cash engine that runs without your time.\n"):
    folder = tmp_path / "warmforward-factory"
    folder.mkdir()
    if goal is not None:
        (folder / "GOAL.md").write_text(goal, encoding="utf-8")
    return folder


def _manifest(tmp_path, folder, **overrides):
    manifest = {
        "factory": "warmforward",
        "title": "WarmForward",
        "folder": str(folder),
        "computer": "SOREN_NORTH",
        "bossSeat": "nora-hale",
        "goalFile": "GOAL.md",
        "goalApprovedOn": "2026-10-04",
        "seats": [
            {"id": "nora-hale", "name": "Boss", "role": "Boss", "briefFile": "agents/ceo.yaml", "schedules": ["cj_a721e6"]},
            {"id": "value-hunter", "name": "Value Hunter", "role": "Value Hunter", "briefFile": "agents/value.yaml", "schedules": []},
        ],
    }
    manifest.update(overrides)
    path = tmp_path / "warmforward.manifest.json"
    path.write_text(json.dumps(manifest), encoding="utf-8")
    return path


REGISTERED = {
    "factory": "warmforward", "title": "WarmForward", "folder": "D:\\f", "computer": "SOREN_NORTH",
    "bossSeat": "nora-hale", "goalText": "A cash engine.", "goalFile": "GOAL.md", "goalApprovedOn": "2026-10-04",
    "seats": [
        {"id": "nora-hale", "name": "Boss", "role": "Boss", "briefFile": "agents/ceo.yaml", "schedules": ["cj_a721e6"], "computer": "SOREN_NORTH"},
        {"id": "value-hunter", "name": "Value Hunter", "role": "Value Hunter", "briefFile": "agents/value.yaml", "schedules": [], "computer": "SOREN_NORTH"},
    ],
    "registeredBy": "session s1", "registeredAtUtc": "2026-10-06T12:00:00Z",
}


# ---------------------------------------------------------------------------------------------------
# factory register
# ---------------------------------------------------------------------------------------------------

def test_register_SendsTheManifestWithTheGoalFilesText_AndPrintsTheSeats(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert call["method"] == "PUT"
    assert call["path"] == "/gateway/factory/registry"
    assert call["auth"] == "Bearer test-session-key"
    assert call["body"]["goalText"] == "A cash engine that runs without your time.\n"
    assert call["body"]["goalFile"] == "GOAL.md"
    assert call["body"]["seats"][0]["schedules"] == ["cj_a721e6"]
    assert "registered: warmforward" in result.stdout
    assert "goal: set, approved 2026-10-04 (GOAL.md)" in result.stdout
    _, seats = axi_output.parse_list(result.stdout, "seats")
    assert [s["id"] for s in seats] == ["nora-hale", "value-hunter"]
    assert seats[0]["name"] == "Boss"


def test_register_NoGoalFile_SendsNoGoalText(gateway_answering, tmp_path):
    gw = gateway_answering(200, dict(REGISTERED, goalText=None, goalFile=None, goalApprovedOn=None))
    folder = _factory_folder(tmp_path, goal=None)
    manifest = _manifest(tmp_path, folder)
    data = json.loads(manifest.read_text(encoding="utf-8"))
    del data["goalFile"], data["goalApprovedOn"]
    manifest.write_text(json.dumps(data), encoding="utf-8")

    result = runner.invoke(app, ["factory", "register", "--manifest", str(manifest)])

    assert result.exit_code == 0, result.output
    assert "goalText" not in gw.calls[0]["body"]
    assert "goal: none" in result.stdout


def test_register_GoalFileMissing_ExitsNonZeroAndSendsNothing(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path, goal=None)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 1
    assert "goal file" in result.stderr and "does not exist" in result.stderr
    assert gw.calls == []


def test_register_UnknownManifestKey_IsRefusedNotDropped(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder, goalfile="GOAL.md"))])

    assert result.exit_code == 1
    assert "goalfile" in result.stderr
    assert gw.calls == []


def test_register_UnknownSeatKey_IsRefused(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path)
    seats = [{"id": "nora-hale", "name": "Boss", "role": "Boss", "briefFile": "a.yaml", "schedules": [], "brief": "x"}]

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder, seats=seats))])

    assert result.exit_code == 1
    assert "brief" in result.stderr
    assert gw.calls == []


@pytest.mark.parametrize(
    "goal_file",
    [
        "../secret.txt",
        "sub/../../secret.txt",
        "/etc/passwd",
        # A drive letter is outside the folder only on Windows. On Linux and macOS "C:/secret.txt" is a relative
        # name INSIDE the factory's folder, so the command refuses it for another reason (no such file), which is
        # right and is not what this test is about (#3594).
        pytest.param(
            "C:/secret.txt",
            marks=pytest.mark.skipif(sys.platform != "win32", reason="a drive letter is an absolute path only on Windows"),
        ),
        r"\\server\share\x.md",
    ],
)
def test_register_GoalFileOutsideTheFolder_IsRefusedBeforeAnythingIsRead(gateway_answering, tmp_path, goal_file):
    gw = gateway_answering(200, REGISTERED)
    (tmp_path / "secret.txt").write_text("TOP-SECRET", encoding="utf-8")
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder, goalFile=goal_file))])

    assert result.exit_code == 1
    assert "inside the factory's folder" in result.stderr
    assert gw.calls == []


def _symlink_or_skip(link, target):
    try:
        link.symlink_to(target)
    except (OSError, NotImplementedError) as exc:
        pytest.skip(f"this machine cannot create a symbolic link here: {exc}")


def test_register_GoalFileThatLinksOutsideTheFolder_IsRefusedAndNothingIsSent(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    secret = tmp_path / "secret.txt"
    secret.write_text("TOP-SECRET", encoding="utf-8")
    folder = _factory_folder(tmp_path, goal=None)
    _symlink_or_skip(folder / "GOAL.md", secret)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 1
    assert "leads outside the factory's folder" in result.stderr
    assert gw.calls == []


def test_register_GoalFileThatLinksInsideTheFolder_IsRead(gateway_answering, tmp_path):
    gw = gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path, goal=None)
    (folder / "docs").mkdir()
    (folder / "docs" / "goal.md").write_text("The real goal.", encoding="utf-8")
    _symlink_or_skip(folder / "GOAL.md", folder / "docs" / "goal.md")

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["goalText"] == "The real goal."


def test_register_NonAsciiGoalFile_RegistersAndPrintsItEscaped(gateway_answering, tmp_path):
    name = "G\u00d6AL.md"
    gateway_answering(200, dict(REGISTERED, goalFile=name))
    folder = _factory_folder(tmp_path, goal=None)
    (folder / name).write_text("A goal.", encoding="utf-8")

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder, goalFile=name))])

    assert result.exit_code == 0, result.output
    assert result.stdout.isascii()
    assert "registered: warmforward" in result.stdout
    assert "goal: set, approved 2026-10-04 (G" in result.stdout


def test_register_ManifestNotJson_ExitsNonZero(gateway_answering, tmp_path):
    gateway_answering(200, REGISTERED)
    bad = tmp_path / "m.json"
    bad.write_text("factory: warmforward\n", encoding="utf-8")

    result = runner.invoke(app, ["factory", "register", "--manifest", str(bad)])

    assert result.exit_code == 1
    assert "not readable JSON" in result.stderr


def test_register_GatewayRefuses_ExitsNonZeroWithItsSentence(gateway_answering, tmp_path):
    gateway_answering(400, {"error": "The folder 'x' must be an absolute path on the factory's computer."})
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 1
    assert "Not registered:" in result.stderr
    assert "must be an absolute path" in result.stderr


def test_register_SwitchOff_SaysTheFeatureIsOff(gateway_answering, tmp_path):
    gateway_answering(404, None)
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder))])

    assert result.exit_code == 1
    assert "switched off" in result.stderr


def test_register_Json_PrintsTheGatewaysAnswer(gateway_answering, tmp_path):
    gateway_answering(200, REGISTERED)
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest", str(_manifest(tmp_path, folder)), "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == REGISTERED


# ---------------------------------------------------------------------------------------------------
# factory list
# ---------------------------------------------------------------------------------------------------

def test_list_PrintsEveryFactoryInFull_WithACount(gateway_answering):
    other = dict(REGISTERED, factory="website-business-factory-long-id", title="Website Business, Inc.",
                 bossSeat=None, goalText=None)
    gw = gateway_answering(200, {"count": 2, "factories": [REGISTERED, other]})

    result = runner.invoke(app, ["factory", "list"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/gateway/factory/registry"
    assert "count: 2" in result.stdout
    fields, rows = axi_output.parse_list(result.stdout, "factories")
    assert fields == ["id", "title", "boss", "seats"]
    assert [r["id"] for r in rows] == ["warmforward", "website-business-factory-long-id"]
    assert rows[1]["title"] == "Website Business, Inc."
    assert rows[0]["boss"] == "nora-hale" and rows[1]["boss"] is None
    assert rows[0]["seats"] == "2"


def test_list_Fields_ShowsTheNamedFieldsInThatOrder(gateway_answering):
    other = dict(REGISTERED, factory="website-business", goalText=None)
    gateway_answering(200, {"count": 2, "factories": [REGISTERED, other]})

    result = runner.invoke(app, ["factory", "list", "--fields", "id,goal,computer,folder"])

    assert result.exit_code == 0, result.output
    fields, rows = axi_output.parse_list(result.stdout, "factories")
    assert fields == ["id", "goal", "computer", "folder"]
    assert rows[0]["goal"] == "yes" and rows[1]["goal"] == "no"
    assert rows[0]["folder"] == "D:\\f"


def test_list_UnknownField_IsAUsageErrorNamingTheValidOnes(gateway_answering):
    gw = gateway_answering(200, {"count": 0, "factories": []})

    result = runner.invoke(app, ["factory", "list", "--fields", "id,owner"])

    assert result.exit_code == 2
    assert "owner" in result.stderr and "computer" in result.stderr
    assert gw.calls == []


def test_list_FieldsWithJson_IsAUsageError(gateway_answering):
    gateway_answering(200, {"count": 0, "factories": []})

    result = runner.invoke(app, ["factory", "list", "--fields", "id", "--json"])

    assert result.exit_code == 2


def test_list_None_SaysCountZero(gateway_answering):
    gateway_answering(200, {"count": 0, "factories": []})

    result = runner.invoke(app, ["factory", "list"])

    assert result.exit_code == 0, result.output
    assert "count: 0" in result.stdout
    assert "factory register --manifest" in result.stdout


def test_list_Json_IsTheGatewaysAnswerUnchanged(gateway_answering):
    answer = {"count": 1, "factories": [REGISTERED]}
    gateway_answering(200, answer)

    result = runner.invoke(app, ["factory", "list", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == answer


def test_list_AnswerWithNoList_IsAnErrorNotNone(gateway_answering):
    gateway_answering(200, {"items": []})

    result = runner.invoke(app, ["factory", "list"])

    assert result.exit_code == 1
    assert "no list of factories" in result.stderr


# ---------------------------------------------------------------------------------------------------
# factory goal-number
# ---------------------------------------------------------------------------------------------------

POST = ["factory", "goal-number", "post", "--factory", "warmforward", "--value", "not yet proven",
        "--unit", "propane saved this season", "--date", "2026-10-06",
        "--link", "https://github.com/thefrederiksen/websites/issues/276"]

POSTED = {"id": "0b6f0000-0000-4000-8000-000000000001", "factory": "warmforward", "value": "not yet proven",
          "unit": "propane saved this season", "asOf": "2026-10-06",
          "link": "https://github.com/thefrederiksen/websites/issues/276", "postedBy": "nora-hale",
          "postedBySession": "s1", "postedAtUtc": "2026-10-06T10:20:00Z"}


def test_post_SendsTheNumber_AndPrintsWhatWasKept(gateway_answering):
    gw = gateway_answering(201, POSTED)

    result = runner.invoke(app, POST + ["--by", "nora-hale"])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert call["method"] == "POST" and call["path"] == "/gateway/factory/goal-numbers"
    assert call["body"] == {"factory": "warmforward", "value": "not yet proven", "unit": "propane saved this season",
                            "asOf": "2026-10-06", "link": "https://github.com/thefrederiksen/websites/issues/276",
                            "postedBy": "nora-hale"}
    assert f"posted: {POSTED['id']}" in result.stdout
    assert "posted by: nora-hale" in result.stdout


def test_post_NoBy_SendsNoPoster_SoTheGatewaySettlesIt(gateway_answering):
    gw = gateway_answering(201, POSTED)

    result = runner.invoke(app, POST)

    assert result.exit_code == 0, result.output
    assert "postedBy" not in gw.calls[0]["body"]


def test_post_NotRegistered_ExitsNonZeroWithTheReason(gateway_answering):
    gateway_answering(409, {"error": "The factory 'warmforward' is not registered, so it has no goal number to post."})

    result = runner.invoke(app, POST)

    assert result.exit_code == 1
    assert "Not posted:" in result.stderr and "not registered" in result.stderr


def test_post_SuccessWithNoId_IsNotReportedAsPosted(gateway_answering):
    gateway_answering(201, {"factory": "warmforward"})

    result = runner.invoke(app, POST)

    assert result.exit_code == 1
    assert "Not posted:" in result.stderr


def test_show_ListsThePostsNewestFirst_WithTheTotal(gateway_answering):
    older = dict(POSTED, id="0b6f0000-0000-4000-8000-000000000000", value="0", asOf="2026-10-05")
    gw = gateway_answering(200, {"factory": "warmforward", "latest": POSTED, "count": 5, "posts": [POSTED, older]})

    result = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward", "-n", "2"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/gateway/factory/goal-numbers?factory=warmforward&count=2"
    assert "count: 2 of 5 total" in result.stdout
    assert "not yet proven" in result.stdout
    assert "--count 5" in result.stdout


def test_show_DefaultFieldsAreFour_AndALongLinkIsCutWithItsSizeAndFull(gateway_answering):
    long_link = "https://example.com/" + "x" * 300
    post = dict(POSTED, link=long_link)
    gateway_answering(200, {"factory": "warmforward", "latest": post, "count": 1, "posts": [post]})

    plain = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward"])
    fields, _ = axi_output.parse_list(plain.stdout, "goalNumbers")
    assert fields == ["asOf", "value", "unit", "postedBy"]
    assert long_link not in plain.stdout

    linked = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward", "--fields", "asOf,link"])
    assert linked.exit_code == 0, linked.output
    assert "(truncated, 320 chars total - use --full)" in linked.stdout
    assert long_link not in linked.stdout

    whole = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward", "--fields", "link", "--full"])
    assert whole.exit_code == 0, whole.output
    _, rows = axi_output.parse_list(whole.stdout, "goalNumbers")
    assert rows[0]["link"] == long_link


def test_show_FieldsWithJson_IsAUsageError(gateway_answering):
    gateway_answering(200, {"factory": "warmforward", "latest": None, "count": 0, "posts": []})

    result = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward", "--fields", "id", "--json"])

    assert result.exit_code == 2


def test_show_NothingPosted_SaysSo(gateway_answering):
    gateway_answering(200, {"factory": "warmforward", "latest": None, "count": 0, "posts": []})

    result = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward"])

    assert result.exit_code == 0, result.output
    assert "count: 0" in result.stdout
    assert "No goal number posted yet." in result.stdout


def test_show_Json_IsTheGatewaysAnswerUnchanged(gateway_answering):
    answer = {"factory": "warmforward", "latest": POSTED, "count": 1, "posts": [POSTED]}
    gateway_answering(200, answer)

    result = runner.invoke(app, ["factory", "goal-number", "show", "--factory", "warmforward", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == answer


def test_actions_ListTheRegistryVerbs():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    ids = {action["id"] for action in json.loads(result.output)["actions"]}
    assert {"factory-register", "factory-list", "factory-purpose", "factory-goal-number-post", "factory-goal-number-show"}.issubset(ids)


# ---------------------------------------------------------------------------------------------------
# factory purpose (the Factories cards, 8 Oct 2026)
# ---------------------------------------------------------------------------------------------------

def test_register_ManifestPurpose_IsSentAsIs(gateway_answering, tmp_path):
    gw = gateway_answering(200, dict(REGISTERED, purpose="Heating monitoring for homeowners"))
    folder = _factory_folder(tmp_path)

    result = runner.invoke(app, ["factory", "register", "--manifest",
                                 str(_manifest(tmp_path, folder, purpose="Heating monitoring for homeowners"))])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"]["purpose"] == "Heating monitoring for homeowners"
    assert "purpose: Heating monitoring for homeowners" in result.stdout


def test_purpose_PutsTheTrimmedLineOnTheFactorysOwnRoute_AndPrintsWhatIsKept(gateway_answering):
    gw = gateway_answering(200, dict(REGISTERED, purpose="Heating monitoring for homeowners"))

    result = runner.invoke(app, ["factory", "purpose", "warmforward", "  Heating monitoring for homeowners  "])

    assert result.exit_code == 0, result.output
    call = gw.calls[0]
    assert call["method"] == "PUT"
    assert call["path"] == "/gateway/factory/registry/warmforward/purpose"
    assert call["body"] == {"purpose": "Heating monitoring for homeowners"}
    assert "factory: warmforward" in result.stdout
    assert "purpose: Heating monitoring for homeowners" in result.stdout


def test_purpose_Clear_SendsNull_AndSaysCleared(gateway_answering):
    gw = gateway_answering(200, dict(REGISTERED, purpose=None))

    result = runner.invoke(app, ["factory", "purpose", "warmforward", "--clear"])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["body"] == {"purpose": None}
    assert "purpose: none (cleared)" in result.stdout


def test_purpose_NoLineAndNoClear_IsAUsageError(gateway_answering):
    gw = gateway_answering(200, REGISTERED)

    result = runner.invoke(app, ["factory", "purpose", "warmforward"])

    assert result.exit_code == 2
    assert "--clear" in result.stderr
    assert gw.calls == []


def test_purpose_LineAndClear_IsAUsageError(gateway_answering):
    gw = gateway_answering(200, REGISTERED)

    result = runner.invoke(app, ["factory", "purpose", "warmforward", "A line", "--clear"])

    assert result.exit_code == 2
    assert gw.calls == []


def test_purpose_GatewayRefusesTooLong_ExitsNonZeroWithItsSentence(gateway_answering):
    gateway_answering(400, {"error": "The purpose is 121 characters; it takes at most 120. Shorten it to one line."})

    result = runner.invoke(app, ["factory", "purpose", "warmforward", "x" * 121])

    assert result.exit_code == 1
    assert "at most 120" in result.stderr


def test_purpose_NotRegistered_ExitsNonZeroWithTheReason(gateway_answering):
    gateway_answering(409, {"error": "No factory 'nobody' is registered in this account."})

    result = runner.invoke(app, ["factory", "purpose", "nobody", "A line"])

    assert result.exit_code == 1
    assert "No factory 'nobody' is registered" in result.stderr


def test_purpose_Json_IsTheGatewaysAnswerUnchanged(gateway_answering):
    answer = dict(REGISTERED, purpose="A line")
    gateway_answering(200, answer)

    result = runner.invoke(app, ["factory", "purpose", "warmforward", "A line", "--json"])

    assert result.exit_code == 0
    assert json.loads(result.stdout) == answer


def test_list_ShowsThePurposeWhenAsked(gateway_answering):
    gateway_answering(200, {"count": 1, "factories": [dict(REGISTERED, purpose="A line")]})

    result = runner.invoke(app, ["factory", "list", "--fields", "id,purpose"])

    assert result.exit_code == 0, result.output
    assert "A line" in result.stdout

"""`cc-devthrottle factory export|import` and `factory definition schemas` (Factory Definition mission, phase 3).

What these tests are about: export writes every file EXACTLY as the Gateway sent it - UTF-8 with no byte-order mark,
the file's own line endings - so two exports of one answer are the same bytes; it writes only into a new or empty
folder and never outside it; what the Gateway reported it could not carry is printed, not dropped. Import sends every
file under the folder exactly as `save` does, to the import route, and prints the Gateway's refusal and findings as
they came; it never calls a publish. They answer from a REAL local HTTP server, as the phase 1 tests do.
"""

import hashlib
import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from tests.test_factory_definition_ops import FACTORY, FILES, _Gateway, version  # noqa: E402

runner = CliRunner()

STORED = {
    **FILES,
    "files/template.md": "line one\r\nline two \u00e6\r\n",
    "rules/base.json": '{\n\t"permissions": {"allow": [], "deny": [], "ask": []}\n}',
}


def exported(source="definition", files=None, reported=None, n=3, state="published"):
    return {
        "factory": FACTORY, "source": source, "version": n if source == "definition" else None,
        "state": state if source == "definition" else None,
        "summary": f"Version {n} of the definition of {FACTORY}, {state}, exactly as stored (4 files).",
        "files": files if files is not None else STORED, "reported": reported or [],
    }


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


def digest(root: Path):
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(root.rglob("*")) if p.is_file()}


# ---------------------------------------------------------------------------------------------------
# export
# ---------------------------------------------------------------------------------------------------

def test_export_WritesEveryFileExactlyAsSent_NoByteOrderMark_ItsOwnLineEndings(gateway, tmp_path):
    gw = gateway((200, exported()))
    out = tmp_path / "out"

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(out)])

    assert result.exit_code == 0, result.output
    assert (gw.calls[0]["method"], gw.calls[0]["path"]) == ("GET", "/factory-definitions/warmforward/export")
    for path, text in STORED.items():
        assert (out / path).read_bytes() == text.encode("utf-8")
    assert (out / "files/template.md").read_bytes().count(b"\r\n") == 2
    assert not (out / "factory.yaml").read_bytes().startswith(b"\xef\xbb\xbf")
    assert "files[4]{path,bytes}:" in result.stdout
    assert f"  files/template.md,{len(STORED['files/template.md'].encode('utf-8'))}" in result.stdout


def test_export_TwiceOfOneAnswer_GivesTheSameBytes(gateway, tmp_path):
    gateway((200, exported()), (200, exported()))

    assert runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "a")]).exit_code == 0
    assert runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "b")]).exit_code == 0

    assert digest(tmp_path / "a") == digest(tmp_path / "b")
    assert len(digest(tmp_path / "a")) == len(STORED)


def test_export_Version_IsSentToTheGateway(gateway, tmp_path):
    gw = gateway((200, exported(n=2, state="draft")))

    assert runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "o"), "--version", "2"]).exit_code == 0

    assert gw.calls[0]["path"] == "/factory-definitions/warmforward/export?version=2"


def test_export_IntoAFolderThatIsNotEmpty_IsRefused_AndCallsNothing(gateway, tmp_path):
    gw = gateway()
    out = tmp_path / "used"
    out.mkdir()
    (out / "left-over.md").write_text("old")

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(out)])

    assert result.exit_code == 1
    assert "is not an empty folder" in result.output
    assert gw.calls == []


def test_export_APathOutsideTheFolder_IsNeverWritten(gateway, tmp_path):
    gateway((200, exported(files={"../escaped.txt": "x", "factory.yaml": "factory: x\n"})))
    out = tmp_path / "out"

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(out)])

    assert result.exit_code == 1
    assert "'../escaped.txt', which is not inside the folder; nothing was written." in result.output
    assert not (tmp_path / "escaped.txt").exists()
    assert not (out / "factory.yaml").exists()


def test_export_OfAFactoryWithNoDefinition_PrintsWhatTheGatewayCouldNotCarry(gateway, tmp_path):
    reported = [{"file": "factory.yaml", "message": "The factory's folder 'D:\\f' on SOREN_NORTH is not part of a definition."},
                {"file": "seats/boss.yaml", "message": "The schedule 'x' (cj_1) is not a start here: it is switched off."}]
    gateway((200, exported(source="live", files=FILES, reported=reported)))

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "o")])

    assert result.exit_code == 0, result.output
    assert "reported[2]{file,message}:" in result.stdout
    assert "  seats/boss.yaml: The schedule 'x' (cj_1) is not a start here: it is switched off." in result.stdout


def test_export_Json_KeepsOneShape_WithTheFilesWrittenAndWhatWasReported(gateway, tmp_path):
    gateway((200, exported()))

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "o"), "--json"])

    assert result.exit_code == 0, result.output
    shown = json.loads(result.stdout)
    assert set(shown) == {"factory", "source", "version", "state", "summary", "out", "files", "reported"}
    assert [f["path"] for f in shown["files"]] == sorted(STORED)


def test_export_ANotFound_ExitsNonZeroWithTheGatewaysSentence_AndWritesNothing(gateway, tmp_path):
    gateway((404, {"error": "warmforward has no definition on the Gateway and is not a registered factory, so there is nothing to export"}))
    out = tmp_path / "o"

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(out)])

    assert result.exit_code == 1
    assert "is not a registered factory, so there is nothing to export" in result.output
    assert not out.exists()


def test_export_AnAnswerWithNoFiles_WritesNothing(gateway, tmp_path):
    gateway((200, {"factory": FACTORY, "source": "definition", "summary": "?"}))
    out = tmp_path / "o"

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(out)])

    assert result.exit_code == 1
    assert "carried no files to write" in result.output
    assert not out.exists()


# ---------------------------------------------------------------------------------------------------
# import
# ---------------------------------------------------------------------------------------------------

@pytest.fixture
def folder(tmp_path):
    root = tmp_path / "warmforward"
    for path, text in STORED.items():
        target = root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(text.encode("utf-8"))
    return root


def test_import_SendsEveryFileExactlyAsOnDisk_ToTheImportRoute_AndPublishesNothing(gateway, folder):
    answer = version(1, files=None)
    answer["created"] = True
    gw = gateway((200, answer))

    result = runner.invoke(app, ["factory", "import", str(folder), "--note", "first"])

    assert result.exit_code == 0, result.output
    assert len(gw.calls) == 1
    call = gw.calls[0]
    assert (call["method"], call["path"]) == ("POST", "/factory-definitions/import")
    assert call["body"] == {"content": STORED, "note": "first"}
    assert "Imported: version 1 of the definition of warmforward is a draft" in result.stdout
    assert "This import created the factory's definition." in result.stdout
    assert "Nothing was published: no version is published yet." in result.stdout


def test_import_SendsNoFactory_AndAPublishedVersionIsSaidToStay(gateway, folder):
    answer = version(4, published=3, files=None)
    answer["created"] = False
    gw = gateway((200, answer))

    result = runner.invoke(app, ["factory", "import", str(folder)])

    assert result.exit_code == 0, result.output
    assert "factory" not in gw.calls[0]["body"]
    assert "Nothing was published: version 3 stays the published definition." in result.stdout
    assert "created" not in result.stdout


def test_import_RefusedByTheCheck_PrintsEveryFindingAsFileLineMessage_AndExitsNonZero(gateway, folder):
    gateway((422, {
        "error": "The folder was not imported: the check found 1 error in the definition of warmforward. Fix each one where it is and try again.",
        "outcome": "CheckFailed",
        "findings": [{"severity": "error", "file": "seats/boss.yaml", "line": 5, "message": "'colour' is not a key a seat file holds."}],
    }))

    result = runner.invoke(app, ["factory", "import", str(folder)])

    assert result.exit_code == 1
    assert "The folder was not imported: the check found 1 error" in result.output
    assert "errors[1]:" in result.output
    assert "  seats/boss.yaml:5: 'colour' is not a key a seat file holds." in result.output


def test_import_AFolderThatIsNotThere_CallsNothing(gateway, tmp_path):
    gw = gateway()

    result = runner.invoke(app, ["factory", "import", str(tmp_path / "missing")])

    assert result.exit_code == 1
    assert "so there is no definition to import" in result.output
    assert gw.calls == []


def test_import_Json_PrintsTheGatewaysAnswerUnchanged(gateway, folder):
    answer = version(1, files=None)
    answer["created"] = True
    gateway((200, answer))

    result = runner.invoke(app, ["factory", "import", str(folder), "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.stdout) == answer


# ---------------------------------------------------------------------------------------------------
# schemas
# ---------------------------------------------------------------------------------------------------

SCHEMAS = {
    "note": "Each schema checks the shape of one kind of file.",
    "schemas": [{"name": "factory.schema.json", "applies": "factory.yaml", "schema": {"type": "object"}},
                {"name": "seat.schema.json", "applies": "seats/<seat>.yaml", "schema": {"type": "object", "title": "a seat file"}}],
}


def test_schemas_ListsThem_AndWritesEachToItsOwnFile(gateway, tmp_path):
    gw = gateway((200, SCHEMAS))
    out = tmp_path / "schemas"

    result = runner.invoke(app, ["factory", "definition", "schemas", "--out", str(out)])

    assert result.exit_code == 0, result.output
    assert gw.calls[0]["path"] == "/factory-definition-schemas"
    assert "schemas[2]{name,applies}:" in result.stdout
    assert "  seat.schema.json,seats/<seat>.yaml" in result.stdout
    assert json.loads((out / "seat.schema.json").read_text(encoding="utf-8")) == {"type": "object", "title": "a seat file"}


def test_schemas_AnAnswerWithNoSchemas_IsNeverReportedAsNone(gateway):
    gateway((200, {"note": "x", "schemas": []}))

    result = runner.invoke(app, ["factory", "definition", "schemas"])

    assert result.exit_code == 1
    assert "answered with no schemas" in result.output


def test_import_Factory_IsNotAnOption_BecauseFactoryYamlNamesTheFactory(gateway, folder):
    gw = gateway()

    result = runner.invoke(app, ["factory", "import", str(folder), "--factory", FACTORY])

    assert result.exit_code != 0
    assert gw.calls == []


def test_export_ADiskFailure_IsASentenceNotATraceback(gateway, tmp_path, monkeypatch):
    real_open = Path.open

    def failing_open(self, mode="r", *args, **kwargs):
        if "w" in mode and self.name == "grants.yaml":
            raise OSError("No space left on device")
        return real_open(self, mode, *args, **kwargs)

    monkeypatch.setattr(Path, "open", failing_open)
    files = dict(STORED, **{"grants.yaml": "grants: []\n"})
    gateway((200, exported(files=files)))

    result = runner.invoke(app, ["factory", "export", FACTORY, "--out", str(tmp_path / "o")])

    assert result.exit_code == 1
    assert "grants.yaml could not be written to" in result.output
    assert "No space left on device" in result.output
    assert "Traceback" not in result.output

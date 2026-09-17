"""Importing a KEY=VALUE file (the owner's credentials.env) as entries, and running a command with several of them.
No value may ever reach the output, whatever happens to a line."""

import json
import sys

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, paths
from src.audit import AuditLog
from src.store import SecretStore
from src.storefile import UserOnlyFile

runner = CliRunner()
PY = sys.executable


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _env_file(tmp_path, lines):
    path = tmp_path / "credentials.env"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def _values():
    return {"API": new_secret(), "OTHER_TOKEN": new_secret(), "SERVICE_HOST": "https://service.example.com"}


def test_Import_CreatesOneEntryPerKey_NamedAfterIt_WithItsVariable_AndPrintsNoValue(store, tmp_path, plain):
    values = _values()
    path = _env_file(tmp_path, ["# comment", "", f"API={values['API']}", f"OTHER_TOKEN={values['OTHER_TOKEN']}",
                                f"SERVICE_HOST={values['SERVICE_HOST']}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--skip", "SERVICE_HOST"])

    assert result.exit_code == 0, _text(result)
    entries = {e.name: e for e in store.entries()}
    assert sorted(entries) == ["api", "other-token"]
    assert entries["api"].secret.reveal() == values["API"]
    assert entries["other-token"].env_name == "OTHER_TOKEN"
    assert entries["api"].uses == ["run"] and entries["api"].agents_may_use is True
    text = plain(_text(result))
    assert not any(v in text for v in (values["API"], values["OTHER_TOKEN"]))
    assert "2 added" in " ".join(text.split()) and "1 skipped" in " ".join(text.split())


def test_Import_SkipNamingAKeyNotInTheFile_ImportsNothing(store, tmp_path):
    path = _env_file(tmp_path, [f"API={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--skip", "NOT_THERE"])

    assert result.exit_code != 0
    assert "NOT_THERE" in _text(result)
    assert store.entries() == []


def test_Import_AValueTooShort_IsReportedByKey_OthersImported_ValueNeverShown(store, tmp_path):
    good = new_secret()
    path = _env_file(tmp_path, ["SHORT=xy9", f"GOOD={good}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code != 0
    assert "SHORT" in _text(result)
    assert "xy9" not in _text(result) and good not in _text(result)
    assert [e.name for e in store.entries()] == ["good"]


def test_Import_ExistingEntry_IsLeftAsItIs_UnlessReplace(store, tmp_path):
    original = add_entry(store, name="api")
    newer = new_secret()
    path = _env_file(tmp_path, [f"API={newer}"])

    runner.invoke(cli.app, ["import", str(path), "--agents"])
    assert store.get("api").secret.reveal() == original

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--replace"])
    assert result.exit_code == 0, _text(result)
    assert store.get("api").secret.reveal() == newer


def test_Import_DryRun_ChangesNothing_AndPrintsNoValue(store, tmp_path, plain):
    value = new_secret()
    path = _env_file(tmp_path, [f"API={value}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--dry-run"])

    assert result.exit_code == 0, _text(result)
    assert store.entries() == []
    assert "api" in plain(_text(result)) and value not in _text(result)


def test_Import_InsideASession_IsRefused(store, tmp_path, monkeypatch):
    path = _env_file(tmp_path, [f"API={new_secret()}"])
    monkeypatch.setenv("CC_SESSION_ID", "agent-session")

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.entries() == []


def test_Import_WithoutSayingWhetherAgentsMayUseThem_IsRefused(store, tmp_path):
    path = _env_file(tmp_path, [f"API={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path)])

    assert result.exit_code != 0
    assert store.entries() == []


def test_Import_AKeyGivenTwice_IsRefusedNamingTheLines_AndImportsNothing(store, tmp_path):
    path = _env_file(tmp_path, [f"API={new_secret()}", f"API={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code != 0
    assert "line 1" in _text(result)
    assert store.entries() == []


def test_Import_SavesTheStoreOnce_ForManyEntries(store, tmp_path, monkeypatch):
    path = _env_file(tmp_path, [f"KEY_{i}={new_secret()}" for i in range(12)])
    writes = []
    original = UserOnlyFile.write

    def counting_write(self, data):
        writes.append(len(data))
        return original(self, data)

    monkeypatch.setattr(UserOnlyFile, "write", counting_write)

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code == 0, _text(result)
    assert len(store.entries()) == 12
    assert len(writes) == 1


def test_Import_IsAudited_PerEntry_WithoutValues(store, tmp_path):
    value = new_secret()
    path = _env_file(tmp_path, [f"API={value}"])

    runner.invoke(cli.app, ["import", str(path), "--agents"])

    lines = AuditLog(paths.audit_path()).read(100)
    assert any(l["entry"] == "api" and l["command"] == "import" for l in lines)
    assert value not in json.dumps(lines)


def _imported(store, tmp_path, lines):
    path = _env_file(tmp_path, lines)
    result = runner.invoke(cli.app, ["import", str(path), "--agents"])
    assert result.exit_code == 0, _text(result)


def test_Run_AnImportedEntry_IsSuppliedInItsOwnVariable_ByDefault(store, tmp_path):
    value = new_secret()
    _imported(store, tmp_path, [f"SERVICE_API_KEY={value}"])

    result = runner.invoke(cli.app, ["run", "service-api-key", "--", PY, "-c",
                                     "import os; v=os.environ.get('SERVICE_API_KEY',''); print('length', len(v))"])

    assert result.exit_code == 0, _text(result)
    assert f"length {len(value)}" in result.output
    assert value not in _text(result)


def test_Run_WithSeveralEntries_SuppliesEachInItsOwnVariable(store, tmp_path):
    key, secret = new_secret(), new_secret() + "x"
    _imported(store, tmp_path, [f"GODADDY_KEY={key}", f"GODADDY_SECRET={secret}"])
    script = ("import os; print('key', len(os.environ['GODADDY_KEY']), 'secret', len(os.environ['GODADDY_SECRET']));"
              "print(os.environ['GODADDY_KEY'], os.environ['GODADDY_SECRET'])")

    result = runner.invoke(cli.app, ["run", "godaddy-key", "--with", "godaddy-secret", "--", PY, "-c", script])

    assert result.exit_code == 0, _text(result)
    assert f"key {len(key)} secret {len(secret)}" in result.output
    assert key not in _text(result) and secret not in _text(result)
    audited = {l["entry"] for l in AuditLog(paths.audit_path()).read(100) if l["command"].startswith("run")}
    assert {"godaddy-key", "godaddy-secret"} <= audited


def test_Run_WithSeveralEntries_AndStdin_IsRefused(store, tmp_path):
    _imported(store, tmp_path, [f"ONE_KEY={new_secret()}", f"TWO_KEY={new_secret()}"])

    result = runner.invoke(cli.app, ["run", "one-key", "--with", "two-key", "--via", "stdin", "--", PY, "-c", "print(1)"])

    assert result.exit_code != 0
    assert "--via env" in _text(result)


def test_Run_WithSeveralEntries_AndOneVariableName_IsRefused(store, tmp_path):
    _imported(store, tmp_path, [f"ONE_KEY={new_secret()}", f"TWO_KEY={new_secret()}"])

    result = runner.invoke(cli.app, ["run", "one-key", "--with", "two-key", "--env-name", "X", "--", PY, "-c", "print(1)"])

    assert result.exit_code != 0


def test_Run_WithAnEntryAgentsMayNotUse_IsRefused_AndNothingRuns(store, tmp_path):
    _imported(store, tmp_path, [f"ONE_KEY={new_secret()}"])
    add_entry(store, name="kept-back", agents=False)
    marker = tmp_path / "ran.txt"

    result = runner.invoke(cli.app, ["run", "one-key", "--with", "kept-back", "--", PY, "-c",
                                     f"open(r'{marker}', 'w').write('ran')"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert not marker.exists()


def test_Run_AnEntryWithoutAVariableName_StillDefaultsToStdin(store):
    secret = add_entry(store)

    result = runner.invoke(cli.app, ["run", "devlinux", "--", PY, "-c", "import sys; print('len', len(sys.stdin.readline().strip()))"])

    assert result.exit_code == 0, _text(result)
    assert f"len {len(secret)}" in result.output


def test_Run_TimeoutZero_MeansNoLimit(store, tmp_path):
    _imported(store, tmp_path, [f"ONE_KEY={new_secret()}"])

    result = runner.invoke(cli.app, ["run", "one-key", "--timeout", "0", "--", PY, "-c", "import time; time.sleep(0.2); print('done')"])

    assert result.exit_code == 0, _text(result)
    assert "done" in result.output


def test_Import_AMalformedLineHoldingAnotherLinesValue_NeverPrintsOrLogsIt(store, tmp_path):
    # The review's case: the value of line 1 sits in the key position of line 2, which is not a variable name.
    value = new_secret()
    path = _env_file(tmp_path, [f"A_KEY={value}", f"TOKEN:{value}={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents"])

    assert result.exit_code != 0
    assert "Line 2" in _text(result)
    assert value not in _text(result)
    logged = "".join(f.read_text(encoding="utf-8") for f in paths.secrets_home().rglob("*.log"))
    assert value not in logged
    assert store.entries() == []


def test_Import_DryRun_AKeyThatIsAlsoAValue_IsNotPrinted(store, tmp_path):
    # The review's case: a value of one line is the key of another, and the dry-run table printed keys raw.
    # Short enough that the table never wraps it - a wrapped cell would pass "not in the output" without redaction.
    value = "SHARED_KEY_77"
    path = _env_file(tmp_path, [f"A_KEY={value}", f"{value}={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--dry-run"])

    # Refused outright now (a key holding a value would also leak through its entry name), by line number.
    assert result.exit_code != 0
    assert "Line 2" in _text(result), "the refusal did not print, so this test shows nothing"
    assert value not in _text(result)


@pytest.mark.parametrize("extra", [[], ["--replace"]])
def test_Import_TwoKeysBecomingTheSameEntry_AreRefused_AndNothingIsSaved(store, tmp_path, extra):
    path = _env_file(tmp_path, [f"API_KEY={new_secret()}", f"api_key={new_secret()}"])

    result = runner.invoke(cli.app, ["import", str(path), "--agents", *extra])
    dry = runner.invoke(cli.app, ["import", str(path), "--agents", "--dry-run", *extra])

    assert result.exit_code != 0 and dry.exit_code != 0
    assert "Lines 1 and 2" in _text(result)
    assert store.entries() == []


def test_Import_AValueIsStoredExactly_AndSurroundingSpacesAreRefusedNotTrimmed(store, tmp_path):
    inner = "in ner " + new_secret()
    spaced = _env_file(tmp_path, [f"SPACED={new_secret()}  "])

    refused = runner.invoke(cli.app, ["import", str(spaced), "--agents"])
    assert refused.exit_code != 0
    assert store.entries() == []

    exact = tmp_path / "exact.env"
    exact.write_text(f"INNER={inner}\n", encoding="utf-8")
    result = runner.invoke(cli.app, ["import", str(exact), "--agents"])
    assert result.exit_code == 0, _text(result)
    assert store.get("inner").secret.reveal() == inner


def test_Import_AKeyHoldingAnotherLinesValue_IsRefusedByLine_InEveryMode_AndNothingIsAudited(store, tmp_path):
    # The review's case (de0f0259): the key becomes a public entry name (secret-key-77) that reveals the value.
    value = "SECRET_KEY_77"
    path = _env_file(tmp_path, [f"A_KEY={value}", f"{value}={new_secret()}", f"secret_key_77={new_secret()}"])
    folded = value.lower().replace("_", "-")

    for extra in ([], ["--dry-run"], ["--replace"]):
        result = runner.invoke(cli.app, ["import", str(path), "--agents", *extra])
        text = _text(result).lower()
        assert result.exit_code != 0
        assert "line" in text
        assert folded not in text and value.lower() not in text

    assert store.entries() == []
    audit = paths.audit_path()
    assert not audit.exists() or folded not in audit.read_text(encoding="utf-8").lower()
    logged = "".join(f.read_text(encoding="utf-8") for f in paths.secrets_home().rglob("*.log")).lower()
    assert folded not in logged and value.lower() not in logged


@pytest.mark.skipif(sys.platform != "win32", reason="Windows ignores letter case in variable names")
def test_Run_TwoEntriesWhoseVariablesDifferOnlyInCase_AreRefusedOnWindows(store, tmp_path):
    # The review's case: an entry without a variable name falls back to FOO_BAR; an imported foo_bar is the same
    # variable on Windows, so one value would silently arrive under both.
    add_entry(store, name="foo.bar")
    _imported(store, tmp_path, [f"foo_bar={new_secret()}"])
    marker = tmp_path / "ran.txt"

    result = runner.invoke(cli.app, ["run", "foo.bar", "--with", "foo-bar", "--", PY, "-c",
                                     f"open(r'{marker}', 'w').write('ran')"])

    assert result.exit_code != 0
    assert "same variable" in _text(result)
    assert not marker.exists()

"""`cc-devthrottle license list | switch | add | remove` in the AXI shape (docs/axi-standard.md), and never a key.

The store's own behaviour is proven in test_license_store.py. These pin what an agent reads: the count line and
a definitive empty state, every record recoverable from the default output, `--json` with every field, usage
errors that exit 2, runtime errors that exit 1 with a next step, and - for every command, on success and on
failure - not one character of a key in standard output or standard error.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from cc_shared import axi_output  # noqa: E402
from src.cli import app  # noqa: E402
from src.license_store import FileKeyBackend, HeldKey  # noqa: E402

runner = CliRunner()

ACCOUNT_A = "account-a@example.com"
ACCOUNT_B = "account-b@example.com"


def _sign_in(folder: Path, email: str, serial: int) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    secret = json.dumps({"claudeAiOauth": {"accessToken": f"sk-ant-oat01-SECRET-{serial}",
                                           "refreshToken": f"sk-ant-ort01-SECRET-{serial}"}})
    FileKeyBackend().write(str(folder), HeldKey(secret))
    (folder / ".claude.json").write_text(json.dumps({"oauthAccount": {
        "emailAddress": email, "organizationName": "Org, with a comma"}}), encoding="utf-8")


@pytest.fixture
def machine(tmp_path, monkeypatch):
    """An isolated default folder and store, named through the environment as a Director would."""
    default, store = tmp_path / "default", tmp_path / "store"
    monkeypatch.setenv("CC_LICENSE_DEFAULT_CONFIG_DIR", str(default))
    monkeypatch.setenv("CC_LICENSE_STORE_DIR", str(store))
    # The file backend on every platform: the Keychain backend is proven with a fake `security` elsewhere.
    monkeypatch.setattr("src.license_store.sys.platform", "win32")
    return {"default": default, "store": store, "root": tmp_path}


def _run(*args):
    result = runner.invoke(app, ["license", *args])
    assert "SECRET" not in result.stdout and "SECRET" not in (result.stderr or ""), "a key reached the output"
    return result


def _with_active_and_spare(machine):
    _sign_in(machine["default"], ACCOUNT_A, 1)
    _sign_in(machine["root"] / "login-b", ACCOUNT_B, 2)
    assert _run("add", str(machine["root"] / "login-b")).exit_code == 0


def test_list_Empty_SaysCountZeroAndWhy(machine):
    result = _run("list")
    assert result.exit_code == 0
    assert result.stdout.startswith("count: 0")
    assert "No accounts held" in result.stdout
    assert "help[" in result.stdout


def test_list_EveryRecordIsRecoverableFromTheDefaultOutput(machine):
    _with_active_and_spare(machine)

    result = _run("list")

    assert result.exit_code == 0
    assert result.stdout.splitlines()[0] == "count: 2 (active 1, spare 1)"
    fields, records = axi_output.parse_list(result.stdout, "accounts")
    assert fields == ["email", "role", "organization"]
    assert [(r["email"], r["role"], r["organization"]) for r in records] == [
        (ACCOUNT_A, "active", "Org, with a comma"), (ACCOUNT_B, "spare", "Org, with a comma")]
    assert f"cc-devthrottle license switch {ACCOUNT_B}" in result.stdout


def test_list_Json_CarriesEveryFieldAndTheCount(machine):
    _with_active_and_spare(machine)

    data = json.loads(_run("list", "--json").stdout)

    assert data["count"] == 2
    assert data["defaultConfigDir"] == str(machine["default"])
    assert set(data["accounts"][0]) == {"email", "role", "organization", "configDir", "accountFile", "keyStore",
                                        "keyLocation"}
    assert data["accounts"][1]["keyStore"] == "file"


def test_list_Fields_ShowsTheChosenFields(machine):
    _with_active_and_spare(machine)
    fields, records = axi_output.parse_list(_run("list", "--fields", "email,configDir").stdout, "accounts")
    assert fields == ["email", "configDir"]
    assert records[0]["configDir"] == str(machine["default"])


@pytest.mark.parametrize("args", [["--fields", "email", "--json"], ["--fields", "bogus"], ["--bogus"]])
def test_list_UsageErrors_ExitTwo(machine, args):
    assert _run("list", *args).exit_code == 2


def test_list_FlagBeatsEnvironment(machine, tmp_path):
    other = tmp_path / "other-default"
    _sign_in(other, ACCOUNT_B, 9)
    data = json.loads(_run("list", "--default-config-dir", str(other), "--json").stdout)
    assert [a["email"] for a in data["accounts"]] == [ACCOUNT_B]


def test_list_RealDefaultFolder_IsRefusedWithANextStep(machine, monkeypatch):
    monkeypatch.delenv("CC_LICENSE_DEFAULT_CONFIG_DIR")
    result = _run("list")
    assert result.exit_code == 1
    assert "real default" in result.stderr
    assert "help[1]:" in result.stderr and "--default-config-dir" in result.stderr


def test_switch_ExchangesAndReportsVerified(machine):
    _with_active_and_spare(machine)

    result = _run("switch", ACCOUNT_B)

    assert result.exit_code == 0
    assert result.stdout.startswith(f"Switched: {ACCOUNT_B} is now the active account, read back and verified.")
    data = json.loads(_run("list", "--json").stdout)
    assert {a["email"]: a["role"] for a in data["accounts"]} == {ACCOUNT_B: "active", ACCOUNT_A: "spare"}


def test_switch_Json(machine):
    _with_active_and_spare(machine)
    data = json.loads(_run("switch", ACCOUNT_B, "--json").stdout)
    assert data == {"switched": True, "active": ACCOUNT_B, "previous": ACCOUNT_A,
                    "previousKeptIn": data["previousKeptIn"], "verified": True, "recovered": None}
    assert data["previousKeptIn"].startswith(str(machine["store"]))


def test_switch_AlreadyActive_SaysUnchanged(machine):
    _with_active_and_spare(machine)
    result = _run("switch", ACCOUNT_A)
    assert result.exit_code == 0 and result.stdout.startswith("Unchanged:")


def test_switch_UnknownAccount_ExitsOneWithANextStep(machine):
    _with_active_and_spare(machine)
    result = _run("switch", "nobody@example.com")
    assert result.exit_code == 1
    assert result.stderr.startswith("Error: nobody@example.com is not held here")
    assert "cc-devthrottle license list" in result.stderr


def test_add_MovesTheSigninAndSaysSo(machine):
    _sign_in(machine["root"] / "login-b", ACCOUNT_B, 2)

    result = _run("add", str(machine["root"] / "login-b"), "--json")

    data = json.loads(result.stdout)
    assert data["added"] == ACCOUNT_B and data["account"]["role"] == "spare"
    assert not (machine["root"] / "login-b" / ".credentials.json").exists()


def test_add_NotSignedIn_ExitsOne(machine):
    (machine["root"] / "empty").mkdir()
    result = _run("add", str(machine["root"] / "empty"))
    assert result.exit_code == 1 and "holds no sign-in key" in result.stderr


def test_remove_WithoutYesAndNoTerminal_IsAUsageError(machine):
    _with_active_and_spare(machine)
    result = _run("remove", ACCOUNT_B)
    assert result.exit_code == 2
    assert len(json.loads(_run("list", "--json").stdout)["accounts"]) == 2


def test_remove_WithYes_DeletesTheSpare(machine):
    _with_active_and_spare(machine)
    result = _run("remove", ACCOUNT_B, "--yes")
    assert result.exit_code == 0 and result.stdout.startswith(f"Removed: the spare sign-in for {ACCOUNT_B}")
    assert [a["email"] for a in json.loads(_run("list", "--json").stdout)["accounts"]] == [ACCOUNT_A]


def test_everyCommand_HasHelp():
    for command in ["list", "switch", "add", "remove"]:
        result = runner.invoke(app, ["license", command, "--help"])
        assert result.exit_code == 0, command

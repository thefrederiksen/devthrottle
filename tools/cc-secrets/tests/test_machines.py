"""The Secret Handoff mission, phase 2: this machine's key pair, the machines that can receive, and pinning."""

import base64
import hashlib
import json
import os

import pytest
from typer.testing import CliRunner

from src import cli, gateway_link, known_machines, machine_key, machines
from src.errors import CcSecretsError
from src.redact import SCRUBBER

runner = CliRunner()


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _other_key() -> bytes:
    from cryptography.hazmat.primitives import serialization
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey

    return X25519PrivateKey.generate().public_key().public_bytes(serialization.Encoding.Raw,
                                                                 serialization.PublicFormat.Raw)


def _row(name, public, **extra):
    row = {"machine": name, "publicKey": base64.b64encode(public).decode(), "lastSeenUtc": "2026-10-10T12:00:00Z",
           "fingerprint": hashlib.sha256(public).hexdigest(), "directors": 1}
    row.update(extra)
    return row


@pytest.fixture
def gateway(monkeypatch):
    """Stand in for the Gateway's list; records what was asked."""
    state = {"rows": [], "asked": []}

    def fake_get(path, link=None):
        state["asked"].append(path)
        return {"machines": state["rows"]}

    monkeypatch.setattr(gateway_link, "get", fake_get)
    return state


# --- The key pair -----------------------------------------------------------------------------------------------

def test_MachineKey_IsMadeOnce_AndTheSameKeyComesBack(home):
    first, created = machine_key.load_or_create()
    again, created_again = machine_key.load_or_create()

    assert (created, created_again) == (True, False)
    assert first.public == again.public
    assert first.fingerprint == hashlib.sha256(first.public).hexdigest()
    assert (home / machine_key.KEY_FILE).is_file()


def test_MachineKey_ThePrivateHalf_IsNeverShown_AndIsHiddenFromOutput(home):
    key, _ = machine_key.load_or_create()
    private_text = json.loads((home / machine_key.KEY_FILE).read_text(encoding="utf-8"))["privateKey"]

    result = runner.invoke(cli.app, ["machine-key", "--json"])

    assert result.exit_code == 0
    assert private_text not in _text(result)
    assert json.loads(result.stdout)["publicKey"] == key.public_b64
    assert private_text not in repr(key)
    assert SCRUBBER.scrub(f"x {private_text} y") != f"x {private_text} y"


def test_MachineKey_APublicKeyThatDoesNotMatchItsPrivateKey_IsRefused(home):
    machine_key.load_or_create()
    path = home / machine_key.KEY_FILE
    document = json.loads(path.read_text(encoding="utf-8"))
    document["publicKey"] = base64.b64encode(_other_key()).decode()
    path.write_text(json.dumps(document), encoding="utf-8")

    with pytest.raises(CcSecretsError, match="does not belong to its private key"):
        machine_key.load_or_create()


@pytest.mark.parametrize("text, reason", [("not base64!", "not valid base64"), (base64.b64encode(b"short").decode(), "5 bytes")])
def test_DecodePublicKey_RefusesWhatIsNotAKey(text, reason):
    with pytest.raises(CcSecretsError, match=reason):
        machine_key.decode_public_key(text)


# --- Pinning ----------------------------------------------------------------------------------------------------

def test_Pin_NewThenSameThenChanged_AndNamesCompareWithoutCase(home):
    assert known_machines.check("Mac-Mini", "aa").state == known_machines.NEW

    known_machines.pin("Mac-Mini", "aa")

    assert known_machines.check("mac-mini", "aa").state == known_machines.SAME
    changed = known_machines.check("MAC-MINI", "bb")
    assert changed.state == known_machines.CHANGED
    assert changed.pinned.fingerprint == "aa"


# --- The list ---------------------------------------------------------------------------------------------------

def test_Listed_MarksThisMachine_ByItsOwnKey_AndChecksEveryOtherAgainstThePin(home, gateway):
    own, _ = machine_key.load_or_create()
    mac = _other_key()
    known_machines.pin("devthrottle-mac-mini", "an older fingerprint")
    gateway["rows"] = [_row("SOREN_NORTH", own.public), _row("devthrottle-mac-mini", mac)]

    found = machines.listed(own)

    assert gateway["asked"] == ["gateway/secrets/machines"]
    assert [m.this_machine for m in found] == [True, False]
    assert machines.this_machine(found).name == "SOREN_NORTH"
    assert machines.find(found, "DEVTHROTTLE-MAC-MINI").pin.state == known_machines.CHANGED


def test_Listed_AFingerprintThatDoesNotMatchTheKey_IsRefused(home, gateway):
    own, _ = machine_key.load_or_create()
    gateway["rows"] = [_row("devthrottle-mac-mini", _other_key(), fingerprint="0" * 64)]

    with pytest.raises(CcSecretsError, match="does not match the key it sent"):
        machines.listed(own)


def test_Find_AMachineThatIsNotThere_NamesTheOnesThatAre(home, gateway):
    own, _ = machine_key.load_or_create()
    gateway["rows"] = [_row("SOREN_NORTH", own.public)]

    with pytest.raises(CcSecretsError, match=r"No machine called 'mac'.*machines that can: SOREN_NORTH"):
        machines.find(machines.listed(own), "mac")


def test_Find_AMachineInConflict_IsRefusedWithTheGatewaysReason(home, gateway):
    own, _ = machine_key.load_or_create()
    gateway["rows"] = [{"machine": "LINUX", "publicKey": "", "fingerprint": "", "directors": 2,
                        "conflict": "2 different keys are reported by the Directors on LINUX"}]

    with pytest.raises(CcSecretsError, match="2 different keys"):
        machines.find(machines.listed(own), "linux")


def test_ThisMachine_NotListed_SaysADirectorMustRunHere(home, gateway):
    own, _ = machine_key.load_or_create()
    gateway["rows"] = [_row("devthrottle-mac-mini", _other_key())]

    with pytest.raises(CcSecretsError, match="This machine is not in the Gateway's list"):
        machines.this_machine(machines.listed(own))


def test_MachinesCommand_PrintsEachMachine_WithItsKeyState(home, gateway, plain):
    own, _ = machine_key.load_or_create()
    gateway["rows"] = [_row("SOREN_NORTH", own.public), _row("devthrottle-mac-mini", _other_key())]

    result = runner.invoke(cli.app, ["machines"])

    text = plain(_text(result))
    assert result.exit_code == 0, text
    assert "SOREN_NORTH" in text and "devthrottle-mac-mini" in text
    assert "not sent to before" in text and "this machine" in text


# --- The credential ---------------------------------------------------------------------------------------------

def test_Resolve_InsideASession_UsesTheSessionsKey(monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "https://gateway.example")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "session-key")

    link = gateway_link.resolve()

    assert (link.kind, link.url, link.bearer) == ("session", "https://gateway.example", "session-key")


def test_Resolve_NotSignedIn_IsRefusedWithTheReason(monkeypatch):
    from cc_shared import tool_errors

    monkeypatch.delenv("CC_GATEWAY_URL", raising=False)
    monkeypatch.delenv("CC_GATEWAY_SESSION_KEY", raising=False)
    monkeypatch.setattr(tool_errors, "resolve_credential",
                        lambda: tool_errors.Credential("before-sign-in", "https://hosted.example", None))

    with pytest.raises(CcSecretsError, match="not signed in to a Gateway"):
        gateway_link.resolve()

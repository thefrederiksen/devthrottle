"""`cc-secrets seal` and `cc-secrets open`: one short value sealed to one machine, for one purpose and one id.

The License Switching mission uses the pair to carry the answer to an approved sign-in from the machine with the
browser to the machine that is waiting, through a Gateway that must not be able to read it.
"""

import json

import pytest
from typer.testing import CliRunner

from conftest import new_secret
from src import cli, machine_key, paths, sealing
from src.redact import SCRUBBER

runner = CliRunner()

PURPOSE = "license-approval"
ID = "0123456789abcdef0123456789abcdef"


def _receiver_key():
    result = runner.invoke(cli.app, ["machine-key", "--json"])
    assert result.exit_code == 0, result.output
    return json.loads(result.output)["publicKey"]


def _seal(value, to, purpose=PURPOSE, value_id=ID):
    return runner.invoke(cli.app, ["seal", "--to", to, "--purpose", purpose, "--id", value_id], input=value + "\n")


def _open(envelope, purpose=PURPOSE, value_id=ID):
    return runner.invoke(cli.app, ["open", "--purpose", purpose, "--id", value_id], input=envelope + "\n")


def test_SealThenOpen_OnTheReceivingMachine_GivesTheValueBack(home):
    value = new_secret()
    sealed = _seal(value, _receiver_key())
    assert sealed.exit_code == 0, sealed.output
    assert value not in sealed.output

    opened = _open(sealed.output.strip())

    assert opened.exit_code == 0, opened.output
    assert opened.output == value + "\n"


def test_Seal_NeverShowsTheValue_AndTwoSealsOfOneValueDiffer(home):
    value = new_secret()
    to = _receiver_key()

    first, second = _seal(value, to), _seal(value, to)

    assert first.exit_code == 0 and second.exit_code == 0
    assert first.output != second.output, "every envelope has its own ephemeral key"
    audit = paths.audit_path().read_text(encoding="utf-8")
    assert value not in audit and "seal license-approval" in audit


@pytest.mark.parametrize("purpose, value_id", [("license-approval", "f" * 32), ("something-else", ID)],
                         ids=["another-id", "another-purpose"])
def test_Open_ForAnotherIdOrPurpose_Fails_AndShowsNothing(home, purpose, value_id):
    value = new_secret()
    sealed = _seal(value, _receiver_key()).output.strip()

    opened = _open(sealed, purpose=purpose, value_id=value_id)

    assert opened.exit_code == cli.EXIT_FAILED
    assert value not in opened.output


def test_Open_OnAnotherMachine_Fails(home, tmp_path, monkeypatch):
    value = new_secret()
    sealed = _seal(value, _receiver_key()).output.strip()

    monkeypatch.setenv("CC_SECRETS_HOME", str(tmp_path / "another-machine"))
    monkeypatch.setattr(paths, "_checked_home", None)
    SCRUBBER.clear()
    opened = _open(sealed)

    assert opened.exit_code == cli.EXIT_FAILED
    assert value not in opened.output


def test_Open_InsideASession_IsRefused(home, monkeypatch):
    value = new_secret()
    sealed = _seal(value, _receiver_key()).output.strip()
    monkeypatch.setenv("CC_SESSION_ID", "11111111-2222-3333-4444-555555555555")

    opened = _open(sealed)

    assert opened.exit_code == cli.EXIT_REFUSED
    assert value not in opened.output


def test_Open_ToATerminal_IsRefused(home, monkeypatch):
    value = new_secret()
    sealed = _seal(value, _receiver_key()).output.strip()
    monkeypatch.setattr(cli, "_stdout_is_tty", lambda: True)

    opened = _open(sealed)

    assert opened.exit_code == cli.EXIT_REFUSED
    assert value not in opened.output


def test_Seal_WithNoValue_OrABadKey_Fails(home):
    assert runner.invoke(cli.app, ["seal", "--to", _receiver_key(), "--purpose", PURPOSE, "--id", ID],
                         input="\n").exit_code == cli.EXIT_FAILED
    assert _seal(new_secret(), "not-a-key").exit_code == cli.EXIT_FAILED
    assert _seal(new_secret(), _receiver_key(), purpose="Has Spaces").exit_code == cli.EXIT_REFUSED
    assert _seal(new_secret(), _receiver_key(), value_id="a/b").exit_code == cli.EXIT_REFUSED


def test_TheTwoEnvelopes_NeverOpenAsEachOther(home):
    receiver, _ = machine_key.load_or_create()
    facts = {"purpose": PURPOSE, "id": ID, "receiverFingerprint": receiver.fingerprint}
    value_envelope = sealing.seal_value(b"the answer", receiver.public, facts)
    transfer_facts = {"transferId": ID, "entry": "e", "targetName": "e", "fromMachine": "a", "toMachine": "b",
                      "replace": False, "expiresAtUtc": "2026-10-13T00:00:00Z", "senderFingerprint": receiver.fingerprint,
                      "receiverFingerprint": receiver.fingerprint}
    transfer_envelope = sealing.seal(b"the entry", receiver.private, receiver.public, transfer_facts)

    assert sealing.open_value(value_envelope, receiver.private, facts) == b"the answer"
    with pytest.raises(sealing.EnvelopeError):
        sealing.open_value(transfer_envelope, receiver.private, facts)
    with pytest.raises(sealing.EnvelopeError):
        sealing.open_envelope(value_envelope, receiver.private, receiver.public, transfer_facts)

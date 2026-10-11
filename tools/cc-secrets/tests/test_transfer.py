"""The Secret Handoff mission, phase 4: sealing an entry for another machine, and both halves of moving it.

Two machines are two secrets folders. Every entry here is made up for the test.
"""

import base64
import json
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone

import pytest
from typer.testing import CliRunner

from src import cli, filelog, gateway_link, known_machines, machine_key, machines, paths, sealing, transfer, transfers
from src.audit import AuditLog
from src.redact import SCRUBBER
from src.saving import save_entry
from src.store import SecretStore
from src.storefile import UserOnlyFile

runner = CliRunner()

TID = "0123456789abcdef0123456789abcdef"
SECRET = "qa-handoff-Vq7#not-a-real-password-91"
NOW = datetime(2026, 10, 10, 12, 0, 0, tzinfo=timezone.utc)


@contextmanager
def on(folder, monkeypatch):
    """Act as the machine whose secrets folder is `folder`."""
    monkeypatch.setenv("CC_SECRETS_HOME", str(folder))
    monkeypatch.setattr(paths, "_checked_home", None)
    yield SecretStore(UserOnlyFile(paths.store_path())), AuditLog(paths.audit_path())


@pytest.fixture
def two(tmp_path, monkeypatch):
    """Two machines, each with its own key; NORTH holds one made-up entry."""
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    SCRUBBER.clear()
    north, mac = tmp_path / "north", tmp_path / "mac"
    with on(mac, monkeypatch):
        mac_key, _ = machine_key.load_or_create()
    with on(north, monkeypatch) as (store, audit):
        north_key, _ = machine_key.load_or_create()
        save_entry(store, audit, "qa-handoff-one", "qa-user", SECRET, ["https://example.com"], "made up for a test",
                   ["run", "login"], "QA_HANDOFF", False, "add", "", None)
    yield {"north": north, "mac": mac, "north_key": north_key, "mac_key": mac_key, "mp": monkeypatch}
    SCRUBBER.clear()


def _facts(t, **change):
    facts = {"transferId": TID, "entry": "qa-handoff-one", "targetName": "qa-handoff-one", "fromMachine": "SOREN_NORTH",
             "toMachine": "devthrottle-mac-mini", "replace": False,
             "expiresAtUtc": (NOW + timedelta(minutes=5)).isoformat().replace("+00:00", "Z"),
             "approvedWhere": "phone", "approvalWords": None,
             "receiverPublicKey": t["mac_key"].public_b64, "receiverFingerprint": t["mac_key"].fingerprint,
             "acceptedReceiverFingerprint": None}
    facts.update(change)
    return facts


def _send(t, **change):
    with on(t["north"], t["mp"]) as (store, audit):
        return transfer.send_half(_facts(t, **change), TID, store, audit)


def _receive_payload(t, sent, **change):
    payload = _facts(t)
    payload.pop("receiverPublicKey")
    payload.pop("acceptedReceiverFingerprint")
    payload.update({"senderPublicKey": sent["senderPublicKey"], "senderFingerprint": sent["senderFingerprint"],
                    "envelope": sent["envelope"]})
    payload.update(change)
    return payload


def _receive(t, payload, now=NOW):
    with on(t["mac"], t["mp"]) as (store, audit):
        return transfer.receive_half(payload, TID, store, audit, now=now)


def _mac_entry(t, name="qa-handoff-one"):
    with on(t["mac"], t["mp"]) as (store, _):
        return store.get(name)


# --- The envelope ------------------------------------------------------------------------------------------------

def _bound(t, **change):
    facts = {"transferId": TID, "entry": "e", "targetName": "e", "fromMachine": "A", "toMachine": "B", "replace": False,
             "expiresAtUtc": "2026-10-10T12:05:00Z", "senderFingerprint": t["north_key"].fingerprint,
             "receiverFingerprint": t["mac_key"].fingerprint}
    facts.update(change)
    return facts


def test_Seal_OpensOnlyForTheReceiver_AndTheFactsItWasSealedFor(two):
    envelope = sealing.seal(b"the entry", two["north_key"].private, two["mac_key"].public, _bound(two))

    assert sealing.open_envelope(envelope, two["mac_key"].private, two["north_key"].public, _bound(two)) == b"the entry"
    assert b"the entry" not in base64.b64decode(envelope)


@pytest.mark.parametrize("fact,value", [("transferId", "f" * 32), ("entry", "other"), ("targetName", "other"),
                                        ("fromMachine", "X"), ("toMachine", "X"), ("replace", True),
                                        ("expiresAtUtc", "2026-10-10T13:00:00Z"), ("senderFingerprint", "0" * 64),
                                        ("receiverFingerprint", "0" * 64)])
def test_Open_AnyChangedFact_DoesNotOpen(two, fact, value):
    envelope = sealing.seal(b"the entry", two["north_key"].private, two["mac_key"].public, _bound(two))

    with pytest.raises(sealing.EnvelopeError):
        sealing.open_envelope(envelope, two["mac_key"].private, two["north_key"].public, _bound(two, **{fact: value}))


def test_Open_ByAnotherMachine_OrClaimingAnotherSender_DoesNotOpen(two):
    envelope = sealing.seal(b"the entry", two["north_key"].private, two["mac_key"].public, _bound(two))

    with pytest.raises(sealing.EnvelopeError):
        sealing.open_envelope(envelope, two["north_key"].private, two["north_key"].public, _bound(two))
    with pytest.raises(sealing.EnvelopeError):
        sealing.open_envelope(envelope, two["mac_key"].private, two["mac_key"].public, _bound(two))


def test_Open_AChangedByte_DoesNotOpen(two):
    envelope = sealing.seal(b"the entry", two["north_key"].private, two["mac_key"].public, _bound(two))
    document = json.loads(base64.b64decode(envelope))
    sealed = bytearray(base64.b64decode(document["ct"]))
    sealed[0] ^= 1
    document["ct"] = base64.b64encode(bytes(sealed)).decode()
    tampered = base64.b64encode(json.dumps(document).encode()).decode()

    with pytest.raises(sealing.EnvelopeError):
        sealing.open_envelope(tampered, two["mac_key"].private, two["north_key"].public, _bound(two))


# --- Both halves -------------------------------------------------------------------------------------------------

def test_ATransfer_StoresTheWholeEntryOnTheReceiver_WithEveryFieldIntact(two):
    sent = _send(two)

    answer = _receive(two, _receive_payload(two, sent))

    assert answer == {"ok": True, "stored": "qa-handoff-one"}
    got = _mac_entry(two)
    assert (got.username, got.secret.reveal(), got.allowed_domains, got.notes, got.uses, got.env_name, got.kind) == \
        ("qa-user", SECRET, ["https://example.com"], "made up for a test", ["login", "run"], "QA_HANDOFF", "secret")


def test_ATransfer_UnderAnotherName_IsStoredUnderThatName(two):
    sent = _send(two, targetName="qa-handoff-renamed")

    _receive(two, _receive_payload(two, sent, targetName="qa-handoff-renamed"))

    assert _mac_entry(two, "qa-handoff-renamed") is not None
    assert _mac_entry(two) is None


def test_Receive_TheSameEnvelopeTwice_IsRefusedTheSecondTime(two):
    # Approved to replace, so the name being taken after the first delivery does not stop the second: only the
    # once-per-transfer rule can.
    sent = _send(two, replace=True)
    payload = _receive_payload(two, sent, replace=True)
    _receive(two, payload)

    with pytest.raises(transfer.TransferRefused, match="already received"):
        _receive(two, payload)


def test_Receive_PastItsTime_IsRefused(two):
    sent = _send(two)

    with pytest.raises(transfer.TransferRefused, match="had to arrive by"):
        _receive(two, _receive_payload(two, sent), now=NOW + timedelta(minutes=6))
    assert _mac_entry(two) is None


def test_Receive_ANameAlreadyThere_IsRefusedUnlessTheApprovalSaidReplace(two):
    with on(two["mac"], two["mp"]) as (store, audit):
        save_entry(store, audit, "qa-handoff-one", "old", "qa-handoff-old-made-up-value-77", [], "", ["run"], "", False,
                   "add", "", None)
    sent = _send(two)

    with pytest.raises(transfer.TransferRefused, match="did not say it replaces it"):
        _receive(two, _receive_payload(two, sent))

    replacing = _send(two, replace=True)
    _receive(two, _receive_payload(two, replacing, replace=True))
    assert _mac_entry(two).username == "qa-user"


def test_Receive_AnEnvelopeRedirectedToAnotherTransfer_DoesNotOpen(two):
    sent = _send(two)
    other = "f" * 32

    with on(two["mac"], two["mp"]) as (store, audit):
        with pytest.raises(sealing.EnvelopeError):
            transfer.receive_half(_receive_payload(two, sent, transferId=other), other, store, audit, now=NOW)
    assert _mac_entry(two) is None


def test_Receive_FromAMachineWhoseKeyChanged_IsRefused(two):
    with on(two["mac"], two["mp"]):
        known_machines.pin("SOREN_NORTH", "a" * 64)
    sent = _send(two)

    with pytest.raises(transfer.TransferRefused, match="has changed since this machine pinned it"):
        _receive(two, _receive_payload(two, sent))


def test_Receive_SealedForAnotherKey_IsRefused(two):
    sent = _send(two)

    with pytest.raises(transfer.TransferRefused, match="different key"):
        _receive(two, _receive_payload(two, sent, receiverFingerprint=two["north_key"].fingerprint))


def test_Send_AnEntryThatIsNotThere_IsRefused(two):
    with pytest.raises(transfer.TransferRefused, match="No entry named 'qa-handoff-missing'"):
        _send(two, entry="qa-handoff-missing")


def test_Send_ToAMachineWhoseKeyChanged_IsRefused_UnlessTheOwnerAcceptedThatKey(two):
    with on(two["north"], two["mp"]):
        known_machines.pin("devthrottle-mac-mini", "b" * 64)

    with pytest.raises(transfer.TransferRefused, match="has changed since this machine pinned it"):
        _send(two)

    sent = _send(two, acceptedReceiverFingerprint=two["mac_key"].fingerprint)
    assert sent["ok"] is True
    with on(two["north"], two["mp"]):
        assert known_machines.check("devthrottle-mac-mini", two["mac_key"].fingerprint).state == known_machines.SAME


def test_Send_AReceiverKeyThatDoesNotMatchItsFingerprint_IsRefused(two):
    with pytest.raises(transfer.TransferRefused, match="does not match its fingerprint"):
        _send(two, receiverFingerprint="c" * 64)


def test_TheSecret_IsInNoAuditLineAndNoLogLine_OnEitherMachine(two):
    sent = _send(two)
    _receive(two, _receive_payload(two, sent))

    for folder in (two["north"], two["mac"]):
        assert SECRET not in (folder / "secrets-audit.log").read_text(encoding="utf-8")
    assert SECRET not in json.dumps(sent)
    north_log = "".join(p.read_text(encoding="utf-8") for p in (two["north"] / "logs").glob("cc-secrets-*.log"))
    mac_log = "".join(p.read_text(encoding="utf-8") for p in (two["mac"] / "logs").glob("cc-secrets-*.log"))
    # The lines are there (so the search looked at a real log), and the secret is not.
    assert "[transfer] send_half" in north_log and "[transfer] receive_half" in mac_log
    assert SECRET not in north_log and SECRET not in mac_log


def test_TheAuditLines_SayTransferSentAndTransferReceived_WithWhereItWasApproved(two):
    sent = _send(two, approvedWhere="chat", approvalWords="yes, send it")
    _receive(two, _receive_payload(two, sent, approvedWhere="chat", approvalWords="yes, send it"))

    def last(folder):
        lines = [json.loads(x) for x in (folder / "secrets-audit.log").read_text(encoding="utf-8").splitlines() if x]
        return lines[-1]

    north, mac = last(two["north"]), last(two["mac"])
    assert (north["command"], mac["command"]) == ("transfer sent", "transfer received")
    assert "approved in the chat" in north["detail"] and "approved in the chat" in mac["detail"]
    assert "yes, send it" in json.dumps(north) and "yes, send it" in json.dumps(mac)


# --- The commands the Director runs ------------------------------------------------------------------------------

def test_TransferSendCommand_PrintsOneJsonLine_WithTheEnvelope_AndNeverTheSecret(two):
    with on(two["north"], two["mp"]):
        result = runner.invoke(cli.app, ["transfer-send", TID], input=json.dumps(_facts(two)))

    assert result.exit_code == 0, result.output
    answer = json.loads(result.stdout.strip())
    assert answer["ok"] is True and answer["senderFingerprint"] == two["north_key"].fingerprint
    assert SECRET not in result.output


def test_TransferReceiveCommand_ARefusal_IsOneJsonLine_WithExit2(two):
    with on(two["mac"], two["mp"]):
        result = runner.invoke(cli.app, ["transfer-receive", TID], input=json.dumps({"transferId": TID}))

    assert result.exit_code == cli.EXIT_REFUSED
    answer = json.loads(result.stdout.strip())
    assert answer["ok"] is False and "missing" in answer["reason"]


def test_TransferSendCommand_AnIdThatDoesNotMatchTheFacts_IsRefused(two):
    with on(two["north"], two["mp"]):
        result = runner.invoke(cli.app, ["transfer-send", "e" * 32], input=json.dumps(_facts(two)))

    assert result.exit_code == cli.EXIT_REFUSED
    assert "does not match" in json.loads(result.stdout.strip())["reason"]


# --- send and request ---------------------------------------------------------------------------------------------

def _dto(state, **extra):
    row = {"transferId": TID, "entry": "qa-handoff-one", "targetName": "qa-handoff-one", "fromMachine": "SOREN_NORTH",
           "toMachine": "devthrottle-mac-mini", "state": state, "summary": "qa-handoff-one from SOREN_NORTH to devthrottle-mac-mini",
           "statusText": "", "outcome": "", "expiresAtUtc": "2026-10-10T12:15:00Z", "canAnswer": state == "waiting"}
    row.update(extra)
    return row


@pytest.fixture
def gateway(two, monkeypatch):
    """NORTH asking: the Gateway lists both machines and walks the transfer through `states`."""
    state = {"kind": "session", "states": ["waiting", "delivered"], "posted": [], "refuse": None}

    def rows():
        return [{"machine": "SOREN_NORTH", "publicKey": two["north_key"].public_b64, "fingerprint": two["north_key"].fingerprint,
                 "lastSeenUtc": "", "directors": 1},
                {"machine": "devthrottle-mac-mini", "publicKey": two["mac_key"].public_b64,
                 "fingerprint": two["mac_key"].fingerprint, "lastSeenUtc": "", "directors": 1}]

    def fake_get(path, link=None):
        if path == machines.ROUTE:
            return {"machines": rows()}
        current = state["states"].pop(0) if len(state["states"]) > 1 else state["states"][0]
        return {"transfer": _dto(current, outcome="Denied on the phone. Nothing was moved." if current == "denied" else "")}

    def fake_post(path, body, link=None):
        state["posted"].append((path, body))
        if state["refuse"]:
            raise state["refuse"]
        return {"transfer": _dto(state["states"].pop(0) if len(state["states"]) > 1 else state["states"][0]), "note": ""}

    monkeypatch.setattr(gateway_link, "resolve", lambda: gateway_link.Link(state["kind"], "https://g.example", "k"))
    monkeypatch.setattr(gateway_link, "get", fake_get)
    monkeypatch.setattr(gateway_link, "post", fake_post)
    monkeypatch.setattr(cli.time, "sleep", lambda seconds: None)
    monkeypatch.setenv("CC_SECRETS_HOME", str(two["north"]))
    monkeypatch.setattr(paths, "_checked_home", None)
    return state


def test_Send_InASession_WaitsForTheAnswer_ThenSaysStored(gateway, plain):
    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini", "--reason", "sudo on the mac"])

    assert result.exit_code == 0, result.output
    path, body = gateway["posted"][0]
    assert (body["fromMachine"], body["toMachine"], body["reason"]) == ("SOREN_NORTH", "devthrottle-mac-mini", "sudo on the mac")
    assert "approvedHere" not in body
    assert "stored qa-handoff-one on devthrottle-mac-mini" in plain(result.output)


def test_Send_Denied_ExitsFour(gateway):
    gateway["states"] = ["waiting", "denied"]

    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini", "--reason", "r"])

    assert result.exit_code == cli.EXIT_DENIED


def test_Send_Expired_ExitsFive(gateway):
    gateway["states"] = ["waiting", "expired"]

    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini", "--reason", "r"])

    assert result.exit_code == cli.EXIT_EXPIRED


def test_Send_AnEntryNotOnThisMachine_IsRefusedBeforeAsking(gateway):
    result = runner.invoke(cli.app, ["send", "qa-handoff-missing", "--to", "devthrottle-mac-mini", "--reason", "r"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert gateway["posted"] == []


def test_Send_InTheOwnersTerminal_ApprovesWithYes_AsTheTerminal(gateway, monkeypatch):
    gateway["kind"] = "machine"
    gateway["states"] = ["approved", "delivered"]
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini"], input="yes\n")

    assert result.exit_code == 0, result.output
    body = gateway["posted"][0][1]
    assert (body["approvedHere"], body["askedOn"]) == ("terminal", "SOREN_NORTH")


def test_Send_InTheOwnersTerminal_NotYes_AsksNothing(gateway, monkeypatch):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini"], input="no\n")

    assert result.exit_code == cli.EXIT_CANCELLED
    assert gateway["posted"] == []


def test_Send_ASessionCannotAcceptAChangedKey(gateway):
    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini", "--reason", "r",
                                     "--accept-new-key"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert gateway["posted"] == []


def test_Request_FromAnotherMachine_AsksForItToComeHere_WithTheOwnersWords(gateway, plain):
    gateway["states"] = ["approved", "delivered"]

    result = runner.invoke(cli.app, ["request", "qa-handoff-one", "--from", "devthrottle-mac-mini", "--reason",
                                     "deploy key", "--owner-approved", "yes, send it"])

    assert result.exit_code == 0, result.output
    body = gateway["posted"][0][1]
    assert (body["fromMachine"], body["toMachine"], body["ownerApproved"]) == ("devthrottle-mac-mini", "SOREN_NORTH", "yes, send it")


def test_Send_TheGatewayRefuses_ExitsTwoWithItsSentence(gateway, plain):
    gateway["refuse"] = gateway_link.GatewayRefusal(409, "machine_offline", "devthrottle-mac-mini is not connected.")

    result = runner.invoke(cli.app, ["send", "qa-handoff-one", "--to", "devthrottle-mac-mini", "--reason", "r"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "devthrottle-mac-mini is not connected." in plain(result.output)

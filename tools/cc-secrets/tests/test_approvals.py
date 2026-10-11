"""The Secret Handoff mission, phase 3: the owner's one answer to a secret transfer, from cc-secrets."""

import json

import pytest
from typer.testing import CliRunner

from src import cli, gateway_link

runner = CliRunner()

TID = "0123456789abcdef0123456789abcdef"


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _dto(state="waiting", can_answer=True, **extra):
    row = {"transferId": TID, "entry": "qa-handoff-one", "targetName": "qa-handoff-one", "fromMachine": "SOREN_NORTH",
           "toMachine": "devthrottle-mac-mini", "replace": False, "askedBy": 'Session 104 "tests"',
           "reason": "sudo on the mac", "state": state, "expiresAtUtc": "2026-10-10T12:15:00Z",
           "summary": "qa-handoff-one from SOREN_NORTH to devthrottle-mac-mini",
           "statusText": "Waiting for your answer. Asked by Session 104 \"tests\".", "canAnswer": can_answer}
    row.update(extra)
    return row


@pytest.fixture
def gateway(monkeypatch, home):
    """Stand in for the Gateway: records every call, answers from `state`."""
    state = {"kind": "session", "rows": [_dto()], "posted": [], "refuse": None}

    monkeypatch.setattr(gateway_link, "resolve", lambda: gateway_link.Link(state["kind"], "https://g.example", "k"))

    def fake_get(path, link=None):
        if path == "gateway/secrets/transfers":
            return {"transfers": state["rows"]}
        return {"transfer": state["rows"][0], "note": ""}

    def fake_post(path, body, link=None):
        state["posted"].append((path, body))
        if state["refuse"]:
            raise state["refuse"]
        where = body.get("where") or "chat"
        answered = _dto(state="approved" if body["approve"] else "denied", can_answer=False, answeredWhere=where)
        return {"transfer": answered, "note": "Approved. Delivering now." if body["approve"] else "Denied. Nothing was moved."}

    monkeypatch.setattr(gateway_link, "get", fake_get)
    monkeypatch.setattr(gateway_link, "post", fake_post)
    return state


def test_Approvals_ShowsWhatIsWaiting_WhoAskedAndWhy_AndHowToAnswer(gateway, plain):
    result = runner.invoke(cli.app, ["approvals"])

    text = plain(_text(result))
    assert result.exit_code == 0, text
    assert "qa-handoff-one from SOREN_NORTH to devthrottle-mac-mini" in text
    assert "Why: sudo on the mac" in text
    assert f"cc-secrets approve {TID}" in text and f"cc-secrets deny {TID}" in text


def test_Approvals_Json_GivesEachTransfersFacts(gateway):
    result = runner.invoke(cli.app, ["approvals", "--json"])

    assert result.exit_code == 0, _text(result)
    row = json.loads(result.stdout)["transfers"][0]
    assert (row["transferId"], row["state"], row["canAnswer"]) == (TID, "waiting", True)


def test_Approve_InASession_SendsTheOwnersWords_AndNoPlace(gateway, plain):
    result = runner.invoke(cli.app, ["approve", TID, "--owner-approved", "yes, send it"])

    assert result.exit_code == 0, _text(result)
    path, body = gateway["posted"][0]
    assert path == f"gateway/secrets/transfers/{TID}/answer"
    assert body == {"approve": True, "deny": False, "ownerApproved": "yes, send it"}
    assert "approved:" in plain(_text(result))


def test_Approve_TheGatewayRefuses_IsARefusalWithItsSentence_NotAFailure(gateway, plain):
    gateway["refuse"] = gateway_link.GatewayRefusal(403, "owner_words_required",
                                                    "A session approves a transfer only with the owner's own words.")

    result = runner.invoke(cli.app, ["approve", TID])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "refused: A session approves a transfer only with the owner's own words." in plain(_text(result))


def test_Approve_InTheOwnersTerminal_AsksForYes_AndAnswersFromTheTerminal(gateway, monkeypatch, plain):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID], input="yes\n")

    assert result.exit_code == 0, _text(result)
    assert gateway["posted"][0][1] == {"approve": True, "deny": False, "where": "terminal"}
    assert "Type yes to approve this transfer" in plain(_text(result))


def test_Approve_InTheOwnersTerminal_AnythingButYes_ApprovesNothing(gateway, monkeypatch):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID], input="y\n")

    assert result.exit_code == cli.EXIT_CANCELLED
    assert gateway["posted"] == []


def test_Approve_OutsideASessionWithNobodyAtTheKeyboard_IsRefused(gateway, monkeypatch):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: False)

    result = runner.invoke(cli.app, ["approve", TID])

    assert result.exit_code == cli.EXIT_REFUSED
    assert gateway["posted"] == []


def test_Approve_ATransferThatCanNoLongerBeAnswered_IsRefusedBeforeAsking(gateway, monkeypatch, plain):
    gateway["kind"] = "machine"
    gateway["rows"] = [_dto(state="expired", can_answer=False,
                            statusText="Expired: nobody answered within 15 minutes. Nothing was moved.")]
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID], input="yes\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert gateway["posted"] == []
    assert "Expired: nobody answered" in plain(_text(result))


def test_Deny_NeedsNoConfirmation_AndSaysNothingMoved(gateway, plain):
    gateway["kind"] = "machine"

    result = runner.invoke(cli.app, ["deny", TID])

    assert result.exit_code == 0, _text(result)
    assert gateway["posted"][0][1] == {"approve": False, "deny": True, "where": "terminal"}
    assert "Nothing was moved" in plain(_text(result))


def test_Answer_IsAudited_WithTheOwnersWords(gateway, home):
    runner.invoke(cli.app, ["approve", TID, "--owner-approved", "yes, send it"])

    lines = [json.loads(line) for line in (home / "secrets-audit.log").read_text(encoding="utf-8").splitlines() if line.strip()]
    line = lines[-1]
    assert (line["entry"], line["command"], line["outcome"]) == (f"transfer {TID}", "approve", "ok")
    assert "yes, send it" in json.dumps(line)


def test_GatewayLink_A4xxWithACode_IsARefusal_AndAnythingElseIsAFailure(monkeypatch):
    from cc_shared import gateway

    def refuse(path, bearer=None, base_url=None):
        exc = gateway.GatewayError("409")
        exc.status = 409
        exc.body = {"code": "already_answered", "error": "Already approved on the phone."}
        raise exc

    def broken(path, bearer=None, base_url=None):
        raise gateway.GatewayError("connection refused")

    link = gateway_link.Link("session", "https://g.example", "k")
    monkeypatch.setattr(gateway, "get_json", refuse)
    with pytest.raises(gateway_link.GatewayRefusal) as refused:
        gateway_link.get("gateway/secrets/transfers", link)
    assert (refused.value.code, str(refused.value)) == ("already_answered", "Already approved on the phone.")

    monkeypatch.setattr(gateway, "get_json", broken)
    with pytest.raises(gateway_link.CcSecretsError) as failed:
        gateway_link.get("gateway/secrets/transfers", link)
    assert not isinstance(failed.value, gateway_link.GatewayRefusal)


def _json_part(stdout: str) -> str:
    """The test runner echoes typed input onto standard output, which a real terminal does not; skip that echo."""
    return stdout[stdout.index("{"):]


# --- The phase 3 review -------------------------------------------------------------------------------------------

def test_Approve_InTheOwnersTerminal_WithOwnerApproved_IsRefusedBeforeAskingForYes(gateway, monkeypatch, plain):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID, "--owner-approved", "yes"], input="yes\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert gateway["posted"] == []
    text = plain(_text(result))
    assert "--owner-approved is how a session reports" in text and "Type yes" not in text


def test_Approve_InAGitBashWindow_CountsAsSomeoneAtTheKeyboard(gateway, monkeypatch):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: False)
    monkeypatch.setattr(cli, "_stdin_is_mintty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID], input="yes\n")

    assert result.exit_code == 0, _text(result)
    assert gateway["posted"][0][1]["where"] == "terminal"


@pytest.mark.parametrize("tty,typed,code", [(False, "", 2), (True, "no\n", 3)])
def test_Approve_Json_KeepsItsShape_OnALocalRefusalOrCancel(gateway, monkeypatch, tty, typed, code):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: tty)
    monkeypatch.setattr(cli, "_stdin_is_mintty", lambda: False)

    result = runner.invoke(cli.app, ["approve", TID, "--json"], input=typed)

    assert result.exit_code == code
    answer = json.loads(_json_part(result.stdout))
    assert answer["outcome"] == ("refused" if code == 2 else "cancelled")


def test_Approve_Json_WithYes_PrintsOnlyJsonOnStandardOutput(gateway, monkeypatch):
    gateway["kind"] = "machine"
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["approve", TID, "--json"], input="yes\n")

    assert result.exit_code == 0, _text(result)
    assert json.loads(_json_part(result.stdout))["outcome"] == "approved"


@pytest.mark.parametrize("typed", ["../machines", "not-an-id", "ABC"])
def test_Approve_AnIdThatIsNotOne_IsRefusedLocally_AndNothingIsAsked(gateway, typed):
    asked = []
    gateway_link.get, original = (lambda path, link=None: asked.append(path) or {}), gateway_link.get
    try:
        result = runner.invoke(cli.app, ["approve", typed])
    finally:
        gateway_link.get = original

    assert result.exit_code == cli.EXIT_REFUSED
    assert asked == [] and gateway["posted"] == []


def test_Find_AsksForTheIdThatWasTyped(monkeypatch, home):
    from src import transfers

    asked = []
    monkeypatch.setattr(gateway_link, "get", lambda path, link=None: asked.append(path) or {"transfer": _dto()})

    transfers.find(TID, gateway_link.Link("session", "https://g.example", "k"))

    assert asked == [f"gateway/secrets/transfers/{TID}"]


def test_Approve_InASession_AuditsTheSessionsName(gateway, monkeypatch, home):
    monkeypatch.setenv("CC_SESSION_ID", "aaaaaaaa-0000-0000-0000-000000000001")
    monkeypatch.setattr(cli, "_session_name", lambda sid: "Session 104 tests")

    runner.invoke(cli.app, ["approve", TID, "--owner-approved", "yes, send it"])

    line = json.loads((home / "secrets-audit.log").read_text(encoding="utf-8").splitlines()[-1])
    assert "Session 104 tests" in json.dumps(line)


def test_Approvals_NotSignedIn_IsARefusal_NotAFault(monkeypatch, home, plain):
    def not_signed_in():
        raise gateway_link.NotSignedIn("This machine is not signed in to a Gateway.")

    monkeypatch.setattr(gateway_link, "resolve", not_signed_in)

    result = runner.invoke(cli.app, ["approvals"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "refused: This machine is not signed in" in plain(_text(result))


def test_Approvals_NothingWaiting_SaysSo(gateway, plain):
    gateway["rows"] = []

    result = runner.invoke(cli.app, ["approvals"])

    assert result.exit_code == 0
    assert "No secret transfer is waiting" in plain(_text(result))


def test_Approvals_AShapeTheToolDoesNotRead_IsAFailure(gateway, monkeypatch):
    monkeypatch.setattr(gateway_link, "get", lambda path, link=None: {"unexpected": True})

    result = runner.invoke(cli.app, ["approvals"])

    assert result.exit_code == cli.EXIT_FAILED

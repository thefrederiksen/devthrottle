"""Owner commands inside a session on the owner's word (issue 3414, owner decision 2026-09-26).

Inside a DevThrottle session add, edit, remove and import refuse unless the session passes
--owner-approved "<the owner's words>". Every use is audited with those words and the session's
id and name; the secret value still never goes on a command line or into any output.
"""

import json

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, paths
from src.audit import AuditLog

runner = CliRunner()

SESSION = "approval-test-session"
SESSION_NAME = "Secrets - Developer - test"
APPROVAL = "Soren approved in chat 2026-09-26: update the username"


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _audit_lines():
    return AuditLog(paths.audit_path()).read(100)


@pytest.fixture
def in_session(monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SESSION)
    monkeypatch.setattr(cli, "_session_name", lambda session_id: SESSION_NAME)


def test_Edit_InsideASession_WithoutApproval_IsRefused_ChangesNothing_AndIsAudited(store, in_session):
    add_entry(store, name="web", username="admin@example.com")

    result = runner.invoke(cli.app, ["edit", "web", "--username", "jake@example.com"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert "--owner-approved" in _text(result)
    assert store.get("web").username == "admin@example.com"
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"], line["session"]) == ("web", "edit", "refused", SESSION)


def test_Edit_InsideASession_WithApproval_IsApplied_AndAuditedWithTheWords(store, in_session):
    secret = add_entry(store, name="web", username="admin@example.com")

    result = runner.invoke(cli.app, ["edit", "web", "--username", "jake@example.com", "--owner-approved", APPROVAL])

    assert result.exit_code == 0, _text(result)
    entry = store.get("web")
    assert entry.username == "jake@example.com"
    assert entry.secret.reveal() == secret
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"]) == ("web", "edit", "ok")
    assert (line["session"], line["sessionName"], line["ownerApproved"]) == (SESSION, SESSION_NAME, APPROVAL)
    assert "username" in line["detail"]
    assert secret not in _text(result) and secret not in json.dumps(line)


def test_Approval_CoversOneCommand_TheNextOneIsRefusedAgain(store, in_session):
    add_entry(store, name="web", username="me")

    assert runner.invoke(cli.app, ["edit", "web", "--notes", "one", "--owner-approved", APPROVAL]).exit_code == 0
    result = runner.invoke(cli.app, ["edit", "web", "--notes", "two"])

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.get("web").notes == "one"


def test_EmptyApproval_IsRefused_EvenOutsideASession(store, monkeypatch):
    add_entry(store, name="web", username="me")

    for session in (SESSION, None):
        if session:
            monkeypatch.setenv("CC_SESSION_ID", session)
        else:
            monkeypatch.delenv("CC_SESSION_ID", raising=False)
        result = runner.invoke(cli.app, ["edit", "web", "--username", "x", "--owner-approved=   "])
        assert result.exit_code == cli.EXIT_REFUSED
        assert store.get("web").username == "me"


def test_Add_InsideASession_WithApproval_TakesTheValueOnStdin_AndNeverShowsIt(store, in_session):
    secret = new_secret()

    result = runner.invoke(cli.app, ["add", "mailbox", "--username", "jake@example.com", "--domains",
                                     "https://mail.example.com", "--agents", "--owner-approved", "yes, add the mailbox"],
                           input=secret + "\n")

    assert result.exit_code == 0, _text(result)
    assert store.get("mailbox").secret.reveal() == secret
    assert secret not in _text(result)
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"], line["detail"]) == ("mailbox", "add", "ok", "added")
    assert line["ownerApproved"] == "yes, add the mailbox"
    assert secret not in paths.audit_path().read_text(encoding="utf-8")


def test_Add_InsideASession_WithoutApproval_IsRefused_BeforeReadingTheValue(store, in_session):
    result = runner.invoke(cli.app, ["add", "mailbox", "--username", "u", "--domains", "example.com", "--agents"],
                           input=new_secret() + "\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert store.get("mailbox") is None


def test_Add_InsideASession_FromATerminal_IsRefused_NoPromptInTheSession(store, in_session, monkeypatch):
    monkeypatch.setattr(cli, "_stdin_is_tty", lambda: True)

    result = runner.invoke(cli.app, ["add", "mailbox", "--username", "u", "--domains", "example.com", "--agents",
                                     "--owner-approved", "yes"], input="typed\ntyped\n")

    assert result.exit_code == cli.EXIT_REFUSED
    assert "piped on standard input" in " ".join(_text(result).split())
    assert store.get("mailbox") is None


def test_Add_AnApprovalThatQuotesTheSecret_NeverWritesItToTheLog(store, in_session):
    secret = new_secret()

    result = runner.invoke(cli.app, ["add", "mailbox", "--username", "u", "--domains", "example.com", "--agents",
                                     "--owner-approved", f"set it to {secret}"], input=secret + "\n")

    assert secret not in _text(result)
    assert secret not in paths.audit_path().read_text(encoding="utf-8")


def test_Remove_InsideASession_WithApproval_RemovesIt_AndIsAudited(store, in_session):
    add_entry(store, name="web")

    result = runner.invoke(cli.app, ["remove", "web", "--yes", "--owner-approved", "remove web"])

    assert result.exit_code == 0, _text(result)
    assert store.get("web") is None
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["ownerApproved"]) == ("web", "remove", "remove web")


def test_Import_InsideASession_WithApproval_ImportsEachEntry_AuditedWithTheWords(store, in_session, tmp_path):
    secret = new_secret()
    path = tmp_path / "credentials.env"
    path.write_text(f"API_KEY={secret}\n", encoding="utf-8")

    result = runner.invoke(cli.app, ["import", str(path), "--agents", "--owner-approved", "import that file"])

    assert result.exit_code == 0, _text(result)
    assert store.get("api-key").secret.reveal() == secret
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["ownerApproved"]) == ("api-key", "import", "import that file")
    assert secret not in _text(result)


def test_Log_ShowsTheApprovalAndTheSessionName(store, in_session):
    add_entry(store, name="web")
    runner.invoke(cli.app, ["edit", "web", "--notes", "n", "--owner-approved", APPROVAL])

    result = runner.invoke(cli.app, ["log", "--json"])

    line = json.loads(result.stdout)["lines"][-1]
    assert (line["ownerApproved"], line["sessionName"]) == (APPROVAL, SESSION_NAME)


def test_Edit_OutsideASession_NeedsNoApproval_AndWritesNoApprovalField(store):
    add_entry(store, name="web", username="me")

    assert runner.invoke(cli.app, ["edit", "web", "--username", "you"]).exit_code == 0

    line = _audit_lines()[-1]
    assert "ownerApproved" not in line and "sessionName" not in line


def test_SessionName_WhenTheGatewayCannotBeAsked_SaysSoInTheRecord(monkeypatch):
    from cc_shared import gateway

    def fail():
        raise gateway.GatewayError("down")

    monkeypatch.setattr(gateway, "get_fleet", fail)

    assert cli._session_name(SESSION) == "(name not known: GatewayError)"

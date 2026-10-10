"""`cc-secrets ask`: an agent opens a window, the owner types the secret.

The window itself needs a screen, so these tests replace `entry_window.show` with a stand-in that plays the
owner: it reads the request the agent pre-filled and presses Save with what the owner "typed". Everything
around the window - argument handling, the refusal of every other way in for the secret, the store write and
the audit line - runs for real.
"""

import dataclasses
import inspect
import json

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, entry_window, paths
from src.audit import AuditLog
from src.store import KIND_SETTING

runner = CliRunner()

SESSION = "ask-test-session"
SESSION_NAME = "Fleet - Developer - machine setup"


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _audit_lines():
    return AuditLog(paths.audit_path()).read(100)


class Owner:
    """Stands in for the window: records the request, then presses Save once per typed form (or Cancel)."""

    def __init__(self, *forms, cancel=False):
        self.forms = list(forms)
        self.cancel = cancel
        self.request = None
        self.answers = []

    def __call__(self, request, on_save):
        self.request = request
        if self.cancel:
            return False
        for form in self.forms:
            typed = entry_window.WindowInput(
                name=form.get("name", request.name), username=form.get("username", request.username),
                notes=form.get("notes", request.notes), secret=form["secret"],
                agents_may_use=form.get("agents", request.agents_may_use))
            answer = on_save(typed)
            self.answers.append(answer)
            if answer is None:
                return True
        return False


@pytest.fixture
def owner(monkeypatch):
    def install(*forms, cancel=False):
        stand_in = Owner(*forms, cancel=cancel)
        monkeypatch.setattr(entry_window, "show", stand_in)
        return stand_in
    return install


def test_Ask_OwnerTypesTheSecret_SavesItThroughTheAddPath_PrintsOnlySaved(store, owner):
    secret = new_secret()
    stand_in = owner({"secret": secret})

    result = runner.invoke(cli.app, ["ask", "devlinux", "--username", "soren", "--notes", "desktop login",
                                     "--reason", "sudo over SSH", "--uses", "run"])

    assert result.exit_code == 0, _text(result)
    assert result.output.strip() == "saved devlinux"
    entry = store.get("devlinux")
    assert (entry.username, entry.notes, entry.uses, entry.agents_may_use) == ("soren", "desktop login", ["run"], True)
    assert entry.secret.reveal() == secret
    assert stand_in.request.reason == "sudo over SSH"
    assert stand_in.request.exists is False


def test_Ask_AuditLine_SaysTheOwnerTypedIt_WithTheReason_AndNeverTheSecret(store, owner):
    secret = new_secret()
    owner({"secret": secret})

    runner.invoke(cli.app, ["ask", "devlinux", "--reason", "sudo over SSH"])

    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"]) == ("devlinux", "ask", "ok")
    assert line["detail"].startswith("added; " + cli.ASK_RECORD)
    assert "reason: sudo over SSH" in line["detail"]
    assert line["ownerApproved"] == cli.ASK_RECORD
    assert secret not in json.dumps(line)


def test_Ask_InsideASession_NeedsNoOwnerApproved_AndRecordsTheSession(store, owner, monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SESSION)
    monkeypatch.setattr(cli, "_session_name", lambda session_id: SESSION_NAME)
    stand_in = owner({"secret": new_secret()})

    result = runner.invoke(cli.app, ["ask", "devlinux", "--reason", "sudo over SSH"])

    assert result.exit_code == 0, _text(result)
    assert SESSION_NAME in stand_in.request.asked_by
    line = _audit_lines()[-1]
    assert (line["session"], line["sessionName"], line["command"]) == (SESSION, SESSION_NAME, "ask")


def test_Ask_Cancelled_ChangesNothing_ExitsCancelled_AndIsAudited(store, owner):
    owner(cancel=True)

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == cli.EXIT_CANCELLED
    assert result.output.strip() == "cancelled"
    assert store.get("devlinux") is None
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"]) == ("devlinux", "ask", "cancelled")


def test_Ask_ExistingEntry_WindowSaysSaveReplaces_AndSaveReplacesIt(store, owner):
    add_entry(store, name="devlinux", username="old")
    secret = new_secret()
    stand_in = owner({"secret": secret, "username": "soren"})

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == 0, _text(result)
    assert stand_in.request.exists is True
    assert store.get("devlinux").secret.reveal() == secret
    assert _audit_lines()[-1]["detail"].startswith("replaced; ")


def test_Ask_OwnerRenamesToAnExistingEntry_IsToldOnce_ThenSaveReplaces(store, owner):
    old_secret = add_entry(store, name="other")
    secret = new_secret()
    stand_in = owner({"name": "other", "secret": secret}, {"name": "other", "secret": secret})

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == 0, _text(result)
    assert "already exists" in stand_in.answers[0] and stand_in.answers[1] is None
    assert store.get("other").secret.reveal() == secret != old_secret
    assert store.get("devlinux") is None


def test_Ask_SecretTooShort_StaysOpenWithTheReason_ThenSavesTheCorrection(store, owner):
    secret = new_secret()
    stand_in = owner({"secret": "abc"}, {"secret": secret})

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == 0, _text(result)
    assert stand_in.answers[0].startswith("Not saved:") and "at least" in stand_in.answers[0]
    assert store.get("devlinux").secret.reveal() == secret


def test_Ask_WithNoName_OpensAnEmptyForm_AndSavesUnderTheTypedName(store, owner):
    stand_in = owner({"name": "github-work", "username": "me", "secret": new_secret()})

    result = runner.invoke(cli.app, ["ask"])

    assert result.exit_code == 0, _text(result)
    assert stand_in.request.name == ""
    assert result.output.strip() == "saved github-work"
    assert store.get("github-work").username == "me"


def test_Ask_Setting_IsStoredAsASetting(store, owner):
    owner({"secret": "devlinux.local"})

    result = runner.invoke(cli.app, ["ask", "devlinux-host", "--setting"])

    assert result.exit_code == 0, _text(result)
    assert store.get("devlinux-host").kind == KIND_SETTING


def test_Ask_OwnerUntoggledSessionsMayUse_IsRespected(store, owner):
    owner({"secret": new_secret(), "agents": False})

    runner.invoke(cli.app, ["ask", "devlinux"])

    assert store.get("devlinux").agents_may_use is False


# --- No way in for the secret except the window ---------------------------------------------------------------

def test_Ask_HasNoParameterThatCouldCarryASecret():
    parameters = set(inspect.signature(cli.ask).parameters)
    assert parameters == {"name", "username", "notes", "reason", "domains", "uses", "agents", "setting", "env_name"}


def test_WindowRequest_HasNoSecretField():
    fields = {f.name for f in dataclasses.fields(entry_window.WindowRequest)}
    assert not any(word in field for field in fields for word in ("secret", "password", "value"))


@pytest.mark.parametrize("option", ["--secret", "--password", "--value"])
def test_Ask_RefusesASecretOption(store, owner, option):
    stand_in = owner({"secret": new_secret()})

    result = runner.invoke(cli.app, ["ask", "devlinux", option, new_secret()])

    assert result.exit_code != 0
    assert stand_in.request is None
    assert store.get("devlinux") is None


def test_Ask_IgnoresStandardInput_TheSecretIsWhatTheOwnerTyped(store, owner, monkeypatch):
    piped = new_secret()
    typed = new_secret()
    monkeypatch.setenv("CC_SECRET", piped)
    stand_in = owner({"secret": typed})

    result = runner.invoke(cli.app, ["ask", "devlinux"], input=piped + "\n")

    assert result.exit_code == 0, _text(result)
    assert store.get("devlinux").secret.reveal() == typed
    assert piped not in json.dumps(dataclasses.asdict(stand_in.request))


# --- What the agent pre-filled is checked before the window opens ---------------------------------------------

@pytest.mark.parametrize("args", [
    ["ask", "Not A Name"],
    ["ask", "devlinux", "--domains", "ftp://example.com"],
    ["ask", "devlinux", "--uses", "everything"],
    ["ask", "devlinux", "--env-name", "1BAD"],
    ["ask", "devlinux", "--username", "--setting"],
])
def test_Ask_BadPreFill_FailsBeforeTheWindowOpens(store, owner, args):
    stand_in = owner({"secret": new_secret()})

    result = runner.invoke(cli.app, args)

    assert result.exit_code == cli.EXIT_FAILED
    assert "ask failed" in _text(result)
    assert stand_in.request is None


# --- No display ------------------------------------------------------------------------------------------------

def test_Ask_NoDisplay_FailsWithWhatToDo(store, monkeypatch):
    monkeypatch.setattr(entry_window, "display_problem", lambda: "neither DISPLAY nor WAYLAND_DISPLAY is set")

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == cli.EXIT_FAILED
    assert "needs a screen" in _text(result) and "cc-secrets add" in _text(result)
    assert store.get("devlinux") is None


def test_DisplayProblem_OnLinuxWithoutDisplay_SaysSo(monkeypatch):
    monkeypatch.setattr(entry_window.sys, "platform", "linux")
    monkeypatch.delenv("DISPLAY", raising=False)
    monkeypatch.delenv("WAYLAND_DISPLAY", raising=False)
    assert "DISPLAY" in entry_window.display_problem()
    monkeypatch.setenv("DISPLAY", ":0")
    assert entry_window.display_problem() is None


def test_DisplayProblem_OnWindowsAndMac_IsNone(monkeypatch):
    for platform in ("win32", "darwin"):
        monkeypatch.setattr(entry_window.sys, "platform", platform)
        assert entry_window.display_problem() is None


# --- Leak search: the typed secret appears in no output, audit line or tool log --------------------------------

def test_Ask_LeaksTheTypedSecretNowhere(store, owner, monkeypatch, tmp_path):
    from leak_search import SecretSearch

    marker = "asktest-" + new_secret()
    monkeypatch.setenv("CC_SESSION_ID", marker)
    monkeypatch.setattr(cli, "_session_name", lambda session_id: SESSION_NAME)
    secret = new_secret()
    transcript = []
    for forms, args in (
        ([{"secret": "abc"}, {"secret": secret}], ["ask", "devlinux", "--username", "leak-user"]),  # short, then saved
        ([{"name": "devlinux", "secret": secret}], ["ask"]),          # typed over an existing name: told, not saved
        ([{"secret": secret}], ["ask", "devlinux", "--reason", "replace it"]),                       # replaced
    ):
        owner(*forms)
        result = runner.invoke(cli.app, args)
        transcript.append(f"$ cc-secrets {' '.join(args)}\n{_text(result)}\nexit {result.exit_code}\n")

    output_file = tmp_path / "command-output.txt"
    output_file.write_text("\n".join(transcript), encoding="utf-8")
    log_files = sorted((paths.secrets_home() / "logs").glob("cc-secrets-*.log"))
    assert log_files, "the tool log was never written"
    search = SecretSearch([(secret, "leak-user")])

    searched, hits = search.search_files(
        [(output_file, "saved devlinux"), (paths.audit_path(), marker)] +
        [(f, "[SecretStore] put: name=devlinux") for f in log_files],
        tmp_path / "work")

    assert searched == 2 + len(log_files)
    assert hits == []
    assert store.get("devlinux").secret.reveal() == secret

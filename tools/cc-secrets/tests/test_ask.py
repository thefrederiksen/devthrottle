"""`cc-secrets ask`: an agent opens a window, the owner types the secret.

The window itself needs a screen, so these tests replace `entry_window.show_ask` with a stand-in that plays the
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
from src.store import KIND_SETTING, make_entry
from src.window_actions import ASK_RECORD, MODE_ASK, FormInput, FormRequest

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

    def __call__(self, request, actions):
        self.request = request
        if self.cancel:
            return False
        for form in self.forms:
            typed = FormInput(
                name=form.get("name", request.name), kind_setting=form.get("setting", request.kind_setting),
                username=form.get("username", request.username), secret=form["secret"],
                notes=form.get("notes", request.notes),
                uses=form.get("uses", request.uses), domains=form.get("domains", request.domains))
            answer = actions.submit(typed, MODE_ASK)
            self.answers.append(answer)
            if answer is None:
                return True
        return False


@pytest.fixture
def owner(monkeypatch):
    def install(*forms, cancel=False):
        stand_in = Owner(*forms, cancel=cancel)
        monkeypatch.setattr(entry_window, "show_ask", stand_in)
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
    assert (entry.username, entry.notes, entry.uses) == ("soren", "desktop login", ["run"])
    assert entry.secret.reveal() == secret
    assert stand_in.request.reason == "sudo over SSH"
    assert stand_in.request.exists is False


def test_Ask_AuditLine_SaysTheOwnerTypedIt_WithTheReason_AndNeverTheSecret(store, owner):
    secret = new_secret()
    owner({"secret": secret})

    runner.invoke(cli.app, ["ask", "devlinux", "--reason", "sudo over SSH"])

    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"]) == ("devlinux", "ask", "ok")
    assert line["detail"].startswith("added; " + ASK_RECORD)
    assert "reason: sudo over SSH" in line["detail"]
    assert line["ownerApproved"] == ASK_RECORD
    assert secret not in json.dumps(line)


def test_Ask_InsideASession_NeedsNoOwnerApproved_AndRecordsTheSession(store, owner, monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", SESSION)
    monkeypatch.setattr(cli, "_session_name", lambda session_id: SESSION_NAME)
    monkeypatch.setattr(cli, "_session_number", lambda session_id: "104")
    stand_in = owner({"secret": new_secret()})

    result = runner.invoke(cli.app, ["ask", "devlinux", "--reason", "sudo over SSH"])

    assert result.exit_code == 0, _text(result)
    assert stand_in.request.asked_by == f'Session 104 "{SESSION_NAME}"'
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


def test_Ask_ARefusedSecret_DoesNotBlockTheNextSave_EvenWhenTheNameContainsIt(store, owner):
    # Review of pull request 3732: a refused three-letter typo stayed in the scrubber, and every later Save of an
    # entry whose name contained it was refused as "an audit line that contained a secret".
    secret = new_secret()
    stand_in = owner({"name": "soren-laptop", "secret": "sor"}, {"name": "soren-laptop", "secret": secret})

    result = runner.invoke(cli.app, ["ask", "soren-laptop"])

    assert result.exit_code == 0, _text(result)
    assert stand_in.answers[1] is None
    assert store.get("soren-laptop").secret.reveal() == secret


def test_Ask_EmptySecret_IsReportedAsTooShort_NotAsAnUnexpectedError(store, owner):
    stand_in = owner({"secret": ""}, {"secret": new_secret()})

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == 0, _text(result)
    assert "at least" in stand_in.answers[0] and "unexpected" not in stand_in.answers[0]


def test_Ask_CancelWhoseAuditLineFails_StillExitsCancelled_WithoutATraceback(store, owner, monkeypatch):
    owner(cancel=True)

    def broken_record(self, *args, **kwargs):
        raise OSError("disk full")
    monkeypatch.setattr(AuditLog, "record", broken_record)

    result = runner.invoke(cli.app, ["ask", "devlinux"])

    assert result.exit_code == cli.EXIT_CANCELLED
    assert "cancelled" in result.output
    assert "Traceback" not in _text(result)
    assert result.exception is None or isinstance(result.exception, SystemExit)


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


# --- No way in for the secret except the window ---------------------------------------------------------------

def test_Ask_HasNoParameterThatCouldCarryASecret():
    parameters = set(inspect.signature(cli.ask).parameters)
    assert parameters == {"name", "username", "notes", "reason", "domains", "uses", "agents", "setting", "env_name"}


def test_Ask_ForASettingThatExists_DoesNotPreFillItsValue(store, owner):
    """The owner types the value; ask never shows what is stored, even for a setting, which the window's Edit
    does show."""
    store.put(make_entry("posthog-host", "", "https://us.i.posthog.com", [], "", ["run"], kind=KIND_SETTING))
    stand_in = owner(cancel=True)

    runner.invoke(cli.app, ["ask", "posthog-host", "--setting"])

    assert stand_in.request.kind_setting and stand_in.request.exists
    assert stand_in.request.setting_value == ""


def test_FormRequest_HasNoSecretField():
    """The one value it carries is a setting's (an email address shown in the edit form), and it refuses one for
    a password. Ask never fills it: the agent pre-fills everything but the value."""
    fields = {f.name for f in dataclasses.fields(FormRequest)}
    assert not any(word in field for field in fields for word in ("secret", "password"))
    assert [f for f in fields if "value" in f] == ["setting_value"]
    with pytest.raises(ValueError):
        FormRequest(mode=MODE_ASK, kind_setting=False, setting_value="hunter2-hunter2")
    assert "setting_value" not in inspect.getsource(cli.ask)


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


def test_DisplayProblem_OnMac_IsLeftToTk(monkeypatch):
    monkeypatch.setattr(entry_window.sys, "platform", "darwin")
    assert entry_window.display_problem() is None


def test_DisplayProblem_OnWindows_FollowsTheVisibleDesktop(monkeypatch):
    # Review of pull request 3732: an SSH shell into Windows gets an invisible desktop, where Tk opens a window
    # nobody can see and the command would wait forever.
    monkeypatch.setattr(entry_window.sys, "platform", "win32")
    monkeypatch.setattr(entry_window, "_windows_desktop_visible", lambda: False)
    assert "desktop" in entry_window.display_problem()
    monkeypatch.setattr(entry_window, "_windows_desktop_visible", lambda: True)
    assert entry_window.display_problem() is None


@pytest.mark.skipif(entry_window.sys.platform != "win32", reason="asks Windows itself")
def test_WindowsDesktopVisible_AnswersOnThisMachine():
    assert isinstance(entry_window._windows_desktop_visible(), bool)


# --- Leak search: the typed secret appears in no output, audit line or tool log --------------------------------

def test_Ask_LeaksTheTypedSecretNowhere(store, owner, monkeypatch, tmp_path):
    from leak_search import SecretSearch

    marker = "asktest-" + new_secret()
    monkeypatch.setenv("CC_SESSION_ID", marker)
    monkeypatch.setattr(cli, "_session_name", lambda session_id: SESSION_NAME)
    monkeypatch.setattr(cli, "_session_number", lambda session_id: "104")
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

"""The cc-secrets window (`cc-secrets ui`): the list, the add / edit form and the eye.

Everything a button does lives in window_actions and runs here for real against a test store - the rows, search,
reveal, save and delete, and their audit lines. Only the drawing needs a screen, and the `ui` command is run with
the drawing replaced by a stand-in.

The owner's rulings (2026-10-09) each have a test: agents may open the list; nothing but the eye reveals a
secret; a reveal stays until clicked again (the window holds it - nothing here hides it on a timer); no Copy.
"""

import dataclasses
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import cli, entry_window, paths, redact
from src.audit import AuditLog, OwnerApproval
from src.errors import CcSecretsError
from src.store import KIND_SETTING, make_entry
from src.window_actions import (MODE_ADD, MODE_EDIT, SHOW_ALL, SHOW_PASSWORDS, SHOW_SETTINGS, WINDOW_RECORD,
                                FormInput, FormRequest, WindowActions, build_rows, filter_rows, last_used,
                                relative_time, sort_rows)

runner = CliRunner()
APPROVAL = OwnerApproval(text=WINDOW_RECORD, session_name="")


def _audit_lines():
    return AuditLog(paths.audit_path()).read(1000)


@pytest.fixture
def actions(store):
    return WindowActions(store, AuditLog(paths.audit_path()), APPROVAL, detail=WINDOW_RECORD)


def _form(name="devlinux", secret="", username="soren", notes="", setting=False, agents=True, uses=("run", "login"),
          domains=""):
    return FormInput(name=name, kind_setting=setting, username=username, secret=secret, notes=notes,
                     agents_may_use=agents, uses=list(uses), domains=domains)


# --- The list -------------------------------------------------------------------------------------------------

def test_Rows_ShowASettingInFull_AndNeverASecretsValue(store, actions):
    secret = add_entry(store, name="desktop-login", username="soren")
    store.put(make_entry("posthog-host", "", "https://us.i.posthog.com", [], "", True, ["run"], kind=KIND_SETTING))

    rows = {r.name: r for r in actions.rows()}

    assert rows["posthog-host"].setting_value == "https://us.i.posthog.com"
    assert rows["desktop-login"].setting_value == ""
    assert secret not in json.dumps([dataclasses.asdict(r) for r in rows.values()])


def test_Rows_LastUsed_ComesFromTheAuditLog(store):
    add_entry(store, name="a")
    add_entry(store, name="b")
    now = datetime(2026, 10, 9, 20, 0, tzinfo=timezone.utc)
    lines = [{"entry": "a", "command": "run ssh x", "outcome": "ok", "time": (now - timedelta(minutes=2)).isoformat()},
             {"entry": "a", "command": "run ssh y", "outcome": "failed", "time": now.isoformat()},
             {"entry": "b", "command": "ui reveal", "outcome": "ok", "time": now.isoformat()}]

    rows = {r.name: r for r in build_rows(store.entries(), last_used(lines), now)}

    assert rows["a"].last_used == "2 min ago"
    assert rows["b"].last_used == "never"  # looking at it is not using it


@pytest.mark.parametrize("ago, text", [(timedelta(seconds=5), "just now"), (timedelta(minutes=7), "7 min ago"),
                                       (timedelta(hours=3), "3 h ago"), (timedelta(days=1), "yesterday"),
                                       (timedelta(days=23), "23 days ago")])
def test_RelativeTime(ago, text):
    now = datetime(2026, 10, 9, 20, 0, tzinfo=timezone.utc)
    assert relative_time((now - ago).isoformat(), now) == text


def test_Search_MatchesNameUserNotesAndSettingValue_NeverASecret(store, actions):
    secret = add_entry(store, name="desktop-login", username="soren", notes="Mac Mini")
    store.put(make_entry("posthog-host", "", "https://us.i.posthog.com", [], "", True, ["run"], kind=KIND_SETTING))
    rows = actions.rows()

    assert [r.name for r in filter_rows(rows, "MAC mini")] == ["desktop-login"]
    assert [r.name for r in filter_rows(rows, "posthog.com")] == ["posthog-host"]
    assert [r.name for r in filter_rows(rows, "SOREN")] == ["desktop-login"]
    assert filter_rows(rows, secret[3:12]) == []
    assert len(filter_rows(rows, "  ")) == 2


# --- The eye --------------------------------------------------------------------------------------------------

def test_Reveal_ReturnsTheSecret_AndAuditsIt_WithoutTheSecret(store, actions, monkeypatch):
    monkeypatch.setenv("CC_SESSION_ID", "window-session")
    secret = add_entry(store, name="desktop-login")

    assert actions.reveal("desktop-login") == secret

    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"], line["session"]) == \
        ("desktop-login", "ui reveal", "ok", "window-session")
    assert line["ownerApproved"] == WINDOW_RECORD
    assert secret not in json.dumps(line)


def test_Reveal_UnknownEntry_Fails_AndWritesNothing(store, actions):
    with pytest.raises(CcSecretsError):
        actions.reveal("nothing-here")
    assert _audit_lines() == []


def test_NoCommandRevealsASecret_OnlyTheWindowsEye():
    commands = {c.name or c.callback.__name__ for c in cli.app.registered_commands}
    assert not commands & {"reveal", "show", "copy", "get-secret"}
    ui_options = [p for p in __import__("inspect").signature(cli.ui).parameters]
    assert ui_options == []


def test_TheListHasNoCopyButton_AndNoRevealTimer():
    source = Path(entry_window.__file__).read_text(encoding="utf-8")
    assert "clipboard" not in source.lower()
    assert '"Copy"' not in source
    assert ".after(" not in source.replace("win.after(500, lambda: win.attributes(\"-topmost\", False))", "")


# --- Add -----------------------------------------------------------------------------------------------------

def test_Add_SavesThroughTheAddPath_AndAudits(store, actions):
    secret = new_secret()

    assert actions.submit(_form(secret=secret, notes="desktop", domains="example.com"), MODE_ADD) is None

    entry = store.get("devlinux")
    assert (entry.secret.reveal(), entry.username, entry.notes, entry.allowed_domains) == \
        (secret, "soren", "desktop", ["https://example.com"])
    line = _audit_lines()[-1]
    assert (line["command"], line["detail"]) == ("ui add", "added; " + WINDOW_RECORD)
    assert secret not in json.dumps(line)


def test_Add_OverAnExistingName_WarnsOnce_ThenReplaces(store, actions):
    add_entry(store, name="devlinux")
    secret = new_secret()

    first = actions.submit(_form(secret=secret), MODE_ADD)
    second = actions.submit(_form(secret=secret), MODE_ADD)

    assert "already exists" in first and "REPLACES" in first
    assert second is None
    assert store.get("devlinux").secret.reveal() == secret


@pytest.mark.parametrize("form, words", [
    (_form(secret="abc"), "at least"),
    (_form(secret=""), "at least"),
    (_form(name="Bad Name", secret="long-enough-1"), "not a valid entry name"),
    (_form(secret="long-enough-1", uses=()), "Uses must be"),
    (_form(secret="long-enough-1", domains="ftp://x.example.com"), "http or https"),
])
def test_Add_BadInput_StaysOpenWithTheReason_AndSavesNothing(store, actions, form, words):
    answer = actions.submit(form, MODE_ADD)

    assert answer.startswith("Not saved:") and words in answer
    assert store.entries() == []


def test_Add_Setting_IsStoredAsASetting(store, actions):
    assert actions.submit(_form(name="devlinux-host", secret="devlinux.local", setting=True), MODE_ADD) is None
    assert store.get("devlinux-host").kind == KIND_SETTING


# --- Edit ----------------------------------------------------------------------------------------------------

def test_EditRequest_CarriesEverythingButTheSecret(store, actions):
    secret = add_entry(store, name="devlinux", username="soren", notes="n", domains=("https://example.com",))

    request = actions.edit_request("devlinux")

    assert (request.mode, request.name, request.username, request.notes, request.domains) == \
        (MODE_EDIT, "devlinux", "soren", "n", "https://example.com")
    assert secret not in json.dumps(dataclasses.asdict(request))


def test_Edit_EmptySecret_KeepsTheCurrentOne(store, actions):
    secret = add_entry(store, name="devlinux", username="old")

    assert actions.submit(_form(secret="", username="new", notes="changed"), MODE_EDIT, "devlinux") is None

    entry = store.get("devlinux")
    assert (entry.secret.reveal(), entry.username, entry.notes) == (secret, "new", "changed")
    assert _audit_lines()[-1]["detail"] == f"replaced; {WINDOW_RECORD}; value kept"


def test_Edit_NewSecret_ReplacesIt_AndKeepsTheVariableName(store, actions):
    add_entry(store, name="devlinux")
    entry = store.get("devlinux")
    entry.env_name = "DEVLINUX_PASSWORD"
    store.put(entry)
    secret = new_secret()

    assert actions.submit(_form(secret=secret, agents=False, uses=("run",)), MODE_EDIT, "devlinux") is None

    entry = store.get("devlinux")
    assert (entry.secret.reveal(), entry.env_name, entry.agents_may_use, entry.uses) == \
        (secret, "DEVLINUX_PASSWORD", False, ["run"])
    assert _audit_lines()[-1]["detail"].endswith("value changed")


def test_Edit_AnEntryDeletedMeanwhile_SaysSo(store, actions):
    answer = actions.submit(_form(secret=new_secret()), MODE_EDIT, "devlinux")
    assert "no longer exists" in answer
    assert store.entries() == []


# --- Delete --------------------------------------------------------------------------------------------------

def test_Delete_RemovesIt_AndAudits(store, actions):
    add_entry(store, name="devlinux")

    actions.delete("devlinux")

    assert store.get("devlinux") is None
    assert (_audit_lines()[-1]["command"], _audit_lines()[-1]["entry"]) == ("ui delete", "devlinux")


def test_Delete_Unknown_Fails_AndWritesNoLine(store, actions):
    with pytest.raises(CcSecretsError):
        actions.delete("nothing-here")
    assert _audit_lines() == []


# --- The ui command ------------------------------------------------------------------------------------------

def test_Ui_InsideASession_IsNotRefused_AndRecordsWhoOpenedIt(store, monkeypatch):
    # Owner ruling 2026-10-09: agents may open the list so they can put it on screen for the owner.
    monkeypatch.setenv("CC_SESSION_ID", "window-session")
    monkeypatch.setattr(cli, "_session_name", lambda session_id: "Fleet - Developer - x")
    monkeypatch.setattr(cli, "_session_number", lambda session_id: "104")
    opened = []
    monkeypatch.setattr(entry_window, "show_list", lambda actions: opened.append(actions))
    secret = add_entry(store, name="devlinux")

    result = runner.invoke(cli.app, ["ui"])

    assert result.exit_code == 0, result.output
    assert len(opened) == 1
    assert secret not in result.output
    opened[0].reveal("devlinux")
    assert _audit_lines()[-1]["sessionName"] == "Fleet - Developer - x"


def test_Ui_NoDisplay_FailsWithWhatToDo(store, monkeypatch):
    monkeypatch.setattr(entry_window, "display_problem", lambda: "neither DISPLAY nor WAYLAND_DISPLAY is set")

    result = runner.invoke(cli.app, ["ui"])

    assert result.exit_code == cli.EXIT_FAILED
    assert "needs a screen" in result.output + (result.stderr if result.stderr_bytes is not None else "")


def test_Shortcut_StartsTheWindowlessPython_RunningThisPackagesUi(tmp_path):
    package = tmp_path / "site-packages" / "cc_secrets"

    target, arguments, folder = cli.start_menu_shortcut_command(str(tmp_path / "Scripts" / "python.exe"), package)

    assert Path(target) == tmp_path / "Scripts" / "pythonw.exe"
    assert arguments == "-m cc_secrets.cli ui"
    assert Path(folder) == tmp_path / "site-packages"


def test_Shortcut_OffWindows_SaysToRunUi(home, monkeypatch):
    monkeypatch.setattr(cli, "_on_windows", lambda: False)

    result = runner.invoke(cli.app, ["shortcut"])

    assert result.exit_code == cli.EXIT_FAILED
    assert "cc-secrets ui" in result.output + (result.stderr if result.stderr_bytes is not None else "")


# --- Second review round ---------------------------------------------------------------------------------------

@pytest.mark.parametrize("typed_is_current", [False, True])
def test_Edit_SecretToSetting_WithItsCurrentValue_IsRefused(store, actions, typed_is_current):
    # Review of pull request 3732: switching Kind to Setting with the box empty (or filled by the eye) turned the
    # stored password into a setting, which get prints and the scrubber never hides.
    secret = add_entry(store, name="devlinux")

    answer = actions.submit(_form(secret=secret if typed_is_current else "", setting=True), MODE_EDIT, "devlinux")

    assert "cannot become a setting" in answer
    assert store.get("devlinux").kind != KIND_SETTING


def test_Edit_SecretToSetting_WithANewValue_IsSaved_AndAuditedAsAKindChange(store, actions):
    add_entry(store, name="devlinux-host")

    assert actions.submit(_form(name="devlinux-host", secret="devlinux.local", setting=True), MODE_EDIT,
                          "devlinux-host") is None

    assert store.get("devlinux-host").kind == KIND_SETTING
    assert _audit_lines()[-1]["detail"].endswith("value changed; kind changed to setting")


def test_Submit_InputErrors_AreNotWrittenToTheToolLog(store, actions):
    # Review of pull request 3732: a password typed into the Name box by reflex was echoed by validate_name into
    # the tool log.
    typed = "Typed Into Name " + new_secret()

    answer = actions.submit(_form(name=typed, secret=new_secret()), MODE_ADD)
    actions.submit(_form(secret=new_secret(), domains="ftp://" + typed.split()[-1]), MODE_ADD)

    assert typed in answer
    assert actions.submit(_form(name="proof-the-log-is-read", secret=new_secret()), MODE_ADD) is None
    logs = "".join(f.read_text(encoding="utf-8") for f in (paths.secrets_home() / "logs").glob("*.log"))
    assert "put: name=proof-the-log-is-read" in logs  # the log was written, so its silence below means something
    assert typed.split()[-1] not in logs


def test_AddOverAnExistingEntry_KeepsItsVariableName(store, actions):
    add_entry(store, name="devlinux")
    entry = store.get("devlinux")
    entry.env_name = "DEVLINUX_PASSWORD"
    store.put(entry)

    actions.submit(_form(secret=new_secret()), MODE_ADD)
    assert actions.submit(_form(secret=new_secret()), MODE_ADD) is None

    assert store.get("devlinux").env_name == "DEVLINUX_PASSWORD"


# --- The second round (owner's report 2026-10-10: slow, the QA email empty, forms in the wrong place) -----------

def test_EditRequest_ForASetting_CarriesItsValue(store, actions):
    store.put(make_entry("mindzie-qa-email", "", "qa@mindzie.com", [], "", True, ["run"], kind=KIND_SETTING))

    assert actions.edit_request("mindzie-qa-email").setting_value == "qa@mindzie.com"


def test_EditRequest_ForAPassword_CarriesNoValue(store, actions):
    add_entry(store, name="devlinux")

    assert actions.edit_request("devlinux").setting_value == ""


def test_Edit_ASettingSavedUnchanged_KeepsItsValue(store, actions):
    store.put(make_entry("mindzie-qa-email", "", "qa@mindzie.com", [], "n", True, ["run"], kind=KIND_SETTING))
    request = actions.edit_request("mindzie-qa-email")

    assert actions.submit(_form(name="mindzie-qa-email", secret=request.setting_value, setting=True, uses=("run",)),
                          MODE_EDIT, "mindzie-qa-email") is None
    assert store.get("mindzie-qa-email").secret.reveal() == "qa@mindzie.com"


def _three(store):
    add_entry(store, name="b-password", username="zed")
    store.put(make_entry("a-setting", "amy", "host.example", [], "", True, ["run"], kind=KIND_SETTING))
    add_entry(store, name="c-password", username="", agents=False)


@pytest.mark.parametrize("show, names", [
    (SHOW_ALL, ["a-setting", "b-password", "c-password"]),
    (SHOW_PASSWORDS, ["b-password", "c-password"]),
    (SHOW_SETTINGS, ["a-setting"]),
])
def test_Filter_ByKind(store, actions, show, names):
    _three(store)

    assert [r.name for r in filter_rows(actions.rows(), "", show)] == names


def test_Filter_ByKindAndText_Together(store, actions):
    _three(store)

    assert [r.name for r in filter_rows(actions.rows(), "zed", SHOW_PASSWORDS)] == ["b-password"]
    assert filter_rows(actions.rows(), "zed", SHOW_SETTINGS) == []


def test_Filter_UnknownKind_Fails(store, actions):
    with pytest.raises(ValueError):
        filter_rows([], "", "secrets")


@pytest.mark.parametrize("column, descending, names", [
    ("name", False, ["a-setting", "b-password", "c-password"]),
    ("name", True, ["c-password", "b-password", "a-setting"]),
    ("kind", False, ["b-password", "c-password", "a-setting"]),
    ("username", False, ["c-password", "a-setting", "b-password"]),
    ("access", False, ["b-password", "c-password", "a-setting"]),  # "login, run" < "not allowed" < "run"
])
def test_Sort_ByEachColumn_TiesByName(store, actions, column, descending, names):
    _three(store)

    assert [r.name for r in sort_rows(actions.rows(), column, descending)] == names


def test_Sort_ByLastUsed_NewestFirst_NeverUsedLast(store, actions):
    _three(store)
    audit = AuditLog(paths.audit_path())
    audit.record("c-password", "run", "ok")
    audit.record("b-password", "run", "ok")

    rows = sort_rows(actions.rows(), "used", True)

    assert [r.name for r in rows] == ["b-password", "c-password", "a-setting"]


def test_Sort_UnknownColumn_Fails():
    with pytest.raises(ValueError):
        sort_rows([], "secret", False)


def test_Row_SaysWhatAgentsMayDo_InWords(store, actions):
    _three(store)
    rows = {r.name: r for r in actions.rows()}

    assert (rows["b-password"].access, rows["a-setting"].access, rows["c-password"].access) == \
        ("login, run", "run", "not allowed")
    assert (rows["b-password"].kind_label, rows["a-setting"].kind_label) == ("Password", "Setting")


def test_TheWindowsReadTheStore_WithoutRebuildingTheScrubberEachTime(store, actions, monkeypatch):
    """The cause of two seconds a click: each store read rebuilt the scrubber's needles for every secret held."""
    for index in range(30):
        add_entry(store, name=f"entry-{index}")
    store.entries()
    calls = []
    real = redact._needle_pairs
    monkeypatch.setattr(redact, "_needle_pairs", lambda *a: calls.append(1) or real(*a))

    actions.rows()
    actions.edit_request("entry-3")
    actions.reveal("entry-3")

    assert calls == []

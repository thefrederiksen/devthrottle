"""`cc-secrets ask` says why it is asking (owner rulings, 9 November 2026).

A request without the explanation is refused before the window opens, naming each missing part. The window shows
the explanation in a panel beside the form. "Not now" carries the owner's reply back to the asking session as its
command's answer - and a reply never carries a secret.

The window itself needs a screen, so `entry_window.show_ask` is replaced by a stand-in that plays the owner; the
panel's content and the reply guard are plain functions and run here for real.
"""

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from src import ask_explanation, cli, entry_window, paths
from src.ask_explanation import (ACCOUNT_EXISTING, ACCOUNT_NEW, NO_REPLY, WHY_ACCOUNT, WHY_IF_NOT_NOW, WHY_REASON,
                                 WHY_STEPS, WHY_WHERE, WHY_WILL_DO, AskExplanation, AskingSession, AskOutcome,
                                 missing_parts, panel_sections, reply_problem)
from src.audit import AuditLog

runner = CliRunner()

SESSION = "explain-test-session"
REASON = ["--reason", "send the approved replies from info@example.com"]
EXISTING = ["--account", "existing", "--where-to-find", "your password manager"]
NEW = ["--account", "new", "--create-step", "Sign in to the provider's console",
       "--create-step", "Create a key with send permission only"]
WILL_DO = ["--will-do", "sign in only to send an approved reply"]
IF_NOT_NOW = ["--if-not-now", "the replies stay queued"]
FULL = REASON + EXISTING + WILL_DO + IF_NOT_NOW


def _text(result):
    return result.output + (result.stderr if result.stderr_bytes is not None else "")


def _audit_lines():
    return AuditLog(paths.audit_path()).read(100)


class Window:
    """Stands in for the window: records the request, then ends the way the test says."""

    def __init__(self, outcome):
        self.outcome = outcome
        self.request = None

    def __call__(self, request, actions):
        self.request = request
        return self.outcome


@pytest.fixture
def window(monkeypatch):
    def install(outcome=AskOutcome(saved=False)):
        stand_in = Window(outcome)
        monkeypatch.setattr(entry_window, "show_ask", stand_in)
        return stand_in
    return install


# --- Refused without the explanation ----------------------------------------------------------------------------

@pytest.mark.parametrize("args, missing", [
    ([], [WHY_REASON, WHY_ACCOUNT, WHY_WILL_DO, WHY_IF_NOT_NOW]),
    (REASON, [WHY_ACCOUNT, WHY_WILL_DO, WHY_IF_NOT_NOW]),
    (REASON + ["--account", "existing"] + WILL_DO + IF_NOT_NOW, [WHY_WHERE]),
    (REASON + ["--account", "new"] + WILL_DO + IF_NOT_NOW, [WHY_STEPS]),
    (EXISTING + WILL_DO + IF_NOT_NOW, [WHY_REASON]),
    (REASON + EXISTING + IF_NOT_NOW, [WHY_WILL_DO]),
    (REASON + EXISTING + WILL_DO, [WHY_IF_NOT_NOW]),
    (["--reason", "   "] + EXISTING + WILL_DO + IF_NOT_NOW, [WHY_REASON]),
])
def test_Ask_PasswordWithoutTheExplanation_IsRefused_NamingEachMissingPart(store, window, args, missing):
    stand_in = window()

    result = runner.invoke(cli.app, ["ask", "info-smtp", "--username", "info@example.com", *args])

    assert result.exit_code == cli.EXIT_REFUSED, _text(result)
    assert stand_in.request is None, "the window opened for a request that did not say why"
    for line in missing:
        assert line in _text(result)
    for line in {WHY_REASON, WHY_ACCOUNT, WHY_WHERE, WHY_STEPS, WHY_WILL_DO, WHY_IF_NOT_NOW} - set(missing):
        assert line not in _text(result)
    assert "Nothing was opened" in _text(result) and "Example: cc-secrets ask" in _text(result)
    line = _audit_lines()[-1]
    assert (line["entry"], line["command"], line["outcome"]) == ("info-smtp", "ask", "refused")
    for part in missing:
        assert part.split()[0] in line["detail"]
    assert store.get("info-smtp") is None


def test_Ask_SettingWithoutAReason_IsRefused_AndNeedsNothingElse(store, window):
    stand_in = window()

    refused = runner.invoke(cli.app, ["ask", "tunnel-host", "--setting"])

    assert refused.exit_code == cli.EXIT_REFUSED
    assert WHY_REASON in _text(refused) and WHY_ACCOUNT not in _text(refused)
    assert stand_in.request is None

    opened = runner.invoke(cli.app, ["ask", "tunnel-host", "--setting", *REASON])

    assert opened.exit_code == cli.EXIT_CANCELLED, _text(opened)
    assert stand_in.request is not None


def test_Ask_AnAccountThatIsNeitherExistingNorNew_FailsBeforeTheWindowOpens(store, window):
    stand_in = window()

    result = runner.invoke(cli.app, ["ask", "info-smtp", *REASON, "--account", "maybe", *WILL_DO, *IF_NOT_NOW])

    assert result.exit_code == cli.EXIT_FAILED
    assert "--account must be 'existing'" in _text(result)
    assert stand_in.request is None


def test_Ask_ACreateStepThatIsAnotherOption_IsRefused(store, window):
    stand_in = window()

    result = runner.invoke(cli.app, ["ask", "info-smtp", *REASON, "--account", "new", "--create-step", "--will-do",
                                     "x", *IF_NOT_NOW])

    assert result.exit_code in (cli.EXIT_FAILED, cli.EXIT_REFUSED)
    assert stand_in.request is None


def test_MissingParts_CompleteRequests_MissNothing():
    assert missing_parts(False, "r", "existing", "w", [], "d", "n") == []
    assert missing_parts(False, "r", "new", "", ["step"], "d", "n") == []
    assert missing_parts(True, "r", None, None, [], None, None) == []


# --- The panel --------------------------------------------------------------------------------------------------

def test_Ask_Explained_HandsTheWindowEveryPart_AndWhoIsAsking(store, window, monkeypatch):
    from cc_shared import gateway

    monkeypatch.setenv("CC_SESSION_ID", SESSION)
    monkeypatch.setattr(gateway, "get_fleet", lambda: ([{
        "sessionId": SESSION, "name": "devthrottle_internal - email", "number": "129", "machineName": "SOREN_NORTH",
        "missionName": "Inbound email", "viewUrl": "https://gateway.example.com/sessions/" + SESSION}],
        True, None, None))
    stand_in = window()

    result = runner.invoke(cli.app, ["ask", "info-smtp", "--account-detail", "The mailbox info@example.com", *FULL])

    assert result.exit_code == cli.EXIT_CANCELLED, _text(result)
    why = stand_in.request.why
    assert why.session == AskingSession(title="129 - devthrottle_internal - email",
                                        where="SOREN_NORTH - Mission: Inbound email",
                                        view_url="https://gateway.example.com/sessions/" + SESSION)
    assert (why.reason, why.account, why.account_detail, why.where_to_find, why.will_do, why.if_not_now) == (
        "send the approved replies from info@example.com", ACCOUNT_EXISTING, "The mailbox info@example.com",
        "your password manager", "sign in only to send an approved reply", "the replies stay queued")
    assert stand_in.request.asked_by == 'Session 129 "devthrottle_internal - email"'
    assert SESSION not in why.session.title and SESSION not in why.session.where  # never an internal id


def test_Ask_NewAccount_CarriesTheStepsInOrder(store, window):
    stand_in = window()

    runner.invoke(cli.app, ["ask", "provider-key", *REASON, *NEW, *WILL_DO, *IF_NOT_NOW])

    assert stand_in.request.why.account == ACCOUNT_NEW
    assert stand_in.request.why.create_steps == ("Sign in to the provider's console",
                                                 "Create a key with send permission only")


def test_Ask_OutsideASession_SaysItIsATerminalCommand(store, window):
    stand_in = window()

    runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert stand_in.request.why.session.title == "A command in a terminal, not a session"
    assert stand_in.request.why.session.view_url == ""


def test_Ask_AViewUrlThatIsNotAWebAddress_IsNotOffered(store, window, monkeypatch):
    from cc_shared import gateway

    monkeypatch.setenv("CC_SESSION_ID", SESSION)
    monkeypatch.setattr(gateway, "get_fleet", lambda: ([{"sessionId": SESSION, "name": "n", "number": "1",
                                                          "viewUrl": "file:///C:/Windows/system32/calc.exe"}],
                                                        True, None, None))
    stand_in = window()

    runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert stand_in.request.why.session.view_url == ""


def test_PanelSections_ExistingAccount_InTheMockupsOrder():
    why = AskExplanation(reason="for", account=ACCOUNT_EXISTING, account_detail="the mailbox", where_to_find="a manager",
                         will_do="send", if_not_now="queued",
                         session=AskingSession(title="129 - email", where="SOREN_NORTH", view_url="https://g/s"))

    sections = panel_sections(why, setting=False)

    assert [s.heading for s in sections] == ["Who is asking", "The account", "What it is for",
                                             "What the session will do with it", "If you say not now"]
    assert (sections[0].text, sections[0].muted, sections[0].link) == ("129 - email", "SOREN_NORTH", "https://g/s")
    assert (sections[1].badge, sections[1].text, sections[1].muted) == (
        "YOU ALREADY HAVE THIS ACCOUNT", "the mailbox", "Where to find it: a manager")
    assert [s.text for s in sections[2:]] == ["for", "send", "queued"]


def test_PanelSections_NewAccount_ShowsTheBadgeAndNumberedSteps():
    why = AskExplanation(reason="for", account=ACCOUNT_NEW, create_steps=("one", "two"), will_do="d", if_not_now="n",
                         session=AskingSession(title="t"))

    account = panel_sections(why, setting=False)[1]

    assert (account.badge, account.badge_kind, account.steps) == ("NEW ACCOUNT - CREATE IT FIRST", ACCOUNT_NEW,
                                                                  ("one", "two"))


def test_PanelSections_ASettingThatGaveOnlyItsReason_LeavesTheRestOut():
    sections = panel_sections(AskExplanation(reason="the host", session=AskingSession(title="t")), setting=True)

    assert [s.heading for s in sections] == ["Who is asking", "What it is for"]


def test_EntryWindow_DrawsThePanel_AndHasNotNowInsteadOfCancel():
    # The drawing needs a screen; this reads the code that draws it, so a refactor that drops either is caught.
    import inspect

    source = inspect.getsource(entry_window)
    assert "class WhyPanel" in source and "panel_sections(request.why" in source
    assert '"Not now..." if asking else "Cancel"' in source


# --- Not now: the reply goes back, and never a secret -----------------------------------------------------------

def test_Ask_NotNow_PrintsTheOwnersReply_AndAuditsIt(store, window):
    window(AskOutcome(saved=False, reply="I don't have that account"))

    result = runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert result.exit_code == cli.EXIT_CANCELLED
    assert result.output.splitlines() == ["cancelled", "reply: I don't have that account"]
    line = _audit_lines()[-1]
    assert (line["outcome"], line["detail"]) == ("cancelled", "the owner said not now: I don't have that account")


def test_Ask_NotNow_AReplyOverSeveralLines_ComesBackAsOne(store, window):
    window(AskOutcome(saved=False, reply="Not this week.\n\n  Ask me   on Monday."))

    result = runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert result.output.splitlines()[1] == "reply: Not this week. Ask me on Monday."


def test_Ask_WindowClosedWithoutAReply_SaysSo(store, window):
    window(AskOutcome(saved=False, reply=""))

    result = runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert result.output.splitlines() == ["cancelled", "reply: " + NO_REPLY]


def test_ReplyProblem_RefusesWhatIsTypedInThePasswordBox():
    typed = new_secret()

    assert "password box" in reply_problem(f"here it is {typed}", typed)
    assert reply_problem("I don't have that account", typed) is None


def test_ReplyProblem_RefusesAStoredPassword(store):
    secret = add_entry(store, name="other", username="u")
    store.entries()  # the window has read the store, as ask does before it opens

    assert "stored password" in reply_problem(f"use {secret} instead", "")


def test_ReplyProblem_RefusesAnOverlongReply():
    assert "under 500" in reply_problem("x" * 501, "")
    assert reply_problem("x" * 500, "") is None


def test_Ask_NotNow_AReplyHoldingAStoredPassword_NeverReachesTheOutputOrTheAudit(store, window):
    secret = add_entry(store, name="other", username="u")
    window(AskOutcome(saved=False, reply=f"try {secret}"))

    result = runner.invoke(cli.app, ["ask", "info-smtp", *FULL])

    assert result.exit_code == cli.EXIT_CANCELLED
    assert secret not in _text(result)
    assert result.output.splitlines()[1].startswith("reply: (withheld by cc-secrets")
    assert secret not in paths.audit_path().read_text(encoding="utf-8")


def test_AskOutcome_AndTheExplanation_HaveNoSecretField():
    import dataclasses

    for cls in (AskOutcome, AskExplanation, AskingSession):
        names = {f.name for f in dataclasses.fields(cls)}
        assert not names & {"secret", "password", "value", "setting_value"}, cls


# --- Review findings (separate review of the first version) -----------------------------------------------------

def test_Ask_WithNoName_AStoredPasswordInTheReply_IsStillWithheld(store, window):
    # The store was read only when a name was given, so with no name the scrubber knew no stored password and the
    # reply guard and the command's output both let one through.
    secret = add_entry(store, name="other", username="u")
    window(AskOutcome(saved=False, reply=f"try {secret}"))

    result = runner.invoke(cli.app, ["ask", *FULL])

    assert result.exit_code == cli.EXIT_CANCELLED
    assert secret not in _text(result)
    assert secret not in paths.audit_path().read_text(encoding="utf-8")


def test_Ask_WithNoName_TheWindowsGuardKnowsTheStoredPasswords(store, monkeypatch):
    secret = add_entry(store, name="other", username="u")
    seen = {}

    def window(request, actions):
        seen["problem"] = reply_problem(f"use {secret}", "")
        return AskOutcome(saved=False)

    monkeypatch.setattr(entry_window, "show_ask", window)
    from src.redact import SCRUBBER
    SCRUBBER.clear()

    runner.invoke(cli.app, ["ask", *FULL])

    assert seen["problem"] is not None and "stored password" in seen["problem"]


@pytest.mark.parametrize("typed", ["two  spaces in it", "a\ttab in it", "a line\nbreak in it"])
def test_ReplyProblem_APasswordWithWhitespaceRuns_IsCaughtEitherWay(typed):
    assert reply_problem(f"here: {typed}", typed) is not None
    assert reply_problem(f"here: {' '.join(typed.split())}", typed) is not None


def _screen():
    return entry_window.display_problem() is None


@pytest.mark.skipif(not _screen(), reason="needs a screen for a real Tk window")
def test_ReplyBox_RealWindow_EscapeGoesBack_AndATypedReplyCountsAsAChange(store):
    from src.audit import OwnerApproval
    from src.window_actions import ASK_RECORD, MODE_ASK, FormRequest, WindowActions

    why = AskExplanation(reason="for", account=ACCOUNT_EXISTING, where_to_find="w", will_do="d", if_not_now="n",
                         session=AskingSession(title="129 - email"))
    request = FormRequest(mode=MODE_ASK, name="x", asked_by="Session 129", reason="for", why=why)
    closed = []
    root = entry_window.open_root("test")
    try:
        form = entry_window.EntryForm(root, request, lambda typed: "no", lambda *a: closed.append(a))
        panel = entry_window.WhyPanel(root, panel_sections(why, False))
        root.update_idletasks()
        panel.fit_height(form.height())

        assert "cancel" in root.bind("<Escape>")
        form.not_now()
        assert "_reply_back" in root.bind("<Escape>")
        form._reply_box.insert("1.0", "I don't have that account")
        assert form._changed()
        form._reply_back()
        assert "cancel" in root.bind("<Escape>")

        form._secret.set("Typed-secret-1234")
        form.not_now()
        form._reply_box.delete("1.0", "end")
        form._reply_box.insert("1.0", "it is Typed-secret-1234")
        form._send_reply()
        assert closed == [] and "password box" in form._reply_message.get()
        form._reply_box.delete("1.0", "end")
        form._reply_box.insert("1.0", "Not this\nweek")
        form._send_reply()
        assert closed == [(False, "Not this week")]
        assert form._secret.get() == ""
    finally:
        root.destroy()

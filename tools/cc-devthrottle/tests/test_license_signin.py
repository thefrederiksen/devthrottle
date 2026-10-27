"""Signing an account in as a spare, and renewing spares before their sign-in dies (license_signin, license_signin_ops).

The login process and the browser are faked everywhere: no test runs `claude auth login`, opens a browser, or reaches
claude.ai, and every config folder is a temporary one, never the machine's real Claude login. The store's own
adopt-and-renew behaviour, crashes included, is proven in test_license_store.py.
"""

from __future__ import annotations

import datetime as dt
import json
import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import license_signin, license_usage  # noqa: E402
from src.cli import app  # noqa: E402
from src.license_signin import SignInError  # noqa: E402
from src.license_store import FileKeyBackend, LicenseStore  # noqa: E402

ACCOUNT_A = "account-a@example.com"
ACCOUNT_B = "account-b@example.com"
ACCOUNT_C = "account-c@example.com"
URL = "https://claude.ai/oauth/authorize?code=true&client_id=x&state=s1&login_hint=account-b%40example.com"


def _url_for(email):
    """The authorize URL `claude auth login --email <email>` prints: it names the account as login_hint."""
    return "https://claude.ai/oauth/authorize?code=true&client_id=x&state=s1&login_hint=" + email.replace("@", "%40")
CODE = "AUTHCODE-never-shown-aaaaaaaaaaaa#STATE-never-shown-bbbbbbbbbbbbb"
NOW = dt.datetime(2026, 10, 11, 14, 0, tzinfo=dt.timezone.utc)
runner = CliRunner()


def _ms(moment: dt.datetime) -> int:
    return int(moment.timestamp() * 1000)


def _write_sign_in(folder: Path, email: str, serial: str, ends: dt.datetime) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    (folder / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": email}}), encoding="utf-8")
    (folder / ".credentials.json").write_text(json.dumps({"claudeAiOauth": {
        "accessToken": f"sk-ant-oat01-SECRET-{serial}", "refreshToken": f"sk-ant-ort01-SECRET-{serial}",
        "expiresAt": _ms(NOW + dt.timedelta(hours=8)), "refreshTokenExpiresAt": _ms(ends),
        "scopes": ["user:inference"], "subscriptionType": "max"}}), encoding="utf-8")


class FakeLogin:
    """`claude auth login` into a folder: prints a URL, and on the right code signs the folder in as `signs_in_as`."""

    instances: list["FakeLogin"] = []

    def __init__(self, folder, email, signs_in_as=None, url=None, finish_error=None, url_error=None):
        self.folder, self.email = Path(folder), email
        self.signs_in_as = signs_in_as or email
        url = url or _url_for(email)
        self.url, self.finish_error, self.url_error = url, finish_error, url_error
        self.code = None
        self.cancelled = 0
        FakeLogin.instances.append(self)

    def authorize_url(self, timeout_seconds):
        if self.url_error:
            raise SignInError(license_signin.LOGIN_FAILED, self.url_error)
        return self.url

    def finish(self, code, timeout_seconds):
        self.code = code
        if self.finish_error:
            raise SignInError(license_signin.LOGIN_FAILED, self.finish_error)
        _write_sign_in(self.folder, self.signs_in_as, f"NEW-{len(FakeLogin.instances)}", NOW + dt.timedelta(days=30))

    def cancel(self):
        self.cancelled += 1


def _starter(**kwargs):
    return lambda folder, email: FakeLogin(folder, email, **kwargs)


def _approve(url, email):
    assert url == _url_for(email)
    return CODE


@pytest.fixture(autouse=True)
def file_backend(monkeypatch):
    monkeypatch.setattr("src.license_store.sys.platform", "win32")
    FakeLogin.instances = []


@pytest.fixture
def store(tmp_path):
    _write_sign_in(tmp_path / "default", ACCOUNT_A, "A", NOW + dt.timedelta(days=20))
    return LicenseStore(str(tmp_path / "default"), str(tmp_path / "store"), backend=FileKeyBackend())


def _held(store):
    return {h.email: h.role for h in store.held_accounts()}


def _new_spare_left(store, email):
    """True when a new spare folder for `email` was left behind."""
    return Path(store.spare_folder(email)).exists()


def _add_spare(store, tmp_path, email, ends):
    _write_sign_in(tmp_path / f"login-{email}", email, f"OLD-{email}", ends)
    store.add(str(tmp_path / f"login-{email}"))


# --- sign_in -----------------------------------------------------------------------------------------


def test_signIn_NewAccount_IsAddedAsASpare_WithItsSignInTime(store):
    result = license_signin.sign_in(store, ACCOUNT_B, _approve, start_login=_starter(), now=lambda: NOW)

    assert not result.renewed
    assert _held(store) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}
    assert result.signed_in_at_utc == "2026-10-11T14:00:00Z"
    assert result.sign_in_expires_at_utc == "2026-11-10T14:00:00Z"
    login = FakeLogin.instances[0]
    assert login.code == CODE and login.cancelled == 1
    assert str(login.folder) == store.spare_folder(ACCOUNT_B), "the login runs straight into the spare folder"


def test_signIn_HeldSpare_IsRenewedInPlace(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=2))
    spare = store.spare_folder(ACCOUNT_B)

    result = license_signin.sign_in(store, ACCOUNT_B, _approve, start_login=_starter(), now=lambda: NOW)

    assert result.renewed and result.config_dir == spare
    key = json.loads((Path(spare) / ".credentials.json").read_text(encoding="utf-8"))["claudeAiOauth"]
    assert "NEW" in key["accessToken"]
    assert _held(store) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}


def test_signIn_ActiveAccount_IsRenewedInPlace(store):
    result = license_signin.sign_in(store, ACCOUNT_A, _approve, start_login=_starter(), now=lambda: NOW)

    assert result.renewed and result.role == "active" and result.config_dir == store.default_config_dir
    key = json.loads((Path(store.default_config_dir) / ".credentials.json").read_text(encoding="utf-8"))
    assert "NEW" in key["claudeAiOauth"]["accessToken"]
    assert _held(store) == {ACCOUNT_A: "active"}
    assert str(FakeLogin.instances[0].folder) == store.default_config_dir


def test_signIn_BrowserSignedInAsAnotherAccount_NothingIsKept(store):
    with pytest.raises(SignInError) as refused:
        license_signin.sign_in(store, ACCOUNT_B, _approve, start_login=_starter(signs_in_as=ACCOUNT_C),
                               now=lambda: NOW)

    assert refused.value.kind == license_signin.STORE_REFUSED
    assert _held(store) == {ACCOUNT_A: "active"}
    assert not _new_spare_left(store, ACCOUNT_B), "the unwanted sign-in must be deleted, not left behind"


@pytest.mark.parametrize("failure", ["no-url", "bad-code", "approve-raises", "finish-fails"])
def test_signIn_AnyFailure_CancelsTheLogin_AndTheHeldSpareIsUntouched(store, tmp_path, failure):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=2))
    old_key = (Path(store.spare_folder(ACCOUNT_B)) / ".credentials.json").read_text(encoding="utf-8")
    starter = {"no-url": _starter(url_error="no URL"), "finish-fails": _starter(finish_error="exit 1")}.get(
        failure, _starter())

    def approve(url, email):
        if failure == "approve-raises":
            raise SignInError(license_signin.WRONG_ACCOUNT, "signed in as someone else")
        return "not a code" if failure == "bad-code" else CODE

    with pytest.raises(SignInError) as failed:
        license_signin.sign_in(store, ACCOUNT_B, approve, start_login=starter, now=lambda: NOW)

    assert CODE not in str(failed.value)
    assert all(login.cancelled for login in FakeLogin.instances)
    # The spare held before is untouched by a renewal that failed.
    assert (Path(store.spare_folder(ACCOUNT_B)) / ".credentials.json").read_text(encoding="utf-8") == old_key


def test_signIn_TheRecordedTimeIsReportedByUsage(store):
    license_signin.sign_in(store, ACCOUNT_B, _approve, start_login=_starter(), now=lambda: NOW)
    key = next(k for k in license_usage.held_keys(store) if k.account == ACCOUNT_B)

    reading = license_usage.read_usage(key, lambda *a: license_usage.HttpAnswer(500, b""))

    row = reading.to_json()
    assert row["signedInAtUtc"] == "2026-10-11T14:00:00Z"
    assert row["signInExpiresAtUtc"] == "2026-11-10T14:00:00Z"


# --- approve_in_browser ------------------------------------------------------------------------------


class FakeProfile:
    def __init__(self):
        self.stopped = 0

    def environment(self):
        return {"BU_NAME": "profile", "BU_CDP_URL": "http://127.0.0.1:1"}

    def stop(self):
        self.stopped += 1


def _harness(result):
    seen = {}

    def run(script, env, timeout):
        seen["script"], seen["env"] = script, env
        return "some page noise\nRESULT " + json.dumps(result) + "\n"

    return run, seen


def test_approve_ConsentPageNamesTheAccount_ReturnsTheCode(store):
    profile = FakeProfile()
    run, seen = _harness({"state": "code", "code": CODE})

    code = license_signin.approve_in_browser(URL, ACCOUNT_B, "Profile B", open_profile=lambda p: profile,
                                             run_harness=run)

    assert code == CODE
    assert json.dumps(URL) in seen["script"] and json.dumps(ACCOUNT_B) in seen["script"]
    assert seen["env"]["BU_NAME"] == "profile"
    assert profile.stopped == 1


@pytest.mark.parametrize("result, kind", [
    ({"state": "wrong-account", "shown": ACCOUNT_C}, license_signin.WRONG_ACCOUNT),
    ({"state": "signed-out", "url": "https://claude.ai/login"}, license_signin.NOT_SIGNED_IN),
    ({"state": "needs-fresh-sign-in", "shown": ACCOUNT_B}, license_signin.NEEDS_FRESH_SIGN_IN),
    ({"state": "check-failed"}, license_signin.APPROVAL_FAILED),
    ({"state": "no-code"}, license_signin.APPROVAL_FAILED),
    ({"state": "code", "code": "short#x"}, license_signin.APPROVAL_FAILED),
])
def test_approve_AnythingButTheRightAccount_IsRefused(result, kind):
    profile = FakeProfile()
    run, _ = _harness(result)

    with pytest.raises(SignInError) as refused:
        license_signin.approve_in_browser(URL, ACCOUNT_B, "Profile B", open_profile=lambda p: profile,
                                          run_harness=run)

    assert refused.value.kind == kind
    assert profile.stopped == 1, "the profile must be put back as it was found"


def test_approve_NotAnAuthorizeUrl_RefusedBeforeAnyBrowserOpens():
    opened = []
    with pytest.raises(SignInError):
        license_signin.approve_in_browser("https://example.com/phish", ACCOUNT_B, "Profile B",
                                          open_profile=opened.append, run_harness=None)
    assert opened == []


@pytest.mark.parametrize("url", [_url_for(ACCOUNT_A), "https://claude.ai/oauth/authorize?code=true&state=s1"],
                         ids=["another-account", "no-account"])
def test_approve_UrlIsForAnotherAccountOrNone_RefusedBeforeAnyBrowserOpens(url):
    # Run by hand with the wrong email and that email's own profile, the consent page and --email would agree.
    opened = []
    with pytest.raises(SignInError) as refused:
        license_signin.approve_in_browser(url, ACCOUNT_C, "Profile C", open_profile=opened.append, run_harness=None)
    assert refused.value.kind == license_signin.WRONG_ACCOUNT
    assert opened == []


@pytest.mark.parametrize("renewed", [ACCOUNT_A, ACCOUNT_B], ids=["active", "spare"])
def test_signIn_RenewalApprovedByHandForAnotherAccount_HeldKeyIsNeverReplaced(store, tmp_path, renewed):
    # The review's path: the target renews an account it holds; on the other machine the approval is run with
    # another account's email and that account's signed-in profile. No code may come back, and the held sign-in -
    # which a wrong code would have REPLACED before any read-back - must be untouched.
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=2))
    folder = store.default_config_dir if renewed == ACCOUNT_A else store.spare_folder(ACCOUNT_B)
    held_before = (Path(folder) / ".credentials.json").read_text(encoding="utf-8")
    profile = FakeProfile()
    run, seen = _harness({"state": "code", "code": CODE})  # account C's profile would approve happily

    def operator_approves_as_c(url, email):
        return license_signin.approve_in_browser(url, ACCOUNT_C, "Profile C", open_profile=lambda p: profile,
                                                 run_harness=run)

    with pytest.raises(SignInError) as refused:
        license_signin.sign_in(store, renewed, operator_approves_as_c,
                               start_login=_starter(signs_in_as=ACCOUNT_C), now=lambda: NOW)

    assert refused.value.kind == license_signin.WRONG_ACCOUNT
    assert seen == {} and profile.stopped == 0, "no browser was opened"
    assert FakeLogin.instances[0].code is None, "no code reached the waiting login"
    assert (Path(folder) / ".credentials.json").read_text(encoding="utf-8") == held_before
    assert _held(store) == {ACCOUNT_A: "active", ACCOUNT_B: "spare"}


def test_approve_HarnessPrintsNoResult_IsAFailure():
    with pytest.raises(SignInError, match="without a result"):
        license_signin.approve_in_browser(URL, ACCOUNT_B, "P", open_profile=lambda p: FakeProfile(),
                                          run_harness=lambda s, e, t: "nothing\n")


def test_approveScript_ComparesTheShownAccountAndClosesItsTab():
    # The browser step runs inside browser-harness; this pins the two things it must never lose. The page itself is
    # judged by classify_page, proven against recorded pages in test_license_approver_check.py.
    script = license_signin._page_script(license_signin._APPROVE_SCRIPT, URL, ACCOUNT_B)
    assert "Logged in as" in script and 'if state != "can-approve"' in script
    assert "Target.closeTarget" in script and "finally:" in script


# --- renew_due -----------------------------------------------------------------------------------------


def test_renewDue_RenewsOnlySparesWithinFiveDays_AndReportsTheRest(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=4))      # due
    _add_spare(store, tmp_path, ACCOUNT_C, NOW + dt.timedelta(days=12))     # not due
    license_signin.set_browser(store, ACCOUNT_B, "Profile B")
    approved = []

    rows = license_signin.renew_due(store, approver_for=lambda p: (approved.append(p) or _approve),
                                    start_login=_starter(), now=lambda: NOW)

    by = {r.account: r for r in rows}
    assert by[ACCOUNT_A].result == "not-due" and by[ACCOUNT_A].role == "active"
    assert by[ACCOUNT_B].result == "renewed" and by[ACCOUNT_B].sign_in_expires_at_utc == "2026-11-10T14:00:00Z"
    assert by[ACCOUNT_C].result == "not-due"
    assert approved == ["Profile B"]


def test_renewDue_NoBrowserConfigured_IsAFailedRow_NeverSilent(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=1))

    rows = license_signin.renew_due(store, start_login=_starter(), now=lambda: NOW)

    row = next(r for r in rows if r.account == ACCOUNT_B)
    assert row.failed and row.result == "failed-no-approver"
    assert "no browser profile is configured" in row.detail
    assert FakeLogin.instances == []


def test_renewDue_ActiveAccountEndingSoon_IsRenewedInPlace(tmp_path):
    _write_sign_in(tmp_path / "default", ACCOUNT_A, "A", NOW + dt.timedelta(days=2))
    store = LicenseStore(str(tmp_path / "default"), str(tmp_path / "store"), backend=FileKeyBackend())
    license_signin.set_browser(store, ACCOUNT_A, "Profile A")

    rows = license_signin.renew_due(store, approver_for=lambda p: _approve, start_login=_starter(), now=lambda: NOW)

    assert [(r.account, r.role, r.result) for r in rows] == [(ACCOUNT_A, "active", "renewed")]
    assert rows[0].sign_in_expires_at_utc == "2026-11-10T14:00:00Z"


def test_renewDue_SpareWithNoEndDate_IsUnknownNotGuessed(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW)
    path = Path(store.spare_folder(ACCOUNT_B)) / ".credentials.json"
    doc = json.loads(path.read_text(encoding="utf-8"))
    del doc["claudeAiOauth"]["refreshTokenExpiresAt"]
    path.write_text(json.dumps(doc), encoding="utf-8")

    rows = license_signin.renew_due(store, start_login=_starter(), now=lambda: NOW)

    assert next(r for r in rows if r.account == ACCOUNT_B).result == "unknown-expiry"


def test_renewDue_Force_RenewsANotDueSpare(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=20))
    license_signin.set_browser(store, ACCOUNT_B, "Profile B")

    rows = license_signin.renew_due(store, account=ACCOUNT_B, force=True, approver_for=lambda p: _approve,
                                    start_login=_starter(), now=lambda: NOW)

    assert [(r.account, r.result) for r in rows] == [(ACCOUNT_B, "renewed")]


# --- the command line ----------------------------------------------------------------------------------


@pytest.fixture
def machine(tmp_path, monkeypatch):
    _write_sign_in(tmp_path / "default", ACCOUNT_A, "A", NOW + dt.timedelta(days=20))
    monkeypatch.setenv("CC_LICENSE_DEFAULT_CONFIG_DIR", str(tmp_path / "default"))
    monkeypatch.setenv("CC_LICENSE_STORE_DIR", str(tmp_path / "store"))
    monkeypatch.setattr(license_signin, "start_claude_login", lambda folder, email: FakeLogin(folder, email))
    # The commands call these with the real clock, and "due" means a sign-in ends within five days of now, so
    # without this the answers changed on 26 October 2026, five days before account A's fixed end. Pinned here,
    # every command test sees the same day as the rest of this file.
    sign_in, renew_due = license_signin.sign_in, license_signin.renew_due
    monkeypatch.setattr(license_signin, "sign_in", lambda *a, **k: sign_in(*a, **{"now": lambda: NOW, **k}))
    monkeypatch.setattr(license_signin, "renew_due", lambda *a, **k: renew_due(*a, **{"now": lambda: NOW, **k}))
    return tmp_path


def _run(*args, input=None):
    result = runner.invoke(app, ["license", *args], input=input)
    out = result.output
    assert "SECRET" not in out, "a key reached the output"
    return result


def test_cli_signInRemoteApproval_PrintsTheUrl_ReadsTheCode_AddsTheSpare(machine):
    result = _run("sign-in", ACCOUNT_B, "--remote-approval", "--json", input=CODE + "\n")

    assert result.exit_code == 0, result.stdout
    assert result.stdout.splitlines()[0] == f"authorize-url: {URL}"
    # Click below 8.2 mixes standard error (the "waiting:" line) into stdout, so the JSON is found, not assumed.
    answer, _ = json.JSONDecoder().raw_decode(result.stdout[result.stdout.index("{"):])
    assert answer["accountEmail"] == ACCOUNT_B and answer["result"] == "added"
    assert FakeLogin.instances[0].code == CODE
    assert CODE not in result.stdout


def test_cli_signInRemoteApproval_NoCode_FailsAndKeepsNothing(machine):
    result = _run("sign-in", ACCOUNT_B, "--remote-approval", input="\n")

    assert result.exit_code == 1
    assert "approval-failed" in result.output
    assert (machine / "store" / "spares").is_dir() and not (machine / "store" / "spares" / "account-b-example.com").exists()


def _first_json(text):
    # Click below 8.2 mixes standard error into stdout, so the JSON is found, not assumed.
    answer, _ = json.JSONDecoder().raw_decode(text[text.index("{"):])
    return answer


def test_cli_signInJson_Failure_AnswersTheReasonWordAsJson_NeverTheSentence(machine):
    # The Director reads this answer and writes its own sentence (contract 8.1), so the word must be on standard
    # output as JSON, after the authorize-url line.
    result = _run("sign-in", ACCOUNT_B, "--remote-approval", "--json", input="\n")

    assert result.exit_code == 1
    assert result.stdout.splitlines()[0] == f"authorize-url: {URL}"
    assert _first_json(result.stdout) == {"accountEmail": ACCOUNT_B, "result": "failed", "reason": "approval-failed"}
    assert not (machine / "store" / "spares" / "account-b-example.com").exists()


def test_cli_signIn_CallerClosesStandardInput_IsCalledOff_NotAToolFailure(machine):
    # The Director ends a sign-in nobody approved by closing standard input. That is the caller stopping the tool,
    # which the error reporting does not report (cc_shared/tool_errors.py); a blank line is still a failure.
    result = _run("sign-in", ACCOUNT_B, "--ask-when-no-browser", "--json", input="")

    assert result.exit_code == 1
    assert _first_json(result.stdout) == {"accountEmail": ACCOUNT_B, "result": "failed", "reason": "approval-failed"}
    assert "Aborted" in result.output  # the stop the error reporting reads as the caller's, never a defect
    assert not (machine / "store" / "spares" / "account-b-example.com").exists()

    blank = _run("sign-in", ACCOUNT_B, "--ask-when-no-browser", "--json", input="\n")
    assert blank.exit_code == 1 and "Aborted" not in blank.output


def test_cli_getBrowser_SaysTheProfileSetHere_OrThatNoneIs_AndNeverFails(machine):
    none = _run("get-browser", ACCOUNT_B, "--json")
    assert none.exit_code == 0
    assert json.loads(none.stdout) == {"accountEmail": ACCOUNT_B, "browser": None}
    assert "No browser profile is set" in _run("get-browser", ACCOUNT_B).output

    assert _run("set-browser", ACCOUNT_B, "Profile B").exit_code == 0
    one = _run("get-browser", ACCOUNT_B.upper(), "--json")
    assert one.exit_code == 0
    assert json.loads(one.stdout) == {"accountEmail": ACCOUNT_B, "browser": "Profile B"}


def test_cli_approveJson_AnswersTheCode_OrTheReasonWord(machine, monkeypatch):
    monkeypatch.setattr(license_signin, "approve_in_browser", lambda url, email, browser: CODE)
    approved = _run("approve", URL, "--email", ACCOUNT_B, "--browser", "Profile B", "--json")
    assert approved.exit_code == 0
    assert _first_json(approved.stdout) == {"accountEmail": ACCOUNT_B, "result": "approved", "code": CODE}

    def refuse(url, email, browser):
        raise SignInError(license_signin.WRONG_ACCOUNT, "the profile is signed in as someone else")

    monkeypatch.setattr(license_signin, "approve_in_browser", refuse)
    refused = _run("approve", URL, "--email", ACCOUNT_B, "--browser", "Profile B", "--json")
    assert refused.exit_code == 1
    assert _first_json(refused.stdout) == {"accountEmail": ACCOUNT_B, "result": "refused", "reason": "wrong-account"}


def test_cli_signIn_BrowserAndRemoteApproval_IsAUsageError(machine):
    assert _run("sign-in", ACCOUNT_B, "--browser", "P", "--remote-approval").exit_code == 2


def test_cli_signIn_NoBrowserConfigured_SaysHowToSetOne(machine):
    result = _run("sign-in", ACCOUNT_B)
    assert result.exit_code == 1
    assert "license set-browser" in result.output
    assert FakeLogin.instances == []


def test_cli_setBrowserThenRenew_FailedRenewalExitsOne_WithTheReason(machine, monkeypatch):
    _write_sign_in(machine / "login-b", ACCOUNT_B, "OLD-B", NOW + dt.timedelta(days=1))
    assert _run("add", str(machine / "login-b")).exit_code == 0
    assert _run("set-browser", ACCOUNT_B, "Profile B").exit_code == 0

    def refuse(profile):
        def approve(url, email):
            raise SignInError(license_signin.WRONG_ACCOUNT, f"profile {profile} is signed in as someone else")
        return approve

    monkeypatch.setattr(license_signin, "browser_approver", refuse)

    result = _run("renew", "--json")

    assert result.exit_code == 1
    answer, _ = json.JSONDecoder().raw_decode(result.stdout)  # the error follows the JSON
    row = next(r for r in answer["accounts"] if r["accountEmail"] == ACCOUNT_B)
    assert row["result"] == "failed-wrong-account" and row["browser"] == "Profile B"
    assert answer["failed"] == 1


def test_cli_renew_NothingDue_CountsEveryAccount(machine):
    result = _run("renew")
    assert result.exit_code == 0
    assert result.stdout.splitlines()[0].startswith("count: 1")


# --- what the Director runs: ask what is due, then sign each due account in by one route or the other ---


def test_renewDue_Check_SaysWhatIsDueAndWithWhichProfile_AndSignsNothingIn(store, tmp_path):
    _add_spare(store, tmp_path, ACCOUNT_B, NOW + dt.timedelta(days=4))      # due, a profile set here
    _add_spare(store, tmp_path, ACCOUNT_C, NOW + dt.timedelta(days=1))      # due, no profile here
    license_signin.set_browser(store, ACCOUNT_B, "Profile B")
    held_before = {email: (Path(store.spare_folder(email)) / ".credentials.json").read_text(encoding="utf-8")
                   for email in (ACCOUNT_B, ACCOUNT_C)}

    rows = {r.account: r for r in license_signin.renew_due(store, check=True, start_login=_starter(), now=lambda: NOW)}

    assert (rows[ACCOUNT_A].result, rows[ACCOUNT_B].result, rows[ACCOUNT_C].result) == ("not-due", "due", "due")
    assert (rows[ACCOUNT_B].browser, rows[ACCOUNT_C].browser) == ("Profile B", None)
    assert not any(r.failed for r in rows.values()), "an account with no profile here is due, not failed"
    assert FakeLogin.instances == [], "a check starts no login"
    for email, held in held_before.items():
        assert (Path(store.spare_folder(email)) / ".credentials.json").read_text(encoding="utf-8") == held


def test_cli_renewCheck_ExitsZero_WithADueAccountThatHasNoProfile(machine):
    store = LicenseStore(str(machine / "default"), str(machine / "store"), backend=FileKeyBackend())
    _add_spare(store, machine, ACCOUNT_B, NOW + dt.timedelta(hours=1))

    result = _run("renew", "--check", "--json")

    # The Director asks this every day. It must not end as a failure: nothing was tried, so nothing failed.
    assert result.exit_code == 0, result.output
    answer = json.loads(result.stdout)
    assert answer["failed"] == 0
    assert {r["accountEmail"]: r["result"] for r in answer["accounts"]} == {ACCOUNT_A: "not-due", ACCOUNT_B: "due"}


def test_cli_signInAskWhenNoBrowser_ProfileSetHere_ApprovesHere_AndPrintsNoUrl(machine, monkeypatch):
    store = LicenseStore(str(machine / "default"), str(machine / "store"), backend=FileKeyBackend())
    license_signin.set_browser(store, ACCOUNT_B, "Profile B")
    asked = []

    def approve_here(url, email, profile, **kwargs):
        asked.append((email, profile))
        return CODE

    monkeypatch.setattr(license_signin, "approve_in_browser", approve_here)

    result = _run("sign-in", ACCOUNT_B, "--ask-when-no-browser", "--json")

    assert result.exit_code == 0, result.output
    assert "authorize-url:" not in result.stdout, "the owner's profile on this machine wins; nobody else is asked"
    assert asked == [(ACCOUNT_B, "Profile B")]
    assert _first_json(result.stdout)["result"] == "added"


def test_cli_signInAskWhenNoBrowser_NoProfileHere_AsksAsRemoteApprovalDoes(machine):
    result = _run("sign-in", ACCOUNT_B, "--ask-when-no-browser", "--json", input=CODE + "\n")

    assert result.exit_code == 0, result.output
    assert result.stdout.splitlines()[0] == f"authorize-url: {URL}"
    assert _first_json(result.stdout)["result"] == "added"
    assert FakeLogin.instances[0].code == CODE


@pytest.mark.parametrize("other", [("--browser", "P"), ("--remote-approval",)], ids=["browser", "remote"])
def test_cli_signInAskWhenNoBrowser_WithAnotherRoute_IsAUsageError(machine, other):
    assert _run("sign-in", ACCOUNT_B, "--ask-when-no-browser", *other).exit_code == 2


# --- a sign-in by hand that replaces the active account keeps it as a spare (--make-active) ---------------------
#
# The owner's decision of 26 October 2026: a sign-in by hand never deletes a login. These run on the Windows/Linux
# credentials file AND on the macOS Keychain, through the store tests' fake `security` (license_store_world.World),
# so the Mac path is proven on every platform. Every folder is under tmp_path; no test reaches a real login.

from src.license_store import keychain_service_for  # noqa: E402
from license_store_world import World  # noqa: E402


class WorldLogin:
    """`claude auth login` into a folder of a World: on the code, signs the folder in through the World's backend - a
    credentials file, or a Keychain item named for the folder - exactly where Claude Code's own write puts it."""

    instances: list["WorldLogin"] = []

    def __init__(self, world, folder, email):
        self.world, self.folder, self.email = world, folder, email
        WorldLogin.instances.append(self)

    def authorize_url(self, timeout_seconds):
        return _url_for(self.email)

    def finish(self, code, timeout_seconds):
        assert code == CODE
        self.world.sign_in(self.folder, self.email)

    def cancel(self):
        pass


@pytest.fixture(params=["file", "keychain"])
def world(tmp_path, request):
    w = World(tmp_path, request.param)
    w.removed = set()
    WorldLogin.instances = []
    return w


def _world_starter(world):
    return lambda folder, email: WorldLogin(world, folder, email)


def _roles(store):
    return [(h.email, h.role) for h in store.held_accounts()]


def test_signInMakeActive_ActiveAHandSignInToB_BIsActiveAndAIsKeptAsASpare(world):
    a_key = world.sign_in(world.default, ACCOUNT_A)
    store = world.store()

    result = license_signin.sign_in(store, ACCOUNT_B, _approve, make_active=True, start_login=_world_starter(world),
                                    now=lambda: NOW)

    assert _roles(store) == [(ACCOUNT_B, "active"), (ACCOUNT_A, "spare")]
    assert world.key_at(store.spare_folder(ACCOUNT_A)) == a_key, "A's own sign-in is the one kept, unchanged"
    assert world.email_at(world.default) == ACCOUNT_B
    assert result.role == "active" and result.config_dir == store.default_config_dir and not result.renewed
    assert (result.previous_kept_as_spare, result.previous_kept_in) == (ACCOUNT_A, store.spare_folder(ACCOUNT_A))
    # B was signed in into its own spare folder, never straight over the active sign-in.
    assert [login.folder for login in WorldLogin.instances] == [store.spare_folder(ACCOUNT_B)]
    world.assert_each_signin_in_exactly_one_place()
    if world.kind == "keychain":
        assert keychain_service_for(store.spare_folder(ACCOUNT_A)) in world.security.items


def test_signInMakeActive_BHeldAsASpare_IsRenewedThenMadeActive_AndAIsKept(world):
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()
    store.add(world.new_signin_folder(ACCOUNT_B))
    old_b = world.key_at(store.spare_folder(ACCOUNT_B))
    world.gone_ok = frozenset({old_b})

    result = license_signin.sign_in(store, ACCOUNT_B, _approve, make_active=True, start_login=_world_starter(world),
                                    now=lambda: NOW)

    assert result.renewed and result.previous_kept_as_spare == ACCOUNT_A
    assert _roles(store) == [(ACCOUNT_B, "active"), (ACCOUNT_A, "spare")]
    assert world.key_at(world.default) != old_b
    world.assert_each_signin_in_exactly_one_place()


def test_signInMakeActive_TheActiveAccountAgain_IsRenewedInPlace_NotDuplicatedInTheSpares(world):
    old_a = world.sign_in(world.default, ACCOUNT_A)
    world.gone_ok = frozenset({old_a})
    store = world.store()

    result = license_signin.sign_in(store, ACCOUNT_A, _approve, make_active=True, start_login=_world_starter(world),
                                    now=lambda: NOW)

    assert result.renewed and result.role == "active" and result.previous_kept_as_spare is None
    assert _roles(store) == [(ACCOUNT_A, "active")]
    assert not world.backend().exists(store.spare_folder(ACCOUNT_A))
    assert world.key_at(world.default) != old_a
    world.assert_each_signin_in_exactly_one_place()


def test_signInMakeActive_AAlreadyHeldAsASpare_TheSpareIsRefreshedNotDoubled(world):
    # A is active AND held as a stale spare - what a raw `claude auth login` over a spare leaves behind.
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()
    stale_a = world.sign_in(store.spare_folder(ACCOUNT_A), ACCOUNT_A)
    active_a = world.key_at(world.default)

    license_signin.sign_in(store, ACCOUNT_B, _approve, make_active=True, start_login=_world_starter(world),
                           now=lambda: NOW)

    assert _roles(store) == [(ACCOUNT_B, "active"), (ACCOUNT_A, "spare")], "A is held once"
    assert world.key_at(store.spare_folder(ACCOUNT_A)) == active_a, "the spare now holds A's current sign-in"
    # The stale one is kept in the store's backups, as every heal does - never deleted.
    assert [world.key_at(b) for b in world.backups()] == [stale_a]
    world.assert_each_signin_in_exactly_one_place()


def test_signInMakeActive_NothingActive_BBecomesActive_AndNothingIsKept(world):
    store = world.store()

    result = license_signin.sign_in(store, ACCOUNT_B, _approve, make_active=True, start_login=_world_starter(world),
                                    now=lambda: NOW)

    assert _roles(store) == [(ACCOUNT_B, "active")]
    assert result.previous_kept_as_spare is None and result.previous_kept_in is None
    world.assert_each_signin_in_exactly_one_place()


@pytest.mark.parametrize("why", ["account-unknown", "spare-folder-taken"])
def test_signInMakeActive_ActiveCannotBeKept_NothingIsSignedInOrOverwritten_AndItSaysWhy(world, why):
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()
    if why == "account-unknown":
        # The active key's account block names no account, so the key could not be put away under any name.
        (Path(world.default) / ".claude.json").write_text(json.dumps({"numStartups": 1}), encoding="utf-8")
    else:
        # A's spare folder holds another account's key, which putting A away would overwrite.
        world.sign_in(store.spare_folder(ACCOUNT_A), ACCOUNT_C)
    before = {folder: world.key_at(folder) for folder in world.folders()}

    with pytest.raises(SignInError) as refused:
        license_signin.sign_in(store, ACCOUNT_B, _approve, make_active=True, start_login=_world_starter(world),
                               now=lambda: NOW)

    assert refused.value.kind == license_signin.STORE_REFUSED
    message = str(refused.value)
    assert message.startswith(f"{ACCOUNT_B} was not signed in, because the account active here could not be kept")
    assert ("oauthAccount email" if why == "account-unknown"
            else f"already holds a sign-in for {ACCOUNT_C}; putting the active key away would overwrite") in message
    assert refused.value.next_steps, "the refusal names the fix"
    assert WorldLogin.instances == [], "no login was started"
    assert {folder: world.key_at(folder) for folder in world.folders()} == before, "nothing was written anywhere"
    assert not Path(store.spare_folder(ACCOUNT_B)).exists()


def test_signInMakeActive_ActiveChangesDuringTheApproval_SwitchIsRefused_NothingIsOverwritten(world):
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()
    store.add(world.new_signin_folder(ACCOUNT_C))

    def approve_while_the_director_switches_to_c(url, email):
        store.switch(ACCOUNT_C)
        return CODE

    with pytest.raises(SignInError) as failed:
        license_signin.sign_in(store, ACCOUNT_B, approve_while_the_director_switches_to_c, make_active=True,
                               start_login=_world_starter(world), now=lambda: NOW)

    assert failed.value.kind == license_signin.STORE_REFUSED
    assert f"{ACCOUNT_B} was signed in and is held as a spare" in str(failed.value)
    assert "could not be made active" in str(failed.value) and ACCOUNT_C in str(failed.value)
    # C stays active, A stays the spare that switch made it, and B is kept as a spare: every sign-in is still held.
    assert _roles(store) == [(ACCOUNT_C, "active"), (ACCOUNT_A, "spare"), (ACCOUNT_B, "spare")]
    world.assert_each_signin_in_exactly_one_place()


def test_signIn_WithoutMakeActive_StillOnlyAddsASpare(world):
    world.sign_in(world.default, ACCOUNT_A)
    store = world.store()

    result = license_signin.sign_in(store, ACCOUNT_B, _approve, start_login=_world_starter(world), now=lambda: NOW)

    assert _roles(store) == [(ACCOUNT_A, "active"), (ACCOUNT_B, "spare")]
    assert result.previous_kept_as_spare is None


def test_cli_signInMakeActive_AnswersWhatWasKept_AndTheListTheDirectorReportsHoldsAAsASpare(machine):
    result = _run("sign-in", ACCOUNT_B, "--make-active", "--remote-approval", "--json", input=CODE + "\n")

    assert result.exit_code == 0, result.output
    answer = _first_json(result.stdout)
    assert answer["accountEmail"] == ACCOUNT_B and answer["role"] == "active" and answer["result"] == "added"
    assert answer["previousKeptAsSpare"] == ACCOUNT_A
    assert Path(answer["previousKeptIn"]).parent == machine / "store" / "spares"
    # `license list --json` is the listing the Director's usage report sends whole to the Gateway, so the next report
    # carries A as a spare.
    listed = json.loads(_run("list", "--json").stdout)
    assert [(a["email"], a["role"]) for a in listed["accounts"]] == [(ACCOUNT_B, "active"), (ACCOUNT_A, "spare")]


def test_cli_signInMakeActive_Text_SaysWhichAccountWasKept(machine):
    result = _run("sign-in", ACCOUNT_B, "--make-active", "--remote-approval", input=CODE + "\n")

    assert result.exit_code == 0, result.output
    assert f"Added: {ACCOUNT_B} is the active account in" in result.output
    assert f"{ACCOUNT_A}, active before, is kept as a spare in" in result.output


# --- director_browser: the profile the Director started for its own run ---------------------------------------------
#
# 19 to 26 October 2026: every approver check the Director ran failed in seconds, because the Director's own process
# has no session, and the only way this tool opened a browser profile was through the Gateway as a session
# ("CC_DIRECTOR_ID is not set"). The Director now starts the profile itself and names it in the run's environment.

DIRECTOR_RUN = {
    license_signin.ENV_ATTACHED_BROWSER_ID: "work-chrome",
    license_signin.ENV_ATTACHED_BROWSER_NAME: "Work Chrome",
    "BU_NAME": "work-chrome",
    "BU_CDP_URL": "http://127.0.0.1:9333",
}


@pytest.mark.parametrize("asked", ["work-chrome", "Work Chrome", "WORK-CHROME"])
def test_director_browser_TheDirectorStartedTheProfile_UsesItWithoutTheGateway(asked):
    browser = license_signin.director_browser(asked, environ=dict(DIRECTOR_RUN))

    assert (browser.id, browser.name) == ("work-chrome", "Work Chrome")
    assert browser.environment() == {"BU_NAME": "work-chrome", "BU_CDP_URL": "http://127.0.0.1:9333"}
    browser.stop()  # the Director puts it back; nothing to do here, and nothing raised


def test_director_browser_TheDirectorStartedAnotherProfile_Refuses():
    with pytest.raises(SignInError) as raised:
        license_signin.director_browser("Home Chrome", environ=dict(DIRECTOR_RUN))

    assert "started the browser profile \"Work Chrome\" for this run, not \"Home Chrome\"" in str(raised.value)


@pytest.mark.parametrize("missing", ["BU_NAME", "BU_CDP_URL"])
def test_director_browser_TheDirectorGaveNoAttachEnvironment_Refuses(missing):
    environ = dict(DIRECTOR_RUN)
    del environ[missing]

    with pytest.raises(SignInError) as raised:
        license_signin.director_browser("work-chrome", environ=environ)

    assert "did not give BU_NAME and BU_CDP_URL" in str(raised.value)


def test_director_browser_InASession_StillGoesThroughTheGateway(monkeypatch):
    # Nothing attached: the session path, unchanged. Without a session either, it says so - the 26 October failure.
    for name in DIRECTOR_RUN:
        monkeypatch.delenv(name, raising=False)
    monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)

    with pytest.raises(SignInError) as raised:
        license_signin.director_browser("work-chrome", environ={})

    assert "CC_DIRECTOR_ID is not set" in str(raised.value)


def test_approve_RunByTheDirector_ApprovesInTheProfileItStarted(monkeypatch):
    # The caller `license approve` uses: the default way to open the profile, with the Director's run environment.
    for name, value in DIRECTOR_RUN.items():
        monkeypatch.setenv(name, value)
    monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)
    run, seen = _harness({"state": "code", "code": CODE})

    code = license_signin.approve_in_browser(_url_for(ACCOUNT_B), ACCOUNT_B, "work-chrome", run_harness=run)

    assert code == CODE
    assert seen["env"] == {"BU_NAME": "work-chrome", "BU_CDP_URL": "http://127.0.0.1:9333"}


def test_browser_approver_RunByTheDirector_SignInAndRenewApproveInTheProfileItStarted(monkeypatch):
    # The caller `license sign-in` (and so the renewal, which signs in again) uses, with the Director's run environment.
    for name, value in DIRECTOR_RUN.items():
        monkeypatch.setenv(name, value)
    monkeypatch.delenv("CC_DIRECTOR_ID", raising=False)
    run, seen = _harness({"state": "code", "code": CODE})

    approve = license_signin.browser_approver("Work Chrome", run_harness=run)

    assert approve(_url_for(ACCOUNT_B), ACCOUNT_B) == CODE
    assert seen["env"]["BU_CDP_URL"] == "http://127.0.0.1:9333"

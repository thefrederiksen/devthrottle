"""R1 and R2: where the check finds cc-gmail's sign-in, how it refreshes it, how it writes it back, and how each
sign-in failure fails that one channel only - an Outlook channel beside it is read as usual.

google-auth is used in these tests only, to prove the written-back file is one google-auth's own loader reads."""

import json
import os
import stat

import pytest
from google.oauth2.credentials import Credentials

from gmail_rig import gw, world  # noqa: F401 - the fixtures
from gmail_rig import (GMAIL_BOX, MACHINE, NICKNAME, NOW, OUTLOOK_BOX, READONLY, MODIFY, TOKEN_URI, channel, cursor,
                       gmsg, phase2, section, token_json)

OUTLOOK_CURSOR = {"newest": "2026-10-18T07:00:00Z", "idsAtNewest": []}
GMAIL_CURSOR = cursor("2026-10-18T07:00:00Z")
EXPIRED = "2026-10-18T11:00:00Z"


def two_channels(gw, mode="observe"):
    """A Gmail channel beside a healthy Outlook one with new mail, so 'the other channel is read' is measured."""
    gw.plant(OUTLOOK_BOX)
    gw.graph.box(OUTLOOK_BOX)["inbox"].append(phase2.msg("o1", "2026-10-18T08:00:00Z"))
    gw.gmail.box(GMAIL_BOX).append(gmsg("g1", "2026-10-18T08:00:00Z"))
    gw.memory(channel(OUTLOOK_BOX, cursor=OUTLOOK_CURSOR), channel(GMAIL_BOX, provider="gmail", mode=mode,
                                                                    cursor=GMAIL_CURSOR))


def gmail_failed(gw):
    """The Gmail channel failed alone: its section carries the failure, its memory is byte for byte as it was, the
    Outlook channel reported its mail and its cursor moved. Returns (sentence, detail)."""
    before = phase2.entry_text(gw.read_memory(), GMAIL_BOX)
    code, output, context, err = gw.run()
    assert code == 0
    failure = section(context, GMAIL_BOX)["failure"]
    assert section(context, GMAIL_BOX)["messages"] == []
    assert phase2.entry_text(gw.read_memory(), GMAIL_BOX) == before
    assert len(section(context, OUTLOOK_BOX)["messages"]) == 1 and output["count"] == 1
    assert gw.read_memory()["channels"][0]["cursor"]["newest"] == "2026-10-18T08:00:00Z"
    assert len(failure["sentence"]) <= gw.screen.SENTENCE_LIMIT
    assert "gtoken" not in err and "refresh-for" not in err and "example-client-secret" not in err
    return failure["sentence"], failure["detail"]


# ---------------------------------------------------------------------------------------------------- where it looks


def test_Store_IsUnderTheAccountRecordsHome_NeverHome(gw):
    """R1.1: <home>/.cc-director/config/gmail with <home> from pwd. A store under HOME is not read."""
    scratch_home = phase2.Path(os.environ["HOME"])
    decoy = scratch_home / ".cc-director" / "config" / "gmail" / "accounts" / NICKNAME
    decoy.mkdir(parents=True)
    (decoy / "config.json").write_text(json.dumps({"email": GMAIL_BOX, "auth_method": "oauth"}), encoding="utf-8")
    (decoy / "token.json").write_text(json.dumps(token_json(GMAIL_BOX)), encoding="utf-8")
    two_channels(gw)
    sentence, detail = gmail_failed(gw)
    assert sentence.startswith(f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail accounts add")
    assert str(gw.store) in detail


def test_Store_LocalAppDataIsIgnoredOnLinux(gw, monkeypatch):
    elsewhere = gw.home / "localappdata"
    monkeypatch.setenv("LOCALAPPDATA", str(elsewhere))
    folder = elsewhere / "cc-director" / "config" / "gmail" / "accounts" / NICKNAME
    folder.mkdir(parents=True)
    (folder / "config.json").write_text(json.dumps({"email": GMAIL_BOX, "auth_method": "oauth"}), encoding="utf-8")
    two_channels(gw)
    assert "is not signed in" in gmail_failed(gw)[0]
    gw.plant_gmail()
    code, _, context, _ = gw.run()
    assert code == 0 and "failure" not in section(context, GMAIL_BOX)


def test_Store_NoLookIntoDirectorInstances(gw):
    folder = gw.base / "instances" / "default" / "config" / "gmail" / "accounts" / NICKNAME
    folder.mkdir(parents=True)
    (folder / "config.json").write_text(json.dumps({"email": GMAIL_BOX, "auth_method": "oauth"}), encoding="utf-8")
    two_channels(gw)
    assert "is not signed in" in gmail_failed(gw)[0]


def test_Account_MatchedWithoutRegardToCase_AndNamedByItsDirectory(gw):
    gw.plant_gmail(nickname="Work-Mail", config_email="Desk@Example.COM")
    two_channels(gw)
    code, _, context, _ = gw.run()
    assert code == 0 and len(section(context, GMAIL_BOX)["messages"]) == 1


def test_Premise_CcGmailsDashATakesTheDirectoryNameAsGiven():
    """R1.4: the nickname in every fix is the account's directory name, because cc-gmail's resolve_account returns -a
    as given and get_account_dir joins it under accounts/. Read from cc-gmail's own source."""
    source = (phase2.REPO / "tools" / "cc-gmail" / "src" / "auth.py").read_text(encoding="utf-8")
    resolve = source.split("def resolve_account(")[1].split("\ndef ")[0]
    assert "if account:\n        return account" in resolve
    directory = source.split("def get_account_dir(")[1].split("\ndef ")[0]
    assert "accounts_dir_path() / account" in directory
    assert "Credentials.from_authorized_user_file(str(token_path)" in source


# ---------------------------------------------------------------------------------------------------- the failures


def test_NoAccount_FailsThatChannelNamingAccountsAdd(gw):
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail accounts add desk and give it this address"


def test_TwoAccounts_FailsThatChannelNamingEveryDirectory_NeverPicks(gw):
    gw.plant_gmail(nickname="desk")
    gw.plant_gmail(nickname="desk-again")
    two_channels(gw)
    sentence, detail = gmail_failed(gw)
    assert sentence == (f"{GMAIL_BOX} is in more than one cc-gmail account on {MACHINE}: keep one and remove the others "
                        f"with cc-gmail accounts remove <name>")
    assert "desk" in detail and "desk-again" in detail
    assert gw.gmail.log == []


def test_AppPassword_FailsThatChannelNamingMethodOauth(gw):
    gw.plant_gmail(auth_method="app_password", token=None)
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} signs in to cc-gmail on {MACHINE} without OAuth: run cc-gmail -a desk auth --method oauth"


def test_NoTokenFile_FailsThatChannelNamingAuth(gw):
    gw.plant_gmail(token=None)
    two_channels(gw)
    sentence, detail = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail -a desk auth"
    assert "token.json" in detail


@pytest.mark.parametrize("missing", ["refresh_token", "client_id", "client_secret", "token_uri"])
def test_TokenWithoutAKeyTheRefreshNeeds_FailsThatChannelNamingAuthForce(gw, missing):
    """R2: no fallback to credentials.json - cc-gmail's own refresh reads token.json only."""
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED, **{missing: ...}))
    two_channels(gw)
    sentence, detail = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail -a desk auth --force"
    assert missing in detail
    assert gw.gmail.refreshes() == []


def test_RefreshRefused_FailsThatChannelNamingAuthForce(gw):
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED))
    gw.gmail.refresh_answer[GMAIL_BOX] = "refused"
    two_channels(gw)
    sentence, detail = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail -a desk auth --force"
    assert "invalid_grant" in detail
    assert not (gw.store / "accounts" / NICKNAME / "token-refresh-error.json").exists()
    assert gw.gmail.gmail_log() == []


@pytest.mark.parametrize("mode,scopes", [("observe", ["https://www.googleapis.com/auth/gmail.send"]),
                                         ("live", [READONLY]),
                                         ("live", "https://www.googleapis.com/auth/gmail.readonly"),
                                         ("observe", [])], ids=["observe-send-only", "live-readonly",
                                                                "live-readonly-as-text", "observe-none"])
def test_ScopeTheModeNeedsMissing_FailsThatChannelNamingAuthForce(gw, mode, scopes):
    gw.plant_gmail(token=token_json(GMAIL_BOX, scopes=scopes))
    two_channels(gw, mode=mode)
    sentence, detail = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} on {MACHINE} lacks the Gmail permission {mode} mode needs: run cc-gmail -a desk auth --force"
    assert "gmail.modify" in detail


@pytest.mark.parametrize("mode,scopes", [("observe", [READONLY]), ("observe", [MODIFY]), ("live", [MODIFY]),
                                         ("observe", f"{READONLY} {MODIFY}")])
def test_ScopeTheModeNeeds_IsEnough(gw, mode, scopes):
    gw.plant_gmail(token=token_json(GMAIL_BOX, scopes=scopes))
    two_channels(gw, mode=mode)
    code, _, context, _ = gw.run()
    assert code == 0 and "failure" not in section(context, GMAIL_BOX)


def test_ATokenUriThatIsNotGoogles_IsNeverFollowed(gw):
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED, token_uri="https://evil.example.com/token"))
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence == f"the Gmail sign-in of {GMAIL_BOX} on {MACHINE} names another sign-in service: run cc-gmail -a desk auth --force"
    assert gw.gmail.log == []


@pytest.mark.parametrize("content", ["not json", "[]", json.dumps(token_json(GMAIL_BOX, expiry="tomorrow"))],
                         ids=["not-json", "not-an-object", "bad-expiry"])
def test_ATokenFileCcGmailDidNotWrite_FailsThatChannelNamingAuthForce(gw, content):
    gw.plant_gmail(token=content)
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence == f"{GMAIL_BOX} is not signed in on {MACHINE}: run cc-gmail -a desk auth --force"


def test_ATokenFileThatCannotBeRead_FailsThatChannelSayingSo(gw):
    gw.plant_gmail()
    path = gw.store / "accounts" / NICKNAME / "token.json"
    path.unlink()
    path.mkdir()
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence.startswith(f"the saved sign-in of {GMAIL_BOX} on {MACHINE} cannot be read (IsADirectoryError)")


def test_AnAccountsSettingsThatCannotBeRead_FailsThatChannelSayingSo(gw):
    folder = gw.store / "accounts" / NICKNAME / "config.json"
    folder.mkdir(parents=True)
    two_channels(gw)
    sentence, _ = gmail_failed(gw)
    assert sentence == (f"{GMAIL_BOX} is not signed in on {MACHINE}, and a cc-gmail account's settings cannot be read: "
                        f"IsADirectoryError")


@pytest.mark.parametrize("behaviour,expected", [
    ("unreachable", "could not reach the Google sign-in service from testmachine for desk@example.com: connection refused"),
    (500, "the Google sign-in service failed on testmachine for desk@example.com: HTTP 500"),
    ({"access_token": ""}, "the Google sign-in service failed on testmachine for desk@example.com: its answer held no access token"),
    ("not-json", "the Google sign-in service failed on testmachine for desk@example.com: it answered something that is "
                 "not JSON"),
], ids=["unreachable", "http-500", "no-access-token", "not-json"])
def test_TheSignInServiceFailing_FailsThatChannelSayingSo(gw, behaviour, expected):
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED))
    gw.gmail.refresh_answer[GMAIL_BOX] = behaviour
    two_channels(gw)
    assert gmail_failed(gw)[0] == expected


@pytest.mark.parametrize("failure,expected", [
    (503, "the Gmail API answered HTTP 503 for desk@example.com while reading the Inbox"),
    ("unreachable", "the Gmail API could not be reached from testmachine for desk@example.com while reading the Inbox: "
                    "connection refused"),
    ("not-json", "the Gmail API answered something that is not JSON for desk@example.com while reading the Inbox"),
    ("bad-list", "the Gmail API answered a list without usable message ids for desk@example.com while reading the Inbox"),
    ("bad-message", "the Gmail API answered a message without a usable id or receive time for desk@example.com while "
                    "reading the Inbox"),
], ids=["http-503", "unreachable", "not-json", "bad-list", "bad-message"])
def test_TheGmailApiFailing_FailsThatChannelAndTheSameMailIsReadNextRun(gw, failure, expected):
    gw.plant_gmail()
    two_channels(gw)
    gw.gmail.fail_for[GMAIL_BOX] = failure
    assert gmail_failed(gw)[0] == expected
    gw.gmail.fail_for.clear()
    _, _, context, _ = gw.run()
    assert len(section(context, GMAIL_BOX)["messages"]) == 1


def test_EveryGmailSentence_FitsTheLimitWithLongNames(gw, monkeypatch):
    """CONTRACT 1.1: 165 characters, with a 30-character address, the longest Windows computer name and a
    20-character nickname."""
    monkeypatch.setattr(gw.screen.socket, "gethostname", lambda: "DESKTOP-ABCD123.corp.example.com")
    address, nickname = "second.mailbox.desk@example.org", "second-mailbox-desk1"
    assert len(address) == 31 and len(nickname) == 20
    cases = [
        lambda: None,
        lambda: (gw.plant_gmail(address, nickname), gw.plant_gmail(address, nickname + "x")),
        lambda: gw.plant_gmail(address, nickname, auth_method="app_password", token=None),
        lambda: gw.plant_gmail(address, nickname, token=None),
        lambda: gw.plant_gmail(address, nickname, token=token_json(address, expiry=EXPIRED, client_id=...)),
        lambda: gw.plant_gmail(address, nickname, token=token_json(address, scopes=[READONLY])),
        lambda: gw.plant_gmail(address, nickname, token=token_json(address, expiry=EXPIRED,
                                                                     token_uri="https://evil.example.com/t")),
    ]
    sentences = []
    for index, plant in enumerate(cases):
        if gw.store.exists():
            import shutil
            shutil.rmtree(gw.store)
        plant()
        gw.memory(channel(address, provider="gmail", mode="live", cursor=GMAIL_CURSOR))
        code, _, context, _ = gw.run()
        sentences.append(section(context, address)["failure"]["sentence"])
    assert len(set(sentences)) == len(cases)
    for sentence in sentences:
        assert len(sentence) <= 165, (len(sentence), sentence)
        assert "DESKTOP-ABCD123" in sentence or "Gmail permission" in sentence or "sign-in of" in sentence


# ---------------------------------------------------------------------------------------------------- refresh, write-back


def test_AValidToken_IsUsedWithoutARefresh_AndTheFileIsNotTouched(gw):
    folder = gw.plant_gmail()
    before = (folder / "token.json").read_bytes(), (folder / "token.json").stat().st_mtime_ns
    two_channels(gw)
    gw.run()
    assert gw.gmail.refreshes() == []
    assert ((folder / "token.json").read_bytes(), (folder / "token.json").stat().st_mtime_ns) == before


@pytest.mark.parametrize("expiry", [EXPIRED, "2026-10-18T12:04:59Z", "2026-10-18T12:04:59.999999Z", None],
                         ids=["expired", "within-5-minutes", "within-5-minutes-fraction", "no-expiry"])
def test_ATokenExpiringWithinFiveMinutes_IsRefreshedWithTheStandardForm(gw, expiry):
    saved = token_json(GMAIL_BOX, expiry=expiry if expiry is not None else ...)
    gw.plant_gmail(token=saved)
    two_channels(gw)
    code, _, context, _ = gw.run()
    assert code == 0 and len(section(context, GMAIL_BOX)["messages"]) == 1
    (method, url, body, headers), = gw.gmail.refreshes()
    assert (method, url) == ("POST", TOKEN_URI)
    assert dict(phase2.urllib.parse.parse_qsl(body)) == {
        "grant_type": "refresh_token", "client_id": saved["client_id"], "client_secret": saved["client_secret"],
        "refresh_token": saved["refresh_token"]}
    assert all(h[3]["authorization"] == "Bearer gtoken-for-desk@example.com#1" for h in gw.gmail.gmail_log())


def test_ATokenWithMoreThanFiveMinutesLeft_IsNotRefreshed(gw):
    gw.plant_gmail(token=token_json(GMAIL_BOX, expiry="2026-10-18T12:05:01Z"))
    two_channels(gw)
    gw.run()
    assert gw.gmail.refreshes() == []


def test_WriteBack_SameKeysInOrder_TokenAndExpiryReplaced_Mode0600_ReadByGoogleAuth(gw):
    saved = token_json(GMAIL_BOX, expiry=EXPIRED)
    folder = gw.plant_gmail(token=saved)
    others = sorted(p.name for p in folder.iterdir())
    two_channels(gw)
    gw.run()
    path = folder / "token.json"
    written = json.loads(path.read_text(encoding="utf-8"))
    assert list(written) == list(saved)
    assert written["token"] == "gtoken-for-desk@example.com#1"
    assert written["expiry"] == "2026-10-18T12:59:59Z"
    assert {k: v for k, v in written.items() if k not in ("token", "expiry")} == \
        {k: v for k, v in saved.items() if k not in ("token", "expiry")}
    assert stat.S_IMODE(path.stat().st_mode) == 0o600
    assert sorted(p.name for p in folder.iterdir()) == others      # no temporary file left, nothing added or deleted
    creds = Credentials.from_authorized_user_file(str(path))
    assert creds.token == written["token"] and creds.refresh_token == saved["refresh_token"]
    assert creds.client_id == saved["client_id"] and creds.expiry.isoformat() == "2026-10-18T12:59:59"
    assert sorted(creds.scopes) == sorted(saved["scopes"])
    err = gw.last_err
    assert "the renewed sign-in was written back to its file" in err and "gtoken" not in err


def test_WriteBack_ANewRefreshTokenFromGoogle_IsKept(gw):
    folder = gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED))
    gw.gmail.refresh_answer[GMAIL_BOX] = {"refresh_token": "refresh-for-desk@example.com-rotated"}
    two_channels(gw)
    gw.run()
    written = json.loads((folder / "token.json").read_text(encoding="utf-8"))
    assert written["refresh_token"] == "refresh-for-desk@example.com-rotated"
    assert Credentials.from_authorized_user_file(str(folder / "token.json")).refresh_token == written["refresh_token"]


def test_WriteBack_AFolderThatCannotBeWritten_FailsThatChannelAndLeavesTheFile(gw):
    folder = gw.plant_gmail(token=token_json(GMAIL_BOX, expiry=EXPIRED))
    before = (folder / "token.json").read_bytes()
    folder.chmod(0o500)
    try:
        two_channels(gw)
        sentence, _ = gmail_failed(gw)
    finally:
        folder.chmod(0o700)
    assert sentence == (f"the renewed sign-in of {GMAIL_BOX} could not be saved on {MACHINE} (PermissionError): check the "
                        f"file's permissions and the free space")
    assert (folder / "token.json").read_bytes() == before


def test_MoreNewMailThanOneRunCanList_FailsThatChannelSayingSo(gw, monkeypatch):
    """Past GMAIL_LIST_MAX_PAGES pages the oldest new mail cannot be known, so the channel fails rather than guess."""
    monkeypatch.setattr(gw.screen, "GMAIL_LIST_PAGE", 2)
    monkeypatch.setattr(gw.screen, "GMAIL_LIST_MAX_PAGES", 3)
    gw.plant_gmail()
    two_channels(gw)
    gw.gmail.box(GMAIL_BOX).extend(gmsg(f"g{i}", f"2026-10-18T08:0{i}:00Z") for i in range(2, 9))
    sentence, _ = gmail_failed(gw)
    assert sentence == (f"{GMAIL_BOX} has more than 6 new Inbox messages, more than screen.py lists in one run: archive "
                        f"the oldest of them in Gmail")

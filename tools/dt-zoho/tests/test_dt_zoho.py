"""dt-zoho driven through its real command line, with only the network replaced (see fakes.py)."""

import json
import time

import pytest
from cc_shared.axi_output import parse_list

from fakes import INBOX, SUB, SUB2, Message
from src import auth, config, mail


def _message(env, **kwargs) -> Message:
    m = Message(**kwargs)
    env.zoho.messages.append(m)
    return m


# --------------------------------------------------------------------------------------------- sign-in


@pytest.mark.parametrize("variable", [config.CLIENT_ID_ENV, config.CLIENT_SECRET_ENV, config.REFRESH_TOKEN_ENV])
def test_command_KeyVariableMissing_NamesTheCcSecretsCommand(env, monkeypatch, variable):
    monkeypatch.delenv(variable)
    monkeypatch.setattr("sys.argv", ["dt-zoho", "folders"])
    result = env.run("folders")
    assert result.exit_code == 1
    assert variable in result.output
    assert "cc-secrets run zoho-api-client-secret --with zoho-api-client-id --with zoho-api-refresh-token" \
           " -- dt-zoho folders" in result.output
    assert env.zoho.token_requests == 0 and env.zoho.calls == []


def test_command_NoDataCentre_SaysHowToSetIt(bare):
    result = bare.run("folders")
    assert result.exit_code == 1 and "dt-zoho settings set --data-centre" in result.output
    assert bare.zoho.token_requests == 0


def test_settingsSet_UnknownDataCentre_IsUsageError(bare):
    assert bare.run("settings", "set", "--data-centre", "mars").exit_code == 2


def test_settingsShow_ShowsTheServersAndNoSecret(env):
    data = json.loads(env.ok("settings", "show", "--json").stdout)
    assert data["mail"] == "https://mail.zohocloud.ca" and data["sign_in"] == "https://accounts.zohocloud.ca"
    stored = (env.folder / "config.json").read_text(encoding="utf-8")
    assert "secret" not in stored and "refresh" not in stored


def test_commands_ReuseOneAccessTokenAcrossRuns(env):
    for _ in range(4):
        env.ok("folders")
    assert env.zoho.token_requests == 1
    assert {c[3] for c in env.zoho.calls} == {"Zoho-oauthtoken access-1"}


def test_tokenFile_HoldsNoTokenInPlainText(env):
    env.ok("folders")
    blob = auth.cache_path().read_bytes()
    assert b"access-1" not in blob and b"secret" not in blob and b"refresh" not in blob


def test_tokenFile_OtherSecret_CannotOpenItAndAsksForANewToken(env, monkeypatch):
    env.ok("folders")
    env.zoho.client_secret = "rotated"
    monkeypatch.setenv(config.CLIENT_SECRET_ENV, "rotated")
    env.ok("folders")
    assert env.zoho.token_requests == 2


def test_tokenFile_OtherDataCentre_DoesNotOpen(env):
    env.ok("folders")
    creds = config.credentials()
    blob = auth.cache_path().read_bytes()
    assert auth.unseal(creds, config.DATA_CENTRES["ca"], blob) is not None
    assert auth.unseal(creds, config.DATA_CENTRES["com"], blob) is None


def test_token_NearExpiry_IsReplaced(env):
    creds = config.credentials()
    centre = config.DATA_CENTRES["ca"]
    env.zoho.valid_tokens.add("old")
    stale = auth.Token("old", time.time() + auth.REUSE_MARGIN_SECONDS - 10, "new")
    auth.cache_path().parent.mkdir(parents=True, exist_ok=True)
    auth.cache_path().write_bytes(auth.seal(creds, centre, stale))
    env.ok("folders")
    assert env.zoho.token_requests == 1
    assert env.zoho.calls[-1][3] == "Zoho-oauthtoken access-1"


def test_token_RevokedBeforeItsHour_IsReplacedOnceAndTheCommandWorks(env):
    env.ok("folders")
    env.zoho.revoke_all()
    env.ok("folders")
    assert env.zoho.token_requests == 2


def test_token_NewTokenRefused_FailsWithoutRetryingForever(env):
    env.ok("folders")
    env.zoho.revoke_all()
    env.zoho.token_limit = 1
    result = env.run("folders")
    assert result.exit_code == 1 and "too many" in result.output and "Wait ten minutes" in result.output
    assert env.zoho.token_requests == 2


def test_token_RateLimited_SaysWaitAndPrintsNoSecret(env):
    env.zoho.token_limit = 0
    result = env.run("folders")
    assert result.exit_code == 1 and "Wait ten minutes" in result.output
    assert "secret" not in result.output and "refresh" not in result.output


def test_token_KeyRevoked_SaysSoWithoutTheKey(env, monkeypatch):
    monkeypatch.setenv(config.REFRESH_TOKEN_ENV, "wrong-refresh")
    result = env.run("folders")
    assert result.exit_code == 1 and "invalid_code" in result.output and "revoked" in result.output
    assert "wrong-refresh" not in result.output and "secret" not in result.output


def test_signinCheck_ReportsTheSourceOfTheToken(env):
    first = json.loads(env.ok("signin", "check", "--json").stdout)
    second = json.loads(env.ok("signin", "check", "--json").stdout)
    assert first["token"] == "new" and second["token"] == "cache"
    assert first["default_account"] == "soren@devthrottle.com"
    assert "access-1" not in json.dumps(first)


def test_signinForget_DeletesTheTokenFile(env):
    env.ok("folders")
    assert json.loads(env.ok("signin", "forget", "--json").stdout)["forgotten"] is True
    env.ok("folders")
    assert env.zoho.token_requests == 2


def test_redirect_IsRefusedAndNeverFollowed(env):
    env.zoho.redirect_next = True
    result = env.run("folders")
    assert result.exit_code == 1 and "redirect" in result.output and "elsewhere.example" in result.output
    assert all("elsewhere" not in c[1] for c in env.zoho.calls)


def test_zohoError_IsOneSentenceWithZohosCode(env):
    result = env.run("read", "999", "--folder", "Inbox")
    assert result.exit_code == 1
    assert "INVALID_MESSAGE" in result.output and "Traceback" not in result.output


def test_unknownOption_IsUsageError(env):
    assert env.run("list", "--no-such-flag").exit_code == 2


# --------------------------------------------------------------------------------------------- accounts, folders


def test_accounts_ListsEveryAddress(env):
    data = json.loads(env.ok("accounts", "--json").stdout)
    assert data["count"] == 1
    assert data["accounts"][0]["addresses"] == ["info@example.com", "soren@devthrottle.com"]


def test_account_ByAliasAddress_PicksThatAccount(env):
    data = json.loads(env.ok("folders", "--account", "INFO@example.com", "--json").stdout)
    assert data["account"] == "soren@devthrottle.com"


def test_account_UnknownAddress_SaysWhichExist(env):
    result = env.run("folders", "--account", "nobody@example.com")
    assert result.exit_code == 1 and "soren@devthrottle.com" in result.output


def test_folders_ShowsNestedPathsAndImapView(env):
    _, rows = parse_list(env.ok("folders").stdout, "folders")
    assert {"id": SUB, "path": "/Inbox/Reports", "type": "Inbox", "imap_view": "true"} in rows


def test_list_FolderNameSharedByTwo_AsksForThePath(env):
    result = env.run("list", "--folder", "Reports")
    assert result.exit_code == 2 and "/Inbox/Reports" in result.output and "/Reports" in result.output


@pytest.mark.parametrize("given", ["/Inbox/Reports", "inbox/reports", SUB])
def test_list_FolderByPathOrId(env, given):
    _message(env, id="1", folder=SUB, subject="nested")
    _message(env, id="2", folder=SUB2, subject="top")
    data = json.loads(env.ok("list", "--folder", given, "--json").stdout)
    assert [m["subject"] for m in data["messages"]] == ["nested"]


# --------------------------------------------------------------------------------------------- listing


def test_list_DefaultsToInboxAndCleansZohosFields(env):
    _message(env, id="1794144263000002500", folder=INBOX, subject="Caf&eacute; &amp; tea", read=True)
    _message(env, id="9", folder=SUB, subject="elsewhere")
    data = json.loads(env.ok("list", "--json").stdout)
    assert data["folder"] == "/Inbox" and data["count"] == 1
    record = data["messages"][0]
    assert record["id"] == "1794144263000002500"
    assert record["to"] == "<soren@devthrottle.com>" and record["cc"] == ""
    assert record["subject"] == "Café & tea" and record["read"] is True
    assert record["from"] == "Alice <alice@example.org>"
    assert record["received"].startswith("2026-")


def test_list_Unread_AsksZohoForUnreadOnly(env):
    _message(env, id="1", folder=INBOX, subject="a", read=True)
    _message(env, id="2", folder=INBOX, subject="b", read=False)
    data = json.loads(env.ok("list", "--unread", "--json").stdout)
    assert [m["id"] for m in data["messages"]] == ["2"]
    assert env.zoho.calls[-1][2]["status"] == "unread"


def test_list_Flagged_FiltersAndSaysHowManyWereLeftOut(env):
    _message(env, id="1", folder=INBOX, subject="a", flag="important")
    _message(env, id="2", folder=INBOX, subject="b")
    text = env.ok("list", "--flagged").stdout
    _, rows = parse_list(text, "messages")
    assert [r["id"] for r in rows] == ["1"]
    assert "1 of the 2 messages fetched were left out" in text
    data = json.loads(env.ok("list", "--flagged", "--json").stdout)
    assert data["count"] == 1 and data["fetched"] == 2


def test_list_Label_SearchesEveryFolderByLabelId(env):
    _message(env, id="1", folder=SUB, subject="tagged", labels=["636823000000007002"])
    _message(env, id="2", folder=INBOX, subject="plain")
    data = json.loads(env.ok("list", "--label", "lwtbi", "--json").stdout)
    assert [m["id"] for m in data["messages"]] == ["1"]
    assert "folderId" not in env.zoho.calls[-1][2] and env.zoho.calls[-1][2]["labelid"] == "636823000000007002"


def test_list_Thread_ListsTheThread(env):
    _message(env, id="10", folder=INBOX, subject="first")
    _message(env, id="11", folder=INBOX, subject="Re: first", thread="10")
    _message(env, id="12", folder=INBOX, subject="other")
    data = json.loads(env.ok("list", "--thread", "10", "--json").stdout)
    assert sorted(m["id"] for m in data["messages"]) == ["10", "11"]


def test_list_FullPage_PointsToTheNextPage(env):
    for i in range(3):
        _message(env, id=str(i), folder=INBOX, subject=f"m{i}")
    text = env.ok("list", "--count", "2").stdout
    assert 'dt-zoho list --folder "/Inbox" --count 2 --start 3' in text
    data = json.loads(env.ok("list", "--count", "2", "--start", "3", "--json").stdout)
    assert [m["id"] for m in data["messages"]] == ["2"] and data["next_start"] is None


def test_list_Empty_SaysCountZero(env):
    assert "count: 0" in env.ok("list").stdout


def test_list_IdsAreNeverCutShort(env):
    _message(env, id="1794144263000002500", folder=INBOX, subject="x" * 300)
    _, rows = parse_list(env.ok("list").stdout, "messages")
    assert rows[0]["id"] == "1794144263000002500" and rows[0]["subject"] == "x" * 300


def test_list_BadFields_IsUsageError(env):
    assert env.run("list", "--fields", "id,nope").exit_code == 2


# --------------------------------------------------------------------------------------------- search


@pytest.mark.parametrize("term,option", [("is:unread", "--unread"), ("is:flagged", "--flagged"),
                                         ("is:read", "--read")])
def test_search_TermZohoIgnores_IsRefusedBeforeAnyCall(env, term, option):
    result = env.run("search", f"subject:invoice::{term}")
    assert result.exit_code == 2 and option in result.output
    assert env.zoho.calls == [] and env.zoho.token_requests == 0


def test_search_UnreadOption_FiltersTheHits(env):
    _message(env, id="1", folder=INBOX, subject="invoice one", read=True)
    _message(env, id="2", folder=SUB, subject="invoice two")
    data = json.loads(env.ok("search", "subject:invoice", "--unread", "--json").stdout)
    assert [m["id"] for m in data["messages"]] == ["2"] and data["fetched"] == 2
    assert env.zoho.calls[-1][2]["searchKey"] == "subject:invoice"


# --------------------------------------------------------------------------------------------- reading


def test_read_ShowsTheBodyAsText(env):
    _message(env, id="5", folder=INBOX, subject="Hi", html="<div><zspan>Line one\r</zspan><br>Line &amp; two</div>")
    text = env.ok("read", "5").stdout
    assert "subject: Hi" in text and "body:\nLine one\nLine & two" in text


def test_read_Json_CarriesTheHtmlToo(env):
    _message(env, id="5", folder=SUB, subject="Hi", html="<p>A</p>")
    data = json.loads(env.ok("read", "5", "--folder", "/Inbox/Reports", "--json").stdout)
    assert data["body"] == "A" and data["body_html"] == "<p>A</p>" and data["folder"] == "/Inbox/Reports"


def test_read_Headers_AddsEveryHeaderLine(env):
    _message(env, id="5", folder=INBOX, subject="Hi")
    assert "headers:\nFrom: alice@example.org" in env.ok("read", "5", "--headers").stdout


def test_read_ListsAttachmentsAndPointsToDownload(env):
    _message(env, id="5", folder=INBOX, subject="Hi", attachments={"77": ("data.bin", b"\0\1")})
    text = env.ok("read", "5").stdout
    assert "data.bin (2 bytes, id 77)" in text and 'dt-zoho download 5 --folder "/Inbox"' in text


def test_readRaw_Save_WritesTheMimeAndNeverOverwrites(env, tmp_path):
    _message(env, id="5", folder=INBOX, subject="Hi")
    target = tmp_path / "m.eml"
    env.ok("read", "5", "--raw", "--save", str(target))
    assert b"Subject: Hi" in target.read_bytes()
    assert env.run("read", "5", "--raw", "--save", str(target)).exit_code == 2


def test_read_SaveWithoutRaw_IsUsageError(env, tmp_path):
    assert env.run("read", "5", "--save", str(tmp_path / "x")).exit_code == 2


def test_labels_ListsThem(env):
    _, rows = parse_list(env.ok("labels").stdout, "labels")
    assert rows == [{"id": "636823000000007002", "name": "LWTBI", "color": "#dd902f"}]


# --------------------------------------------------------------------------------------------- attachments


def test_download_WritesExactBytes(env, tmp_path):
    binary = bytes(range(256)) * 8
    _message(env, id="5", folder=INBOX, subject="Hi", attachments={"1": ("data.bin", binary), "2": ("notes.txt",
                                                                                                    b"a\r\nb\r\n")})
    text = env.ok("download", "5", "--out-dir", str(tmp_path)).stdout
    assert (tmp_path / "data.bin").read_bytes() == binary
    assert (tmp_path / "notes.txt").read_bytes() == b"a\r\nb\r\n"
    assert "Zoho rewrites LF to CRLF" in text


def test_download_OneByName_AndNeverOverwrites(env, tmp_path):
    _message(env, id="5", folder=INBOX, subject="Hi", attachments={"1": ("a.bin", b"1"), "2": ("b.bin", b"2")})
    env.ok("download", "5", "--attachment", "b.bin", "--out-dir", str(tmp_path))
    assert not (tmp_path / "a.bin").exists() and (tmp_path / "b.bin").read_bytes() == b"2"
    result = env.run("download", "5", "--attachment", "2", "--out-dir", str(tmp_path))
    assert result.exit_code == 2 and "already exist" in result.output


def test_download_UnknownAttachment_NamesWhatExists(env, tmp_path):
    _message(env, id="5", folder=INBOX, subject="Hi", attachments={"1": ("a.bin", b"1")})
    result = env.run("download", "5", "--attachment", "zzz", "--out-dir", str(tmp_path))
    assert result.exit_code == 2 and "a.bin" in result.output


def test_attachments_ListsThem(env):
    _message(env, id="5", folder=INBOX, subject="Hi", attachments={"1": ("a.bin", b"123")})
    data = json.loads(env.ok("attachments", "5", "--json").stdout)
    assert data["attachments"] == [{"id": "1", "name": "a.bin", "size": 3}]


# --------------------------------------------------------------------------------------------- pieces


def test_htmlToText_DropsStyleAndKeepsLines():
    markup = "<html><head><style>p{}</style></head><body><p>One</p><p>Two<br/>Three</p></body></html>"
    assert mail.html_to_text(markup) == "One\n\nTwo\nThree"


def test_tokenRepr_NeverShowsTheToken():
    assert "abc" not in repr(auth.Token("abc", 1.0, "new"))


def test_list_FullPageWithLabel_NextPageHintKeepsTheLabel(env):
    for i in range(3):
        _message(env, id=str(i), folder=INBOX, subject=f"m{i}", labels=["636823000000007002"], read=False)
    text = env.ok("list", "--label", "LWTBI", "--unread", "--count", "2").stdout
    assert 'more may exist: dt-zoho list --label "LWTBI" --unread --count 2 --start 3' in text


def test_read_EmptyCc_IsNotPrintedAsABlankLine(env):
    _message(env, id="5", folder=INBOX, subject="Hi")
    assert "\ncc:" not in env.ok("read", "5").stdout

"""dt-email driven through its real command line, with only the network replaced (see fakes.py)."""

import json

import pytest
from cc_shared.axi_output import parse_list

from conftest import ME, PASSWORD_ENV, make_message
from src import config, imap_client


# --------------------------------------------------------------------------------------------- accounts


def test_account_add_StoresNoPassword(env, tmp_path):
    text = (tmp_path / "dt-email" / "config.json").read_text(encoding="utf-8")
    data = json.loads(text)
    assert data["accounts"]["test"]["secret_entry"] == "test-entry"
    assert data["accounts"]["test"]["password_env"] == PASSWORD_ENV
    assert "pw" not in json.dumps(data)


def test_command_PasswordVariableMissing_NamesTheCcSecretsCommand(env, monkeypatch):
    monkeypatch.delenv(PASSWORD_ENV)
    result = env.run("list")
    assert result.exit_code == 1
    assert f"cc-secrets run test-entry -- dt-email" in result.output
    assert env.imap.connections == 0


def test_account_check_SignsInToBoth(env):
    result = env.ok("account", "check", "--json")
    data = json.loads(result.stdout)
    assert data["has_move"] and data["has_uidplus"]
    assert env.smtp.sent == []


def test_unknownOption_IsUsageError(env):
    assert env.run("list", "--no-such-flag").exit_code == 2


# --------------------------------------------------------------------------------------------- test mode


def _allow(env, *addresses):
    env.ok("allowed-recipients", "set", *addresses)


@pytest.mark.parametrize("option", ["--to", "--cc", "--bcc"])
def test_send_RecipientNotAllowed_RefusedBeforeConnecting(env, option):
    _allow(env, "ok@example.com")
    args = ["send", "--to", "ok@example.com", "--subject", "s", "--body", "b"]
    args += [option, "stranger@elsewhere.org"] if option != "--to" else []
    if option == "--to":
        args[2] = "stranger@elsewhere.org"
    result = env.run(*args)
    assert result.exit_code == 1
    assert "stranger@elsewhere.org" in result.output and "Nothing was sent" in result.output
    assert env.smtp.connections == 0 and env.smtp.sent == []


def test_send_AllRecipientsAllowed_Sends(env):
    _allow(env, "ok@example.com", "OK2@example.com")
    env.ok("send", "--to", "ok@example.com", "--cc", "ok2@example.com", "--subject", "s", "--body", "b")
    assert len(env.smtp.sent) == 1


def test_allowedRecipients_ClearTurnsTestModeOff(env):
    _allow(env, "ok@example.com")
    env.ok("allowed-recipients", "clear")
    assert "off" in env.ok("allowed-recipients", "show").stdout
    env.ok("send", "--to", "anyone@example.net", "--subject", "s", "--body", "b")


def test_replyAll_OriginalCcNotAllowed_Refused(env):
    uid = env.imap.add("INBOX", make_message(sender="ok@example.com", to=f"{ME}, other@stranger.org"))
    _allow(env, "ok@example.com")
    result = env.run("reply", str(uid), "--all", "--body", "x")
    assert result.exit_code == 1 and "other@stranger.org" in result.output
    assert env.smtp.sent == []


def test_forward_NotAllowed_Refused(env):
    uid = env.imap.add("INBOX", make_message())
    _allow(env, "ok@example.com")
    assert env.run("forward", str(uid), "--to", "x@stranger.org").exit_code == 1
    assert env.smtp.sent == []


def test_draftSend_NotAllowed_RefusedAndDraftKept(env):
    _allow(env, "ok@example.com")
    saved = json.loads(env.ok("draft", "save", "--to", "x@stranger.org", "--subject", "d", "--body", "b",
                              "--json").stdout)
    assert env.run("draft", "send", str(saved["saved_draft_uid"])).exit_code == 1
    assert env.smtp.sent == []
    assert saved["saved_draft_uid"] in env.imap.folders["Drafts"].messages


# --------------------------------------------------------------------------------------------- sending


def test_send_ToCcBcc_BccDeliveredButNeverTransmitted(env):
    env.ok("send", "--to", "a@example.com", "--cc", "b@example.com", "--bcc", "c@example.com",
           "--subject", "Plain", "--body", "Hello")
    sent = env.smtp.sent[0]
    assert sorted(sent["to"]) == ["a@example.com", "b@example.com", "c@example.com"]
    assert b"c@example.com" not in sent["data"]
    assert sent["message"]["Cc"] == "b@example.com"
    assert sent["message"]["Message-ID"].endswith("@example.com>")


def test_send_DoesNotSaveToSentByDefault(env):
    env.ok("send", "--to", "a@example.com", "--subject", "s", "--body", "b")
    assert env.imap.folders["Sent"].messages == {}
    assert env.imap.connections == 0


def test_send_SaveSentAccount_SavesOneCopyWithoutBcc(env):
    env.ok("account", "add", "saver", "--email", ME, "--imap-host", "i", "--smtp-host", "s", "--secret-entry", "e",
           "--password-env", PASSWORD_ENV, "--save-sent", "--default")
    env.ok("send", "--to", "a@example.com", "--bcc", "hidden@example.com", "--subject", "s", "--body", "b")
    copies = list(env.imap.folders["Sent"].messages.values())
    assert len(copies) == 1 and b"hidden@example.com" not in copies[0].raw


def test_send_HtmlAndAttachments(env, tmp_path):
    html = tmp_path / "b.html"
    html.write_text("<p>Hi <b>there</b></p>", encoding="utf-8")
    data = tmp_path / "data.bin"
    data.write_bytes(bytes(range(256)) * 8)
    env.ok("send", "--to", "a@example.com", "--subject", "s", "--body", "plain", "--html-file", str(html),
           "--attach", str(data))
    msg = env.smtp.sent[0]["message"]
    types = [p.get_content_type() for p in msg.walk()]
    assert "text/plain" in types and "text/html" in types
    att = next(msg.iter_attachments())
    assert att.get_filename() == "data.bin" and att.get_payload(decode=True) == bytes(range(256)) * 8


def test_send_FromAlias_UsesAliasAsSender(env):
    env.ok("send", "--from", "alias@example.com", "--to", "a@example.com", "--subject", "s", "--body", "b")
    assert env.smtp.sent[0]["from"] == "alias@example.com"
    assert env.smtp.sent[0]["message"]["From"] == "alias@example.com"


def test_send_FromNotAllowedByServer_ClearError(env):
    result = env.run("send", "--from", "nobody@example.com", "--to", "a@example.com", "--subject", "s", "--body", "b")
    assert result.exit_code == 1
    assert "refused the message from nobody@example.com: 550" in result.output and "Nothing was sent" in result.output


def test_send_NoRecipients_UsageError(env):
    assert env.run("send", "--subject", "s", "--body", "b").exit_code == 2


# --------------------------------------------------------------------------------------------- reading


def test_list_NewestFirstWithCountAndTotal(env):
    for i in range(5):
        env.imap.add("INBOX", make_message(subject=f"Message {i}, with comma"))
    result = env.ok("list", "--count", "3")
    assert "count: 3 of 5 total" in result.stdout
    fields, rows = parse_list(result.stdout.split("help[")[0].split("\n", 1)[1].strip(), "messages")
    assert fields == ["uid", "date", "from", "subject"]
    assert [r["uid"] for r in rows] == ["5", "4", "3"]
    assert rows[0]["subject"] == "Message 4, with comma"


def test_list_EmptyFolder_SaysCountZero(env):
    assert "count: 0" in env.ok("list").stdout


def test_list_Json_KeepsEveryField(env):
    env.imap.add("INBOX", make_message(), flags=["\\Seen"])
    data = json.loads(env.ok("list", "--json").stdout)
    assert data["count"] == 1 and data["total"] == 1
    assert set(data["messages"][0]) == {"uid", "date", "received", "from", "to", "cc", "subject", "message_id",
                                       "flags", "unread", "flagged", "size"}
    assert data["uidvalidity"] == 1
    assert data["messages"][0]["unread"] is False


def test_list_UnknownField_IsUsageError(env):
    env.imap.add("INBOX", make_message())
    assert env.run("list", "--fields", "uid,nonsense").exit_code == 2


def test_read_ShowsBodyAndAttachmentsWithoutMarkingRead(env):
    uid = env.imap.add("INBOX", make_message(body="The body text.",
                                            attachments=[("notes.txt", b"notes\n", "text/plain")]))
    result = env.ok("read", str(uid))
    assert "The body text." in result.stdout
    assert "notes.txt" in result.stdout
    assert env.imap.folders["INBOX"].messages[uid].flags == []


def test_read_LongBody_TruncatedWithHint(env):
    uid = env.imap.add("INBOX", make_message(body="x" * 5000))
    assert "truncated, 5001 chars total - use --full" in env.ok("read", str(uid)).stdout
    assert "x" * 5000 in env.ok("read", str(uid), "--full").stdout


def test_read_MissingUid_ClearError(env):
    result = env.run("read", "99")
    assert result.exit_code == 1 and "no message with UID 99" in result.output


def test_search_SendsCriteriaAndFindsMatches(env):
    env.imap.add("INBOX", make_message(subject="Invoice 42"))
    env.imap.add("INBOX", make_message(subject="Lunch"))
    data = json.loads(env.ok("search", "--subject", "invoice", "--since", "2026-11-01", "--json").stdout)
    assert [m["subject"] for m in data["messages"]] == ["Invoice 42"]
    search = [c for c in env.imap.commands if c[0] == "SEARCH"][-1]
    assert search[2:] == ("SUBJECT", '"invoice"', "SINCE", "01-Nov-2026")


def test_search_NothingToSearchFor_UsageError(env):
    assert env.run("search").exit_code == 2


def test_search_NonAscii_Refused(env):
    assert env.run("search", "--subject", "café").exit_code == 2


def test_download_BytesIdenticalAndNoSilentOverwrite(env, tmp_path):
    blob = bytes(range(256)) * 8
    uid = env.imap.add("INBOX", make_message(attachments=[("data.bin", blob, "application/octet-stream"),
                                                         ("notes.txt", b"n\n", "text/plain")]))
    out = tmp_path / "out"
    env.ok("download", str(uid), "--out", str(out))
    assert (out / "data.bin").read_bytes() == blob
    again = env.run("download", str(uid), "--out", str(out))
    assert again.exit_code == 1 and "already exist" in again.output
    env.ok("download", str(uid), "--out", str(out), "--overwrite", "--index", "2")


# --------------------------------------------------------------------------------------------- threads


def test_reply_KeepsTheThread(env):
    raw = make_message(sender="alice@example.org", message_id="<orig@example.org>", references="<root@example.org>")
    uid = env.imap.add("INBOX", raw)
    env.ok("reply", str(uid), "--body", "Thanks")
    msg = env.smtp.sent[0]["message"]
    assert msg["In-Reply-To"] == "<orig@example.org>"
    assert msg["References"] == "<root@example.org> <orig@example.org>"
    assert msg["Subject"] == "Re: Hello" and env.smtp.sent[0]["to"] == ["alice@example.org"]
    assert "> Hi there." in msg.get_body(("plain",)).get_content()


def test_replyAll_CopiesEveryoneButThisAccount(env):
    uid = env.imap.add("INBOX", make_message(sender="alice@example.org", to=f"{ME}, bob@example.org",
                                            cc="carol@example.org"))
    env.ok("reply", str(uid), "--all", "--body", "All")
    msg = env.smtp.sent[0]["message"]
    assert msg["To"] == "alice@example.org"
    assert msg["Cc"] == "bob@example.org, carol@example.org"


def test_forward_AttachesOriginalAndKeepsReferences(env):
    uid = env.imap.add("INBOX", make_message(message_id="<orig@example.org>"))
    env.ok("forward", str(uid), "--to", "dave@example.org", "--body", "FYI")
    msg = env.smtp.sent[0]["message"]
    assert msg["Subject"] == "Fwd: Hello" and "<orig@example.org>" in msg["References"]
    assert any(p.get_content_type() == "message/rfc822" for p in msg.walk())


# --------------------------------------------------------------------------------------------- drafts


def test_drafts_SaveReplaceSendDelete(env):
    first = json.loads(env.ok("draft", "save", "--to", "a@example.com", "--subject", "v1", "--body", "b",
                              "--json").stdout)["saved_draft_uid"]
    assert "\\Draft" in env.imap.folders["Drafts"].messages[first].flags
    second = json.loads(env.ok("draft", "save", "--to", "a@example.com", "--subject", "v2", "--body", "b",
                               "--replace", str(first), "--json").stdout)["saved_draft_uid"]
    assert list(env.imap.folders["Drafts"].messages) == [second]
    env.ok("draft", "send", str(second))
    assert env.smtp.sent[0]["message"]["Subject"] == "v2"
    assert env.imap.folders["Drafts"].messages == {}
    third = json.loads(env.ok("draft", "save", "--subject", "v3", "--json").stdout)["saved_draft_uid"]
    env.ok("draft", "delete", str(third))
    assert env.imap.folders["Drafts"].messages == {}


# --------------------------------------------------------------------------------------------- flags, moving, deleting


def test_mark_ReadUnreadFlagUnflag_FlagsInParentheses(env):
    uid = env.imap.add("INBOX", make_message())
    stored = env.imap.folders["INBOX"].messages[uid]
    env.ok("mark", str(uid), "--as", "read")
    assert stored.flags == ["\\Seen"]
    env.ok("mark", str(uid), "--as", "flagged")
    env.ok("mark", str(uid), "--as", "unread")
    assert stored.flags == ["\\Flagged"]
    env.ok("mark", str(uid), "--as", "unflagged")
    assert stored.flags == []
    assert all(c[3].startswith("(") for c in env.imap.commands if c[0] == "STORE")


def test_mark_UnknownUid_ChangesNothing(env):
    env.imap.add("INBOX", make_message())
    result = env.run("mark", "1,7", "--as", "read")
    assert result.exit_code == 1 and "UID 7" in result.output
    assert env.imap.folders["INBOX"].messages[1].flags == []


def test_move_And_ArchiveFoundByName(env):
    a = env.imap.add("INBOX", make_message(subject="a"))
    b = env.imap.add("INBOX", make_message(subject="b"))
    env.ok("folder", "create", "Projects")
    env.ok("move", str(a), "--to", "Projects")
    env.ok("archive", str(b))
    assert len(env.imap.folders["Projects"].messages) == 1
    assert len(env.imap.folders["Archive"].messages) == 1
    assert env.imap.folders["INBOX"].messages == {}


@pytest.mark.parametrize("command", [["move", "--to", "Archive"], ["archive"]])
def test_move_ExpectUidValidity_StillTheSame_Moves(env, command):
    uid = env.imap.add("INBOX", make_message())
    env.ok(command[0], str(uid), *command[1:], "--expect-uidvalidity", "1")
    assert env.imap.folders["INBOX"].messages == {} and len(env.imap.folders["Archive"].messages) == 1


@pytest.mark.parametrize("command", [["move", "--to", "Archive"], ["archive"]])
def test_move_ExpectUidValidity_FolderRenumbered_MovesNothing(env, command):
    uid = env.imap.add("INBOX", make_message())
    env.imap.folders["INBOX"].uidvalidity = 2
    result = env.run(command[0], str(uid), *command[1:], "--expect-uidvalidity", "1")
    assert result.exit_code == 1
    assert "the UIDVALIDITY of folder 'INBOX' is 2 now, not 1" in result.output and "Nothing was moved" in result.output
    assert list(env.imap.folders["INBOX"].messages) == [uid] and env.imap.folders["Archive"].messages == {}
    assert not any(c[0] == "MOVE" for c in env.imap.commands)


def test_move_ServerWithoutMove_ClearError(env):
    env.imap.capabilities = ("IMAP4REV1", "UIDPLUS")
    env.imap.add("INBOX", make_message())
    result = env.run("move", "1", "--to", "Archive")
    assert result.exit_code == 1 and "MOVE extension" in result.output


def test_delete_ToTrashThenPermanent_OnlyThatMessage(env):
    keep = env.imap.add("Trash", make_message(subject="already deleted elsewhere"), flags=["\\Deleted"])
    uid = env.imap.add("INBOX", make_message(subject="bye"))
    env.ok("delete", str(uid))
    assert env.imap.folders["INBOX"].messages == {}
    trashed = [u for u, m in env.imap.folders["Trash"].messages.items() if b"bye" in m.raw][0]
    assert env.run("delete", str(trashed), "--folder", "Trash").exit_code == 2
    env.ok("delete", str(trashed), "--folder", "Trash", "--permanent")
    assert list(env.imap.folders["Trash"].messages) == [keep]


def test_delete_PermanentFromInbox(env):
    uid = env.imap.add("INBOX", make_message())
    env.ok("delete", str(uid), "--permanent")
    assert env.imap.folders["INBOX"].messages == {} and env.imap.folders["Trash"].messages == {}


# --------------------------------------------------------------------------------------------- folders


def test_folders_ListsSpecialUseAndCounts(env):
    env.imap.add("INBOX", make_message())
    data = json.loads(env.ok("folders", "--json").stdout)
    by_name = {f["name"]: f for f in data["folders"]}
    assert by_name["Drafts"]["special"] == "drafts" and by_name["Spam"]["special"] == "junk"
    assert by_name["INBOX"]["messages"] == 1 and by_name["INBOX"]["unseen"] == 1
    assert by_name["Archive"]["special"] is None


def test_folder_CreateRenameDelete_ReportsServerKeptItInTrash(env):
    env.ok("folder", "create", "Old")
    env.ok("folder", "rename", "Old", "New")
    result = env.ok("folder", "delete", "New")
    assert "Trash/New" in result.stdout
    env.ok("folder", "delete", "Trash/New")
    assert not any(n.endswith("New") for n in env.imap.folders)


# --------------------------------------------------------------------------------------------- answers recorded from Zoho


def test_parseList_RecordedZohoAnswer():
    lines = [b'(\\HasNoChildren) "/" "INBOX"', b'(\\Noinferiors \\Drafts) "/" "Drafts"',
             b'(\\Noinferiors \\Junk) "/" "Spam"', b'(\\HasNoChildren) "/" "Archive"',
             (b'(\\HasNoChildren) "/" {9}', b'Odd "one"')]
    folders = imap_client.parse_list_response(lines)
    assert [(f.name, f.special) for f in folders] == [("INBOX", None), ("Drafts", "drafts"), ("Spam", "junk"),
                                                      ("Archive", None), ('Odd "one"', None)]


def test_parseFetch_FlagsBeforeUidAsZohoSendsThem():
    data = [(b'3 (FLAGS (\\Recent) UID 3 BODY[HEADER.FIELDS ("FROM")] {25}', b"From: a@example.com\r\n\r\n"), b")",
            (b"4 (UID 4 BODY[] {3}", b"abc"), b" FLAGS (\\Seen))"]
    items = imap_client.parse_fetch_response(data)
    assert [(i.uid, i.flags()) for i in items] == [(3, ["\\Recent"]), (4, ["\\Seen"])]


def test_mailboxNames_ModifiedUtf7RoundTrip():
    assert imap_client.encode_mailbox("Entwürfe & Co") == "Entw&APw-rfe &- Co"
    assert imap_client.decode_mailbox("Entw&APw-rfe &- Co") == "Entwürfe & Co"


def test_config_NormaliseAddress_RefusesTwoAddresses():
    with pytest.raises(Exception):
        config.normalise_address("a@example.com, b@example.com")


def test_replyAll_ToOwnSentMessage_GoesToItsRecipientsNotBackToSelf(env):
    # Found in the live run: replying to all on a message this account sent put the account itself in To.
    uid = env.imap.add("INBOX", make_message(sender=ME, to="bob@example.org", cc="carol@example.org"))
    env.ok("reply", str(uid), "--all", "--body", "Again")
    msg = env.smtp.sent[0]["message"]
    assert msg["To"] == "bob@example.org" and msg["Cc"] == "carol@example.org"
    assert ME not in env.smtp.sent[0]["to"]


def test_reply_ToOwnSentMessage_GoesToItsRecipient(env):
    uid = env.imap.add("INBOX", make_message(sender=ME, to="bob@example.org", cc="carol@example.org"))
    env.ok("reply", str(uid), "--body", "Again")
    assert env.smtp.sent[0]["to"] == ["bob@example.org"]


def test_folder_DeleteNested_ReportsTheNameTheServerGaveItInTrash(env):
    env.ok("folder", "create", "Parent")
    env.ok("folder", "create", "Parent/Child")
    data = json.loads(env.ok("folder", "delete", "Parent/Child", "--json").stdout)
    assert data["server_moved_it_to"] == "Trash/Parent_Child"


# --------------------------------------------------------------------------------------------- review findings


def test_move_ServerAdvertisesMoveOnlyAfterLogin_Works(env):
    env.imap.pre_login_capabilities = ("IMAP4REV1", "IDLE")
    env.imap.add("INBOX", make_message())
    env.ok("move", "1", "--to", "Archive")
    assert len(env.imap.folders["Archive"].messages) == 1
    assert json.loads(env.ok("account", "check", "--json").stdout)["has_move"] is True


def test_download_TwoAttachmentsWithOneName_BothKept(env, tmp_path):
    uid = env.imap.add("INBOX", make_message(attachments=[("report.pdf", b"aaaa", "application/pdf"),
                                                         ("report.pdf", b"bbbbbbbb", "application/pdf")]))
    out = tmp_path / "out"
    data = json.loads(env.ok("download", str(uid), "--out", str(out), "--json").stdout)
    assert sorted(p.name for p in out.iterdir()) == ["report-2.pdf", "report.pdf"]
    assert (out / "report.pdf").read_bytes() == b"aaaa" and (out / "report-2.pdf").read_bytes() == b"bbbbbbbb"
    assert len({s["path"] for s in data["saved"]}) == 2


def test_send_MissingBodyFile_UsageErrorNamingThePath(env, tmp_path):
    missing = tmp_path / "nothere.txt"
    result = env.run("send", "--to", "a@example.com", "--subject", "s", "--body-file", str(missing))
    assert result.exit_code == 2
    assert "nothere.txt" in result.output and "mail server" not in result.output
    assert env.smtp.connections == 0


def test_replyAll_WithReplyTo_StillCopiesTheSender(env):
    raw = make_message(sender="Alice <alice@example.org>", reply_to="list@example.org")
    uid = env.imap.add("INBOX", raw)
    env.ok("reply", str(uid), "--all", "--body", "x")
    msg = env.smtp.sent[0]["message"]
    assert msg["To"] == "list@example.org" and msg["Cc"] == "alice@example.org"


def test_reply_WithReplyTo_GoesOnlyToReplyTo(env):
    raw = make_message(sender="Alice <alice@example.org>", reply_to="list@example.org")
    uid = env.imap.add("INBOX", raw)
    env.ok("reply", str(uid), "--body", "x")
    assert env.smtp.sent[0]["to"] == ["list@example.org"]


def test_search_ControlCharacters_Refused(env):
    result = env.run("search", "--subject", "x\r\nA1 UID STORE 1:* +FLAGS (\\Deleted)")
    assert result.exit_code == 2
    assert not [c for c in env.imap.commands if c[0] == "SEARCH"]


def test_send_ServerClosesAfterRefusing_RealErrorShown(env):
    result = env.run("send", "--from", "nobody@example.com", "--to", "a@example.com", "--subject", "s", "--body", "b")
    assert result.exit_code == 1
    assert "550" in result.output and "closed" not in result.output


def test_list_Unread_TotalIsTheUnreadTotal(env):
    for i in range(4):
        env.imap.add("INBOX", make_message(subject=f"m{i}"), flags=["\\Seen"] if i else [])
    data = json.loads(env.ok("list", "--unread", "--json").stdout)
    assert data["count"] == 1 and data["total"] == 1
    assert "count: 1" in env.ok("list", "--unread").stdout and "of 4" not in env.ok("list", "--unread").stdout


def test_accountCheck_CapabilitiesListedOnce(env):
    caps = json.loads(env.ok("account", "check", "--json").stdout)["imap_capabilities"]
    assert len(caps) == len(set(caps)) and "MOVE" in caps


# --------------------------------------------------------------------------------------------- list for a reader
#
# What the Mail Desk's IMAP reader asks for: the messages after its cursor, oldest first, with a preview, the folder's
# UIDVALIDITY beside them, and nothing marked read.


def _json(env, *args):
    return json.loads(env.ok("list", *args, "--json").stdout)


def test_list_AfterUid_OnlyLaterMessages_EvenWhenNoneIsLater(env):
    for i in range(4):
        env.imap.add("INBOX", make_message(subject=f"m{i}"))
    assert [m["uid"] for m in _json(env, "--after-uid", "2")["messages"]] == [4, 3]
    # "5:*" matches the newest message on a real server too; it must not come back as new.
    empty = _json(env, "--after-uid", "4")
    assert empty["count"] == 0 and empty["total"] == 0 and empty["messages"] == []


def test_list_Oldest_TakesTheOldestCountOldestFirst(env):
    for i in range(5):
        env.imap.add("INBOX", make_message(subject=f"m{i}"))
    data = _json(env, "--after-uid", "1", "--oldest", "--count", "2")
    assert [m["uid"] for m in data["messages"]] == [2, 3]
    assert data["total"] == 4


def test_list_Received_IsTheServersArrivalTimeInUtc(env):
    env.imap.add("INBOX", make_message(), internal_date="07-Nov-2026 17:31:20 -0500")
    assert _json(env)["messages"][0]["received"] == "2026-11-07T22:31:20Z"


def test_list_UidValidity_IsTheFolders(env):
    env.imap.folders["INBOX"].uidvalidity = 1762540000
    assert _json(env)["uidvalidity"] == 1762540000


def test_list_Preview_FirstTextAndAttachmentCount_WithoutMarkingRead(env):
    uid = env.imap.add("INBOX", make_message(body="Hello   there," + chr(10) * 2 + "second line. " + "x" * 400,
                                            attachments=[("a.txt", b"a", "text/plain")]))
    message = _json(env, "--preview")["messages"][0]
    assert message["preview"].startswith("Hello there, second line. x")
    assert len(message["preview"]) == 200
    assert message["attachments"] == 1
    assert env.imap.folders["INBOX"].messages[uid].flags == []


def test_list_Preview_ALargeAttachment_IsNeverDownloaded_ButIsCounted(env):
    """The preview reads only the first 64 KB: a 2 MB attachment is never downloaded, and still counted."""
    env.imap.add("INBOX", make_message(body="Short note.", attachments=[("big.bin", b"z" * 2_000_000, "application/octet-stream")]))
    message = _json(env, "--preview")["messages"][0]
    assert message["preview"] == "Short note." and message["attachments"] == 1
    fetches = [c for c in env.imap.commands if c[0] == "FETCH" and "BODY.PEEK[]" in c[2]]
    assert fetches and all("BODY.PEEK[]<0.65536>" in c[2] for c in fetches)


def test_list_Preview_HtmlOnly_TagsAndEntitiesTakenOut(env):
    from email.message import EmailMessage
    msg = EmailMessage()
    msg["From"], msg["To"], msg["Subject"] = "a@example.org", ME, "html"
    msg.set_content("<html><style>p{color:red}</style><p>Caf&eacute; <b>menu</b></p></html>", subtype="html")
    env.imap.add("INBOX", msg.as_bytes())
    assert _json(env, "--preview")["messages"][0]["preview"] == "Caf" + chr(0xE9) + " menu"


def test_list_Preview_CalendarOnly_IsEmpty(env):
    from email.message import EmailMessage
    msg = EmailMessage()
    msg["From"], msg["To"], msg["Subject"] = "a@example.org", ME, "Invitation"
    msg.set_content(chr(10).join(["BEGIN:VCALENDAR", "END:VCALENDAR", ""]), subtype="calendar")
    env.imap.add("INBOX", msg.as_bytes())
    assert _json(env, "--preview")["messages"][0]["preview"] == ""


def test_list_WithoutPreview_HasNoPreviewField(env):
    env.imap.add("INBOX", make_message())
    assert "preview" not in _json(env)["messages"][0]


def test_list_Since_IsSentToTheServer(env):
    env.imap.add("INBOX", make_message())
    env.ok("list", "--since", "2026-10-09")
    searches = [c for c in env.imap.commands if c[0] == "SEARCH"]
    assert searches[-1][2:] == ("SINCE", "09-Oct-2026")


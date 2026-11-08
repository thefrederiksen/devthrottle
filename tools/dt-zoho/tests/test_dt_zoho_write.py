"""dt-zoho's writing commands driven through the real command line, with only the network replaced (fakes.py)."""

import json

import pytest

from fakes import ARCHIVE, INBOX, SPAM, SUB, TRASH, Message
from src import config

ME = "soren@devthrottle.com"
OTHER = "email@devthrottle.com"


def _message(env, **kwargs) -> Message:
    m = Message(**kwargs)
    env.zoho.messages.append(m)
    return m


def _only_reads(env) -> bool:
    """True when no call that changes anything reached Zoho."""
    return all(c[0] == "GET" for c in env.zoho.calls) and not env.zoho.uploads


# --------------------------------------------------------------------------------------------- test mode


def test_allowedRecipients_SetShowClear_KeptInTheSettingsFile(env):
    env.ok("allowed-recipients", "set", "Soren <SOREN@devthrottle.com>", OTHER)
    assert json.loads(env.ok("allowed-recipients", "show", "--json").stdout)["allowed_recipients"] == [OTHER, ME]
    stored = json.loads((env.folder / "config.json").read_text(encoding="utf-8"))
    assert stored == {"data_centre": "ca", "allowed_recipients": [OTHER, ME]}
    env.ok("allowed-recipients", "clear")
    assert "off" in env.ok("allowed-recipients", "show").stdout


def test_allowedRecipients_NotAnAddress_IsUsageError(env):
    assert env.run("allowed-recipients", "set", "nobody").exit_code == 2


@pytest.mark.parametrize("option", ["--to", "--cc", "--bcc"])
def test_send_RecipientNotAllowed_IsRefusedBeforeAnyCall(env, option, tmp_path):
    env.ok("allowed-recipients", "set", ME, OTHER)
    attached = tmp_path / "a.txt"
    attached.write_text("x", encoding="utf-8")
    args = ["send", "--to", OTHER, "--subject", "s", "--body", "b", "--attach", str(attached)]
    args += ["--to" if option == "--to" else option, "stranger@example.org"]
    result = env.run(*args)
    assert result.exit_code == 1 and "stranger@example.org" in result.output and "Nothing was sent" in result.output
    assert env.zoho.calls == [] and env.zoho.token_requests == 0 and not env.zoho.uploads


def test_reply_AllToSomeoneNotAllowed_IsRefusedAndNothingSent(env):
    env.ok("allowed-recipients", "set", ME, OTHER)
    _message(env, id="5", folder=INBOX, subject="hi", sender=OTHER, cc="stranger@example.org")
    result = env.run("reply", "5", "--all", "--body", "b")
    assert result.exit_code == 1 and "stranger@example.org" in result.output
    assert env.zoho.sent == [] and _only_reads(env)


def test_replyAll_OwnAliasOnTheOriginalNotAllowed_IsRefused(env):
    # Zoho copies the original CC itself on a reply to all, own aliases included, so they are checked too.
    env.ok("allowed-recipients", "set", ME, OTHER)
    _message(env, id="5", folder=INBOX, subject="hi", sender=OTHER, cc="info@example.com")
    result = env.run("reply", "5", "--all", "--body", "b")
    assert result.exit_code == 1 and "info@example.com" in result.output and env.zoho.sent == []
    env.ok("reply", "5", "--body", "b")
    assert env.zoho.sent[-1]["toAddress"] == OTHER


# --------------------------------------------------------------------------------------------- send


def test_send_PlainWithCcAndBcc_SendsOneBody(env):
    data = json.loads(env.ok("send", "--to", OTHER, "--cc", "a@x.org,b@x.org", "--bcc", ME, "--subject", "Hi",
                             "--body", "Hello", "--json").stdout)
    sent = env.zoho.sent[-1]
    assert sent["_path"] == "messages"
    assert (sent["fromAddress"], sent["toAddress"], sent["ccAddress"], sent["bccAddress"]) == \
           (ME, OTHER, "a@x.org,b@x.org", ME)
    assert (sent["content"], sent["mailFormat"]) == ("Hello", "plaintext")
    assert data["sent"] is True and data["message_id"] and data["mail_id"]


def test_send_HtmlFile_SendsHtml(env, tmp_path):
    page = tmp_path / "b.html"
    page.write_text("<b>hi</b>", encoding="utf-8")
    env.ok("send", "--to", OTHER, "--subject", "s", "--html-file", str(page))
    assert (env.zoho.sent[-1]["content"], env.zoho.sent[-1]["mailFormat"]) == ("<b>hi</b>", "html")


def test_send_TwoBodies_IsUsageErrorBeforeAnyCall(env, tmp_path):
    page = tmp_path / "b.html"
    page.write_text("x", encoding="utf-8")
    result = env.run("send", "--to", OTHER, "--subject", "s", "--body", "b", "--html-file", str(page))
    assert result.exit_code == 2 and env.zoho.calls == []


def test_send_NoTo_IsUsageError(env):
    assert env.run("send", "--cc", OTHER, "--subject", "s", "--body", "b").exit_code == 2


def test_send_Attachments_UploadedThenCarried(env, tmp_path):
    first, second = tmp_path / "notes.txt", tmp_path / "data.bin"
    first.write_bytes(b"notes\n")
    second.write_bytes(bytes(range(256)))
    env.ok("send", "--to", OTHER, "--subject", "s", "--body", "b", "--attach", str(first), "--attach", str(second))
    assert env.zoho.uploads == [("notes.txt", b"notes\n"), ("data.bin", bytes(range(256)))]
    assert [a["attachmentName"] for a in env.zoho.sent[-1]["attachments"]] == ["notes.txt", "data.bin"]
    assert all(set(a) == {"storeName", "attachmentPath", "attachmentName"} for a in env.zoho.sent[-1]["attachments"])


def test_send_MissingAttachment_IsUsageErrorBeforeAnyCall(env, tmp_path):
    result = env.run("send", "--to", OTHER, "--subject", "s", "--body", "b", "--attach", str(tmp_path / "no.txt"))
    assert result.exit_code == 2 and env.zoho.calls == []


def test_send_FromAlias_UsesIt(env):
    env.ok("send", "--to", OTHER, "--subject", "s", "--body", "b", "--from", "info@example.com")
    assert env.zoho.sent[-1]["fromAddress"] == "info@example.com"


def test_send_FromAddressNotTheAccounts_IsRefused(env):
    result = env.run("send", "--to", OTHER, "--subject", "s", "--body", "b", "--from", "boss@example.org")
    assert result.exit_code == 2 and "info@example.com" in result.output and env.zoho.sent == []


def test_send_At_SchedulesInTheAccountsTimeZone(env):
    data = json.loads(env.ok("send", "--to", OTHER, "--subject", "s", "--body", "b", "--at", "2026-11-09 09:05",
                             "--json").stdout)
    sent = env.zoho.sent[-1]
    assert (sent["isSchedule"], sent["scheduleType"], sent["timeZone"], sent["scheduleTime"]) == \
           (True, 6, "America/Toronto", "11/09/2026 09:05:00")
    assert data["scheduled"] is True and data["sent"] is False
    assert data["scheduled_for"] == "11/09/2026 09:05:00 America/Toronto"


def test_send_AtWithTimeZone_UsesIt(env):
    env.ok("send", "--to", OTHER, "--subject", "s", "--body", "b", "--at", "2026-11-09 09:05",
           "--time-zone", "Europe/Copenhagen")
    assert env.zoho.sent[-1]["timeZone"] == "Europe/Copenhagen"


@pytest.mark.parametrize("args", [("--at", "tomorrow"), ("--time-zone", "Europe/Copenhagen")])
def test_send_BadSchedule_IsUsageErrorBeforeAnyCall(env, args):
    result = env.run("send", "--to", OTHER, "--subject", "s", "--body", "b", *args)
    assert result.exit_code == 2 and env.zoho.calls == [] and env.zoho.token_requests == 0


# --------------------------------------------------------------------------------------------- reply, forward


def test_reply_ToTheSender_KeepsTheThreadAndPrefixesRe(env):
    _message(env, id="5", folder=INBOX, subject="Plans", sender=OTHER, cc="a@x.org")
    data = json.loads(env.ok("reply", "5", "--body", "ok", "--json").stdout)
    sent = env.zoho.sent[-1]
    assert sent["_path"] == "messages/5" and sent["action"] == "reply"
    assert (sent["toAddress"], sent.get("ccAddress"), sent["subject"]) == (OTHER, None, "Re: Plans")
    assert data["in_reply_to"] == "5"


def test_replyAll_CopiesEveryoneButThisAccount(env):
    _message(env, id="5", folder=INBOX, subject="Re: Plans", sender=OTHER,
             to="&lt;soren@devthrottle.com&gt;, b@x.org", cc="a@x.org, info@example.com")
    env.ok("reply", "5", "--all", "--body", "ok")
    sent = env.zoho.sent[-1]
    assert sent["action"] == "replyall" and sent["subject"] == "Re: Plans"
    assert sent["toAddress"] == OTHER and sent["ccAddress"] == "b@x.org,a@x.org"


def test_reply_ToOwnMessage_GoesToItsRecipients(env):
    _message(env, id="5", folder=INBOX, subject="Out", sender=ME, to="b@x.org")
    env.ok("reply", "5", "--body", "again")
    assert env.zoho.sent[-1]["toAddress"] == "b@x.org"


def test_reply_ReplyTo_IsUsedOverFrom(env):
    _message(env, id="5", folder=INBOX, subject="List", sender="list@x.org", reply_to="answers@x.org")
    env.ok("reply", "5", "--body", "b")
    assert env.zoho.sent[-1]["toAddress"] == "answers@x.org"


def test_forward_SaysZohoCannotAndNamesDtEmail(env):
    result = env.run("forward", "5", "--to", OTHER)
    assert result.exit_code == 2 and "dt-email forward" in result.output and env.zoho.calls == []


def test_filters_SaysZohoHasNone(env):
    result = env.run("filters", "list")
    assert result.exit_code == 2 and "web settings" in result.output and env.zoho.calls == []


def test_zohoServerError_OnSend_IsOneSentence(env):
    env.zoho.fail_action = "reply"
    _message(env, id="5", folder=INBOX, subject="x", sender=OTHER)
    result = env.run("reply", "5", "--body", "b")
    assert result.exit_code == 1 and "500" in result.output and "Traceback" not in result.output


# --------------------------------------------------------------------------------------------- drafts


def test_draft_SaveListDelete(env):
    data = json.loads(env.ok("draft", "save", "--to", "stranger@example.org", "--subject", "Later", "--body", "b",
                             "--json").stdout)
    assert env.zoho.sent[-1]["mode"] == "draft" and data["saved"] == "Drafts"
    listed = json.loads(env.ok("draft", "list", "--json").stdout)
    assert [m["subject"] for m in listed["messages"]] == ["Later"]
    env.ok("draft", "delete", data["message_id"])
    assert env.zoho.calls[-1][0] == "DELETE" and env.zoho.calls[-1][2] == {"expunge": "true"}
    assert json.loads(env.ok("draft", "list", "--json").stdout)["count"] == 0


def test_draftSend_SaysZohoCannot(env):
    result = env.run("draft", "send", "123")
    assert result.exit_code == 2 and "dt-zoho send" in result.output and env.zoho.calls == []


# --------------------------------------------------------------------------------------------- managing messages


@pytest.mark.parametrize("as_,read,flag", [("read", True, "flag_not_set"), ("important", False, "important"),
                                           ("followup", False, "followup"), ("info", False, "info")])
def test_mark_SetsReadOrFlag(env, as_, read, flag):
    m1 = _message(env, id="1", folder=INBOX, subject="a")
    m2 = _message(env, id="2", folder=INBOX, subject="b")
    env.ok("mark", "1,2", "--as", as_)
    assert (m1.read, m1.flag) == (read, flag) and (m2.read, m2.flag) == (read, flag)


def test_mark_UnreadAndUnflagged(env):
    m = _message(env, id="1", folder=INBOX, subject="a", read=True, flag="important")
    env.ok("mark", "1", "--as", "unread")
    env.ok("mark", "1", "--as", "unflagged")
    assert (m.read, m.flag) == (False, "flag_not_set")


@pytest.mark.parametrize("args", [("mark", "1", "--as", "loud"), ("mark", "abc", "--as", "read")])
def test_mark_BadInput_IsUsageErrorBeforeAnyCall(env, args):
    result = env.run(*args)
    assert result.exit_code == 2 and env.zoho.calls == []


def test_move_ToAFolderByPath(env):
    m = _message(env, id="1", folder=INBOX, subject="a")
    env.ok("move", "1", "--to", "/Inbox/Reports")
    assert m.folder == SUB


def test_archive_ThenUnarchive_IsAMoveBack(env):
    m = _message(env, id="1", folder=INBOX, subject="a")
    env.ok("archive", "1")
    assert m.folder == ARCHIVE
    env.ok("unarchive", "1")
    assert m.folder == INBOX


def test_unarchive_NeverUsesZohosUnarchiveCall(env, monkeypatch):
    _message(env, id="1", folder=ARCHIVE, subject="a")
    seen = []
    real = env.zoho._apply
    monkeypatch.setattr(env.zoho, "_apply", lambda body, targets: (seen.append(body["mode"]), real(body, targets)))
    env.ok("unarchive", "1", "--to", "/Inbox/Reports")
    assert seen == ["moveMessage"]


def test_delete_MovesToTrash_ThenPermanentRemoves(env):
    m = _message(env, id="1", folder=INBOX, subject="a")
    env.ok("delete", "1")
    assert m.folder == TRASH and m in env.zoho.messages
    env.ok("delete", "1", "--folder", "Trash", "--permanent")
    assert m not in env.zoho.messages


def test_delete_InTrashWithoutPermanent_SaysHowAndDeletesNothing(env):
    m = _message(env, id="1", folder=TRASH, subject="a")
    result = env.run("delete", "1", "--folder", "Trash")
    assert result.exit_code == 2 and "--permanent" in result.output and m in env.zoho.messages


def test_spam_ThenNotSpam(env):
    m = _message(env, id="1", folder=INBOX, subject="a")
    env.ok("spam", "1")
    assert m.folder == SPAM
    env.ok("not-spam", "1")
    assert m.folder == INBOX


# --------------------------------------------------------------------------------------------- labels


def test_label_CreateApplyRemoveClearRenameDelete(env):
    m = _message(env, id="1", folder=INBOX, subject="a")
    created = json.loads(env.ok("label", "create", "work", "--color", "#112233", "--json").stdout)
    lid = created["id"]
    env.ok("label", "apply", "1", "--label", "work")
    env.ok("label", "apply", "1", "--label", "LWTBI")
    assert set(m.labels) == {lid, "636823000000007002"}
    env.ok("label", "remove", "1", "--label", "work")
    assert m.labels == ["636823000000007002"]
    env.ok("label", "clear", "1")
    assert m.labels == []
    env.ok("label", "rename", "work", "--name", "jobs")
    label = next(l for l in env.zoho.labels if l["labelId"] == lid)
    assert (label["displayName"], label["color"]) == ("jobs", "#112233")
    env.ok("label", "delete", "jobs")
    assert all(l["labelId"] != lid for l in env.zoho.labels)


@pytest.mark.parametrize("args", [("label", "create", "x", "--color", "blue"), ("label", "rename", "LWTBI")])
def test_label_BadInput_IsUsageErrorBeforeAnyCall(env, args):
    result = env.run(*args)
    assert result.exit_code == 2 and env.zoho.calls == []


# --------------------------------------------------------------------------------------------- folders


def test_folder_CreateNestedRenameMoveImapEmptyDelete(env):
    parent = json.loads(env.ok("folder", "create", "Projects", "--json").stdout)
    child = json.loads(env.ok("folder", "create", "Alpha", "--parent", "Projects", "--json").stdout)
    assert child["path"] == "/Projects/Alpha" and child["parent_id"] == parent["id"]
    env.ok("folder", "rename", "/Projects/Alpha", "--name", "Beta")
    env.ok("folder", "move", "/Projects/Beta", "--to-top")
    moved = next(f for f in env.zoho.folders if f["folderId"] == child["id"])
    assert moved["path"] == "/Beta" and "parentFolderId" not in moved
    env.ok("folder", "imap", "Beta", "--hide")
    assert moved["imapAccess"] is False
    env.ok("folder", "imap", "Beta", "--show")
    assert moved["imapAccess"] is True
    _message(env, id="1", folder=child["id"], subject="a")
    _message(env, id="2", folder=child["id"], subject="b")
    env.ok("folder", "mark-read", "Beta")
    assert all(m.read for m in env.zoho.messages)
    env.ok("folder", "empty", "Beta")
    assert [m.folder for m in env.zoho.messages] == [TRASH, TRASH]
    first = json.loads(env.ok("folder", "delete", "Beta", "--json").stdout)
    assert first["permanent"] is False and first["moved_to"] == "/Trash/Beta"
    second = json.loads(env.ok("folder", "delete", "/Trash/Beta", "--json").stdout)
    assert second["permanent"] is True and "moved_to" not in second
    env.ok("folder", "delete", "Projects")
    env.ok("folder", "delete", "/Trash/Projects")
    assert all(f["folderId"] not in (parent["id"], child["id"]) for f in env.zoho.folders)


@pytest.mark.parametrize("args", [("folder", "create", "a/b"), ("folder", "move", "Inbox"),
                                  ("folder", "imap", "Inbox"), ("folder", "imap", "Inbox", "--show", "--hide")])
def test_folder_BadInput_IsUsageErrorBeforeAnyCall(env, args):
    result = env.run(*args)
    assert result.exit_code == 2 and env.zoho.calls == []


# --------------------------------------------------------------------------------------------- threads


def test_thread_MarkAndLabel_ReachEveryMessageInIt(env):
    first = _message(env, id="10", folder=INBOX, subject="a")
    second = _message(env, id="11", folder=INBOX, subject="Re: a", thread="10")
    other = _message(env, id="12", folder=INBOX, subject="b")
    env.ok("thread", "mark", "10", "--as", "read")
    assert first.read and second.read and not other.read
    env.ok("thread", "label", "10", "--apply", "LWTBI")
    assert first.labels == second.labels == ["636823000000007002"] and other.labels == []
    env.ok("thread", "label", "10", "--remove", "LWTBI")
    assert first.labels == second.labels == []


def test_thread_Label_NeedsApplyOrRemove(env):
    assert env.run("thread", "label", "10").exit_code == 2
    assert env.zoho.calls == []


def test_writingCommands_NoKey_NameTheCcSecretsCommand(env, monkeypatch):
    monkeypatch.delenv(config.CLIENT_SECRET_ENV)
    result = env.run("mark", "1", "--as", "read")
    assert result.exit_code == 1 and "cc-secrets run" in result.output

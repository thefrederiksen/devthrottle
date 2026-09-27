"""not-spam: move one message out of Spam into the Inbox, and nothing else.

A fake Gmail service keeps real label state, so the command is proven by the labels read back after the
move, the way it proves itself against Gmail. No real mailbox is touched.
"""

import json
from unittest.mock import MagicMock, patch

from typer.testing import CliRunner

from src import cli
from src.gmail_api import GmailClient


class _Call:
    def __init__(self, answer):
        self._answer = answer

    def execute(self):
        if isinstance(self._answer, Exception):
            raise self._answer
        return self._answer


class LabelledGmail:
    """messages.get and messages.modify over a dict of message id -> labels. Records every call."""

    def __init__(self, labels, ignore_modify=False):
        self.labels = {k: list(v) for k, v in labels.items()}
        self.ignore_modify = ignore_modify
        self.calls = []

    def users(self):
        return self

    def messages(self):
        return self

    def get(self, userId, id, format):
        self.calls.append(("get", id, format))
        return _Call({"id": id, "threadId": "t-" + id, "labelIds": list(self.labels[id])})

    def modify(self, userId, id, body):
        self.calls.append(("modify", id, body))
        if not self.ignore_modify:
            now = [x for x in self.labels[id] if x not in body.get("removeLabelIds", [])]
            now += [x for x in body.get("addLabelIds", []) if x not in now]
            self.labels[id] = now
        return _Call({"id": id, "threadId": "t-" + id, "labelIds": list(self.labels[id])})

    def send(self, userId, body):
        raise AssertionError("not-spam must never send")


def _client(svc):
    with patch("src.gmail_api.build") as build:
        build.return_value = svc
        return GmailClient(credentials=MagicMock())


def _invoke(svc, args, auth="oauth"):
    with patch.object(cli, "_resolve_and_get_auth", return_value=("consulting", auth)), \
            patch.object(cli, "get_client", return_value=_client(svc)):
        return CliRunner().invoke(cli.app, args)


def test_a_spam_message_lands_in_the_inbox_and_the_answer_is_read_back():
    svc = LabelledGmail({"m1": ["SPAM", "UNREAD", "CATEGORY_PERSONAL"]})
    result = _invoke(svc, ["not-spam", "m1", "--json"])
    assert result.exit_code == 0, result.output
    out = json.loads(result.output)
    assert out["id"] == "m1" and out["thread_id"] == "t-m1"
    assert "INBOX" in out["labels"] and "SPAM" not in out["labels"]
    assert "UNREAD" in out["labels"], "not-spam leaves the message unread"
    assert [c[0] for c in svc.calls] == ["get", "modify", "get"]
    assert svc.calls[1][2] == {"removeLabelIds": ["SPAM"], "addLabelIds": ["INBOX"]}


def test_a_message_not_in_spam_is_refused_and_untouched():
    svc = LabelledGmail({"m2": ["CATEGORY_UPDATES"]})   # archived, not spam: must not be pulled into the inbox
    result = _invoke(svc, ["not-spam", "m2", "--json"])
    assert result.exit_code == 1
    assert "not in Spam" in result.output
    assert [c[0] for c in svc.calls] == ["get"], "nothing is modified"
    assert svc.labels["m2"] == ["CATEGORY_UPDATES"]


def test_a_message_in_trash_is_refused_and_untouched():
    svc = LabelledGmail({"m7": ["SPAM", "TRASH", "UNREAD"]})
    result = _invoke(svc, ["not-spam", "m7", "--json"])
    assert result.exit_code == 1
    assert "in Trash" in result.output
    assert [c[0] for c in svc.calls] == ["get"]


def test_a_message_that_lands_in_trash_is_an_error_not_a_success():
    svc = LabelledGmail({"m8": ["SPAM"]})
    real_modify = svc.modify

    def modify_then_trashed(userId, id, body):
        answer = real_modify(userId, id, body)
        svc.labels[id].append("TRASH")   # deleted by someone else between the move and the read-back
        return answer

    svc.modify = modify_then_trashed
    result = _invoke(svc, ["not-spam", "m8", "--json"])
    assert result.exit_code == 1
    assert "still not in the Inbox" in result.output


def test_a_move_gmail_did_not_make_is_an_error_not_a_success():
    svc = LabelledGmail({"m3": ["SPAM"]}, ignore_modify=True)
    result = _invoke(svc, ["not-spam", "m3", "--json"])
    assert result.exit_code == 1
    assert "still not in the Inbox" in result.output


def test_plain_output_says_what_moved():
    svc = LabelledGmail({"m4": ["SPAM"]})
    result = _invoke(svc, ["not-spam", "m4"])
    assert result.exit_code == 0, result.output
    assert "Moved out of Spam into the Inbox" in result.output


def test_an_app_password_account_is_refused_before_anything():
    svc = LabelledGmail({"m5": ["SPAM"]})
    result = _invoke(svc, ["not-spam", "m5"], auth="app_password")
    assert result.exit_code == 1
    assert "needs an OAuth account" in result.output
    assert svc.calls == []

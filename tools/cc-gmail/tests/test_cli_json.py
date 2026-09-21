"""Tests for the --json output of search, list, read, draft and reply.

The real GmailClient runs against a fake Gmail service, so the thread id is
proven to travel from Gmail's threadId through the library into the printed
JSON. No real mailbox is touched.
"""

import json
import re
from unittest.mock import MagicMock, patch

import pytest
from typer.testing import CliRunner

from src import cli
from src.gmail_api import GmailClient


def _gmail_message(msg_id, thread_id, sender, subject, labels):
    """A message as Gmail's messages.get(format=full) returns it."""
    return {
        "id": msg_id,
        "threadId": thread_id,
        "snippet": "snippet",
        "labelIds": labels,
        "internalDate": "1790000000000",
        "payload": {
            "headers": [
                {"name": "From", "value": sender},
                {"name": "To", "value": "soren@centerconsulting.com"},
                {"name": "Subject", "value": subject},
                {"name": "Date", "value": "Mon, 21 Sep 2026 09:15:00 -0400"},
                {"name": "Message-ID", "value": f"<{msg_id}@mail.example.com>"},
            ],
            "body": {"data": "SGVsbG8gdGhlcmU"},  # "Hello there"
        },
    }


MESSAGES = {
    "m1": _gmail_message("m1", "t1", "Ann Prospect <ann@prospect.example>", "Your website", ["INBOX", "UNREAD"]),
    "m2": _gmail_message("m2", "t2", "Bob <bob@other.example>", "Caf\u00e9 menu", ["INBOX"]),
}


class _Call:
    """One Gmail API call: execute() returns the canned answer."""

    def __init__(self, answer):
        self._answer = answer

    def execute(self):
        return self._answer


class FakeGmailService:
    """Just enough of the Gmail API resource tree for the commands under test.

    Every call is recorded so a test can prove what did - and did not - happen.
    """

    def __init__(self, messages, draft_answer=None):
        self._messages = messages
        self._draft_answer = draft_answer
        self.calls = []

    # resource tree
    def users(self):
        return self

    def messages(self):
        return _Messages(self)

    def drafts(self):
        return _Drafts(self)

    def getProfile(self, userId):
        self.calls.append(("getProfile", {}))
        return _Call({"emailAddress": "soren@centerconsulting.com"})


class _Messages:
    def __init__(self, svc):
        self._svc = svc

    def list(self, **kwargs):
        self._svc.calls.append(("messages.list", kwargs))
        ids = list(self._svc._messages)
        if kwargs.get("q") == "subject:nothing-matches-this":
            ids = []
        return _Call({"messages": [{"id": i, "threadId": self._svc._messages[i]["threadId"]} for i in ids]})

    def get(self, userId, id, format):
        self._svc.calls.append(("messages.get", {"id": id}))
        return _Call(self._svc._messages[id])

    def modify(self, userId, id, body):
        self._svc.calls.append(("messages.modify", {"id": id, "body": body}))
        return _Call({"id": id})

    def send(self, userId, body):
        self._svc.calls.append(("messages.send", {"body": body}))
        return _Call({"id": "sent1"})


class _Drafts:
    def __init__(self, svc):
        self._svc = svc

    def create(self, userId, body):
        self._svc.calls.append(("drafts.create", {"body": body}))
        return _Call(self._svc._draft_answer)


def _gmail_client(service):
    with patch("src.gmail_api.build") as mock_build:
        mock_build.return_value = service
        return GmailClient(credentials=MagicMock())


@pytest.fixture
def runner():
    return CliRunner()


def _oauth(service):
    """Patch the CLI to an OAuth account whose client talks to the fake service."""
    client = _gmail_client(service)
    return (
        patch.object(cli, "_resolve_and_get_auth", return_value=("consulting", "oauth")),
        patch.object(cli, "get_client", return_value=client),
    )


def _invoke(runner, service, args):
    auth_patch, client_patch = _oauth(service)
    with auth_patch, client_patch:
        return runner.invoke(cli.app, args)


_ANSI_ESCAPE = re.compile(r"\x1b\[[0-9;]*m")


def _text(output):
    """The printed text without terminal color codes.

    Continuous integration runs this suite with FORCE_COLOR=1, so Rich styles the
    plain output there; the words are what must stay unchanged.
    """
    return _ANSI_ESCAPE.sub("", output)


def _call_names(service):
    return [name for name, _ in service.calls]


# -- search --json --


class TestSearchJson:
    def test_search_json_prints_one_object_per_message_with_thread_id(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["search", "in:inbox", "--json"])

        assert result.exit_code == 0, result.output
        data = json.loads(result.output)
        assert data == [
            {
                "id": "m1",
                "thread_id": "t1",
                "from": "Ann Prospect <ann@prospect.example>",
                "to": "soren@centerconsulting.com",
                "subject": "Your website",
                "date": "Mon, 21 Sep 2026 09:15:00 -0400",
                "labels": ["INBOX", "UNREAD"],
            },
            {
                "id": "m2",
                "thread_id": "t2",
                "from": "Bob <bob@other.example>",
                "to": "soren@centerconsulting.com",
                "subject": "Caf\u00e9 menu",
                "date": "Mon, 21 Sep 2026 09:15:00 -0400",
                "labels": ["INBOX"],
            },
        ]

    def test_search_json_output_is_ascii_even_for_non_ascii_headers(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["search", "in:inbox", "--json"])

        assert result.output.isascii()
        assert "Caf\\u00e9 menu" in result.output

    def test_search_json_no_match_prints_empty_array(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["search", "subject:nothing-matches-this", "--json"])

        assert result.exit_code == 0, result.output
        assert json.loads(result.output) == []

    def test_search_json_passes_count_and_query_through(self, runner):
        svc = FakeGmailService(MESSAGES)
        _invoke(runner, svc, ["search", "from:ann", "-n", "1", "--json"])

        list_kwargs = [kw for name, kw in svc.calls if name == "messages.list"][0]
        assert list_kwargs["q"] == "from:ann"
        assert list_kwargs["maxResults"] == 1

    def test_search_json_changes_no_mail(self, runner):
        svc = FakeGmailService(MESSAGES)
        _invoke(runner, svc, ["search", "in:inbox", "--json"])

        assert set(_call_names(svc)) == {"messages.list", "messages.get"}

    def test_search_plain_output_is_unchanged(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["search", "in:inbox"])

        assert result.exit_code == 0, result.output
        assert _text(result.output) == (
            "\n"
            "Search: in:inbox (consulting)\n"
            "\n"
            "[ ] m1\n"
            "    From: Ann Prospect <ann@prospect.example>\n"
            "    Subject: Your website\n"
            "    Date: Mon, 21 Sep 2026 09:15:00\n"
            "\n"
            "[ ] m2\n"
            "    From: Bob <bob@other.example>\n"
            "    Subject: Caf menu\n"
            "    Date: Mon, 21 Sep 2026 09:15:00\n"
            "\n"
            "Found 2 message(s)\n"
        )

    def test_search_plain_no_match_is_unchanged(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["search", "subject:nothing-matches-this"])

        assert _text(result.output) == "No messages matching: subject:nothing-matches-this\n"


# -- list --json --


class TestListJson:
    def test_list_json_prints_same_shape_as_search(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["list", "--json"])

        assert result.exit_code == 0, result.output
        data = json.loads(result.output)
        assert [(m["id"], m["thread_id"]) for m in data] == [("m1", "t1"), ("m2", "t2")]
        assert set(data[0]) == {"id", "thread_id", "from", "to", "subject", "date", "labels"}

    def test_list_json_changes_no_mail(self, runner):
        svc = FakeGmailService(MESSAGES)
        _invoke(runner, svc, ["list", "--json"])

        assert set(_call_names(svc)) == {"messages.list", "messages.get"}

    def test_list_plain_output_is_unchanged(self, runner):
        svc = FakeGmailService({"m1": MESSAGES["m1"]})
        result = _invoke(runner, svc, ["list"])

        assert _text(result.output) == (
            "\n"
            "Messages in INBOX (consulting)\n"
            "\n"
            "[*] m1\n"
            "    From: Ann Prospect <ann@prospect.example>\n"
            "    Subject: Your website\n"
            "    Date: Mon, 21 Sep 2026 09:15:00\n"
            "\n"
        )


# -- read --json --


class TestReadJson:
    def test_read_json_prints_message_with_thread_id_and_body(self, runner):
        svc = FakeGmailService(MESSAGES)
        result = _invoke(runner, svc, ["read", "m1", "--json"])

        assert result.exit_code == 0, result.output
        data = json.loads(result.output)
        assert data["id"] == "m1"
        assert data["thread_id"] == "t1"
        assert data["from"] == "Ann Prospect <ann@prospect.example>"
        assert data["body"] == "Hello there"

    def test_read_json_still_marks_the_message_read(self, runner):
        # Documented behaviour, deliberately unchanged: read marks the message read.
        svc = FakeGmailService(MESSAGES)
        _invoke(runner, svc, ["read", "m1", "--json"])

        assert ("messages.modify", {"id": "m1", "body": {"removeLabelIds": ["UNREAD"]}}) in svc.calls


# -- draft --json --


DRAFT_ANSWER = {"id": "r-123", "message": {"id": "dm1", "threadId": "dt1", "labelIds": ["DRAFT"]}}


class TestDraftJson:
    def test_draft_json_prints_draft_message_and_thread_id(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=DRAFT_ANSWER)
        result = _invoke(runner, svc, ["draft", "-t", "ann@prospect.example", "-s", "Hi", "-b", "Body", "--json"])

        assert result.exit_code == 0, result.output
        assert json.loads(result.output) == {"draft_id": "r-123", "message_id": "dm1", "thread_id": "dt1"}

    def test_draft_json_never_sends(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=DRAFT_ANSWER)
        _invoke(runner, svc, ["draft", "-t", "ann@prospect.example", "-s", "Hi", "-b", "Body", "--json"])

        assert _call_names(svc) == ["drafts.create"]

    def test_draft_json_response_missing_thread_id_is_an_error(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer={"id": "r-123", "message": {"id": "dm1"}})
        result = _invoke(runner, svc, ["draft", "-t", "ann@prospect.example", "-s", "Hi", "-b", "Body", "--json"])

        assert result.exit_code == 1
        assert "missing thread_id" in result.output

    def test_draft_json_refused_on_app_password_account_before_creating(self, runner):
        imap = MagicMock()
        with patch.object(cli, "_resolve_and_get_auth", return_value=("personal", "app_password")), \
                patch.object(cli, "_get_imap_client", return_value=imap):
            result = runner.invoke(cli.app, ["draft", "-t", "a@b.example", "-s", "Hi", "-b", "Body", "--json"])

        assert result.exit_code == 1
        assert "needs an OAuth account" in result.output
        imap.create_draft.assert_not_called()

    def test_draft_plain_output_is_unchanged(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=DRAFT_ANSWER)
        result = _invoke(runner, svc, ["draft", "-t", "ann@prospect.example", "-s", "Hi", "-b", "Body"])

        assert _text(result.output) == "Draft created. ID: r-123\n"


# -- reply --json --


REPLY_DRAFT_ANSWER = {"id": "r-456", "message": {"id": "dm2", "threadId": "t1", "labelIds": ["DRAFT"]}}


class TestReplyJson:
    def test_reply_json_prints_draft_in_the_original_thread(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=REPLY_DRAFT_ANSWER)
        result = _invoke(runner, svc, ["reply", "m1", "-b", "Thanks", "--json"])

        assert result.exit_code == 0, result.output
        assert json.loads(result.output) == {"draft_id": "r-456", "message_id": "dm2", "thread_id": "t1"}
        create = [kw for name, kw in svc.calls if name == "drafts.create"][0]
        assert create["body"]["message"]["threadId"] == "t1"

    def test_reply_json_never_sends(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=REPLY_DRAFT_ANSWER)
        _invoke(runner, svc, ["reply", "m1", "-b", "Thanks", "--json"])

        assert "messages.send" not in _call_names(svc)
        assert "drafts.create" in _call_names(svc)

    def test_reply_json_with_send_is_refused_and_nothing_is_sent(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=REPLY_DRAFT_ANSWER)
        result = _invoke(runner, svc, ["reply", "m1", "-b", "Thanks", "--send", "--json"])

        assert result.exit_code == 1
        assert "cannot be combined with --send" in result.output
        assert svc.calls == []

    def test_reply_json_refused_on_app_password_account_before_creating(self, runner):
        imap = MagicMock()
        with patch.object(cli, "_resolve_and_get_auth", return_value=("personal", "app_password")), \
                patch.object(cli, "_get_imap_client", return_value=imap):
            result = runner.invoke(cli.app, ["reply", "42", "-b", "Thanks", "--json"])

        assert result.exit_code == 1
        assert "needs an OAuth account" in result.output
        imap.create_reply_draft.assert_not_called()

    def test_reply_plain_output_is_unchanged(self, runner):
        svc = FakeGmailService(MESSAGES, draft_answer=REPLY_DRAFT_ANSWER)
        result = _invoke(runner, svc, ["reply", "m1", "-b", "Thanks"])

        assert _text(result.output) == (
            "Reply draft created.\n"
            "  To: Ann Prospect <ann@prospect.example>\n"
            "  Subject: Re: Your website\n"
            "  Draft ID: r-456\n"
        )


# -- the contract's rules for every --json command --
# CC-GMAIL-JSON-CONTRACT.md (cc-consult, website-factory-phase0): a missing header is "",
# never null; on failure the message is on stderr and NOTHING is on stdout.


def _gmail_message_without(msg_id, thread_id, missing):
    """A Gmail message that does not carry the named headers (e.g. an automated reply)."""
    msg = _gmail_message(msg_id, thread_id, "Mailer <mailer@example.com>", "Out of office", ["INBOX"])
    msg["payload"]["headers"] = [
        h for h in msg["payload"]["headers"] if h["name"].lower() not in missing
    ]
    return msg


@pytest.fixture
def split_runner():
    """A runner that keeps stderr apart from stdout, so a test can see which one carried the text."""
    return CliRunner(mix_stderr=False)


class _ExplodingMessages(_Messages):
    def get(self, userId, id, format):
        raise ValueError("gmail exploded mid-list")


class ExplodingGmailService(FakeGmailService):
    def messages(self):
        return _ExplodingMessages(self)


class TestMissingHeaderIsEmptyString:
    def test_search_json_missing_headers_are_empty_strings(self, runner):
        svc = FakeGmailService({"m9": _gmail_message_without("m9", "t9", {"to", "subject", "date"})})
        result = _invoke(runner, svc, ["search", "in:inbox", "--json"])

        assert result.exit_code == 0, result.output
        message = json.loads(result.output)[0]
        assert message["to"] == ""
        assert message["subject"] == ""
        assert message["date"] == ""
        assert message["from"] == "Mailer <mailer@example.com>"

    def test_read_json_missing_headers_are_empty_strings(self, runner):
        svc = FakeGmailService({"m9": _gmail_message_without("m9", "t9", {"from", "to"})})
        result = _invoke(runner, svc, ["read", "m9", "--json"])

        assert result.exit_code == 0, result.output
        message = json.loads(result.output)
        assert message["from"] == ""
        assert message["to"] == ""
        assert message["thread_id"] == "t9"


class TestFailureGoesToStderrNotStdout:
    @pytest.mark.parametrize("args", [
        ["search", "in:inbox", "--json"],
        ["list", "--json"],
        ["read", "m1", "--json"],
    ])
    def test_api_failure_message_is_on_stderr_and_stdout_is_empty(self, split_runner, args):
        svc = ExplodingGmailService(MESSAGES)
        result = _invoke(split_runner, svc, args)

        assert result.exit_code == 1
        assert result.stdout == ""
        assert "gmail exploded mid-list" in _text(result.stderr)

    def test_draft_json_bad_response_is_on_stderr_and_stdout_is_empty(self, split_runner):
        svc = FakeGmailService(MESSAGES, draft_answer={"id": "r-123", "message": {"id": "dm1"}})
        result = _invoke(split_runner, svc, ["draft", "-t", "a@b.example", "-s", "Hi", "-b", "Body", "--json"])

        assert result.exit_code == 1
        assert result.stdout == ""
        assert "missing thread_id" in _text(result.stderr)

    def test_reply_json_with_send_refusal_is_on_stderr_and_stdout_is_empty(self, split_runner):
        svc = FakeGmailService(MESSAGES, draft_answer=REPLY_DRAFT_ANSWER)
        result = _invoke(split_runner, svc, ["reply", "m1", "-b", "Thanks", "--send", "--json"])

        assert result.exit_code == 1
        assert result.stdout == ""
        assert "cannot be combined with --send" in _text(result.stderr)

    def test_app_password_refusal_is_on_stderr_and_stdout_is_empty(self, split_runner):
        with patch.object(cli, "_resolve_and_get_auth", return_value=("personal", "app_password")), \
                patch.object(cli, "_get_imap_client", return_value=MagicMock()):
            result = split_runner.invoke(cli.app, ["draft", "-t", "a@b.example", "-s", "Hi", "-b", "Body", "--json"])

        assert result.exit_code == 1
        assert result.stdout == ""
        assert "needs an OAuth account" in _text(result.stderr)

    def test_account_resolution_failure_is_on_stderr_and_stdout_is_empty(self, split_runner):
        # _resolve_and_get_auth runs before anything else in the command and prints its own error.
        def fail_resolution():
            cli.console.print("[red]Error:[/red] No account configured.")
            raise cli.typer.Exit(1)

        with patch.object(cli, "_resolve_and_get_auth", side_effect=fail_resolution):
            result = split_runner.invoke(cli.app, ["search", "in:inbox", "--json"])

        assert result.exit_code == 1
        assert result.stdout == ""
        assert "No account configured." in _text(result.stderr)

    def test_plain_failure_still_prints_on_stdout_after_a_json_run(self, split_runner):
        # The switch to stderr lasts for one --json invocation only; plain output is unchanged.
        _invoke(split_runner, ExplodingGmailService(MESSAGES), ["search", "in:inbox", "--json"])
        result = _invoke(split_runner, ExplodingGmailService(MESSAGES), ["search", "in:inbox"])

        assert result.exit_code == 1
        assert _text(result.stdout).endswith("Error: gmail exploded mid-list\n")
        assert result.stderr == ""

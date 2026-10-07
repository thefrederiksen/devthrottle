"""Regression tests for issues #2877 and #3251.

- reply_message must pass to_all explicitly: O365's Message has no reply_all()
  (so --all crashed), and reply() defaults to_all=True (so a plain reply went
  to everyone).
- read on a draft must not mark it read: Graph refuses that for a draft and the
  command exited non-zero after printing the message.
"""

from unittest.mock import MagicMock, patch

import pytest
from typer.testing import CliRunner

from O365.connection import MSGraphProtocol
from O365.message import Message

from src.cli import app
from src.outlook_api import OutlookClient

runner = CliRunner()


def _recipient(address):
    r = MagicMock()
    r.address = address
    return r


def _client_with_original():
    account = MagicMock()
    original = MagicMock(spec=["reply", "object_id", "is_draft"])
    original.is_draft = False
    reply = MagicMock()
    reply.object_id = "draft-1"
    reply.subject = "RE: hello"
    reply.to = [_recipient("sender@example.com")]
    reply.cc = []
    original.reply.return_value = reply
    account.mailbox.return_value.get_message.return_value = original
    return OutlookClient(account=account), original, reply


class TestReplyModes:
    def test_reply_message_DefaultMode_RepliesToSenderOnly(self):
        client, original, reply = _client_with_original()

        result = client.reply_message("msg-1", body="hi")

        original.reply.assert_called_once_with(to_all=False)
        reply.save_draft.assert_called_once()
        reply.send.assert_not_called()
        assert result["reply_all"] is False

    def test_reply_message_ReplyAll_PassesToAllTrue(self):
        client, original, reply = _client_with_original()
        reply.cc = [_recipient("cc@example.com")]

        result = client.reply_message("msg-1", body="hi", reply_all=True)

        original.reply.assert_called_once_with(to_all=True)
        assert result["cc"] == ["cc@example.com"]

    def test_reply_message_GraphReturnsNothing_RaisesConnectionError(self):
        client, original, _ = _client_with_original()
        original.reply.return_value = None

        with pytest.raises(ConnectionError):
            client.reply_message("msg-1", body="hi")

    def test_reply_message_GraphRefusesSave_RaisesConnectionError(self):
        client, _, reply = _client_with_original()
        reply.save_draft.return_value = False

        with pytest.raises(ConnectionError):
            client.reply_message("msg-1", body="hi")

    def test_reply_message_GraphRefusesSend_RaisesConnectionError(self):
        client, _, reply = _client_with_original()
        reply.send.return_value = False

        with pytest.raises(ConnectionError):
            client.reply_message("msg-1", body="hi", send=True)

    def test_reply_message_OriginalIsDraft_RaisesValueError(self):
        client, original, _ = _client_with_original()
        original.is_draft = True

        with pytest.raises(ValueError, match="draft"):
            client.reply_message("msg-1", body="hi")
        original.reply.assert_not_called()

    @pytest.mark.parametrize("args,expected", [([], False), (["--all"], True), (["-a"], True)])
    def test_reply_cli_AllFlag_ReachesClient(self, args, expected):
        client = MagicMock()
        client.reply_message.return_value = {"id": "d", "to": ["s@example.com"], "cc": []}
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["reply", "msg-1", "--body", "hi", *args])

        assert result.exit_code == 0, result.output
        assert client.reply_message.call_args.kwargs["reply_all"] is expected

    def test_reply_cli_LongDraftId_PrintedOnOneLine(self):
        long_id = "AAMk" + "x" * 150 + "AAA="
        client = MagicMock()
        client.reply_message.return_value = {"id": long_id, "to": ["s@example.com"], "cc": []}
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["reply", "msg-1", "--body", "hi"], env={"COLUMNS": "80"})

        assert f"Draft ID: {long_id}" in result.output

    def test_reply_cli_Help_StatesBothModes(self):
        result = runner.invoke(app, ["reply", "--help"], env={"COLUMNS": "200"})
        assert "sender only" in result.output
        assert "Reply all" in result.output


def _graph_reply(content_type, content):
    """A real O365 Message shaped like Graph's createReply answer: a draft that
    already holds the quoted original."""
    data = {"id": "r1", "isDraft": True, "subject": "RE: x",
            "body": {"contentType": content_type, "content": content},
            "toRecipients": [{"emailAddress": {"address": "s@example.com"}}],
            "ccRecipients": []}
    reply = Message(con=MagicMock(), protocol=MSGraphProtocol(), main_resource="me",
                    **{Message._cloud_data_key: data})
    reply.save_draft = MagicMock(return_value=True)
    return reply


def _saved_body(content_type, content, body, html):
    """Run reply_message over a real reply and return the body it would PATCH."""
    reply = _graph_reply(content_type, content)
    client, original, _ = _client_with_original()
    original.reply.return_value = reply
    client.reply_message("msg-1", body=body, html=html)
    return reply.to_api_data(restrict_keys=reply._track_changes)["body"]


HTML_QUOTE = "<html><head></head><body><div>quoted &amp; original</div></body></html>"


class TestReplyBodyAsSaved:
    def test_reply_message_PlainTextOverHtmlQuote_StaysHtmlAndEscaped(self):
        saved = _saved_body("html", HTML_QUOTE, "a < b\nline two", html=False)

        assert saved["contentType"] == "html"
        assert "a &lt; b<br/>line two" in saved["content"]
        assert "quoted &amp; original" in saved["content"]

    def test_reply_message_HtmlOverHtmlQuote_InsertsMarkup(self):
        saved = _saved_body("html", HTML_QUOTE, "<p>hi</p>", html=True)

        assert saved["contentType"] == "html"
        assert saved["content"].index("<p>hi</p>") < saved["content"].index("quoted")

    def test_reply_message_HtmlOverTextQuote_RebuildsAsHtml(self):
        saved = _saved_body("text", "From: s\nquoted <original>", "<p>hi</p>", html=True)

        assert saved["contentType"] == "html"
        assert saved["content"] == "<p>hi</p><br><br>From: s<br>quoted &lt;original&gt;"

    def test_reply_message_PlainTextOverTextQuote_StaysText(self):
        saved = _saved_body("text", "quoted", "hi", html=False)

        assert saved["contentType"] == "text"
        assert saved["content"] == "hi\nquoted"


def _msg(is_draft):
    return {"id": "m", "subject": "s", "from": "a@example.com", "from_name": "A",
            "to": ["b@example.com"], "cc": [], "date": None, "body": "body text",
            "is_draft": is_draft}


class TestReadDraft:
    @pytest.mark.parametrize("raw", [[], ["--raw"]])
    def test_read_Draft_DoesNotMarkAsReadAndExitsZero(self, raw):
        client = MagicMock()
        client.get_message.return_value = _msg(is_draft=True)
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["read", "m", *raw])

        assert result.exit_code == 0, result.output
        assert "body text" in result.output
        client.mark_as_read.assert_not_called()

    @pytest.mark.parametrize("raw", [[], ["--raw"]])
    def test_read_ReceivedMessage_MarksAsRead(self, raw):
        client = MagicMock()
        client.get_message.return_value = _msg(is_draft=False)
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["read", "m", *raw])

        assert result.exit_code == 0, result.output
        client.mark_as_read.assert_called_once_with("m")

    def test_format_message_IncludesIsDraft(self):
        client = OutlookClient(account=MagicMock())
        msg = MagicMock()
        msg.is_draft = True
        msg.flag = None
        assert client._format_message(msg)["is_draft"] is True

"""Regression tests for issue #3604: delete --permanent was ignored, so it did a
plain Graph DELETE, which leaves the message restorable (measured: Recoverable
Items, Deletions) instead of purging it."""

from unittest.mock import MagicMock, patch

import pytest
import requests
from typer.testing import CliRunner

from src.cli import app
from src.outlook_api import OutlookClient

runner = CliRunner()


def _client_with_message():
    account = MagicMock()
    message = MagicMock()
    message.object_id = "msg-1"
    message.build_url.side_effect = lambda path: "https://graph/me" + path
    account.mailbox.return_value.get_message.return_value = message
    return OutlookClient(account=account), message


class TestDeleteMessage:
    def test_delete_message_Default_UsesGraphDelete(self):
        client, message = _client_with_message()

        client.delete_message("msg-1")

        message.delete.assert_called_once()
        message.con.post.assert_not_called()

    def test_delete_message_Permanent_CallsPermanentDelete(self):
        client, message = _client_with_message()

        client.delete_message("msg-1", permanent=True)

        message.con.post.assert_called_once_with(
            "https://graph/me/messages/msg-1/permanentDelete")
        message.delete.assert_not_called()

    @pytest.mark.parametrize("permanent", [False, True])
    def test_delete_message_GraphRefuses_RaisesConnectionError(self, permanent):
        client, message = _client_with_message()
        message.delete.return_value = False
        message.con.post.return_value = None

        with pytest.raises(ConnectionError):
            client.delete_message("msg-1", permanent=permanent)

    def test_delete_message_NotFound_RaisesValueError(self):
        client, message = _client_with_message()
        client.account.mailbox.return_value.get_message.return_value = None

        with pytest.raises(ValueError):
            client.delete_message("msg-1")

    @pytest.mark.parametrize("args,expected", [([], False), (["--permanent"], True)])
    def test_delete_cli_PermanentFlag_ReachesClient(self, args, expected):
        client = MagicMock()
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["delete", "msg-1", "-y", *args])

        assert result.exit_code == 0, result.output
        assert client.delete_message.call_args.kwargs["permanent"] is expected

    def test_delete_cli_GraphHttpError_ExitsOneWithMessage(self):
        # O365 raises on a 4xx/5xx by default (raise_http_errors=True), so this
        # is the shape a real refusal takes.
        client = MagicMock()
        client.delete_message.side_effect = requests.HTTPError("404 Client Error: Not Found")
        with patch("src.cli.get_client", return_value=client):
            result = runner.invoke(app, ["delete", "msg-1", "-y", "--permanent"])

        assert result.exit_code == 1
        assert "404 Client Error" in result.output

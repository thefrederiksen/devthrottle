"""Regression tests for cc-outlook fixes.

Covers:
- --html flag actually sets the body type (O365 defaults to HTML, so plain text
  was silently sent as HTML).
- get_profile returns the real mailbox email, not the 'me' resource path.
- get_free_busy / forward_event raise a clear error on a falsy Graph response
  instead of dereferencing .status_code (which masked the real error).
- Naive datetimes are converted with the local timezone, not stamped as UTC.
"""

from datetime import datetime, timezone
from unittest.mock import MagicMock

import pytest

from src.outlook_api import OutlookClient, _to_utc, _as_aware


def _client_with_message():
    account = MagicMock()
    message = MagicMock()
    account.mailbox.return_value.new_message.return_value = message
    client = OutlookClient(account=account)
    return client, message


class TestHtmlBodyType:
    def test_send_message_text_sets_text_body_type(self):
        client, message = _client_with_message()
        client.send_message(to=["a@example.com"], subject="s", body="b", html=False)
        assert message.body_type == "text"

    def test_send_message_html_sets_html_body_type(self):
        client, message = _client_with_message()
        client.send_message(to=["a@example.com"], subject="s", body="b", html=True)
        assert message.body_type == "HTML"

    def test_create_draft_text_sets_text_body_type(self):
        client, message = _client_with_message()
        client.create_draft(to=["a@example.com"], subject="s", body="b", html=False)
        assert message.body_type == "text"

    def test_create_draft_html_sets_html_body_type(self):
        client, message = _client_with_message()
        client.create_draft(to=["a@example.com"], subject="s", body="b", html=True)
        assert message.body_type == "HTML"


class TestGetProfile:
    def test_returns_real_email_from_graph(self):
        account = MagicMock()
        response = MagicMock()
        response.json.return_value = {
            "mail": "real.user@contoso.com",
            "userPrincipalName": "real.user@contoso.com",
        }
        account.connection.get.return_value = response
        client = OutlookClient(account=account)
        assert client.get_profile()["emailAddress"] == "real.user@contoso.com"

    def test_falls_back_to_upn_when_mail_missing(self):
        account = MagicMock()
        response = MagicMock()
        response.json.return_value = {"mail": None, "userPrincipalName": "upn@contoso.com"}
        account.connection.get.return_value = response
        client = OutlookClient(account=account)
        assert client.get_profile()["emailAddress"] == "upn@contoso.com"

    def test_falsy_response_raises_clear_error(self):
        account = MagicMock()
        account.connection.get.return_value = None
        client = OutlookClient(account=account)
        with pytest.raises(ConnectionError):
            client.get_profile()


class TestFalsyGraphResponses:
    def test_get_free_busy_falsy_response_raises_without_status_code(self):
        account = MagicMock()
        account.connection.post.return_value = None  # falsy: request failed
        client = OutlookClient(account=account)
        with pytest.raises(ConnectionError) as exc:
            client.get_free_busy(
                ["a@example.com"],
                datetime(2026, 1, 1, 8, 0),
                datetime(2026, 1, 1, 18, 0),
            )
        # Must be a clear message, not an AttributeError about status_code.
        assert "status_code" not in str(exc.value)

    def test_forward_event_falsy_response_raises_without_status_code(self):
        account = MagicMock()
        account.connection.post.return_value = None
        client = OutlookClient(account=account)
        with pytest.raises(ConnectionError) as exc:
            client.forward_event("event-1", ["a@example.com"])
        assert "status_code" not in str(exc.value)


class TestTimezoneHelpers:
    def test_to_utc_treats_naive_as_local(self):
        naive = datetime(2026, 1, 1, 12, 0, 0)
        result = _to_utc(naive)
        assert result.tzinfo is not None
        # Same instant as interpreting the naive value in local time.
        assert result == naive.astimezone(timezone.utc)

    def test_as_aware_attaches_timezone(self):
        naive = datetime(2026, 1, 1, 12, 0, 0)
        aware = _as_aware(naive)
        assert aware.tzinfo is not None

    def test_as_aware_leaves_aware_unchanged(self):
        aware_in = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
        assert _as_aware(aware_in) is aware_in

    def test_as_aware_none_passthrough(self):
        assert _as_aware(None) is None

    def test_get_free_busy_sends_utc_datetimes(self):
        account = MagicMock()
        captured = {}

        def fake_post(url, data=None):
            captured["data"] = data
            resp = MagicMock()
            resp.json.return_value = {"value": []}
            return resp

        account.connection.post.side_effect = fake_post
        client = OutlookClient(account=account)
        client.get_free_busy(
            ["a@example.com"],
            datetime(2026, 1, 1, 8, 0),
            datetime(2026, 1, 1, 18, 0),
        )
        start_iso = captured["data"]["startTime"]["dateTime"]
        # An offset-aware ISO string is produced (naive was localized + UTC-converted).
        assert "+00:00" in start_iso or start_iso.endswith("Z")


class TestForwardMessageNote:
    """A note prepended to a forwarded HTML body must be HTML-escaped and
    <br>-wrapped (newlines collapse in HTML) with the body type kept aligned;
    a plain-text body keeps its newlines."""

    def _client_with_forward(self, body_type, original="<p>original</p>"):
        account = MagicMock()
        message = MagicMock()
        forward = MagicMock()
        forward.body = original
        forward.body_type = body_type
        message.forward.return_value = forward
        account.mailbox.return_value.get_message.return_value = message
        client = OutlookClient(account=account)
        return client, forward

    def test_html_body_escapes_and_brwraps_note(self):
        client, forward = self._client_with_forward("HTML")
        client.forward_message(
            "id1", ["a@example.com"], body="Line1\nLine2 <tag> & more"
        )
        assert "Line1<br>Line2 &lt;tag&gt; &amp; more" in forward.body
        assert forward.body.endswith("<p>original</p>")
        assert forward.body_type == "HTML"
        forward.send.assert_called_once()

    def test_text_body_keeps_newlines(self):
        client, forward = self._client_with_forward("text", original="original")
        client.forward_message("id1", ["a@example.com"], body="Line1\nLine2")
        assert forward.body.startswith("Line1\nLine2\n\n")
        forward.send.assert_called_once()

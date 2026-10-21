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


class TestCreateEventTimezone:
    """Regression tests for the calendar-create timezone crash.

    create_event previously assigned a NAIVE datetime (or a fixed-offset
    datetime.timezone via _as_aware/.astimezone()) to event.start / event.end.
    The O365 Event start/end setters isinstance-check tzinfo against ZoneInfo
    and raise 'TimeZone data must be set using ZoneInfo objects' otherwise. The
    fix attaches the machine's local zone via tzlocal.get_localzone(), which is
    a real zoneinfo.ZoneInfo.
    """

    def _client_with_event(self):
        account = MagicMock()
        event = MagicMock()
        # Plain attributes so assignment stores the real value for inspection.
        event.start = None
        event.end = None
        event.object_id = "fake-event-id"
        calendar = MagicMock()
        calendar.new_event.return_value = event
        account.schedule.return_value.get_default_calendar.return_value = calendar
        client = OutlookClient(account=account)
        return client, event

    def test_naive_datetime_does_not_raise_and_is_aware(self):
        client, event = self._client_with_event()

        naive = datetime(2027, 1, 1, 9, 0)  # tzinfo is None
        assert naive.tzinfo is None

        # (a) must not raise
        result = client.create_event(
            subject="tz-regression", start_time=naive, duration_minutes=30
        )

        event.save.assert_called_once()

        # (b) the datetime assigned to event.start / event.end is tz-aware
        assert event.start is not None
        assert event.start.tzinfo is not None
        assert event.end is not None
        assert event.end.tzinfo is not None
        assert result["subject"] == "tz-regression"

    def test_daily_recurrence_threads_through(self):
        client, event = self._client_with_event()

        naive = datetime(2026, 7, 5, 8, 0)
        client.create_event(
            subject="daily",
            start_time=naive,
            duration_minutes=15,
            recurrence="daily",
        )

        event.recurrence.set_daily.assert_called_once()
        args, kwargs = event.recurrence.set_daily.call_args
        assert args[0] == 1
        assert kwargs.get("start") == naive.date()


class TestFlagMessage:
    """Regression tests for issue #455.

    flag_message previously did `message.flag = flag_data`, but the O365
    Message.flag property has no setter, so every flag command crashed with
    "property 'flag' of 'Message' object has no setter". The fix drives the
    MessageFlag helper methods (set_flagged / set_completed / delete_flag).
    """

    def _client_with_inbox_message(self):
        account = MagicMock()
        message = MagicMock()
        account.mailbox.return_value.get_message.return_value = message
        client = OutlookClient(account=account)
        return client, message

    def test_flagged_calls_set_flagged(self):
        client, message = self._client_with_inbox_message()
        due = datetime(2027, 1, 2, 9, 0)

        result = client.flag_message("id-1", flag_status="flagged", due_date=due)

        assert result is True
        message.flag.set_flagged.assert_called_once_with(due_date=due)
        message.save_message.assert_called_once()

    def test_complete_calls_set_completed(self):
        client, message = self._client_with_inbox_message()

        client.flag_message("id-1", flag_status="complete")

        message.flag.set_completed.assert_called_once()
        message.save_message.assert_called_once()

    def test_notflagged_calls_delete_flag(self):
        client, message = self._client_with_inbox_message()

        client.flag_message("id-1", flag_status="notFlagged")

        message.flag.delete_flag.assert_called_once()
        message.save_message.assert_called_once()

    def test_unknown_status_raises(self):
        client, message = self._client_with_inbox_message()

        with pytest.raises(ValueError):
            client.flag_message("id-1", flag_status="bogus")


class TestListAttachments:
    """Regression tests for issue #530.

    list_attachments / download_attachment fetched the message without
    download_attachments=True, so message.attachments was always empty and the
    command wrongly reported "No attachments". The fix loads the attachments
    when fetching the message.
    """

    def _client(self):
        account = MagicMock()
        client = OutlookClient(account=account)
        return client, account.mailbox.return_value

    def test_list_attachments_requests_download(self):
        client, mailbox = self._client()
        att = MagicMock()
        att.attachment_id = "att-1"
        att.name = "invite.ics"
        mailbox.get_message.return_value.attachments = [att]

        result = client.list_attachments("msg-1")

        # The message must be fetched WITH attachments loaded.
        _, kwargs = mailbox.get_message.call_args
        assert kwargs.get("download_attachments") is True
        assert len(result) == 1
        assert result[0]["id"] == "att-1"
        assert result[0]["name"] == "invite.ics"

    def test_download_attachment_requests_download(self):
        client, mailbox = self._client()
        att = MagicMock()
        att.attachment_id = "att-1"
        att.name = "invite.ics"
        mailbox.get_message.return_value.attachments = [att]

        client.download_attachment("msg-1", "att-1", "C:/tmp/invite.ics")

        _, kwargs = mailbox.get_message.call_args
        assert kwargs.get("download_attachments") is True
        att.save.assert_called_once_with("C:/tmp/invite.ics")


class TestSingleEventLookupMatchesO365:
    """get/update/delete/respond looked the event up with calendar.get_event(object_id=...),
    a keyword O365 2.1.10 no longer accepts, so all four crashed with a TypeError (#3265).
    The calendar here is autospecced from the real O365 Calendar, so a call the installed
    library would reject fails here too."""

    def _client_with_calendar(self):
        from O365.calendar import Calendar
        from unittest.mock import create_autospec

        account = MagicMock()
        calendar = create_autospec(Calendar, instance=True)
        account.schedule.return_value.get_default_calendar.return_value = calendar
        client = OutlookClient(account=account)
        client._format_event = MagicMock(return_value={"id": "evt-1"})
        return client, calendar

    def test_get_event_passes_id_positionally(self):
        client, calendar = self._client_with_calendar()
        assert client.get_event("evt-1") == {"id": "evt-1"}
        calendar.get_event.assert_called_once_with("evt-1")

    def test_delete_event_looks_up_and_deletes(self):
        client, calendar = self._client_with_calendar()
        assert client.delete_event("evt-1") is True
        calendar.get_event.assert_called_once_with("evt-1")
        calendar.get_event.return_value.delete.assert_called_once_with()

    def test_update_event_looks_up_and_saves(self):
        client, calendar = self._client_with_calendar()
        client.update_event("evt-1", subject="New", body="Body")
        calendar.get_event.assert_called_once_with("evt-1")
        event = calendar.get_event.return_value
        assert event.subject == "New"
        assert event.body == "Body"
        event.save.assert_called_once_with()

    def test_respond_to_event_looks_up_by_id(self):
        client, calendar = self._client_with_calendar()
        client.respond_to_event("evt-1", "accept")
        calendar.get_event.assert_called_once_with("evt-1")

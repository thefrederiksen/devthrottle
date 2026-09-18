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
from pathlib import Path
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


class TestOutgoingAttachments:
    """Regression tests for issue #2526.

    Only `send` could carry an attachment. `draft` could not, so a draft could
    never be built already carrying the file it is about - which is exactly what
    a review-before-send workflow needs; the choice was to send unreviewed or to
    ask a human to drag the file on. `reply --draft` and `forward` shared the gap.

    The ordering assertions matter as much as the attach assertions: attaching
    after save_draft/send would leave the draft in Drafts, or the mail in the
    recipient's inbox, without the attachment.
    """

    def _existing_file(self, tmp_path, name="report.pdf"):
        path = tmp_path / name
        path.write_bytes(b"PDF")
        return str(path)

    def _call_order(self, mock):
        return [name for name, _, _ in mock.mock_calls]

    # --- draft -----------------------------------------------------------

    def test_draft_attaches_before_saving(self, tmp_path):
        client, message = _client_with_message()
        path = self._existing_file(tmp_path)

        client.create_draft(to=["a@example.com"], subject="s", body="b",
                            attachments=[path])

        message.attachments.add.assert_called_once_with(path)
        order = self._call_order(message)
        assert order.index("attachments.add") < order.index("save_draft")

    def test_draft_attaches_every_file_given(self, tmp_path):
        client, message = _client_with_message()
        first = self._existing_file(tmp_path, "one.pdf")
        second = self._existing_file(tmp_path, "two.pdf")

        client.create_draft(to=["a@example.com"], subject="s", body="b",
                            attachments=[first, second])

        assert [c.args[0] for c in message.attachments.add.call_args_list] == [first, second]

    def test_draft_without_attachments_attaches_nothing(self):
        client, message = _client_with_message()

        client.create_draft(to=["a@example.com"], subject="s", body="b")

        message.attachments.add.assert_not_called()
        message.save_draft.assert_called_once()

    def test_draft_missing_attachment_raises_and_saves_no_draft(self, tmp_path):
        client, message = _client_with_message()

        with pytest.raises(FileNotFoundError):
            client.create_draft(to=["a@example.com"], subject="s", body="b",
                                attachments=[str(tmp_path / "nope.pdf")])

        # No half-built draft is left behind in Drafts.
        message.save_draft.assert_not_called()

    # --- reply -----------------------------------------------------------

    def _client_with_reply(self):
        account = MagicMock()
        original = MagicMock()
        account.mailbox.return_value.get_message.return_value = original
        return OutlookClient(account=account), original.reply.return_value

    def test_reply_draft_attaches_before_saving(self, tmp_path):
        client, reply = self._client_with_reply()
        path = self._existing_file(tmp_path)

        client.reply_message("id1", body="b", attachments=[path])

        reply.attachments.add.assert_called_once_with(path)
        order = self._call_order(reply)
        assert order.index("attachments.add") < order.index("save_draft")

    def test_reply_send_attaches_before_sending(self, tmp_path):
        client, reply = self._client_with_reply()
        path = self._existing_file(tmp_path)

        client.reply_message("id1", body="b", send=True, attachments=[path])

        reply.attachments.add.assert_called_once_with(path)
        order = self._call_order(reply)
        assert order.index("attachments.add") < order.index("send")

    def test_reply_missing_attachment_raises_and_sends_nothing(self, tmp_path):
        client, reply = self._client_with_reply()

        with pytest.raises(FileNotFoundError):
            client.reply_message("id1", body="b", send=True,
                                 attachments=[str(tmp_path / "nope.pdf")])

        reply.send.assert_not_called()
        reply.save_draft.assert_not_called()

    # --- forward ---------------------------------------------------------

    def _client_with_forward(self):
        account = MagicMock()
        original = MagicMock()
        account.mailbox.return_value.get_message.return_value = original
        return OutlookClient(account=account), original.forward.return_value

    def test_forward_attaches_before_sending(self, tmp_path):
        client, forward = self._client_with_forward()
        path = self._existing_file(tmp_path)

        client.forward_message("id1", ["a@example.com"], attachments=[path])

        forward.attachments.add.assert_called_once_with(path)
        order = self._call_order(forward)
        assert order.index("attachments.add") < order.index("send")

    def test_forward_missing_attachment_raises_and_sends_nothing(self, tmp_path):
        client, forward = self._client_with_forward()

        with pytest.raises(FileNotFoundError):
            client.forward_message("id1", ["a@example.com"],
                                   attachments=[str(tmp_path / "nope.pdf")])

        forward.send.assert_not_called()

    # --- send (unchanged behavior, guarded through the shared helper) -----

    def test_send_still_attaches(self, tmp_path):
        client, message = _client_with_message()
        path = self._existing_file(tmp_path)

        client.send_message(to=["a@example.com"], subject="s", body="b",
                            attachments=[path])

        message.attachments.add.assert_called_once_with(path)
        order = self._call_order(message)
        assert order.index("attachments.add") < order.index("send")

    def test_send_missing_attachment_raises_and_sends_nothing(self, tmp_path):
        client, message = _client_with_message()

        with pytest.raises(FileNotFoundError):
            client.send_message(to=["a@example.com"], subject="s", body="b",
                                attachments=[str(tmp_path / "nope.pdf")])

        message.send.assert_not_called()


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


class FakeAttachment:
    """Stands in for an O365 Attachment, honoring the save() contract of #2539.

    O365's Attachment.save(location, custom_name) requires location to be an
    EXISTING DIRECTORY. Given anything else it writes nothing and returns False,
    and given no content it returns False as well. A double that accepts any
    arguments and writes nothing cannot tell the fixed code from the broken code,
    which is how the bug survived: the old test asserted only the call shape.
    """

    def __init__(self, attachment_id, name, content=b"CONTENT"):
        self.attachment_id = attachment_id
        self.name = name
        self.content = content
        self.attachment = None
        self.on_disk = False
        self.size = 0
        self.save_calls = []

    def save(self, location=None, custom_name=None):
        self.save_calls.append((location, custom_name))
        if not self.content:
            return False
        location = Path(location or '')
        if not location.exists():
            return False
        # O365 sanitizes the name it is handed.
        name = (custom_name or self.name).replace('/', '-').replace('\\', '')
        try:
            path = location / name
            with path.open('wb') as handle:
                handle.write(self.content)
        except OSError:
            return False
        self.attachment = path
        self.on_disk = True
        self.size = path.stat().st_size
        return True


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

    def test_download_attachment_requests_download(self, tmp_path):
        client, mailbox = self._client()
        att = FakeAttachment("att-1", "invite.ics")
        mailbox.get_message.return_value.attachments = [att]

        client.download_attachment("msg-1", "att-1", str(tmp_path / "invite.ics"))

        _, kwargs = mailbox.get_message.call_args
        assert kwargs.get("download_attachments") is True
        # The directory goes in location and the file name in custom_name. This
        # assertion used to require the whole path in location, which is the
        # shape that silently wrote nothing (issue #2539).
        assert att.save_calls == [(str(tmp_path), "invite.ics")]


class TestDownloadAttachmentWritesTheFile:
    """Regression tests for issue #2539.

    download_attachment passed a full file path as O365's `location`, which wants
    an existing directory. O365 wrote nothing and returned False; the False was
    discarded and the requested path returned as though it had been written, so
    the command printed a green success line and exited 0 having produced no file.

    Every test here is written to FAIL against that old behavior: each one either
    asserts bytes on disk at the reported path, or asserts that a failed write is
    raised rather than reported as success.
    """

    def _client_with_attachment(self, att):
        account = MagicMock()
        client = OutlookClient(account=account)
        account.mailbox.return_value.get_message.return_value.attachments = [att]
        return client

    def test_writes_the_file_and_reports_where_it_wrote_it(self, tmp_path):
        att = FakeAttachment("att-1", "invite.ics", content=b"BEGIN:VCALENDAR")
        client = self._client_with_attachment(att)
        target = tmp_path / "invite.ics"

        result = client.download_attachment("msg-1", "att-1", str(target))

        # The file exists, holds the attachment's bytes, and is where we were told.
        assert target.exists()
        assert target.read_bytes() == b"BEGIN:VCALENDAR"
        assert Path(result["path"]) == target
        assert result["name"] == "invite.ics"
        assert result["size"] == len(b"BEGIN:VCALENDAR")

    def test_bare_file_name_writes_into_the_current_directory(self, tmp_path, monkeypatch):
        # The no -o shape: cli.py passes the attachment's own name, with no
        # directory part. This was broken the same way as the -o shape.
        att = FakeAttachment("att-1", "invite.ics")
        client = self._client_with_attachment(att)
        monkeypatch.chdir(tmp_path)

        result = client.download_attachment("msg-1", "att-1", "invite.ics")

        written = Path(result["path"])
        assert written.exists()
        assert written.read_bytes() == b"CONTENT"
        assert (tmp_path / "invite.ics").exists()

    def test_directory_target_writes_under_the_attachment_name(self, tmp_path):
        att = FakeAttachment("att-1", "invite.ics")
        client = self._client_with_attachment(att)

        result = client.download_attachment("msg-1", "att-1", str(tmp_path))

        assert Path(result["path"]) == tmp_path / "invite.ics"
        assert (tmp_path / "invite.ics").read_bytes() == b"CONTENT"

    def test_reports_the_sanitized_name_o365_actually_used(self, tmp_path):
        # O365 rewrites '/' in a name; the path asked for is then not the path
        # written, and the old code reported the path asked for.
        att = FakeAttachment("att-1", "report/2026.pdf")
        client = self._client_with_attachment(att)

        result = client.download_attachment("msg-1", "att-1", str(tmp_path))

        assert Path(result["path"]) == tmp_path / "report-2026.pdf"
        assert (tmp_path / "report-2026.pdf").exists()

    def test_failed_write_raises_instead_of_reporting_success(self, tmp_path):
        # Empty content makes O365's save() return False. The old code discarded
        # that False and returned a success dict.
        att = FakeAttachment("att-1", "invite.ics", content=b"")
        client = self._client_with_attachment(att)

        with pytest.raises(RuntimeError):
            client.download_attachment("msg-1", "att-1", str(tmp_path / "invite.ics"))

        assert not (tmp_path / "invite.ics").exists()

    def test_missing_output_directory_raises(self, tmp_path):
        att = FakeAttachment("att-1", "invite.ics")
        client = self._client_with_attachment(att)

        with pytest.raises(ValueError, match="Output directory does not exist"):
            client.download_attachment("msg-1", "att-1", str(tmp_path / "nope" / "invite.ics"))

        assert att.save_calls == []

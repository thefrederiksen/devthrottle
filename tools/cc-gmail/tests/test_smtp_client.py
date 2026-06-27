"""Regression tests for cc-gmail SmtpClient.

Covers the BCC privacy leak: smtplib transmits the MIME message verbatim, so a
Bcc header would be delivered to every To/Cc recipient. Bcc must live ONLY in
the SMTP envelope (the recipient list passed to sendmail), never in the headers.
"""

import email
from unittest.mock import patch, MagicMock

from src.smtp_client import SmtpClient


def _run_send(**kwargs):
    """Call send_message with smtplib mocked; return (envelope_recipients, wire_message)."""
    client = SmtpClient("me@gmail.com", "app-password")
    captured = {}

    mock_server = MagicMock()

    def fake_sendmail(from_addr, recipients, message_str):
        captured["from"] = from_addr
        captured["recipients"] = recipients
        captured["message"] = message_str

    mock_server.sendmail.side_effect = fake_sendmail

    cm = MagicMock()
    cm.__enter__.return_value = mock_server
    cm.__exit__.return_value = False

    with patch("src.smtp_client.smtplib.SMTP", return_value=cm):
        client.send_message(**kwargs)

    return captured


class TestBccLeak:
    def test_bcc_not_in_wire_message(self):
        captured = _run_send(
            to="alice@example.com",
            subject="Hello",
            body="Body text",
            cc="carol@example.com",
            bcc="secret@example.com",
        )
        wire = captured["message"]
        # The Bcc recipient must NOT appear anywhere in the transmitted message.
        assert "secret@example.com" not in wire
        assert "Bcc" not in wire
        assert "bcc" not in wire

    def test_bcc_present_in_envelope(self):
        captured = _run_send(
            to="alice@example.com",
            subject="Hello",
            body="Body text",
            cc="carol@example.com",
            bcc="secret@example.com",
        )
        recipients = captured["recipients"]
        # All three classes of recipient must be delivered via the envelope.
        assert "alice@example.com" in recipients
        assert "carol@example.com" in recipients
        assert "secret@example.com" in recipients

    def test_to_and_cc_still_in_headers(self):
        captured = _run_send(
            to="alice@example.com",
            subject="Hello",
            body="Body text",
            cc="carol@example.com",
            bcc="secret@example.com",
        )
        wire = captured["message"]
        # To and Cc are legitimate visible headers and must remain.
        assert "alice@example.com" in wire
        assert "carol@example.com" in wire

    def test_no_bcc_argument_is_fine(self):
        captured = _run_send(
            to="alice@example.com",
            subject="Hello",
            body="Body text",
        )
        assert "alice@example.com" in captured["recipients"]
        assert "Bcc" not in captured["message"]


class TestAttachmentContentDisposition:
    """The Content-Disposition must be built structurally so a non-ASCII or
    spaced filename does not mangle the 'attachment' disposition type."""

    def _send_with_attachment(self, tmp_path, filename):
        attachment = tmp_path / filename
        attachment.write_bytes(b"PDF-DATA")
        captured = _run_send(
            to="alice@example.com",
            subject="Hello",
            body="Body text",
            attachments=[attachment],
        )
        return email.message_from_string(captured["message"])

    def test_non_ascii_filename_keeps_attachment_disposition(self, tmp_path):
        msg = self._send_with_attachment(tmp_path, "r\xe9sum\xe9.pdf")
        part = msg.get_payload()[-1]
        # The disposition type must stay the literal "attachment", not be
        # swallowed into an RFC 2047-encoded blob covering the whole header.
        assert part.get_content_disposition() == "attachment"
        # The filename must be recoverable (RFC 2231 decodes back to the value).
        assert part.get_filename() == "r\xe9sum\xe9.pdf"

    def test_spaced_filename_is_quoted(self, tmp_path):
        msg = self._send_with_attachment(tmp_path, "quarterly report.pdf")
        part = msg.get_payload()[-1]
        assert part.get_content_disposition() == "attachment"
        assert part.get_filename() == "quarterly report.pdf"
        # The raw header must not contain an unquoted, space-broken filename.
        raw = part.get("Content-Disposition")
        assert "filename=quarterly report.pdf" not in raw

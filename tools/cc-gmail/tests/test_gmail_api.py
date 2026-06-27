"""Tests for GmailClient._decode_base64 and _extract_body methods."""

import base64
import email
from email.utils import getaddresses
from unittest.mock import patch, MagicMock

import pytest

from src.gmail_api import GmailClient, _encode_address_header


@pytest.fixture
def gmail_client():
    """Create a GmailClient instance without hitting the Google API."""
    with patch("src.gmail_api.build") as mock_build:
        mock_build.return_value = MagicMock()
        client = GmailClient(credentials=MagicMock())
    return client


# -- _decode_base64 --


class TestDecodeBase64:
    def test_valid_base64_string(self, gmail_client):
        # Encode "Hello, World!" in URL-safe base64
        original = "Hello, World!"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        # Strip padding to simulate Gmail behavior
        encoded = encoded.rstrip("=")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_base64_with_missing_padding(self, gmail_client):
        # "Test" -> base64url = "VGVzdA==" -> strip padding -> "VGVzdA"
        original = "Test"
        encoded_no_padding = "VGVzdA"
        result = gmail_client._decode_base64(encoded_no_padding)
        assert result == original

    def test_base64_already_padded(self, gmail_client):
        original = "Already padded"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_base64_with_url_safe_chars(self, gmail_client):
        # Content that produces + and / in standard base64 uses - and _ in urlsafe
        original = "data with special bytes: \xff\xfe"
        encoded = base64.urlsafe_b64encode(original.encode("utf-8")).decode("utf-8")
        encoded = encoded.rstrip("=")
        result = gmail_client._decode_base64(encoded)
        assert result == original

    def test_empty_content(self, gmail_client):
        encoded = base64.urlsafe_b64encode(b"").decode("utf-8")
        result = gmail_client._decode_base64(encoded)
        assert result == ""


# -- _extract_body --


class TestExtractBody:
    def test_simple_text_plain_payload(self, gmail_client):
        """Payload with body.data directly (no parts)."""
        text = "This is the email body."
        encoded = base64.urlsafe_b64encode(text.encode("utf-8")).decode("utf-8")
        payload = {
            "mimeType": "text/plain",
            "body": {"data": encoded, "size": len(text)},
        }
        result = gmail_client._extract_body(payload)
        assert result == text

    def test_multipart_text_plain_and_html(self, gmail_client):
        """Multipart payload with text/plain and text/html -- should prefer text/plain."""
        plain_text = "Plain text version"
        html_text = "<p>HTML version</p>"
        plain_encoded = base64.urlsafe_b64encode(
            plain_text.encode("utf-8")
        ).decode("utf-8")
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/plain",
                    "body": {"data": plain_encoded, "size": len(plain_text)},
                },
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == plain_text

    def test_nested_multipart(self, gmail_client):
        """Nested multipart/mixed containing multipart/alternative with text/plain."""
        plain_text = "Nested plain text"
        plain_encoded = base64.urlsafe_b64encode(
            plain_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/mixed",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "multipart/alternative",
                    "body": {"size": 0},
                    "parts": [
                        {
                            "mimeType": "text/plain",
                            "body": {
                                "data": plain_encoded,
                                "size": len(plain_text),
                            },
                        },
                    ],
                },
                {
                    "mimeType": "application/pdf",
                    "filename": "attachment.pdf",
                    "body": {"attachmentId": "att123", "size": 5000},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == plain_text

    def test_html_only_fallback(self, gmail_client):
        """When no text/plain part exists, should fall back to text/html."""
        html_text = "<p>Only HTML available</p>"
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == html_text

    def test_empty_payload(self, gmail_client):
        """Payload with no body data and no parts returns empty string."""
        payload = {
            "mimeType": "text/plain",
            "body": {"size": 0},
        }
        result = gmail_client._extract_body(payload)
        assert result == ""

    def test_multipart_with_empty_text_plain(self, gmail_client):
        """text/plain part exists but has no data -- should fall back to HTML."""
        html_text = "<p>HTML fallback</p>"
        html_encoded = base64.urlsafe_b64encode(
            html_text.encode("utf-8")
        ).decode("utf-8")

        payload = {
            "mimeType": "multipart/alternative",
            "body": {"size": 0},
            "parts": [
                {
                    "mimeType": "text/plain",
                    "body": {"size": 0},
                },
                {
                    "mimeType": "text/html",
                    "body": {"data": html_encoded, "size": len(html_text)},
                },
            ],
        }
        result = gmail_client._extract_body(payload)
        assert result == html_text


# -- _encode_address_header --


class TestEncodeAddressHeader:
    def test_non_ascii_name_keeps_addr_spec_and_is_ascii(self):
        encoded = _encode_address_header("Jos\xe9 <jose@example.com>, plain@example.com")
        # Pure ASCII output (house rule) - the phrase is RFC 2047-encoded.
        encoded.encode("ascii")
        addrs = [addr for _, addr in getaddresses([encoded])]
        assert "jose@example.com" in addrs
        assert "plain@example.com" in addrs

    def test_empty_passthrough(self):
        assert _encode_address_header("") == ""


# -- send path: To/Cc headers must not blob the addresses --


def _capture_sent_raw(gmail_client):
    captured = {}

    def fake_send(userId, body):
        captured["raw"] = body["raw"]
        result = MagicMock()
        result.execute.return_value = {"id": "sent-1"}
        return result

    gmail_client.service.users().messages().send.side_effect = fake_send
    return captured


def _decode_raw(raw):
    return email.message_from_bytes(base64.urlsafe_b64decode(raw))


class TestSendMessageAddressHeaders:
    def test_non_ascii_display_name_does_not_corrupt_to_header(self, gmail_client):
        captured = _capture_sent_raw(gmail_client)
        gmail_client.send_message(
            to="Jos\xe9 <jose@example.com>, plain@example.com",
            subject="Hi",
            body="Body",
        )
        msg = _decode_raw(captured["raw"])
        to_header = msg["to"]
        # The header must be ASCII and the addresses must remain parseable.
        to_header.encode("ascii")
        addrs = [addr for _, addr in getaddresses([to_header])]
        assert "jose@example.com" in addrs
        assert "plain@example.com" in addrs

    def test_cc_header_encoded_too(self, gmail_client):
        captured = _capture_sent_raw(gmail_client)
        gmail_client.send_message(
            to="a@example.com",
            subject="Hi",
            body="Body",
            cc="M\xfcller <muller@example.com>",
        )
        msg = _decode_raw(captured["raw"])
        cc_header = msg["cc"]
        cc_header.encode("ascii")
        addrs = [addr for _, addr in getaddresses([cc_header])]
        assert "muller@example.com" in addrs


class TestSendMessageAttachmentDisposition:
    def test_non_ascii_attachment_filename_keeps_disposition(self, gmail_client, tmp_path):
        captured = _capture_sent_raw(gmail_client)
        attachment = tmp_path / "r\xe9sum\xe9.pdf"
        attachment.write_bytes(b"PDF-DATA")
        gmail_client.send_message(
            to="a@example.com",
            subject="Hi",
            body="Body",
            attachments=[attachment],
        )
        msg = _decode_raw(captured["raw"])
        part = msg.get_payload()[-1]
        assert part.get_content_disposition() == "attachment"
        assert part.get_filename() == "r\xe9sum\xe9.pdf"

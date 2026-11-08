"""Turning raw messages into the records dt-email prints: a summary row, a full read, and attachments."""

from __future__ import annotations

import html
import mimetypes
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from email.message import EmailMessage
from email.utils import parsedate_to_datetime
from typing import Iterator, List, Optional

from .compose import parse
from .imap_client import FetchItem

SUMMARY_FIELDS = ["uid", "date", "received", "from", "to", "cc", "subject", "message_id", "flags", "unread",
                  "flagged", "size"]

#: How much of the text a preview (dt-email list --preview) carries.
PREVIEW_CHARS = 200

#: How much of each message a preview reads: the text and the headers of the first parts, never a whole attachment.
PREVIEW_BYTES = 65536
SUMMARY_DEFAULT = ["uid", "date", "from", "subject"]


def _date(header: Optional[str], internal: Optional[str]) -> Optional[str]:
    """The Date header as ISO 8601 to the minute; the server's arrival time when the header is unreadable."""
    if header:
        try:
            return parsedate_to_datetime(str(header)).isoformat(timespec="minutes")
        except (TypeError, ValueError):
            pass
    if internal:
        return datetime.strptime(internal, "%d-%b-%Y %H:%M:%S %z").isoformat(timespec="minutes")
    return str(header) if header else None


def summary(item: FetchItem) -> dict:
    headers = parse(item.literal or b"")
    flags = item.flags()
    return {
        "uid": item.uid,
        "date": _date(headers.get("Date"), item.internal_date()),
        "received": received(item.internal_date()),
        "from": str(headers.get("From", "")) or None,
        "to": str(headers.get("To", "")) or None,
        "cc": str(headers.get("Cc", "")) or None,
        "subject": str(headers.get("Subject", "")),
        "message_id": str(headers.get("Message-ID", "")).strip() or None,
        "flags": " ".join(flags),
        "unread": "\\Seen" not in flags,
        "flagged": "\\Flagged" in flags,
        "size": item.size(),
    }


def received(internal: Optional[str]) -> Optional[str]:
    """When the server received the message (its INTERNALDATE), in UTC, to the second: 2026-11-07T22:31:20Z. Unlike
    the Date header it is the server's own clock, so it orders messages the way they arrived."""
    if not internal:
        return None
    moment = datetime.strptime(internal, "%d-%b-%Y %H:%M:%S %z").astimezone(timezone.utc)
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def preview(raw: bytes) -> dict:
    """The first PREVIEW_CHARS characters of a message's text, its spaces collapsed, and how many attachments it has.
    `raw` may be only the start of the message (PREVIEW_BYTES): attachments are then counted among the parts that
    start within it.
    The plain text part is preferred; an HTML-only message has its tags and entities taken out. A message with no text
    part at all (a calendar invitation) has an empty preview."""
    message = parse(raw)
    body_type, text = body(message, prefer_html=False)
    if body_type == "text/html":
        text = html.unescape(re.sub(r"(?is)<(script|style)\b.*?</\1>|<[^>]+>", " ", text))
    return {"preview": " ".join((text or "").split())[:PREVIEW_CHARS], "attachments": len(attachments(message))}


@dataclass
class Attachment:
    index: int
    filename: str
    content_type: str
    part: EmailMessage

    def data(self) -> bytes:
        if self.content_type == "message/rfc822":
            inner = self.part.get_content()
            return inner.as_bytes()
        payload = self.part.get_payload(decode=True)
        return payload if payload is not None else b""

    def record(self) -> dict:
        return {"index": self.index, "filename": self.filename, "type": self.content_type, "size": len(self.data())}


def _leaf_parts(part: EmailMessage) -> Iterator[EmailMessage]:
    """Every leaf part. An attached message (message/rfc822) is one leaf, not opened up."""
    if part.get_content_type() == "message/rfc822":
        yield part
    elif part.is_multipart():
        for sub in part.iter_parts():
            yield from _leaf_parts(sub)
    else:
        yield part


def _safe_name(name: str) -> str:
    name = re.split(r"[\\/]", name)[-1].strip()
    name = re.sub(r'[<>:"|?*\x00-\x1f]', "_", name)
    return name if name not in ("", ".", "..") else "_"


def attachments(message: EmailMessage) -> List[Attachment]:
    found: List[Attachment] = []
    for part in _leaf_parts(message):
        if part is message:
            continue
        filename = part.get_filename()
        disposition = part.get_content_disposition()
        ctype = part.get_content_type()
        if filename is None and disposition != "attachment" and ctype != "message/rfc822":
            continue
        index = len(found) + 1
        if filename is None:
            extension = ".eml" if ctype == "message/rfc822" else (mimetypes.guess_extension(ctype) or ".bin")
            filename = f"attachment-{index}{extension}"
        found.append(Attachment(index=index, filename=_safe_name(filename), content_type=ctype, part=part))
    return found


def body(message: EmailMessage, prefer_html: bool) -> tuple[Optional[str], str]:
    """(content type, text) of the body to show."""
    order = ("html", "plain") if prefer_html else ("plain", "html")
    part = message.get_body(preferencelist=order)
    if part is None:
        return None, ""
    return part.get_content_type(), part.get_content()


def full_record(uid: int, folder: str, flags: List[str], raw: bytes, prefer_html: bool) -> dict:
    message = parse(raw)
    body_type, text = body(message, prefer_html)
    return {
        "uid": uid,
        "folder": folder,
        "from": str(message.get("From", "")) or None,
        "to": str(message.get("To", "")) or None,
        "cc": str(message.get("Cc", "")) or None,
        "bcc": str(message.get("Bcc", "")) or None,
        "reply_to": str(message.get("Reply-To", "")) or None,
        "date": _date(message.get("Date"), None),
        "subject": str(message.get("Subject", "")),
        "message_id": str(message.get("Message-ID", "")).strip() or None,
        "in_reply_to": str(message.get("In-Reply-To", "")).strip() or None,
        "references": str(message.get("References", "")).strip() or None,
        "flags": " ".join(flags),
        "attachments": [a.record() for a in attachments(message)],
        "body_type": body_type,
        "body": text,
        "size": len(raw),
    }

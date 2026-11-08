"""Building messages: a new one, a reply, a reply to all, and a forward that keeps the thread.

Threading is the client's job (servers like Zoho offer no THREAD extension): a reply carries
In-Reply-To = the original's Message-ID and References = the original's References plus its Message-ID;
a forward carries References and attaches the original whole, as message/rfc822.
"""

from __future__ import annotations

import email
import email.policy
import html as html_lib
import mimetypes
import re
from email.message import EmailMessage
from email.utils import formatdate, getaddresses, make_msgid, parseaddr
from pathlib import Path
from typing import List, Optional, Sequence

from .errors import DtEmailError


def parse(raw: bytes) -> EmailMessage:
    return email.message_from_bytes(raw, policy=email.policy.default)


def addresses(*values: Optional[Sequence[str] | str]) -> List[str]:
    """Plain addresses from header values or lists of them, in order, without repeats."""
    flat: List[str] = []
    for value in values:
        if value is None:
            continue
        if isinstance(value, str):
            flat.append(value)
        else:
            flat.extend(str(v) for v in value)
    seen: List[str] = []
    for _, addr in getaddresses(flat):
        addr = addr.strip()
        if addr and addr.lower() not in [s.lower() for s in seen]:
            seen.append(addr)
    return seen


def recipients_of(message: EmailMessage) -> List[str]:
    return addresses(message.get_all("To"), message.get_all("Cc"), message.get_all("Bcc"))


def _domain(address: str) -> str:
    plain = parseaddr(address)[1]
    if "@" not in plain:
        raise DtEmailError(f"'{address}' is not an email address.", exit_code=2)
    return plain.split("@", 1)[1]


def _set_body(msg: EmailMessage, text: Optional[str], html: Optional[str]) -> None:
    if text is None and html is None:
        raise DtEmailError("the message has no body. Pass --body, --body-file or --html-file.", exit_code=2)
    if text is not None:
        msg.set_content(text)
        if html is not None:
            msg.add_alternative(html, subtype="html")
    else:
        msg.set_content(html, subtype="html")


def _attach_files(msg: EmailMessage, files: Sequence[str]) -> None:
    for name in files:
        path = Path(name)
        if not path.is_file():
            raise DtEmailError(f"attachment {path} does not exist or is not a file.", exit_code=2)
        ctype, encoding = mimetypes.guess_type(path.name)
        if ctype is None or encoding is not None:
            ctype = "application/octet-stream"
        maintype, subtype = ctype.split("/", 1)
        msg.add_attachment(path.read_bytes(), maintype=maintype, subtype=subtype, filename=path.name)


def _headers(msg: EmailMessage, from_addr: str, to: Sequence[str], cc: Sequence[str], bcc: Sequence[str],
             subject: str) -> None:
    msg["From"] = from_addr
    if to:
        msg["To"] = ", ".join(to)
    if cc:
        msg["Cc"] = ", ".join(cc)
    if bcc:
        msg["Bcc"] = ", ".join(bcc)
    msg["Subject"] = subject
    msg["Date"] = formatdate(localtime=True)
    msg["Message-ID"] = make_msgid(domain=_domain(from_addr))


def new_message(from_addr: str, to: Sequence[str], cc: Sequence[str], bcc: Sequence[str], subject: str,
                text: Optional[str], html: Optional[str], attachments: Sequence[str]) -> EmailMessage:
    msg = EmailMessage()
    _headers(msg, from_addr, to, cc, bcc, subject)
    _set_body(msg, text, html)
    _attach_files(msg, attachments)
    return msg


def _prefixed(prefix: str, subject: str) -> str:
    subject = subject or ""
    if subject.lower().startswith(prefix.lower()):
        return subject
    return f"{prefix} {subject}".rstrip()


def _references(original: EmailMessage) -> Optional[str]:
    mid = original.get("Message-ID")
    if not mid:
        return original.get("References")
    return " ".join(x for x in (original.get("References", "").strip(), mid.strip()) if x)


def _original_text(original: EmailMessage) -> str:
    part = original.get_body(preferencelist=("plain", "html"))
    if part is None:
        return ""
    content = part.get_content()
    if part.get_content_type() == "text/html":
        return html_lib.unescape(re.sub(r"<[^>]+>", "", content))
    return content


def reply_recipients(original: EmailMessage, own: Sequence[str], reply_all: bool) -> tuple[List[str], List[str]]:
    """(to, cc) for a reply.

    To is the Reply-To, else the From. When that is this account itself (a reply to a message it sent),
    To is the original's To instead, as a mail program does. Reply-all adds the rest of the original To
    and CC. The account's own addresses are never a recipient."""
    own_lower = {o.lower() for o in own}

    def not_own(found: List[str]) -> List[str]:
        return [a for a in found if a.lower() not in own_lower]

    sender = addresses(original.get_all("Reply-To") or original.get_all("From"))
    if sender and not not_own(sender):
        to = not_own(addresses(original.get_all("To")))
        pool = addresses(original.get_all("Cc"))
    else:
        to = not_own(sender)
        pool = addresses(original.get_all("To"), original.get_all("Cc"))
    if not to:
        raise DtEmailError("cannot tell whom to reply to: the original has no sender or recipient other than this "
                           "account. Use dt-email send instead.", exit_code=2)
    if not reply_all:
        return to, []
    to_lower = {t.lower() for t in to}
    return to, [a for a in not_own(pool) if a.lower() not in to_lower]


def reply(original: EmailMessage, from_addr: str, own: Sequence[str], reply_all: bool, text: Optional[str],
          html: Optional[str], attachments: Sequence[str]) -> EmailMessage:
    to, cc = reply_recipients(original, own, reply_all)
    msg = EmailMessage()
    _headers(msg, from_addr, to, cc, [], _prefixed("Re:", original.get("Subject", "")))
    if original.get("Message-ID"):
        msg["In-Reply-To"] = original["Message-ID"].strip()
    refs = _references(original)
    if refs:
        msg["References"] = refs
    quoted_from = original.get("From", "")
    quoted_date = original.get("Date", "")
    old = _original_text(original)
    header_line = f"On {quoted_date}, {quoted_from} wrote:"
    quoted = "\n".join("> " + line for line in old.splitlines())
    body_text = None if text is None else f"{text}\n\n{header_line}\n{quoted}\n"
    body_html = None
    if html is not None:
        body_html = (f"{html}\n<p>{html_lib.escape(header_line)}</p>\n<blockquote>"
                     f"<pre>{html_lib.escape(old)}</pre></blockquote>")
    _set_body(msg, body_text, body_html)
    _attach_files(msg, attachments)
    return msg


def forward(original: EmailMessage, from_addr: str, to: Sequence[str], cc: Sequence[str], bcc: Sequence[str],
            text: Optional[str], html: Optional[str], attachments: Sequence[str]) -> EmailMessage:
    msg = EmailMessage()
    _headers(msg, from_addr, to, cc, bcc, _prefixed("Fwd:", original.get("Subject", "")))
    refs = _references(original)
    if refs:
        msg["References"] = refs
    if text is None and html is None:
        text = ""
    _set_body(msg, text, html)
    msg.add_attachment(original)
    _attach_files(msg, attachments)
    return msg

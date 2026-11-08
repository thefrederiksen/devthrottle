"""Reading mail through Zoho's Mail API: accounts, folders, labels, message lists, search, one message, attachments.

Every call and quirk here was run against soren@devthrottle.com on 8 November 2026 (proof\\prover-a.md):
- the account list is `GET /api/accounts`;
- list fields come HTML-escaped (`&lt;a@b.c&gt;`) and an empty CC is the literal text "Not Provided";
- times are milliseconds since 1970 as strings; `status` is "1" for read and "0" for unread;
- search ignores `is:flagged` and `is:unread` silently, so dt-zoho refuses them and filters by flag instead;
- Zoho rewrites the line endings of text attachments (LF becomes CRLF); binary files come back byte for byte.
"""

from __future__ import annotations

import html
import re
from datetime import datetime, timezone
from html.parser import HTMLParser
from typing import Any, Dict, List, Optional

from .client import ZohoClient
from .errors import DtZohoError

FLAGS = ("info", "important", "followup", "flag_not_set")
SUMMARY_FIELDS = ("id", "folder_id", "thread_id", "received", "from", "to", "cc", "subject", "read", "flag",
                  "attachments", "size", "summary")
SUMMARY_DEFAULT = ("id", "received", "from", "subject", "read", "flag")
# A list that spans folders shows each message's folder, which `read` needs.
SUMMARY_DEFAULT_ACROSS_FOLDERS = ("id", "folder_id", "received", "from", "subject", "read", "flag")
IGNORED_SEARCH_TERMS = {"is:unread": "--unread", "is:read": "--read", "is:flagged": "--flagged"}
NOT_PROVIDED = "Not Provided"


# --------------------------------------------------------------------------------------------- accounts


def accounts(client: ZohoClient) -> List[dict]:
    return client.mail("GET", "/api/accounts")["data"]


def account_record(raw: dict) -> dict:
    addresses = [a.get("mailId") for a in raw.get("emailAddress") or [] if a.get("mailId")]
    primary = next((a.get("mailId") for a in raw.get("emailAddress") or [] if a.get("isPrimary")), None)
    return {"account_id": str(raw.get("accountId")), "email": primary or raw.get("incomingUserName"),
            "name": raw.get("firstName") or raw.get("displayName"), "default": bool(raw.get("isDefaultAccount")),
            "zuid": str(raw.get("zuid")) if raw.get("zuid") is not None else None,
            "imap_enabled": raw.get("imapAccessEnabled"), "time_zone": raw.get("timeZone"),
            "addresses": addresses,
            "aliases": [a.get("mailId") for a in raw.get("emailAddress") or [] if a.get("isAlias") and a.get("mailId")],
            "zoid": str((raw.get("policyId") or {}).get("zoid")) if (raw.get("policyId") or {}).get("zoid") else None}


def pick_account(client: ZohoClient, email: Optional[str]) -> dict:
    """The account whose addresses include `email`, or the default one. Never guesses between several."""
    records = [account_record(a) for a in accounts(client)]
    if not records:
        raise DtZohoError("Zoho lists no mail account for this key.")
    if email is not None:
        wanted = email.strip().lower()
        for record in records:
            if wanted == (record["email"] or "").lower() or wanted in [a.lower() for a in record["addresses"]]:
                return record
        raise DtZohoError(f"no Zoho mail account has the address {email}. Accounts: "
                          f"{', '.join(r['email'] or r['account_id'] for r in records)}. See them with: dt-zoho accounts")
    defaults = [r for r in records if r["default"]]
    if len(defaults) == 1:
        return defaults[0]
    if len(records) == 1:
        return records[0]
    raise DtZohoError(f"this key reaches several mail accounts ({', '.join(r['email'] for r in records)}) and "
                      "none is marked default. Pass --account <address>.")


# --------------------------------------------------------------------------------------------- folders and labels


def folders(client: ZohoClient, account_id: str) -> List[dict]:
    return client.mail("GET", f"/api/accounts/{account_id}/folders")["data"]


def folder_record(raw: dict) -> dict:
    return {"id": str(raw.get("folderId")), "name": raw.get("folderName"), "path": raw.get("path"),
            "type": raw.get("folderType"), "imap_view": raw.get("imapAccess"),
            "parent_id": str(raw["parentFolderId"]) if raw.get("parentFolderId") else None}


def resolve_folder(client: ZohoClient, account_id: str, folder: str) -> dict:
    """A folder by its id, its path (/Inbox/Sub) or its name. A name two folders share must be given as a path."""
    records = [folder_record(f) for f in folders(client, account_id)]
    wanted = folder.strip()
    for record in records:
        if wanted == record["id"]:
            return record
    lowered = wanted.lower()
    if "/" in lowered:
        by_path = [r for r in records if (r["path"] or "").lower() == "/" + lowered.strip("/")]
        if len(by_path) == 1:
            return by_path[0]
        raise DtZohoError(f"no folder with the path '{folder}'. See the folders with: dt-zoho folders", exit_code=2)
    by_name = [r for r in records if (r["name"] or "").lower() == lowered]
    if len(by_name) == 1:
        return by_name[0]
    if len(by_name) > 1:
        raise DtZohoError(f"several folders are named '{folder}': {', '.join(r['path'] for r in by_name)}. "
                          "Pass its path or its id instead.", exit_code=2)
    raise DtZohoError(f"no folder '{folder}'. See the folders with: dt-zoho folders", exit_code=2)


def labels(client: ZohoClient, account_id: str) -> List[dict]:
    return client.mail("GET", f"/api/accounts/{account_id}/labels")["data"]


def label_record(raw: dict) -> dict:
    return {"id": str(raw.get("labelId")), "name": raw.get("displayName"), "color": raw.get("color")}


def resolve_label(client: ZohoClient, account_id: str, label: str) -> dict:
    records = [label_record(l) for l in labels(client, account_id)]
    for record in records:
        if label == record["id"] or label.lower() == (record["name"] or "").lower():
            return record
    raise DtZohoError(f"no label '{label}'. See the labels with: dt-zoho labels", exit_code=2)


# --------------------------------------------------------------------------------------------- messages


def _addresses(value: Any) -> str:
    if not value or value == NOT_PROVIDED:
        return ""
    return html.unescape(str(value)).strip()


def _time(value: Any) -> Optional[str]:
    if value in (None, ""):
        return None
    try:
        millis = int(value)
    except (TypeError, ValueError):
        return str(value)
    return datetime.fromtimestamp(millis / 1000, tz=timezone.utc).astimezone().isoformat(timespec="seconds")


def summary_record(raw: dict) -> dict:
    sender = raw.get("fromAddress") or ""
    name = raw.get("sender")
    shown_from = html.unescape(f"{name} <{sender}>" if name and name != sender else sender)
    return {"id": str(raw.get("messageId")), "folder_id": str(raw.get("folderId")) if raw.get("folderId") else None,
            "thread_id": str(raw.get("threadId")) if raw.get("threadId") else None,
            "received": _time(raw.get("receivedTime")), "from": shown_from, "to": _addresses(raw.get("toAddress")),
            "cc": _addresses(raw.get("ccAddress")), "subject": html.unescape(raw.get("subject") or ""),
            "read": str(raw.get("status")) == "1", "flag": raw.get("flagid"),
            "attachments": str(raw.get("hasAttachment")) == "1", "size": raw.get("size"),
            "summary": html.unescape(raw.get("summary") or "").strip()}


def flag_matches(record: dict, flagged: bool) -> bool:
    return not flagged or (record["flag"] not in (None, "", "flag_not_set"))


def read_matches(record: dict, unread: bool, read: bool) -> bool:
    return not (unread and record["read"]) and not (read and not record["read"])


def list_messages(client: ZohoClient, account_id: str, folder_id: Optional[str], start: int, limit: int,
                  status: str, label_id: Optional[str], thread_id: Optional[str],
                  include_archive: bool) -> List[dict]:
    query: Dict[str, Any] = {"start": start, "limit": limit, "status": status, "includeto": "true"}
    if folder_id:
        query["folderId"] = folder_id
    if label_id:
        query["labelid"] = label_id
    if thread_id:
        query["threadId"] = thread_id
    if include_archive:
        query["includearchive"] = "true"
    data = client.mail("GET", f"/api/accounts/{account_id}/messages/view", query=query)
    return [summary_record(m) for m in data.get("data") or []]


def check_search_key(key: str) -> None:
    """Refuse the search words Zoho ignores without saying so, naming the option that does it instead."""
    # Terms are joined by "::" (and) or "::or:" (or), so each part is a term, with "or:" in front for an OR.
    terms = {part.strip()[3:] if part.strip().startswith("or:") else part.strip()
             for part in key.lower().split("::")}
    for term, option in IGNORED_SEARCH_TERMS.items():
        if term in terms:
            raise DtZohoError(f"Zoho's search silently ignores '{term}' (it returns the same messages without it). "
                              f"Leave it out and pass {option} instead.", exit_code=2)


def search(client: ZohoClient, account_id: str, key: str, start: int, limit: int) -> List[dict]:
    check_search_key(key)
    data = client.mail("GET", f"/api/accounts/{account_id}/messages/search",
                       query={"searchKey": key, "start": start, "limit": limit})
    return [summary_record(m) for m in data.get("data") or []]


def details(client: ZohoClient, account_id: str, folder_id: str, message_id: str) -> dict:
    return client.mail("GET", f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}/details")["data"]


def content(client: ZohoClient, account_id: str, folder_id: str, message_id: str) -> str:
    data = client.mail("GET", f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}/content")
    return data["data"].get("content") or ""


def header(client: ZohoClient, account_id: str, folder_id: str, message_id: str) -> str:
    data = client.mail("GET", f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}/header")
    return data["data"].get("headerContent") or ""


def original(client: ZohoClient, account_id: str, message_id: str) -> str:
    data = client.mail("GET", f"/api/accounts/{account_id}/messages/{message_id}/originalmessage")
    return data["data"].get("content") or ""


def attachment_info(client: ZohoClient, account_id: str, folder_id: str, message_id: str) -> List[dict]:
    data = client.mail("GET", f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}/attachmentinfo")
    inner = data.get("data") or {}
    # A message can carry an attachment with no file name; it is named by its id, so it can still be saved.
    return [{"id": str(a.get("attachmentId")),
             "name": a.get("attachmentName") or f"attachment-{a.get('attachmentId')}",
             "size": a.get("attachmentSize")}
            for a in inner.get("attachments") or []]


def attachment_bytes(client: ZohoClient, account_id: str, folder_id: str, message_id: str,
                     attachment_id: str) -> bytes:
    return client.mail_bytes(
        f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}/attachments/{attachment_id}")


# --------------------------------------------------------------------------------------------- body text


class _TextOf(HTMLParser):
    BREAKS = {"br", "p", "div", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "table"}
    SKIP = {"script", "style", "head", "title"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts: List[str] = []
        self.skipping = 0

    def handle_starttag(self, tag, attrs):
        if tag in self.SKIP:
            self.skipping += 1
        elif tag in self.BREAKS:
            self.parts.append("\n")

    def handle_startendtag(self, tag, attrs):
        if tag == "br":
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if tag in self.SKIP:
            self.skipping = max(0, self.skipping - 1)
        elif tag in self.BREAKS and tag != "br":
            self.parts.append("\n")

    def handle_data(self, data):
        if not self.skipping:
            self.parts.append(data)


def html_to_text(markup: str) -> str:
    """Readable text from Zoho's HTML body: tags dropped, block ends and <br> as line breaks."""
    parser = _TextOf()
    parser.feed(markup.replace("\r", ""))
    parser.close()
    text = "".join(parser.parts).replace("\xa0", " ")
    lines = [line.rstrip() for line in text.split("\n")]
    return re.sub(r"\n{3,}", "\n\n", "\n".join(lines)).strip()

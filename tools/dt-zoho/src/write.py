"""Writing mail through Zoho's Mail API: send, reply, drafts, marks, moves, delete, spam, labels, folders, threads.

Every call here was run against soren@devthrottle.com on 8 November 2026 (proof\\prover-a.md, the dt-zoho
tables and "the exact call for every cell"):
- a send is `POST /api/accounts/{a}/messages`; an attachment is uploaded first, as raw bytes, to
  `/messages/attachments?fileName=`, and the send carries what the upload answered;
- a reply is `POST /api/accounts/{a}/messages/{m}` with `action=reply`, or the undocumented `action=replyall`,
  and both keep the thread (In-Reply-To and References name the original);
- `action=forward` answers 500 and sends nothing: Zoho's API cannot forward;
- a draft is a send with `mode=draft`; Zoho has no call to change or to send a saved draft;
- `unArchiveMails` answers 200 and leaves the message in Archive, so unarchive is a move out of Archive;
- a scheduled send is `isSchedule` with `scheduleType 6`, a `timeZone` and `scheduleTime` "MM/DD/YYYY HH:MM:SS";
  there is no call to list or cancel one;
- delete moves to Trash, and `expunge=true` removes for good.
"""

from __future__ import annotations

from datetime import datetime
from email import policy
from email.parser import BytesParser
from email.utils import getaddresses
from pathlib import Path
from typing import Dict, List, Optional, Sequence

from .client import ZohoClient
from .errors import DtZohoError

# What `mark --as` takes, and the updatemessage / updatethread body each one sends.
MARKS: Dict[str, dict] = {
    "read": {"mode": "markAsRead"},
    "unread": {"mode": "markAsUnread"},
    "important": {"mode": "setFlag", "flagid": "important"},
    "info": {"mode": "setFlag", "flagid": "info"},
    "followup": {"mode": "setFlag", "flagid": "followup"},
    "unflagged": {"mode": "setFlag", "flagid": "flag_not_set"},
}
SCHEDULE_FORMAT = "%Y-%m-%d %H:%M"
FORWARD_REFUSAL = ("Zoho's API cannot forward: it answers 500 Internal Error and sends nothing. "
                   "Forward with dt-email instead: dt-email forward <uid> --to <address>")
FILTERS_REFUSAL = ("Zoho's API has no filters: there is no call to create, list, change or delete one. "
                   "Filters are made in Zoho Mail's web settings.")


# --------------------------------------------------------------------------------------------- addresses


def split_addresses(values: Optional[Sequence[str]]) -> List[str]:
    """Addresses from repeated options, each of which may itself hold several, comma separated."""
    if not values:
        return []
    found = [a.strip() for _, a in getaddresses(list(values)) if a.strip()]
    for a in found:
        if "@" not in a:
            raise DtZohoError(f"'{a}' is not an email address.", exit_code=2)
    return found


def split_ids(values: Sequence[str]) -> List[str]:
    """Message, thread or other ids from arguments that may be space or comma separated."""
    ids = [v.strip() for value in values for v in value.split(",") if v.strip()]
    for i in ids:
        if not i.isdigit():
            raise DtZohoError(f"'{i}' is not a Zoho id (ids are numbers; see dt-zoho list).", exit_code=2)
    if not ids:
        raise DtZohoError("no id given.", exit_code=2)
    return ids


def reply_recipients(original_mime: str, own: Sequence[str], reply_all: bool) -> tuple[List[str], List[str]]:
    """(to, cc) for a reply, from the original message as Zoho stores it.

    To is the Reply-To, else the From. When that is this account itself (a reply to a message it sent), To is
    the original's To instead, as a mail program does. Reply-all adds the rest of the original To and CC. The
    account's own addresses are never a recipient."""
    message = BytesParser(policy=policy.default).parsebytes(original_mime.encode("utf-8"))
    own_lower = {o.lower() for o in own}

    def found(*headers: str) -> List[str]:
        values = [str(v) for h in headers for v in (message.get_all(h) or [])]
        return [a for _, a in getaddresses(values) if a]

    def not_own(addresses: List[str]) -> List[str]:
        return [a for a in addresses if a.lower() not in own_lower]

    sender = found("Reply-To") or found("From")
    if sender and not not_own(sender):
        to = not_own(found("To"))
        pool = found("Cc")
    else:
        to = not_own(sender)
        pool = found("From", "To", "Cc")
    if not to:
        raise DtZohoError("cannot tell whom to reply to: the original has no sender or recipient other than this "
                          "account. Use dt-zoho send instead.", exit_code=2)
    if not reply_all:
        return to, []
    to_lower = {t.lower() for t in to}
    seen: set = set()
    cc = []
    for a in not_own(pool):
        if a.lower() not in to_lower and a.lower() not in seen:
            seen.add(a.lower())
            cc.append(a)
    return to, cc


def original_subject(original_mime: str) -> str:
    message = BytesParser(policy=policy.default).parsebytes(original_mime.encode("utf-8"))
    return str(message.get("Subject") or "")


def reply_subject(subject: str) -> str:
    return subject if subject.lower().startswith("re:") else f"Re: {subject}"


# --------------------------------------------------------------------------------------------- composing


def read_body(body: Optional[str], body_file: Optional[Path], html_file: Optional[Path]) -> tuple[str, str]:
    """(content, mailFormat). Zoho sends one body, so plain text and HTML are one or the other."""
    given = [name for name, value in (("--body", body), ("--body-file", body_file), ("--html-file", html_file))
             if value is not None]
    if len(given) > 1:
        raise DtZohoError(f"pass one of --body, --body-file or --html-file, not {' and '.join(given)}: Zoho sends "
                          "one body, as plain text or as HTML.", exit_code=2)
    for label, path in (("--body-file", body_file), ("--html-file", html_file)):
        if path is not None and not path.is_file():
            raise DtZohoError(f"{label} {path} does not exist or is not a file.", exit_code=2)
    if html_file is not None:
        return html_file.read_text(encoding="utf-8"), "html"
    if body_file is not None:
        return body_file.read_text(encoding="utf-8"), "plaintext"
    return body or "", "plaintext"


def check_files(paths: Optional[Sequence[Path]]) -> List[Path]:
    files = list(paths or [])
    for path in files:
        if not path.is_file():
            raise DtZohoError(f"--attach {path} does not exist or is not a file.", exit_code=2)
    return files


def _parse_at(at: str) -> datetime:
    try:
        return datetime.strptime(at.strip(), SCHEDULE_FORMAT)
    except ValueError as exc:
        raise DtZohoError(f"--at must be a date and time as YYYY-MM-DD HH:MM, not '{at}'.", exit_code=2) from exc


def check_at(at: Optional[str], time_zone: Optional[str]) -> None:
    """Check --at and --time-zone before anything reaches Zoho."""
    if at is None and time_zone is not None:
        raise DtZohoError("--time-zone goes with --at.", exit_code=2)
    if at is not None:
        _parse_at(at)


def schedule_fields(at: str, time_zone: Optional[str]) -> dict:
    """The send body's schedule fields for `--at`."""
    when = _parse_at(at)
    if not time_zone:
        raise DtZohoError("the account has no time zone for --at; pass --time-zone, for example America/Toronto.",
                          exit_code=2)
    return {"isSchedule": True, "scheduleType": 6, "timeZone": time_zone,
            "scheduleTime": when.strftime("%m/%d/%Y %H:%M:%S")}


def upload(client: ZohoClient, account_id: str, path: Path) -> dict:
    data = client.mail_upload(f"/api/accounts/{account_id}/messages/attachments", {"fileName": path.name},
                              path.read_bytes())["data"]
    return {k: data[k] for k in ("storeName", "attachmentPath", "attachmentName")}


def message_body(from_address: str, to: List[str], cc: List[str], bcc: List[str], subject: str, content: str,
                 mail_format: str, attachments: List[dict], extra: Optional[dict] = None) -> dict:
    body = {"fromAddress": from_address, "toAddress": ",".join(to), "subject": subject, "content": content,
            "mailFormat": mail_format}
    if cc:
        body["ccAddress"] = ",".join(cc)
    if bcc:
        body["bccAddress"] = ",".join(bcc)
    if attachments:
        body["attachments"] = attachments
    body.update(extra or {})
    return body


def send(client: ZohoClient, account_id: str, body: dict) -> dict:
    return client.mail("POST", f"/api/accounts/{account_id}/messages", json_body=body)["data"]


def reply(client: ZohoClient, account_id: str, message_id: str, body: dict, reply_all: bool) -> dict:
    body = dict(body, action="replyall" if reply_all else "reply")
    return client.mail("POST", f"/api/accounts/{account_id}/messages/{message_id}", json_body=body)["data"]


def sent_record(answer: dict, scheduled: bool) -> dict:
    return {"message_id": str(answer.get("messageId")) if answer.get("messageId") else None,
            "mail_id": answer.get("mailId"), "from": answer.get("fromAddress"), "to": answer.get("toAddress"),
            "cc": answer.get("ccAddress"), "subject": answer.get("subject"),
            "scheduled_for": f"{answer.get('scheduleTime')} {answer.get('timeZone')}" if scheduled else None}


# --------------------------------------------------------------------------------------------- managing messages


def update_messages(client: ZohoClient, account_id: str, message_ids: List[str], body: dict) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}/updatemessage", json_body=dict(body, messageId=message_ids))


def update_threads(client: ZohoClient, account_id: str, thread_ids: List[str], body: dict) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}/updatethread",
                json_body=dict(body, threadId=[int(t) for t in thread_ids]))


def delete_message(client: ZohoClient, account_id: str, folder_id: str, message_id: str, permanent: bool) -> None:
    client.mail("DELETE", f"/api/accounts/{account_id}/folders/{folder_id}/messages/{message_id}",
                query={"expunge": "true"} if permanent else None)


# --------------------------------------------------------------------------------------------- folders


def create_folder(client: ZohoClient, account_id: str, name: str, parent_id: Optional[str]) -> dict:
    body = {"folderName": name}
    if parent_id:
        body["parentFolderId"] = parent_id
    return client.mail("POST", f"/api/accounts/{account_id}/folders", json_body=body)["data"]


def update_folder(client: ZohoClient, account_id: str, folder_id: str, body: dict) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}/folders/{folder_id}", json_body=body)


def delete_folder(client: ZohoClient, account_id: str, folder_id: str) -> None:
    client.mail("DELETE", f"/api/accounts/{account_id}/folders/{folder_id}")


# --------------------------------------------------------------------------------------------- labels


def create_label(client: ZohoClient, account_id: str, name: str, color: str) -> dict:
    return client.mail("POST", f"/api/accounts/{account_id}/labels",
                       json_body={"displayName": name, "color": color})["data"]


def update_label(client: ZohoClient, account_id: str, label_id: str, name: str, color: str) -> None:
    client.mail("PUT", f"/api/accounts/{account_id}/labels/{label_id}", json_body={"displayName": name, "color": color})


def delete_label(client: ZohoClient, account_id: str, label_id: str) -> None:
    client.mail("DELETE", f"/api/accounts/{account_id}/labels/{label_id}")

"""An in-memory Zoho for the dt-zoho tests. No network: it replaces `transport.send`.

Its answers copy the shapes Zoho gave in the proof of 8 November 2026 (proof\\prover-a\\evidence\\zoho-*.txt):
the token endpoint's JSON, `{"status": {...}, "data": ...}` around every Mail answer, list fields HTML-escaped
with an empty CC as "Not Provided", times in milliseconds as strings, `status` "1" for read, and the Mail error
shape `{"data": {"errorCode": ...}, "status": {"code": 404, "description": "Invalid Input"}}`.
"""

from __future__ import annotations

import json
import urllib.parse
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from src.transport import Response

ACCOUNTS = "https://accounts.zohocloud.ca"
MAIL = "https://mail.zohocloud.ca"
ACCOUNT_ID = "636823000000002002"
INBOX, SENT, SUB, SUB2 = "636823000000002008", "636823000000002012", "636823000000007009", "636823000000007010"


def _ok(data, code=200) -> Response:
    return Response(200, json.dumps({"status": {"code": code, "description": "success"}, "data": data}).encode())


def _mail_error(status: int, code: str) -> Response:
    return Response(status, json.dumps({"data": {"errorCode": code},
                                        "status": {"code": status, "description": "Invalid Input"}}).encode())


@dataclass
class Message:
    id: str
    folder: str
    subject: str
    sender: str = "alice@example.org"
    sender_name: str = "Alice"
    to: str = "&lt;soren@devthrottle.com&gt;"
    cc: str = "Not Provided"
    read: bool = False
    flag: str = "flag_not_set"
    thread: Optional[str] = None
    labels: List[str] = field(default_factory=list)
    html: str = "<div>Hello<br>there</div>"
    attachments: Dict[str, tuple] = field(default_factory=dict)   # id -> (name, bytes)
    received: str = "1794144262990"

    def summary(self) -> dict:
        data = {"summary": "Hello there", "sentDateInGMT": self.received, "subject": self.subject,
                "messageId": self.id, "flagid": self.flag, "toAddress": self.to, "folderId": self.folder,
                "ccAddress": self.cc, "hasAttachment": "1" if self.attachments else "0", "size": "110",
                "sender": self.sender_name, "receivedTime": self.received, "fromAddress": self.sender,
                "status": "1" if self.read else "0", "threadId": self.thread or self.id}
        if self.labels:
            data["labelId"] = list(self.labels)
        return data


class FakeZoho:
    def __init__(self, client_id="cid", client_secret="secret", refresh_token="refresh"):
        self.client_id, self.client_secret, self.refresh_token = client_id, client_secret, refresh_token
        self.token_requests = 0
        self.token_limit: Optional[int] = None       # answer "too many requests" past this many
        self.valid_tokens: set = set()
        self.calls: List[tuple] = []                 # (method, path, query, authorization)
        self.redirect_next = False
        self.accounts = [{
            "accountId": ACCOUNT_ID, "zuid": 110003647767, "isDefaultAccount": True, "firstName": "Soren Frederiksen",
            "incomingUserName": "soren@devthrottle.com", "imapAccessEnabled": True, "timeZone": "America/Toronto",
            "emailAddress": [{"isAlias": True, "isPrimary": False, "mailId": "info@example.com"},
                             {"isAlias": False, "isPrimary": True, "mailId": "soren@devthrottle.com"}]}]
        self.folders = [
            {"folderId": INBOX, "folderName": "Inbox", "path": "/Inbox", "folderType": "Inbox", "imapAccess": True},
            {"folderId": SENT, "folderName": "Sent", "path": "/Sent", "folderType": "Sent", "imapAccess": True},
            {"folderId": SUB, "folderName": "Reports", "path": "/Inbox/Reports", "folderType": "Inbox",
             "imapAccess": True, "parentFolderId": INBOX},
            {"folderId": SUB2, "folderName": "Reports", "path": "/Reports", "folderType": "Inbox",
             "imapAccess": False},
        ]
        self.labels = [{"labelId": "636823000000007002", "displayName": "LWTBI", "color": "#dd902f"}]
        self.messages: List[Message] = []

    # ----- the wire

    def send(self, method: str, url: str, headers: Dict[str, str], body: Optional[bytes] = None) -> Response:
        parts = urllib.parse.urlsplit(url)
        query = dict(urllib.parse.parse_qsl(parts.query))
        base = f"{parts.scheme}://{parts.netloc}"
        if base == ACCOUNTS and parts.path == "/oauth/v2/token":
            return self._token(dict(urllib.parse.parse_qsl((body or b"").decode())))
        auth = headers.get("Authorization", "")
        self.calls.append((method, parts.path, query, auth))
        if self.redirect_next:
            self.redirect_next = False
            return Response(302, b"", {"location": "https://elsewhere.example/steal"})
        if not auth.startswith("Zoho-oauthtoken ") or auth.split(" ", 1)[1] not in self.valid_tokens:
            return Response(401, json.dumps({"data": {"errorCode": "INVALID_OAUTHTOKEN"},
                                             "status": {"code": 401, "description": "Invalid Input"}}).encode())
        if base != MAIL:
            return _mail_error(404, "URL_RULE_NOT_CONFIGURED")
        return self._mail(method, parts.path, query)

    def _token(self, form: dict) -> Response:
        self.token_requests += 1
        if self.token_limit is not None and self.token_requests > self.token_limit:
            return Response(400, json.dumps({
                "error_description": "You have made too many requests continuously. Please try again after some time.",
                "error": "Access Denied", "status": "failure"}).encode())
        if (form.get("client_id"), form.get("client_secret"), form.get("refresh_token"),
                form.get("grant_type")) != (self.client_id, self.client_secret, self.refresh_token, "refresh_token"):
            return Response(200, json.dumps({"error": "invalid_code"}).encode())
        token = f"access-{self.token_requests}"
        self.valid_tokens.add(token)
        return Response(200, json.dumps({"access_token": token, "scope": "ZohoMail.messages.ALL",
                                         "api_domain": "https://www.zohoapis.ca", "token_type": "Bearer",
                                         "expires_in": 3600}).encode())

    def revoke_all(self) -> None:
        self.valid_tokens.clear()

    # ----- the Mail API

    def _message(self, mid: str, folder: Optional[str] = None) -> Optional[Message]:
        for m in self.messages:
            if m.id == mid and (folder is None or m.folder == folder):
                return m
        return None

    def _mail(self, method: str, path: str, query: dict) -> Response:
        p = path.split("/")
        if path == "/api/accounts" and method == "GET":
            return _ok(self.accounts)
        if path == "/api/accounts/":
            return _mail_error(404, "URL_RULE_NOT_CONFIGURED")
        if len(p) < 4 or p[3] != ACCOUNT_ID:
            return _mail_error(404, "INVALID_ACCOUNT")
        rest = p[4:]
        if rest == ["folders"]:
            return _ok(self.folders)
        if rest == ["labels"]:
            return _ok(self.labels)
        if rest == ["messages", "view"]:
            found = [m for m in self.messages
                     if (not query.get("folderId") or m.folder == query["folderId"])
                     and (not query.get("labelid") or query["labelid"] in m.labels)
                     and (not query.get("threadId") or (m.thread or m.id) == query["threadId"])
                     and (query.get("status", "all") == "all" or m.read == (query["status"] == "read"))]
            start, limit = int(query.get("start", 1)), int(query.get("limit", 10))
            return _ok([m.summary() for m in found[start - 1:start - 1 + limit]])
        if rest == ["messages", "search"]:
            words = query["searchKey"].lower()
            found = [m for m in self.messages if words.split(":", 1)[-1] in m.subject.lower()]
            start, limit = int(query.get("start", 1)), int(query.get("limit", 10))
            return _ok([m.summary() for m in found[start - 1:start - 1 + limit]])
        if len(rest) == 3 and rest[0] == "messages" and rest[2] == "originalmessage":
            m = self._message(rest[1])
            if m is None:
                return _mail_error(404, "INVALID_MESSAGE")
            return _ok({"messageId": int(m.id), "content": f"From: {m.sender}\r\nSubject: {m.subject}\r\n\r\nHello"})
        if len(rest) >= 5 and rest[0] == "folders" and rest[2] == "messages":
            m = self._message(rest[3], rest[1])
            if m is None:
                return _mail_error(404, "INVALID_MESSAGE")
            what = rest[4]
            if what == "details":
                return _ok(m.summary())
            if what == "content":
                return _ok({"messageId": int(m.id), "content": m.html})
            if what == "header":
                return _ok({"headerContent": f"From: {m.sender}\r\nSubject: {m.subject}\r\n"})
            if what == "attachmentinfo":
                return _ok({"messageId": m.id, "attachments": [
                    {"attachmentId": aid, "attachmentName": n, "attachmentSize": len(d)}
                    for aid, (n, d) in m.attachments.items()]})
            if what == "attachments" and len(rest) == 6:
                if rest[5] not in m.attachments:
                    return _mail_error(404, "INVALID_ATTACHMENT")
                return Response(200, m.attachments[rest[5]][1], {"content-type": "application/octet-stream"})
        return _mail_error(404, "URL_RULE_NOT_CONFIGURED")

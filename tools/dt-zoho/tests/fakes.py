"""An in-memory Zoho for the dt-zoho tests. No network: it replaces `transport.send`.

Its answers copy the shapes Zoho gave in the proof of 8 November 2026 (proof\\prover-a\\evidence\\zoho-*.txt):
the token endpoint's JSON, `{"status": {...}, "data": ...}` around every Mail answer, list fields HTML-escaped
with an empty CC as "Not Provided", times in milliseconds as strings, `status` "1" for read, and the Mail error
shape `{"data": {"errorCode": ...}, "status": {"code": 404, "description": "Invalid Input"}}`.
"""

from __future__ import annotations

import html
import json
import urllib.parse
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from fake_admin import FakeAdmin
from fake_calendar import CAL, FakeCalendar
from src.transport import Response

ACCOUNTS = "https://accounts.zohocloud.ca"
MAIL = "https://mail.zohocloud.ca"
ACCOUNT_ID = "636823000000002002"
INBOX, SENT, SUB, SUB2 = "636823000000002008", "636823000000002012", "636823000000007009", "636823000000007010"
DRAFTS, ARCHIVE, SPAM, TRASH = "636823000000002010", "636823000000002020", "636823000000002016", "636823000000002014"


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
    reply_to: Optional[str] = None

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
            {"folderId": DRAFTS, "folderName": "Drafts", "path": "/Drafts", "folderType": "Drafts", "imapAccess": True},
            {"folderId": ARCHIVE, "folderName": "Archive", "path": "/Archive", "folderType": "Archive",
             "imapAccess": True},
            {"folderId": SPAM, "folderName": "Spam", "path": "/Spam", "folderType": "Spam", "imapAccess": True},
            {"folderId": TRASH, "folderName": "Trash", "path": "/Trash", "folderType": "Trash", "imapAccess": True},
        ]
        self.sent: List[dict] = []                   # every POST to .../messages and .../messages/{m}, with its path
        self.uploads: List[tuple] = []               # (fileName, bytes)
        self.fail_action: Optional[str] = None       # answer 500 to a send with this action, as Zoho does to forward
        self.next_id = 900000000000000000
        self.labels = [{"labelId": "636823000000007002", "displayName": "LWTBI", "color": "#dd902f"}]
        self.messages: List[Message] = []
        self.calendar = FakeCalendar()
        self.admin = FakeAdmin(self.accounts[0], self._new_id)

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
        if base == CAL:
            return self.calendar.handle(method, parts.path, query, headers)
        if base != MAIL:
            return _mail_error(404, "URL_RULE_NOT_CONFIGURED")
        return self._mail(method, parts.path, query, body)

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

    def _new_id(self) -> str:
        self.next_id += 1
        return str(self.next_id)

    def _write(self, method: str, rest: List[str], query: dict, raw: Optional[bytes]) -> Optional[Response]:
        """The writing calls. None when the path is not one of them."""
        body = json.loads(raw.decode()) if raw and rest != ["messages", "attachments"] else {}
        if rest == ["messages", "attachments"] and method == "POST":
            self.uploads.append((query["fileName"], raw))
            return _ok({"storeName": "110003647767", "attachmentName": query["fileName"],
                        "attachmentPath": f"/Mail/{len(self.uploads)}-{query['fileName']}"})
        if method == "POST" and (rest == ["messages"] or (len(rest) == 2 and rest[0] == "messages")):
            if self.fail_action and body.get("action") == self.fail_action:
                return Response(500, json.dumps({"status": {"code": 500, "description": "Internal Error"},
                                                 "data": {"moreInfo": "An internal error occurred"}}).encode())
            self.sent.append(dict(body, _path="/".join(rest)))
            mid = self._new_id()
            if body.get("mode") == "draft":
                self.messages.append(Message(id=mid, folder=DRAFTS, subject=body.get("subject", "")))
            return _ok(dict(body, messageId=mid, mailId=f"<{mid}@devthrottle.com>"))
        if rest == ["updatemessage"] and method == "PUT":
            self._apply(body, [m for m in self.messages if m.id in body["messageId"]])
            return _ok(None)
        if rest == ["updatethread"] and method == "PUT":
            wanted = {str(t) for t in body["threadId"]}
            self._apply(body, [m for m in self.messages if (m.thread or m.id) in wanted])
            return _ok(None)
        if len(rest) == 4 and rest[0] == "folders" and rest[2] == "messages" and method == "DELETE":
            m = self._message(rest[3], rest[1])
            if m is None:
                return _mail_error(404, "INVALID_MESSAGE")
            if query.get("expunge") == "true":
                self.messages.remove(m)
            else:
                m.folder = TRASH
            return _ok(None)
        if rest == ["folders"] and method == "POST":
            fid = self._new_id()
            parent = next((f for f in self.folders if f["folderId"] == body.get("parentFolderId")), None)
            path = (parent["path"] if parent else "") + "/" + body["folderName"]
            folder = {"folderId": fid, "folderName": body["folderName"], "path": path, "folderType": "Inbox",
                      "imapAccess": True}
            if parent:
                folder["parentFolderId"] = parent["folderId"]
            self.folders.append(folder)
            return _ok({"folderId": fid, "folderName": body["folderName"], "path": path})
        if len(rest) == 2 and rest[0] == "folders" and method in ("PUT", "DELETE"):
            folder = next((f for f in self.folders if f["folderId"] == rest[1]), None)
            if folder is None:
                return _mail_error(404, "INVALID_FOLDER")
            if method == "DELETE":
                # As Zoho does: a folder outside Trash moves into it; a folder in Trash is removed for good.
                if folder["path"].startswith("/Trash/"):
                    self.folders.remove(folder)
                else:
                    folder["path"] = "/Trash" + folder["path"]
                return _ok(None)
            mode = body.get("mode")
            if mode == "rename":
                folder["folderName"] = body["folderName"]
                folder["path"] = folder["path"].rsplit("/", 1)[0] + "/" + body["folderName"]
            elif mode == "move":
                folder.pop("parentFolderId", None)
                folder["path"] = "/" + folder["folderName"]
            elif mode in ("setViewableInIMAP", "removeViewableInIMAP"):
                folder["imapAccess"] = mode == "setViewableInIMAP"
            elif mode == "markAsRead":
                for m in self.messages:
                    if m.folder == folder["folderId"]:
                        m.read = True
            elif mode == "emptyFolder":
                for m in self.messages:
                    if m.folder == folder["folderId"]:
                        m.folder = TRASH
            else:
                return _mail_error(400, "INVALID_MODE")
            return _ok(None)
        if rest == ["labels"] and method == "POST":
            lid = self._new_id()
            self.labels.append({"labelId": lid, "displayName": body["displayName"], "color": body["color"]})
            return _ok({"labelId": lid, "displayName": body["displayName"]})
        if len(rest) == 2 and rest[0] == "labels" and method in ("PUT", "DELETE"):
            label = next((l for l in self.labels if l["labelId"] == rest[1]), None)
            if label is None:
                return _mail_error(404, "INVALID_LABEL")
            if method == "DELETE":
                self.labels.remove(label)
            else:
                label.update(displayName=body["displayName"], color=body["color"])
            return _ok(None)
        return None

    def _apply(self, body: dict, targets: List["Message"]) -> None:
        mode = body["mode"]
        for m in targets:
            if mode in ("markAsRead", "markAsUnread"):
                m.read = mode == "markAsRead"
            elif mode == "setFlag":
                m.flag = body["flagid"]
            elif mode == "moveMessage":
                m.folder = body["destfolderId"]
            elif mode == "archiveMails":
                m.folder = ARCHIVE
            elif mode == "moveToSpam":
                m.folder = SPAM
            elif mode == "markNotSpam":
                m.folder = INBOX
            elif mode == "applyLabel":
                m.labels = sorted(set(m.labels) | set(body["labelId"]))
            elif mode == "removeLabel":
                m.labels = [l for l in m.labels if l not in body["labelId"]]
            elif mode == "removeAllLabels":
                m.labels = []
            else:
                raise AssertionError(f"the fake Zoho does not know the mode {mode}")

    def _mail(self, method: str, path: str, query: dict, raw: Optional[bytes] = None) -> Response:
        p = path.split("/")
        if path == "/api/accounts" and method == "GET":
            return _ok([self.admin.account_answer()] + self.accounts[1:])
        if path == "/api/accounts/":
            return _mail_error(404, "URL_RULE_NOT_CONFIGURED")
        admin = self.admin.handle(method, path, query, raw)
        if admin is not None:
            return admin
        if len(p) < 4 or p[3] != ACCOUNT_ID:
            return _mail_error(404, "INVALID_ACCOUNT")
        rest = p[4:]
        written = self._write(method, rest, query, raw)
        if written is not None:
            return written
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
            headers = f"From: {m.sender_name} <{m.sender}>\r\nTo: {html.unescape(m.to)}\r\n"
            if m.cc != "Not Provided":
                headers += f"Cc: {html.unescape(m.cc)}\r\n"
            if m.reply_to:
                headers += f"Reply-To: {m.reply_to}\r\n"
            return _ok({"messageId": int(m.id), "content": f"{headers}Subject: {m.subject}\r\n\r\nHello"})
        if len(rest) >= 5 and rest[0] == "folders" and rest[2] == "messages":
            what = rest[4]
            if what == "details":
                # Looked up by id alone: the worst case for a caller that trusts the folder in the path, since it
                # was not proven whether Zoho honours it.
                m = self._message(rest[3])
                if m is None:
                    # Zoho's own answer for an id it does not have (seen live on 8 November 2026).
                    return Response(400, json.dumps({"data": {"errorCode": "messageId is invalid"},
                                                     "status": {"code": 400, "description": "Invalid Input"}}).encode())
                return _ok(m.summary())
            m = self._message(rest[3], rest[1])
            if m is None:
                return _mail_error(404, "INVALID_MESSAGE")
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

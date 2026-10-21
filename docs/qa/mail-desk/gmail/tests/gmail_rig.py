"""The shared rig for the Gmail reader's tests (docs/qa/mail-desk/gmail/CONTRACT.md).

It builds on phase 2's rig (docs/qa/mail-desk/phase-2/tests/conftest.py), loaded here under its own module name so the
two directories' conftest files never meet: the same fresh screen.py per test, every CC_ variable removed, a scratch
HOME, a scratch "account record" home, the fake Graph and the fake MSAL sign-in. Beside them:

- a fake Gmail API and a fake Google sign-in service, which stand in for urllib.request.urlopen together with the fake
  Graph (one dispatcher, by host), so screen.py's own transport runs for real. They record EVERY request that reaches
  them, so "nothing reached the network" is their log being empty;
- a fake cc-gmail store under the scratch account-record home, in cc-gmail's own shape (R1): accounts/<nickname>/
  config.json and token.json.

No real address, token, id or mail text: example.com, example.org and example.net only. No network, no port.

This folder has no conftest.py on purpose: phase 2's tests import from a module named conftest, and pytest gives both
folders' conftest files that one name when they run together. The fixtures are here instead, and each test file
imports them (`from gmail_rig import gw, world`).

    uv run --no-project --with pytest --with "msal>=1.28" --with google-auth python -m pytest \
        docs/qa/mail-desk/phase-2/tests docs/qa/mail-desk/gmail/tests -q
"""

import importlib.util
import io
import json
import sys
import urllib.error
import urllib.parse
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

sys.dont_write_bytecode = True  # a __pycache__ beside the check would end up in the published template

HERE = Path(__file__).resolve().parent
PHASE2 = HERE.parents[1] / "phase-2" / "tests" / "conftest.py"

_spec = importlib.util.spec_from_file_location("mail_desk_phase2_rig", PHASE2)
phase2 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(phase2)

NOW = phase2.NOW
MACHINE = phase2.MACHINE
GMAIL_BOX = "desk@example.com"
GMAIL_TWO = "second.desk@example.org"
OUTLOOK_BOX = "office@example.net"
NICKNAME = "desk"
TOKEN_URI = "https://oauth2.googleapis.com/token"
READONLY = "https://www.googleapis.com/auth/gmail.readonly"
MODIFY = "https://www.googleapis.com/auth/gmail.modify"
# The scopes cc-gmail asks for (tools/cc-gmail/src/auth.py SCOPES): it can send, which is why the guard is the promise.
CC_GMAIL_SCOPES = [READONLY, "https://www.googleapis.com/auth/gmail.send",
                   "https://www.googleapis.com/auth/gmail.compose", MODIFY,
                   "https://www.googleapis.com/auth/calendar", "https://www.googleapis.com/auth/contacts"]

Response = phase2.Response
gateway_reason = phase2.gateway_reason
channel = phase2.channel


def ms(text):
    """Milliseconds since 1970 of "YYYY-MM-DDTHH:MM:SS[.fff]Z"."""
    whole, _, fraction = text.rstrip("Z").partition(".")
    moment = datetime.strptime(whole, "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)
    return int(moment.timestamp()) * 1000 + int((fraction or "0").ljust(3, "0")[:3])


def gmsg(mid, received, sender="Some One <someone@example.com>", subject="Hello", snippet="A short note.",
         labels=("INBOX",), attached=False, to=None, cc=None, bcc=None):
    """A Gmail message as the Gmail API holds it: internalDate as milliseconds in text, headers, snippet, labels.
    `attached` is what Gmail's has:attachment search finds; format=metadata shows no parts (G0), and neither does
    the fake."""
    headers = [{"name": "From", "value": sender}, {"name": "Subject", "value": subject}]
    for name, value in (("To", to), ("Cc", cc), ("Bcc", bcc)):
        if value is not None:
            headers.append({"name": name, "value": value})
    payload = {"mimeType": "multipart/alternative", "headers": headers}
    return {"id": mid, "threadId": f"t{mid}", "labelIds": list(labels), "snippet": snippet,
            "internalDate": str(ms(received) if isinstance(received, str) else received), "payload": payload,
            "attached": attached}


def gsent(mid, to, received="2026-10-10T08:00:00Z", cc=None, bcc=None):
    return gmsg(mid, received, sender=f"Desk <{GMAIL_BOX}>", subject="Re: your note", labels=("SENT",), to=to, cc=cc,
                bcc=bcc)


class FakeGmail:
    """One fake Gmail API for every mailbox, told apart by the access token ("gtoken-for-<address>"), and one fake
    Google sign-in service that refreshes a saved sign-in by its refresh token ("refresh-for-<address>"). A request
    it does not know fails the test."""

    def __init__(self):
        self.mailboxes = {}
        self.log = []          # (method, url, body text or None, headers) for EVERY request that reached the network
        self.fail_for = {}     # address -> an HTTP status, "unreachable", "not-json", "bad-list", "bad-message",
        #                         "bad-page-token" or "bad-sent-payload"
        self.unreachable_reason = "connection refused"
        self.refresh_answer = {}   # address -> "refused", an HTTP status, "unreachable", "not-json", or extra answer keys
        self.issued = 0

    def box(self, address):
        return self.mailboxes.setdefault(address, [])

    def gmail_log(self):
        return [entry for entry in self.log if "gmail.googleapis.com" in entry[1]]

    def posts(self):
        return [entry for entry in self.gmail_log() if entry[0] != "GET"]

    def refreshes(self):
        return [entry for entry in self.log if entry[1] == TOKEN_URI]

    def urlopen(self, request, timeout=None):
        method = request.get_method()
        url = request.full_url
        body = request.data.decode("utf-8") if request.data is not None else None
        headers = {k.lower(): v for k, v in request.header_items()}
        self.log.append((method, url, body, headers))
        if url == TOKEN_URI:
            return self.token(method, body, headers)
        token = (headers.get("authorization") or "").removeprefix("Bearer ")
        assert token.startswith("gtoken-for-"), f"a Gmail request without the fake sign-in's token: {method} {url}"
        address = token.removeprefix("gtoken-for-").split("#")[0]
        failure = self.fail_for.get(address)
        if failure == "unreachable":
            raise urllib.error.URLError(self.unreachable_reason)
        if isinstance(failure, int):
            raise urllib.error.HTTPError(url, failure, "error", {}, io.BytesIO(b'{"error": {"code": 500}}'))
        if failure == "not-json":
            return Response(b"<html>a proxy page</html>")
        status, answer = self.answer(method, url, body, self.box(address))
        if failure == "bad-list" and "messages" in answer:
            answer["messages"] = [{"threadId": "t1"}]
        if failure == "bad-message" and "internalDate" in answer:
            answer["internalDate"] = "yesterday"
        if failure == "bad-page-token" and "messages" in answer:
            answer["nextPageToken"] = "12\n34"
        if failure == "bad-sent-payload" and "SENT" in answer.get("labelIds", []):
            answer["payload"] = "unexpected"
        if status >= 400:
            raise urllib.error.HTTPError(url, status, "error", {}, io.BytesIO(json.dumps(answer).encode("utf-8")))
        return Response(json.dumps(answer).encode("utf-8"))

    def token(self, method, body, headers):
        assert method == "POST" and "authorization" not in headers
        assert headers.get("content-type") == "application/x-www-form-urlencoded"
        form = dict(urllib.parse.parse_qsl(body))
        assert set(form) == {"grant_type", "client_id", "client_secret", "refresh_token"}
        address = form["refresh_token"].removeprefix("refresh-for-")
        behaviour = self.refresh_answer.get(address, {})
        if behaviour == "refused":
            raise urllib.error.HTTPError(TOKEN_URI, 400, "Bad Request", {}, io.BytesIO(
                b'{"error": "invalid_grant", "error_description": "Token has been expired or revoked."}'))
        if behaviour == "unreachable":
            raise urllib.error.URLError("connection refused")
        if isinstance(behaviour, int):
            raise urllib.error.HTTPError(TOKEN_URI, behaviour, "error", {}, io.BytesIO(b"{}"))
        if behaviour == "not-json":
            return Response(b"<html>a captive portal</html>")
        if behaviour == "a-list":
            return Response(b'["access_token"]')
        self.issued += 1
        answer = {"access_token": f"gtoken-for-{address}#{self.issued}", "expires_in": 3599, "token_type": "Bearer",
                  "scope": " ".join(CC_GMAIL_SCOPES)}
        answer.update(behaviour)
        return Response(json.dumps(answer).encode("utf-8"))

    def answer(self, method, url, body, box):
        parts = urllib.parse.urlsplit(url)
        assert parts.netloc == "gmail.googleapis.com", url
        query = urllib.parse.parse_qs(parts.query)
        segments = parts.path.split("/")
        if method == "GET" and parts.path == "/gmail/v1/users/me/messages":
            labels = query.get("labelIds", [])
            found = [m for m in box if all(label in m["labelIds"] for label in labels)]
            q = query.get("q", [""])[0]
            if " OR " in q or q.startswith(("to:", "cc:", "bcc:")):
                wanted = {term.split(":", 1)[1] for term in q.split(" OR ")}
                found = [m for m in found if any(w in json.dumps(m["payload"]["headers"]).lower() for w in wanted)]
            else:
                for term in q.split():
                    if term.startswith("after:"):
                        found = [m for m in found if int(m["internalDate"]) >= int(term.removeprefix("after:")) * 1000]
                    elif term == "has:attachment":
                        found = [m for m in found if m["attached"]]
                    else:
                        raise AssertionError(f"the fake Gmail API does not know the search {q!r}")
            found.sort(key=lambda m: int(m["internalDate"]), reverse=True)
            size, start = int(query["maxResults"][0]), int(query.get("pageToken", ["0"])[0])
            answer = {"resultSizeEstimate": len(found)}
            if found[start:start + size]:
                answer["messages"] = [{"id": m["id"], "threadId": m["threadId"]} for m in found[start:start + size]]
            if start + size < len(found):
                answer["nextPageToken"] = str(start + size)
            return 200, answer
        if method == "GET" and len(segments) == 7 and segments[:6] == ["", "gmail", "v1", "users", "me", "messages"]:
            message = next((m for m in box if m["id"] == segments[6]), None)
            if message is None:
                return 404, {"error": {"code": 404, "message": "Requested entity was not found."}}
            assert query["format"] == ["metadata"]
            wanted = {h.lower() for h in query.get("metadataHeaders", [])}
            shown = json.loads(json.dumps(message))
            del shown["attached"]
            shown["payload"]["headers"] = [h for h in shown["payload"]["headers"] if h["name"].lower() in wanted]
            return 200, shown
        if method == "POST" and len(segments) == 8 and segments[7] == "modify":
            message = next((m for m in box if m["id"] == segments[6]), None)
            if message is None:
                return 404, {"error": {"code": 404, "message": "Requested entity was not found."}}
            change = json.loads(body)
            message["labelIds"] = [label for label in message["labelIds"] if label not in change.get("removeLabelIds", [])]
            message["labelIds"] += [label for label in change.get("addLabelIds", []) if label not in message["labelIds"]]
            return 200, {"id": message["id"], "threadId": message["threadId"], "labelIds": message["labelIds"]}
        raise AssertionError(f"the fake Gmail API does not know {method} {parts.path}")


class Network:
    """One urlopen for the whole run: Graph's host to the fake Graph, Google's to the fake Gmail API."""

    def __init__(self, graph, gmail):
        self.graph, self.gmail = graph, gmail

    def urlopen(self, request, timeout=None):
        if urllib.parse.urlsplit(request.full_url).netloc == "graph.microsoft.com":
            return self.graph.urlopen(request, timeout)
        return self.gmail.urlopen(request, timeout)


def token_json(address, token=None, expiry=None, scopes=None, **changes):
    """token.json as google-auth's Credentials.to_json() writes it, for the fake sign-in service."""
    saved = {"token": token if token is not None else f"gtoken-for-{address}",
             "refresh_token": f"refresh-for-{address}", "token_uri": TOKEN_URI,
             "client_id": "1234567890-example.apps.googleusercontent.com", "client_secret": "example-client-secret",
             "scopes": scopes if scopes is not None else CC_GMAIL_SCOPES, "universe_domain": "googleapis.com",
             "account": "", "expiry": expiry if expiry is not None else "2026-10-18T13:00:00.123456Z"}
    saved.update(changes)
    return {k: v for k, v in saved.items() if v is not ...}


class GmailWorld(phase2.World):
    def __init__(self, world, gmail):
        super().__init__(world.screen, world.graph, world.sign_in, world.home, world.state, world.capsys)
        self.gmail = gmail

    def run_later(self, runs_before):
        """One run, 5 minutes after the one before, as the Director runs it - so the key table's "most recently
        reported" can tell runs apart."""
        self.screen.utc_now = lambda: NOW + timedelta(minutes=5 * runs_before)
        return self.run()

    @property
    def store(self):
        return self.home / ".cc-director" / "config" / "gmail"

    def plant_gmail(self, address=GMAIL_BOX, nickname=NICKNAME, auth_method="oauth", token=True, config_email=None):
        """A cc-gmail account in cc-gmail's own shape. `token` True plants a valid token.json, a dict plants that,
        None plants none."""
        folder = self.store / "accounts" / nickname
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "config.json").write_text(json.dumps({"email": config_email or address, "auth_method": auth_method},
                                                       indent=2), encoding="utf-8")
        (folder / "credentials.json").write_text('{"installed": {"client_id": "x"}}', encoding="utf-8")
        if token is True:
            token = token_json(address)
        if token is not None:
            path = folder / "token.json"
            path.write_text(json.dumps(token) if isinstance(token, dict) else token, encoding="utf-8")
            path.chmod(0o600)
        return folder


world = phase2.world   # phase 2's fixture: fresh screen.py, scratch homes, the fake Graph and MSAL


@pytest.fixture
def gw(world, monkeypatch):
    """Phase 2's world, with the fake Gmail API and Google sign-in service beside the fake Graph."""
    gmail = FakeGmail()
    network = Network(world.graph, gmail)
    monkeypatch.setattr(world.screen.urllib.request, "urlopen", network.urlopen)
    return GmailWorld(world, gmail)


def section(context, address):
    return next(s for s in context["channels"] if s["address"] == address)


def all_messages(context):
    return [m for s in context["channels"] for m in s["messages"]]


def cursor(newest, *ids):
    return {"provider": "gmail", "newestMs": ms(newest) if isinstance(newest, str) else newest,
            "idsAtNewest": sorted(ids)}

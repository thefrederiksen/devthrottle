# /// script
# requires-python = ">=3.11"
# dependencies = []
# ///
"""Mail Desk Gmail support, proof G0: find the Gmail sign-in, renew it, and measure the mailbox read-only.

This is a proof script, not product code. It implements rulings R1 and R2 of
docs/qa/mail-desk/gmail/RULINGS.md exactly, with the Python standard library only (plain urllib, no Google
library), as the real check will:

- R1: the store is found in ONE documented place, with the home folder taken from the operating system's account
  record (never HOME, and LOCALAPPDATA ignored on Linux and macOS). The account is the directory under accounts/
  whose config.json "email" equals the address given, without regard to case. None found, or more than one: fail
  naming the fix or every directory. Never pick, no fallback.
- R2: token.json only (never credentials.json). Refresh when the access token is missing or expires within five
  minutes (or once more with --force-refresh), POST to exactly https://oauth2.googleapis.com/token, then write the
  token back atomically (temporary file in the same folder, os.replace, mode 0600) with "token" and "expiry"
  replaced (and "refresh_token" if Google returned a new one) and every other key unchanged.

Every Gmail call goes through gmail_get(), which allows only GET on two exact paths. There is no code path in this
script that can POST to Gmail: the only POST is post_token_refresh(), to the one token address.

The output never holds the mailbox's address, a sender, a subject, a snippet, a token or a real message id: counts,
sizes, timings, key names and hash prefixes only. Paths are written with the home folder as "~".

Run:  uv run --script gmail_probe.py --address <mailbox address> [--force-refresh] [--measure]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Callable

TOKEN_URI = "https://oauth2.googleapis.com/token"
GMAIL_HOST = "gmail.googleapis.com"
MESSAGES_PATH = "/gmail/v1/users/me/messages"
REFRESH_WINDOW = timedelta(minutes=5)
REQUIRED_TOKEN_KEYS = ("refresh_token", "client_id", "client_secret", "token_uri")
OBSERVE_SCOPES = ("https://www.googleapis.com/auth/gmail.readonly", "https://www.googleapis.com/auth/gmail.modify")
LIVE_SCOPE = "https://www.googleapis.com/auth/gmail.modify"
# Query parameters the guard lets through to Gmail, per path. Anything else is refused before the network.
LIST_QUERY_NAMES = {"labelIds", "q", "maxResults", "pageToken"}
GET_QUERY_NAMES = {"format", "metadataHeaders"}
ALLOWED_FORMATS = {"metadata", "minimal"}
MESSAGE_ID = re.compile(r"[A-Za-z0-9]+")


class ChannelFailed(Exception):
    """The channel cannot be read; the message names the fix."""


class GuardRefused(Exception):
    """A request the guard does not allow. Raised before anything reaches the network."""


# A transport is the only thing that touches the network: (method, url, headers, body) -> (status, body bytes).
# Tests pass a fake one.
Transport = Callable[[str, str, dict, bytes | None], tuple[int, bytes]]


def urllib_transport(method: str, url: str, headers: dict, body: bytes | None) -> tuple[int, bytes]:
    request = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


# ---------------------------------------------------------------------------------------------------------------
# R1: where the sign-in is
# ---------------------------------------------------------------------------------------------------------------

def account_record_home() -> Path:
    """The home folder from the operating system's account record, never from HOME."""
    import pwd

    return Path(pwd.getpwuid(os.getuid()).pw_dir)


def gmail_store(system: str | None = None, home: Path | None = None, environ: dict | None = None) -> Path:
    """The ONE documented place of cc-gmail's store for this operating-system user (ruling R1.1)."""
    system = system or platform.system()
    if system == "Windows":
        environ = os.environ if environ is None else environ
        local = environ.get("LOCALAPPDATA")
        if not local:
            raise ChannelFailed("LOCALAPPDATA is not set, so the Gmail sign-in cannot be found on this Windows machine")
        return Path(local) / "cc-director" / "config" / "gmail"
    home = home or account_record_home()
    if system == "Darwin":
        return home / "Library" / "Application Support" / "cc-director" / "config" / "gmail"
    return home / ".cc-director" / "config" / "gmail"


@dataclass
class Account:
    nickname: str
    directory: Path
    auth_method: str | None


def find_account(store: Path, address: str) -> Account:
    """The account whose config.json email equals the address, case-blind. Fails on none and on more than one."""
    accounts_dir = store / "accounts"
    matches: list[Account] = []
    if accounts_dir.is_dir():
        for directory in sorted(accounts_dir.iterdir()):
            config_path = directory / "config.json"
            if not directory.is_dir() or not config_path.is_file():
                continue
            try:
                config = json.loads(config_path.read_text(encoding="utf-8"))
            except (OSError, ValueError) as error:
                raise ChannelFailed(f"{config_path} cannot be read ({error.__class__.__name__}): repair or remove it")
            email = config.get("email") if isinstance(config, dict) else None
            if isinstance(email, str) and email.casefold() == address.casefold():
                matches.append(Account(directory.name, directory, config.get("auth_method")))
    if not matches:
        raise ChannelFailed(
            f"the mailbox is not signed in to cc-gmail on this machine (looked under {accounts_dir}): "
            "run cc-gmail accounts add <nickname> and then cc-gmail -a <nickname> auth"
        )
    if len(matches) > 1:
        names = ", ".join(str(m.directory) for m in matches)
        raise ChannelFailed(f"more than one cc-gmail account holds this address ({names}): remove all but one")
    return matches[0]


def load_token(account: Account, mode: str = "observe") -> tuple[Path, dict]:
    """token.json of the account, checked as rulings R1.3, R1.4, R1.5 and R2 say. Never reads credentials.json."""
    nick = account.nickname
    if account.auth_method != "oauth":
        raise ChannelFailed(
            f"account {nick} signs in with {account.auth_method or 'no method'}, not oauth: "
            f"run cc-gmail -a {nick} auth --method oauth"
        )
    token_path = account.directory / "token.json"
    if not token_path.is_file():
        raise ChannelFailed(f"account {nick} has no token.json: run cc-gmail -a {nick} auth")
    data = json.loads(token_path.read_text(encoding="utf-8"))
    if not data.get("refresh_token"):
        raise ChannelFailed(f"account {nick}'s token.json has no refresh token: run cc-gmail -a {nick} auth --force")
    missing = [key for key in REQUIRED_TOKEN_KEYS if not data.get(key)]
    if missing:
        raise ChannelFailed(
            f"account {nick}'s token.json lacks {', '.join(missing)}: run cc-gmail -a {nick} auth --force"
        )
    if data["token_uri"] != TOKEN_URI:
        raise ChannelFailed(
            f"account {nick}'s token.json names a token address other than Google's: run cc-gmail -a {nick} auth --force"
        )
    scopes = set(data.get("scopes") or [])
    needed_ok = (LIVE_SCOPE in scopes) if mode == "live" else bool(scopes & set(OBSERVE_SCOPES))
    if not needed_ok:
        raise ChannelFailed(f"account {nick}'s token lacks the scope {mode} mode needs: run cc-gmail -a {nick} auth --force")
    return token_path, data


# ---------------------------------------------------------------------------------------------------------------
# R2: the refresh and the write-back
# ---------------------------------------------------------------------------------------------------------------

def utcnow() -> datetime:
    """Naive UTC, as google-auth keeps its expiry."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


def parse_expiry(value: str | None) -> datetime | None:
    """Parsed exactly as google-auth's from_authorized_user_info does: drop the Z and the fraction."""
    if not value:
        return None
    return datetime.strptime(value.rstrip("Z").split(".")[0], "%Y-%m-%dT%H:%M:%S")


def format_expiry(value: datetime) -> str:
    """Written exactly as google-auth's to_json does: naive UTC isoformat plus Z."""
    return value.isoformat() + "Z"


def needs_refresh(data: dict, now: datetime) -> bool:
    expiry = parse_expiry(data.get("expiry"))
    return not data.get("token") or expiry is None or expiry - now < REFRESH_WINDOW


def post_token_refresh(data: dict, transport: Transport) -> dict:
    """The one POST this script can make: the standard OAuth refresh, to the one token address."""
    if data.get("token_uri") != TOKEN_URI:
        raise GuardRefused("the refresh goes only to " + TOKEN_URI)
    body = urllib.parse.urlencode({
        "grant_type": "refresh_token",
        "client_id": data["client_id"],
        "client_secret": data["client_secret"],
        "refresh_token": data["refresh_token"],
    }).encode("ascii")
    status, raw = transport("POST", TOKEN_URI, {"Content-Type": "application/x-www-form-urlencoded"}, body)
    try:
        answer = json.loads(raw.decode("utf-8") or "{}")
    except ValueError:
        answer = {}
    if status != 200 or "access_token" not in answer:
        error = answer.get("error", f"HTTP {status}")
        raise ChannelFailed(f"Google refused the refresh ({error}): run cc-gmail -a <nickname> auth --force")
    return answer


def renewed_token(data: dict, answer: dict, now: datetime) -> dict:
    """The same keys in the same order; token and expiry replaced, refresh_token only if Google sent a new one."""
    renewed = dict(data)
    renewed["token"] = answer["access_token"]
    renewed["expiry"] = format_expiry(now + timedelta(seconds=int(answer.get("expires_in", 3600))))
    if answer.get("refresh_token"):
        renewed["refresh_token"] = answer["refresh_token"]
    return renewed


def write_token_atomically(token_path: Path, data: dict) -> None:
    """A temporary file in the same folder, then os.replace; mode 0600 on Linux and macOS."""
    descriptor, temporary = tempfile.mkstemp(prefix=".token-", suffix=".tmp", dir=token_path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
            json.dump(data, handle)
            handle.flush()
            os.fsync(handle.fileno())
        if os.name == "posix":
            os.chmod(temporary, 0o600)
        os.replace(temporary, token_path)
    except BaseException:
        if os.path.exists(temporary):
            os.unlink(temporary)
        raise
    if os.name == "posix":
        os.chmod(token_path, 0o600)


def ensure_fresh_token(token_path: Path, data: dict, transport: Transport, force: bool = False,
                       now: datetime | None = None) -> tuple[dict, bool]:
    """Refresh when needed (or forced) and write back. Returns the token data in use and whether it was renewed."""
    now = now or utcnow()
    if not force and not needs_refresh(data, now):
        return data, False
    answer = post_token_refresh(data, transport)
    renewed = renewed_token(data, answer, now)
    write_token_atomically(token_path, renewed)
    return renewed, True


# ---------------------------------------------------------------------------------------------------------------
# R3 (the part G0 needs): the request guard. GET on two exact paths; nothing else.
# ---------------------------------------------------------------------------------------------------------------

def guard_gmail_get(path: str, query: list[tuple[str, str]]) -> str:
    """Build the URL of an allowed GET, or refuse before the network."""
    # Spaces are refused in the path and the parameter names; a search value (q) may hold spaces, since it is
    # URL-encoded. Control characters are refused everywhere. All of this before anything is parsed or sent.
    for text in [path] + [name for name, _ in query]:
        if any(ch.isspace() for ch in text):
            raise GuardRefused("spaces in the path or a parameter name")
    for text in [path] + [part for pair in query for part in pair]:
        if any(ord(ch) < 32 or ord(ch) == 127 for ch in text):
            raise GuardRefused("control characters")
    allowed_names = LIST_QUERY_NAMES
    if path != MESSAGES_PATH:
        prefix = MESSAGES_PATH + "/"
        if not path.startswith(prefix) or not MESSAGE_ID.fullmatch(path[len(prefix):]):
            raise GuardRefused(f"path not allowed: {path}")
        allowed_names = GET_QUERY_NAMES
        if ("format", "metadata") not in query and ("format", "minimal") not in query:
            raise GuardRefused("a message GET must name format metadata or minimal")
    for name, value in query:
        if name not in allowed_names:
            raise GuardRefused(f"query parameter not allowed: {name}")
        if name == "format" and value not in ALLOWED_FORMATS:
            raise GuardRefused(f"format not allowed: {value}")
    return urllib.parse.urlunsplit(("https", GMAIL_HOST, path, urllib.parse.urlencode(query), ""))


@dataclass
class CallRecord:
    kind: str
    status: int
    milliseconds: float
    bytes: int


class Gmail:
    """Read-only Gmail client: every call goes through gmail_get."""

    def __init__(self, access_token: str, transport: Transport):
        self._token = access_token
        self._transport = transport
        self.calls: list[CallRecord] = []

    def gmail_get(self, kind: str, path: str, query: list[tuple[str, str]]) -> dict:
        url = guard_gmail_get(path, query)
        started = time.perf_counter()
        status, raw = self._transport("GET", url, {"Authorization": "Bearer " + self._token}, None)
        elapsed = (time.perf_counter() - started) * 1000
        self.calls.append(CallRecord(kind, status, elapsed, len(raw)))
        if status != 200:
            raise ChannelFailed(f"Gmail answered HTTP {status} to a {kind} call")
        return json.loads(raw.decode("utf-8"))

    def list_ids(self, kind: str, label: str, q: str | None, max_results: int, cap: int) -> tuple[list[dict], list[int]]:
        found: list[dict] = []
        page_sizes: list[int] = []
        page_token = None
        while len(found) < cap:
            query = [("labelIds", label), ("maxResults", str(min(max_results, cap - len(found))))]
            if q:
                query.append(("q", q))
            if page_token:
                query.append(("pageToken", page_token))
            page = self.gmail_get(kind, MESSAGES_PATH, query)
            messages = page.get("messages", [])
            page_sizes.append(len(messages))
            found.extend(messages)
            page_token = page.get("nextPageToken")
            if not page_token:
                break
        return found[:cap], page_sizes

    def metadata(self, kind: str, message_id: str, headers: list[str]) -> dict:
        query = [("format", "metadata")] + [("metadataHeaders", h) for h in headers]
        return self.gmail_get(kind, f"{MESSAGES_PATH}/{message_id}", query)


# ---------------------------------------------------------------------------------------------------------------
# Reporting helpers: nothing personal leaves these
# ---------------------------------------------------------------------------------------------------------------

def tilde(path: Path, home: Path) -> str:
    text = str(path)
    return "~" + text[len(str(home)):] if text.startswith(str(home)) else text


def hash_prefix(value: object) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode("utf-8")).hexdigest()[:8]


def timing_summary(records: list[CallRecord]) -> dict:
    if not records:
        return {"calls": 0}
    times = sorted(r.milliseconds for r in records)
    return {
        "calls": len(records),
        "totalMs": round(sum(times)),
        "medianMs": round(times[len(times) // 2]),
        "minMs": round(times[0]),
        "maxMs": round(times[-1]),
        "http429": sum(1 for r in records if r.status == 429),
    }


def key_shape(value: object, depth: int = 0) -> object:
    """Key names and sizes only: dicts become their key names, lists their length and first element's shape."""
    if isinstance(value, dict):
        return {k: key_shape(v, depth + 1) for k, v in value.items()} if depth < 4 else sorted(value)
    if isinstance(value, list):
        return {"listLength": len(value), "first": key_shape(value[0], depth + 1) if value else None}
    if isinstance(value, str):
        return f"str({len(value)})"
    return type(value).__name__


def part_has_filename(part: dict) -> bool:
    if part.get("filename"):
        return True
    return any(part_has_filename(p) for p in part.get("parts", []) or [])


# ---------------------------------------------------------------------------------------------------------------
# The measurements (brief points c to h)
# ---------------------------------------------------------------------------------------------------------------

def measure(gmail: Gmail, token_data: dict) -> dict:
    from email.utils import getaddresses, parseaddr

    report: dict = {}
    scopes = token_data.get("scopes") or []
    report["c_scopes"] = {
        "scopes": scopes,
        "canSend": any(s.endswith(("/gmail.send", "/gmail.compose", "/gmail.modify")) or s == "https://mail.google.com/"
                       for s in scopes),
    }

    # d. Inbox, last 30 days, cap 200.
    after = int((datetime.now(timezone.utc) - timedelta(days=30)).timestamp())
    inbox, page_sizes = gmail.list_ids("inbox-list", "INBOX", f"after:{after}", 100, 200)
    list_calls = [c for c in gmail.calls if c.kind == "inbox-list"]
    report["d_inbox"] = {
        "messagesLast30DaysCapped200": len(inbox),
        "pageSizesAskedFor100": page_sizes,
        "listCallTimes": timing_summary(list_calls),
    }

    # e. Metadata for 20 recent Inbox messages.
    sample = inbox[:20]
    metadata_shapes: dict[str, int] = {}
    encoded_subjects = 0
    subjects_seen = 0
    entity_snippets = 0
    with_parts = 0
    filename_in_metadata = 0
    sizes = []
    senders: list[str] = []
    for message in sample:
        got = gmail.metadata("metadata-get", message["id"], ["From", "Subject"])
        sizes.append(len(json.dumps(got)))
        shape = json.dumps(sorted(got))
        metadata_shapes[shape] = metadata_shapes.get(shape, 0) + 1
        payload = got.get("payload", {})
        if payload.get("parts"):
            with_parts += 1
        if part_has_filename(payload):
            filename_in_metadata += 1
        for header in payload.get("headers", []):
            if header.get("name", "").lower() == "subject":
                subjects_seen += 1
                if re.search(r"=\?[^?]+\?[bBqQ]\?", header.get("value", "")):
                    encoded_subjects += 1
            if header.get("name", "").lower() == "from":
                address = parseaddr(header.get("value", ""))[1].lower()
                if address and address not in senders:
                    senders.append(address)
        if re.search(r"&(#\d+|#x[0-9a-fA-F]+|[A-Za-z]+);", got.get("snippet", "")):
            entity_snippets += 1
    first_payload_keys = []
    if sample:
        probe = gmail.metadata("metadata-shape", sample[0]["id"], ["From", "Subject"])
        first_payload_keys = sorted(probe.get("payload", {}))
        payload_shape = key_shape(probe.get("payload", {}))
        payload_header_names = sorted({h.get("name", "") for h in probe.get("payload", {}).get("headers", [])})
    else:
        payload_header_names = []
        payload_shape = None
    report["e_metadata"] = {
        "sampled": len(sample),
        "topLevelKeySets": [{"keys": json.loads(k), "messages": n} for k, n in metadata_shapes.items()],
        "payloadKeys": first_payload_keys,
        "payloadHeaderNamesReturned": payload_header_names,
        "payloadShapeOfOneMessage": payload_shape,
        "responseBytes": {"min": min(sizes, default=0), "max": max(sizes, default=0),
                          "median": sorted(sizes)[len(sizes) // 2] if sizes else 0},
        "messagesWithPayloadParts": with_parts,
        "messagesWithAFilenameInMetadata": filename_in_metadata,
        "subjectsSeen": subjects_seen,
        "subjectsStillRfc2047Encoded": encoded_subjects,
        "snippetsWithHtmlEntities": entity_snippets,
        "metadataGetTimes": timing_summary([c for c in gmail.calls if c.kind == "metadata-get"]),
    }

    # e (continued). The cheapest way to tell attachments: one list with has:attachment over the same window,
    # and, for comparison, format=minimal per message (minimal carries no payload, so it cannot tell).
    attached, _ = gmail.list_ids("attachment-list", "INBOX", f"after:{after} has:attachment", 500, 500)
    attached_ids = {m["id"] for m in attached}
    minimal_keys: list[str] = []
    for message in sample[:5]:
        got = gmail.gmail_get("minimal-get", f"{MESSAGES_PATH}/{message['id']}", [("format", "minimal")])
        minimal_keys = sorted(got)
    # Does payload.mimeType (which metadata format DOES return) agree with the search? Over the sample plus every
    # message the search called attached, at most 40.
    compare_ids = list(dict.fromkeys([m["id"] for m in sample] + sorted(attached_ids)))[:40]
    mime_counts: dict[str, int] = {}
    agreement = {"searchYesMixedYes": 0, "searchYesMixedNo": 0, "searchNoMixedYes": 0, "searchNoMixedNo": 0}
    for message_id in compare_ids:
        got = gmail.metadata("mime-compare-get", message_id, ["From"])
        mime = got.get("payload", {}).get("mimeType", "")
        mime_counts[mime] = mime_counts.get(mime, 0) + 1
        key = ("searchYes" if message_id in attached_ids else "searchNo") + ("MixedYes" if mime == "multipart/mixed" else "MixedNo")
        agreement[key] += 1
    report["e_attachments"] = {
        "comparedMessages": len(compare_ids),
        "payloadMimeTypeCounts": dict(sorted(mime_counts.items())),
        "hasAttachmentSearchVersusMultipartMixed": agreement,
        "hasAttachmentListCalls": timing_summary([c for c in gmail.calls if c.kind == "attachment-list"]),
        "inboxLast30DaysWithAttachment": len(attached_ids),
        "sampleOf20WithAttachmentBySearch": sum(1 for m in sample if m["id"] in attached_ids),
        "minimalFormatKeys": minimal_keys,
        "minimalGetTimes": timing_summary([c for c in gmail.calls if c.kind == "minimal-get"]),
    }

    # f. knownSender both ways.
    started = time.perf_counter()
    sent, sent_pages = gmail.list_ids("sent-list", "SENT", None, 500, 500)
    written_to: set[str] = set()
    for message in sent:
        got = gmail.metadata("sent-get", message["id"], ["To", "Cc", "Bcc"])
        values = [h.get("value", "") for h in got.get("payload", {}).get("headers", [])]
        written_to.update(a.lower() for _, a in getaddresses(values) if a)
    cache_ms = (time.perf_counter() - started) * 1000
    ten = senders[:10]
    per_sender_known: list[bool] = []
    search_records_before = len(gmail.calls)
    for address in ten:
        page = gmail.gmail_get("sent-search", MESSAGES_PATH, [
            ("labelIds", "SENT"), ("q", f"to:{address} OR cc:{address} OR bcc:{address}"), ("maxResults", "1")])
        per_sender_known.append(bool(page.get("messages")))
    report["f_knownSender"] = {
        "a_sentCache": {
            "sentMessages": len(sent),
            "listPageSizes": sent_pages,
            "distinctAddressesWrittenTo": len(written_to),
            "wholeBuildMs": round(cache_ms),
            "sentGetTimes": timing_summary([c for c in gmail.calls if c.kind == "sent-get"]),
        },
        "b_searchPerSender": {
            "senders": len(ten),
            "searchTimes": timing_summary(gmail.calls[search_records_before:]),
            "knownBySearch": sum(per_sender_known),
        },
        "agreement": {
            "knownByCache": sum(1 for a in ten if a in written_to),
            "knownBySearch": sum(per_sender_known),
            "sendersWhereTheyDiffer": sum(1 for a, k in zip(ten, per_sender_known) if (a in written_to) != k),
        },
    }

    # g. Message ids: length, alphabet, stability across two list calls.
    ids = [m["id"] for m in inbox]
    again, _ = gmail.list_ids("inbox-relist", "INBOX", f"after:{after}", 100, 200)
    again_ids = [m["id"] for m in again]
    report["g_ids"] = {
        "lengths": sorted({len(i) for i in ids}),
        "allLowercaseHex": all(re.fullmatch(r"[0-9a-f]+", i) for i in ids),
        "allLettersAndDigits": all(MESSAGE_ID.fullmatch(i) for i in ids),
        "sameIdsInSecondList": ids == again_ids,
        "idEqualsThreadIdCount": sum(1 for m in inbox if m.get("id") == m.get("threadId")),
        "listedCount": len(ids),
    }

    # h. A realistic run of 10 messages: one list since a cursor, one metadata GET each, one attachment list.
    started = time.perf_counter()
    ten_messages, _ = gmail.list_ids("run-list", "INBOX", f"after:{after}", 10, 10)
    run_senders: set[str] = set()
    for message in ten_messages:
        got = gmail.metadata("run-get", message["id"], ["From", "Subject"])
        for header in got.get("payload", {}).get("headers", []):
            if header.get("name", "").lower() == "from":
                run_senders.add(parseaddr(header.get("value", ""))[1].lower())
    gmail.list_ids("run-attachment-list", "INBOX", f"after:{after} has:attachment", 500, 500)
    run_senders.discard("")
    for address in run_senders:
        gmail.gmail_get("run-sent-search", MESSAGES_PATH, [
            ("labelIds", "SENT"), ("q", f"to:{address} OR cc:{address} OR bcc:{address}"), ("maxResults", "1")])
    report["h_rate"] = {
        "realisticRunOf10Ms": round((time.perf_counter() - started) * 1000),
        "realisticRunCalls": 1 + len(ten_messages) + 1 + len(run_senders),
        "allCalls": timing_summary(gmail.calls),
        "statusCounts": {str(s): sum(1 for c in gmail.calls if c.status == s) for s in sorted({c.status for c in gmail.calls})},
    }
    return report


# ---------------------------------------------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------------------------------------------

def main(argv: list[str] | None = None, transport: Transport = urllib_transport, home: Path | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--address", required=True, help="the mailbox address; never printed")
    parser.add_argument("--force-refresh", action="store_true", help="refresh once even if the token is still valid")
    parser.add_argument("--measure", action="store_true", help="also take the read-only measurements")
    args = parser.parse_args(argv)

    home = home or (account_record_home() if platform.system() != "Windows" else Path.home())
    out: dict = {
        "environment": {
            "HOME": os.environ.get("HOME"),
            "LOCALAPPDATA": os.environ.get("LOCALAPPDATA"),
            "ccVariableNames": sorted(k for k in os.environ if k.startswith("CC_")),
            "accountRecordHome": "~",
        }
    }
    try:
        store = gmail_store(home=home)
        out["a_lookup"] = {"store": tilde(store, home),
                           "accountDirectoriesExamined": len(list((store / "accounts").glob("*/config.json")))
                           if (store / "accounts").is_dir() else 0}
        account = find_account(store, args.address)
        token_path, data = load_token(account)
        out["a_lookup"].update({"accountNickname": account.nickname, "tokenFile": tilde(token_path, home)})

        before_stat = token_path.stat()
        before_keys = list(data)
        data_in_use, renewed = ensure_fresh_token(token_path, data, transport, force=args.force_refresh)
        after_text = token_path.read_text(encoding="utf-8")
        after = json.loads(after_text)
        after_stat = token_path.stat()
        changed = {"token", "expiry", "refresh_token"}
        out["b_refresh"] = {
            "forced": args.force_refresh,
            "renewed": renewed,
            "modeBefore": oct(before_stat.st_mode & 0o777),
            "modeAfter": oct(after_stat.st_mode & 0o777),
            "keysBefore": before_keys,
            "keysAfter": list(after),
            "sameKeysSameOrder": before_keys == list(after),
            "unchangedValues": {k: {"before": hash_prefix(data[k]), "after": hash_prefix(after.get(k)),
                                    "equal": data[k] == after.get(k)} for k in before_keys if k not in changed},
            "tokenChanged": data.get("token") != after.get("token"),
            "refreshTokenChanged": data.get("refresh_token") != after.get("refresh_token"),
            "expiryAfterMinutesFromNow": round(((parse_expiry(after.get("expiry")) or utcnow()) - utcnow()).total_seconds() / 60),
        }

        gmail = Gmail(data_in_use["token"], transport)
        probe = gmail.gmail_get("inbox-list", MESSAGES_PATH, [("labelIds", "INBOX"), ("maxResults", "1")])
        out["b_refresh"]["readOnlyGetWorked"] = "messages" in probe or "resultSizeEstimate" in probe
        if args.measure:
            gmail.calls.clear()
            out.update(measure(gmail, data_in_use))
    except ChannelFailed as error:
        out["failed"] = str(error)
        print(json.dumps(out, indent=2))
        return 1
    print(json.dumps(out, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())

# /// script
# requires-python = ">=3.10"
# dependencies = ["msal>=1.28"]
# ///
"""The Mail Desk's screening check: read new mail, and file what the Gateway queued. It never decides.

Run by a Director every 5 minutes from the published Mail Desk factory, with every CC_ variable stripped from its
environment except the CC_CHECK_ ones. The whole contract - the memory, the output, every limit - is in
docs/qa/mail-desk/phase-2/CONTRACT.md. In short:

- The memory (the file named by CC_CHECK_STATE_FILE) is a JSON object whose "channels" list the Gateway writes: one
  entry per mailbox, with its address, provider and mode. This check keeps its own fields in each entry (cursor,
  sentCache, keys) and removes from "filingQueue" the items it attempted. It writes the whole memory back, keeping
  every field it does not own exactly as it was.
- Live mode only: each queued item is moved to Archive or Junk Email, and nothing else. Observe mode moves nothing
  and leaves the queue as it is.
- New Inbox mail since the cursor, oldest first, is reported in batches of at most 10 messages and 7 KB of context
  across all channels; the cursor moves only past what was reported, and the next run continues.
- Standard output is exactly {"count": N, "context": "<JSON>"}.
- Any failure is a non-zero exit with the reason on standard error, and the memory is NOT rewritten, so the same mail
  is read again next run.

Every Microsoft Graph call goes through graph_request(), the one request guard. It lets out only GET to
graph.microsoft.com and POST /v1.0/me/messages/{id}/move with a body of exactly {"destinationId": "archive"} or
{"destinationId": "junkemail"}. The saved sign-in can send mail (Mail Desk phase 0 finding), so this guard is the only
thing that keeps the Mail Desk from sending, replying, forwarding, changing or deleting anything.

Standard error is the check's only log. It says what was done in plain words, and never holds a token, a sender, a
subject or any body text.
"""

import hashlib
import json
import os
import re
import socket
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

import msal

# ---------------------------------------------------------------------------------------------------- limits

CONTEXT_BUDGET_BYTES = 7168     # phase 0 (b): fill the context to 7 KB of UTF-8 (the Director's limit is 8 KB)
MAX_MESSAGES_PER_RUN = 10       # phase 0 (b): and stop at 10 messages, across all channels together
FILING_BUDGET_BYTES = CONTEXT_BUDGET_BYTES // 2   # filing results may use at most half; new mail gets the rest
OUTPUT_CAP_BYTES = 16384        # TriggerCheckLimits.OutputCap
MEMORY_CAP_BYTES = 64 * 1024    # TriggerCheckLimits.StateCapBytes: above it the Director keeps no memory at all
PREVIEW_CHARS = 200
FIRST_RUN_MESSAGES = 200        # mission D18: the most recent 200, or the last 30 days, whichever is fewer
FIRST_RUN_DAYS = 30
PAGE_SIZE = 50
MAX_INBOX_PAGES = 20
SENT_REFRESH = timedelta(days=1)
SENT_PAGE_SIZE = 100
SENT_MAX_PAGES = 5              # the most recent 500 sent messages
SENT_MAX_ADDRESSES = 400
KEYS_KEPT = 40                  # per channel, the most recently reported; see CONTRACT.md "Short keys"
KEYS_MAX_AGE = timedelta(days=7)
KEY_HEX = 16
ADDRESS_HASH_HEX = 12

DESTINATIONS = ("archive", "junkemail")
MODES = ("observe", "live")

# ---------------------------------------------------------------------------------------------------- Outlook

# cc-outlook's own Azure app and authority (tools/cc-outlook/src/auth.py, MSALTokenBackend and
# authenticate_device_code_with_cache: authority "https://login.microsoftonline.com/common"), as phase 0 used them.
OUTLOOK_CLIENT_ID = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
OUTLOOK_AUTHORITY = "https://login.microsoftonline.com/common"
OUTLOOK_SCOPE = "https://graph.microsoft.com/Mail.ReadWrite"   # never Mail.Send
GRAPH_HOST = "graph.microsoft.com"
GRAPH = f"https://{GRAPH_HOST}/v1.0"
# A Graph message id as this check will put it in a move path: letters, digits and the URL-safe "-", "_" and "=".
# Anything else (a "/", a "%", a ".") is refused, so no id can reach another path.
MESSAGE_ID = re.compile(r"[A-Za-z0-9_=-]+")
MOVE_PATH = re.compile(r"/v1\.0/me/messages/([^/]+)/move")


class CheckFailed(Exception):
    """A reason the check cannot answer. Printed on standard error; the exit code is 1; the memory is not written."""


class RefusedRequest(Exception):
    """The request guard refused a Graph call. Raised before anything reaches the network."""


class GraphError(Exception):
    """Graph answered with an HTTP error status."""

    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def log(text):
    print(f"screen.py: {text}", file=sys.stderr)


def utc_now():
    return datetime.now(timezone.utc)


def iso(moment):
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------------------------------- the guard


def graph_request(method, url, token, body=None):
    """The one way this check talks to Microsoft Graph. Refuses, before any network call, everything but GET to
    graph.microsoft.com and the move of one message to Archive or Junk Email."""
    check_request(method, url, body)
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    return transport(method, url, headers, data)


def check_request(method, url, body):
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.netloc != GRAPH_HOST:
        raise RefusedRequest(f"refused: {parts.scheme}://{parts.netloc} is not https://{GRAPH_HOST}")
    if parts.fragment:
        raise RefusedRequest("refused: a Graph address with a fragment")
    if ".." in parts.path or "%" in parts.path or "\\" in parts.path:
        raise RefusedRequest("refused: the path holds '..', '%' or '\\'")
    if method == "GET":
        if body is not None:
            raise RefusedRequest("refused: a GET with a body")
        if not parts.path.startswith("/v1.0/"):
            raise RefusedRequest(f"refused: GET {parts.path} is not under /v1.0/")
        return
    if method == "POST":
        match = MOVE_PATH.fullmatch(parts.path)
        if not match:
            raise RefusedRequest(f"refused: POST {parts.path} is not the move of one message")
        if not MESSAGE_ID.fullmatch(match.group(1)):
            raise RefusedRequest("refused: the message id holds a character a Graph id never has")
        if parts.query:
            raise RefusedRequest("refused: a move with a query")
        if not (isinstance(body, dict) and list(body) == ["destinationId"] and body["destinationId"] in DESTINATIONS):
            raise RefusedRequest('refused: a move body must be exactly {"destinationId": "archive"} or '
                                 '{"destinationId": "junkemail"}')
        return
    raise RefusedRequest(f"refused: {method} is never sent to Graph; only GET and the move to Archive or Junk Email")


def transport(method, url, headers, data):
    """The network. Only graph_request() calls it; the tests replace it with a fake Graph that records every call."""
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
    except urllib.error.HTTPError as e:
        raise GraphError(e.code, f"{method} {url.split('?')[0]} answered HTTP {e.code}") from None
    except (urllib.error.URLError, OSError) as e:
        raise CheckFailed(f"Microsoft Graph could not be reached ({method} {url.split('?')[0]}): "
                          f"{getattr(e, 'reason', e)}") from None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise CheckFailed(f"{method} {url.split('?')[0]} answered something that is not JSON") from None


def graph_get(url, token):
    try:
        page = graph_request("GET", url, token)
    except GraphError as e:
        raise CheckFailed(str(e)) from None
    if not isinstance(page, dict):
        raise CheckFailed(f"GET {url.split('?')[0]} answered a page that is not a JSON object")
    return page


# ---------------------------------------------------------------------------------------------------- text


# Characters that show nothing but are not in Unicode's format or control categories. Email senders pad the preview
# with them (phase 0 (c): 8 of 19 real previews were mostly padding).
INVISIBLE_LETTERS = {"͏", "ᅟ", "ᅠ", "ㅤ", "ﾠ", "⠀"}


def clean_text(text):
    """Strip invisible characters FIRST - zero-width characters, the soft hyphen, the byte order mark, every other
    format and control character - and collapse every run of whitespace to one space."""
    kept = []
    for ch in text or "":
        if ch.isspace():
            kept.append(" ")
        elif unicodedata.category(ch) in ("Cc", "Cf", "Cs", "Co") or ch in INVISIBLE_LETTERS:
            continue
        else:
            kept.append(ch)
    return " ".join("".join(kept).split())


def preview_of(body_preview):
    """Cleaned, THEN cut to 200 characters, so padding never uses up the preview."""
    return clean_text(body_preview)[:PREVIEW_CHARS]


def short_key(address, message_id):
    return hashlib.sha256(f"{address}\n{message_id}".encode("utf-8")).hexdigest()[:KEY_HEX]


def address_hash(address):
    return hashlib.sha256(address.strip().lower().encode("utf-8")).hexdigest()[:ADDRESS_HASH_HEX]


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def utf8_size(text):
    return len(text.encode("utf-8"))


# ---------------------------------------------------------------------------------------------------- sign-in


def account_record_home():
    """The user's real home from the operating system's account record, never from HOME (phase 0 finding)."""
    if os.name != "posix":
        return os.path.expanduser("~")
    import pwd
    return pwd.getpwuid(os.getuid()).pw_dir


def director_base(home):
    """The Director's per-user base folder: Windows %LOCALAPPDATA%, macOS Application Support, Linux ~/.local/share.
    Phase 0: a check cannot use CC_DIRECTOR_ROOT (stripped), and there is no fallback to ~/.cc-director."""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        return Path(local) / "cc-director" if local else Path(home) / "AppData" / "Local" / "cc-director"
    if sys.platform == "darwin":
        return Path(home) / "Library" / "Application Support" / "cc-director"
    return Path(home) / ".local" / "share" / "cc-director"


def outlook_cache_name(address):
    """cc-outlook's file naming rule, from get_msal_token_path in tools/cc-outlook/src/auth.py (line 355 on
    origin/main 266f16865): safe_name = email.replace('@', '_').replace('.', '_'); the file is f'{safe_name}_msal.json'."""
    safe_name = address.replace("@", "_").replace(".", "_")
    return f"{safe_name}_msal.json"


def outlook_fix(channel):
    """cc-outlook takes an account by nickname or by its address (resolve_account in tools/cc-outlook/src/auth.py), so
    the fix names the nickname the Gateway gave, or else the address itself - never a guessed nickname."""
    return f"run cc-outlook -a {channel.get('nickname') or channel['address']} auth"


def find_outlook_sign_in(channel, base):
    name = outlook_cache_name(channel["address"])
    found = sorted(Path(base).glob(f"instances/*/config/outlook/tokens/{name}"))
    machine = socket.gethostname()
    if not found:
        raise CheckFailed(f"{channel['address']} is not signed in on {machine} (looked for "
                          f"{base}/instances/*/config/outlook/tokens/{name} and found none): {outlook_fix(channel)}")
    if len(found) > 1:
        raise CheckFailed(f"{channel['address']} is signed in more than once on {machine}, so the check cannot tell "
                          f"which sign-in to use: {', '.join(str(p) for p in found)}. Keep one: sign out of the others "
                          f"with cc-outlook")
    return found[0]


class OutlookReader:
    """Everything Outlook-specific: the saved cc-outlook sign-in, MSAL, and the Graph addresses."""

    def __init__(self, channel):
        self.channel = channel
        self.token = None

    def sign_in(self):
        address = self.channel["address"]
        path = find_outlook_sign_in(self.channel, director_base(account_record_home()))
        cache = msal.SerializableTokenCache()
        try:
            cache.deserialize(path.read_text(encoding="utf-8"))
        except ValueError:
            raise CheckFailed(f"the saved sign-in for {address} at {path} is not a sign-in cache: "
                              f"{outlook_fix(self.channel)}") from None
        app = msal.PublicClientApplication(client_id=OUTLOOK_CLIENT_ID, authority=OUTLOOK_AUTHORITY, token_cache=cache)
        accounts = app.get_accounts(username=address)
        if not accounts:
            raise CheckFailed(f"{address} is not signed in on {socket.gethostname()} (the saved sign-in at {path} holds "
                              f"no account for it): {outlook_fix(self.channel)}")
        result = app.acquire_token_silent([OUTLOOK_SCOPE], account=accounts[0])
        if not result or "access_token" not in result:
            error = (result or {}).get("error", "no token came back")
            raise CheckFailed(f"the sign-in for {address} on {socket.gethostname()} has expired and could not be renewed "
                              f"({error}): {outlook_fix(self.channel)}")
        # Written back exactly as cc-outlook does (MSALTokenBackend._save_cache): when it changed, to the same file,
        # then owner-only. Never into the memory.
        if cache.has_state_changed:
            path.write_text(cache.serialize(), encoding="utf-8")
            if os.name == "posix":
                os.chmod(path, 0o600)
            log(f"{address}: the renewed sign-in was written back to its file")
        self.token = result["access_token"]

    def sent_hashes(self):
        """The hashed addresses of everyone in the most recent sent messages, newest first, at most 400."""
        query = urllib.parse.urlencode({"$select": "toRecipients,ccRecipients,bccRecipients",
                                        "$orderby": "sentDateTime desc", "$top": str(SENT_PAGE_SIZE)})
        url = f"{GRAPH}/me/mailFolders/sentitems/messages?{query}"
        hashes = []
        for _ in range(SENT_MAX_PAGES):
            page = graph_get(url, self.token)
            for message in page_values(page, url):
                for field in ("toRecipients", "ccRecipients", "bccRecipients"):
                    for recipient in message.get(field) or []:
                        address = ((recipient or {}).get("emailAddress") or {}).get("address")
                        if address:
                            digest = address_hash(address)
                            if digest not in hashes:
                                hashes.append(digest)
            url = page.get("@odata.nextLink")
            if not url or len(hashes) >= SENT_MAX_ADDRESSES:
                break
        return hashes[:SENT_MAX_ADDRESSES]

    def inbox_first_run(self, now):
        """First run: the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer. Read
        newest first and returned oldest first."""
        query = urllib.parse.urlencode({"$select": INBOX_FIELDS,
                                        "$filter": f"receivedDateTime ge {iso(now - timedelta(days=FIRST_RUN_DAYS))}",
                                        "$orderby": "receivedDateTime desc", "$top": str(PAGE_SIZE)})
        url = f"{GRAPH}/me/mailFolders/inbox/messages?{query}"
        seen = []
        while url and len(seen) < FIRST_RUN_MESSAGES:
            page = graph_get(url, self.token)
            seen.extend(read_message(m, url) for m in page_values(page, url))
            url = page.get("@odata.nextLink")
        return list(reversed(seen[:FIRST_RUN_MESSAGES]))

    def inbox_since(self, cursor, wanted):
        """Inbox messages received at or after the cursor's time and not among the ids already read at exactly that
        time, oldest first, until `wanted` are found or the folder ends. Returns (messages, more)."""
        already = set(cursor["idsAtNewest"])
        query = urllib.parse.urlencode({"$select": INBOX_FIELDS, "$filter": f"receivedDateTime ge {cursor['newest']}",
                                        "$orderby": "receivedDateTime asc", "$top": str(PAGE_SIZE)})
        url = f"{GRAPH}/me/mailFolders/inbox/messages?{query}"
        fresh = []
        for _ in range(MAX_INBOX_PAGES):
            page = graph_get(url, self.token)
            fresh.extend(m for m in (read_message(x, url) for x in page_values(page, url)) if m["id"] not in already)
            url = page.get("@odata.nextLink")
            if not url or len(fresh) > wanted:
                break
        return fresh, bool(url) or len(fresh) > wanted

    def move(self, message_id, destination):
        """Moves one message. Returns "moved", or "already-gone" when Graph no longer has it."""
        try:
            graph_request("POST", f"{GRAPH}/me/messages/{message_id}/move", self.token, {"destinationId": destination})
        except GraphError as e:
            if e.status == 404:
                return "already-gone"
            raise CheckFailed(f"moving a message to {destination} failed: {e}") from None
        return "moved"


INBOX_FIELDS = "id,receivedDateTime,from,subject,hasAttachments,bodyPreview"

READERS = {"outlook": OutlookReader}


def page_values(page, url):
    values = page.get("value")
    if not isinstance(values, list):
        raise CheckFailed(f"GET {url.split('?')[0]} answered a page without a 'value' list")
    return values


def read_message(raw, url):
    try:
        sender = (raw.get("from") or {}).get("emailAddress") or {}
        message = {"id": raw["id"], "received": raw["receivedDateTime"], "from": (sender.get("address") or "").lower(),
                   "name": sender.get("name") or "", "subject": raw.get("subject") or "",
                   "hasAttachments": bool(raw.get("hasAttachments")), "preview": raw.get("bodyPreview") or ""}
    except (AttributeError, KeyError, TypeError):
        raise CheckFailed(f"GET {url.split('?')[0]} answered a message without an id or a receive time") from None
    if not isinstance(message["id"], str) or not isinstance(message["received"], str):
        raise CheckFailed(f"GET {url.split('?')[0]} answered a message whose id or receive time is not text")
    return message


# ---------------------------------------------------------------------------------------------------- memory


def read_memory(path):
    text = Path(path).read_text(encoding="utf-8").strip()
    if not text:
        raise CheckFailed("the memory is empty: the Gateway writes the Mail Desk channels into it before the first run")
    try:
        memory = json.loads(text)
    except json.JSONDecodeError:
        raise CheckFailed("the memory is not JSON") from None
    channels = memory.get("channels") if isinstance(memory, dict) else None
    if not isinstance(channels, list) or not channels:
        raise CheckFailed("the memory holds no 'channels': the Gateway writes the Mail Desk channels into it")
    addresses = set()
    for channel in channels:
        if not isinstance(channel, dict) or not isinstance(channel.get("address"), str) or "@" not in channel["address"]:
            raise CheckFailed("a channel in the memory has no 'address'")
        address = channel["address"].strip().lower()
        if address in addresses:
            raise CheckFailed(f"the channel {address} is in the memory twice")
        addresses.add(address)
        if channel.get("mode") not in MODES:
            raise CheckFailed(f"the channel {address} has mode {channel.get('mode')!r}; it must be observe or live")
        if channel.get("provider") not in READERS:
            raise CheckFailed(f"provider {channel.get('provider')} has no reader in this version of screen.py "
                              f"(channel {address}); this version reads: {', '.join(sorted(READERS))}")
        queue = channel.get("filingQueue", [])
        if not isinstance(queue, list):
            raise CheckFailed(f"the channel {address} has a 'filingQueue' that is not a list")
        cursor = channel.get("cursor")
        if cursor is not None and not (isinstance(cursor, dict) and isinstance(cursor.get("newest"), str)
                                       and isinstance(cursor.get("idsAtNewest"), list)):
            raise CheckFailed(f"the channel {address} has a 'cursor' this check did not write")
    return memory


def prune_keys(keys, queue, now):
    """Keeps a key while it is in the filing queue, or is among the 40 most recently reported and under 7 days old."""
    queued = {item.get("key") for item in queue if isinstance(item, dict)}
    recent = sorted(keys.items(), key=lambda kv: kv[1]["reportedUtc"], reverse=True)
    oldest = iso(now - KEYS_MAX_AGE)
    kept = {k: v for k, v in recent[:KEYS_KEPT] if v["reportedUtc"] >= oldest}
    kept.update({k: v for k, v in keys.items() if k in queued})
    return kept


# ---------------------------------------------------------------------------------------------------- the run


def file_queue(channel, reader, keys, budget_left):
    """Live mode: carry out queued items oldest first while their results fit in the filing budget. Returns the
    results, the items left in the queue, and the bytes used."""
    queue = channel.get("filingQueue", [])
    results, used = [], 0
    for index, item in enumerate(queue):
        key = item.get("key") if isinstance(item, dict) else None
        destination = item.get("to") if isinstance(item, dict) else None
        worst = utf8_size(compact({"key": key, "to": destination, "result": "already-gone",
                                   "reason": "no such key in this channel's key table"})) + 1
        if used + worst > budget_left:
            return results, queue[index:], used
        if destination not in DESTINATIONS:
            result = {"key": key, "to": destination, "result": "refused", "reason": "not archive or junkemail"}
        elif not isinstance(key, str) or key not in keys:
            result = {"key": key, "to": destination, "result": "refused",
                      "reason": "no such key in this channel's key table"}
        else:
            try:
                result = {"key": key, "to": destination, "result": reader.move(keys[key]["id"], destination)}
            except RefusedRequest as e:
                result = {"key": key, "to": destination, "result": "refused", "reason": str(e)}
        results.append(result)
        used += utf8_size(compact(result)) + 1
    return results, [], used


def trim_to_fit(entry, room):
    """A message that alone does not fit: drop its preview, then shorten its subject and then its name, until it
    fits. It is marked "trimmed"; the sender, key and time are never cut."""
    entry = dict(entry, trimmed=True, preview="")
    for field in ("subject", "name"):
        while utf8_size(compact(entry)) > room and entry[field]:
            over = utf8_size(compact(entry)) - room
            # A character is at most 6 bytes once written as JSON, so cutting over // 6 + 1 never cuts too much.
            entry[field] = entry[field][:-(over // 6 + 1)]
    if utf8_size(compact(entry)) > room:
        raise CheckFailed("a message's sender address alone is larger than the context budget")
    return entry


def build_context(sections):
    return compact({"version": 1, "channels": sections})


def run(state_path, now):
    memory = read_memory(state_path)
    channels = memory["channels"]
    log(f"{len(channels)} channel(s): " + ", ".join(f"{c['address']} ({c['provider']}, {c['mode']})" for c in channels))

    readers = [READERS[c["provider"]](c) for c in channels]
    for reader in readers:
        reader.sign_in()

    sections, candidates, more, filed_total = [], [], [], 0
    filing_room = FILING_BUDGET_BYTES
    for index, (channel, reader) in enumerate(zip(channels, readers)):
        address = channel["address"].strip().lower()
        keys = channel.get("keys", {})
        # "more" is in place from the start, as false (one byte longer than true), so the budget is measured on the
        # context as it is finally written.
        section = {"address": address, "provider": channel["provider"], "mode": channel["mode"], "messages": [],
                   "filing": [], "more": False}
        queue = channel.get("filingQueue", [])
        if queue and channel["mode"] == "observe":
            section["filingNotCarriedOut"] = {"count": len(queue), "reason": "the channel is in observe mode"}
            log(f"{address}: {len(queue)} queued filing item(s) NOT carried out: the channel is in observe mode")
        elif queue:
            results, left, used = file_queue(channel, reader, keys, filing_room)
            filing_room -= used
            section["filing"] = results
            channel["filingQueue"] = left
            filed_total += len(results)
            log(f"{address}: filing " + ", ".join(f"{r['key']} {r['to']} {r['result']}" for r in results)
                + (f"; {len(left)} left queued for the next run" if left else ""))

        sent = channel.get("sentCache")
        if not sent or sent.get("refreshedUtc", "") < iso(now - SENT_REFRESH):
            sent = {"refreshedUtc": iso(now), "hashes": reader.sent_hashes()}
            channel["sentCache"] = sent
            log(f"{address}: the Sent folder was read again: {len(sent['hashes'])} address(es) written to")
        known = set(sent["hashes"])

        if channel.get("cursor"):
            found, more_left = reader.inbox_since(channel["cursor"], MAX_MESSAGES_PER_RUN)
        else:
            found = reader.inbox_first_run(now)
            more_left = False
            log(f"{address}: first run: {len(found)} message(s) in the look-back")
        for order, message in enumerate(found):
            candidates.append((message["received"], index, order, message, known))
        more.append(more_left)
        sections.append(section)

    # New mail across every channel, oldest first, until 10 messages or the 7 KB budget.
    candidates.sort(key=lambda c: (c[0], c[1], c[2]))
    reported = [[] for _ in channels]
    reported_count = 0
    for received, index, _order, message, known in candidates:
        if reported_count >= MAX_MESSAGES_PER_RUN:
            break
        address = sections[index]["address"]
        key = short_key(address, message["id"])
        known_keys = channels[index].setdefault("keys", {})
        if key in known_keys and known_keys[key]["id"] != message["id"]:
            raise CheckFailed(f"two messages of {address} share the short key {key}")
        entry = {"key": key, "received": received, "from": message["from"], "name": clean_text(message["name"]),
                 "subject": clean_text(message["subject"]), "hasAttachments": message["hasAttachments"],
                 "knownSender": address_hash(message["from"]) in known if message["from"] else False,
                 "preview": preview_of(message["preview"])}
        sections[index]["messages"].append(entry)
        if utf8_size(build_context(sections)) > CONTEXT_BUDGET_BYTES:
            sections[index]["messages"].pop()
            if reported_count:
                break
            room = CONTEXT_BUDGET_BYTES - utf8_size(build_context(sections)) - 1
            entry = trim_to_fit(entry, room)
            sections[index]["messages"].append(entry)
            log(f"{address}: message {key} alone was larger than the budget, so it was reported trimmed")
        known_keys[key] = {"id": message["id"], "reportedUtc": iso(now)}
        reported[index].append(message)
        reported_count += 1

    for index, channel in enumerate(channels):
        mine = reported[index]
        left = len([c for c in candidates if c[1] == index]) - len(mine)
        sections[index]["more"] = more[index] or left > 0
        if mine:
            newest = mine[-1]["received"]
            cursor = channel.get("cursor")
            ids = {m["id"] for m in mine if m["received"] == newest}
            if cursor and cursor["newest"] == newest:
                ids |= set(cursor["idsAtNewest"])
            channel["cursor"] = {"newest": newest, "idsAtNewest": sorted(ids)}
        channel["keys"] = prune_keys(channel.get("keys", {}), channel.get("filingQueue", []), now)
        log(f"{sections[index]['address']}: reported {len(mine)} new message(s)"
            + ("; more is waiting for the next run" if sections[index]["more"] else ""))

    context = build_context(sections)
    if utf8_size(context) > CONTEXT_BUDGET_BYTES:
        raise CheckFailed(f"the context came to {utf8_size(context)} bytes, over the budget of {CONTEXT_BUDGET_BYTES}")
    output = compact({"count": reported_count + filed_total, "context": context})
    if utf8_size(output) > OUTPUT_CAP_BYTES:
        raise CheckFailed(f"the output is {utf8_size(output)} bytes, over the Director's {OUTPUT_CAP_BYTES}")
    memory_text = compact(memory)
    if utf8_size(memory_text) > MEMORY_CAP_BYTES:
        raise CheckFailed(f"the memory would be {utf8_size(memory_text)} bytes, over the Director's {MEMORY_CAP_BYTES}; "
                          f"the Director would keep none of it, so nothing is written")
    return memory_text, output


def main():
    try:
        state_path = os.environ.get("CC_CHECK_STATE_FILE")
        if not state_path:
            raise CheckFailed("CC_CHECK_STATE_FILE is not set; the Director names the memory file for every run")
        if not os.path.isfile(state_path):
            raise CheckFailed(f"the memory file {state_path} does not exist; the Director writes it before the run")
        memory_text, output = run(state_path, utc_now())
    except (CheckFailed, RefusedRequest) as e:
        print(f"check failed: {e}", file=sys.stderr)
        return 1
    Path(state_path).write_text(memory_text, encoding="utf-8")
    sys.stdout.buffer.write(output.encode("utf-8") + b"\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())

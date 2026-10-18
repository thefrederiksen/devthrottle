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
- Any failure is a non-zero exit, and the memory is NOT rewritten, so the same mail is read again next run. The FIRST
  line of standard error is then one plain sentence saying what is wrong and how to fix it (the Gateway shows it as
  it is); detail and the run's log follow on later lines.

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

# The filing decisions, in the Gateway's words (phase 1 MailDeskWords). Each reader maps them to its own folders.
FILING_DECISIONS = ("archive", "junk")
MODES = ("observe", "live")


class CheckFailed(Exception):
    """A reason the check cannot answer: one plain sentence, and optional detail for the lines after it. The exit code
    is 1 and the memory is not written."""

    def __init__(self, sentence, detail=None):
        super().__init__(sentence)
        self.detail = detail


class RefusedRequest(Exception):
    """The request guard refused a Graph call. Raised before anything reaches the network."""


class GraphError(Exception):
    """Graph answered with an HTTP error status."""

    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


RUN_LOG = []


def log(text):
    """Kept until the end of the run, so that a failure's sentence is the first line of standard error."""
    RUN_LOG.append(f"screen.py: {text}")


def utc_now():
    return datetime.now(timezone.utc)


def iso(moment):
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------------------------------------------- text


# Characters that show nothing but are not in Unicode's format or control categories. Email senders pad the preview
# with them (phase 0 (c): 8 of 19 real previews were mostly padding): the combining grapheme joiner, the Hangul
# fillers, the halfwidth Hangul filler and the blank Braille pattern.
INVISIBLE_LETTERS = {chr(c) for c in (0x034F, 0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800)}


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


# ---------------------------------------------------------------------------------------------------- the guard

GRAPH_HOST = "graph.microsoft.com"
GRAPH = f"https://{GRAPH_HOST}/v1.0"
GRAPH_DESTINATIONS = ("archive", "junkemail")
# A Graph message id as this check will put it in a move path: letters, digits and the URL-safe "-", "_" and "=".
# Anything else (a "/", a "%", a ".") is refused, so no id can reach another path.
MESSAGE_ID = re.compile(r"[A-Za-z0-9_=-]+")
MOVE_PATH = re.compile(r"/v1\.0/me/messages/([^/]+)/move")


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
        if not (isinstance(body, dict) and list(body) == ["destinationId"]
                and body["destinationId"] in GRAPH_DESTINATIONS):
            raise RefusedRequest('refused: a move body must be exactly {"destinationId": "archive"} or '
                                 '{"destinationId": "junkemail"}')
        return
    raise RefusedRequest(f"refused: {method} is never sent to Graph; only GET and the move to Archive or Junk Email")


def transport(method, url, headers, data):
    """The network. Only graph_request() calls it; the tests replace urlopen with a fake Graph that records every
    call."""
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
    except urllib.error.HTTPError as e:
        raise GraphError(e.code, f"Microsoft Graph answered HTTP {e.code} to {method} {url.split('?')[0]}") from None
    except (urllib.error.URLError, OSError) as e:
        raise CheckFailed(f"Microsoft Graph could not be reached from {socket.gethostname()}",
                          f"{method} {url.split('?')[0]}: {getattr(e, 'reason', e)}") from None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise CheckFailed(f"Microsoft Graph answered something that is not JSON to {method} {url.split('?')[0]}") from None


def graph_get(url, token):
    try:
        page = graph_request("GET", url, token)
    except GraphError as e:
        raise CheckFailed(str(e)) from None
    if not isinstance(page, dict):
        raise CheckFailed(f"Microsoft Graph answered a page that is not a JSON object to GET {url.split('?')[0]}")
    return page


# ---------------------------------------------------------------------------------------------------- Outlook

# cc-outlook's own Azure app and authority (tools/cc-outlook/src/auth.py, MSALTokenBackend and
# authenticate_device_code_with_cache: authority "https://login.microsoftonline.com/common"), as phase 0 used them.
OUTLOOK_CLIENT_ID = "f575e3f4-8f62-4bfb-8f30-f776e55b51bd"
OUTLOOK_AUTHORITY = "https://login.microsoftonline.com/common"
OUTLOOK_SCOPE = "https://graph.microsoft.com/Mail.ReadWrite"   # never Mail.Send
OUTLOOK_FOLDERS = {"archive": "archive", "junk": "junkemail"}


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
    origin/main 7a365544b): safe_name = email.replace('@', '_').replace('.', '_'); the file is f'{safe_name}_msal.json'."""
    safe_name = address.replace("@", "_").replace(".", "_")
    return f"{safe_name}_msal.json"


def derive_nickname(email):
    """cc-outlook's own default nickname, from derive_nickname in tools/cc-outlook/src/auth.py: the domain label just
    before the top-level domain (email@devthrottle.com -> devthrottle)."""
    labels = [label for label in email.rsplit("@", 1)[1].strip().lower().split(".") if label]
    return labels[-2] if len(labels) >= 2 else labels[0]


def outlook_fix(address, base):
    """How to sign the address in, in cc-outlook's own words, read from cc-outlook's account list in every Director
    instance (instances/*/config/outlook/profiles.json, PROFILES_FILE in tools/cc-outlook/src/auth.py).

    An account's nickname is its stored one, or else the derived one (get_nickname), and it is used only if
    cc-outlook's nickname lookup (_build_nickname_map, where the last account wins) leads back to this address. When
    the instances disagree, the address itself is named: resolve_account matches an account's address exactly first.
    When no instance holds the account, the fix says how to add it. Never a guessed nickname."""
    names = set()
    for path in sorted(Path(base).glob("instances/*/config/outlook/profiles.json")):
        try:
            profiles = json.loads(path.read_text(encoding="utf-8")).get("profiles") or {}
            registered = [email for email in profiles if email.strip().lower() == address]
            if not registered:
                continue
            email = registered[0]
            nickname = (profiles[email] or {}).get("nickname") or derive_nickname(email)
            lookup = {(p or {}).get("nickname") or derive_nickname(e): e for e, p in profiles.items()}
        except (ValueError, AttributeError, IndexError, TypeError):
            continue  # cc-outlook cannot read this file either, so it names no account
        names.add(nickname if lookup.get(nickname) == email else email)
    if len(names) == 1:
        return f"run cc-outlook -a {names.pop()} auth"
    if names:
        return f"run cc-outlook -a {address} auth"
    return (f"add the account with cc-outlook accounts add {address} --client-id {OUTLOOK_CLIENT_ID}, "
            f"then run cc-outlook -a {address} auth")


class OutlookReader:
    """Everything Outlook-specific: the saved cc-outlook sign-in, MSAL, the Graph addresses and folder names."""

    def __init__(self, channel):
        self.address = channel["address"].strip().lower()
        self.token = None

    def not_signed_in(self, base, detail):
        return CheckFailed(f"{self.address} is not signed in on {socket.gethostname()}: {outlook_fix(self.address, base)}",
                           detail)

    def sign_in(self):
        base = director_base(account_record_home())
        name = outlook_cache_name(self.address)
        found = sorted(Path(base).glob(f"instances/*/config/outlook/tokens/{name}"))
        if not found:
            raise self.not_signed_in(base, f"looked for {base}/instances/*/config/outlook/tokens/{name} and found none")
        if len(found) > 1:
            raise CheckFailed(f"{self.address} is signed in more than once on {socket.gethostname()}, so the check cannot "
                              f"tell which sign-in to use: keep one and sign out of the others with cc-outlook",
                              "the sign-ins found: " + ", ".join(str(p) for p in found))
        path = found[0]
        cache = msal.SerializableTokenCache()
        try:
            cache.deserialize(path.read_text(encoding="utf-8"))
        except ValueError:
            raise self.not_signed_in(base, f"the saved sign-in at {path} is not a sign-in cache") from None
        app = msal.PublicClientApplication(client_id=OUTLOOK_CLIENT_ID, authority=OUTLOOK_AUTHORITY, token_cache=cache)
        accounts = app.get_accounts(username=self.address)
        if not accounts:
            raise self.not_signed_in(base, f"the saved sign-in at {path} holds no account for {self.address}")
        result = app.acquire_token_silent([OUTLOOK_SCOPE], account=accounts[0])
        if not result or "access_token" not in result:
            error = (result or {}).get("error", "no token came back")
            raise self.not_signed_in(base, f"the saved sign-in at {path} has expired and could not be renewed ({error})")
        # Written back exactly as cc-outlook does (MSALTokenBackend._save_cache): when it changed, to the same file,
        # then owner-only. Never into the memory.
        if cache.has_state_changed:
            path.write_text(cache.serialize(), encoding="utf-8")
            if os.name == "posix":
                os.chmod(path, 0o600)
            log(f"{self.address}: the renewed sign-in was written back to its file")
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
        time, oldest first, until more than `wanted` are found or the folder ends. Returns (messages, more)."""
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

    def file(self, message_id, decision):
        """Moves one message to the folder of a filing decision. Returns "moved", or "already-gone" when Graph no longer
        has it."""
        destination = OUTLOOK_FOLDERS[decision]
        try:
            graph_request("POST", f"{GRAPH}/me/messages/{message_id}/move", self.token, {"destinationId": destination})
        except GraphError as e:
            if e.status == 404:
                return "already-gone"
            raise CheckFailed(f"moving a message of {self.address} to {destination} failed: {e}") from None
        return "moved"


INBOX_FIELDS = "id,receivedDateTime,from,subject,hasAttachments,bodyPreview"

READERS = {"outlook": OutlookReader}


def page_values(page, url):
    values = page.get("value")
    if not isinstance(values, list):
        raise CheckFailed(f"Microsoft Graph answered a page without a 'value' list to GET {url.split('?')[0]}")
    return values


def read_message(raw, url):
    try:
        sender = (raw.get("from") or {}).get("emailAddress") or {}
        message = {"id": raw["id"], "received": raw["receivedDateTime"], "from": (sender.get("address") or "").lower(),
                   "name": sender.get("name") or "", "subject": raw.get("subject") or "",
                   "hasAttachments": bool(raw.get("hasAttachments")), "preview": raw.get("bodyPreview") or ""}
    except (AttributeError, KeyError, TypeError):
        raise CheckFailed(f"Microsoft Graph answered a message without an id or a receive time to GET "
                          f"{url.split('?')[0]}") from None
    if not isinstance(message["id"], str) or not isinstance(message["received"], str):
        raise CheckFailed(f"Microsoft Graph answered a message whose id or receive time is not text to GET "
                          f"{url.split('?')[0]}")
    return message


# ---------------------------------------------------------------------------------------------------- memory


def read_memory(path):
    text = Path(path).read_text(encoding="utf-8").strip()
    if not text:
        raise CheckFailed("the Mail Desk check's memory is empty: the Gateway writes the channels into it before the "
                          "first run")
    try:
        memory = json.loads(text)
    except json.JSONDecodeError:
        raise CheckFailed("the Mail Desk check's memory is not JSON") from None
    channels = memory.get("channels") if isinstance(memory, dict) else None
    if not isinstance(channels, list) or not channels:
        raise CheckFailed("the Mail Desk check's memory holds no 'channels': the Gateway writes the channels into it")
    addresses = set()
    for channel in channels:
        if not isinstance(channel, dict) or not isinstance(channel.get("address"), str) or "@" not in channel["address"]:
            raise CheckFailed("a channel in the Mail Desk check's memory has no 'address'")
        address = channel["address"].strip().lower()
        if address in addresses:
            raise CheckFailed(f"the channel {address} is in the Mail Desk check's memory twice")
        addresses.add(address)
        if channel.get("mode") not in MODES:
            raise CheckFailed(f"the channel {address} has mode {channel.get('mode')!r}; it must be observe or live")
        if channel.get("provider") not in READERS:
            raise CheckFailed(f"provider {channel.get('provider')} has no reader in this version of screen.py "
                              f"(channel {address}); this version reads: {', '.join(sorted(READERS))}")
        if not isinstance(channel.get("filingQueue", []), list):
            raise CheckFailed(f"the channel {address} has a 'filingQueue' that is not a list")
        cursor = channel.get("cursor")
        if cursor is not None and not (isinstance(cursor, dict) and isinstance(cursor.get("newest"), str)
                                       and isinstance(cursor.get("idsAtNewest"), list)):
            raise CheckFailed(f"the channel {address} has a 'cursor' this check did not write")
    return memory


def prune_keys(keys, queue, now):
    """Keeps a key while it is in the filing queue, or is among the 40 most recently reported and under 7 days old."""
    queued = {item.get("messageKey") for item in queue if isinstance(item, dict)}
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
        key = item.get("messageKey") if isinstance(item, dict) else None
        decision = item.get("decision") if isinstance(item, dict) else None
        worst = utf8_size(compact({"messageKey": key, "decision": decision, "result": "already-gone",
                                   "reason": "no such key in this channel's key table"})) + 1
        if used + worst > budget_left:
            return results, queue[index:], used
        if decision not in FILING_DECISIONS:
            result = {"messageKey": key, "decision": decision, "result": "refused", "reason": "not archive or junk"}
        elif not isinstance(key, str) or key not in keys:
            result = {"messageKey": key, "decision": decision, "result": "refused",
                      "reason": "no such key in this channel's key table"}
        else:
            try:
                result = {"messageKey": key, "decision": decision, "result": reader.file(keys[key]["id"], decision)}
            except RefusedRequest as e:
                result = {"messageKey": key, "decision": decision, "result": "refused", "reason": str(e)}
        results.append(result)
        used += utf8_size(compact(result)) + 1
    return results, [], used


def trim_to_fit(entry, room):
    """A message that alone does not fit: drop its preview, then shorten its subject and then its sender's name, until
    it fits. It is marked "trimmed"; the sender address, key and time are never cut."""
    entry = dict(entry, trimmed=True, preview="")
    for field in ("subject", "senderName"):
        while utf8_size(compact(entry)) > room and entry[field]:
            over = utf8_size(compact(entry)) - room
            # A character is at most 6 bytes once written as JSON, so cutting over // 6 + 1 never cuts too much.
            entry[field] = entry[field][:-(over // 6 + 1)]
    if utf8_size(compact(entry)) > room:
        raise CheckFailed("a message's sender address alone is larger than the Mail Desk context budget")
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
        if "id" in channel:
            section = {"id": channel["id"], **section}
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
            log(f"{address}: filing " + ", ".join(f"{r['messageKey']} {r['decision']} {r['result']}" for r in results)
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
        entry = {"messageKey": key, "receivedUtc": received, "sender": message["from"],
                 "senderName": clean_text(message["name"]), "subject": clean_text(message["subject"]),
                 "hasAttachments": message["hasAttachments"],
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
        raise CheckFailed(f"the Mail Desk context came to {utf8_size(context)} bytes, over the budget of "
                          f"{CONTEXT_BUDGET_BYTES}")
    output = compact({"count": reported_count + filed_total, "context": context})
    if utf8_size(output) > OUTPUT_CAP_BYTES:
        raise CheckFailed(f"the Mail Desk output is {utf8_size(output)} bytes, over the Director's {OUTPUT_CAP_BYTES}")
    memory_text = compact(memory)
    if utf8_size(memory_text) > MEMORY_CAP_BYTES:
        raise CheckFailed(f"the Mail Desk check's memory would be {utf8_size(memory_text)} bytes, over the Director's "
                          f"{MEMORY_CAP_BYTES}, so nothing was written (the Director would keep none of it)")
    return memory_text, output


def main():
    RUN_LOG.clear()
    try:
        state_path = os.environ.get("CC_CHECK_STATE_FILE")
        if not state_path:
            raise CheckFailed("CC_CHECK_STATE_FILE is not set: the Director names the memory file for every run")
        if not os.path.isfile(state_path):
            raise CheckFailed(f"the memory file {state_path} does not exist: the Director writes it before the run")
        memory_text, output = run(state_path, utc_now())
    except (CheckFailed, RefusedRequest) as e:
        detail = getattr(e, "detail", None)
        print("\n".join([str(e)] + ([detail] if detail else []) + RUN_LOG), file=sys.stderr)
        return 1
    Path(state_path).write_text(memory_text, encoding="utf-8")
    print("\n".join(RUN_LOG), file=sys.stderr)
    sys.stdout.buffer.write(output.encode("utf-8") + b"\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())

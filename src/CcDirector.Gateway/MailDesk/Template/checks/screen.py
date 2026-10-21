# /// script
# requires-python = ">=3.10"
# dependencies = ["msal>=1.28"]   # Outlook's sign-in only; the Gmail reader uses plain urllib, no Google library
# ///
"""The Mail Desk's screening check: read new mail, and file what the Gateway queued. It never decides.

Run by a Director every 5 minutes from the published Mail Desk factory, with every CC_ variable stripped from its
environment except the CC_CHECK_ ones. The whole contract - the memory, the output, every limit - is in
docs/qa/mail-desk/phase-2/CONTRACT.md, and the Gmail reader's in docs/qa/mail-desk/gmail/CONTRACT.md. In short:

- The memory (the file named by CC_CHECK_STATE_FILE) is a JSON object whose "channels" list the Gateway writes: one
  entry per mailbox, with its address, provider and mode. The provider chooses the reader: "outlook" (cc-outlook's
  sign-in, Microsoft Graph) or "gmail" (cc-gmail's sign-in, the Gmail API). This check keeps its own fields in each entry (cursor,
  sentCache, keys) and removes from "filingQueue" the items it attempted. It writes the whole memory back, keeping
  every field it does not own exactly as it was.
- Live mode only: each queued item is moved to Archive or Junk Email (Gmail: taken out of the Inbox, or moved to Spam),
  and nothing else. Observe mode moves nothing and leaves the queue as it is.
- New Inbox mail since the cursor, oldest first, is reported in batches of at most 10 messages and 7 KB of context
  across all channels; the cursor moves only past what was reported, and the next run continues.
- Standard output is exactly {"count": N, "context": "<JSON>"}.
- One mailbox failing does not stop the others (phase 3, docs/qa/mail-desk/phase-3/CONTRACT.md section 6.1). A
  failure that belongs to one channel - its sign-in, its Graph calls, its provider or mode, a field of its own this
  check did not write - goes into that channel's section of the context as "failure": {"sentence", "detail"}, its
  memory fields come back exactly as they were, every other channel is read as usual, and the exit is 0.
- A failure of the whole run (the memory itself unusable, the memory over its cap, anything unforeseen) is a non-zero
  exit, and the memory is NOT rewritten, so the same mail is read again next run. The FIRST line of standard error is
  then one plain sentence saying what is wrong and how to fix it; detail and the run's log follow on later lines. The
  Gateway keeps only the first 200 characters of standard error, flattened onto one line, and uv's own line can come
  first, so every sentence - a channel's too - is at most SENTENCE_LIMIT characters (CONTRACT.md section 1). No
  failure is ever a traceback: main() turns anything unexpected into a sentence too.

Every Microsoft Graph call goes through graph_request(), the one request guard. It lets out only GET to
graph.microsoft.com and POST /v1.0/me/messages/{id}/move with a body of exactly {"destinationId": "archive"} or
{"destinationId": "junkemail"}. Every Gmail API call goes through gmail_request(), which lets out only the message list
and one message's metadata, and - in live mode only - POST .../messages/{id}/modify with a body of exactly
{"removeLabelIds": ["INBOX"]} or {"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}; the Gmail sign-in's refresh
goes through google_token_request(), to Google's token address only. Both saved sign-ins can send mail (phase 0 and
G0 findings), so these guards are the only thing that keeps the Mail Desk from sending, replying, forwarding, changing
or deleting anything.

Standard error is the check's only log. It says what was done in plain words, and never holds a token, a sender, a
subject or any body text.
"""

import copy
import email.errors
import email.header
import email.utils
import hashlib
import html
import json
import os
import re
import socket
import sys
import tempfile
import traceback
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
# The Gateway quotes a failed check as "exit code 1: " plus the first 200 characters of standard error with every line
# joined by a space (TriggerCheckContract.OneLine and QuotedOutputLimit). uv writes a line of its own first whenever it
# builds the script's environment ("Installed 10 packages in 95ms", 29 characters); 35 are kept for it, so the
# check's own first line must be at most 165 characters for the whole sentence, fix and all, to reach the owner.
GATEWAY_REASON_WINDOW = 200
UV_LINE_ALLOWANCE = 35
SENTENCE_LIMIT = GATEWAY_REASON_WINDOW - UV_LINE_ALLOWANCE
SHORT_ERROR_CHARS = 50
# A failing channel's detail line, as it is put into the context: cut so that failing channels never crowd new mail out
# of the 7 KB budget. The detail is for a person; the sentence before it is never cut.
FAILURE_DETAIL_CHARS = 500
# When healthy channels have new mail, failing channels' sections may fill the context only up to this much short of the
# budget, so the first new message always fits (a typical one whole, phase 0's worst one at 680 bytes too). Past that,
# failing channels lose their detail lines, the last channel first; their sentences are never dropped (review finding 1).
MESSAGE_RESERVE_BYTES = 1024
CONTEXT_VERSION = 2             # phase 3: a channel's section may carry "failure"
KEY_HEX = 16
ADDRESS_HASH_HEX = 12

# The filing decisions, in the Gateway's words (phase 1 MailDeskWords). Each reader maps them to its own folders.
FILING_DECISIONS = ("archive", "junk")
MODES = ("observe", "live")


class CheckFailed(Exception):
    """A reason the check cannot answer: one plain sentence, and optional detail for the lines after it. Raised while
    one channel is being checked, it fails that channel only (its section carries it); anywhere else, the exit code is
    1 and the memory is not written."""

    def __init__(self, sentence, detail=None):
        super().__init__(sentence)
        self.detail = detail


class RefusedRequest(Exception):
    """The request guard refused a Graph call. Raised before anything reaches the network."""


class GraphError(Exception):
    """Graph (or the Gmail API, or Google's sign-in service) answered with an HTTP error status. The reader that made
    the call turns it into a sentence. `answer` is the error's body as JSON when it was JSON, else None."""

    def __init__(self, status, detail, answer=None):
        super().__init__(detail)
        self.status = status
        self.answer = answer


class GraphUnreachable(Exception):
    """Graph could not be reached at all. The reader that made the call turns it into a sentence."""

    def __init__(self, reason, detail):
        super().__init__(detail)
        self.reason = reason


class GraphNotJson(Exception):
    """Graph answered something that is not JSON. The reader that made the call turns it into a sentence."""


RUN_LOG = []


def log(text):
    """Kept until the end of the run, so that a failure's sentence is the first line of standard error."""
    RUN_LOG.append(f"screen.py: {text}")


def machine():
    """This computer's name without its domain, so a fully qualified name does not use up the reason window."""
    return socket.gethostname().split(".")[0]


def short_error(text):
    """An error message on one line, cut to SHORT_ERROR_CHARS, for a failure sentence. The whole message goes on the
    detail line."""
    flat = " ".join(str(text).split())
    return flat if len(flat) <= SHORT_ERROR_CHARS else flat[:SHORT_ERROR_CHARS - 3] + "..."


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
    # Before parsing: urlsplit silently removes tab, carriage return and line feed, so an id holding one would
    # otherwise look clean to every rule below (review finding 3).
    if any(ord(ch) <= 0x20 or ord(ch) == 0x7F for ch in url):
        raise RefusedRequest("refused: the address holds a space or a control character")
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
            # The path is not quoted: it may hold a Graph id from the memory, and the reason goes into the context.
            raise RefusedRequest("refused: a POST that is not the move of one message")
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


def shown_url(url):
    """A Graph address for the log: no query, and no message id (it may come from the memory, and the Gateway never
    handles a Graph id)."""
    path = re.sub(r"/messages/[^/]+/move$", "/messages/{id}/move", url.split("?")[0])
    return re.sub(r"(/gmail/v1/users/me/messages)/[^/]+", r"\1/{id}", path)


def error_answer(error):
    """The JSON body of an HTTP error, or None when it has none or it is not JSON."""
    try:
        answer = json.loads(error.read().decode("utf-8"))
    except (OSError, ValueError, AttributeError):
        return None
    return answer if isinstance(answer, dict) else None


def transport(method, url, headers, data):
    """The network. Only graph_request(), gmail_request() and google_token_request() call it; the tests replace urlopen
    with a fake that records every call."""
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
    except urllib.error.HTTPError as e:
        raise GraphError(e.code, f"HTTP {e.code} to {method} {shown_url(url)}", error_answer(e)) from None
    except (urllib.error.URLError, OSError) as e:
        reason = getattr(e, "reason", e)
        raise GraphUnreachable(reason, f"{method} {shown_url(url)}: {reason}") from None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise GraphNotJson(f"{method} {shown_url(url)}") from None


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
    instance (instances/*/config/outlook/profiles.json, PROFILES_FILE in tools/cc-outlook/src/auth.py). Returns the fix
    for the sentence and, when there is one, the step after it for the detail line.

    An account's nickname is its stored one, or else the derived one (get_nickname), and it is used only if
    cc-outlook's nickname lookup (_build_nickname_map, where the last account wins) leads back to this address. When
    the instances disagree, the address is named as cc-outlook holds it: resolve_account matches the address key
    exactly and with case, and save_profile keeps it as it was typed. When no instance holds the account, the fix is
    the first step of adding it; the next run's failure then names the sign-in. Never a guessed nickname."""
    names, registered_as = set(), set()
    for path in sorted(Path(base).glob("instances/*/config/outlook/profiles.json")):
        try:
            text = path.read_text(encoding="utf-8")
        except OSError as e:
            # A list that is there but cannot be read is said as it is, never guessed past (review B).
            raise CheckFailed(f"{address} is not signed in on {machine()}, and cc-outlook's account list cannot be "
                              f"read: {type(e).__name__}", f"{path}: {e}") from None
        try:
            profiles = json.loads(text).get("profiles") or {}
            registered = [email for email in profiles if email.strip().lower() == address]
            if not registered:
                continue
            email = registered[0]
            nickname = (profiles[email] or {}).get("nickname") or derive_nickname(email)
            lookup = {(p or {}).get("nickname") or derive_nickname(e): e for e, p in profiles.items()}
        except (ValueError, AttributeError, IndexError, TypeError):
            continue  # cc-outlook cannot read this file either, so it names no account
        names.add(nickname if lookup.get(nickname) == email else email)
        registered_as.add(email)
    if len(names) == 1:
        return f"run cc-outlook -a {names.pop()} auth", None
    if names:
        return f"run cc-outlook -a {registered_as.pop() if len(registered_as) == 1 else address} auth", None
    return (f"first run cc-outlook accounts add {address} -c {OUTLOOK_CLIENT_ID}",
            f"then run cc-outlook -a {address} auth")


class OutlookReader:
    """Everything Outlook-specific: the saved cc-outlook sign-in, MSAL, the Graph addresses and folder names."""

    # The Sent-folder cache (3.2) is how this reader knows a sender.
    SENT_CACHE = True

    def __init__(self, channel):
        self.address = channel["address"].strip().lower()
        self.token = None

    @staticmethod
    def cursor_ok(cursor):
        """A cursor this reader wrote: {"newest": <text>, "idsAtNewest": [<text>, ...]}."""
        return (isinstance(cursor, dict) and isinstance(cursor.get("newest"), str)
                and isinstance(cursor.get("idsAtNewest"), list) and all(isinstance(i, str) for i in cursor["idsAtNewest"]))

    @staticmethod
    def advance(cursor, mine):
        """The cursor after reporting `mine` (oldest first): the newest receive time, and every id reported at exactly
        that time, plus the ids already held when the time did not change."""
        newest = mine[-1]["received"]
        ids = {m["id"] for m in mine if m["received"] == newest}
        if cursor and cursor["newest"] == newest:
            ids |= set(cursor["idsAtNewest"])
        return {"newest": newest, "idsAtNewest": sorted(ids)}

    def not_signed_in(self, base, detail):
        fix, then = outlook_fix(self.address, base)
        return CheckFailed(f"{self.address} is not signed in on {machine()}: {fix}",
                           "; ".join(part for part in (then, detail) if part))

    def sign_in(self):
        base = director_base(account_record_home())
        name = outlook_cache_name(self.address)
        # cc-outlook names the file after the address as it was typed (get_msal_token_path), so a capital letter there
        # is matched here regardless of case; MSAL's get_accounts matches the username regardless of case too.
        found = sorted(p for p in Path(base).glob("instances/*/config/outlook/tokens/*_msal.json")
                       if p.name.lower() == name)
        if not found:
            raise self.not_signed_in(base, f"looked for {base}/instances/*/config/outlook/tokens/{name} and found none")
        if len(found) > 1:
            raise CheckFailed(f"{self.address} is signed in more than once on {machine()}: keep one and sign out of "
                              f"the others with cc-outlook",
                              "the sign-ins found: " + ", ".join(str(p) for p in found))
        path = found[0]
        cache = msal.SerializableTokenCache()
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as e:
            # The file is there and cannot be read (its permissions, a folder in its place, a disk error): this
            # mailbox's sign-in, said as it is (review finding 2).
            raise CheckFailed(f"the saved sign-in of {self.address} on {machine()} cannot be read "
                              f"({type(e).__name__}): check its permissions or sign in again", f"{path}: {e}") from None
        try:
            # A sign-in cache is a JSON object. MSAL takes any JSON and only fails later, on its first lookup, with an
            # AttributeError, so anything else is refused here as not a sign-in cache (re-review finding 5).
            if not isinstance(json.loads(text), dict):
                raise ValueError("not a JSON object")
            cache.deserialize(text)
        except ValueError:
            raise self.not_signed_in(base, f"the saved sign-in at {path} is not a sign-in cache") from None
        try:
            # Both reach the sign-in service: the constructor reads the authority's configuration, and the silent
            # renewal posts the refresh token when the access token has expired.
            app = msal.PublicClientApplication(client_id=OUTLOOK_CLIENT_ID, authority=OUTLOOK_AUTHORITY,
                                               token_cache=cache)
            accounts = app.get_accounts(username=self.address)
            result = app.acquire_token_silent([OUTLOOK_SCOPE], account=accounts[0]) if accounts else None
        except OSError as e:   # MSAL's transport is requests, whose every error is an OSError
            raise CheckFailed(f"could not reach the Microsoft sign-in service from {machine()} for {self.address}: "
                              f"{type(e).__name__}", f"{type(e).__name__}: {e}") from None
        except (ValueError, RuntimeError) as e:
            raise CheckFailed(f"the Microsoft sign-in service failed on {machine()} for {self.address}: "
                              f"{short_error(e)}", f"{type(e).__name__}: {e}") from None
        # Outside the try above, so reading cc-outlook's account list for the fix is never taken for the sign-in
        # service being out of reach (review B).
        if not accounts:
            raise self.not_signed_in(base, f"the saved sign-in at {path} holds no account for {self.address}")
        if not result or "access_token" not in result:
            error = (result or {}).get("error", "no token came back")
            raise self.not_signed_in(base, f"the saved sign-in at {path} has expired and could not be renewed ({error})")
        # Written back exactly as cc-outlook does (MSALTokenBackend._save_cache): when it changed, to the same file,
        # then owner-only. Never into the memory.
        if cache.has_state_changed:
            try:
                path.write_text(cache.serialize(), encoding="utf-8")
                if os.name == "posix":
                    os.chmod(path, 0o600)
            except OSError as e:
                raise CheckFailed(f"the renewed sign-in of {self.address} could not be saved on {machine()} "
                                  f"({type(e).__name__}): check the file's permissions and the free space",
                                  f"{path}: {e}") from None
            log(f"{self.address}: the renewed sign-in was written back to its file")
        self.token = result["access_token"]

    def graph(self, method, url, what, body=None, missing=None):
        """One Graph call through the guard, its failure turned into a sentence naming this mailbox and what was being
        done. The address it called goes on the detail line. With `missing`, an HTTP 404 returns it instead."""
        try:
            return graph_request(method, url, self.token, body)
        except GraphError as e:
            if e.status == 404 and missing is not None:
                return missing
            raise CheckFailed(f"Microsoft Graph answered HTTP {e.status} for {self.address} while {what}", str(e)) from None
        except GraphUnreachable as e:
            raise CheckFailed(f"Microsoft Graph could not be reached from {machine()} for {self.address} while {what}: "
                              f"{short_error(e.reason)}", str(e)) from None
        except GraphNotJson as e:
            raise CheckFailed(f"Microsoft Graph answered something that is not JSON for {self.address} while {what}",
                              str(e)) from None

    def page(self, url, what):
        page = self.graph("GET", url, what)
        if not isinstance(page, dict) or not isinstance(page.get("value"), list):
            raise CheckFailed(f"Microsoft Graph answered a page without a 'value' list for {self.address} while {what}",
                              f"GET {shown_url(url)}")
        return page

    def message(self, raw, what):
        message = read_message(raw)
        if message is None:
            raise CheckFailed(f"Microsoft Graph answered a message without a usable id or receive time for "
                              f"{self.address} while {what}")
        return message

    def sent_hashes(self):
        """The hashed addresses of everyone in the most recent sent messages, newest first, at most 400."""
        query = urllib.parse.urlencode({"$select": "toRecipients,ccRecipients,bccRecipients",
                                        "$orderby": "sentDateTime desc", "$top": str(SENT_PAGE_SIZE)})
        url = f"{GRAPH}/me/mailFolders/sentitems/messages?{query}"
        hashes = []
        for _ in range(SENT_MAX_PAGES):
            page = self.page(url, "reading the Sent folder")
            for message in page["value"]:
                if not isinstance(message, dict):
                    continue
                for field in ("toRecipients", "ccRecipients", "bccRecipients"):
                    for recipient in message.get(field) or []:
                        address = ((recipient or {}).get("emailAddress") or {}).get("address")
                        if isinstance(address, str) and address:
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
            page = self.page(url, "reading the Inbox")
            seen.extend(self.message(m, "reading the Inbox") for m in page["value"])
            url = page.get("@odata.nextLink")
        return list(reversed(seen[:FIRST_RUN_MESSAGES])), False

    def inbox_since(self, cursor, wanted):
        """Inbox messages received at or after the cursor's time and not among the ids already read at exactly that
        time, oldest first, until more than `wanted` are found or the folder ends. Returns (messages, more)."""
        already = set(cursor["idsAtNewest"])
        query = urllib.parse.urlencode({"$select": INBOX_FIELDS, "$filter": f"receivedDateTime ge {cursor['newest']}",
                                        "$orderby": "receivedDateTime asc", "$top": str(PAGE_SIZE)})
        url = f"{GRAPH}/me/mailFolders/inbox/messages?{query}"
        fresh = []
        for _ in range(MAX_INBOX_PAGES):
            page = self.page(url, "reading the Inbox")
            fresh.extend(m for m in (self.message(x, "reading the Inbox") for x in page["value"])
                         if m["id"] not in already)
            url = page.get("@odata.nextLink")
            if not url or len(fresh) > wanted:
                break
        return fresh, bool(url) or len(fresh) > wanted

    def file(self, message_id, decision):
        """Moves one message to the folder of a filing decision. Returns "moved", or "already-gone" when Graph no longer
        has it."""
        destination = OUTLOOK_FOLDERS[decision]
        answer = self.graph("POST", f"{GRAPH}/me/messages/{message_id}/move", f"moving a message to {destination}",
                            {"destinationId": destination}, missing="already-gone")
        return "already-gone" if answer == "already-gone" else "moved"


INBOX_FIELDS = "id,receivedDateTime,from,subject,hasAttachments,bodyPreview"


# ---------------------------------------------------------------------------------------------------- Gmail
#
# The whole Gmail contract is docs/qa/mail-desk/gmail/CONTRACT.md, following the Tech Lead's rulings R1 to R5
# (docs/qa/mail-desk/gmail/RULINGS.md). Plain urllib throughout: no Google library, so msal stays the only package.

GMAIL_HOST = "gmail.googleapis.com"
GMAIL = f"https://{GMAIL_HOST}/gmail/v1/users/me"
GOOGLE_TOKEN_URI = "https://oauth2.googleapis.com/token"
GMAIL_READONLY = "https://www.googleapis.com/auth/gmail.readonly"
GMAIL_MODIFY = "https://www.googleapis.com/auth/gmail.modify"
# The scopes each mode needs, any one of them (R1.5): observe only reads, live also files.
GMAIL_MODE_SCOPES = {"observe": (GMAIL_READONLY, GMAIL_MODIFY), "live": (GMAIL_MODIFY,)}
# The two filing bodies, exactly (R3). A Gmail message keeps its id when it is filed.
GMAIL_FILING = {"archive": {"removeLabelIds": ["INBOX"]},
                "junk": {"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}}
GMAIL_FILING_STEPS = {"archive": "archiving a message", "junk": "moving a message to Spam"}
# A Gmail message id is hexadecimal; the guard allows letters and digits and nothing else.
GMAIL_ID = re.compile(r"[A-Za-z0-9]+")
GMAIL_MESSAGE_PATH = re.compile(r"/gmail/v1/users/me/messages/([^/]*)")
GMAIL_MODIFY_PATH = re.compile(r"/gmail/v1/users/me/messages/([^/]*)/modify")
GMAIL_LIST_QUERY = {"labelIds": ("INBOX", "SENT"), "q": None, "maxResults": None, "pageToken": None}
GMAIL_GET_QUERY = {"format": ("metadata", "minimal"), "metadataHeaders": ("From", "Subject", "To", "Cc", "Bcc")}
GMAIL_LIST_PAGE = 500           # the Gmail API's largest page of message ids
GMAIL_LIST_MAX_PAGES = 20       # 10,000 new ids at most; past that the oldest cannot be known, and the channel fails
# Each run reads the metadata of at most this many messages, from the oldest end of the list, stopping once it has one
# more new message than a run can report. The query cannot leave out the ids already reported in the second before the
# cursor (R4); those still in the key table are skipped unread, the rest cost one read each.
GMAIL_METADATA_PER_RUN = 60
GMAIL_REFRESH_MARGIN = timedelta(minutes=5)
# A sender address the Sent search may take (the Tech Lead's ruling on G0 review finding 1): the From address is the
# sender's to write, and a known sender skips the spam screen, so only a plain address ever goes into a Gmail query.
# Local part: letters, digits and . _ + - ; domain: labels of letters, digits and -, with dots between.
GMAIL_PLAIN_ADDRESS = re.compile(r"[a-z0-9_+][a-z0-9._+-]*@[a-z0-9-]+(\.[a-z0-9-]+)+")
# google-auth's Credentials.from_authorized_user_info needs these three; a refresh also needs token_uri (R2).
GMAIL_TOKEN_KEYS = ("refresh_token", "client_id", "client_secret", "token_uri")


def gmail_request(method, url, token, body=None, live=False):
    """The one way this check talks to the Gmail API. Refuses, before any network call, everything but reading
    messages and - in live mode only - the two filing bodies (R3)."""
    check_gmail_request(method, url, body, live)
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    return transport(method, url, headers, data)


def check_query(query, allowed):
    """Each query parameter is one this check names, and a named value list holds every value given. A value may hold
    a space once decoded (both searches need one, G0); a name may not, and neither may hold a control character."""
    try:
        pairs = urllib.parse.parse_qsl(query, keep_blank_values=True, strict_parsing=bool(query))
    except ValueError:
        raise RefusedRequest("refused: a Gmail query that does not parse") from None
    for name, value in pairs:
        if any(ord(ch) < 0x20 or ord(ch) == 0x7F for ch in name + value) or " " in name:
            raise RefusedRequest("refused: a Gmail query parameter holds a control character or a space in its name")
        if name not in allowed:
            raise RefusedRequest(f"refused: the Gmail query parameter {name!r} is not one this check sends")
        if allowed[name] is not None and value not in allowed[name]:
            raise RefusedRequest(f"refused: {name}={value!r} is not one this check sends")
    return dict(pairs)


def check_gmail_request(method, url, body, live):
    # Before parsing, for the same reason as Graph's: urlsplit silently removes tab, carriage return and line feed.
    if any(ord(ch) <= 0x20 or ord(ch) == 0x7F for ch in url):
        raise RefusedRequest("refused: the address holds a space or a control character")
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.netloc != GMAIL_HOST:
        raise RefusedRequest(f"refused: {parts.scheme}://{parts.netloc} is not https://{GMAIL_HOST}")
    if parts.fragment:
        raise RefusedRequest("refused: a Gmail address with a fragment")
    if ".." in parts.path or "%" in parts.path or "\\" in parts.path:
        raise RefusedRequest("refused: the path holds '..', '%' or '\\'")
    if method == "GET":
        if body is not None:
            raise RefusedRequest("refused: a GET with a body")
        if parts.path == "/gmail/v1/users/me/messages":
            check_query(parts.query, GMAIL_LIST_QUERY)
            return
        match = GMAIL_MESSAGE_PATH.fullmatch(parts.path)
        if not match:
            raise RefusedRequest("refused: a GET that is not the list of messages or one message")
        if not GMAIL_ID.fullmatch(match.group(1)):
            raise RefusedRequest("refused: the message id holds a character a Gmail id never has")
        if "format" not in check_query(parts.query, GMAIL_GET_QUERY):
            raise RefusedRequest("refused: a GET of one message names no format, so it would read the whole message")
        return
    if method == "POST":
        match = GMAIL_MODIFY_PATH.fullmatch(parts.path)
        if not match:
            raise RefusedRequest("refused: a POST that is not the filing of one message")
        if not live:
            raise RefusedRequest("refused: a channel in observe mode never files a message")
        if not GMAIL_ID.fullmatch(match.group(1)):
            raise RefusedRequest("refused: the message id holds a character a Gmail id never has")
        if parts.query:
            raise RefusedRequest("refused: a filing with a query")
        if not any(isinstance(body, dict) and body == allowed and list(body) == list(allowed)
                   for allowed in GMAIL_FILING.values()):
            raise RefusedRequest('refused: a filing body must be exactly {"removeLabelIds": ["INBOX"]} or '
                                 '{"addLabelIds": ["SPAM"], "removeLabelIds": ["INBOX"]}')
        return
    raise RefusedRequest(f"refused: {method} is never sent to the Gmail API; only GET and the two filings")


def google_token_request(url, form):
    """The one way this check talks to Google's sign-in service: the standard refresh of one saved sign-in (R2), to
    exactly https://oauth2.googleapis.com/token with exactly the four form fields. Refused before the network
    otherwise."""
    if url != GOOGLE_TOKEN_URI:
        raise RefusedRequest(f"refused: a sign-in refresh goes to {GOOGLE_TOKEN_URI} only")
    if not (isinstance(form, dict) and set(form) == {"grant_type", "client_id", "client_secret", "refresh_token"}
            and form["grant_type"] == "refresh_token" and all(isinstance(v, str) and v for v in form.values())):
        raise RefusedRequest("refused: a sign-in refresh carries exactly grant_type=refresh_token, client_id, "
                             "client_secret and refresh_token")
    data = urllib.parse.urlencode(form).encode("utf-8")
    headers = {"Content-Type": "application/x-www-form-urlencoded", "Accept": "application/json"}
    return transport("POST", url, headers, data)


def gmail_store(home):
    """cc-gmail's one store per operating-system user (R1): CcStorage.user_tool_config("gmail") in
    tools/cc_storage/storage.py, whose _user_base() is %LOCALAPPDATA%\\cc-director on Windows, Application Support on
    macOS and ~/.cc-director elsewhere. On Linux and macOS the home comes from the account record and LOCALAPPDATA is
    not read: it is not that system's variable."""
    if os.name == "nt":
        local = os.environ.get("LOCALAPPDATA")
        base = Path(local) / "cc-director" if local else Path(home) / ".cc-director"
    elif sys.platform == "darwin":
        base = Path(home) / "Library" / "Application Support" / "cc-director"
    else:
        base = Path(home) / ".cc-director"
    return base / "config" / "gmail"


def gmail_expiry(text):
    """google-auth's own reading of `expiry` (from_authorized_user_info): the text before any fraction, without the
    "Z", is a UTC time. Returns None when there is none; raises ValueError when it is not one."""
    if text is None:
        return None
    if not isinstance(text, str):
        raise ValueError("expiry is not text")
    return datetime.strptime(text.rstrip("Z").split(".")[0], "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)


def gmail_header(headers, name):
    """One header's value from a Gmail message's metadata (the first, as Gmail lists them), or ""."""
    for header in headers:
        if isinstance(header, dict) and str(header.get("name", "")).lower() == name.lower():
            value = header.get("value")
            return value if isinstance(value, str) else ""
    return ""


def decoded_subject(text):
    """RFC 2047 encoded words decoded (decoding is not rewording, R5). A word in a character set Python does not know
    is kept as Gmail gave it."""
    try:
        return str(email.header.make_header(email.header.decode_header(text)))
    except (LookupError, ValueError, UnicodeError, email.errors.HeaderParseError):
        return text


def read_gmail_message(raw):
    """One Gmail message (format=metadata) in the check's own shape, or None when it has no usable id or internalDate,
    or a field of the wrong kind. `received` is internalDate in whole seconds (R4); `ms` keeps the milliseconds for
    the cursor. hasAttachments is set after, from Gmail's own attachment search: format=metadata carries no parts
    (G0)."""
    try:
        mid, internal = raw["id"], raw["internalDate"]
        headers = (raw.get("payload") or {}).get("headers") or []
        snippet = raw.get("snippet") or ""
        if not (isinstance(mid, str) and GMAIL_ID.fullmatch(mid) and isinstance(internal, str)
                and internal.isdigit() and isinstance(headers, list) and isinstance(snippet, str)):
            return None
        ms = int(internal)
        received = iso(datetime.fromtimestamp(ms / 1000, timezone.utc))
        name, address = email.utils.parseaddr(gmail_header(headers, "From"))
        return {"id": mid, "ms": ms, "received": received, "from": address.lower(), "name": name,
                "subject": decoded_subject(gmail_header(headers, "Subject")), "hasAttachments": False,
                "preview": html.unescape(snippet)}
    except (AttributeError, KeyError, TypeError, ValueError, OverflowError, OSError):
        return None


class GmailReader:
    """Everything Gmail-specific: cc-gmail's saved sign-in, its refresh, the Gmail API addresses and labels."""

    # A sender is known by one search of the SENT label (known_hashes), never a Sent cache: G0 measured 2.4 seconds for
    # 10 senders against 123 seconds to build a 500-message cache. So a Gmail channel never has a sentCache.
    SENT_CACHE = False

    def __init__(self, channel):
        self.address = channel["address"].strip().lower()
        self.live = channel["mode"] == "live"
        self.mode = channel["mode"]
        self.token = None
        # Every id in the key table was reported already, so it is never new mail and its metadata is never read.
        self.reported = {entry["id"] for entry in channel.get("keys", {}).values()}

    # ------------------------------------------------------------------------------------------------ the cursor

    @staticmethod
    def cursor_ok(cursor):
        """A cursor this reader wrote: {"provider": "gmail", "newestMs": <whole number>, "idsAtNewest": [<text>, ...]}.
        The provider key keeps an Outlook cursor from ever being read as a Gmail one, and the other way round."""
        return (isinstance(cursor, dict) and set(cursor) == {"provider", "newestMs", "idsAtNewest"}
                and cursor["provider"] == "gmail" and isinstance(cursor["newestMs"], int)
                and not isinstance(cursor["newestMs"], bool) and cursor["newestMs"] >= 0
                and isinstance(cursor["idsAtNewest"], list) and all(isinstance(i, str) for i in cursor["idsAtNewest"]))

    @staticmethod
    def advance(cursor, mine):
        """The cursor after reporting `mine` (oldest first): internalDate in milliseconds of the newest reported, and
        every id reported at exactly that millisecond, plus those already held when it did not change."""
        newest = mine[-1]["ms"]
        ids = {m["id"] for m in mine if m["ms"] == newest}
        if cursor and cursor["newestMs"] == newest:
            ids |= set(cursor["idsAtNewest"])
        return {"provider": "gmail", "newestMs": newest, "idsAtNewest": sorted(ids)}

    # ------------------------------------------------------------------------------------------------ the sign-in

    def find_account(self, store):
        """The one account directory whose config.json names this address (R1.2). cc-gmail's -a takes the directory
        name exactly (resolve_account returns it as given, and get_account_dir joins it under accounts/)."""
        found = []
        for config in sorted(Path(store).glob("accounts/*/config.json")):
            try:
                text = config.read_text(encoding="utf-8")
            except (OSError, UnicodeDecodeError) as e:
                raise CheckFailed(f"{self.address} is not signed in on {machine()}, and a cc-gmail account's settings "
                                  f"cannot be read: {type(e).__name__}", f"{config}: {e}") from None
            try:
                settings = json.loads(text)
            except ValueError:
                continue  # cc-gmail cannot read this account either, so it names no address
            if isinstance(settings, dict) and isinstance(settings.get("email"), str) \
                    and settings["email"].strip().lower() == self.address:
                found.append((config.parent, settings))
        if not found:
            raise CheckFailed(f"{self.address} is not signed in on {machine()}: run cc-gmail accounts add "
                              f"{self.address.split('@')[0]} and give it this address",
                              f"no account under {store}/accounts holds {self.address} in its config.json")
        if len(found) > 1:
            raise CheckFailed(f"{self.address} is in more than one cc-gmail account on {machine()}: keep one and remove "
                              f"the others with cc-gmail accounts remove <name>",
                              "the accounts found: " + ", ".join(folder.name for folder, _ in found))
        return found[0]

    def sign_in(self):
        store = gmail_store(account_record_home())
        folder, settings = self.find_account(store)
        nickname = folder.name
        if settings.get("auth_method") != "oauth":
            # An app password reads IMAP, never the Gmail API (R1.3).
            raise CheckFailed(f"{self.address} signs in to cc-gmail on {machine()} without OAuth: run cc-gmail -a "
                              f"{nickname} auth --method oauth",
                              f"{folder / 'config.json'} has auth_method {settings.get('auth_method')!r}")
        path = folder / "token.json"
        renew = f"{self.address} is not signed in on {machine()}: run cc-gmail -a {nickname} auth --force"
        if not path.exists():
            # cc-gmail's auth signs in when there is no token yet; with one, it needs --force (authenticate()).
            raise CheckFailed(f"{self.address} is not signed in on {machine()}: run cc-gmail -a {nickname} auth",
                              f"looked for {path} and found none")
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as e:
            raise CheckFailed(f"the saved sign-in of {self.address} on {machine()} cannot be read "
                              f"({type(e).__name__}): check its permissions or sign in again", f"{path}: {e}") from None
        try:
            saved = json.loads(text)
            if not isinstance(saved, dict):
                raise ValueError("not a JSON object")
            expiry = gmail_expiry(saved.get("expiry"))
        except ValueError as e:
            raise CheckFailed(renew, f"the saved sign-in at {path} is not one cc-gmail wrote ({e})") from None
        missing = [key for key in GMAIL_TOKEN_KEYS if not (isinstance(saved.get(key), str) and saved[key])]
        if missing:
            # No fallback to credentials.json: cc-gmail's own refresh reads token.json only (R2).
            raise CheckFailed(renew, f"the saved sign-in at {path} has no {', '.join(missing)}")
        if saved["token_uri"] != GOOGLE_TOKEN_URI:
            raise CheckFailed(f"the Gmail sign-in of {self.address} on {machine()} names another sign-in service: "
                              f"run cc-gmail -a {nickname} auth --force",
                              f"{path}: token_uri is not {GOOGLE_TOKEN_URI}, and it is never followed")
        scopes = saved.get("scopes") or []
        scopes = scopes.split(" ") if isinstance(scopes, str) else scopes
        if not isinstance(scopes, list) or not any(s in scopes for s in GMAIL_MODE_SCOPES[self.mode]):
            needed = " or ".join(s.rsplit("/", 1)[1] for s in GMAIL_MODE_SCOPES[self.mode])
            raise CheckFailed(f"{self.address} on {machine()} lacks the Gmail permission {self.mode} mode needs: "
                              f"run cc-gmail -a {nickname} auth --force",
                              f"{path} grants {len(scopes) if isinstance(scopes, list) else 0} scope(s), none of them "
                              f"{needed}")
        token = saved.get("token")
        if isinstance(token, str) and token and expiry is not None and expiry - GMAIL_REFRESH_MARGIN > utc_now():
            self.token = token
            return
        self.token = self.refresh(path, saved, nickname)

    def refresh(self, path, saved, nickname):
        """The standard OAuth refresh (R2), then the write-back exactly as cc-gmail would leave the file."""
        form = {"grant_type": "refresh_token", "client_id": saved["client_id"],
                "client_secret": saved["client_secret"], "refresh_token": saved["refresh_token"]}
        now = utc_now()
        try:
            answer = google_token_request(saved["token_uri"], form)
        except GraphError as e:
            error = (e.answer or {}).get("error")
            if e.status in (400, 401) and isinstance(error, str):
                raise CheckFailed(f"{self.address} is not signed in on {machine()}: run cc-gmail -a {nickname} auth "
                                  f"--force", f"Google refused the refresh of the saved sign-in at {path} ({error})") \
                    from None
            raise CheckFailed(f"the Google sign-in service failed on {machine()} for {self.address}: HTTP {e.status}",
                              str(e)) from None
        except GraphUnreachable as e:
            raise CheckFailed(f"could not reach the Google sign-in service from {machine()} for {self.address}: "
                              f"{short_error(e.reason)}", str(e)) from None
        except GraphNotJson as e:
            raise CheckFailed(f"the Google sign-in service failed on {machine()} for {self.address}: it answered "
                              f"something that is not JSON", str(e)) from None
        token, lifetime = (answer or {}).get("access_token"), (answer or {}).get("expires_in")
        if not (isinstance(answer, dict) and isinstance(token, str) and token and isinstance(lifetime, int)
                and not isinstance(lifetime, bool) and lifetime > 0):
            raise CheckFailed(f"the Google sign-in service failed on {machine()} for {self.address}: its answer held "
                              f"no access token", f"POST {GOOGLE_TOKEN_URI}")
        renewed = dict(saved)
        renewed["token"] = token
        # As google-auth writes it (to_json): a naive UTC time in isoformat, then "Z".
        renewed["expiry"] = (now + timedelta(seconds=lifetime)).astimezone(timezone.utc).replace(tzinfo=None) \
            .isoformat() + "Z"
        if isinstance(answer.get("refresh_token"), str) and answer["refresh_token"]:
            renewed["refresh_token"] = answer["refresh_token"]
        self.write_back(path, renewed)
        return token

    def write_back(self, path, renewed):
        """The same keys in the same order, to a temporary file in the same folder, then os.replace; owner-only on
        Linux and macOS. Nothing in the store is ever deleted, and token-refresh-error.json is never written (R2)."""
        temporary = None
        try:
            handle, temporary = tempfile.mkstemp(prefix=".token-", suffix=".json", dir=path.parent)
            with os.fdopen(handle, "w", encoding="utf-8") as stream:
                stream.write(json.dumps(renewed))
            if os.name == "posix":
                os.chmod(temporary, 0o600)
            os.replace(temporary, path)
            temporary = None
        except OSError as e:
            raise CheckFailed(f"the renewed sign-in of {self.address} could not be saved on {machine()} "
                              f"({type(e).__name__}): check the file's permissions and the free space",
                              f"{path}: {e}") from None
        finally:
            if temporary is not None and os.path.exists(temporary):
                os.unlink(temporary)   # this check's own temporary file, never one of cc-gmail's
        log(f"{self.address}: the renewed sign-in was written back to its file")

    # ------------------------------------------------------------------------------------------------ the Gmail API

    def gmail(self, method, url, what, body=None, missing=None):
        """One Gmail API call through the guard, its failure turned into a sentence naming this mailbox and what was
        being done. With `missing`, an HTTP 404 returns it instead."""
        try:
            return gmail_request(method, url, self.token, body, live=self.live)
        except GraphError as e:
            if e.status == 404 and missing is not None:
                return missing
            raise CheckFailed(f"the Gmail API answered HTTP {e.status} for {self.address} while {what}", str(e)) from None
        except GraphUnreachable as e:
            raise CheckFailed(f"the Gmail API could not be reached from {machine()} for {self.address} while {what}: "
                              f"{short_error(e.reason)}", str(e)) from None
        except GraphNotJson as e:
            raise CheckFailed(f"the Gmail API answered something that is not JSON for {self.address} while {what}",
                              str(e)) from None

    def message_ids(self, label, query, limit, what):
        """The ids of a label's messages matching `query`, newest first as the Gmail API lists them, at most `limit`.
        Returns (ids, whether the list went on past `limit`)."""
        ids, token = [], None
        for _ in range(GMAIL_LIST_MAX_PAGES):
            params = [("labelIds", label), ("q", query), ("maxResults", str(min(GMAIL_LIST_PAGE, limit - len(ids))))]
            if token:
                params.append(("pageToken", token))
            url = f"{GMAIL}/messages?{urllib.parse.urlencode(params)}"
            page = self.gmail("GET", url, what)
            listed = page.get("messages", []) if isinstance(page, dict) else None
            token = page.get("nextPageToken") if isinstance(page, dict) else None
            if not isinstance(listed, list) or not all(isinstance(m, dict) and isinstance(m.get("id"), str)
                                                       and GMAIL_ID.fullmatch(m["id"]) for m in listed) \
                    or (token is not None and not (isinstance(token, str) and token)):
                raise CheckFailed(f"the Gmail API answered a list without usable message ids for {self.address} "
                                  f"while {what}", f"GET {shown_url(url)}")
            ids.extend(m["id"] for m in listed if m["id"] not in ids)
            if not token or len(ids) >= limit:
                return ids[:limit], bool(token) or len(ids) > limit
        raise self.too_many(what)

    def too_many(self, what):
        """More new mail than GMAIL_LIST_MAX_PAGES pages: the oldest of it cannot be known, so nothing is guessed, and
        every later run would stop here too until the Inbox holds fewer."""
        return CheckFailed(f"{self.address} has more than {GMAIL_LIST_PAGE * GMAIL_LIST_MAX_PAGES} new Inbox messages, "
                           f"more than screen.py lists in one run: archive the oldest of them in Gmail", f"while {what}")

    def metadata(self, mid, headers, what):
        query = urllib.parse.urlencode([("format", "metadata")] + [("metadataHeaders", h) for h in headers])
        return self.gmail("GET", f"{GMAIL}/messages/{mid}?{query}", what)

    def oldest(self, ids, keep, query, limit):
        """Walks a newest-first list from its oldest end, reading each message's metadata, until it holds one more
        message `keep` accepts than a run can report, or has read GMAIL_METADATA_PER_RUN. Returns those accepted,
        oldest first by internalDate, and whether ids were left unread. The Gmail API lists by internalDate, newest
        first, so the oldest new mail is at the end. hasAttachments comes from one more list, of the same query with
        has:attachment, when anything was found."""
        found, read, left = [], 0, list(ids)
        while left and len(found) <= MAX_MESSAGES_PER_RUN and read < GMAIL_METADATA_PER_RUN:
            mid = left.pop()
            if mid in self.reported:
                continue
            read += 1
            message = read_gmail_message(self.metadata(mid, ("From", "Subject"), "reading the Inbox"))
            if message is None or message["id"] != mid:
                raise CheckFailed(f"the Gmail API answered a message without a usable id or receive time for "
                                  f"{self.address} while reading the Inbox")
            if keep(message):
                found.append(message)
        if found:
            attached = self.attachment_ids(query, limit)
            for message in found:
                message["hasAttachments"] = message["id"] in attached
        found.sort(key=lambda m: (m["ms"], m["id"]))
        return found, bool(left)

    def attachment_ids(self, query, limit):
        """The Inbox ids Gmail's own search calls attached, for the same window as the Inbox list: one list a run,
        whatever the number of messages (G0: format=metadata carries no parts, and the MIME type disagreed with this
        search on 6 of 36 messages). It is Gmail's definition of an attachment, which may differ from Outlook's at the
        edges (an inline picture)."""
        ids, cut = self.message_ids("INBOX", f"{query} has:attachment", limit, "reading the Inbox")
        if cut and limit > FIRST_RUN_MESSAGES:
            raise self.too_many("reading the Inbox")
        return set(ids)

    def inbox_first_run(self, now):
        """First run: the most recent 200 Inbox messages, or those of the last 30 days, whichever is fewer (D18).
        Returns the oldest of them first, and whether more are waiting."""
        since = int((now - timedelta(days=FIRST_RUN_DAYS)).timestamp())
        ids, _ = self.message_ids("INBOX", f"after:{since}", FIRST_RUN_MESSAGES, "reading the Inbox")
        floor = since * 1000
        return self.oldest(ids, lambda m: m["ms"] >= floor, f"after:{since}", FIRST_RUN_MESSAGES)

    def inbox_since(self, cursor, wanted):
        """Inbox messages at or after the cursor's millisecond and not among the ids already reported at exactly it,
        oldest first. The query is one second before the cursor (Gmail's after: counts whole seconds); the rest is
        filtered here. Returns (messages, more)."""
        already, newest = set(cursor["idsAtNewest"]), cursor["newestMs"]
        query, limit = f"after:{newest // 1000 - 1}", GMAIL_LIST_PAGE * GMAIL_LIST_MAX_PAGES
        ids, cut = self.message_ids("INBOX", query, limit, "reading the Inbox")
        if cut:
            raise self.too_many("reading the Inbox")
        found, more = self.oldest(ids, lambda m: m["ms"] >= newest and m["id"] not in already, query, limit)
        return found[:wanted + 1], more or len(found) > wanted

    def known_hashes(self, found):
        """"We have written to this sender" (R5, settled by G0): for each distinct sender found, one search of the SENT
        label for a message to, cc or bcc that address, at most one result. It covers the whole Sent history. Returns
        the hashes of those written to; nothing of it is kept.

        Two guards (the Tech Lead's ruling on G0 review finding 1), because the address is the sender's to write and a
        known sender skips the spam screen: only a plain address (GMAIL_PLAIN_ADDRESS) is searched for, anything else
        is not known and costs no call; and a hit counts only when the found message's own To, Cc or Bcc holds exactly
        that address."""
        known = set()
        for sender in sorted({m["from"] for m in found if m["from"]}):
            if not GMAIL_PLAIN_ADDRESS.fullmatch(sender):
                continue
            ids, _ = self.message_ids("SENT", f"to:{sender} OR cc:{sender} OR bcc:{sender}", 1, "reading the Sent mail")
            if not ids:
                continue
            raw = self.metadata(ids[0], ("To", "Cc", "Bcc"), "reading the Sent mail")
            headers = (raw.get("payload") or {}).get("headers") if isinstance(raw, dict) else None
            # Only headers that are there: getaddresses in strict mode answers nothing at all for a list holding "".
            values = [v for v in (gmail_header(headers if isinstance(headers, list) else [], f)
                                  for f in ("To", "Cc", "Bcc")) if v]
            if sender in {a.lower() for _, a in email.utils.getaddresses(values) if a}:
                known.add(address_hash(sender))
        return known

    def file(self, message_id, decision):
        """Files one message: archive takes it out of the Inbox, junk moves it to Spam. Returns "moved", or
        "already-gone" when the Gmail API no longer has it (404). The id stays the same, so a retry is a no-op."""
        answer = self.gmail("POST", f"{GMAIL}/messages/{message_id}/modify", GMAIL_FILING_STEPS[decision],
                            copy.deepcopy(GMAIL_FILING[decision]), missing="already-gone")
        return "already-gone" if answer == "already-gone" else "moved"


READERS = {"outlook": OutlookReader, "gmail": GmailReader}


def read_message(raw):
    """One Graph message in the check's own shape, or None when it has no id or receive time as text, or a text field
    that is not text."""
    try:
        sender = (raw.get("from") or {}).get("emailAddress") or {}
        message = {"id": raw["id"], "received": raw["receivedDateTime"], "from": sender.get("address") or "",
                   "name": sender.get("name") or "", "subject": raw.get("subject") or "",
                   "hasAttachments": bool(raw.get("hasAttachments")), "preview": raw.get("bodyPreview") or ""}
    except (AttributeError, KeyError, TypeError):
        return None
    if not all(isinstance(message[f], str) for f in ("id", "received", "from", "name", "subject", "preview")) \
            or not message["id"] or not message["received"]:
        return None
    message["from"] = message["from"].lower()
    return message


# ---------------------------------------------------------------------------------------------------- memory


def read_memory(path):
    try:
        text = Path(path).read_text(encoding="utf-8").strip()
    except (OSError, UnicodeDecodeError) as e:
        raise CheckFailed(f"the Mail Desk check's memory could not be read: {short_error(e)}", str(e)) from None
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
    return memory


def check_channel(channel, address):
    """What is wrong with one channel's entry, before any network call: a mode, provider or filing queue the Gateway
    should not have written, or a field of this check's own that it did not write. Fails that channel only."""
    if channel.get("mode") not in MODES:
        raise CheckFailed(f"the channel {address} has mode {channel.get('mode')!r}; it must be observe or live")
    provider = channel.get("provider")
    if not isinstance(provider, str) or provider not in READERS:
        raise CheckFailed(f"provider {provider} has no reader in this version of screen.py "
                          f"(channel {address}); this version reads: {', '.join(READERS)}")
    if not isinstance(channel.get("filingQueue", []), list):
        raise CheckFailed(f"the channel {address} has a 'filingQueue' that is not a list")
    cursor = channel.get("cursor")
    # Each reader knows its own cursor, so a channel whose provider changed never reads the other's as its own.
    if cursor is not None and not READERS[provider].cursor_ok(cursor):
        raise CheckFailed(f"the channel {address} has a 'cursor' this check did not write")
    keys = channel.get("keys", {})
    if not (isinstance(keys, dict) and all(isinstance(entry, dict) and isinstance(entry.get("id"), str)
                                           and isinstance(entry.get("reportedUtc"), str)
                                           for entry in keys.values())):
        raise CheckFailed(f"the channel {address} has a 'keys' table this check did not write")
    sent = channel.get("sentCache")
    if sent is not None and not READERS[provider].SENT_CACHE:
        raise CheckFailed(f"the channel {address} has a 'sentCache' this check did not write")
    if sent is not None and not (isinstance(sent, dict) and isinstance(sent.get("refreshedUtc"), str)
                                 and isinstance(sent.get("hashes"), list)
                                 and all(isinstance(h, str) for h in sent["hashes"])):
        raise CheckFailed(f"the channel {address} has a 'sentCache' this check did not write")


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
    return compact({"version": CONTEXT_VERSION, "channels": sections})


def failure_of(error):
    """A channel's failure as its section carries it: the sentence whole, and the detail line cut to
    FAILURE_DETAIL_CHARS ("" when there is none)."""
    detail = " ".join(str(error.detail or "").split())
    if len(detail) > FAILURE_DETAIL_CHARS:
        detail = detail[:FAILURE_DETAIL_CHARS - 3] + "..."
    return {"sentence": str(error), "detail": detail}


def fit_failures(sections, reserve):
    """Failing channels' sections must leave `reserve` bytes of the context for new mail. Over that, the detail lines
    go, the last failing channel's first; the sentences always stay. If the sentences alone still do not fit, the run
    cannot name every failing mailbox, and says how many failed instead (a failure of the whole run)."""
    room = CONTEXT_BUDGET_BYTES - reserve
    failing = [section for section in sections if "failure" in section]
    dropped = 0
    for section in reversed(failing):
        if utf8_size(build_context(sections)) <= room:
            break
        if section["failure"]["detail"]:
            section["failure"]["detail"] = ""
            dropped += 1
    if dropped:
        log(f"{dropped} failing channel(s) reported without their detail, so the context stays within its budget")
    if failing and utf8_size(build_context(sections)) > room:
        sentence = f"{len(failing)} of {len(sections)} mailboxes failed on {machine()}, too many to name in the context"
        first = f"; first: {failing[0]['address']}"
        # The address is the place to start, so it is named when it fits in the sentence's 165 characters; when it
        # does not, line 2 (the first mailbox's own sentence) still names it (re-review finding 4).
        raise CheckFailed(sentence + first if len(sentence + first) <= SENTENCE_LIMIT else sentence,
                          failing[0]["failure"]["sentence"])


def read_channel(channel, reader, address, now, filing_room):
    """With the channel signed in: carries out its filing queue (live mode), refreshes its Sent-folder cache and reads
    its new mail. Changes only this channel's entry. Returns (filing results, bytes of filing used, the messages found,
    whether more is waiting, the Sent-folder hashes). A CheckFailed raised here fails this channel only."""
    results, used = [], 0
    queue = channel.get("filingQueue", [])
    if queue and channel["mode"] == "live":
        results, left, used = file_queue(channel, reader, channel.get("keys", {}), filing_room)
        channel["filingQueue"] = left
        log(f"{address}: filing " + ", ".join(f"{r['messageKey']} {r['decision']} {r['result']}" for r in results)
            + (f"; {len(left)} left queued for the next run" if left else ""))

    sent = channel.get("sentCache")
    if reader.SENT_CACHE and (not sent or sent.get("refreshedUtc", "") < iso(now - SENT_REFRESH)):
        sent = {"refreshedUtc": iso(now), "hashes": reader.sent_hashes()}
        channel["sentCache"] = sent
        log(f"{address}: the Sent folder was read again: {len(sent['hashes'])} address(es) written to")

    if channel.get("cursor"):
        found, more_left = reader.inbox_since(channel["cursor"], MAX_MESSAGES_PER_RUN)
    else:
        found, more_left = reader.inbox_first_run(now)
        log(f"{address}: first run: {len(found)} message(s) in the look-back")
    known = set(sent["hashes"]) if reader.SENT_CACHE else reader.known_hashes(found)
    return results, used, found, more_left, known


def run(state_path, now):
    memory = read_memory(state_path)
    channels = memory["channels"]
    log(f"{len(channels)} channel(s): " + ", ".join(f"{c['address']} ({c.get('provider')}, {c.get('mode')})" for c in channels))

    sections, readers, originals, candidates, more, filed_total = [], [], [], [], [], 0
    failed = set()

    def fail(index, error):
        # The channel's entry is put back whole, so its memory comes back exactly as it was even when it failed
        # part-way (a move done, the Sent folder read, then the Inbox failing).
        channels[index] = originals[index]
        failed.add(index)
        sections[index]["failure"] = failure_of(error)
        log(f"{sections[index]['address']}: could not be checked, the other channels go on: {error}")

    # First every channel is checked and signed in, before any channel files or reads. The checks come before any
    # network call. Signing in first means the failures a whole machine has at once (its sign-ins gone) are known
    # before filing takes its share of the context (re-review observation D).
    for index, channel in enumerate(channels):
        address = channel["address"].strip().lower()
        # "more" is in place from the start, as false (one byte longer than true), so the budget is measured on the
        # context as it is finally written.
        section = {"address": address, "provider": channel.get("provider"), "mode": channel.get("mode"), "messages": [],
                   "filing": [], "more": False}
        if "id" in channel:
            section = {"id": channel["id"], **section}
        sections.append(section)
        more.append(False)
        originals.append(copy.deepcopy(channel))
        readers.append(None)
        try:
            check_channel(channel, address)
            readers[index] = READERS[channel["provider"]](channel)
        except CheckFailed as e:
            fail(index, e)
    for index, reader in enumerate(readers):
        if reader is None:
            continue
        try:
            reader.sign_in()
        except CheckFailed as e:
            readers[index] = None
            fail(index, e)

    # Filing gets its half of the context, less what the failures known now need (their sentences; their details can
    # go) and the room kept for new mail, so a long filing report and many failing mailboxes never meet over budget.
    named_only = [dict(sec, failure=dict(sec["failure"], detail="")) if "failure" in sec else sec for sec in sections]
    filing_room = max(0, min(FILING_BUDGET_BYTES,
                             CONTEXT_BUDGET_BYTES - MESSAGE_RESERVE_BYTES - utf8_size(build_context(named_only))))
    for index, reader in enumerate(readers):
        if reader is None:
            continue
        channel, section = channels[index], sections[index]
        try:
            results, used, found, more_left, known = read_channel(channel, reader, section["address"], now,
                                                                  filing_room)
        except CheckFailed as e:
            fail(index, e)
            continue
        queue = channel.get("filingQueue", [])
        if queue and channel["mode"] == "observe":
            section["filingNotCarriedOut"] = {"count": len(queue), "reason": "the channel is in observe mode"}
            log(f"{section['address']}: {len(queue)} queued filing item(s) NOT carried out: the channel is in "
                f"observe mode")
        filing_room -= used
        section["filing"] = results
        filed_total += len(results)
        for order, message in enumerate(found):
            candidates.append((message["received"], index, order, message, known))
        more[index] = more_left

    fit_failures(sections, MESSAGE_RESERVE_BYTES if candidates else 0)

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
        if index in failed:
            continue
        mine = reported[index]
        left = len([c for c in candidates if c[1] == index]) - len(mine)
        sections[index]["more"] = more[index] or left > 0
        if mine:
            channel["cursor"] = type(readers[index]).advance(channel.get("cursor"), mine)
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
                          f"{MEMORY_CAP_BYTES}, so nothing was written: give this factory fewer channels")
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
    except Exception as e:
        # The entry point: a failure the check did not foresee is still one sentence first, never a traceback, and
        # the memory is not written. Where it stopped is the next line; the whole traceback comes last, after the
        # run's log, so the cause is not hidden.
        where = traceback.extract_tb(e.__traceback__)[-1]
        print("\n".join([f"screen.py stopped on an error it does not handle: {type(e).__name__}: {short_error(e)}",
                         f"in {where.name}, {Path(where.filename).name} line {where.lineno}"] + RUN_LOG
                        + [traceback.format_exc().rstrip()]), file=sys.stderr)
        return 1
    Path(state_path).write_text(memory_text, encoding="utf-8")
    print("\n".join(RUN_LOG), file=sys.stderr)
    sys.stdout.buffer.write(output.encode("utf-8") + b"\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""The guided path from "no Google project" to a working cc-gmail account.

Google gives nobody a way to reach a mailbox without an OAuth credential from a
Google Cloud project, and there is no API that creates a classic Desktop OAuth
client - that one click lives in a browser and cannot be scripted. Everything
either side of it can be, and this module is that everything:

  - which fork the user is on, Workspace or personal Gmail, said out loud before
    anything is opened
  - watching the downloads folder for the client JSON and putting it where the
    account expects it, because "where do I put this file" is where people get lost
  - proving the account works with a real Google call, never with an absence of
    errors
  - saying that an unpublished External app's token dies in seven days, rather
    than letting the user discover it next week

The client secret is read here and written to exactly one place: the account
directory. It is never printed and never logged.
"""

import json
import logging
import os
import ssl
import time
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Tuple

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# The fork: Workspace or personal Gmail
# ---------------------------------------------------------------------------

FORK_WORKSPACE = "workspace"
FORK_PERSONAL = "personal"
FORK_UNKNOWN = "unknown"

# Addresses on these domains are always a personal Google Account.
PERSONAL_DOMAINS = frozenset({"gmail.com", "googlemail.com"})

# An MX host under one of these domains means the domain's mail is hosted by
# Google, which for an address a person signs in with means Workspace. The
# leading dot matters: without it "mx.notgoogle.com" matches "google.com".
_GOOGLE_MX_DOMAINS = ("google.com", "googlemail.com")

_DOH_URL = "https://dns.google/resolve?name={domain}&type=MX"


def _ssl_context():
    """A verifying SSL context, preferring certifi's bundle when it is installed.

    google-api-python-client already pulls certifi in, and a Python built
    against a bare system trust store cannot verify dns.google without it. No
    unverified fallback: a fork detected over an unauthenticated channel is
    worse than one the user is simply asked about.
    """
    try:
        import certifi

        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        return ssl.create_default_context()


class SetupError(Exception):
    """A step of the guided setup could not be completed."""


def email_domain(email_address: str) -> str:
    """The domain of an email address, lowercased. Empty when there isn't one."""
    _, _, domain = email_address.strip().partition("@")
    return domain.strip().lower()


def _lookup_mx(domain: str, timeout: float = 5.0) -> List[str]:
    """MX hosts for a domain, over Google's public DNS-over-HTTPS endpoint.

    Plain HTTPS, no credentials, no extra dependency. An empty list means the
    lookup gave no answer - it never means "this domain has no Google mail",
    which is why the caller treats it as unknown rather than as personal.
    """
    url = _DOH_URL.format(domain=urllib.parse.quote(domain))
    with urllib.request.urlopen(url, timeout=timeout, context=_ssl_context()) as response:
        payload = json.loads(response.read().decode("utf-8"))
    return [answer.get("data", "") for answer in payload.get("Answer", [])]


def detect_fork(email_address: str, lookup_mx: Optional[Callable[[str], List[str]]] = None) -> str:
    """Which of the two setups this address needs: Workspace, personal, or unknown.

    Args:
        email_address: The address the account will sign in as.
        lookup_mx: Resolver for a domain's MX hosts. Defaults to a DNS-over-HTTPS
            lookup; tests pass their own.

    Returns:
        FORK_WORKSPACE, FORK_PERSONAL or FORK_UNKNOWN. Unknown is returned
        whenever the answer was not established - the caller asks the user
        rather than guessing, because the two forks differ on the step that
        decides whether the token survives a week.
    """
    domain = email_domain(email_address)
    if not domain:
        return FORK_UNKNOWN
    if domain in PERSONAL_DOMAINS:
        return FORK_PERSONAL

    resolver = lookup_mx or _lookup_mx
    try:
        hosts = resolver(domain)
    except Exception as exc:  # noqa: BLE001 - any resolver failure means "not established"
        logger.debug("MX lookup for %s failed: %s", domain, exc)
        return FORK_UNKNOWN

    for host in hosts:
        # An MX answer reads "<priority> <host>"; the host is the last field,
        # and DNS gives it with a trailing dot.
        fields = host.strip().lower().split()
        if not fields:
            continue
        name = fields[-1].rstrip(".")
        if any(name == d or name.endswith("." + d) for d in _GOOGLE_MX_DOMAINS):
            return FORK_WORKSPACE

    # The domain answered, and Google does not carry its mail. Any Google Account
    # on such an address is a personal one, so the consent screen is External.
    return FORK_PERSONAL if hosts else FORK_UNKNOWN


# ---------------------------------------------------------------------------
# The client JSON: recognising it, finding it, installing it
# ---------------------------------------------------------------------------

CLIENT_DESKTOP = "desktop"
CLIENT_WEB = "web"

# A downloaded client JSON is a few hundred bytes. Anything larger is not one,
# and reading an arbitrary large file out of a downloads folder is not free.
_MAX_CLIENT_JSON_BYTES = 64 * 1024


def classify_client_json(path: Path) -> Optional[Tuple[str, Dict[str, Any]]]:
    """Read a file and say whether it is an OAuth client JSON, and of what type.

    Matched on SHAPE, never on filename: Google names the download
    client_secret_<a very long client id>.json, browsers rename duplicates, and
    users rename files. The shape is a single top-level key - "installed" for a
    Desktop client, "web" for a Web application client - holding both a
    client_id and a client_secret.

    Returns:
        (CLIENT_DESKTOP | CLIENT_WEB, the parsed document), or None when the
        file is not an OAuth client JSON at all.
    """
    try:
        if path.stat().st_size > _MAX_CLIENT_JSON_BYTES:
            return None
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, UnicodeDecodeError):
        return None

    if not isinstance(document, dict):
        return None

    for key, client_type in (("installed", CLIENT_DESKTOP), ("web", CLIENT_WEB)):
        body = document.get(key)
        if isinstance(body, dict) and body.get("client_id") and body.get("client_secret"):
            return client_type, document

    return None


def describe_client_json(document: Dict[str, Any]) -> Dict[str, Any]:
    """What is safe to show the user about a client JSON.

    The client secret is deliberately absent, and the client id is reduced to
    its tail so a user can tell two clients apart without the full value
    landing in a terminal transcript.
    """
    body = document.get("installed") or document.get("web") or {}
    client_id = str(body.get("client_id", ""))
    return {
        "project_id": body.get("project_id") or "(not in the file)",
        "client_id_tail": client_id[-32:] if client_id else "(none)",
    }


def find_client_jsons(
    directories: Iterable[Path],
    modified_after: float,
    client_type: str = CLIENT_DESKTOP,
) -> List[Path]:
    """Every OAuth client JSON in these directories touched after a moment in time.

    Args:
        directories: Folders to look in. Missing folders are skipped.
        modified_after: Only files modified at or after this epoch time count,
            so a client JSON downloaded months ago for something else is not
            mistaken for the one the user just downloaded.
        client_type: CLIENT_DESKTOP or CLIENT_WEB.

    Returns:
        Matching paths, newest first.
    """
    found: List[Tuple[float, Path]] = []
    for directory in directories:
        if not directory.is_dir():
            continue
        for path in directory.glob("*.json"):
            try:
                modified = path.stat().st_mtime
            except OSError:
                continue
            if modified < modified_after:
                continue
            classified = classify_client_json(path)
            if classified and classified[0] == client_type:
                found.append((modified, path))

    found.sort(key=lambda item: item[0], reverse=True)
    return [path for _, path in found]


def watch_for_client_json(
    directories: Iterable[Path],
    modified_after: float,
    timeout: float = 300.0,
    poll_seconds: float = 1.0,
    sleep: Callable[[float], None] = time.sleep,
    now: Callable[[], float] = time.monotonic,
    on_wrong_type: Optional[Callable[[Path], None]] = None,
) -> Optional[Path]:
    """Wait for the user to download a Desktop client JSON, and hand back its path.

    Args:
        directories: Folders to watch (the downloads folder, plus anything the
            user named).
        modified_after: Epoch time the watch started; older files are ignored.
        timeout: Seconds to wait before giving up.
        poll_seconds: Gap between sweeps.
        sleep: Injected for tests.
        now: Monotonic clock, injected for tests.
        on_wrong_type: Called once with the path when a WEB application client
            turns up instead - the user picked the wrong application type in
            the console and needs to be told, not left waiting.

    Returns:
        The path of the Desktop client JSON, or None if the timeout passed.
    """
    directories = list(directories)
    deadline = now() + timeout
    warned_about: set = set()

    while True:
        matches = find_client_jsons(directories, modified_after, CLIENT_DESKTOP)
        if matches:
            return matches[0]

        if on_wrong_type:
            for path in find_client_jsons(directories, modified_after, CLIENT_WEB):
                if path not in warned_about:
                    warned_about.add(path)
                    on_wrong_type(path)

        if now() >= deadline:
            return None
        sleep(poll_seconds)


def install_client_json(source: Path, destination: Path) -> Path:
    """Copy a downloaded client JSON to the account directory, owner-readable only.

    The bytes are copied unchanged - google-auth reads this file itself and is
    entitled to every field Google put in it. The destination is whatever
    auth.get_credentials_path() resolves to; this function never decides where
    the store lives.

    Raises:
        SetupError: If the source is not an OAuth client JSON after all.
    """
    classified = classify_client_json(source)
    if classified is None:
        raise SetupError(f"{source} is not an OAuth client JSON.")

    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(source.read_bytes())
    if os.name == "posix":
        os.chmod(destination, 0o600)
    return destination


def default_download_dirs() -> List[Path]:
    """Where a browser drops a download on this machine."""
    return [Path.home() / "Downloads"]


# ---------------------------------------------------------------------------
# Proving it works
# ---------------------------------------------------------------------------

def verify_gmail(client) -> Dict[str, Any]:
    """Prove Gmail answers for this token, by what comes back and not by silence.

    A mailbox always has system labels (INBOX, SENT), so an empty label list is
    a broken instrument rather than a clean run, and is failed here.

    Raises:
        SetupError: If Google answered with nothing that proves access.
    """
    profile = client.get_profile()
    address = (profile or {}).get("emailAddress")
    if not address:
        raise SetupError("Gmail returned a profile with no email address in it.")

    labels = client.list_labels()
    if not labels:
        raise SetupError(
            "Gmail returned no labels at all. Every mailbox has INBOX and SENT, "
            "so this is a broken answer, not an empty mailbox."
        )

    return {
        "email": address,
        "messages_total": profile.get("messagesTotal"),
        "labels": len(labels),
    }


def verify_calendar(client) -> Dict[str, Any]:
    """Prove the Calendar API answers. Every account has at least one calendar.

    Raises:
        SetupError: If Google answered with an empty list.
    """
    calendars = client.list_calendars()
    if not calendars:
        raise SetupError(
            "Calendar returned no calendars. Every account has a primary calendar, "
            "so this is a broken answer."
        )
    return {"calendars": len(calendars)}


def verify_contacts(client) -> Dict[str, Any]:
    """Prove the People API answers.

    Unlike Gmail and Calendar, zero is a real answer here: a fresh account has
    no contacts. The proof is that the call returned a list at all, so that is
    what is checked, and the count is reported for what it is.

    Raises:
        SetupError: If the call did not return a list.
    """
    contacts = client.list_contacts(max_results=1)
    if not isinstance(contacts, list):
        raise SetupError("The People API did not return a list of contacts.")
    return {"contacts_sampled": len(contacts)}


# ---------------------------------------------------------------------------
# Telling the truth about how long the token will last
# ---------------------------------------------------------------------------

USER_TYPE_INTERNAL = "internal"
USER_TYPE_EXTERNAL = "external"
USER_TYPE_UNKNOWN = "unknown"

# The user type each fork leads to. Workspace users MAY still choose External;
# the setup command asks, and this is only the default it offers.
FORK_USER_TYPE = {
    FORK_WORKSPACE: USER_TYPE_INTERNAL,
    FORK_PERSONAL: USER_TYPE_EXTERNAL,
    FORK_UNKNOWN: USER_TYPE_UNKNOWN,
}

SEVEN_DAY_WARNING = (
    "This app is External and has NOT been published.\n"
    "Google expires the refresh token of an External app in Testing after 7 days.\n"
    "cc-gmail will work now and stop working next week.\n"
    "\n"
    "Fix it once, at https://console.cloud.google.com/auth/audience :\n"
    "  click 'Publish app', confirm, and the status reads 'In production'.\n"
    "Then run: cc-gmail --account {account} auth --force"
)

PUBLISHED_NOTE = (
    "This app is External and published (In production), so the 7-day token "
    "expiry does not apply. The one-off 'Google hasn't verified this app' screen "
    "is expected and is not an error."
)

INTERNAL_NOTE = (
    "This app is Internal to your Google Workspace organisation. There is no "
    "'Publish app' step, no unverified-app screen, and no 7-day token expiry - "
    "those all belong to the External user type."
)


def token_lifetime_note(user_type: str, published: Optional[bool], account: str) -> Tuple[str, str]:
    """What to say about how long this token will live.

    The discriminator is the USER TYPE the consent screen was configured with,
    not which kind of mailbox it is: Google's 7-day expiry is written against
    "an external user type and a publishing status of Testing", so an Internal
    app is outside it by construction and an unpublished External app is inside
    it whoever owns the mailbox.

    Args:
        user_type: USER_TYPE_INTERNAL, USER_TYPE_EXTERNAL or USER_TYPE_UNKNOWN.
        published: True if the user confirmed they clicked "Publish app", False
            if they confirmed they did not, None if it was not established.
            None is treated like False - an unestablished publish is exactly the
            case that dies quietly in seven days.
        account: Account name, so the fix can name the command to re-run.

    Returns:
        (severity, text) where severity is "ok" or "warning".
    """
    if user_type == USER_TYPE_INTERNAL:
        return "ok", INTERNAL_NOTE
    if published:
        return "ok", PUBLISHED_NOTE
    return "warning", SEVEN_DAY_WARNING.format(account=account)

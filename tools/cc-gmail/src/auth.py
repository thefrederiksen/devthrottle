"""Authentication for Gmail with dual auth support (App Password + OAuth).

App Password (IMAP/SMTP) -- Quick Setup:
  - Works with most Gmail accounts
  - No Google Cloud project needed
  - Password stored in OS credential manager via keyring

OAuth (Gmail API) -- Full Setup:
  - Required if IMAP/App Passwords are blocked
  - Requires Google Cloud project + credentials.json
"""

import json
import logging
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional, List, Dict, Any

from google.auth.transport.requests import Request
from google.auth.exceptions import RefreshError
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import InstalledAppFlow

import keyring

try:
    from cc_storage import CcStorage
except ImportError:
    import sys
    _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage

logger = logging.getLogger(__name__)


# Gmail API scopes (for OAuth path)
GMAIL_SCOPES = [
    "https://www.googleapis.com/auth/gmail.readonly",
    "https://www.googleapis.com/auth/gmail.send",
    "https://www.googleapis.com/auth/gmail.compose",
    "https://www.googleapis.com/auth/gmail.modify",
]

# Calendar and Contacts scopes (OAuth-only features)
CALENDAR_SCOPES = [
    "https://www.googleapis.com/auth/calendar",
]

CONTACTS_SCOPES = [
    "https://www.googleapis.com/auth/contacts",
]

# All scopes requested during OAuth consent
SCOPES = GMAIL_SCOPES + CALENDAR_SCOPES + CONTACTS_SCOPES

# Keyring service name for storing app passwords
KEYRING_SERVICE = "cc-gmail"

# README location for help messages
README_PATH = Path(__file__).parent.parent / "README.md"

# Set once the legacy instance stores have been looked at, so the scan happens
# at most once per process. The notes survive the scan so a command can print
# what was adopted even when something else in the process triggered it.
_adopted_legacy_stores = False
_adoption_notes: List[str] = []


def get_readme_path() -> str:
    """Get the path to the README file for help messages."""
    if README_PATH.exists():
        return str(README_PATH)
    return "https://github.com/cc-director/cc-director"


# ---------------------------------------------------------------------------
# Where the store lives
#
# ONE store per operating-system user, resolved through CcStorage.user_config()
# so CC_DIRECTOR_ROOT does NOT move it. A Director sets CC_DIRECTOR_ROOT to its
# instance home for every session it runs; a plain terminal sets nothing. While
# this tool resolved its store through CcStorage.tool_config() there were two
# independent stores, and the "Re-authenticate by running: cc-gmail auth" the
# tool printed inside a session was an instruction that could not work - the
# auth the owner then ran in a terminal wrote the other store (issue #3011).
#
# These are functions, not module constants, so the path is resolved per access
# and a test can point the tool at a store it wrote.
# ---------------------------------------------------------------------------

def config_dir_path() -> Path:
    """The store directory for this user. Not created by this call."""
    return CcStorage.user_tool_config("gmail")


def accounts_dir_path() -> Path:
    """The directory holding one sub-directory per account. Not created by this call."""
    return config_dir_path() / "accounts"


def config_file_path() -> Path:
    """The store's own config file (default account). Not created by this call."""
    return config_dir_path() / "config.json"


def get_config_dir() -> Path:
    """Get the configuration directory, creating it if necessary."""
    config_dir = config_dir_path()
    config_dir.mkdir(parents=True, exist_ok=True)
    accounts_dir_path().mkdir(parents=True, exist_ok=True)
    adopt_legacy_stores()
    return config_dir


def get_account_dir(account: str) -> Path:
    """Get the directory for a specific account."""
    get_config_dir()
    account_dir = accounts_dir_path() / account
    account_dir.mkdir(parents=True, exist_ok=True)
    return account_dir


# ---------------------------------------------------------------------------
# Adopting what the older, per-instance store left behind
# ---------------------------------------------------------------------------

def legacy_store_dirs() -> List[Path]:
    """Every older per-instance gmail store on this machine, newest token first.

    Two shapes are looked for: the store under whatever CC_DIRECTOR_ROOT is set
    to right now (what this process would have used before the fix), and the
    store inside each named Director instance home (what a session left behind
    when the owner is running from a plain terminal). The current per-user store
    is never in the list.
    """
    mine = config_dir_path().resolve()
    candidates = [CcStorage.tool_config("gmail")]
    for home in CcStorage.instance_homes():
        candidates.append(home / "config" / "gmail")

    found: List[Path] = []
    seen = set()
    for candidate in candidates:
        if not candidate.is_dir():
            continue
        resolved = candidate.resolve()
        if resolved == mine or resolved in seen:
            continue
        seen.add(resolved)
        found.append(candidate)

    def newest_token(store: Path) -> float:
        tokens = store.glob("accounts/*/token.json")
        return max((token.stat().st_mtime for token in tokens), default=0.0)

    return sorted(found, key=newest_token, reverse=True)


def adopt_legacy_stores() -> List[str]:
    """Copy accounts that exist ONLY in an older per-instance store into this one.

    Runs at most once per process, copies and never deletes, and never touches an
    account this store already has. Without it, moving the store out from under
    CC_DIRECTOR_ROOT would hide the setup of anyone who only ever ran cc-gmail
    inside a Director session - which is exactly what issue #3011 told them to do.

    Returns a line per account adopted, for the caller to print.
    """
    global _adopted_legacy_stores
    if _adopted_legacy_stores:
        return _adoption_notes
    _adopted_legacy_stores = True

    import shutil

    accounts_dir = accounts_dir_path()
    adopted = _adoption_notes

    for store in legacy_store_dirs():
        legacy_accounts = store / "accounts"
        if legacy_accounts.is_dir():
            for legacy_account in sorted(legacy_accounts.iterdir()):
                if not legacy_account.is_dir():
                    continue
                # A directory with neither a token nor a credentials file is not an
                # account - it is a leftover, and adopting it would only add a row
                # to 'accounts list' that stands for nothing.
                if not (legacy_account / "token.json").exists() and not (legacy_account / "credentials.json").exists():
                    continue
                destination = accounts_dir / legacy_account.name
                if destination.exists():
                    continue
                shutil.copytree(legacy_account, destination)
                for secret in destination.glob("*.json"):
                    _harden_file_permissions(secret)
                note = f"Adopted Gmail account '{legacy_account.name}' from {legacy_account}"
                logger.warning(note)
                adopted.append(note)

        legacy_config = store / "config.json"
        if legacy_config.is_file() and not config_file_path().exists():
            shutil.copy2(legacy_config, config_file_path())

    return adopted


def adoption_notes() -> List[str]:
    """What this process adopted from an older per-instance store, if anything."""
    return list(_adoption_notes)


def get_credentials_path(account: str) -> Path:
    """Get the path to the OAuth credentials file for an account."""
    return get_account_dir(account) / "credentials.json"


def get_token_path(account: str) -> Path:
    """Get the path to the token file for an account."""
    return get_account_dir(account) / "token.json"


def get_account_config_path(account: str) -> Path:
    """Get the path to the account config file."""
    return get_account_dir(account) / "config.json"


# ---------------------------------------------------------------------------
# Account config (stores email, auth_method -- NO secrets)
# ---------------------------------------------------------------------------

def load_account_config(account: str) -> Dict[str, Any]:
    """Load account-specific config (email, auth_method, etc.)."""
    config_path = get_account_config_path(account)
    if config_path.exists():
        return json.loads(config_path.read_text())
    return {}


def save_account_config(account: str, config: Dict[str, Any]) -> None:
    """Save account-specific config."""
    get_account_dir(account)  # Ensure directory exists
    get_account_config_path(account).write_text(json.dumps(config, indent=2))


def get_auth_method(account: str) -> Optional[str]:
    """Get the auth method for an account ('app_password' or 'oauth')."""
    config = load_account_config(account)
    return config.get("auth_method")


def get_account_email(account: str) -> Optional[str]:
    """Get the email address for an account."""
    config = load_account_config(account)
    return config.get("email")


# ---------------------------------------------------------------------------
# App Password management (via keyring)
# ---------------------------------------------------------------------------

def store_app_password(account: str, password: str) -> None:
    """Store an app password in the OS credential manager.

    Args:
        account: Account name.
        password: The app password to store.
    """
    keyring.set_password(KEYRING_SERVICE, account, password)


def get_app_password(account: str) -> Optional[str]:
    """Retrieve an app password from the OS credential manager.

    Args:
        account: Account name.

    Returns:
        The app password, or None if not found.
    """
    return keyring.get_password(KEYRING_SERVICE, account)


def delete_app_password(account: str) -> bool:
    """Delete an app password from the OS credential manager.

    Args:
        account: Account name.

    Returns:
        True if deleted, False if not found.
    """
    try:
        keyring.delete_password(KEYRING_SERVICE, account)
        return True
    except keyring.errors.PasswordDeleteError:
        return False


# ---------------------------------------------------------------------------
# Connection testing (for App Password setup)
# ---------------------------------------------------------------------------

def test_imap_connection(email_address: str, password: str) -> bool:
    """Test IMAP connection to Gmail.

    Args:
        email_address: Gmail address.
        password: App password.

    Returns:
        True if connection successful.

    Raises:
        ConnectionError: With specific error message if connection fails.
    """
    import imaplib

    try:
        conn = imaplib.IMAP4_SSL("imap.gmail.com", 993)
        conn.login(email_address, password)
        conn.logout()
        return True
    except imaplib.IMAP4.error as e:
        error_msg = str(e)
        if "AUTHENTICATIONFAILED" in error_msg.upper():
            raise ConnectionError(
                "Authentication failed. Check your email address and app password.\n\n"
                "Common causes:\n"
                "  - Wrong app password (must be 16 characters, no spaces)\n"
                "  - 2-Step Verification not enabled\n"
                "  - App password was revoked\n\n"
                "Create a new app password at:\n"
                "  https://myaccount.google.com/apppasswords"
            )
        raise ConnectionError(f"IMAP connection failed: {error_msg}")
    except OSError as e:
        raise ConnectionError(
            "Could not connect to imap.gmail.com.\n\n"
            "This usually means:\n"
            "  - Your organization blocks IMAP access\n"
            "  - Network/firewall is blocking port 993\n\n"
            "Try OAuth setup instead: cc-gmail auth --method oauth\n\n"
            "Or ask your Google Workspace admin to enable IMAP:\n"
            "  Admin Console -> Apps -> Google Workspace -> Gmail -> User access"
        )


def test_smtp_connection(email_address: str, password: str) -> bool:
    """Test SMTP connection to Gmail.

    Args:
        email_address: Gmail address.
        password: App password.

    Returns:
        True if connection successful.

    Raises:
        ConnectionError: With specific error message if connection fails.
    """
    import smtplib

    try:
        server = smtplib.SMTP("smtp.gmail.com", 587)
        server.starttls()
        server.login(email_address, password)
        server.quit()
        return True
    except smtplib.SMTPAuthenticationError:
        raise ConnectionError(
            "SMTP authentication failed. Check your email and app password."
        )
    except OSError as e:
        raise ConnectionError(
            "Could not connect to smtp.gmail.com.\n\n"
            "This usually means:\n"
            "  - Your organization blocks SMTP access\n"
            "  - Network/firewall is blocking port 587\n\n"
            "Try OAuth setup instead: cc-gmail auth --method oauth"
        )


# ---------------------------------------------------------------------------
# Global config (default account, etc.)
# ---------------------------------------------------------------------------

def load_config() -> Dict[str, Any]:
    """Load the global config file."""
    get_config_dir()  # Ensure directory exists
    config_file = config_file_path()
    if config_file.exists():
        return json.loads(config_file.read_text())
    return {"default_account": None}


def save_config(config: Dict[str, Any]) -> None:
    """Save the global config file."""
    get_config_dir()
    config_file_path().write_text(json.dumps(config, indent=2))


def get_default_account() -> Optional[str]:
    """Get the default account name."""
    config = load_config()
    return config.get("default_account")


def set_default_account(account: str) -> None:
    """Set the default account."""
    config = load_config()
    config["default_account"] = account
    save_config(config)


def list_accounts() -> List[Dict[str, Any]]:
    """List all configured accounts with their auth method."""
    get_config_dir()
    accounts = []
    default = get_default_account()

    accounts_dir = accounts_dir_path()
    if not accounts_dir.exists():
        return accounts

    for account_dir in accounts_dir.iterdir():
        if account_dir.is_dir():
            name = account_dir.name
            acct_config = load_account_config(name)
            auth_method = acct_config.get("auth_method")
            email_addr = acct_config.get("email")

            # Determine authentication status based on auth method
            if auth_method == "app_password":
                has_password = get_app_password(name) is not None
                authenticated = has_password
                creds_exist = has_password  # For app_password, "credentials" = password in keyring
                token_state = "ready" if has_password else "no_credentials"
                status = "Ready" if has_password else "Setup needed"
                detail = "" if has_password else "No app password in the credential manager"
            else:
                # OAuth path. The token state comes from describe_token, so an
                # account whose refresh Google has refused is never called Ready.
                described = describe_token(name)
                creds_exist = (account_dir / "credentials.json").exists()
                token_state = described["state"]
                status = described["status"]
                detail = described["detail"]
                authenticated = token_state == "ready"

            accounts.append({
                "name": name,
                "is_default": name == default,
                "credentials_exists": creds_exist,
                "authenticated": authenticated,
                "token_state": token_state,
                "status": status,
                "status_detail": detail,
                "auth_method": auth_method or "unknown",
                "email": email_addr,
            })

    return sorted(accounts, key=lambda x: x["name"])


# ---------------------------------------------------------------------------
# OAuth functions (existing, kept intact)
# ---------------------------------------------------------------------------

def credentials_exist(account: str) -> bool:
    """Check if OAuth credentials file exists for an account."""
    return get_credentials_path(account).exists()


def token_exists(account: str) -> bool:
    """Check if token file exists for an account."""
    return get_token_path(account).exists()


# ---------------------------------------------------------------------------
# The refresh-failure record
#
# A token file on disk says only that an 'auth' ran once. Google can revoke or
# expire the refresh token behind it at any time, and then every command fails
# while 'accounts list' still says Ready, because file existence was the whole
# test (issue #3011). So when a refresh is actually REFUSED, the refusal is
# written down beside the token, and the status commands read it. Saving a new
# token clears it.
# ---------------------------------------------------------------------------

def get_token_error_path(account: str) -> Path:
    """Where a refused refresh is recorded for an account."""
    return get_account_dir(account) / "token-refresh-error.json"


def record_token_failure(account: str, reason: str) -> None:
    """Write down that a refresh was refused, so status stops claiming Ready."""
    record = {
        "error": reason,
        "recorded_at": datetime.now(timezone.utc).isoformat(),
        "token_path": str(get_token_path(account)),
    }
    get_token_error_path(account).write_text(json.dumps(record, indent=2))


def clear_token_failure(account: str) -> None:
    """Forget a recorded refusal, because a token has just been obtained."""
    error_path = get_token_error_path(account)
    if error_path.exists():
        error_path.unlink()


def read_token_failure(account: str) -> Optional[Dict[str, Any]]:
    """The recorded refusal for an account, or None when the last attempt worked."""
    error_path = get_token_error_path(account)
    if not error_path.exists():
        return None
    return json.loads(error_path.read_text())


def describe_token(account: str) -> Dict[str, Any]:
    """What is actually known about an OAuth account's token, without a network call.

    Every answer here comes from disk: the credentials file, the token file, and
    the refresh-failure record. 'ready' means a token exists that is either still
    valid or carries a refresh token, AND the last refresh was not refused.
    """
    credentials_path = get_credentials_path(account)
    token_path = get_token_path(account)

    described = {
        "state": "ready",
        "status": "Ready",
        "detail": "",
        "token_path": str(token_path),
        "credentials_path": str(credentials_path),
    }

    if not credentials_path.exists():
        described.update(
            state="no_credentials",
            status="Setup needed",
            detail=f"No OAuth credentials file at {credentials_path}",
        )
        return described

    if not token_path.exists():
        described.update(
            state="no_token",
            status="Setup needed",
            detail=f"No token at {token_path} - run: cc-gmail --account {account} auth",
        )
        return described

    failure = read_token_failure(account)
    if failure:
        described.update(
            state="refresh_failed",
            status="Re-auth needed",
            detail=(
                f"Google refused the last refresh of {token_path} "
                f"at {failure.get('recorded_at', 'an unknown time')}: {failure.get('error', 'no reason recorded')}"
            ),
        )
        return described

    try:
        token_data = json.loads(token_path.read_text())
    except (ValueError, OSError) as e:
        described.update(
            state="unreadable",
            status="Re-auth needed",
            detail=f"Could not read {token_path}: {e}",
        )
        return described

    if not token_data.get("refresh_token"):
        described.update(
            state="no_refresh_token",
            status="Re-auth needed",
            detail=(
                f"{token_path} holds no refresh token, so it cannot be renewed - "
                f"run: cc-gmail --account {account} auth --force"
            ),
        )
        return described

    return described


def load_credentials(account: str) -> Optional[Credentials]:
    """Load OAuth credentials from token file if available and valid."""
    token_path = get_token_path(account)
    if not token_path.exists():
        return None

    creds = Credentials.from_authorized_user_file(str(token_path), SCOPES)

    # If credentials are expired, try to refresh
    if creds and creds.expired and creds.refresh_token:
        try:
            creds.refresh(Request())
            save_credentials(account, creds)
        except RefreshError as e:
            logger.warning(f"Token refresh failed for account '{account}': {e}")
            record_token_failure(account, str(e))
            return None

    return creds if creds and creds.valid else None


def _harden_file_permissions(path: Path) -> None:
    """Restrict a secret file to the owner only (POSIX).

    The OAuth token file holds a refresh token that grants ongoing mailbox
    access. On POSIX the default umask can leave it group/world-readable, so
    scope it to the owner (0600). On Windows the %LOCALAPPDATA% profile already
    restricts access per-user, so no change is needed there.
    """
    if os.name == "posix":
        os.chmod(path, 0o600)


def save_credentials(account: str, creds: Credentials) -> None:
    """Save OAuth credentials to token file."""
    get_account_dir(account)  # Ensure directory exists
    token_path = get_token_path(account)
    token_path.write_text(creds.to_json())
    _harden_file_permissions(token_path)
    clear_token_failure(account)


def authenticate(account: str, force: bool = False, open_browser: bool = True,
                 interactive: bool = True) -> Credentials:
    """Authenticate with Gmail API via OAuth for a specific account.

    Args:
        account: Account name.
        force: If True, force re-authentication even if valid token exists.
        open_browser: If True, auto-open default browser. If False, print the
            auth URL so the user can open it in a specific browser.
        interactive: If True, run OAuth browser flow when token is missing/expired.
            If False, raise ValueError instead of opening browser.

    Returns:
        Valid credentials for Gmail API.

    Raises:
        FileNotFoundError: If credentials.json is missing.
        ValueError: If interactive=False and no valid credentials available.
    """
    creds_path = get_credentials_path(account)

    if not creds_path.exists():
        raise FileNotFoundError(
            f"OAuth credentials not found for account '{account}'\n\n"
            f"Expected location: {creds_path}\n\n"
            f"See README for setup instructions: {get_readme_path()}"
        )

    creds = None

    # Try to load existing credentials if not forcing re-auth
    if not force:
        creds = load_credentials(account)

    # If no valid credentials, either run OAuth flow or raise error
    if not creds:
        if not interactive:
            described = describe_token(account)
            reason = described["detail"] or "the token could not be used"
            raise ValueError(
                f"OAuth token expired or missing for account '{account}'.\n\n"
                f"Token read: {get_token_path(account)}\n"
                f"Reason: {reason}\n\n"
                f"Re-authenticate by running:\n"
                f"  cc-gmail --account {account} auth --force\n\n"
                f"Then retry your command."
            )
        flow = InstalledAppFlow.from_client_secrets_file(str(creds_path), SCOPES)
        creds = flow.run_local_server(port=0, open_browser=open_browser)
        save_credentials(account, creds)

    return creds


def revoke_token(account: str) -> bool:
    """Delete the token file to force re-authentication."""
    token_path = get_token_path(account)
    if token_path.exists():
        token_path.unlink()
        return True
    return False


def delete_account(account: str) -> bool:
    """Delete an account and all its files."""
    import shutil
    account_dir = get_account_dir(account)
    if account_dir.exists():
        # Also remove app password from keyring
        delete_app_password(account)
        shutil.rmtree(account_dir)
        # If this was the default, clear it
        if get_default_account() == account:
            config = load_config()
            config["default_account"] = None
            save_config(config)
        return True
    return False


def get_auth_status(account: str) -> dict:
    """Get the authentication status for an account."""
    acct_config = load_account_config(account)
    auth_method = acct_config.get("auth_method", "unknown")

    status = {
        "account": account,
        "account_dir": str(get_account_dir(account)),
        "auth_method": auth_method,
        "email": acct_config.get("email"),
        "credentials_exists": False,
        "token_exists": False,
        "token_path": str(get_token_path(account)),
        "authenticated": False,
        "token_state": "unknown",
        "status_detail": "",
        "is_default": get_default_account() == account,
    }

    if auth_method == "app_password":
        has_password = get_app_password(account) is not None
        status["credentials_exists"] = has_password
        status["authenticated"] = has_password
        status["token_state"] = "ready" if has_password else "no_credentials"
        if not has_password:
            status["status_detail"] = "No app password in the credential manager"
    else:
        status["credentials_exists"] = credentials_exist(account)
        status["token_exists"] = token_exists(account)
        # This is the one place that goes to the network: a refused refresh is
        # recorded by load_credentials, so describe_token below reports the real
        # reason rather than "a token file exists".
        creds = load_credentials(account)
        if creds and creds.valid:
            status["authenticated"] = True
        described = describe_token(account)
        status["token_state"] = described["state"]
        status["status_detail"] = described["detail"]

    return status


def check_token_scopes(account: str) -> Dict[str, bool]:
    """Check which scope groups are present in the current token.

    Returns:
        Dict with keys 'gmail', 'calendar', 'contacts' mapping to bool.
    """
    token_path = get_token_path(account)
    if not token_path.exists():
        return {"gmail": False, "calendar": False, "contacts": False}

    token_data = json.loads(token_path.read_text())
    granted = set(token_data.get("scopes", []))

    return {
        "gmail": all(s in granted for s in GMAIL_SCOPES),
        "calendar": all(s in granted for s in CALENDAR_SCOPES),
        "contacts": all(s in granted for s in CONTACTS_SCOPES),
    }


def resolve_account(account: Optional[str]) -> str:
    """Resolve which account to use.

    Args:
        account: Explicit account name, or None to use default.

    Returns:
        Account name to use.

    Raises:
        ValueError: If no account specified and no default set.
    """
    if account:
        return account

    default = get_default_account()
    if default:
        return default

    # Check if there's only one account
    accounts = list_accounts()
    if len(accounts) == 1:
        return accounts[0]["name"]

    if len(accounts) == 0:
        raise ValueError(
            "No accounts configured.\n\n"
            "To add an account:\n"
            "  1. Run: cc-gmail accounts add <name>\n"
            "  2. Follow the setup instructions\n\n"
            f"See README for details: {get_readme_path()}"
        )

    raise ValueError(
        "Multiple accounts configured but no default set.\n\n"
        "Either:\n"
        "  - Specify an account: cc-gmail --account <name> <command>\n"
        "  - Set a default: cc-gmail accounts default <name>\n\n"
        f"Available accounts: {', '.join(a['name'] for a in accounts)}"
    )

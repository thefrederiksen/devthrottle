"""
Authentication and account management for cc-outlook.

IMPLEMENTATION NOTES - MSAL Device Code Flow (February 2026)
=============================================================

This module implements Microsoft Graph API authentication using MSAL (Microsoft
Authentication Library) with Device Code Flow. This approach was chosen after
extensive troubleshooting of the O365 library's built-in browser-based OAuth flow,
which consistently failed with "wrongplace" redirect errors due to OAuth state
mismatches.

WHY DEVICE CODE FLOW:
- No browser redirect issues - user manually enters code at microsoft.com/devicelogin
- No OAuth state mismatch problems
- Works reliably with MFA-enabled accounts
- Microsoft's recommended approach for CLI applications

KEY IMPLEMENTATION DETAILS:

1. MSALTokenBackend class:
   - Custom token backend that bridges MSAL tokens to O365 library
   - Loads/saves tokens using MSAL's SerializableTokenCache format
   - Token files stored at: {data_dir}/outlook/tokens/{email}_msal.json

2. authenticate_device_code_with_cache():
   - Handles the device code flow interaction
   - First tries silent token refresh (for existing valid tokens)
   - Falls back to interactive device code flow if needed
   - Saves token cache after successful auth

3. CRITICAL WORKAROUND - O365 is_authenticated check:
   - O365's Account.is_authenticated property returns False even with valid MSAL tokens
   - This is because O365 expects a different token format internally
   - WORKAROUND: After MSAL returns a valid token, we ignore is_authenticated=False
     and return the account anyway. The MSALTokenBackend.load_token() method
     provides tokens in the format O365 needs for actual API calls.

4. Token refresh:
   - MSAL handles token refresh automatically via acquire_token_silent()
   - Refresh tokens are stored in the MSAL cache and used transparently

TROUBLESHOOTING:
- If auth fails, check: Azure app has "Allow public client flows" enabled
- Device code expires in ~15 minutes - run auth again if expired
- Token files can be deleted to force re-authentication

Date: February 2026
"""

import json
import logging
import os
from pathlib import Path
from typing import Optional

import msal
from O365 import Account
from O365.utils import BaseTokenBackend

try:
    from cc_storage import CcStorage
except ImportError:
    import sys
    _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage

logger = logging.getLogger(__name__)


def _harden_file_permissions(path: Path) -> None:
    """Restrict a secret file to the owner only (POSIX).

    The MSAL token cache holds a refresh token that grants ongoing mailbox
    access. On POSIX the default umask can leave it group/world-readable, so
    scope it to the owner (0600). On Windows the %LOCALAPPDATA% profile already
    restricts access per-user, so no change is needed there.
    """
    if os.name == "posix":
        os.chmod(path, 0o600)


# Configuration - uses centralized cc-director storage
CONFIG_DIR = CcStorage.tool_config('outlook')
PROFILES_FILE = CONFIG_DIR / 'profiles.json'
TOKENS_DIR = CONFIG_DIR / 'tokens'

# Delegated permissions - no admin consent required
# Note: MSAL automatically handles offline_access for refresh token support
# We don't need to (and cannot) explicitly request it - it's a reserved scope
SCOPES = [
    'https://graph.microsoft.com/Mail.ReadWrite',
    'https://graph.microsoft.com/Mail.Send',
    'https://graph.microsoft.com/Calendars.ReadWrite',
    'https://graph.microsoft.com/User.Read',
    'https://graph.microsoft.com/MailboxSettings.Read',
]


# =============================================================================
# MSAL Token Backend for O365
# =============================================================================

class MSALTokenBackend(BaseTokenBackend):
    """
    Token backend that bridges MSAL tokens to O365 library.

    This custom backend allows the O365 library to use tokens acquired via MSAL's
    Device Code Flow. It implements the BaseTokenBackend interface that O365 expects.

    Token Storage:
    - Uses MSAL's SerializableTokenCache for persistence
    - Tokens stored as JSON at: {data_dir}/outlook/tokens/{email}_msal.json
    - Contains access tokens, refresh tokens, and account metadata

    Token Refresh:
    - acquire_token_silent() handles automatic refresh using stored refresh token
    - If silent refresh fails, user must re-authenticate via device code flow

    Format Conversion:
    - MSAL returns tokens in its own format
    - load_token() converts to the dict format O365 expects:
      {'token_type', 'access_token', 'refresh_token', 'expires_in', 'scope'}
    """

    def __init__(self, client_id: str, token_path: Path):
        super().__init__()
        self.client_id = client_id
        self.token_path = token_path
        # Always use SCOPES for token operations (includes offline_access for refresh tokens)
        self.scopes = SCOPES
        self._msal_app = None
        self._token_cache = msal.SerializableTokenCache()
        self._load_cache()

    def _load_cache(self):
        """Load token cache from file."""
        if self.token_path.exists():
            try:
                cache_data = self.token_path.read_text(encoding='utf-8')
                self._token_cache.deserialize(cache_data)
            except (json.JSONDecodeError, IOError) as e:
                logger.debug(f"Could not load token cache: {e}")

    def _save_cache(self):
        """Save token cache to file."""
        if self._token_cache.has_state_changed:
            self.token_path.parent.mkdir(parents=True, exist_ok=True)
            self.token_path.write_text(self._token_cache.serialize(), encoding='utf-8')
            _harden_file_permissions(self.token_path)

    @property
    def msal_app(self) -> msal.PublicClientApplication:
        """Get or create MSAL application instance."""
        if self._msal_app is None:
            self._msal_app = msal.PublicClientApplication(
                client_id=self.client_id,
                authority="https://login.microsoftonline.com/common",
                token_cache=self._token_cache
            )
        return self._msal_app

    def load_token(self) -> dict:
        """Load token, refreshing silently if needed."""
        import time
        accounts = self.msal_app.get_accounts()
        logger.debug(f"load_token: Found {len(accounts)} accounts in MSAL cache")
        if accounts:
            logger.debug(f"load_token: Calling acquire_token_silent with scopes: {self.scopes[:2]}...")
            result = self.msal_app.acquire_token_silent(self.scopes, account=accounts[0])
            if result:
                logger.debug(f"load_token: acquire_token_silent returned keys: {list(result.keys())}")
                if "error" in result:
                    logger.debug(f"load_token: MSAL error: {result.get('error')} - {result.get('error_description')}")
            else:
                logger.debug("load_token: acquire_token_silent returned None")
            if result and "access_token" in result:
                self._save_cache()
                # Set expires_at far in the future (1 year) so O365 never tries to refresh on its own.
                # MSAL handles actual token refresh internally via acquire_token_silent().
                # Every time O365 calls load_token(), we call acquire_token_silent() which
                # returns a fresh token if needed.
                expires_at = time.time() + (365 * 24 * 60 * 60)  # 1 year from now
                # Convert to O365 expected format
                return {
                    'token_type': result.get('token_type', 'Bearer'),
                    'access_token': result['access_token'],
                    'refresh_token': 'managed_by_msal',  # Placeholder - MSAL handles refresh internally
                    'expires_in': 365 * 24 * 60 * 60,  # 1 year
                    'expires_at': expires_at,
                    'scope': ' '.join(self.scopes),
                }
        return None

    def save_token(self):
        """Save is handled automatically by MSAL cache."""
        self._save_cache()

    def delete_token(self):
        """Clear the token cache."""
        if self.token_path.exists():
            self.token_path.unlink()
        self._token_cache = msal.SerializableTokenCache()
        self._msal_app = None

    def check_token(self) -> bool:
        """Check if we have a valid token."""
        token = self.load_token()
        return token is not None and 'access_token' in token

    def token_is_expired(self, username=None) -> bool:
        """
        Override: Always return False because MSAL handles token refresh internally.

        When O365 calls this, we return False to prevent O365 from trying to refresh
        the token using its own mechanism. Instead, every call to load_token() or
        get_access_token() goes through MSAL which handles refresh automatically.

        Args:
            username: Ignored - kept for API compatibility with BaseTokenBackend
        """
        return False

    def should_refresh_token(self, con=None) -> bool:
        """
        Override: Always return False because MSAL handles token refresh internally.

        This prevents O365 from attempting to refresh using its refresh mechanism.

        Args:
            con: Ignored - kept for API compatibility
        """
        return False

    def get_access_token(self, username=None) -> Optional[dict]:
        """
        Override: Get access token directly from MSAL.

        This bypasses O365's internal token caching and always gets a fresh
        (or refreshed) token from MSAL.

        O365's Connection class expects this to return a dict with a 'secret' key
        containing the actual access token string.

        Args:
            username: Ignored - kept for API compatibility with BaseTokenBackend

        Returns:
            Dict with 'secret' key containing access token, or None
        """
        token = self.load_token()
        if token and 'access_token' in token:
            return {'secret': token['access_token']}
        return None


# =============================================================================
# Device Code Flow Authentication
# =============================================================================

def authenticate_device_code_with_cache(client_id: str, token_path: Path,
                                         force: bool = False) -> dict:
    """
    Authenticate using Device Code Flow with token caching.

    Args:
        client_id: Azure App Client ID
        token_path: Path to store token cache
        force: Force re-authentication even if cached token exists

    Returns:
        Dict with token info
    """
    # Always use SCOPES which includes offline_access for refresh token support
    scopes = SCOPES

    # Set up token cache
    token_cache = msal.SerializableTokenCache()

    if not force and token_path.exists():
        try:
            cache_data = token_path.read_text(encoding='utf-8')
            token_cache.deserialize(cache_data)
        except (json.JSONDecodeError, IOError) as e:
            logger.debug(f"Could not load existing cache: {e}")

    app = msal.PublicClientApplication(
        client_id=client_id,
        authority="https://login.microsoftonline.com/common",
        token_cache=token_cache
    )

    result = None

    # Try silent authentication first (using cached refresh token)
    if not force:
        accounts = app.get_accounts()
        if accounts:
            result = app.acquire_token_silent(scopes, account=accounts[0])
            if result and "access_token" in result:
                logger.debug("Got token silently from cache")
                _save_token_cache(token_path, token_cache)
                return result

    # Need interactive authentication via device code
    flow = app.initiate_device_flow(scopes=scopes)

    if "user_code" not in flow:
        error_desc = flow.get('error_description', 'Unknown error initiating device flow')
        raise Exception(f"Failed to create device flow: {error_desc}")

    # Print the message for user (contains URL and code)
    # Use flush=True to ensure output appears immediately (important for PyInstaller)
    import sys
    print("", flush=True)
    print("=" * 60, flush=True)
    print("DEVICE CODE AUTHENTICATION", flush=True)
    print("=" * 60, flush=True)
    print("", flush=True)
    print(flow["message"], flush=True)
    print("", flush=True)
    print("=" * 60, flush=True)
    print("", flush=True)
    sys.stdout.flush()

    # Block until user completes authentication (or timeout)
    result = app.acquire_token_by_device_flow(flow)

    if "access_token" not in result:
        # Show full error details for debugging
        print(f"MSAL returned: {result}")
        error = result.get('error', 'unknown_error')
        error_desc = result.get('error_description', 'No description')
        correlation_id = result.get('correlation_id', 'N/A')
        raise Exception(f"Auth failed - Error: {error}, Description: {error_desc}, Correlation ID: {correlation_id}")

    # Save the cache (contains refresh token for future silent auth)
    _save_token_cache(token_path, token_cache)

    return result


def _save_token_cache(token_path: Path, token_cache: msal.SerializableTokenCache):
    """Save MSAL token cache to file."""
    if token_cache.has_state_changed:
        token_path.parent.mkdir(parents=True, exist_ok=True)
        token_path.write_text(token_cache.serialize(), encoding='utf-8')
        _harden_file_permissions(token_path)


def get_msal_token_path(email: str) -> Path:
    """Get the MSAL token cache path for an email account."""
    safe_name = email.replace('@', '_').replace('.', '_')
    return TOKENS_DIR / f'{safe_name}_msal.json'


def get_config_dir() -> Path:
    """Get the configuration directory path."""
    return CONFIG_DIR


def get_readme_path() -> Path:
    """Get path to README file."""
    return Path(__file__).parent.parent / 'README.md'


def _ensure_config_dirs() -> None:
    """Create config directories if they don't exist."""
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    TOKENS_DIR.mkdir(parents=True, exist_ok=True)


def _load_profiles() -> dict:
    """Load profiles from file."""
    _ensure_config_dirs()
    if PROFILES_FILE.exists():
        return json.loads(PROFILES_FILE.read_text(encoding='utf-8'))
    return {'profiles': {}, 'default': None}


def _save_profiles(profiles: dict) -> None:
    """Save profiles to file."""
    _ensure_config_dirs()
    PROFILES_FILE.write_text(json.dumps(profiles, indent=2), encoding='utf-8')


def get_token_path(account_name: str) -> Path:
    """Get token file path for an account."""
    safe_name = account_name.replace('@', '_').replace('.', '_')
    return TOKENS_DIR / f'{safe_name}.txt'


def get_account_dir(account_name: str) -> Path:
    """Get account directory path."""
    return CONFIG_DIR


def list_accounts() -> list:
    """
    List all configured accounts.

    Returns:
        List of dicts with account info:
        - name: Account name/email
        - is_default: Whether this is the default account
        - authenticated: Whether token exists and is valid
    """
    profiles = _load_profiles()
    result = []

    for email, profile in profiles.get('profiles', {}).items():
        # Check MSAL token path
        msal_token_path = get_msal_token_path(email)

        result.append({
            'name': email,
            'is_default': email == profiles.get('default'),
            'authenticated': msal_token_path.exists(),
            'client_id': profile.get('client_id', '')[:20] + '...' if profile.get('client_id') else '',
        })

    return result


def get_default_account() -> Optional[str]:
    """Get the default account email."""
    profiles = _load_profiles()
    return profiles.get('default')


def set_default_account(account_name: str) -> bool:
    """Set the default account."""
    profiles = _load_profiles()
    if account_name in profiles.get('profiles', {}):
        profiles['default'] = account_name
        _save_profiles(profiles)
        return True
    return False


def resolve_account(account_name: Optional[str] = None) -> str:
    """
    Resolve account name to use.

    Args:
        account_name: Optional explicit account name

    Returns:
        Account name to use

    Raises:
        ValueError: If no account can be resolved
    """
    if account_name:
        profiles = _load_profiles()
        if account_name not in profiles.get('profiles', {}):
            raise ValueError(f"Account '{account_name}' not found. Run 'cc-outlook accounts list' to see available accounts.")
        return account_name

    default = get_default_account()
    if not default:
        raise ValueError("No default account set. Run 'cc-outlook accounts add <email> --client-id <id>' to add an account.")

    return default


def get_profile(account_name: str) -> Optional[dict]:
    """Get profile data for an account."""
    profiles = _load_profiles()
    return profiles.get('profiles', {}).get(account_name)


def save_profile(email: str, client_id: str, tenant_id: str = 'common') -> None:
    """
    Save a new account profile.

    Args:
        email: Email address for the account
        client_id: Azure App Client ID
        tenant_id: Azure Tenant ID (default: 'common')
    """
    _ensure_config_dirs()
    profiles = _load_profiles()

    token_file = str(get_token_path(email))

    profiles['profiles'][email] = {
        'client_id': client_id,
        'tenant_id': tenant_id,
        'token_file': token_file
    }

    # Set as default if it's the first account
    if not profiles.get('default'):
        profiles['default'] = email

    _save_profiles(profiles)


def delete_account(account_name: str) -> bool:
    """
    Delete an account and its token.

    Args:
        account_name: Account email to delete

    Returns:
        True if deleted, False if not found
    """
    profiles = _load_profiles()

    if account_name not in profiles.get('profiles', {}):
        return False

    profile = profiles['profiles'].pop(account_name)

    # Delete MSAL token cache if it exists
    msal_token_path = get_msal_token_path(account_name)
    if msal_token_path.exists():
        msal_token_path.unlink()

    # Delete old token file if it exists (backwards compatibility)
    old_token_file = Path(profile.get('token_file', ''))
    if old_token_file.exists():
        old_token_file.unlink()

    # Update default if needed
    if profiles.get('default') == account_name:
        remaining = list(profiles.get('profiles', {}).keys())
        profiles['default'] = remaining[0] if remaining else None

    _save_profiles(profiles)
    return True


def get_auth_status(account_name: str) -> dict:
    """
    Get detailed authentication status for an account.

    Args:
        account_name: Account email

    Returns:
        Dict with status info
    """
    profile = get_profile(account_name)
    if not profile:
        return {
            'account_dir': str(CONFIG_DIR),
            'token_exists': False,
            'authenticated': False,
            'is_default': False,
        }

    # Use MSAL token path
    token_path = get_msal_token_path(account_name)
    is_authenticated = False

    # Check if token is valid
    if token_path.exists():
        try:
            account = _create_account(profile, email=account_name)
            is_authenticated = account.is_authenticated
        except ValueError as e:
            logger.debug(f"Token validation failed (ValueError): {e}")
            is_authenticated = False
        except KeyError as e:
            logger.debug(f"Token validation failed (KeyError): {e}")
            is_authenticated = False
        except OSError as e:
            logger.debug(f"Token file inaccessible: {e}")
            is_authenticated = False

    return {
        'account_dir': str(CONFIG_DIR),
        'token_exists': token_path.exists(),
        'authenticated': is_authenticated,
        'is_default': account_name == get_default_account(),
        'client_id': profile.get('client_id', '')[:20] + '...',
        'tenant_id': profile.get('tenant_id', 'common'),
    }


def _create_account(profile: dict, email: str = None) -> Account:
    """Create an O365 Account instance from profile data using MSAL token backend."""
    client_id = profile['client_id']
    tenant_id = profile.get('tenant_id', 'common')

    # Use MSAL token backend
    token_path = get_msal_token_path(email) if email else None
    token_backend = None

    if token_path:
        token_backend = MSALTokenBackend(
            client_id=client_id,
            token_path=token_path
        )

    # Initialize account with public client flow (no client secret)
    credentials = (client_id,)

    return Account(
        credentials,
        tenant_id=tenant_id,
        scopes=SCOPES,
        auth_flow_type='public',
        token_backend=token_backend
    )


def _log_token_debug_info(result: dict, token_path: Path, account: Account) -> None:
    """Log debug information about token acquisition."""
    logger.debug("Token acquired: %s", 'access_token' in result)
    logger.debug("Token file exists: %s", token_path.exists())
    if token_path.exists():
        logger.debug("Token file size: %s bytes", token_path.stat().st_size)

    if account.con and account.con.token_backend:
        token = account.con.token_backend.load_token()
        logger.debug("Token backend returned: %s", token is not None)
        if token:
            logger.debug("Token has access_token: %s", 'access_token' in token)

    logger.debug("account.is_authenticated = %s", account.is_authenticated)


def authenticate(account_name: str, force: bool = False) -> Account:
    """
    Authenticate with Outlook/Microsoft Graph using Device Code Flow.

    Args:
        account_name: Account email
        force: Force re-authentication even if token exists

    Returns:
        Authenticated O365 Account

    Raises:
        ValueError: If account not found
        RuntimeError: If authentication fails
    """
    profile = get_profile(account_name)
    if not profile:
        raise ValueError(f"Account '{account_name}' not found")

    client_id = profile['client_id']
    token_path = get_msal_token_path(account_name)

    if force and token_path.exists():
        token_path.unlink()

    account = _create_account(profile, email=account_name)

    if not force and account.is_authenticated:
        return account

    result = authenticate_device_code_with_cache(
        client_id=client_id,
        token_path=token_path,
        force=force
    )

    account = _create_account(profile, email=account_name)
    _log_token_debug_info(result, token_path, account)

    if account.is_authenticated:
        return account

    # O365 may report not authenticated even with valid MSAL token
    if result and 'access_token' in result:
        logger.debug("Returning account with valid MSAL token")
        return account

    raise RuntimeError("Authentication failed. Please try again.")


def revoke_token(account_name: str) -> bool:
    """
    Revoke/delete the token for an account.

    Args:
        account_name: Account email

    Returns:
        True if token was deleted, False if not found
    """
    profile = get_profile(account_name)
    if not profile:
        return False

    # Delete MSAL token cache
    msal_token_path = get_msal_token_path(account_name)
    if msal_token_path.exists():
        msal_token_path.unlink()
        return True

    # Also try old token path for backwards compatibility
    old_token_path = Path(profile.get('token_file', ''))
    if old_token_path.exists():
        old_token_path.unlink()
        return True

    return False

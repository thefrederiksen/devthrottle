"""CLI for cc-gmail - Gmail from the command line with multi-account support.

Supports two authentication methods:
  - App Password (IMAP/SMTP) -- Quick Setup, works for most accounts
  - OAuth (Gmail API) -- Full Setup, required when IMAP is blocked
"""

import json
import logging
import sys
from datetime import datetime
from pathlib import Path
from typing import Optional, List, Tuple

# Suppress Google's file_cache warning before importing googleapiclient
logging.getLogger("googleapiclient.discovery_cache").setLevel(logging.ERROR)

import typer
from googleapiclient.errors import HttpError
from rich.console import Console
from rich.table import Table as _RichTable
from rich.panel import Panel as _RichPanel
from rich.text import Text
from rich import box as _box

# --- ASCII-only output (project house rule): Rich truncates an overflowing table cell with the
# Unicode ellipsis U+2026; emit ASCII "..." instead. Patched once at module import. ---
def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return
    def truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    truncate._ascii_ellipsis = True
    rich.text.Text.truncate = truncate


_install_ascii_truncation()


def Table(*args, **kwargs):
    """Rich Table that defaults to an ASCII box (house ASCII-only output rule).

    Rich's default table border uses Unicode box-drawing characters. Defaulting
    the box to ASCII keeps all rendered output to plain ASCII. Call sites that
    pass box=None (borderless) are preserved.
    """
    kwargs.setdefault("box", _box.ASCII)
    return _RichTable(*args, **kwargs)


def Panel(*args, **kwargs):
    """Rich Panel that defaults to an ASCII box (house ASCII-only output rule)."""
    kwargs.setdefault("box", _box.ASCII)
    return _RichPanel(*args, **kwargs)

logger = logging.getLogger(__name__)

try:
    from . import __version__
    from .auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        credentials_exist,
        get_credentials_path,
        get_readme_path,
        list_accounts,
        set_default_account,
        get_default_account,
        delete_account,
        resolve_account,
        get_account_dir,
        get_auth_method,
        get_account_email,
        get_app_password,
        store_app_password,
        delete_app_password,
        save_account_config,
        load_account_config,
        test_imap_connection,
        test_smtp_connection,
    )
    from .gmail_api import GmailClient
    from .imap_client import ImapClient
    from .smtp_client import SmtpClient
    from .calendar_api import CalendarClient
    from .contacts_api import ContactsClient
    from .utils import format_timestamp, truncate, format_message_summary
    from .auth import check_token_scopes
except ImportError:
    from src import __version__
    from src.auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        credentials_exist,
        get_credentials_path,
        get_readme_path,
        list_accounts,
        set_default_account,
        get_default_account,
        delete_account,
        resolve_account,
        get_account_dir,
        get_auth_method,
        get_account_email,
        get_app_password,
        store_app_password,
        delete_app_password,
        save_account_config,
        load_account_config,
        test_imap_connection,
        test_smtp_connection,
        check_token_scopes,
    )
    from src.gmail_api import GmailClient
    from src.imap_client import ImapClient
    from src.smtp_client import SmtpClient
    from src.calendar_api import CalendarClient
    from src.contacts_api import ContactsClient
    from src.utils import format_timestamp, truncate, format_message_summary

# Configure logging for library modules.
# User-facing output goes through the Rich console (stdout). Attaching a
# NullHandler to the root logger keeps internal logger.error/info calls from
# ALSO printing to stderr, which would double-print every error (once via the
# logger and once via console.print). A handler is present, so Python's
# last-resort stderr handler stays disabled.
logging.getLogger().addHandler(logging.NullHandler())

app = typer.Typer(
    name="cc-gmail",
    help="Gmail CLI: read, send, search, and manage emails from the command line.",
    add_completion=False,
)
accounts_app = typer.Typer(help="Manage Gmail accounts")
app.add_typer(accounts_app, name="accounts")

calendar_app = typer.Typer(help="Google Calendar operations (OAuth only)")
app.add_typer(calendar_app, name="calendar")

contacts_app = typer.Typer(help="Google Contacts operations (OAuth only)")
app.add_typer(contacts_app, name="contacts")

# Configure console to handle Unicode safely on Windows
# This prevents UnicodeEncodeError when emails contain emoji
if sys.platform == "win32":
    # Use UTF-8 encoding for Windows console output
    import io
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

console = Console()


def handle_api_error(error: Exception, account: str) -> None:
    """Parse Gmail API errors and provide user-friendly guidance."""
    error_str = str(error)

    if "Gmail API has not been used in project" in error_str or "accessNotConfigured" in error_str:
        console.print("[red]Error:[/red] Gmail API is not enabled for your Google Cloud project.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Go to: https://console.cloud.google.com/apis/library/gmail.googleapis.com")
        console.print("2. Make sure your project is selected at the top")
        console.print("3. Click 'Enable'")
        console.print("4. Wait a minute, then try again")
        return

    if "redirect_uri_mismatch" in error_str:
        console.print("[red]Error:[/red] OAuth client type is incorrect.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Go to: https://console.cloud.google.com/apis/credentials")
        console.print("2. Delete your existing OAuth client")
        console.print("3. Create a new one with type 'Desktop app' (not 'Web application')")
        console.print("4. Download the new credentials.json")
        console.print(f"5. Replace: {get_credentials_path(account)}")
        return

    if "access_denied" in error_str or "has not completed the Google verification" in error_str:
        console.print("[red]Error:[/red] Your Google account is not authorized to use this app.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Go to: https://console.cloud.google.com/apis/credentials/consent")
        console.print("2. Under 'Test users', click 'Add Users'")
        console.print("3. Add your Gmail address")
        console.print("4. Try again")
        return

    if "invalid_grant" in error_str or "Token has been expired or revoked" in error_str:
        console.print("[red]Error:[/red] Your authentication token has expired or been revoked.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print(f"  cc-gmail --account {account} auth --force")
        return

    if "invalid_client" in error_str:
        console.print("[red]Error:[/red] The credentials.json file is invalid or corrupted.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Go to: https://console.cloud.google.com/apis/credentials")
        console.print("2. Download your OAuth client credentials again")
        console.print(f"3. Replace: {get_credentials_path(account)}")
        return

    console.print(f"[red]Error:[/red] {error}")
    console.print(f"\nSee README for troubleshooting: {get_readme_path()}")


def handle_calendar_api_error(error: Exception, account: str) -> None:
    """Parse Calendar API errors and provide user-friendly guidance."""
    error_str = str(error)

    if "Calendar API has not been used" in error_str or "calendar-json.googleapis.com" in error_str:
        console.print("[red]Error:[/red] Google Calendar API is not enabled for your project.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Open this link in your browser:")
        console.print("   https://console.cloud.google.com/apis/library/calendar-json.googleapis.com")
        console.print("2. Make sure your project is selected at the top")
        console.print("3. Click 'Enable'")
        console.print("4. Wait 1-2 minutes for the change to propagate")
        console.print("5. Try again")
        return

    if "insufficient" in error_str.lower() or "scope" in error_str.lower():
        console.print("[red]Error:[/red] Your OAuth token is missing calendar permissions.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print(f"  cc-gmail -a {account} auth --force")
        console.print("\nThis will open a browser to re-authorize with calendar access.")
        console.print("You only need to do this once.")
        return

    handle_api_error(error, account)


def handle_contacts_api_error(error: Exception, account: str) -> None:
    """Parse People API errors and provide user-friendly guidance."""
    error_str = str(error)

    if "People API has not been used" in error_str or "people.googleapis.com" in error_str:
        console.print("[red]Error:[/red] Google People API is not enabled for your project.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Open this link in your browser:")
        console.print("   https://console.cloud.google.com/apis/library/people.googleapis.com")
        console.print("2. Make sure your project is selected at the top")
        console.print("3. Click 'Enable'")
        console.print("4. Wait 1-2 minutes for the change to propagate")
        console.print("5. Try again")
        return

    if "insufficient" in error_str.lower() or "scope" in error_str.lower():
        console.print("[red]Error:[/red] Your OAuth token is missing contacts permissions.")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print(f"  cc-gmail -a {account} auth --force")
        console.print("\nThis will open a browser to re-authorize with contacts access.")
        console.print("You only need to do this once.")
        return

    handle_api_error(error, account)


def _require_oauth(acct: str, auth_method: str, feature: str) -> None:
    """Exit with guidance if user is on app_password for an OAuth-only feature."""
    if auth_method != "oauth":
        console.print(f"[red]Error:[/red] {feature} requires OAuth authentication.")
        console.print("\nApp Password (IMAP/SMTP) does not support this feature.")
        console.print("Google Calendar and Contacts require the OAuth (API) path.")
        console.print("\n[yellow]To switch to OAuth:[/yellow]")
        console.print(f"  cc-gmail -a {acct} auth --method oauth")
        console.print("\nThis will walk you through Google Cloud Console setup.")
        raise typer.Exit(1)


def _require_calendar_scopes(acct: str) -> None:
    """Check that the current token has calendar scopes, guide upgrade if not."""
    scope_status = check_token_scopes(acct)
    if not scope_status["calendar"]:
        console.print("[yellow]Notice:[/yellow] Your OAuth token does not include calendar permissions.")
        console.print("\nThis happens if you set up OAuth before calendar support was added.")
        console.print("\n[yellow]To fix this (one-time):[/yellow]")
        console.print(f"  cc-gmail -a {acct} auth --force")
        console.print("\nThis will open a browser to re-authorize with the new permissions.")
        raise typer.Exit(1)


def _require_contacts_scopes(acct: str) -> None:
    """Check that the current token has contacts scopes, guide upgrade if not."""
    scope_status = check_token_scopes(acct)
    if not scope_status["contacts"]:
        console.print("[yellow]Notice:[/yellow] Your OAuth token does not include contacts permissions.")
        console.print("\nThis happens if you set up OAuth before contacts support was added.")
        console.print("\n[yellow]To fix this (one-time):[/yellow]")
        console.print(f"  cc-gmail -a {acct} auth --force")
        console.print("\nThis will open a browser to re-authorize with the new permissions.")
        raise typer.Exit(1)


def _get_calendar_client(account: Optional[str] = None) -> Tuple[str, CalendarClient]:
    """Get authenticated CalendarClient. Returns (account_name, client)."""
    acct, auth_method = _resolve_and_get_auth(account)
    _require_oauth(acct, auth_method, "Calendar")
    _require_calendar_scopes(acct)

    try:
        creds = authenticate(acct)
        return acct, CalendarClient(creds)
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)
    except (ValueError, OSError) as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


def _get_contacts_client(account: Optional[str] = None) -> Tuple[str, ContactsClient]:
    """Get authenticated ContactsClient. Returns (account_name, client)."""
    acct, auth_method = _resolve_and_get_auth(account)
    _require_oauth(acct, auth_method, "Contacts")
    _require_contacts_scopes(acct)

    try:
        creds = authenticate(acct)
        return acct, ContactsClient(creds)
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)
    except (ValueError, OSError) as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


# Global state for account selection
class State:
    account: Optional[str] = None

state = State()


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-gmail version {__version__}")
        raise typer.Exit()


def _get_imap_client(account: str) -> ImapClient:
    """Get an IMAP client for an app_password account."""
    email_addr = get_account_email(account)
    password = get_app_password(account)
    if not email_addr or not password:
        console.print(f"[red]Error:[/red] App password not configured for account '{account}'")
        console.print(f"\nRun: cc-gmail accounts add {account}")
        raise typer.Exit(1)
    return ImapClient(email_addr, password)


def _get_smtp_client(account: str) -> SmtpClient:
    """Get an SMTP client for an app_password account."""
    email_addr = get_account_email(account)
    password = get_app_password(account)
    if not email_addr or not password:
        console.print(f"[red]Error:[/red] App password not configured for account '{account}'")
        console.print(f"\nRun: cc-gmail accounts add {account}")
        raise typer.Exit(1)
    return SmtpClient(email_addr, password)


def get_client(account: Optional[str] = None) -> GmailClient:
    """Get authenticated Gmail API client (OAuth path only)."""
    try:
        acct = resolve_account(account or state.account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    if not credentials_exist(acct):
        console.print(f"[red]Error:[/red] OAuth credentials not found for account '{acct}'")
        console.print(f"\nExpected location: {get_credentials_path(acct)}")
        console.print("\n[yellow]To fix this:[/yellow]")
        console.print("1. Download credentials from Google Cloud Console")
        console.print("2. Save as: credentials.json in the path above")
        console.print(f"\nSee README for detailed steps: {get_readme_path()}")
        raise typer.Exit(1)

    try:
        creds = authenticate(acct, interactive=False)
        return GmailClient(creds)
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except HttpError as e:
        handle_api_error(e, acct)
        raise typer.Exit(1)
    except (ValueError, OSError) as e:
        logger.error(f"Authentication error for {acct}: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


def _resolve_and_get_auth(account: Optional[str] = None):
    """Resolve the account and return (account_name, auth_method).

    Returns:
        Tuple of (account_name, auth_method).
    """
    try:
        acct = resolve_account(account or state.account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    auth_method = get_auth_method(acct) or "oauth"
    return acct, auth_method


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    version: bool = typer.Option(
        False,
        "--version",
        "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
    account: Optional[str] = typer.Option(
        None,
        "--account",
        "-a",
        help="Gmail account to use (default: uses default account)",
    ),
):
    """Gmail CLI: read, send, search, and manage emails."""
    state.account = account
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


# =============================================================================
# Account Management Commands
# =============================================================================

@accounts_app.command("list")
def accounts_list(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON for machine consumption"),
) -> None:
    """List all configured Gmail accounts."""
    accts = list_accounts()

    if json_output:
        result = {
            "tool": "cc-gmail",
            "accounts": [
                {
                    "name": acct["name"],
                    "email": acct.get("email", ""),
                    "is_default": acct["is_default"],
                    "authenticated": acct["authenticated"],
                    "can_send": acct["authenticated"] and bool(acct.get("email")),
                }
                for acct in accts
            ],
        }
        print(json.dumps(result))
        return

    if not accts:
        console.print("[yellow]No accounts configured.[/yellow]")
        console.print("\nTo add an account:")
        console.print("  cc-gmail accounts add <name>")
        console.print(f"\nSee README for setup: {get_readme_path()}")
        return

    table = Table(title="Gmail Accounts")
    table.add_column("Account", style="cyan")
    table.add_column("Default")
    table.add_column("Auth Method")
    table.add_column("Email")
    table.add_column("Status")

    for acct in accts:
        method = acct.get("auth_method", "unknown")
        method_display = "App Password" if method == "app_password" else "OAuth" if method == "oauth" else method

        table.add_row(
            acct["name"],
            "[green]*[/green]" if acct["is_default"] else "",
            method_display,
            acct.get("email", ""),
            "[green]Ready[/green]" if acct["authenticated"] else "[yellow]Setup needed[/yellow]",
        )

    console.print(table)


@accounts_app.command("add")
def accounts_add(
    name: str = typer.Argument(..., help="Account name (e.g., 'personal', 'work')"),
    set_as_default: bool = typer.Option(False, "--default", "-d", help="Set as default account"),
):
    """Add a new Gmail account with interactive setup."""
    account_dir = get_account_dir(name)

    # Check if account already exists
    existing_config = load_account_config(name)
    if existing_config.get("auth_method"):
        console.print(f"[yellow]Account '{name}' already exists.[/yellow]")
        console.print(f"  Auth method: {existing_config.get('auth_method')}")
        console.print(f"  Email: {existing_config.get('email', 'not set')}")
        console.print(f"\nTo re-authenticate: cc-gmail -a {name} auth --force")
        console.print(f"To remove: cc-gmail accounts remove {name}")
        return

    console.print(f"\n[cyan]Setting up account:[/cyan] {name}")
    console.print()

    # Get email address
    email_addr = typer.prompt("Email address")

    console.print()
    console.print("[bold]-- Quick Setup (App Password) --[/bold]")
    console.print("Works with most Gmail accounts. Takes 2 minutes.")
    console.print()
    console.print("Steps:")
    console.print("  1. Enable 2-Step Verification (if not already on)")
    console.print("     https://myaccount.google.com/security")
    console.print("  2. Create an App Password")
    console.print("     https://myaccount.google.com/apppasswords")
    console.print('     Name it "cc-gmail", copy the 16-character password')
    console.print()

    password_input = typer.prompt(
        "App Password (or 'oauth' for advanced setup)",
        hide_input=True,
    )

    if password_input.strip().lower() == "oauth":
        # OAuth path
        _setup_oauth_account(name, email_addr, set_as_default)
        return

    # App Password path -- test the connection
    # Strip spaces from app password (Google shows it as "xxxx xxxx xxxx xxxx")
    password = password_input.replace(" ", "")

    console.print()
    console.print("Testing connection...")

    try:
        test_imap_connection(email_addr, password)
        console.print("  [green][OK][/green] IMAP login successful (imap.gmail.com)")
    except ConnectionError as e:
        console.print(f"  [red][FAILED][/red] IMAP login failed")
        console.print()
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)

    try:
        test_smtp_connection(email_addr, password)
        console.print("  [green][OK][/green] SMTP login successful (smtp.gmail.com)")
    except ConnectionError as e:
        console.print(f"  [red][FAILED][/red] SMTP login failed")
        console.print()
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)

    # Store credentials
    store_app_password(name, password)
    save_account_config(name, {
        "email": email_addr,
        "auth_method": "app_password",
    })

    if set_as_default or not get_default_account():
        set_default_account(name)

    console.print()
    console.print(f"[green]Account '{name}' ready![/green] Auth method: app_password")


def _setup_oauth_account(name: str, email_addr: str, set_as_default: bool) -> None:
    """Set up an account with OAuth authentication."""
    creds_path = get_credentials_path(name)

    save_account_config(name, {
        "email": email_addr,
        "auth_method": "oauth",
    })

    console.print()
    console.print("[bold]-- OAuth Setup (Advanced) --[/bold]")
    console.print()

    if creds_path.exists():
        console.print(f"[yellow]Credentials file already exists for '{name}'[/yellow]")
        console.print(f"Run 'cc-gmail -a {name} auth' to authenticate.")
    else:
        console.print("To complete setup:")
        console.print()
        console.print("1. Go to Google Cloud Console:")
        console.print("   https://console.cloud.google.com/")
        console.print()
        console.print("2. Create a new project (one project per Gmail account is recommended)")
        console.print("   - Name it 'cc-gmail' or similar")
        console.print("   - For Workspace accounts, create under your organization")
        console.print()
        console.print("3. Enable these APIs (click each link, select your project, click Enable):")
        console.print()
        console.print("   Gmail API (required for email):")
        console.print("   https://console.cloud.google.com/apis/library/gmail.googleapis.com")
        console.print()
        console.print("   Google Calendar API (for calendar commands):")
        console.print("   https://console.cloud.google.com/apis/library/calendar-json.googleapis.com")
        console.print()
        console.print("   People API (for contacts commands):")
        console.print("   https://console.cloud.google.com/apis/library/people.googleapis.com")
        console.print()
        console.print("4. Set up OAuth consent screen:")
        console.print("   https://console.cloud.google.com/apis/credentials/consent")
        console.print("   - Select 'External' user type (or 'Internal' for Workspace)")
        console.print("   - Fill in app name (e.g., 'cc-gmail') and your email")
        console.print("   - Under 'Test users', add your Gmail address")
        console.print()
        console.print("5. Register scopes on the Data Access page (critical step):")
        console.print("   Click 'Data Access' in the left sidebar")
        console.print("   - Click 'Add or remove scopes'")
        console.print("   - Scroll to 'Manually add scopes' at the bottom")
        console.print("   - Add each scope one at a time: type it, check the box, click Update")
        console.print("   - Add these 6 scopes:")
        console.print("     gmail.send")
        console.print("     gmail.readonly")
        console.print("     gmail.compose")
        console.print("     gmail.modify")
        console.print("     auth/calendar  (pick the shortest match)")
        console.print("     auth/contacts  (pick the shortest match)")
        console.print("   - After all 6 are added, click 'Save'")
        console.print("   (Without this, Google silently drops scopes from consent)")
        console.print("   NOTE: Do NOT add mail.google.com -- that is a different scope")
        console.print()
        console.print("6. Create OAuth credentials:")
        console.print("   https://console.cloud.google.com/apis/credentials")
        console.print("   - Click 'Create Credentials' -> 'OAuth client ID'")
        console.print("   - Select 'Desktop app' as application type")
        console.print("   - Download the JSON file")
        console.print()
        console.print("7. Save the downloaded file as:")
        console.print(f"   [green]{creds_path}[/green]")
        console.print()
        console.print("8. Run authentication:")
        console.print(f"   cc-gmail -a {name} auth")
        console.print()
        console.print("   If using a specific browser profile or remote machine:")
        console.print(f"   cc-gmail -a {name} auth --no-browser")

    if set_as_default or not get_default_account():
        set_default_account(name)
        console.print(f"\n[green]'{name}' set as default account.[/green]")


@accounts_app.command("default")
def accounts_default(
    name: str = typer.Argument(..., help="Account name to set as default"),
):
    """Set the default Gmail account."""
    accts = list_accounts()
    account_names = [a["name"] for a in accts]

    if name not in account_names:
        console.print(f"[red]Error:[/red] Account '{name}' not found.")
        if account_names:
            console.print(f"Available accounts: {', '.join(account_names)}")
        else:
            console.print("No accounts configured. Run 'cc-gmail accounts add <name>' first.")
        raise typer.Exit(1)

    set_default_account(name)
    console.print(f"[green]Default account set to '{name}'[/green]")


@accounts_app.command("remove")
def accounts_remove(
    name: str = typer.Argument(..., help="Account name to remove"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Remove a Gmail account."""
    accts = list_accounts()
    account_names = [a["name"] for a in accts]

    if name not in account_names:
        console.print(f"[red]Error:[/red] Account '{name}' not found.")
        raise typer.Exit(1)

    if not yes:
        confirm = typer.confirm(f"Remove account '{name}' and all its data?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    if delete_account(name):
        console.print(f"[green]Account '{name}' removed.[/green]")
    else:
        console.print(f"[red]Failed to remove account '{name}'[/red]")


@accounts_app.command("status")
def accounts_status(
    name: Optional[str] = typer.Argument(None, help="Account name (default: current account)"),
):
    """Show detailed status for an account."""
    try:
        acct = resolve_account(name or state.account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    info = get_auth_status(acct)

    table = Table(title=f"Account Status: {acct}")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("Account Directory", info["account_dir"])
    table.add_row("Auth Method", info.get("auth_method", "unknown"))
    table.add_row("Email", info.get("email") or "not set")

    if info.get("auth_method") == "app_password":
        table.add_row(
            "App Password",
            "[green]Stored[/green]" if info["credentials_exists"] else "[red]Missing[/red]",
        )
    else:
        table.add_row(
            "Credentials File",
            "[green]Found[/green]" if info["credentials_exists"] else "[red]Missing[/red]",
        )
        table.add_row(
            "Token File",
            "[green]Found[/green]" if info.get("token_exists") else "[yellow]Not created[/yellow]",
        )

    table.add_row(
        "Authenticated",
        "[green]Yes[/green]" if info["authenticated"] else "[red]No[/red]",
    )
    table.add_row(
        "Default Account",
        "[green]Yes[/green]" if info["is_default"] else "No",
    )

    # Show scope status for OAuth accounts
    if info.get("auth_method") == "oauth" and info["authenticated"]:
        scope_status = check_token_scopes(acct)
        table.add_row(
            "Gmail Scopes",
            "[green]OK[/green]" if scope_status["gmail"] else "[red]Missing[/red]",
        )
        table.add_row(
            "Calendar Scopes",
            "[green]OK[/green]" if scope_status["calendar"] else "[yellow]Not granted[/yellow] (run auth --force)",
        )
        table.add_row(
            "Contacts Scopes",
            "[green]OK[/green]" if scope_status["contacts"] else "[yellow]Not granted[/yellow] (run auth --force)",
        )

    console.print(table)

    if not info["authenticated"]:
        if info.get("auth_method") == "app_password":
            console.print(f"\n[yellow]Setup needed.[/yellow] Run: cc-gmail accounts add {acct}")
        else:
            console.print(f"\n[yellow]Setup needed.[/yellow] See: {get_readme_path()}")


# =============================================================================
# Authentication Commands
# =============================================================================

@app.command()
def auth(
    force: bool = typer.Option(False, "--force", "-f", help="Force re-authentication"),
    revoke: bool = typer.Option(False, "--revoke", help="Revoke current token / delete app password"),
    method: Optional[str] = typer.Option(None, "--method", "-m", help="Auth method: app_password or oauth"),
    no_browser: bool = typer.Option(False, "--no-browser", help="Don't auto-open browser; print auth URL instead"),
):
    """Authenticate with Gmail."""
    try:
        acct = resolve_account(state.account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    auth_method = method or get_auth_method(acct) or "oauth"

    if revoke:
        if auth_method == "app_password":
            if delete_app_password(acct):
                console.print(f"[green]App password removed for '{acct}'.[/green]")
                console.print(f"Run 'cc-gmail accounts add {acct}' to set up again.")
            else:
                console.print("[yellow]No app password to remove.[/yellow]")
        else:
            if revoke_token(acct):
                console.print(f"[green]Token revoked for '{acct}'.[/green]")
                console.print("Run 'cc-gmail auth' to re-authenticate.")
            else:
                console.print("[yellow]No token to revoke.[/yellow]")
        return

    # If method is specified and different from current, switch
    if method and method != get_auth_method(acct):
        console.print(f"[blue]Switching auth method to '{method}' for account '{acct}'...[/blue]")
        acct_config = load_account_config(acct)
        acct_config["auth_method"] = method
        save_account_config(acct, acct_config)
        auth_method = method

    if auth_method == "app_password":
        # App password auth flow
        email_addr = get_account_email(acct)
        if not email_addr:
            email_addr = typer.prompt("Email address")

        if force or not get_app_password(acct):
            console.print()
            console.print("Create an App Password at:")
            console.print("  https://myaccount.google.com/apppasswords")
            console.print()
            password = typer.prompt("App Password", hide_input=True).replace(" ", "")

            console.print("Testing connection...")
            try:
                test_imap_connection(email_addr, password)
                console.print("  [green][OK][/green] IMAP login successful")
            except ConnectionError as e:
                console.print(f"  [red][FAILED][/red] {e}")
                raise typer.Exit(1)

            try:
                test_smtp_connection(email_addr, password)
                console.print("  [green][OK][/green] SMTP login successful")
            except ConnectionError as e:
                console.print(f"  [red][FAILED][/red] {e}")
                raise typer.Exit(1)

            store_app_password(acct, password)
            save_account_config(acct, {
                "email": email_addr,
                "auth_method": "app_password",
            })
            console.print(f"\n[green]Authenticated as:[/green] {email_addr}")
        else:
            console.print(f"[green]Already authenticated as:[/green] {email_addr}")
            console.print("Use --force to re-authenticate.")

    else:
        # OAuth flow (existing behavior)
        if not credentials_exist(acct):
            console.print(f"[red]Error:[/red] OAuth credentials not found for account '{acct}'")
            console.print(f"\nExpected location: {get_credentials_path(acct)}")
            console.print(f"\nRun 'cc-gmail accounts add {acct}' for setup instructions.")
            console.print(f"Or see README: {get_readme_path()}")
            raise typer.Exit(1)

        try:
            console.print(f"[blue]Authenticating account '{acct}'...[/blue]")
            if no_browser:
                console.print("Auth URL will be printed below. Open it in your preferred browser.")
            else:
                console.print("A browser window will open for authentication.")
            creds = authenticate(acct, force=force, open_browser=not no_browser)

            client = GmailClient(creds)
            profile = client.get_profile()

            console.print(f"\n[green]Authenticated as:[/green] {profile.get('emailAddress')}")
        except HttpError as e:
            handle_api_error(e, acct)
            raise typer.Exit(1)
        except (ValueError, OSError) as e:
            logger.error(f"Auth error: {e}")
            console.print(f"[red]Error:[/red] {e}")
            raise typer.Exit(1)


# =============================================================================
# Email Commands -- dual-path routing
# =============================================================================

@app.command("list")
def list_emails(
    label: str = typer.Option("INBOX", "-l", "--label", help="Label/folder to list"),
    count: int = typer.Option(10, "-n", "--count", help="Number of emails to show"),
    unread: bool = typer.Option(False, "-u", "--unread", help="Show only unread"),
    include_spam: bool = typer.Option(False, "--include-spam", help="Include messages from spam and trash"),
):
    """List recent emails from a label/folder."""
    acct, auth_method = _resolve_and_get_auth()

    label_ids = [label.upper()]
    if unread:
        label_ids.append("UNREAD")

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            messages = client.list_all_messages(
                label_ids=label_ids,
                max_results=count,
            )
        else:
            api_client = get_client()
            messages = api_client.list_all_messages(
                label_ids=label_ids,
                max_results=count,
            )

        if not messages:
            console.print(f"[yellow]No messages in {label}[/yellow]")
            return

        console.print(f"\n[cyan]Messages in {label} ({acct})[/cyan]\n")

        for msg_summary in messages:
            if auth_method == "app_password":
                msg = client.get_message_details(msg_summary["id"])
            else:
                msg = api_client.get_message_details(msg_summary["id"])
            summary = format_message_summary(msg)

            is_unread = "UNREAD" in msg.get("labels", [])
            style = "bold" if is_unread else ""
            marker = "[*]" if is_unread else "[ ]"

            console.print(f"{marker} [dim]{summary['id']}[/dim]", style=style)
            console.print(f"    From: {truncate(summary['from'], 50)}", style=style)
            console.print(f"    Subject: {truncate(summary['subject'], 60)}", style=style)
            console.print(f"    Date: {summary['date'][:25] if summary['date'] else ''}", style=style)
            console.print()

    except HttpError as e:
        logger.error(f"Gmail API error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def read(
    message_id: str = typer.Argument(..., help="Message ID to read"),
    raw: bool = typer.Option(False, "--raw", help="Show raw message data"),
):
    """Read a specific email."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            msg = client.get_message_details(message_id)
            client.mark_as_read(message_id)
        else:
            api_client = get_client()
            msg = api_client.get_message_details(message_id)
            api_client.mark_as_read(message_id)

        summary = format_message_summary(msg)

        header_text = Text()
        header_text.append(f"From: ", style="cyan")
        header_text.append(f"{summary['from']}\n")
        header_text.append(f"To: ", style="cyan")
        header_text.append(f"{summary['to']}\n")
        header_text.append(f"Date: ", style="cyan")
        header_text.append(f"{summary['date']}\n")
        header_text.append(f"Subject: ", style="cyan bold")
        header_text.append(f"{summary['subject']}")

        console.print(Panel(header_text, title=f"Message {message_id[:16]}"))

        body = msg.get("body", "(No body)")
        if raw:
            console.print(body)
        else:
            console.print("\n" + body)

    except HttpError as e:
        logger.error(f"Gmail API error reading message: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def send(
    to: str = typer.Option(..., "-t", "--to", help="Recipient email"),
    subject: str = typer.Option(..., "-s", "--subject", help="Email subject"),
    body: str = typer.Option(None, "-b", "--body", help="Email body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    cc: str = typer.Option(None, "--cc", help="CC recipients"),
    bcc: str = typer.Option(None, "--bcc", help="BCC recipients"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
    attach: Optional[list[Path]] = typer.Option(None, "--attach", help="Attachments"),
):
    """Send an email."""
    acct, auth_method = _resolve_and_get_auth()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text()
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    try:
        if auth_method == "app_password":
            client = _get_smtp_client(acct)
            result = client.send_message(
                to=to,
                subject=subject,
                body=body,
                cc=cc,
                bcc=bcc,
                html=html,
                attachments=attach,
            )
        else:
            api_client = get_client()
            result = api_client.send_message(
                to=to,
                subject=subject,
                body=body,
                cc=cc,
                bcc=bcc,
                html=html,
                attachments=attach,
            )
        console.print(f"[green]Message sent.[/green] ID: {result.get('id')}")

    except HttpError as e:
        logger.error(f"Gmail API error sending: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Send error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def draft(
    to: str = typer.Option(..., "-t", "--to", help="Recipient email"),
    subject: str = typer.Option(..., "-s", "--subject", help="Email subject"),
    body: str = typer.Option(None, "-b", "--body", help="Email body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    cc: str = typer.Option(None, "--cc", help="CC recipients"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
):
    """Create a draft email."""
    acct, auth_method = _resolve_and_get_auth()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text()
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            result = client.create_draft(
                to=to,
                subject=subject,
                body=body,
                cc=cc,
                html=html,
            )
        else:
            api_client = get_client()
            result = api_client.create_draft(
                to=to,
                subject=subject,
                body=body,
                cc=cc,
                html=html,
            )
        console.print(f"[green]Draft created.[/green] ID: {result.get('id')}")

    except HttpError as e:
        logger.error(f"Gmail API error creating draft: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Draft error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def reply(
    message_id: str = typer.Argument(..., help="Message ID to reply to"),
    body: str = typer.Option(None, "-b", "--body", help="Reply body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    reply_all: bool = typer.Option(False, "--all", help="Reply to all recipients"),
    send_flag: bool = typer.Option(False, "--send", help="Send immediately instead of saving as draft"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
):
    """Create a reply to an existing email (draft or send)."""
    acct, auth_method = _resolve_and_get_auth()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text()
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            original = client.get_message_details(message_id)
            original_from = original.get("headers", {}).get("from", "unknown")
            original_subject = original.get("headers", {}).get("subject", "")

            result = client.create_reply_draft(
                message_uid=message_id,
                body=body,
                reply_all=reply_all,
                send=send_flag,
                html=html,
            )
        else:
            api_client = get_client()
            original = api_client.get_message_details(message_id)
            original_from = original.get("headers", {}).get("from", "unknown")
            original_subject = original.get("headers", {}).get("subject", "")

            result = api_client.create_reply_draft(
                message_id=message_id,
                body=body,
                reply_all=reply_all,
                send=send_flag,
                html=html,
            )

        if send_flag:
            console.print(f"[green]Reply sent.[/green]")
            console.print(f"  To: {original_from}")
            console.print(f"  Subject: Re: {original_subject}" if not original_subject.lower().startswith("re:") else f"  Subject: {original_subject}")
        else:
            console.print(f"[green]Reply draft created.[/green]")
            console.print(f"  To: {original_from}")
            console.print(f"  Subject: Re: {original_subject}" if not original_subject.lower().startswith("re:") else f"  Subject: {original_subject}")
            console.print(f"  Draft ID: {result.get('id')}")

    except HttpError as e:
        logger.error(f"Gmail API error creating reply: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Reply error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def drafts(
    count: int = typer.Option(10, "-n", "--count", help="Number of drafts to show"),
):
    """List draft emails."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            draft_list = client.list_drafts(max_results=count)
        else:
            api_client = get_client()
            draft_list = api_client.list_drafts(max_results=count)

        if not draft_list:
            console.print("[yellow]No drafts found.[/yellow]")
            return

        console.print(f"\n[cyan]Drafts ({acct})[/cyan]\n")

        for draft_item in draft_list:
            draft_id = draft_item.get("id")
            msg_id = draft_item.get("message", {}).get("id")
            if msg_id:
                if auth_method == "app_password":
                    msg = client.get_message_details(msg_id)
                else:
                    msg = api_client.get_message_details(msg_id)
                summary = format_message_summary(msg)
                console.print(f"[dim]{draft_id}[/dim]")
                console.print(f"    To: {truncate(summary.get('to', 'N/A'), 50)}")
                console.print(f"    Subject: {truncate(summary['subject'], 60)}")
                console.print()

    except HttpError as e:
        logger.error(f"Gmail API error listing drafts: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Drafts error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def search(
    query: str = typer.Argument(..., help="Gmail search query"),
    count: int = typer.Option(10, "-n", "--count", help="Number of results"),
    include_spam: bool = typer.Option(False, "--include-spam", help="Include messages from spam and trash"),
):
    """Search emails using Gmail query syntax."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            messages = client.search(
                query=query,
                max_results=count,
                include_spam_trash=include_spam,
            )
        else:
            api_client = get_client()
            messages = api_client.search(
                query=query,
                max_results=count,
                include_spam_trash=include_spam,
            )

        if not messages:
            console.print(f"[yellow]No messages matching:[/yellow] {query}")
            return

        console.print(f"\n[cyan]Search: {query} ({acct})[/cyan]\n")

        for msg_summary in messages:
            if auth_method == "app_password":
                msg = client.get_message_details(msg_summary["id"])
            else:
                msg = api_client.get_message_details(msg_summary["id"])
            summary = format_message_summary(msg)

            console.print(f"[ ] [dim]{summary['id']}[/dim]")
            console.print(f"    From: {truncate(summary['from'], 50)}")
            console.print(f"    Subject: {truncate(summary['subject'], 60)}")
            console.print(f"    Date: {summary['date'][:25] if summary['date'] else ''}")
            console.print()

        console.print(f"[dim]Found {len(messages)} message(s)[/dim]")

    except HttpError as e:
        logger.error(f"Gmail API error searching: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Search error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def count(
    query: str = typer.Argument(None, help="Gmail search query (optional)"),
    label: str = typer.Option(None, "-l", "--label", help="Label to count (e.g., INBOX)"),
):
    """Count emails matching a query (exact count, paginates all results)."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        label_ids = [label.upper()] if label else None

        if auth_method == "app_password":
            client = _get_imap_client(acct)
            result = client.count_messages(label_ids=label_ids, query=query)
        else:
            api_client = get_client()
            result = api_client.count_messages(label_ids=label_ids, query=query)
        console.print(f"{result}")

    except HttpError as e:
        logger.error(f"Gmail API error counting: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Count error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def labels() -> None:
    """List all labels/folders."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            all_labels = client.list_labels()
        else:
            api_client = get_client()
            all_labels = api_client.list_labels()

        system_labels = []
        user_labels = []

        for label in all_labels:
            if label.get("type") == "system":
                system_labels.append(label)
            else:
                user_labels.append(label)

        if system_labels:
            table = Table(title="System Labels")
            table.add_column("ID", style="cyan")
            table.add_column("Name")

            for label in sorted(system_labels, key=lambda x: x.get("name", "")):
                table.add_row(label.get("id"), label.get("name"))

            console.print(table)

        if user_labels:
            table = Table(title="User Labels")
            table.add_column("ID", style="cyan")
            table.add_column("Name")

            for label in sorted(user_labels, key=lambda x: x.get("name", "")):
                table.add_row(label.get("id"), label.get("name"))

            console.print(table)

    except HttpError as e:
        logger.error(f"Gmail API error listing labels: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Labels error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def delete(
    message_id: str = typer.Argument(..., help="Message ID to delete"),
    permanent: bool = typer.Option(False, "--permanent", help="Permanently delete (no trash)"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Delete/trash an email."""
    acct, auth_method = _resolve_and_get_auth()

    if not yes:
        action = "permanently delete" if permanent else "move to trash"
        confirm = typer.confirm(f"Are you sure you want to {action} message {message_id[:16]}?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            client.delete_message(message_id, permanent=permanent)
        else:
            api_client = get_client()
            api_client.delete_message(message_id, permanent=permanent)

        if permanent:
            console.print(f"[green]Message permanently deleted.[/green]")
        else:
            console.print(f"[green]Message moved to trash.[/green]")

    except HttpError as e:
        logger.error(f"Gmail API error deleting: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Delete error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def untrash(
    message_id: str = typer.Argument(..., help="Message ID to restore"),
):
    """Restore an email from trash."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            client.untrash_message(message_id)
        else:
            api_client = get_client()
            api_client.untrash_message(message_id)
        console.print("[green]Message restored from trash.[/green]")

    except HttpError as e:
        logger.error(f"Gmail API error untrashing: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Untrash error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def archive(
    message_ids: List[str] = typer.Argument(..., help="Message ID(s) to archive"),
):
    """Archive email(s) (remove from inbox, keep in All Mail). Accepts multiple IDs."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            if len(message_ids) == 1:
                client.archive_message(message_ids[0])
                console.print("[green]Message archived.[/green]")
            else:
                client.batch_archive_messages(message_ids)
                console.print(f"[green]{len(message_ids)} messages archived.[/green]")
        else:
            api_client = get_client()
            if len(message_ids) == 1:
                api_client.archive_message(message_ids[0])
                console.print("[green]Message archived.[/green]")
            else:
                api_client.batch_archive_messages(message_ids)
                console.print(f"[green]{len(message_ids)} messages archived.[/green]")

    except HttpError as e:
        logger.error(f"Gmail API error archiving: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Archive error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


def _show_archive_sample_imap(client: ImapClient, query: str) -> None:
    """Show sample messages that would be archived in dry-run mode (IMAP)."""
    console.print("\n[yellow]DRY RUN - No changes made.[/yellow]")
    sample = client.list_messages(query=query, max_results=5)
    if sample:
        console.print("\nSample messages that would be archived:")
        for msg_summary in sample:
            msg = client.get_message_details(msg_summary["id"])
            summary = format_message_summary(msg)
            date_str = summary['date'][:10] if summary['date'] else 'N/A'
            console.print(f"  - {date_str} | {truncate(summary['from'], 30)} | {truncate(summary['subject'], 40)}")


def _show_archive_sample(client: GmailClient, query: str) -> None:
    """Show sample messages that would be archived in dry-run mode."""
    console.print("\n[yellow]DRY RUN - No changes made.[/yellow]")
    sample = client.list_messages(query=query, max_results=5)
    if sample:
        console.print("\nSample messages that would be archived:")
        for msg_summary in sample:
            msg = client.get_message_details(msg_summary["id"])
            summary = format_message_summary(msg)
            date_str = summary['date'][:10] if summary['date'] else 'N/A'
            console.print(f"  - {date_str} | {truncate(summary['from'], 30)} | {truncate(summary['subject'], 40)}")


def _execute_archive_imap(client: ImapClient, query: str) -> int:
    """Fetch and archive all messages matching query via IMAP. Returns count archived."""
    console.print("\n[blue]Fetching message IDs...[/blue]")
    messages = client.list_all_messages(query=query)
    total = len(messages)
    console.print(f"[blue]Found {total:,} messages to archive.[/blue]")

    if total == 0:
        return 0

    console.print("[blue]Archiving...[/blue]")
    message_ids = [m["id"] for m in messages]
    return client.batch_archive_messages(message_ids)


def _execute_archive(client: GmailClient, query: str) -> int:
    """Fetch and archive all messages matching query. Returns count archived."""
    console.print("\n[blue]Fetching message IDs...[/blue]")
    messages = client.list_all_messages(query=query)
    total = len(messages)
    console.print(f"[blue]Found {total:,} messages to archive.[/blue]")

    if total == 0:
        return 0

    console.print("[blue]Archiving...[/blue]")
    message_ids = [m["id"] for m in messages]
    return client.batch_archive_messages(message_ids)


@app.command("archive-before")
def archive_before(
    date: str = typer.Argument(..., help="Archive messages before this date (YYYY-MM-DD)"),
    dry_run: bool = typer.Option(False, "--dry-run", "-n", help="Show what would be archived without doing it"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation prompt"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category (updates, promotions, social, forums)"),
):
    """Archive all inbox messages before a specified date."""
    acct, auth_method = _resolve_and_get_auth()

    # Build query (convert YYYY-MM-DD to YYYY/MM/DD for Gmail)
    date_formatted = date.replace("-", "/")
    query = f"in:inbox before:{date_formatted}"
    if category:
        query += f" category:{category.lower()}"

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            estimate = client.count_messages(query=query)

            console.print(f"\n[cyan]Account:[/cyan] {acct}")
            console.print(f"[cyan]Query:[/cyan] {query}")
            console.print(f"[cyan]Estimated matches:[/cyan] {estimate:,}")

            if estimate == 0:
                console.print("[yellow]No messages match this query.[/yellow]")
                return

            if dry_run:
                _show_archive_sample_imap(client, query)
                return

            if not yes:
                if not typer.confirm(f"\nArchive ~{estimate:,} messages before {date}?"):
                    console.print("[yellow]Cancelled.[/yellow]")
                    return

            archived = _execute_archive_imap(client, query)
        else:
            api_client = get_client()
            estimate = api_client.count_messages(query=query)

            console.print(f"\n[cyan]Account:[/cyan] {acct}")
            console.print(f"[cyan]Query:[/cyan] {query}")
            console.print(f"[cyan]Estimated matches:[/cyan] {estimate:,}")

            if estimate == 0:
                console.print("[yellow]No messages match this query.[/yellow]")
                return

            if dry_run:
                _show_archive_sample(api_client, query)
                return

            if not yes:
                if not typer.confirm(f"\nArchive ~{estimate:,} messages before {date}?"):
                    console.print("[yellow]Cancelled.[/yellow]")
                    return

            archived = _execute_archive(api_client, query)

        if archived > 0:
            console.print(f"\n[green]Done! Archived {archived:,} messages.[/green]")
        else:
            console.print("[yellow]No messages to archive.[/yellow]")

    except HttpError as e:
        logger.error(f"Gmail API error in archive-before: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Archive-before error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def profile() -> None:
    """Show authenticated user profile."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            info = client.get_profile()
        else:
            api_client = get_client()
            info = api_client.get_profile()

        table = Table(title=f"Gmail Profile ({acct})")
        table.add_column("Property", style="cyan")
        table.add_column("Value")

        table.add_row("Email", info.get("emailAddress", "Unknown"))
        table.add_row("Auth Method", auth_method)
        if "messagesTotal" in info:
            table.add_row("Messages Total", str(info.get("messagesTotal", 0)))
            table.add_row("Threads Total", str(info.get("threadsTotal", 0)))
        if "historyId" in info:
            table.add_row("History ID", info.get("historyId", "Unknown"))

        console.print(table)

    except HttpError as e:
        logger.error(f"Gmail API error getting profile: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Profile error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


def format_number(n: int) -> str:
    """Format number with thousands separator."""
    return f"{n:,}"


@app.command()
def stats(
    show_labels: bool = typer.Option(False, "-l", "--labels", help="Show user labels"),
    top: int = typer.Option(10, "-t", "--top", help="Number of top labels to show"),
):
    """Show comprehensive mailbox statistics dashboard."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        console.print(f"\n[bold cyan]Gmail Statistics Dashboard[/bold cyan] ({acct})")
        console.print("Loading stats from server...\n")

        if auth_method == "app_password":
            client = _get_imap_client(acct)
            stats_data = client.get_mailbox_stats()
        else:
            api_client = get_client()
            stats_data = api_client.get_mailbox_stats()

        profile_data = stats_data["profile"]
        system = stats_data["system_labels"]
        categories = stats_data["categories"]
        user_labels = stats_data["user_labels"]

        # Profile summary
        console.print(f"[bold]Account:[/bold] {profile_data['email']}")
        console.print(f"[bold]Total Messages:[/bold] {format_number(profile_data['messages_total'])}")
        console.print(f"[bold]Total Threads:[/bold] {format_number(profile_data['threads_total'])}")
        console.print()

        # Inbox overview table
        inbox_table = Table(title="Inbox Overview")
        inbox_table.add_column("Folder", style="cyan")
        inbox_table.add_column("Total", justify="right")
        inbox_table.add_column("Unread", justify="right", style="yellow")

        inbox_order = ["INBOX", "UNREAD", "STARRED", "IMPORTANT", "SENT", "DRAFT", "SPAM", "TRASH"]
        for label_id in inbox_order:
            if label_id in system:
                data = system[label_id]
                inbox_table.add_row(
                    label_id.title(),
                    format_number(data["total"]),
                    format_number(data["unread"]) if data["unread"] > 0 else "-",
                )

        console.print(inbox_table)
        console.print()

        # Categories table
        if categories:
            cat_table = Table(title="Categories")
            cat_table.add_column("Category", style="cyan")
            cat_table.add_column("Total", justify="right")
            cat_table.add_column("Unread", justify="right", style="yellow")

            cat_order = ["Personal", "Updates", "Promotions", "Social", "Forums"]
            for cat_name in cat_order:
                if cat_name in categories:
                    data = categories[cat_name]
                    cat_table.add_row(
                        cat_name,
                        format_number(data["total"]),
                        format_number(data["unread"]) if data["unread"] > 0 else "-",
                    )

            console.print(cat_table)
            console.print()

        # User labels (optional, sorted by unread)
        if show_labels and user_labels:
            label_table = Table(title=f"User Labels (Top {top} by unread)")
            label_table.add_column("Label", style="cyan")
            label_table.add_column("Total", justify="right")
            label_table.add_column("Unread", justify="right", style="yellow")

            for label in user_labels[:top]:
                name = label["name"]
                try:
                    name.encode('cp1252')
                except UnicodeEncodeError:
                    name = name.encode('ascii', 'replace').decode('ascii')

                label_table.add_row(
                    name[:30],
                    format_number(label["total"]),
                    format_number(label["unread"]) if label["unread"] > 0 else "-",
                )

            console.print(label_table)
            console.print()

        # Quick summary
        inbox_unread = system.get("INBOX", {}).get("unread", 0)
        total_unread = system.get("UNREAD", {}).get("total", 0)

        console.print("[bold]Quick Summary:[/bold]")
        console.print(f"  Inbox unread: {format_number(inbox_unread)}")
        console.print(f"  Total unread: {format_number(total_unread)}")

        if categories:
            updates_unread = categories.get("Updates", {}).get("unread", 0)
            promos_unread = categories.get("Promotions", {}).get("unread", 0)
            social_unread = categories.get("Social", {}).get("unread", 0)
            console.print(f"  Updates: {format_number(updates_unread)} | Promotions: {format_number(promos_unread)} | Social: {format_number(social_unread)}")

        console.print()

    except HttpError as e:
        logger.error(f"Gmail API error getting stats: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Stats error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command("label-stats")
def label_stats(
    label: str = typer.Argument(..., help="Label name or ID"),
):
    """Show detailed statistics for a specific label."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            try:
                data = client.get_label(label.upper())
            except ValueError:
                found = client.get_label_by_name(label)
                if not found:
                    console.print(f"[red]Error:[/red] Label '{label}' not found")
                    raise typer.Exit(1)
                data = client.get_label(found["id"])
        else:
            api_client = get_client()
            try:
                data = api_client.get_label(label.upper())
            except HttpError:
                found = api_client.get_label_by_name(label)
                if not found:
                    console.print(f"[red]Error:[/red] Label '{label}' not found")
                    raise typer.Exit(1)
                data = api_client.get_label(found["id"])

        table = Table(title=f"Label: {data.get('name', label)}")
        table.add_column("Metric", style="cyan")
        table.add_column("Value", justify="right")

        table.add_row("Total Messages", format_number(data.get("messagesTotal", 0)))
        table.add_row("Unread Messages", format_number(data.get("messagesUnread", 0)))
        table.add_row("Total Threads", format_number(data.get("threadsTotal", 0)))
        table.add_row("Unread Threads", format_number(data.get("threadsUnread", 0)))
        table.add_row("Type", data.get("type", "user"))
        table.add_row("ID", data.get("id", ""))

        console.print(table)

    except typer.Exit:
        raise
    except HttpError as e:
        logger.error(f"Gmail API error getting label stats: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Label stats error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command("label-create")
def label_create(
    name: str = typer.Argument(..., help="Label name to create"),
):
    """Create a new label/folder."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            existing = client.get_label_by_name(name)
            if existing:
                console.print(f"[yellow]Label '{name}' already exists.[/yellow]")
                console.print(f"Label ID: {existing.get('id')}")
                return
            label = client.create_label(name)
        else:
            api_client = get_client()
            existing = api_client.get_label_by_name(name)
            if existing:
                console.print(f"[yellow]Label '{name}' already exists.[/yellow]")
                console.print(f"Label ID: {existing.get('id')}")
                return
            label = api_client.create_label(name)

        console.print(f"[green]Label created:[/green] {label.get('name')}")
        console.print(f"Label ID: {label.get('id')}")

    except HttpError as e:
        logger.error(f"Gmail API error creating label: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Label create error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def move(
    message_id: str = typer.Argument(..., help="Message ID to move"),
    label: str = typer.Option(..., "-l", "--label", help="Target label name"),
    keep_inbox: bool = typer.Option(False, "--keep-inbox", help="Keep in inbox (just add label)"),
):
    """Move an email to a label (removes from inbox by default)."""
    acct, auth_method = _resolve_and_get_auth()

    try:
        if auth_method == "app_password":
            client = _get_imap_client(acct)
            target_label = client.get_or_create_label(label)
            label_id = target_label.get("id")

            add_labels = [label_id]
            remove_labels = [] if keep_inbox else ["INBOX"]

            client.modify_labels(message_id, add_labels=add_labels, remove_labels=remove_labels)
        else:
            api_client = get_client()
            target_label = api_client.get_or_create_label(label)
            label_id = target_label.get("id")

            add_labels = [label_id]
            remove_labels = [] if keep_inbox else ["INBOX"]

            api_client.modify_labels(message_id, add_labels=add_labels, remove_labels=remove_labels)

        if keep_inbox:
            console.print(f"[green]Label '{label}' added to message.[/green]")
        else:
            console.print(f"[green]Message moved to '{label}'.[/green]")

    except HttpError as e:
        logger.error(f"Gmail API error moving message: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error(f"Move error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Recipients Command (OAuth only)
# =============================================================================

@app.command()
def recipients(
    format: str = typer.Option("table", "--format", "-f", help="Output format: table or json"),
    count: int = typer.Option(0, "-n", "--count", help="Limit results (0 = all)"),
):
    """List all unique recipients from sent emails with send counts."""
    client = get_client()

    try:
        err_console = Console(stderr=True)
        err_console.print("[blue]Scanning sent messages for recipients (this may take a minute)...[/blue]")
        results = client.get_all_recipients()

        if not results:
            console.print("[yellow]No recipients found in sent messages.[/yellow]")
            return

        # Sort by sent_count descending
        sorted_recipients = sorted(
            results.items(),
            key=lambda x: x[1]["sent_count"],
            reverse=True,
        )

        if count > 0:
            sorted_recipients = sorted_recipients[:count]

        if format == "json":
            import json
            output = [
                {"email": email, "name": data["name"], "sent_count": data["sent_count"]}
                for email, data in sorted_recipients
            ]
            sys.stdout.buffer.write(json.dumps(output, indent=2, ensure_ascii=False).encode("utf-8"))
            sys.stdout.buffer.write(b"\n")
        else:
            acct_name = resolve_account(state.account)
            table = Table(title=f"Sent Recipients ({acct_name}) - {len(sorted_recipients)} contacts")
            table.add_column("Email", style="cyan")
            table.add_column("Name")
            table.add_column("Sent", justify="right", style="green")

            for email, data in sorted_recipients:
                table.add_row(email, data["name"] or "-", str(data["sent_count"]))

            console.print(table)

    except HttpError as e:
        acct_name = resolve_account(state.account)
        handle_api_error(e, acct_name)
        raise typer.Exit(1)
    except (ValueError, ConnectionError, OSError) as e:
        logger.error("Error fetching recipients: %s", e)
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Calendar Commands (OAuth only)
# =============================================================================

@calendar_app.command("list")
def calendar_list_cmd():
    """List all calendars."""
    acct, client = _get_calendar_client()
    try:
        calendars = client.list_calendars()
        if not calendars:
            console.print("[yellow]No calendars found.[/yellow]")
            return

        table = Table(title="Calendars")
        table.add_column("Name", style="cyan")
        table.add_column("ID")
        table.add_column("Primary")
        table.add_column("Role")

        for cal in calendars:
            primary = "[green]Yes[/green]" if cal.get("primary") else ""
            cal_id = truncate(cal["id"], 40)
            table.add_row(cal["name"], cal_id, primary, cal.get("access_role", ""))

        console.print(table)
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


@calendar_app.command("events")
def calendar_events_cmd(
    days: int = typer.Option(7, "-d", "--days", help="Days ahead to show"),
    calendar_id: str = typer.Option("primary", "-c", "--calendar", help="Calendar ID"),
):
    """View upcoming calendar events."""
    acct, client = _get_calendar_client()
    try:
        events = client.get_events(days_ahead=days, calendar_id=calendar_id)
        if not events:
            console.print(f"[yellow]No events in the next {days} days.[/yellow]")
            return

        console.print(f"\n[cyan]Upcoming Events ({acct}) - next {days} days[/cyan]\n")
        _display_event_list(events)
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


@calendar_app.command("today")
def calendar_today_cmd(
    calendar_id: str = typer.Option("primary", "-c", "--calendar", help="Calendar ID"),
):
    """View today's calendar events."""
    acct, client = _get_calendar_client()
    try:
        events = client.get_today(calendar_id=calendar_id)
        if not events:
            console.print("[yellow]No events today.[/yellow]")
            return

        console.print(f"\n[cyan]Today's Events ({acct})[/cyan]\n")
        _display_event_list(events)
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


@calendar_app.command("get")
def calendar_get_cmd(
    event_id: str = typer.Argument(..., help="Event ID to view"),
    calendar_id: str = typer.Option("primary", "-c", "--calendar", help="Calendar ID"),
):
    """View details of a calendar event."""
    acct, client = _get_calendar_client()
    try:
        event = client.get_event(event_id, calendar_id=calendar_id)

        console.print(f"\n[bold cyan]{event['summary']}[/bold cyan]")
        console.print(f"[dim]ID: {event['id']}[/dim]\n")

        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=15)
        table.add_column("Value")

        table.add_row("Start", event["start"])
        table.add_row("End", event["end"])
        table.add_row("All Day", "Yes" if event["is_all_day"] else "No")
        if event.get("location"):
            table.add_row("Location", event["location"])
        if event.get("organizer"):
            table.add_row("Organizer", event["organizer"])
        if event.get("status"):
            table.add_row("Status", event["status"])
        if event.get("hangout_link"):
            table.add_row("Google Meet", event["hangout_link"])
        if event.get("html_link"):
            table.add_row("Calendar Link", event["html_link"])

        console.print(table)

        if event.get("attendees"):
            console.print("\n[cyan]Attendees:[/cyan]")
            response_map = {
                "accepted": "[green]Y[/green]",
                "declined": "[red]N[/red]",
                "tentative": "[yellow]?[/yellow]",
            }
            for att in event["attendees"]:
                icon = response_map.get(att["response"], "[ ]")
                console.print(f"  {icon} {att['email']}")

        if event.get("description"):
            console.print("\n[cyan]Description:[/cyan]")
            console.print(event["description"])

    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


@calendar_app.command("create")
def calendar_create_cmd(
    subject: str = typer.Option(..., "-s", "--subject", help="Event subject"),
    date_str: str = typer.Option(..., "-d", "--date", help="Event date (YYYY-MM-DD)"),
    time_str: Optional[str] = typer.Option(None, "-t", "--time", help="Start time (HH:MM)"),
    duration: int = typer.Option(60, "--duration", help="Duration in minutes"),
    location: Optional[str] = typer.Option(None, "-l", "--location", help="Event location"),
    attendees: Optional[str] = typer.Option(None, "--attendees", help="Attendee emails, comma-separated"),
    body: Optional[str] = typer.Option(None, "-b", "--body", help="Event description"),
    all_day: bool = typer.Option(False, "--all-day", help="Create as all-day event"),
):
    """Create a calendar event."""
    acct, client = _get_calendar_client()

    try:
        parsed_date = datetime.strptime(date_str, "%Y-%m-%d")
    except ValueError:
        console.print("[red]Error:[/red] Invalid date format. Use YYYY-MM-DD (e.g., 2026-03-15)")
        raise typer.Exit(1)

    if not all_day and not time_str:
        console.print("[red]Error:[/red] --time is required for timed events (or use --all-day).")
        raise typer.Exit(1)

    if time_str:
        try:
            parsed_time = datetime.strptime(time_str, "%H:%M")
            start_time = parsed_date.replace(hour=parsed_time.hour, minute=parsed_time.minute)
        except ValueError:
            console.print("[red]Error:[/red] Invalid time format. Use HH:MM (e.g., 14:30)")
            raise typer.Exit(1)
    else:
        start_time = parsed_date

    attendee_list = [a.strip() for a in attendees.split(",")] if attendees else None

    try:
        result = client.create_event(
            summary=subject,
            start_time=start_time,
            duration_minutes=duration,
            location=location,
            description=body,
            attendees=attendee_list,
            all_day=all_day,
        )
        console.print(f"[green]Event created:[/green] {subject}")
        console.print(f"  ID: {result['id']}")
        if not all_day:
            console.print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M')} ({duration} min)")
        else:
            console.print(f"  Date: {date_str} (all day)")
        if location:
            console.print(f"  Location: {location}")
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


@calendar_app.command("delete")
def calendar_delete_cmd(
    event_id: str = typer.Argument(..., help="Event ID to delete"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Delete a calendar event."""
    acct, client = _get_calendar_client()

    if not yes:
        confirm = typer.confirm(f"Delete event {event_id[:16]}...?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.delete_event(event_id)
        console.print("[green]Event deleted.[/green]")
    except HttpError as e:
        handle_calendar_api_error(e, acct)
        raise typer.Exit(1)


def _display_event_list(events: list) -> None:
    """Display a list of events in vertical format (shared by events/today)."""
    for event in events:
        console.print(f"  [bold]{event['summary']}[/bold]")
        console.print(f"    ID: [dim]{truncate(event['id'], 40)}[/dim]")
        if event["is_all_day"]:
            console.print(f"    Date: {event['start']} (all day)")
        else:
            console.print(f"    Start: {event['start']}")
            console.print(f"    End: {event['end']}")
        if event.get("location"):
            console.print(f"    Location: {event['location']}")
        if event.get("hangout_link"):
            console.print(f"    [blue]Google Meet:[/blue] {event['hangout_link']}")
        if event.get("attendees"):
            att_emails = [a["email"] for a in event["attendees"][:3]]
            att_display = ", ".join(att_emails)
            if len(event["attendees"]) > 3:
                att_display += "..."
            console.print(f"    Attendees: {att_display}")
        console.print()


# =============================================================================
# Contacts Commands (OAuth only)
# =============================================================================

@contacts_app.command("list")
def contacts_list_cmd(
    count: int = typer.Option(25, "-n", "--count", help="Number of contacts to show"),
):
    """List contacts."""
    acct, client = _get_contacts_client()
    try:
        contacts = client.list_contacts(max_results=count)
        if not contacts:
            console.print("[yellow]No contacts found.[/yellow]")
            return

        table = Table(title=f"Contacts ({acct})")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Phone")
        table.add_column("Organization")
        table.add_column("Resource", style="dim")

        for c in contacts:
            table.add_row(
                c["name"],
                c["emails"][0] if c["emails"] else "",
                c["phones"][0] if c["phones"] else "",
                c["organization"],
                c["resource_name"],
            )

        console.print(table)
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)


@contacts_app.command("search")
def contacts_search_cmd(
    query: str = typer.Argument(..., help="Search query (name or email)"),
):
    """Search contacts by name or email."""
    acct, client = _get_contacts_client()
    try:
        contacts = client.search_contacts(query)
        if not contacts:
            console.print(f"[yellow]No contacts matching '{query}'.[/yellow]")
            return

        table = Table(title=f"Search Results: '{query}'")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Phone")
        table.add_column("Organization")
        table.add_column("Resource", style="dim")

        for c in contacts:
            table.add_row(
                c["name"],
                c["emails"][0] if c["emails"] else "",
                c["phones"][0] if c["phones"] else "",
                c["organization"],
                c["resource_name"],
            )

        console.print(table)
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)


@contacts_app.command("get")
def contacts_get_cmd(
    resource_name: str = typer.Argument(..., help="Contact resource name (e.g., people/c1234567890)"),
):
    """View full details of a contact."""
    acct, client = _get_contacts_client()
    try:
        contact = client.get_contact(resource_name)

        console.print(f"\n[bold cyan]{contact['name']}[/bold cyan]")
        console.print(f"[dim]{contact['resource_name']}[/dim]\n")

        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=15)
        table.add_column("Value")

        table.add_row("Given Name", contact["given_name"])
        table.add_row("Family Name", contact["family_name"])
        for email in contact["emails"]:
            table.add_row("Email", email)
        for phone in contact["phones"]:
            table.add_row("Phone", phone)
        if contact["organization"]:
            table.add_row("Organization", contact["organization"])
        if contact["title"]:
            table.add_row("Title", contact["title"])

        console.print(table)
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)


@contacts_app.command("create")
def contacts_create_cmd(
    name: str = typer.Option(..., "--name", help="Full name (e.g., 'John Doe')"),
    email: Optional[str] = typer.Option(None, "--email", help="Email address"),
    phone: Optional[str] = typer.Option(None, "--phone", help="Phone number"),
    org: Optional[str] = typer.Option(None, "--org", help="Organization"),
):
    """Create a new contact."""
    acct, client = _get_contacts_client()

    parts = name.strip().split(" ", 1)
    given_name = parts[0]
    family_name = parts[1] if len(parts) > 1 else ""

    try:
        contact = client.create_contact(
            given_name=given_name,
            family_name=family_name,
            email=email,
            phone=phone,
            organization=org,
        )
        console.print(f"[green]Contact created:[/green] {contact['name']}")
        console.print(f"  Resource: {contact['resource_name']}")
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)


@contacts_app.command("delete")
def contacts_delete_cmd(
    resource_name: str = typer.Argument(..., help="Contact resource name"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Delete a contact."""
    acct, client = _get_contacts_client()

    if not yes:
        confirm = typer.confirm(f"Delete contact {resource_name}?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.delete_contact(resource_name)
        console.print("[green]Contact deleted.[/green]")
    except HttpError as e:
        handle_contacts_api_error(e, acct)
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

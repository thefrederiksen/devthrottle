"""CLI for cc-outlook - Outlook from the command line with multi-account support."""

import json
import logging
import sys
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional, List

import typer
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

# Configure logger for this module
logger = logging.getLogger(__name__)

# Handle imports for both package mode and PyInstaller frozen mode
try:
    from . import __version__
    from .auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        list_accounts,
        set_default_account,
        get_default_account,
        delete_account,
        resolve_account,
        get_config_dir,
        get_readme_path,
        save_profile,
        get_profile,
    )
    from .outlook_api import OutlookClient
    from .utils import truncate, sanitize_text
except ImportError:
    # Running as frozen executable or direct script
    __version__ = "0.1.0"
    from auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        list_accounts,
        set_default_account,
        get_default_account,
        delete_account,
        resolve_account,
        get_config_dir,
        get_readme_path,
        save_profile,
        get_profile,
    )
    from outlook_api import OutlookClient
    from utils import truncate, sanitize_text

# Configure logging
logging.basicConfig(level=logging.INFO, format="%(message)s")

app = typer.Typer(
    name="cc-outlook",
    help="Outlook CLI: read, send, search emails and manage calendar from the command line.",
    add_completion=False,
)
accounts_app = typer.Typer(help="Manage Outlook accounts")
calendar_app = typer.Typer(help="Calendar operations")
app.add_typer(accounts_app, name="accounts")
app.add_typer(calendar_app, name="calendar")

# Configure console to handle Unicode safely on Windows.
# A non-Rich print() of a message body/subject containing characters outside the
# legacy console code page would otherwise raise UnicodeEncodeError on a cp1252
# console. Wrap stdout/stderr in a UTF-8 TextIOWrapper(errors='replace') so such
# output never crashes. Only the streams whose encoding is not already UTF-8 are
# re-wrapped: a console already on UTF-8 (or a redirected/captured stream) needs
# no fix, and re-wrapping it would needlessly take ownership of its buffer.
if sys.platform == "win32":
    import io

    for _stream_name in ("stdout", "stderr"):
        _stream = getattr(sys, _stream_name)
        _buffer = getattr(_stream, "buffer", None)
        _encoding = (getattr(_stream, "encoding", "") or "").lower()
        if _buffer is not None and _encoding not in ("utf-8", "utf8"):
            setattr(
                sys,
                _stream_name,
                io.TextIOWrapper(_buffer, encoding="utf-8", errors="replace"),
            )

console = Console()


# =============================================================================
# Global State
# =============================================================================

# Module-level state for current account selection
_current_account: Optional[str] = None


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-outlook version {__version__}")
        raise typer.Exit()


def get_client(account: Optional[str] = None) -> OutlookClient:
    """Get authenticated Outlook client for the specified or default account."""
    try:
        acct = resolve_account(account or _current_account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    profile = get_profile(acct)
    if not profile:
        console.print(f"[red]Error:[/red] Account '{acct}' not configured")
        console.print("\nRun 'cc-outlook accounts add <email> --client-id <id>' to add an account.")
        raise typer.Exit(1)

    try:
        auth_account = authenticate(acct)
        return OutlookClient(auth_account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] Configuration error: {e}")
        console.print("\nTry running: cc-outlook auth --force")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error during authentication: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        console.print("\nCheck your internet connection and try again.")
        raise typer.Exit(1)
    except RuntimeError as e:
        logger.error(f"Authentication failed: {e}")
        console.print(f"[red]Error:[/red] Authentication failed: {e}")
        console.print("\nTry running: cc-outlook auth --force")
        raise typer.Exit(1)


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
        help="Outlook account to use (default: uses default account)",
    ),
):
    """Outlook CLI: read, send, search emails and manage calendar."""
    global _current_account
    _current_account = account
    # Show help if no command provided
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


# =============================================================================
# Account Management Commands
# =============================================================================

@accounts_app.command("list")
def accounts_list(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON for machine consumption"),
) -> None:
    """List all configured Outlook accounts."""
    accts = list_accounts()

    if json_output:
        result = {
            "tool": "cc-outlook",
            "accounts": [
                {
                    "name": acct["name"],
                    "email": acct["name"],  # For cc-outlook, the account name IS the email
                    "is_default": acct["is_default"],
                    "authenticated": acct["authenticated"],
                    "can_send": acct["authenticated"],
                }
                for acct in accts
            ],
        }
        print(json.dumps(result))
        return

    if not accts:
        console.print("[yellow]No accounts configured.[/yellow]")
        console.print("\nTo add an account:")
        console.print("  cc-outlook accounts add <email> --client-id <your-azure-client-id>")
        console.print("\nSee docs/AUTHENTICATION.md for Azure setup instructions.")
        return

    table = Table(title="Outlook Accounts")
    table.add_column("Account", style="cyan")
    table.add_column("Default")
    table.add_column("Authenticated")
    table.add_column("Client ID")

    for acct in accts:
        table.add_row(
            acct["name"],
            "[green]*[/green]" if acct["is_default"] else "",
            "[green]Yes[/green]" if acct["authenticated"] else "[yellow]No[/yellow]",
            acct.get("client_id", ""),
        )

    console.print(table)


@accounts_app.command("add")
def accounts_add(
    email: str = typer.Argument(..., help="Email address for the account"),
    client_id: str = typer.Option(..., "--client-id", "-c", help="Azure App Client ID"),
    tenant_id: str = typer.Option("common", "--tenant-id", "-t", help="Azure Tenant ID (default: common)"),
    set_as_default: bool = typer.Option(False, "--default", "-d", help="Set as default account"),
):
    """Add a new Outlook account."""
    console.print(f"[cyan]Setting up account:[/cyan] {email}")
    console.print(f"[cyan]Client ID:[/cyan] {client_id[:20]}...")
    console.print()

    # Save the profile
    save_profile(email, client_id, tenant_id)
    console.print(f"[green]Profile saved for {email}[/green]")

    if set_as_default or not get_default_account():
        set_default_account(email)
        console.print(f"[green]'{email}' set as default account.[/green]")

    console.print()
    console.print("Next step: Run authentication:")
    console.print(f"  cc-outlook auth")
    console.print()
    console.print("[cyan]Device Code Flow Authentication:[/cyan]")
    console.print("  1. Run 'cc-outlook auth'")
    console.print("  2. A code will be displayed")
    console.print("  3. Go to https://microsoft.com/devicelogin")
    console.print("  4. Enter the code and sign in")
    console.print("  5. Authentication completes automatically")


@accounts_app.command("default")
def accounts_default(
    name: str = typer.Argument(..., help="Account name to set as default"),
):
    """Set the default Outlook account."""
    accts = list_accounts()
    account_names = [a["name"] for a in accts]

    if name not in account_names:
        console.print(f"[red]Error:[/red] Account '{name}' not found.")
        if account_names:
            console.print(f"Available accounts: {', '.join(account_names)}")
        else:
            console.print("No accounts configured. Run 'cc-outlook accounts add' first.")
        raise typer.Exit(1)

    set_default_account(name)
    console.print(f"[green]Default account set to '{name}'[/green]")


@accounts_app.command("remove")
def accounts_remove(
    name: str = typer.Argument(..., help="Account name to remove"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Remove an Outlook account."""
    accts = list_accounts()
    account_names = [a["name"] for a in accts]

    if name not in account_names:
        console.print(f"[red]Error:[/red] Account '{name}' not found.")
        raise typer.Exit(1)

    if not yes:
        confirm = typer.confirm(f"Remove account '{name}' and its token?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    if delete_account(name):
        console.print(f"[green]Account '{name}' removed.[/green]")
    else:
        console.print(f"[red]Failed to remove account '{name}'[/red]")
        raise typer.Exit(1)


# =============================================================================
# Authentication Commands
# =============================================================================

@app.command()
def auth(
    force: bool = typer.Option(False, "--force", "-f", help="Force re-authentication"),
    revoke: bool = typer.Option(False, "--revoke", help="Revoke current token"),
):
    """Authenticate with Outlook using Device Code Flow."""
    try:
        acct = resolve_account(_current_account)
    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)

    if revoke:
        if revoke_token(acct):
            console.print(f"[green]Token revoked for '{acct}'.[/green]")
            console.print("Run 'cc-outlook auth' to re-authenticate.")
        else:
            console.print("[yellow]No token to revoke.[/yellow]")
        return

    profile = get_profile(acct)
    if not profile:
        console.print(f"[red]Error:[/red] Account '{acct}' not configured")
        console.print("\nRun 'cc-outlook accounts add <email> --client-id <id>' first.")
        raise typer.Exit(1)

    try:
        console.print(f"[blue]Authenticating account '{acct}'...[/blue]")
        console.print()
        console.print("[cyan]Device Code Flow Authentication:[/cyan]")
        console.print("  1. A code will be displayed below")
        console.print("  2. Go to https://microsoft.com/devicelogin")
        console.print("  3. Enter the code shown")
        console.print("  4. Sign in with your Microsoft account")
        console.print("  5. Authentication completes automatically")
        console.print()

        auth_account = authenticate(acct, force=force)
        client = OutlookClient(auth_account)
        profile_info = client.get_profile()

        console.print(f"\n[green]Authenticated as:[/green] {profile_info.get('emailAddress', acct)}")
    except ValueError as e:
        logger.error(f"Configuration error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        logger.error(f"Authentication error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Email Commands
# =============================================================================

@app.command("list")
def list_emails(
    folder: str = typer.Option("inbox", "-f", "--folder", help="Folder to list (inbox, sent, drafts, deleted, junk)"),
    count: int = typer.Option(10, "-n", "--count", help="Number of emails to show"),
    unread: bool = typer.Option(False, "-u", "--unread", help="Show only unread"),
):
    """List recent emails from a folder."""
    client = get_client()

    try:
        messages = client.list_messages(folder=folder, limit=count, unread_only=unread)

        if not messages:
            console.print(f"[yellow]No messages in {folder}[/yellow]")
            return

        acct = resolve_account(_current_account)
        console.print(f"\n[cyan]Messages in {folder} ({acct})[/cyan]\n")

        for msg in messages:
            is_unread = not msg.get('is_read', True)
            style = "bold" if is_unread else ""
            marker = "[*]" if is_unread else "[ ]"

            # Build status indicators
            indicators = []
            if msg.get('flag_status') == 'flagged':
                indicators.append("[yellow]FLAG[/yellow]")
            if msg.get('has_attachments'):
                indicators.append("[blue]ATT[/blue]")
            if msg.get('importance') == 'high':
                indicators.append("[red]![/red]")
            indicator_str = ' '.join(indicators)

            console.print(f"{marker} {msg['id']} {indicator_str}", style=style)
            console.print(f"    From: {truncate(msg.get('from_name', '') or msg.get('from', ''), 50)}", style=style)
            console.print(f"    Subject: {truncate(msg.get('subject', ''), 60)}", style=style)
            console.print(f"    Date: {msg.get('date', '')[:25] if msg.get('date') else ''}", style=style)
            if msg.get('categories'):
                console.print(f"    Categories: {', '.join(msg.get('categories', []))}")
            console.print()

    except ValueError as e:
        logger.error(f"Invalid folder or parameter: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error listing emails: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def read(
    message_id: str = typer.Argument(..., help="Message ID to read"),
    raw: bool = typer.Option(False, "--raw", help="Show raw message data"),
):
    """Read a specific email."""
    client = get_client()

    try:
        msg = client.get_message(message_id)

        if not msg:
            console.print(f"[red]Error:[/red] Message not found: {message_id}")
            raise typer.Exit(1)

        # Raw mode: emit the full message dict as JSON (machine-readable), then
        # mark as read and return without the formatted view.
        if raw:
            print(json.dumps(msg, default=str, indent=2, ensure_ascii=True))
            client.mark_as_read(message_id)
            return

        # Header panel
        header_text = Text()
        header_text.append(f"From: ", style="cyan")
        header_text.append(f"{msg.get('from_name', '')} <{msg.get('from', '')}>\n")
        header_text.append(f"To: ", style="cyan")
        header_text.append(f"{', '.join(msg.get('to', []))}\n")
        if msg.get('cc'):
            header_text.append(f"CC: ", style="cyan")
            header_text.append(f"{', '.join(msg.get('cc', []))}\n")
        header_text.append(f"Date: ", style="cyan")
        header_text.append(f"{msg.get('date', '')}\n")
        header_text.append(f"Subject: ", style="cyan bold")
        header_text.append(f"{msg.get('subject', '')}\n")
        header_text.append(f"Importance: ", style="cyan")
        header_text.append(f"{msg.get('importance', 'normal')}")
        if msg.get('categories'):
            header_text.append(f"\nCategories: ", style="cyan")
            header_text.append(f"{', '.join(msg.get('categories', []))}")
        if msg.get('flag_status') and msg.get('flag_status') != 'notFlagged':
            header_text.append(f"\nFlag: ", style="cyan")
            header_text.append(f"[yellow]{msg.get('flag_status')}[/yellow]")
        if msg.get('has_attachments'):
            header_text.append(f"\n[yellow]Has attachments[/yellow]")

        console.print(Panel(header_text, title=f"Message {message_id[:16]}..."))

        # Body
        body = msg.get("body", "(No body)")
        console.print("\n" + sanitize_text(body) if body else "(No body)")

        # Mark as read
        client.mark_as_read(message_id)

    except typer.Exit:
        raise
    except ValueError as e:
        logger.error(f"Invalid message ID: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error reading message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def send(
    to: str = typer.Option(..., "-t", "--to", help="Recipient email(s), comma-separated"),
    subject: str = typer.Option(..., "-s", "--subject", help="Email subject"),
    body: str = typer.Option(None, "-b", "--body", help="Email body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    cc: str = typer.Option(None, "--cc", help="CC recipients, comma-separated"),
    bcc: str = typer.Option(None, "--bcc", help="BCC recipients, comma-separated"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
    attach: Optional[List[Path]] = typer.Option(None, "--attach", "-a", help="Attachments"),
    importance: str = typer.Option("normal", "--importance", "-i", help="Importance: low, normal, high"),
):
    """Send an email."""
    client = get_client()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text(encoding='utf-8')
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    # Parse recipients
    to_list = [addr.strip() for addr in to.split(',')]
    cc_list = [addr.strip() for addr in cc.split(',')] if cc else None
    bcc_list = [addr.strip() for addr in bcc.split(',')] if bcc else None
    attach_list = [str(p) for p in attach] if attach else None

    try:
        result = client.send_message(
            to=to_list,
            subject=subject,
            body=body,
            cc=cc_list,
            bcc=bcc_list,
            attachments=attach_list,
            importance=importance,
            html=html,
        )
        console.print(f"[green]Message sent to {', '.join(to_list)}[/green]")

    except FileNotFoundError as e:
        logger.error(f"Attachment not found: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        logger.error(f"Invalid email parameter: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error sending message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def draft(
    to: str = typer.Option(..., "-t", "--to", help="Recipient email(s), comma-separated"),
    subject: str = typer.Option(..., "-s", "--subject", help="Email subject"),
    body: str = typer.Option(None, "-b", "--body", help="Email body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    cc: str = typer.Option(None, "--cc", help="CC recipients, comma-separated"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
):
    """Create a draft email."""
    client = get_client()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text(encoding='utf-8')
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    # Parse recipients
    to_list = [addr.strip() for addr in to.split(',')]
    cc_list = [addr.strip() for addr in cc.split(',')] if cc else None

    try:
        result = client.create_draft(
            to=to_list,
            subject=subject,
            body=body,
            cc=cc_list,
            html=html,
        )
        console.print(f"[green]Draft created.[/green] ID: {result.get('id', '')[:50]}...")

    except ValueError as e:
        logger.error(f"Invalid draft parameter: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error creating draft: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def search(
    query: str = typer.Argument(..., help="Search query"),
    folder: str = typer.Option("inbox", "-f", "--folder", help="Folder to search"),
    count: int = typer.Option(10, "-n", "--count", help="Number of results"),
):
    """Search emails."""
    client = get_client()

    try:
        messages = client.search_messages(query=query, folder=folder, limit=count)

        if not messages:
            console.print(f"[yellow]No messages matching:[/yellow] {query}")
            return

        acct = resolve_account(_current_account)
        console.print(f"\n[cyan]Search: {query} ({acct})[/cyan]\n")

        for msg in messages:
            console.print(f"[ ] {msg['id']}")
            console.print(f"    From: {truncate(msg.get('from_name', '') or msg.get('from', ''), 50)}")
            console.print(f"    Subject: {truncate(msg.get('subject', ''), 60)}")
            console.print(f"    Date: {msg.get('date', '')[:25] if msg.get('date') else ''}")
            console.print()

        console.print(f"[dim]Found {len(messages)} message(s)[/dim]")

    except ValueError as e:
        logger.error(f"Invalid search parameter: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error during search: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def reply(
    message_id: str = typer.Argument(..., help="Message ID to reply to"),
    body: str = typer.Option(None, "-b", "--body", help="Reply body"),
    body_file: Path = typer.Option(None, "-f", "--file", help="Read body from file"),
    reply_all: bool = typer.Option(False, "--all", "-a", help="Reply to all recipients"),
    send_flag: bool = typer.Option(False, "--send", help="Send immediately instead of saving as draft"),
    html: bool = typer.Option(False, "--html", help="Body is HTML"),
):
    """Create a reply to an email (draft or send)."""
    client = get_client()

    # Get body content
    if body_file:
        if not body_file.exists():
            console.print(f"[red]Error:[/red] File not found: {body_file}")
            raise typer.Exit(1)
        body = body_file.read_text(encoding='utf-8')
    elif not body:
        console.print("[red]Error:[/red] Provide --body or --file")
        raise typer.Exit(1)

    try:
        result = client.reply_message(message_id, body=body, reply_all=reply_all,
                                      send=send_flag, html=html)
        action = "Reply-all" if reply_all else "Reply"
        if send_flag:
            console.print(f"[green]{action} sent.[/green]")
        else:
            console.print(f"[green]{action} draft created.[/green]")
            if result.get('id'):
                console.print(f"Draft ID: {result['id']}")

    except ValueError as e:
        logger.error(f"Reply error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def forward(
    message_id: str = typer.Argument(..., help="Message ID to forward"),
    to: str = typer.Option(..., "-t", "--to", help="Recipient email(s), comma-separated"),
    body: str = typer.Option(None, "-b", "--body", help="Additional message"),
):
    """Forward an email."""
    client = get_client()

    to_list = [addr.strip() for addr in to.split(',')]

    try:
        result = client.forward_message(message_id, to=to_list, body=body)
        console.print(f"[green]Message forwarded to {', '.join(to_list)}[/green]")

    except ValueError as e:
        logger.error(f"Forward error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def flag(
    message_id: str = typer.Argument(..., help="Message ID to flag"),
    status: str = typer.Option("flagged", "-s", "--status", help="Flag status: flagged, complete, notFlagged"),
    due: str = typer.Option(None, "-d", "--due", help="Due date (YYYY-MM-DD)"),
):
    """Flag a message for follow-up."""
    client = get_client()

    due_date = None
    if due:
        try:
            due_date = datetime.strptime(due, "%Y-%m-%d")
        except ValueError:
            console.print("[red]Error:[/red] Invalid date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)

    try:
        client.flag_message(message_id, flag_status=status, due_date=due_date)
        console.print(f"[green]Message flagged as '{status}'[/green]")

    except ValueError as e:
        logger.error(f"Flag error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def categorize(
    message_id: str = typer.Argument(..., help="Message ID"),
    categories: str = typer.Argument(..., help="Categories, comma-separated"),
):
    """Set categories on a message."""
    client = get_client()

    category_list = [c.strip() for c in categories.split(',')]

    try:
        client.set_categories(message_id, categories=category_list)
        console.print(f"[green]Categories set: {', '.join(category_list)}[/green]")

    except ValueError as e:
        logger.error(f"Categorize error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def attachments(
    message_id: str = typer.Argument(..., help="Message ID"),
):
    """List attachments on a message."""
    client = get_client()

    try:
        atts = client.list_attachments(message_id)

        if not atts:
            console.print("[yellow]No attachments[/yellow]")
            return

        table = Table(title="Attachments")
        table.add_column("Name", style="cyan")
        table.add_column("Size", justify="right")
        table.add_column("Type")
        table.add_column("ID")

        for att in atts:
            size = att.get('size', 0)
            size_str = f"{size / 1024:.1f} KB" if size > 1024 else f"{size} B"
            table.add_row(
                att.get('name', ''),
                size_str,
                att.get('content_type', ''),
                # Print the full id: download-attachment consumes it verbatim.
                att.get('id', ''),
            )

        console.print(table)

    except ValueError as e:
        logger.error(f"Attachments error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command("download-attachment")
def download_attachment(
    message_id: str = typer.Argument(..., help="Message ID"),
    attachment_id: str = typer.Argument(..., help="Attachment ID"),
    output: Path = typer.Option(None, "-o", "--output", help="Output path (default: current directory)"),
):
    """Download an attachment from a message."""
    client = get_client()

    try:
        # Get attachment info first
        atts = client.list_attachments(message_id)
        target_att = None
        for att in atts:
            if att.get('id') == attachment_id:
                target_att = att
                break

        if not target_att:
            console.print(f"[red]Error:[/red] Attachment not found: {attachment_id}")
            raise typer.Exit(1)

        # Determine output path
        if output:
            save_path = str(output)
        else:
            save_path = target_att.get('name', 'attachment')

        result = client.download_attachment(message_id, attachment_id, save_path)
        console.print(f"[green]Downloaded:[/green] {result.get('name')} -> {save_path}")

    except ValueError as e:
        logger.error(f"Download error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def delete(
    message_id: str = typer.Argument(..., help="Message ID to delete"),
    permanent: bool = typer.Option(False, "--permanent", help="Permanently delete (no trash)"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Delete/trash an email."""
    client = get_client()

    if not yes:
        action = "permanently delete" if permanent else "move to trash"
        confirm = typer.confirm(f"Are you sure you want to {action} message {message_id[:16]}...?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.delete_message(message_id, permanent=permanent)

        if permanent:
            console.print("[green]Message permanently deleted.[/green]")
        else:
            console.print("[green]Message deleted.[/green]")

    except ValueError as e:
        logger.error(f"Invalid message ID: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error deleting message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def archive(
    message_id: str = typer.Argument(..., help="Message ID to archive"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Archive an email (move to Archive folder)."""
    client = get_client()

    if not yes:
        confirm = typer.confirm(f"Archive message {message_id[:16]}...?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.move_message(message_id, "Archive")
        console.print("[green]Message archived.[/green]")
    except ValueError as e:
        logger.error(f"Archive error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error archiving message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def unarchive(
    message_id: str = typer.Argument(..., help="Message ID to move back to inbox"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Move an email from Archive back to Inbox."""
    client = get_client()

    if not yes:
        confirm = typer.confirm(f"Move message {message_id[:16]}... back to inbox?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.move_message(message_id, "Inbox")
        console.print("[green]Message moved to inbox.[/green]")
    except ValueError as e:
        logger.error(f"Unarchive error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error moving message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def move(
    message_id: str = typer.Argument(..., help="Message ID to move"),
    folder: str = typer.Argument(
        ...,
        help="Target folder by ID, full path, or path suffix (e.g. 'Customers/Nufarm' or 'Nufarm')",
    ),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Move an email to a folder (resolves nested paths like 'Customers/Nufarm')."""
    client = get_client()

    if not yes:
        confirm = typer.confirm(f"Move message {message_id[:16]}... to '{folder}'?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        dest = client.move_message_to(message_id, folder)
        console.print(f"[green]Message moved to:[/green] {dest}")
    except ValueError as e:
        logger.error(f"Move error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error moving message: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command("create-folder")
def create_folder(
    name: str = typer.Argument(..., help="Name of the new folder"),
    parent: str = typer.Option(
        None,
        "--parent",
        "-p",
        help="Parent folder by ID, path, or suffix (default: Inbox)",
    ),
):
    """Create a new mail folder (under Inbox, or under --parent)."""
    client = get_client()

    try:
        result = client.create_folder(name, parent_folder=parent)
        console.print(f"[green]Folder created:[/green] {result.get('name')}")
    except ValueError as e:
        logger.error(f"Create folder error: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error creating folder: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def folders(
    show_ids: bool = typer.Option(False, "--ids", help="Show folder IDs"),
) -> None:
    """List all mail folders."""
    client = get_client()

    try:
        folder_list = client.list_folders()

        if not folder_list:
            console.print("[yellow]No folders found[/yellow]")
            return

        table = Table(title="Mail Folders")
        table.add_column("Name", style="cyan")
        table.add_column("Total", justify="right")
        table.add_column("Unread", justify="right", style="yellow")
        if show_ids:
            table.add_column("ID", style="dim")

        for folder in folder_list:
            unread = folder.get('unread_count', 0)
            row = [
                folder.get('display_name', folder.get('name', '')),
                str(folder.get('total_count', 0)),
                str(unread) if unread > 0 else "-",
            ]
            if show_ids:
                row.append(folder.get('id', ''))
            table.add_row(*row)

        console.print(table)

    except ValueError as e:
        logger.error(f"Error listing folders: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error listing folders: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def recipients(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON for machine consumption"),
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

        if json_output:
            output = [
                {"email": email, "name": data["name"], "sent_count": data["sent_count"]}
                for email, data in sorted_recipients
            ]
            print(json.dumps(output, indent=2, ensure_ascii=True))
        else:
            acct = resolve_account(_current_account)
            table = Table(title=f"Sent Recipients ({acct}) - {len(sorted_recipients)} contacts")
            table.add_column("Email", style="cyan")
            table.add_column("Name")
            table.add_column("Sent", justify="right", style="green")

            for email, data in sorted_recipients:
                table.add_row(email, data["name"] or "-", str(data["sent_count"]))

            console.print(table)

    except ValueError as e:
        logger.error("Error fetching recipients: %s", e)
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error("Network error fetching recipients: %s", e)
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@app.command()
def profile() -> None:
    """Show authenticated user profile."""
    client = get_client()

    try:
        info = client.get_profile()
        acct = resolve_account(_current_account)
        status = get_auth_status(acct)

        table = Table(title=f"Outlook Profile ({acct})")
        table.add_column("Property", style="cyan")
        table.add_column("Value")

        table.add_row("Email", info.get("emailAddress", acct))
        table.add_row("Config Directory", status.get("account_dir", ""))
        table.add_row("Authenticated", "[green]Yes[/green]" if status.get("authenticated") else "[red]No[/red]")
        table.add_row("Default Account", "[green]Yes[/green]" if status.get("is_default") else "No")

        console.print(table)

    except ValueError as e:
        logger.error(f"Error getting profile: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error getting profile: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


# =============================================================================
# Calendar Helpers
# =============================================================================

def _print_event(event: dict) -> None:
    """Print a single calendar event in standard format."""
    console.print(f"  [bold]{event.get('subject', '(No subject)')}[/bold]")
    # Print the full id: calendar get / forward consume it verbatim.
    console.print(f"    ID: [dim]{event.get('id', '')}[/dim]")
    console.print(f"    Start: {event.get('start', '')}")
    console.print(f"    End: {event.get('end', '')}")
    if event.get('location'):
        console.print(f"    Location: {event.get('location')}")
    if event.get('organizer'):
        console.print(f"    Organizer: {event.get('organizer')}")
    if event.get('is_online_meeting'):
        join_url = event.get('join_url', '')
        if join_url:
            console.print(f"    [blue]Teams Meeting:[/blue] {join_url}")
    if event.get('attendees'):
        att_list = event['attendees']
        att_display = ', '.join([a.get('email', a) if isinstance(a, dict) else a for a in att_list[:3]])
        console.print(f"    Attendees: {att_display}{'...' if len(att_list) > 3 else ''}")
    if event.get('my_response'):
        console.print(f"    My Response: {event.get('my_response')}")
    console.print()


# =============================================================================
# Calendar Commands
# =============================================================================

@calendar_app.command("list")
def calendar_list() -> None:
    """List all calendars."""
    client = get_client()

    try:
        calendars = client.list_calendars()

        if not calendars:
            console.print("[yellow]No calendars found[/yellow]")
            return

        table = Table(title="Calendars")
        table.add_column("Name", style="cyan")
        table.add_column("ID")

        for cal in calendars:
            # Print the full id so it can be reused in follow-up commands.
            table.add_row(cal.get('name', ''), cal.get('id', ''))

        console.print(table)

    except ValueError as e:
        logger.error(f"Error listing calendars: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error listing calendars: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("events")
def calendar_events(
    days: int = typer.Option(7, "-d", "--days", help="Number of days to show"),
    start_date: str = typer.Option(None, "--start", "-s", help="Start date (YYYY-MM-DD). Default: today"),
    end_date: str = typer.Option(None, "--end", "-e", help="End date (YYYY-MM-DD). Default: start + days"),
    calendar_name: str = typer.Option(None, "-c", "--calendar", help="Specific calendar name"),
):
    """View calendar events for any date range."""
    client = get_client()

    # Parse start/end dates
    start_dt = None
    end_dt = None
    if start_date:
        try:
            start_dt = datetime.strptime(start_date, "%Y-%m-%d")
        except ValueError:
            console.print("[red]Error:[/red] Invalid start date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)
    if end_date:
        try:
            end_dt = datetime.strptime(end_date, "%Y-%m-%d").replace(hour=23, minute=59, second=59)
        except ValueError:
            console.print("[red]Error:[/red] Invalid end date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)

    try:
        events = client.get_events(
            days_ahead=days,
            calendar_name=calendar_name,
            start_date=start_dt,
            end_date=end_dt,
        )

        if not events:
            if start_dt or end_dt:
                console.print("[yellow]No events found in the specified date range[/yellow]")
            else:
                console.print(f"[yellow]No events in the next {days} days[/yellow]")
            return

        acct = resolve_account(_current_account)
        if start_dt or end_dt:
            range_start = start_date or "today"
            range_end = end_date or f"{range_start} + {days} days"
            console.print(f"\n[cyan]Events ({acct}) - {range_start} to {range_end}[/cyan]\n")
        else:
            console.print(f"\n[cyan]Upcoming Events ({acct})[/cyan]\n")

        for event in events:
            _print_event(event)

    except ValueError as e:
        logger.error(f"Error getting events: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error getting events: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("create")
def calendar_create(
    subject: str = typer.Option(..., "-s", "--subject", help="Event subject"),
    date: str = typer.Option(..., "-d", "--date", help="Event date (YYYY-MM-DD)"),
    time: str = typer.Option(..., "-t", "--time", help="Start time (HH:MM)"),
    duration: int = typer.Option(60, "--duration", help="Duration in minutes"),
    location: str = typer.Option(None, "-l", "--location", help="Event location"),
    attendees: str = typer.Option(None, "--attendees", help="Attendee emails, comma-separated"),
    body: str = typer.Option(None, "-b", "--body", help="Event description"),
    all_day: bool = typer.Option(False, "--all-day", help="Create as all-day event"),
):
    """Create a calendar event."""
    client = get_client()

    # Parse datetime
    try:
        start_time = datetime.strptime(f"{date} {time}", "%Y-%m-%d %H:%M")
    except ValueError:
        console.print("[red]Error:[/red] Invalid date/time format. Use YYYY-MM-DD for date and HH:MM for time.")
        raise typer.Exit(1)

    # Parse attendees
    attendee_list = [addr.strip() for addr in attendees.split(',')] if attendees else None

    try:
        result = client.create_event(
            subject=subject,
            start_time=start_time,
            duration_minutes=duration,
            attendees=attendee_list,
            location=location,
            body=body,
            all_day=all_day,
        )
        console.print(f"[green]Event created:[/green] {subject}")
        console.print(f"  Time: {start_time} ({duration} min)")
        if location:
            console.print(f"  Location: {location}")

    except ValueError as e:
        logger.error(f"Error creating event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error creating event: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("get")
def calendar_get(
    event_id: str = typer.Argument(..., help="Event ID to view"),
):
    """View details of a calendar event."""
    client = get_client()

    try:
        event = client.get_event(event_id)

        if not event:
            console.print(f"[red]Error:[/red] Event not found: {event_id}")
            raise typer.Exit(1)

        # Header
        console.print(f"\n[bold cyan]{event.get('subject', '(No subject)')}[/bold cyan]")
        console.print(f"[dim]ID: {event.get('id', '')}[/dim]\n")

        # Details table
        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=15)
        table.add_column("Value")

        table.add_row("Start", event.get('start', ''))
        table.add_row("End", event.get('end', ''))
        table.add_row("All Day", "Yes" if event.get('is_all_day') else "No")

        if event.get('location'):
            table.add_row("Location", event.get('location'))

        table.add_row("Organizer", event.get('organizer', ''))
        table.add_row("Importance", event.get('importance', 'normal'))
        table.add_row("Show As", event.get('show_as', 'busy'))

        if event.get('is_online_meeting'):
            table.add_row("Online Meeting", "[green]Yes[/green]")
            if event.get('join_url'):
                table.add_row("Join URL", event.get('join_url'))

        if event.get('my_response'):
            table.add_row("My Response", event.get('my_response'))

        if event.get('categories'):
            table.add_row("Categories", ', '.join(event.get('categories', [])))

        console.print(table)

        # Attendees
        if event.get('attendees'):
            console.print("\n[cyan]Attendees:[/cyan]")
            for att in event['attendees']:
                response = att.get('response', 'none')
                response_icon = {'accepted': '[green]Y[/green]', 'declined': '[red]N[/red]', 'tentative': '[yellow]?[/yellow]'}.get(response, '[ ]')
                console.print(f"  {response_icon} {att.get('email', '')} ({att.get('type', 'required')})")

        # Body
        if event.get('body'):
            console.print("\n[cyan]Description:[/cyan]")
            console.print(sanitize_text(event.get('body', '')))

    except typer.Exit:
        raise
    except ValueError as e:
        logger.error(f"Error getting event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("delete")
def calendar_delete(
    event_id: str = typer.Argument(..., help="Event ID to delete"),
    yes: bool = typer.Option(False, "-y", "--yes", help="Skip confirmation"),
):
    """Delete a calendar event."""
    client = get_client()

    if not yes:
        confirm = typer.confirm(f"Are you sure you want to delete event {event_id[:16]}...?")
        if not confirm:
            console.print("[yellow]Cancelled.[/yellow]")
            return

    try:
        client.delete_event(event_id)
        console.print("[green]Event deleted.[/green]")

    except ValueError as e:
        logger.error(f"Error deleting event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("respond")
def calendar_respond(
    event_id: str = typer.Argument(..., help="Event ID to respond to"),
    response: str = typer.Argument(..., help="Response: accept, decline, or tentative"),
    message: str = typer.Option(None, "-m", "--message", help="Optional response message"),
):
    """Respond to a calendar event invitation."""
    client = get_client()

    if response not in ['accept', 'decline', 'tentative']:
        console.print(f"[red]Error:[/red] Invalid response '{response}'. Use 'accept', 'decline', or 'tentative'.")
        raise typer.Exit(1)

    try:
        client.respond_to_event(event_id, response=response, message=message)
        console.print(f"[green]Response '{response}' sent.[/green]")

    except ValueError as e:
        logger.error(f"Error responding to event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("update")
def calendar_update(
    event_id: str = typer.Argument(..., help="Event ID to update"),
    subject: str = typer.Option(None, "-s", "--subject", help="New subject"),
    date: str = typer.Option(None, "-d", "--date", help="New date (YYYY-MM-DD)"),
    time: str = typer.Option(None, "-t", "--time", help="New start time (HH:MM)"),
    end_time: str = typer.Option(None, "--end-time", help="New end time (HH:MM)"),
    location: str = typer.Option(None, "-l", "--location", help="New location"),
    body: str = typer.Option(None, "-b", "--body", help="New description"),
):
    """Update a calendar event."""
    client = get_client()

    start_dt = None
    end_dt = None

    if date and time:
        try:
            start_dt = datetime.strptime(f"{date} {time}", "%Y-%m-%d %H:%M")
        except ValueError:
            console.print("[red]Error:[/red] Invalid date/time format. Use YYYY-MM-DD for date and HH:MM for time.")
            raise typer.Exit(1)

    if date and end_time:
        try:
            end_dt = datetime.strptime(f"{date} {end_time}", "%Y-%m-%d %H:%M")
        except ValueError:
            console.print("[red]Error:[/red] Invalid end time format. Use HH:MM.")
            raise typer.Exit(1)

    try:
        result = client.update_event(
            event_id,
            subject=subject,
            start_time=start_dt,
            end_time=end_dt,
            location=location,
            body=body,
        )
        console.print(f"[green]Event updated:[/green] {result.get('subject', '')}")

    except ValueError as e:
        logger.error(f"Error updating event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("today")
def calendar_today(
    calendar_name: str = typer.Option(None, "-c", "--calendar", help="Specific calendar name"),
):
    """Show today's agenda."""
    client = get_client()

    today = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
    tomorrow = today + timedelta(days=1)

    try:
        events = client.get_events(
            start_date=today,
            end_date=tomorrow,
            calendar_name=calendar_name,
        )

        acct = resolve_account(_current_account)
        date_str = today.strftime("%A, %B %d, %Y")
        console.print(f"\n[cyan]Today's Agenda ({acct}) - {date_str}[/cyan]\n")

        if not events:
            console.print("  [yellow]No events today[/yellow]")
            return

        for event in events:
            _print_event(event)

    except ValueError as e:
        logger.error(f"Error getting today's events: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("week")
def calendar_week(
    calendar_name: str = typer.Option(None, "-c", "--calendar", help="Specific calendar name"),
):
    """Show this week's events (Monday through Sunday)."""
    client = get_client()

    today = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
    monday = today - timedelta(days=today.weekday())
    sunday_end = monday + timedelta(days=7)

    try:
        events = client.get_events(
            start_date=monday,
            end_date=sunday_end,
            calendar_name=calendar_name,
        )

        acct = resolve_account(_current_account)
        mon_str = monday.strftime("%b %d")
        sun_str = (sunday_end - timedelta(days=1)).strftime("%b %d, %Y")
        console.print(f"\n[cyan]This Week ({acct}) - {mon_str} to {sun_str}[/cyan]\n")

        if not events:
            console.print("  [yellow]No events this week[/yellow]")
            return

        for event in events:
            _print_event(event)

    except ValueError as e:
        logger.error(f"Error getting week events: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("search")
def calendar_search(
    query: str = typer.Argument(..., help="Search text (matches subject, organizer, attendee names/emails)"),
    start_date: str = typer.Option(None, "--start", "-s", help="Start date (YYYY-MM-DD). Default: 1 year ago"),
    end_date: str = typer.Option(None, "--end", "-e", help="End date (YYYY-MM-DD). Default: today"),
    calendar_name: str = typer.Option(None, "-c", "--calendar", help="Specific calendar name"),
    limit: int = typer.Option(25, "-n", "--limit", help="Max results to show"),
):
    """Search calendar events by subject, organizer, or attendee."""
    client = get_client()

    start_dt = None
    end_dt = None
    if start_date:
        try:
            start_dt = datetime.strptime(start_date, "%Y-%m-%d")
        except ValueError:
            console.print("[red]Error:[/red] Invalid start date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)
    if end_date:
        try:
            end_dt = datetime.strptime(end_date, "%Y-%m-%d").replace(hour=23, minute=59, second=59)
        except ValueError:
            console.print("[red]Error:[/red] Invalid end date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)

    try:
        events = client.search_events(
            query=query,
            start_date=start_dt,
            end_date=end_dt,
            calendar_name=calendar_name,
            limit=limit,
        )

        acct = resolve_account(_current_account)
        console.print(f"\n[cyan]Search Results for \"{query}\" ({acct})[/cyan]\n")

        if not events:
            console.print("  [yellow]No matching events found[/yellow]")
            return

        console.print(f"  Found {len(events)} matching event(s)\n")

        for event in events:
            _print_event(event)

    except ValueError as e:
        logger.error(f"Error searching events: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("freebusy")
def calendar_freebusy(
    emails: str = typer.Argument(..., help="Email addresses, comma-separated"),
    date: str = typer.Option(None, "-d", "--date", help="Date to check (YYYY-MM-DD). Default: today"),
    start_time: str = typer.Option("08:00", "--start", "-s", help="Start time (HH:MM). Default: 08:00"),
    end_time: str = typer.Option("18:00", "--end", "-e", help="End time (HH:MM). Default: 18:00"),
):
    """Check free/busy availability for one or more people."""
    client = get_client()

    # Parse date
    check_date = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)
    if date:
        try:
            check_date = datetime.strptime(date, "%Y-%m-%d")
        except ValueError:
            console.print("[red]Error:[/red] Invalid date format. Use YYYY-MM-DD.")
            raise typer.Exit(1)

    # Parse start/end times
    try:
        start_h, start_m = map(int, start_time.split(':'))
        end_h, end_m = map(int, end_time.split(':'))
    except ValueError:
        console.print("[red]Error:[/red] Invalid time format. Use HH:MM.")
        raise typer.Exit(1)

    start_dt = check_date.replace(hour=start_h, minute=start_m)
    end_dt = check_date.replace(hour=end_h, minute=end_m)

    email_list = [e.strip() for e in emails.split(',')]

    try:
        schedules = client.get_free_busy(
            emails=email_list,
            start=start_dt,
            end=end_dt,
        )

        date_str = check_date.strftime("%A, %B %d, %Y")
        console.print(f"\n[cyan]Availability for {date_str} ({start_time} - {end_time})[/cyan]\n")

        for sched in schedules:
            console.print(f"  [bold]{sched['email']}[/bold]")

            if not sched['items']:
                console.print("    [green]Free (no events)[/green]")
            else:
                table = Table(show_header=True, box=None, padding=(0, 2))
                table.add_column("Time", style="cyan", width=25)
                table.add_column("Status", width=12)
                table.add_column("Subject")

                for item in sched['items']:
                    status = item.get('status', '')
                    status_display = {
                        'busy': '[red]Busy[/red]',
                        'tentative': '[yellow]Tentative[/yellow]',
                        'oof': '[magenta]OOF[/magenta]',
                        'free': '[green]Free[/green]',
                        'workingElsewhere': '[blue]Working Elsewhere[/blue]',
                    }.get(status, status)

                    start_str = item.get('start', '')[:16].replace('T', ' ')
                    end_str = item.get('end', '')[:16].replace('T', ' ')
                    time_range = f"{start_str} - {end_str}"

                    table.add_row(time_range, status_display, item.get('subject', ''))

                console.print(table)
            console.print()

    except ValueError as e:
        logger.error(f"Error getting free/busy: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


@calendar_app.command("forward")
def calendar_forward(
    event_id: str = typer.Argument(..., help="Event ID to forward"),
    to: str = typer.Option(..., "-t", "--to", help="Recipient email(s), comma-separated"),
    comment: str = typer.Option(None, "-m", "--message", help="Optional message to include"),
):
    """Forward a calendar event to someone."""
    client = get_client()

    to_list = [e.strip() for e in to.split(',')]

    try:
        client.forward_event(
            event_id=event_id,
            to_emails=to_list,
            comment=comment,
        )
        console.print(f"[green]Event forwarded to: {', '.join(to_list)}[/green]")

    except ValueError as e:
        logger.error(f"Error forwarding event: {e}")
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except (ConnectionError, OSError) as e:
        logger.error(f"Network error: {e}")
        console.print(f"[red]Error:[/red] Network error: {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

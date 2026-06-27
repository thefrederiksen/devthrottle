"""CLI for cc-comm-queue - Communication Manager Queue Tool."""

import atexit
import json
import logging
import re
import os
import sys
from pathlib import Path
from typing import List, Optional

# Force unbuffered stdout/stderr for PyInstaller frozen executables.
# Without this, output may be swallowed when the parent process reads via pipe.
if getattr(sys, 'frozen', False):
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(write_through=True)
        sys.stderr.reconfigure(write_through=True)
    else:
        # Python < 3.7 fallback
        sys.stdout = os.fdopen(sys.stdout.fileno(), 'w', buffering=1)
        sys.stderr = os.fdopen(sys.stderr.fileno(), 'w', buffering=1)

    def _flush_on_exit():
        try:
            sys.stdout.flush()
        except Exception:
            pass
        try:
            sys.stderr.flush()
        except Exception:
            pass
    atexit.register(_flush_on_exit)

import typer
from rich import box
from rich.console import Console
from rich.table import Table as _RichTable

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
    """Rich Table that defaults to ASCII box drawing (house ASCII-only rule).

    Callers that explicitly pass box=... (including box=None) keep their choice.
    """
    kwargs.setdefault("box", box.ASCII)
    return _RichTable(*args, **kwargs)

__version__ = "0.1.0"

# Handle imports for both package and frozen executable
try:
    from .schema import (
        ContentItem, ContentType, EmailSpecific, FacebookSpecific,
        LinkedInSpecific, Persona, Platform, RecipientInfo, RedditSpecific,
        SendTiming, Status, Visibility, WhatsAppSpecific, YouTubeSpecific,
    )
    from .queue_manager import QueueManager
    from .database import InvalidStatusTransition
except ImportError:
    # Running as frozen executable
    from schema import (
        ContentItem, ContentType, EmailSpecific, FacebookSpecific,
        LinkedInSpecific, Persona, Platform, RecipientInfo, RedditSpecific,
        SendTiming, Status, Visibility, WhatsAppSpecific, YouTubeSpecific,
    )
    from queue_manager import QueueManager
    from database import InvalidStatusTransition

# Configure logging
logging.basicConfig(level=logging.WARNING, format="%(message)s")
logger = logging.getLogger(__name__)

app = typer.Typer(
    name="cc-comm-queue",
    help="CLI tool for adding content to the Communication Manager approval queue.",
    add_completion=False,
)

config_app = typer.Typer(help="Configuration management")
app.add_typer(config_app, name="config")

_is_tty = sys.stdout.isatty()
console = Console(force_terminal=_is_tty, no_color=not _is_tty)


def get_config():
    """Get configuration using cc_shared."""
    from cc_shared.config import get_config as get_cc_config
    return get_cc_config()


def get_queue_manager() -> QueueManager:
    """Get a QueueManager instance with configured path.

    The underlying SQLite connection is closed at process exit so the shared
    communications.db file is released cleanly for the desktop app.
    """
    config = get_config()
    queue_path = config.comm_manager.get_queue_path()
    qm = QueueManager(queue_path)
    atexit.register(qm.close)
    return qm


def _reject_invalid_choice(field: str, value: str, valid: List[str], json_output: bool) -> None:
    """Print a clear ASCII error for an unknown choice and exit with code 2.

    Used so a typo in a constrained option (status, visibility, audience, privacy)
    fails loudly instead of silently defaulting or broadening the result.
    """
    valid_str = ", ".join(valid)
    if json_output:
        print(json.dumps({
            "success": False,
            "error": f"Invalid {field}: {value}. Valid values: {valid_str}",
        }))
    else:
        console.print(f"[red]ERROR:[/red] Invalid {field}: {value}")
        console.print(f"Valid values: {valid_str}")
    raise typer.Exit(2)


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-comm-queue version {__version__}")
        raise typer.Exit()


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
):
    """CLI tool for adding content to the Communication Manager approval queue."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


@app.command()
def add(
    platform: str = typer.Argument(..., help="Platform: linkedin, twitter, reddit, youtube, email, blog, facebook, whatsapp, medium"),
    content_type: str = typer.Argument(..., help="Type: post, comment, reply, message, article, email"),
    content: str = typer.Argument(..., help="The actual content text"),
    persona: str = typer.Option("personal", "--persona", "-p", help="Persona: mindzie, center_consulting, personal"),
    destination: Optional[str] = typer.Option(None, "--destination", "-d", help="Where to post (URL)"),
    context_url: Optional[str] = typer.Option(None, "--context-url", "-c", help="What we're responding to (URL)"),
    context_title: Optional[str] = typer.Option(None, "--context-title", help="Title of content we're responding to"),
    tags: Optional[str] = typer.Option(None, "--tags", "-t", help="Comma-separated tags"),
    reason: Optional[str] = typer.Option(None, "--reason", "-r", help="Why this content was written this way -- context for the reviewer"),
    notes: Optional[str] = typer.Option(None, "--notes", "-n", help="Notes for reviewer"),
    first_comment: Optional[str] = typer.Option(None, "--first-comment", "--fc", help="First comment to post immediately after the parent post (LinkedIn/IG/FB/YouTube convention -- often a CTA, link, or hashtags)"),
    created_by: Optional[str] = typer.Option(None, "--created-by", help="Agent/tool name that created this"),
    # LinkedIn-specific
    linkedin_visibility: str = typer.Option("public", "--linkedin-visibility", help="LinkedIn visibility: public, connections"),
    # Reddit-specific
    reddit_subreddit: Optional[str] = typer.Option(None, "--reddit-subreddit", help="Target subreddit (e.g., r/processimprovement)"),
    reddit_title: Optional[str] = typer.Option(None, "--reddit-title", help="Reddit post title"),
    # Email-specific
    email_to: Optional[str] = typer.Option(None, "--email-to", help="Recipient email address"),
    email_subject: Optional[str] = typer.Option(None, "--email-subject", help="Email subject line"),
    email_cc: Optional[List[str]] = typer.Option(None, "--email-cc", help="CC recipient email (can be repeated)"),
    email_bcc: Optional[List[str]] = typer.Option(None, "--email-bcc", help="BCC recipient email (can be repeated)"),
    email_reply_to: Optional[str] = typer.Option(None, "--email-reply-to", help="Original message ID for reply threading"),
    email_attach: Optional[List[str]] = typer.Option(None, "--email-attach", help="Email attachment file path (can be repeated)"),
    # Facebook-specific
    facebook_page_id: Optional[str] = typer.Option(None, "--facebook-page-id", help="Facebook page ID"),
    facebook_page_name: Optional[str] = typer.Option(None, "--facebook-page-name", help="Facebook page name"),
    facebook_audience: str = typer.Option("public", "--facebook-audience", help="Audience: public, friends, only_me"),
    # WhatsApp-specific
    whatsapp_phone: Optional[str] = typer.Option(None, "--whatsapp-phone", help="WhatsApp phone number"),
    whatsapp_contact: Optional[str] = typer.Option(None, "--whatsapp-contact", help="WhatsApp contact name"),
    # YouTube-specific
    youtube_title: Optional[str] = typer.Option(None, "--youtube-title", help="YouTube video title"),
    youtube_description: Optional[str] = typer.Option(None, "--youtube-description", help="YouTube video description"),
    youtube_tags: Optional[str] = typer.Option(None, "--youtube-tags", help="Comma-separated YouTube tags"),
    youtube_category: Optional[str] = typer.Option(None, "--youtube-category", help="YouTube category"),
    youtube_privacy: str = typer.Option("private", "--youtube-privacy", help="Privacy: private, unlisted, public"),
    youtube_video: Optional[str] = typer.Option(None, "--youtube-video", help="Path to video file"),
    youtube_thumbnail: Optional[str] = typer.Option(None, "--youtube-thumbnail", help="Path to thumbnail image"),
    # Dispatch fields
    send_timing: str = typer.Option("asap", "--send-timing", "-st", help="When to send: immediate, scheduled, asap, hold"),
    scheduled_for: Optional[str] = typer.Option(None, "--scheduled-for", help="ISO datetime for scheduled send"),
    send_from: Optional[str] = typer.Option(None, "--send-from", "-sf", help="Account: mindzie, personal, consulting"),
    # Media attachments
    media: Optional[List[str]] = typer.Option(None, "--media", "-m", help="Path to media file (can be repeated)"),
    # Recipient info (required for LinkedIn messages)
    recipient_name: Optional[str] = typer.Option(None, "--recipient-name", help="Recipient full name (required for LinkedIn messages)"),
    recipient_url: Optional[str] = typer.Option(None, "--recipient-url", help="Recipient profile URL (required for LinkedIn messages)"),
    recipient_title: Optional[str] = typer.Option(None, "--recipient-title", help="Recipient job title"),
    recipient_company: Optional[str] = typer.Option(None, "--recipient-company", help="Recipient company"),
    # Campaign
    campaign_id: Optional[str] = typer.Option(None, "--campaign-id", help="Campaign identifier for grouping related items"),
    # Output format
    json_output: bool = typer.Option(False, "--json", help="Output as JSON (for agents)"),
):
    """Add content to the pending_review queue."""
    config = get_config()
    qm = get_queue_manager()

    # Parse platform
    try:
        plat = Platform(platform.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid platform: {platform}")
            console.print("Valid platforms: linkedin, twitter, reddit, youtube, email, blog, facebook, whatsapp, medium")
        else:
            print(json.dumps({"success": False, "error": f"Invalid platform: {platform}"}))
        raise typer.Exit(1)

    # Parse content type
    try:
        ctype = ContentType(content_type.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid type: {content_type}")
            console.print("Valid types: post, comment, reply, message, article, email")
        else:
            print(json.dumps({"success": False, "error": f"Invalid type: {content_type}"}))
        raise typer.Exit(1)

    # Parse persona
    try:
        pers = Persona(persona.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid persona: {persona}")
            console.print("Valid personas: mindzie, center_consulting, personal")
        else:
            print(json.dumps({"success": False, "error": f"Invalid persona: {persona}"}))
        raise typer.Exit(1)

    # Parse tags
    tag_list = [t.strip() for t in tags.split(",")] if tags else []

    # Get created_by from config if not provided
    actual_created_by = created_by or config.comm_manager.default_created_by

    # Parse send_timing
    try:
        timing = SendTiming(send_timing.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid send_timing: {send_timing}")
            console.print("Valid options: immediate, scheduled, asap, hold")
        else:
            print(json.dumps({"success": False, "error": f"Invalid send_timing: {send_timing}"}))
        raise typer.Exit(1)

    # Validate scheduled_for if timing is scheduled
    if timing == SendTiming.SCHEDULED and not scheduled_for:
        if not json_output:
            console.print("[red]ERROR:[/red] --scheduled-for required when send_timing is 'scheduled'")
        else:
            print(json.dumps({"success": False, "error": "--scheduled-for required when send_timing is 'scheduled'"}))
        raise typer.Exit(1)

    # Require send_from for email platform
    valid_accounts = config.comm_manager.get_valid_account_names()
    if plat == Platform.EMAIL and not send_from:
        acct_list = ", ".join(valid_accounts) if valid_accounts else "(none configured)"
        if not json_output:
            console.print("[red]ERROR:[/red] --send-from is required for email.")
            console.print(f"Valid accounts: {acct_list}")
        else:
            print(json.dumps({"success": False, "error": f"--send-from is required for email. Valid accounts: {acct_list}"}))
        raise typer.Exit(1)

    # Validate send_from if provided
    if send_from and valid_accounts and send_from.lower() not in valid_accounts:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid send_from: {send_from}")
            acct_list = ", ".join(
                f"{name} ({config.comm_manager.get_account_email(name)})"
                for name in valid_accounts
            )
            console.print(f"Valid accounts: {acct_list}")
        else:
            print(json.dumps({"success": False, "error": f"Invalid send_from: {send_from}. Valid: {', '.join(valid_accounts)}"}))
        raise typer.Exit(1)

    # Build recipient info if provided
    recipient = None
    if recipient_name:
        recipient = RecipientInfo(
            name=recipient_name,
            profile_url=recipient_url,
            title=recipient_title,
            company=recipient_company,
        )

    # Build the content item (model_validator enforces recipient for LinkedIn messages)
    try:
        item = ContentItem(
            platform=plat,
            type=ctype,
            persona=pers,
            content=content,
            created_by=actual_created_by,
            destination_url=destination,
            context_url=context_url,
            context_title=context_title,
            tags=tag_list,
            reason=reason,
            notes=notes,
            campaign_id=campaign_id,
            send_timing=timing,
            scheduled_for=scheduled_for,
            send_from=send_from.lower() if send_from else None,
            recipient=recipient,
            first_comment=first_comment,
        )
    except Exception as e:
        if json_output:
            print(json.dumps({"success": False, "error": str(e)}))
        else:
            console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)

    # Add platform-specific data
    if plat == Platform.LINKEDIN:
        try:
            vis = Visibility(linkedin_visibility.lower())
        except ValueError:
            _reject_invalid_choice(
                "linkedin-visibility", linkedin_visibility,
                [v.value for v in Visibility], json_output,
            )
        item.linkedin_specific = LinkedInSpecific(visibility=vis)

    elif plat == Platform.REDDIT:
        if reddit_subreddit:
            item.reddit_specific = RedditSpecific(
                subreddit=reddit_subreddit,
                title=reddit_title,
            )

    elif plat == Platform.EMAIL:
        if email_to and email_subject:
            # Validate attachment files exist
            attachment_paths = []
            if email_attach:
                for ap in email_attach:
                    p = Path(ap)
                    if not p.exists():
                        if not json_output:
                            console.print(f"[red]ERROR:[/red] Attachment file not found: {ap}")
                        else:
                            print(json.dumps({"success": False, "error": f"Attachment file not found: {ap}"}))
                        raise typer.Exit(1)
                    attachment_paths.append(str(p.resolve()))
            item.email_specific = EmailSpecific(
                to=[email_to],
                cc=email_cc or [],
                bcc=email_bcc or [],
                subject=email_subject,
                reply_to_message_id=email_reply_to,
                attachments=attachment_paths,
            )

    elif plat == Platform.FACEBOOK:
        valid_audiences = ["public", "friends", "only_me"]
        if facebook_audience.lower() not in valid_audiences:
            _reject_invalid_choice("facebook-audience", facebook_audience, valid_audiences, json_output)
        item.facebook_specific = FacebookSpecific(
            page_id=facebook_page_id,
            page_name=facebook_page_name,
            audience=facebook_audience.lower(),
        )

    elif plat == Platform.WHATSAPP:
        item.whatsapp_specific = WhatsAppSpecific(
            phone_number=whatsapp_phone,
            contact_name=whatsapp_contact,
        )

    elif plat == Platform.YOUTUBE:
        valid_privacy = ["private", "unlisted", "public"]
        if youtube_privacy.lower() not in valid_privacy:
            _reject_invalid_choice("youtube-privacy", youtube_privacy, valid_privacy, json_output)
        youtube_privacy = youtube_privacy.lower()
        yt_tags = [t.strip() for t in youtube_tags.split(",")] if youtube_tags else []
        # Validate video file exists if provided
        if youtube_video:
            vp = Path(youtube_video)
            if not vp.exists():
                if not json_output:
                    console.print(f"[red]ERROR:[/red] Video file not found: {youtube_video}")
                else:
                    print(json.dumps({"success": False, "error": f"Video file not found: {youtube_video}"}))
                raise typer.Exit(1)
        # Validate thumbnail file exists if provided
        if youtube_thumbnail:
            tp = Path(youtube_thumbnail)
            if not tp.exists():
                if not json_output:
                    console.print(f"[red]ERROR:[/red] Thumbnail file not found: {youtube_thumbnail}")
                else:
                    print(json.dumps({"success": False, "error": f"Thumbnail file not found: {youtube_thumbnail}"}))
                raise typer.Exit(1)
        item.youtube_specific = YouTubeSpecific(
            title=youtube_title,
            description=youtube_description,
            tags=yt_tags,
            category=youtube_category,
            privacy_status=youtube_privacy,
            video_file_path=youtube_video,
            thumbnail_path=youtube_thumbnail,
        )

    # Parse media files - collect all files that need to be stored as BLOBs
    media_files = None
    if media:
        media_files = [Path(m) for m in media]
    else:
        media_files = []

    # Auto-ingest YouTube video/thumbnail as media BLOBs so we own the data
    if plat == Platform.YOUTUBE:
        if youtube_video:
            media_files.append(Path(youtube_video))
        if youtube_thumbnail:
            media_files.append(Path(youtube_thumbnail))

    # Convert empty list to None for downstream
    if not media_files:
        media_files = None

    # Validate all media files exist
    if media_files:
        for mf in media_files:
            if not mf.exists():
                if not json_output:
                    console.print(f"[red]ERROR:[/red] Media file not found: {mf}")
                else:
                    print(json.dumps({"success": False, "error": f"Media file not found: {mf}"}))
                raise typer.Exit(1)

    # Add to queue
    result = qm.add_content(item, media_files=media_files)

    if json_output:
        print(json.dumps({
            "success": result.success,
            "id": result.id,
            "file": result.file,
            "error": result.error,
        }))
    else:
        if result.success:
            console.print(f"[green]OK:[/green] Content added to queue")
            console.print(f"  ID: {result.id}")
            console.print(f"  File: {result.file}")
        else:
            console.print(f"[red]ERROR:[/red] {result.error}")
            raise typer.Exit(1)


@app.command("add-json")
def add_json(
    file: str = typer.Argument(..., help="JSON file path, or '-' for stdin"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Add content from a JSON file or stdin."""
    qm = get_queue_manager()

    try:
        if file == "-":
            data = json.load(sys.stdin)
        else:
            with open(file, "r", encoding="utf-8") as f:
                data = json.load(f)

        # Validate and create item
        item = ContentItem(**data)
        result = qm.add_content(item)

        if json_output:
            print(json.dumps({
                "success": result.success,
                "id": result.id,
                "file": result.file,
                "error": result.error,
            }))
        else:
            if result.success:
                console.print(f"[green]OK:[/green] Content added from JSON")
                console.print(f"  ID: {result.id}")
                console.print(f"  File: {result.file}")
            else:
                console.print(f"[red]ERROR:[/red] {result.error}")
                raise typer.Exit(1)

    except json.JSONDecodeError as e:
        if json_output:
            print(json.dumps({"success": False, "error": f"Invalid JSON: {e}"}))
        else:
            console.print(f"[red]ERROR:[/red] Invalid JSON: {e}")
        raise typer.Exit(1)
    except Exception as e:
        if json_output:
            print(json.dumps({"success": False, "error": str(e)}))
        else:
            console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


@app.command("list")
def list_content(
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Filter by status: pending, approved, rejected, posted, error"),
    campaign_id: Optional[str] = typer.Option(None, "--campaign-id", help="Filter by campaign identifier"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List content items in the queue."""
    qm = get_queue_manager()

    # Parse status -- reject unknown values instead of silently showing everything.
    status_filter = None
    if status:
        status_map = {
            "pending": Status.PENDING_REVIEW,
            "pending_review": Status.PENDING_REVIEW,
            "approved": Status.APPROVED,
            "rejected": Status.REJECTED,
            "posted": Status.POSTED,
            "error": Status.ERROR,
        }
        status_filter = status_map.get(status.lower())
        if status_filter is None:
            _reject_invalid_choice(
                "status", status, sorted(status_map.keys()), json_output=False,
            )

    # Pass the CLI limit through so "-n 200" can actually return up to 200 rows,
    # and compute the true total separately so the footer is accurate.
    items = qm.list_content(status=status_filter, limit=limit, campaign_id=campaign_id)
    total = qm.count_content(status=status_filter, campaign_id=campaign_id)

    if not items:
        console.print("[yellow]No content items found[/yellow]")
        return

    table = Table(title="Content Queue")
    table.add_column("ID", style="dim", width=10)
    table.add_column("Platform", style="cyan")
    table.add_column("Type")
    table.add_column("Persona")
    table.add_column("Status", style="yellow")
    table.add_column("+C", justify="center", width=3)  # first-comment indicator
    table.add_column("Content", width=40)

    for item in items[:limit]:
        content_preview = item.get("content", "")[:35]
        if len(item.get("content", "")) > 35:
            content_preview += "..."

        status_style = {
            "pending_review": "[yellow]",
            "approved": "[green]",
            "rejected": "[red]",
            "posted": "[dim]",
        }.get(item.get("status", ""), "")
        status_end = status_style.replace("[", "[/") if status_style else ""

        # First-comment indicator: blank if no comment, "[+]" if pending,
        # "[*]" if posted. ASCII only -- no unicode glyphs.
        if item.get("first_comment"):
            if item.get("first_comment_posted_at"):
                fc_indicator = "[dim][*][/dim]"
            else:
                fc_indicator = "[yellow][+][/yellow]"
        else:
            fc_indicator = ""

        table.add_row(
            item.get("id", "")[:8],
            item.get("platform", "-"),
            item.get("type", "-"),
            item.get("persona", "-"),
            f"{status_style}{item.get('status', '-')}{status_end}",
            fc_indicator,
            content_preview,
        )

    console.print(table)
    console.print(f"\n[dim]Showing {len(items)} of {total} items[/dim]")


@app.command("status")
def status_cmd():
    """Show queue status and counts."""
    qm = get_queue_manager()
    stats = qm.get_stats()

    table = Table(title="Queue Status")
    table.add_column("Status", style="cyan")
    table.add_column("Count", justify="right")

    table.add_row("[yellow]Pending Review[/yellow]", str(stats.pending_review))
    table.add_row("[green]Approved[/green]", str(stats.approved))
    table.add_row("[red]Rejected[/red]", str(stats.rejected))
    table.add_row("[dim]Posted[/dim]", str(stats.posted))
    table.add_row("[red]Error[/red]", str(stats.error))
    table.add_row("", "")
    table.add_row("[bold]Total[/bold]", str(stats.pending_review + stats.approved + stats.rejected + stats.posted + stats.error))

    console.print(table)
    console.print(f"\n[dim]Queue path: {qm.queue_path}[/dim]")


@app.command("show")
def show_content(
    content_id: str = typer.Argument(..., help="Content ID (can be partial)"),
    json_output: bool = typer.Option(False, "--json", help="Output full record as JSON"),
):
    """Show details of a specific content item."""
    qm = get_queue_manager()

    # Support ticket number lookup
    item = None
    if content_id.isdigit():
        item = qm.get_content_by_ticket(int(content_id))
    if not item:
        item = qm.get_content_by_id(content_id)

    if not item:
        if json_output:
            print(json.dumps({"error": f"Content not found: {content_id}"}))
        else:
            console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    # JSON output mode -- full record for automation
    if json_output:
        # Remove internal fields
        output = {k: v for k, v in item.items() if not k.startswith("_")}
        print(json.dumps(output, indent=2, default=str))
        return

    # Header
    console.print(f"\n[bold cyan]{item.get('platform', '')} {item.get('type', '')}[/bold cyan]")
    console.print(f"[dim]ID: {item.get('id', '')}[/dim]\n")

    # Details table
    table = Table(show_header=False, box=None)
    table.add_column("Property", style="cyan", width=15)
    table.add_column("Value")

    table.add_row("Status", item.get("status", "-"))
    table.add_row("Persona", f"{item.get('persona', '-')} ({item.get('persona_display', '-')})")
    table.add_row("Created By", item.get("created_by", "-"))
    table.add_row("Created At", item.get("created_at", "-"))

    # Recipient info
    recipient = item.get("recipient")
    if recipient and isinstance(recipient, dict):
        table.add_row("Recipient", recipient.get("name", "-"))
        if recipient.get("profile_url"):
            table.add_row("Profile URL", recipient["profile_url"])
        if recipient.get("title"):
            table.add_row("Title", recipient["title"])
        if recipient.get("company"):
            table.add_row("Company", recipient["company"])

    if item.get("destination_url"):
        table.add_row("Destination", item["destination_url"])
    if item.get("context_url"):
        table.add_row("Context URL", item["context_url"])
    if item.get("tags"):
        table.add_row("Tags", ", ".join(item["tags"]))
    if item.get("reason"):
        table.add_row("Reason", item["reason"])
    if item.get("notes"):
        table.add_row("Notes", item["notes"])

    console.print(table)

    # Content
    console.print(f"\n[cyan]Content:[/cyan]\n{item.get('content', '')}")

    # First comment (rendered as its own labeled block so reviewers don't
    # miss that the post ships with a follow-up comment)
    if item.get("first_comment"):
        if item.get("first_comment_posted_at"):
            posted_marker = f" [dim](posted {item['first_comment_posted_at']})[/dim]"
        else:
            posted_marker = " [yellow](pending)[/yellow]"
        console.print(f"\n[cyan]First comment:[/cyan]{posted_marker}\n{item['first_comment']}")
        if item.get("first_comment_url"):
            console.print(f"[dim]Comment URL: {item['first_comment_url']}[/dim]")

    # File path
    if item.get("_file_path"):
        console.print(f"\n[dim]File: {item['_file_path']}[/dim]")


@app.command("delete")
def delete_content(
    content_id: str = typer.Argument(..., help="Ticket number or content ID (can be partial)"),
    force: bool = typer.Option(False, "--force", "-f", help="Skip confirmation prompt"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON (for agents)"),
):
    """Delete a content item from the queue."""
    qm = get_queue_manager()

    # Look up by ticket number if numeric, else by ID
    item = None
    ticket_number = None
    if content_id.isdigit():
        ticket_number = int(content_id)
        item = qm.get_content_by_ticket(ticket_number)
    else:
        item = qm.get_content_by_id(content_id)
        if item:
            ticket_number = item.get("ticket_number")

    if not item:
        if json_output:
            print(json.dumps({"success": False, "error": f"Content not found: {content_id}"}))
        else:
            console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    if ticket_number is None:
        if json_output:
            print(json.dumps({"success": False, "error": "Item has no ticket number"}))
        else:
            console.print("[red]ERROR:[/red] Item has no ticket number, cannot delete")
        raise typer.Exit(1)

    # Show summary
    platform = item.get("platform", "?")
    status = item.get("status", "?")
    content_preview = (item.get("content", "") or "")[:60]
    if len(item.get("content", "") or "") > 60:
        content_preview += "..."

    if not force and not json_output:
        console.print(f"  Ticket: #{ticket_number}")
        console.print(f"  Platform: {platform}")
        console.print(f"  Status: {status}")
        console.print(f"  Content: {content_preview}")
        if not typer.confirm("\nDelete this item?"):
            console.print("[yellow]Cancelled[/yellow]")
            raise typer.Exit(0)

    deleted = qm.delete_content(ticket_number)

    if json_output:
        print(json.dumps({
            "success": deleted,
            "ticket_number": ticket_number,
            "error": None if deleted else "Delete failed",
        }))
    else:
        if deleted:
            console.print(f"[green]OK:[/green] Deleted ticket #{ticket_number}")
        else:
            console.print(f"[red]ERROR:[/red] Failed to delete ticket #{ticket_number}")
            raise typer.Exit(1)


@app.command("mark-posted")
def mark_posted_cmd(
    content_id: str = typer.Argument(..., help="Ticket number or content ID (can be partial)"),
    posted_by: str = typer.Option("cc_director", "--by", help="Who posted the content"),
    force: bool = typer.Option(False, "--force", "-f", help="Bypass the approval workflow (mark a non-approved item posted)"),
):
    """Mark a content item as posted (sent).

    Only items in 'approved' status can be marked posted -- the approval queue
    exists so a human reviews every item first. Pass --force to override.
    """
    qm = get_queue_manager()

    item = None
    ticket_number = None
    if content_id.isdigit():
        ticket_number = int(content_id)
        item = qm.get_content_by_ticket(ticket_number)
    if not item:
        item = qm.get_content_by_id(content_id)
        if item:
            ticket_number = item.get("ticket_number")

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    if ticket_number is None:
        console.print("[red]ERROR:[/red] Item has no ticket number")
        raise typer.Exit(1)

    try:
        success = qm.mark_posted(ticket_number, posted_by=posted_by, force=force)
    except InvalidStatusTransition as e:
        console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)
    if success:
        console.print(f"[green]OK:[/green] Marked ticket #{ticket_number} as posted")
        # Auto-log to vault
        vault_id = qm.log_to_vault(ticket_number)
        if vault_id is not None:
            console.print(f"[green]OK:[/green] Logged to vault (interaction #{vault_id})")
        else:
            console.print("[yellow]NOTE:[/yellow] Could not log to vault (no matching contact or cc-vault unavailable)")
    else:
        console.print(f"[red]ERROR:[/red] Failed to mark ticket #{ticket_number} as posted")
        raise typer.Exit(1)


@app.command("mark-comment-posted")
def mark_comment_posted_cmd(
    content_id: str = typer.Argument(..., help="Ticket number or content ID (can be partial)"),
    url: Optional[str] = typer.Option(None, "--url", help="Permalink to the comment (if available)"),
    text: Optional[str] = typer.Option(None, "--text", help="Update first_comment text (only if it changed at post time)"),
):
    """Mark the first_comment as posted under its parent post.

    This does NOT change the parent post's status -- use mark-posted for
    the parent. This only records that the follow-up comment landed.
    """
    qm = get_queue_manager()

    item = None
    ticket_number = None
    if content_id.isdigit():
        ticket_number = int(content_id)
        item = qm.get_content_by_ticket(ticket_number)
    if not item:
        item = qm.get_content_by_id(content_id)
        if item:
            ticket_number = item.get("ticket_number")

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)
    if ticket_number is None:
        console.print("[red]ERROR:[/red] Item has no ticket number")
        raise typer.Exit(1)
    if not item.get("first_comment") and not text:
        console.print(f"[red]ERROR:[/red] Item #{ticket_number} has no first_comment to mark posted. Pass --text to set it now.")
        raise typer.Exit(1)

    success = qm.mark_first_comment_posted(ticket_number, comment_url=url, comment_text=text)
    if success:
        console.print(f"[green]OK:[/green] Marked first comment for ticket #{ticket_number} as posted")
        if url:
            console.print(f"      URL: {url}")
    else:
        console.print(f"[red]ERROR:[/red] Failed to update first_comment state for ticket #{ticket_number}")
        raise typer.Exit(1)


@app.command("mark-error")
def mark_error_cmd(
    content_id: str = typer.Argument(..., help="Ticket number or content ID (can be partial)"),
    reason: str = typer.Option(..., "--reason", "-r", help="Why the send failed"),
    error_by: str = typer.Option("cc_director", "--by", help="Who detected the error"),
):
    """Mark a content item as error (send failed)."""
    qm = get_queue_manager()

    item = None
    ticket_number = None
    if content_id.isdigit():
        ticket_number = int(content_id)
        item = qm.get_content_by_ticket(ticket_number)
    else:
        item = qm.get_content_by_id(content_id)
        if item:
            ticket_number = item.get("ticket_number")

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    if ticket_number is None:
        console.print("[red]ERROR:[/red] Item has no ticket number")
        raise typer.Exit(1)

    success = qm.mark_error(ticket_number, error_reason=reason, error_by=error_by)
    if success:
        console.print(f"[green]OK:[/green] Marked ticket #{ticket_number} as error: {reason}")
    else:
        console.print(f"[red]ERROR:[/red] Failed to mark ticket #{ticket_number} as error")
        raise typer.Exit(1)


@app.command("log-to-vault")
def log_to_vault_cmd(
    content_id: str = typer.Argument(..., help="Ticket number or content ID"),
):
    """Log a posted communication to the vault as an interaction.

    Resolves the recipient to a vault contact and creates an interaction record.
    This is called automatically by mark-posted, but can also be run manually.

    Usage:
      cc-comm-queue log-to-vault 42
    """
    qm = get_queue_manager()

    item = None
    ticket_number = None
    if content_id.isdigit():
        ticket_number = int(content_id)
        item = qm.get_content_by_ticket(ticket_number)
    else:
        item = qm.get_content_by_id(content_id)
        if item:
            ticket_number = item.get("ticket_number")

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    if ticket_number is None:
        console.print("[red]ERROR:[/red] Item has no ticket number")
        raise typer.Exit(1)

    vault_id = qm.log_to_vault(ticket_number)
    if vault_id is not None:
        console.print(f"[green]OK:[/green] Logged ticket #{ticket_number} to vault (interaction #{vault_id})")
    else:
        console.print(f"[yellow]NOTE:[/yellow] Could not log ticket #{ticket_number} to vault (no matching contact or cc-vault unavailable)")


@app.command("backfill-recipients")
def backfill_recipients_cmd():
    """Backfill null recipient fields from destination_url and notes.

    For LinkedIn messages where recipient is null but destination_url has
    a profile URL, parses the URL and notes to populate the recipient field.
    """
    qm = get_queue_manager()
    result = qm.backfill_recipients()

    console.print(f"[green]OK:[/green] Backfill complete")
    console.print(f"  Updated: {result['updated']}")
    console.print(f"  Skipped: {result['skipped']}")


@app.command("send")
def send_cmd(
    content_id: str = typer.Argument(..., help="Ticket number or content ID"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would be sent without sending"),
):
    """Send a LinkedIn message from the queue via cc-browser.

    Opens the LinkedIn browser, navigates to the recipient's profile,
    clicks Message, pastes content, and sends. Marks as posted on success.

    Only works for LinkedIn messages. The item must be in 'approved' status.
    """
    import subprocess
    import time
    import random

    qm = get_queue_manager()

    # Resolve item
    item = None
    if content_id.isdigit():
        item = qm.get_content_by_ticket(int(content_id))
    if not item:
        item = qm.get_content_by_id(content_id)

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    # Validate
    if item.get('platform') != 'linkedin':
        console.print(f"[red]ERROR:[/red] Only LinkedIn messages are supported. This item is: {item.get('platform')}")
        raise typer.Exit(1)

    if item.get('type') != 'message':
        console.print(f"[red]ERROR:[/red] Only messages are supported. This item is: {item.get('type')}")
        raise typer.Exit(1)

    if item.get('status') != 'approved':
        console.print(f"[red]ERROR:[/red] Item must be approved. Current status: {item.get('status')}")
        raise typer.Exit(1)

    ticket_number = item.get('ticket_number')
    content = item.get('content', '')
    destination_url = item.get('destination_url', '')

    # Get profile URL
    profile_url = None
    recipient = item.get('recipient')
    if recipient and isinstance(recipient, dict):
        profile_url = recipient.get('profile_url')
    if not profile_url:
        profile_url = destination_url

    if not profile_url or '/in/' not in profile_url:
        console.print(f"[red]ERROR:[/red] No LinkedIn profile URL found for this item")
        raise typer.Exit(1)

    # Ensure full URL
    if not profile_url.startswith('http'):
        profile_url = f"https://{profile_url}"
    if 'linkedin.com' not in profile_url:
        console.print(f"[red]ERROR:[/red] Not a LinkedIn URL: {profile_url}")
        raise typer.Exit(1)

    # Extract first name from content (first line is "{FirstName},")
    first_name = content.split('\n')[0].strip().rstrip(',').strip()
    if not first_name or len(first_name) > 30:
        console.print(f"[red]ERROR:[/red] Could not extract first name from message content")
        raise typer.Exit(1)

    # Show plan
    console.print(f"\n[cyan]Sending LinkedIn message[/cyan]")
    console.print(f"  Ticket: #{ticket_number}")
    console.print(f"  To: {first_name} ({profile_url})")
    console.print(f"  Content: {content[:60]}...")

    if dry_run:
        console.print(f"\n[yellow]DRY RUN:[/yellow] Would send the above message. Use without --dry-run to send.")
        return

    # Resolve cc-browser path (frozen exes may not inherit PATH)
    # IMPORTANT: On Windows, .cmd wrappers use cmd.exe which truncates
    # arguments at newlines. We must call node + cli.mjs directly to
    # pass multi-line content (e.g., paste --text with message body).
    import shutil
    cc_browser_bin = os.path.join(
        os.environ.get("LOCALAPPDATA", ""), "cc-director", "bin"
    )
    cc_browser_cli = os.path.join(cc_browser_bin, "_cc-browser", "src", "cli.mjs")
    node_path = shutil.which("node")

    if not node_path or not os.path.exists(cc_browser_cli):
        # Fallback: try the .cmd wrapper (won't work for multi-line paste)
        cc_browser_path = shutil.which("cc-browser") or shutil.which("cc-browser.cmd")
        if not cc_browser_path:
            cc_browser_path = os.path.join(cc_browser_bin, "cc-browser.cmd")
        if not os.path.exists(cc_browser_path):
            console.print("[red]ERROR:[/red] cc-browser not found on PATH or in default location")
            raise typer.Exit(1)
        use_node_direct = False
    else:
        use_node_direct = True

    def run_browser(args, check=True):
        """Run a cc-browser command and return stdout."""
        if use_node_direct:
            cmd = [node_path, cc_browser_cli, "-c", "linkedin"] + args
        else:
            cmd = [cc_browser_path, "-c", "linkedin"] + args
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30, encoding='utf-8', errors='replace')
        if check and result.returncode != 0:
            err = (result.stderr or '').strip() or (result.stdout or '').strip()
            raise RuntimeError(f"cc-browser failed: {err}")
        return (result.stdout or '').strip()

    def run_browser_raw(args):
        """Run a cc-browser command without -c linkedin."""
        if use_node_direct:
            cmd = [node_path, cc_browser_cli] + args
        else:
            cmd = [cc_browser_path] + args
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30, encoding='utf-8', errors='replace')
        return (result.stdout or '').strip()

    def jitter(base_seconds):
        """Sleep with random jitter."""
        time.sleep(base_seconds + random.uniform(0, base_seconds * 0.5))

    try:
        # Step 1: Check if browser is connected
        console.print("  [dim]Checking browser connection...[/dim]")
        status_out = run_browser_raw(["connections", "status"])
        if "linkedin: CONNECTED" not in status_out:
            console.print("  [dim]Opening LinkedIn browser...[/dim]")
            run_browser_raw(["connections", "open", "linkedin"])
            jitter(6)
            status_out = run_browser_raw(["connections", "status"])
            if "linkedin: CONNECTED" not in status_out:
                # Retry once
                run_browser_raw(["connections", "close", "linkedin"])
                jitter(3)
                run_browser_raw(["connections", "open", "linkedin"])
                jitter(10)
                status_out = run_browser_raw(["connections", "status"])
                if "linkedin: CONNECTED" not in status_out:
                    console.print("[red]ERROR:[/red] Could not connect to LinkedIn browser")
                    raise typer.Exit(1)

        # Step 2: Navigate to profile
        console.print(f"  [dim]Navigating to profile...[/dim]")
        run_browser(["navigate", profile_url])
        jitter(5)

        # Step 3: Check for 404 or auth issues
        snapshot = run_browser(["snapshot", "--interactive"])
        if "page not found" in snapshot.lower() or "/404" in snapshot.lower():
            console.print(f"[red]ERROR:[/red] Profile not found: {profile_url}")
            raise typer.Exit(1)
        if "authwall" in snapshot.lower() or "login" in snapshot.lower():
            console.print(f"[red]ERROR:[/red] Session expired -- login required")
            raise typer.Exit(1)

        # Step 4: Check connection status and find Message button
        # If "Invite ... to connect" is present, this person is NOT a 1st-degree
        # connection. The Message button would open InMail, not regular chat.
        invite_match = re.search(r'button "Invite ' + re.escape(first_name) + r'[^"]*to connect"', snapshot)
        if invite_match:
            console.print(f"[red]ERROR:[/red] {first_name} is not a 1st-degree connection. Cannot send regular message.")
            raise typer.Exit(1)

        # LinkedIn may show "Message Allan" or "Message Allan J." -- match prefix
        msg_match = re.search(r'button "(Message ' + re.escape(first_name) + r'[^"]*)" \[ref=(e\d+)\]', snapshot)
        if not msg_match:
            console.print(f"[red]ERROR:[/red] No 'Message {first_name}...' button found. Not connected to this person?")
            raise typer.Exit(1)

        message_btn_text = msg_match.group(1)
        msg_ref = msg_match.group(2)
        console.print(f"  [dim]Clicking {message_btn_text}...[/dim]")
        run_browser(["click", "--ref", msg_ref])
        jitter(3)

        # Step 5: Close any stale overlays, find the right textbox
        textbox_match = None
        for attempt in range(3):
            snapshot = run_browser(["snapshot", "--interactive"])

            # Close other overlays if present
            for close_match in re.finditer(r'text "Close your conversation with (?!.*' + re.escape(first_name) + r').*?"', snapshot):
                close_text = close_match.group(0).replace('text "', '').rstrip('"')
                run_browser(["click", "--text", close_text], check=False)
                jitter(1)
                snapshot = run_browser(["snapshot", "--interactive"])

            # Find textbox
            textbox_match = re.search(r'textbox "Write a message.*?" \[ref=(e\d+)\]', snapshot)
            if textbox_match:
                break
            if attempt < 2:
                console.print(f"  [dim]Waiting for chat overlay (attempt {attempt + 2}/3)...[/dim]")
                jitter(3)

        if not textbox_match:
            console.print(f"[red]ERROR:[/red] No message textbox found after 3 attempts")
            raise typer.Exit(1)

        # Step 6: Focus and paste
        console.print(f"  [dim]Pasting message...[/dim]")
        textbox_ref = textbox_match.group(1)
        run_browser(["click", "--ref", textbox_ref])
        jitter(1)

        paste_result = run_browser(["paste", "--selector", "div.msg-form__contenteditable", "--text", content])
        if '"pasted": false' in paste_result or '"pasted":false' in paste_result:
            console.print("[red]ERROR:[/red] Paste failed")
            raise typer.Exit(1)

        # Step 7: Find Send button and click
        snapshot = run_browser(["snapshot", "--interactive"])
        send_match = re.search(r'button "Send" \[ref=(e\d+)\]', snapshot)
        if not send_match:
            console.print("[red]ERROR:[/red] Send button not found")
            raise typer.Exit(1)

        send_ref = send_match.group(1)
        console.print(f"  [dim]Sending...[/dim]")
        run_browser(["click", "--ref", send_ref])
        jitter(3)

        # Step 8: Verify sent
        snapshot = run_browser(["snapshot", "--interactive"])
        if "sent the following message" in snapshot.lower() or "Write a message" in snapshot:
            console.print(f"[green]OK:[/green] Message sent to {first_name}")
        else:
            console.print(f"[yellow]WARNING:[/yellow] Could not verify message was sent. Check manually.")

        # Step 9: Close overlay
        close_match = re.search(r'text "(Close your conversation with .*?)"', snapshot)
        if close_match:
            run_browser(["click", "--text", close_match.group(1)], check=False)
            jitter(1)

        # Step 10: Mark as posted
        success = qm.mark_posted(ticket_number, posted_by="cc-comm-queue-send")
        if success:
            console.print(f"[green]OK:[/green] Marked ticket #{ticket_number} as posted")
        vault_id = qm.log_to_vault(ticket_number)
        if vault_id is not None:
            console.print(f"[green]OK:[/green] Logged to vault (interaction #{vault_id})")

        # Step 11: Close browser
        console.print("  [dim]Closing browser...[/dim]")
        run_browser_raw(["connections", "close", "linkedin"])

    except subprocess.TimeoutExpired:
        console.print("[red]ERROR:[/red] Browser command timed out")
        try:
            run_browser_raw(["connections", "close", "linkedin"])
        except Exception:
            pass
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]ERROR:[/red] {e}")
        try:
            run_browser_raw(["connections", "close", "linkedin"])
        except Exception:
            pass
        raise typer.Exit(1)
    except typer.Exit:
        try:
            run_browser_raw(["connections", "close", "linkedin"])
        except Exception:
            pass
        raise


# =============================================================================
# Config Commands
# =============================================================================

@config_app.command("show")
def config_show():
    """Show current configuration."""
    config = get_config()

    table = Table(title="Communication Manager Configuration")
    table.add_column("Setting", style="cyan")
    table.add_column("Value")

    table.add_row("Queue Path", config.comm_manager.queue_path)
    table.add_row("Default Persona", config.comm_manager.default_persona)
    table.add_row("Default Created By", config.comm_manager.default_created_by)

    console.print(table)

    # Show send-from accounts
    accounts = config.comm_manager.send_from_accounts
    if accounts:
        acct_table = Table(title="\nSend-From Accounts")
        acct_table.add_column("Name", style="cyan")
        acct_table.add_column("Email")
        acct_table.add_column("Tool")
        acct_table.add_column("Tool Account")

        for name, acct in accounts.items():
            acct_table.add_row(name, acct.email, acct.tool, acct.tool_account or "-")

        console.print(acct_table)
    else:
        console.print("\n[yellow]No send-from accounts configured[/yellow]")
        console.print("[dim]Add them to config.json under comm_manager.send_from_accounts[/dim]")


@config_app.command("set")
def config_set(
    key: str = typer.Argument(..., help="Config key: queue_path, default_persona, default_created_by"),
    value: str = typer.Argument(..., help="Config value"),
):
    """Set a configuration value."""
    valid_keys = ["queue_path", "default_persona", "default_created_by"]
    if key not in valid_keys:
        console.print(f"[red]ERROR:[/red] Unknown config key: {key}")
        console.print(f"Valid keys: {', '.join(valid_keys)}")
        raise typer.Exit(1)

    # Write through the SAME shared config store that `config show` and queue
    # operations read (cc-director's config.json). CCDirectorConfig.save()
    # deep-merges over the on-disk file, so unknown keys owned by the desktop
    # app or other tools are preserved.
    from cc_shared.config import CCDirectorConfig, reload_config

    cfg = CCDirectorConfig().load()
    setattr(cfg.comm_manager, key, value)
    cfg.save()
    # Drop the process-wide cached config so a later get_config() in the same
    # process sees the new value.
    reload_config()

    console.print(f"[green]OK:[/green] Set {key} = {value}")


@app.command("migrate")
def migrate_json(
    delete: bool = typer.Option(False, "--delete", help="Delete JSON files after successful migration"),
):
    """Migrate existing JSON files to SQLite database."""
    config = get_config()
    queue_path = Path(config.comm_manager.queue_path)

    console.print(f"[cyan]Migrating JSON files from:[/cyan] {queue_path}")

    try:
        from .migrate import migrate_json_to_sqlite
    except ImportError:
        from migrate import migrate_json_to_sqlite

    stats = migrate_json_to_sqlite(queue_path, backup=True, delete_json=delete)

    if stats["total_migrated"] > 0:
        console.print(f"\n[green]OK:[/green] Migrated {stats['total_migrated']} items to SQLite database")
    else:
        console.print("\n[yellow]No items to migrate[/yellow]")

    if stats["total_errors"] > 0:
        console.print(f"[red]Errors:[/red] {stats['total_errors']} items failed to migrate")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

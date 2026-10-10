"""CLI for cc-youtube - YouTube from the command line.

Upload videos, manage comments, and list channel content via YouTube Data API v3.
"""

import json
import logging
import sys
from pathlib import Path
from typing import Optional

# Suppress Google's file_cache warning before importing googleapiclient
logging.getLogger("googleapiclient.discovery_cache").setLevel(logging.ERROR)

import typer
from googleapiclient.errors import HttpError
from rich.console import Console
from rich.table import Table
from rich.panel import Panel
from rich.text import Text

logger = logging.getLogger(__name__)

try:
    from . import __version__
    from .auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        credentials_exist,
        get_credentials_path,
        get_config_dir,
        get_token_path,
    )
    from .youtube_api import YouTubeAPI, extract_video_id
except ImportError:
    from src import __version__
    from src.auth import (
        authenticate,
        get_auth_status,
        revoke_token,
        credentials_exist,
        get_credentials_path,
        get_config_dir,
        get_token_path,
    )
    from src.youtube_api import YouTubeAPI, extract_video_id


app = typer.Typer(
    name="cc-youtube",
    help="YouTube CLI - upload videos, manage comments, and list channel content.",
    no_args_is_help=True,
)

console = Console()

# Global state for output format
_output_format = "text"


def _version_callback(value: bool):
    """Print version and exit."""
    if value:
        console.print(f"cc-youtube v{__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v", help="Show version and exit.",
        callback=_version_callback, is_eager=True,
    ),
    output_format: str = typer.Option(
        "text", "--format", "-f", help="Output format: text or json.",
    ),
):
    """YouTube CLI - upload videos, manage comments, and list channel content."""
    global _output_format
    _output_format = output_format


def _get_api() -> YouTubeAPI:
    """Authenticate and return a YouTubeAPI instance."""
    creds = authenticate(force=False)
    return YouTubeAPI(creds)


def _output_json(data):
    """Print data as JSON."""
    console.print_json(json.dumps(data, indent=2, default=str))


@app.command()
def auth(
    force: bool = typer.Option(False, "--force", help="Force re-authentication."),
    no_browser: bool = typer.Option(False, "--no-browser", help="Print auth URL instead of opening browser."),
):
    """Run OAuth authentication flow.

    Requires credentials.json to exist in the config directory.
    If not found, shows setup instructions for Google Cloud Console.
    """
    config_dir = get_config_dir()
    creds_path = get_credentials_path()

    if not creds_path.exists():
        console.print(
            Panel(
                "[bold]YouTube OAuth Setup Required[/bold]\n\n"
                "1. Go to https://console.cloud.google.com/\n"
                "2. Create a project (or select existing)\n"
                "3. Enable 'YouTube Data API v3'\n"
                "4. Go to Credentials -> Create Credentials -> OAuth client ID\n"
                "5. Application type: Desktop app\n"
                "6. Download the JSON file\n"
                f"7. Save it as:\n   {creds_path}",
                title="Setup",
                border_style="yellow",
            )
        )
        raise typer.Exit(1)

    console.print("Starting OAuth authentication flow...")
    creds = authenticate(force=force, open_browser=not no_browser)

    if creds and creds.valid:
        console.print("[bold green]Authentication successful.[/bold green]")

        # Show channel info
        api = YouTubeAPI(creds)
        info = api.get_channel_info()
        if "error" not in info:
            console.print(f"  Channel: {info.get('title', 'N/A')}")
            console.print(f"  Videos:  {info.get('video_count', '0')}")
            console.print(f"  Subscribers: {info.get('subscriber_count', '0')}")
    else:
        console.print("[bold red]Authentication failed.[/bold red]")
        raise typer.Exit(1)


@app.command()
def status():
    """Show authentication status and channel info."""
    auth_status = get_auth_status()

    if _output_format == "json":
        _output_json(auth_status)
        return

    console.print("[bold]YouTube Authentication Status[/bold]")
    console.print(f"  Config dir:   {auth_status['config_dir']}")
    console.print(f"  Credentials:  {'Found' if auth_status['credentials_exists'] else 'Not found'}")
    console.print(f"  Token:        {'Found' if auth_status['token_exists'] else 'Not found'}")
    console.print(f"  Authenticated: {'Yes' if auth_status['authenticated'] else 'No'}")

    if auth_status["authenticated"]:
        console.print()
        api = _get_api()
        info = api.get_channel_info()
        if "error" not in info:
            console.print("[bold]Channel Info[/bold]")
            console.print(f"  Channel:     {info.get('title', 'N/A')}")
            console.print(f"  ID:          {info.get('id', 'N/A')}")
            console.print(f"  Custom URL:  {info.get('custom_url', 'N/A')}")
            console.print(f"  Videos:      {info.get('video_count', '0')}")
            console.print(f"  Subscribers: {info.get('subscriber_count', '0')}")
            console.print(f"  Total views: {info.get('view_count', '0')}")
        else:
            console.print(f"  [yellow]{info['error']}[/yellow]")
    else:
        console.print()
        console.print("Run 'cc-youtube auth' to authenticate.")


@app.command()
def upload(
    file: str = typer.Argument(..., help="Path to the video file to upload."),
    title: str = typer.Option(..., "--title", "-t", help="Video title."),
    description: str = typer.Option("", "--description", "-d", help="Video description."),
    tags: Optional[str] = typer.Option(None, "--tags", help="Comma-separated tags."),
    privacy: str = typer.Option("private", "--privacy", "-p", help="Privacy: private, unlisted, or public."),
    thumbnail: Optional[str] = typer.Option(None, "--thumbnail", help="Path to thumbnail image."),
    category: str = typer.Option("22", "--category", "-c", help="YouTube category ID (default: 22 = People & Blogs)."),
):
    """Upload a video to YouTube.

    The video defaults to private. Use --privacy to change.
    """
    video_path = Path(file)
    if not video_path.exists():
        console.print(f"[bold red]ERROR: Video file not found: {file}[/bold red]")
        raise typer.Exit(1)

    if privacy not in ("private", "unlisted", "public"):
        console.print(f"[bold red]ERROR: Invalid privacy setting: {privacy}[/bold red]")
        console.print("Valid options: private, unlisted, public")
        raise typer.Exit(1)

    tag_list = [t.strip() for t in tags.split(",")] if tags else None

    api = _get_api()

    console.print(f"Uploading: {video_path.name}")
    console.print(f"  Title:   {title}")
    console.print(f"  Privacy: {privacy}")
    if tag_list:
        console.print(f"  Tags:    {', '.join(tag_list)}")

    result = api.upload(
        file_path=str(video_path),
        title=title,
        description=description,
        tags=tag_list,
        category=category,
        privacy=privacy,
        thumbnail=thumbnail,
    )

    if _output_format == "json":
        _output_json(result)
        return

    console.print()
    console.print("[bold green]Upload complete.[/bold green]")
    console.print(f"  Video ID: {result['id']}")
    console.print(f"  URL:      {result['url']}")


@app.command("list")
def list_videos(
    count: int = typer.Option(10, "--count", "-n", help="Number of videos to list."),
):
    """List your channel's videos."""
    api = _get_api()
    videos = api.list_videos(count=count)

    if _output_format == "json":
        _output_json(videos)
        return

    if not videos:
        console.print("No videos found.")
        return

    table = Table(title="Your Videos")
    table.add_column("Title", style="bold", max_width=40)
    table.add_column("ID", style="dim")
    table.add_column("Published", style="cyan")
    table.add_column("Views", justify="right")
    table.add_column("Likes", justify="right")
    table.add_column("Comments", justify="right")
    table.add_column("Privacy", style="yellow")

    for v in videos:
        published = v.get("published_at", "")
        if published and len(published) >= 10:
            published = published[:10]

        table.add_row(
            v.get("title", "N/A"),
            v.get("id", "N/A"),
            published,
            v.get("view_count", "0"),
            v.get("like_count", "0"),
            v.get("comment_count", "0"),
            v.get("privacy", "unknown"),
        )

    console.print(table)


@app.command()
def comments(
    video_url: str = typer.Argument(..., help="Video URL or ID to list comments for."),
    count: int = typer.Option(20, "--count", "-n", help="Number of comments to list."),
):
    """List comments on a video."""
    video_id = extract_video_id(video_url)
    api = _get_api()
    comment_list = api.list_comments(video_id=video_id, count=count)

    if _output_format == "json":
        _output_json(comment_list)
        return

    if not comment_list:
        console.print("No comments found.")
        return

    table = Table(title=f"Comments on {video_id}")
    table.add_column("Author", style="bold", max_width=20)
    table.add_column("Comment", max_width=50)
    table.add_column("Likes", justify="right")
    table.add_column("Replies", justify="right")
    table.add_column("Date", style="cyan")
    table.add_column("ID", style="dim")

    for c in comment_list:
        published = c.get("published_at", "")
        if published and len(published) >= 10:
            published = published[:10]

        # Truncate long comments for table display
        text = c.get("text", "")
        if len(text) > 80:
            text = text[:77] + "..."

        table.add_row(
            c.get("author", "N/A"),
            text,
            str(c.get("like_count", 0)),
            str(c.get("reply_count", 0)),
            published,
            c.get("id", "N/A"),
        )

    console.print(table)


@app.command()
def comment(
    video_url: str = typer.Argument(..., help="Video URL or ID to comment on."),
    text: str = typer.Argument(..., help="Comment text."),
):
    """Post a comment on a video."""
    video_id = extract_video_id(video_url)
    api = _get_api()
    result = api.comment(video_id=video_id, text=text)

    if _output_format == "json":
        _output_json(result)
        return

    console.print("[bold green]Comment posted.[/bold green]")
    console.print(f"  Comment ID: {result['id']}")
    console.print(f"  Video:      {video_id}")


@app.command()
def reply(
    comment_id: str = typer.Argument(..., help="Parent comment thread ID to reply to."),
    text: str = typer.Argument(..., help="Reply text."),
):
    """Reply to a comment."""
    api = _get_api()
    result = api.reply(parent_comment_id=comment_id, text=text)

    if _output_format == "json":
        _output_json(result)
        return

    console.print("[bold green]Reply posted.[/bold green]")
    console.print(f"  Reply ID:  {result['id']}")
    console.print(f"  Parent:    {comment_id}")


@app.command()
def delete(
    video_id: str = typer.Argument(..., help="Video ID to delete."),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip confirmation prompt."),
):
    """Delete a video."""
    if not yes:
        confirm = typer.confirm(f"Delete video {video_id}? This cannot be undone")
        if not confirm:
            console.print("Cancelled.")
            raise typer.Exit()

    api = _get_api()
    api.delete_video(video_id)

    if _output_format == "json":
        _output_json({"deleted": True, "video_id": video_id})
        return

    console.print(f"[bold green]Video {video_id} deleted.[/bold green]")


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-youtube", app=app)


if __name__ == "__main__":
    tool_main()

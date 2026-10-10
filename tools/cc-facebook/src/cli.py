"""CLI for cc-facebook - Facebook Page management via Graph API."""

import json
import logging
import sys
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table
from rich.panel import Panel

logger = logging.getLogger(__name__)

try:
    from . import __version__
    from .auth import (
        get_credentials,
        store_credentials,
        delete_credentials,
        has_credentials,
        get_page_id,
    )
    from .facebook_api import FacebookAPI, extract_post_id
except ImportError:
    from src import __version__
    from src.auth import (
        get_credentials,
        store_credentials,
        delete_credentials,
        has_credentials,
        get_page_id,
    )
    from src.facebook_api import FacebookAPI, extract_post_id

app = typer.Typer(
    name="cc-facebook",
    help="Facebook Page CLI - manage posts, comments, and page info via Graph API.",
    no_args_is_help=True,
)

console = Console()

# Global state for output format
_output_format = "text"


def _version_callback(value: bool) -> None:
    """Print version and exit."""
    if value:
        console.print(f"cc-facebook {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False,
        "--version",
        "-v",
        help="Show version and exit.",
        callback=_version_callback,
        is_eager=True,
    ),
    output_format: str = typer.Option(
        "text",
        "--format",
        "-f",
        help="Output format: text or json.",
    ),
) -> None:
    """Facebook Page CLI - manage posts, comments, and page info."""
    global _output_format
    _output_format = output_format


def _output_json(data: dict | list) -> None:
    """Print data as formatted JSON."""
    console.print(json.dumps(data, indent=2, default=str))


@app.command()
def auth() -> None:
    """Configure Facebook credentials interactively.

    Prompts for App ID, App Secret, Page Access Token, and Page ID.
    Stores all values securely in the system keyring.
    """
    logger.info("[cli] auth: starting interactive credential setup")
    console.print(Panel(
        "Facebook Page Credential Setup\n\n"
        "You need:\n"
        "  1. A Facebook App ID and App Secret (from developers.facebook.com)\n"
        "  2. A Page Access Token (long-lived, with pages_manage_posts permission)\n"
        "  3. Your Facebook Page ID\n\n"
        "See: https://developers.facebook.com/docs/pages-api/overview",
        title="cc-facebook auth",
    ))

    app_id = typer.prompt("Facebook App ID")
    app_secret = typer.prompt("Facebook App Secret")
    page_access_token = typer.prompt("Page Access Token")
    page_id = typer.prompt("Facebook Page ID")

    if not all([app_id, app_secret, page_access_token, page_id]):
        console.print("[red]ERROR:[/red] All fields are required.")
        raise typer.Exit(1)

    store_credentials(
        app_id=app_id.strip(),
        app_secret=app_secret.strip(),
        page_access_token=page_access_token.strip(),
        page_id=page_id.strip(),
    )

    console.print("[green]Done.[/green] Credentials stored in system keyring.")
    console.print(f"Page ID: {page_id.strip()}")
    console.print("Run 'cc-facebook status' to verify the connection.")
    logger.info("[cli] auth: credentials stored for page_id=%s", page_id.strip())


@app.command()
def status() -> None:
    """Show authentication status and page info."""
    logger.info("[cli] status: checking auth status")

    if not has_credentials():
        if _output_format == "json":
            _output_json({"authenticated": False, "error": "No credentials configured"})
        else:
            console.print("[red]Not configured.[/red] Run 'cc-facebook auth' to set up credentials.")
        raise typer.Exit(1)

    creds = get_credentials()
    page_id = creds["page_id"] if creds else "N/A"

    # Try to fetch page info to verify the token works
    try:
        api = FacebookAPI()
        page_info = api.get_page_info()

        if _output_format == "json":
            _output_json({
                "authenticated": True,
                "page_id": page_id,
                "page_name": page_info.get("name", "N/A"),
                "category": page_info.get("category", "N/A"),
                "followers": page_info.get("followers_count", 0),
                "fans": page_info.get("fan_count", 0),
            })
        else:
            table = Table(title="Facebook Page Status")
            table.add_column("Field", style="bold")
            table.add_column("Value")
            table.add_row("Status", "Authenticated")
            table.add_row("Page ID", page_id)
            table.add_row("Page Name", page_info.get("name", "N/A"))
            table.add_row("Category", page_info.get("category", "N/A"))
            table.add_row("Followers", str(page_info.get("followers_count", 0)))
            table.add_row("Fans (Likes)", str(page_info.get("fan_count", 0)))
            link = page_info.get("link", "")
            if link:
                table.add_row("Link", link)
            website = page_info.get("website", "")
            if website:
                table.add_row("Website", website)
            console.print(table)

    except RuntimeError as exc:
        if _output_format == "json":
            _output_json({"authenticated": False, "page_id": page_id, "error": str(exc)})
        else:
            console.print(f"[yellow]Credentials stored[/yellow] but API call failed:")
            console.print(f"  [red]{exc}[/red]")
            console.print("Your token may be expired. Run 'cc-facebook auth' to update.")
        raise typer.Exit(1)

    logger.info("[cli] status: authenticated, page_id=%s", page_id)


@app.command()
def pages() -> None:
    """List Facebook Pages managed by the authenticated user.

    Uses GET /me/accounts to list pages where the user has a role.
    """
    logger.info("[cli] pages: listing managed pages")

    api = FacebookAPI()
    page_list = api.get_pages()

    if _output_format == "json":
        _output_json(page_list)
        return

    if not page_list:
        console.print("No pages found. Make sure your token has 'pages_show_list' permission.")
        return

    table = Table(title="Managed Facebook Pages")
    table.add_column("Page ID", style="bold")
    table.add_column("Name")
    table.add_column("Category")
    table.add_column("Has Token")

    for page in page_list:
        has_token = "Yes" if page.get("access_token") else "No"
        table.add_row(
            page.get("id", "N/A"),
            page.get("name", "N/A"),
            page.get("category", "N/A"),
            has_token,
        )

    console.print(table)
    logger.info("[cli] pages: listed %d pages", len(page_list))


@app.command("post")
def create_post(
    message: str = typer.Argument(..., help="The post text content."),
    link: Optional[str] = typer.Option(None, "--link", "-l", help="URL to attach to the post."),
) -> None:
    """Create a new post on the Facebook Page."""
    logger.info("[cli] post: creating post, has_link=%s", bool(link))

    api = FacebookAPI()
    result = api.post(message=message, link=link or "")

    if _output_format == "json":
        _output_json(result)
    else:
        console.print("[green]Post created.[/green]")
        console.print(f"  Post ID: {result.get('id', 'N/A')}")
        url = result.get("url", "")
        if url:
            console.print(f"  URL: {url}")

    logger.info("[cli] post: created post_id=%s", result.get("id"))


@app.command("comment")
def create_comment(
    post_url: str = typer.Argument(..., help="Post URL or post ID to comment on."),
    message: str = typer.Argument(..., help="The comment text."),
) -> None:
    """Add a comment to a Facebook post."""
    logger.info("[cli] comment: commenting on post")

    post_id = extract_post_id(post_url)
    api = FacebookAPI()
    result = api.comment(post_id=post_id, message=message)

    if _output_format == "json":
        _output_json(result)
    else:
        console.print("[green]Comment added.[/green]")
        console.print(f"  Comment ID: {result.get('id', 'N/A')}")

    logger.info("[cli] comment: created comment_id=%s", result.get("id"))


@app.command("reply")
def create_reply(
    comment_id: str = typer.Argument(..., help="Comment ID to reply to."),
    message: str = typer.Argument(..., help="The reply text."),
) -> None:
    """Reply to a comment on a Facebook post."""
    logger.info("[cli] reply: replying to comment_id=%s", comment_id)

    api = FacebookAPI()
    result = api.reply(comment_id=comment_id, message=message)

    if _output_format == "json":
        _output_json(result)
    else:
        console.print("[green]Reply added.[/green]")
        console.print(f"  Reply ID: {result.get('id', 'N/A')}")

    logger.info("[cli] reply: created reply_id=%s", result.get("id"))


@app.command("list")
def list_posts(
    count: int = typer.Option(10, "--count", "-n", help="Number of posts to list."),
) -> None:
    """List recent posts from the Facebook Page."""
    logger.info("[cli] list: listing posts, count=%d", count)

    api = FacebookAPI()
    posts = api.list_posts(count=count)

    if _output_format == "json":
        _output_json(posts)
        return

    if not posts:
        console.print("No posts found on this page.")
        return

    table = Table(title="Recent Page Posts")
    table.add_column("Post ID", style="bold")
    table.add_column("Created", style="dim")
    table.add_column("Type")
    table.add_column("Message", max_width=60)
    table.add_column("URL")

    for p in posts:
        msg = p.get("message", "")
        # Truncate long messages for display
        if len(msg) > 57:
            msg = msg[:57] + "..."
        table.add_row(
            p.get("id", "N/A"),
            p.get("created_time", "N/A"),
            p.get("type", "N/A"),
            msg,
            p.get("permalink_url", ""),
        )

    console.print(table)
    logger.info("[cli] list: displayed %d posts", len(posts))


@app.command("delete")
def delete_post(
    post_id: str = typer.Argument(..., help="The post ID to delete."),
) -> None:
    """Delete a post from the Facebook Page."""
    logger.info("[cli] delete: deleting post_id=%s", post_id)

    api = FacebookAPI()
    success = api.delete_post(post_id=post_id)

    if _output_format == "json":
        _output_json({"deleted": success, "post_id": post_id})
    else:
        if success:
            console.print(f"[green]Post deleted.[/green] ID: {post_id}")
        else:
            console.print(f"[red]Failed to delete post.[/red] ID: {post_id}")

    logger.info("[cli] delete: post_id=%s, success=%s", post_id, success)


@app.command("logout")
def logout() -> None:
    """Remove stored Facebook credentials from keyring."""
    logger.info("[cli] logout: removing credentials")

    if not has_credentials():
        console.print("No credentials stored.")
        return

    deleted = delete_credentials()
    if deleted:
        console.print("[green]Credentials removed.[/green]")
    else:
        console.print("No credentials to remove.")

    logger.info("[cli] logout: deleted=%s", deleted)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-facebook", app=app)


if __name__ == "__main__":
    tool_main()

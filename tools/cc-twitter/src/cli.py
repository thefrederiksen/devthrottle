"""CLI for cc-twitter - Twitter/X from the command line.

Uses OAuth 1.0a User Context with Twitter API v2 via tweepy.
"""

import json
import logging
import sys
from typing import List, Optional

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
    )
    from .twitter_api import TwitterAPI, extract_tweet_id
except ImportError:
    from src import __version__
    from src.auth import (
        get_credentials,
        store_credentials,
        delete_credentials,
        has_credentials,
    )
    from src.twitter_api import TwitterAPI, extract_tweet_id

app = typer.Typer(
    name="cc-twitter",
    help="Twitter/X CLI - post, reply, like, retweet, and view timeline.",
    no_args_is_help=True,
)

console = Console()

# Global state for output format
_output_format: str = "text"


def _get_format() -> str:
    """Get the current output format."""
    return _output_format


def _version_callback(value: bool) -> None:
    """Print version and exit. Eager so --version works without a subcommand."""
    if value:
        console.print(f"cc-twitter {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: Optional[bool] = typer.Option(
        None,
        "--version",
        "-v",
        help="Show version and exit.",
        callback=_version_callback,
        is_eager=True,
    ),
    fmt: str = typer.Option(
        "text",
        "--format",
        "-f",
        help="Output format: text or json.",
    ),
) -> None:
    """cc-twitter - Twitter/X CLI tool."""
    global _output_format
    _output_format = fmt


@app.command()
def auth() -> None:
    """Configure Twitter API credentials (OAuth 1.0a).

    Prompts for API Key, API Secret, Access Token, and Access Token Secret.
    These are obtained from the Twitter Developer Portal.
    """
    logger.info("[cli] auth: starting credential setup")
    console.print(
        Panel(
            "Twitter OAuth 1.0a Setup\n\n"
            "You need credentials from the Twitter Developer Portal:\n"
            "  1. Go to https://developer.twitter.com/en/portal/dashboard\n"
            "  2. Create or select a project/app\n"
            "  3. Generate API Key, API Secret, Access Token, Access Token Secret\n"
            "  4. Ensure your app has Read and Write permissions",
            title="cc-twitter auth",
        )
    )

    api_key = typer.prompt("API Key (Consumer Key)")
    api_secret = typer.prompt("API Secret (Consumer Secret)")
    access_token = typer.prompt("Access Token")
    access_token_secret = typer.prompt("Access Token Secret")

    if not all([api_key.strip(), api_secret.strip(), access_token.strip(), access_token_secret.strip()]):
        console.print("[red]ERROR:[/red] All four credentials are required.")
        raise typer.Exit(code=1)

    store_credentials(
        api_key=api_key.strip(),
        api_secret=api_secret.strip(),
        access_token=access_token.strip(),
        access_token_secret=access_token_secret.strip(),
    )
    console.print("[green]Credentials stored successfully.[/green]")

    # Verify by fetching user info
    console.print("Verifying credentials...")
    try:
        api = TwitterAPI()
        me = api.get_me()
        console.print(f"[green]Authenticated as @{me['username']} ({me['name']})[/green]")
    except Exception as ex:
        console.print(f"[red]WARNING: Credentials stored but verification failed: {ex}[/red]")
        console.print("Check that your credentials are correct and have the right permissions.")

    logger.info("[cli] auth: done")


@app.command()
def status() -> None:
    """Show authentication status and account info."""
    logger.info("[cli] status: checking auth")

    if not has_credentials():
        if _get_format() == "json":
            console.print(json.dumps({"authenticated": False}))
        else:
            console.print("Status: Not authenticated")
            console.print("Run 'cc-twitter auth' to configure credentials.")
        raise typer.Exit()

    try:
        api = TwitterAPI()
        me = api.get_me()
    except Exception as ex:
        console.print(f"[red]ERROR: Credentials found but API call failed: {ex}[/red]")
        raise typer.Exit(code=1)

    if _get_format() == "json":
        console.print(json.dumps({"authenticated": True, **me}))
    else:
        metrics = me.get("public_metrics", {})
        console.print(
            Panel(
                f"Username:  @{me['username']}\n"
                f"Name:      {me['name']}\n"
                f"ID:        {me['id']}\n"
                f"Bio:       {me['description']}\n"
                f"Followers: {metrics.get('followers_count', 'N/A')}\n"
                f"Following: {metrics.get('following_count', 'N/A')}\n"
                f"Tweets:    {metrics.get('tweet_count', 'N/A')}",
                title="Twitter Account",
            )
        )

    logger.info("[cli] status: done")


@app.command()
def post(
    text: str = typer.Argument(..., help="Tweet text to post (max 280 characters)."),
) -> None:
    """Create a new tweet."""
    logger.info("[cli] post: text_len=%d", len(text))

    if len(text) > 280:
        console.print(f"[red]ERROR: Tweet is {len(text)} characters (max 280).[/red]")
        raise typer.Exit(code=1)

    api = TwitterAPI()
    result = api.post(text)

    if _get_format() == "json":
        console.print(json.dumps(result))
    else:
        console.print(f"[green]Tweet posted:[/green] {result['url']}")

    logger.info("[cli] post: done id=%s", result["id"])


@app.command()
def reply(
    text: str = typer.Argument(..., help="Reply text."),
    to: str = typer.Option(..., "--to", "-t", help="Tweet URL or ID to reply to."),
) -> None:
    """Reply to a tweet."""
    logger.info("[cli] reply: to=%s text_len=%d", to, len(text))

    tweet_id = extract_tweet_id(to)
    api = TwitterAPI()
    result = api.reply(text, tweet_id)

    if _get_format() == "json":
        console.print(json.dumps(result))
    else:
        console.print(f"[green]Reply posted:[/green] {result['url']}")

    logger.info("[cli] reply: done id=%s", result["id"])


@app.command()
def thread(
    texts: List[str] = typer.Argument(..., help="Tweet texts forming the thread (one per argument)."),
) -> None:
    """Post a multi-tweet thread.

    Each argument is one tweet in the thread.

    Example:
        cc-twitter thread "First tweet" "Second tweet" "Third tweet"
    """
    logger.info("[cli] thread: %d tweets", len(texts))

    if not texts:
        console.print("[red]ERROR: Provide at least one tweet for the thread.[/red]")
        raise typer.Exit(code=1)

    for i, t in enumerate(texts):
        if len(t) > 280:
            console.print(f"[red]ERROR: Tweet {i + 1} is {len(t)} characters (max 280).[/red]")
            raise typer.Exit(code=1)

    api = TwitterAPI()
    results = api.thread(texts)

    if _get_format() == "json":
        console.print(json.dumps(results))
    else:
        console.print(f"[green]Thread posted ({len(results)} tweets):[/green]")
        for i, r in enumerate(results):
            console.print(f"  {i + 1}. {r['url']}")

    logger.info("[cli] thread: done, %d tweets", len(results))


@app.command()
def like(
    tweet_url: str = typer.Argument(..., help="Tweet URL or ID to like."),
) -> None:
    """Like a tweet."""
    logger.info("[cli] like: %s", tweet_url)

    tweet_id = extract_tweet_id(tweet_url)
    api = TwitterAPI()
    result = api.like(tweet_id)

    if _get_format() == "json":
        console.print(json.dumps({"liked": result, "tweet_id": tweet_id}))
    else:
        if result:
            console.print(f"[green]Liked tweet {tweet_id}[/green]")
        else:
            console.print(f"Tweet {tweet_id} was already liked or could not be liked.")

    logger.info("[cli] like: done result=%s", result)


@app.command()
def retweet(
    tweet_url: str = typer.Argument(..., help="Tweet URL or ID to retweet."),
) -> None:
    """Retweet a tweet."""
    logger.info("[cli] retweet: %s", tweet_url)

    tweet_id = extract_tweet_id(tweet_url)
    api = TwitterAPI()
    result = api.retweet(tweet_id)

    if _get_format() == "json":
        console.print(json.dumps({"retweeted": result, "tweet_id": tweet_id}))
    else:
        if result:
            console.print(f"[green]Retweeted tweet {tweet_id}[/green]")
        else:
            console.print(f"Tweet {tweet_id} was already retweeted or could not be retweeted.")

    logger.info("[cli] retweet: done result=%s", result)


@app.command(name="delete")
def delete_tweet(
    tweet_url: str = typer.Argument(..., help="Tweet URL or ID to delete."),
) -> None:
    """Delete one of your own tweets."""
    logger.info("[cli] delete: %s", tweet_url)

    tweet_id = extract_tweet_id(tweet_url)
    api = TwitterAPI()
    result = api.delete(tweet_id)

    if _get_format() == "json":
        console.print(json.dumps({"deleted": result, "tweet_id": tweet_id}))
    else:
        if result:
            console.print(f"[green]Deleted tweet {tweet_id}[/green]")
        else:
            console.print(f"[red]Failed to delete tweet {tweet_id}[/red]")

    logger.info("[cli] delete: done result=%s", result)


@app.command()
def timeline(
    count: int = typer.Option(10, "--count", "-n", help="Number of tweets to show (max 100)."),
) -> None:
    """Show home timeline."""
    logger.info("[cli] timeline: count=%d", count)

    api = TwitterAPI()
    tweets = api.timeline(count)

    if _get_format() == "json":
        console.print(json.dumps(tweets))
    else:
        if not tweets:
            console.print("No tweets found in timeline.")
            return

        table = Table(title="Home Timeline")
        table.add_column("ID", style="dim", no_wrap=True)
        table.add_column("Author", style="cyan", no_wrap=True)
        table.add_column("Text", max_width=60)
        table.add_column("Date", style="dim", no_wrap=True)

        for tweet in tweets:
            table.add_row(
                tweet["id"],
                tweet.get("author_id", ""),
                tweet["text"][:60] + ("..." if len(tweet["text"]) > 60 else ""),
                tweet.get("created_at", "")[:19],
            )

        console.print(table)

    logger.info("[cli] timeline: done, %d tweets", len(tweets))


@app.command()
def mentions(
    count: int = typer.Option(10, "--count", "-n", help="Number of mentions to show (max 100)."),
) -> None:
    """Show recent mentions."""
    logger.info("[cli] mentions: count=%d", count)

    api = TwitterAPI()
    tweets = api.mentions(count)

    if _get_format() == "json":
        console.print(json.dumps(tweets))
    else:
        if not tweets:
            console.print("No mentions found.")
            return

        table = Table(title="Mentions")
        table.add_column("ID", style="dim", no_wrap=True)
        table.add_column("Author", style="cyan", no_wrap=True)
        table.add_column("Text", max_width=60)
        table.add_column("Date", style="dim", no_wrap=True)

        for tweet in tweets:
            table.add_row(
                tweet["id"],
                tweet.get("author_id", ""),
                tweet["text"][:60] + ("..." if len(tweet["text"]) > 60 else ""),
                tweet.get("created_at", "")[:19],
            )

        console.print(table)

    logger.info("[cli] mentions: done, %d tweets", len(tweets))


if __name__ == "__main__":
    app()

"""cc-reddit CLI - Reddit interactions via browser automation."""

import typer
from rich.console import Console
from rich.table import Table
from typing import Optional
import json
import re
import time
import random
from pathlib import Path

from .browser_client import BrowserClient, BrowserError, WorkspaceError
from .selectors import RedditURLs, NewReddit

app = typer.Typer(
    name="cc-reddit",
    help="Reddit CLI via browser automation",
    no_args_is_help=True,
)

console = Console()


def get_config_dir() -> Path:
    """Get cc-reddit config directory."""
    try:
        from cc_storage import CcStorage
        return CcStorage.tool_config("reddit")
    except ImportError:
        import sys
        _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
        if _tools_dir not in sys.path:
            sys.path.insert(0, _tools_dir)
        from cc_storage import CcStorage
        return CcStorage.tool_config("reddit")


def load_default_connection() -> Optional[str]:
    """Load default connection from config.json.

    Returns:
        Default connection name from config, or None to auto-resolve by tool binding.
    """
    config_file = get_config_dir() / "config.json"

    if not config_file.exists():
        return None  # Auto-resolve by tool binding

    try:
        with open(config_file, "r") as f:
            data = json.load(f)
        return data.get("default_connection", data.get("default_workspace"))
    except (json.JSONDecodeError, IOError):
        return None


# Global options stored in context
class Config:
    workspace: str = ""
    format: str = "text"
    delay: float = 1.0
    verbose: bool = False


config = Config()


def human_delay(base_seconds: float) -> None:
    """Sleep with random jitter (+-30%) to simulate human timing."""
    jitter = base_seconds * 0.3
    actual = base_seconds + random.uniform(-jitter, jitter)
    time.sleep(max(0.1, actual))


def type_slowly(client: "BrowserClient", ref: str, text: str, delay_ms: int = 75) -> None:
    """Type text character by character with human-like delays.

    Args:
        client: Browser client instance
        ref: Element reference to type into
        text: Text to type
        delay_ms: Milliseconds between keystrokes (default 75ms)
    """
    for char in text:
        client.type(ref, char)
        # Add slight random variation to typing speed (+-30%)
        jitter = delay_ms * 0.3
        actual_delay = delay_ms + random.uniform(-jitter, jitter)
        time.sleep(max(0.02, actual_delay / 1000))


def get_client() -> BrowserClient:
    """Get browser client instance for configured workspace."""
    try:
        return BrowserClient(workspace=config.workspace)
    except WorkspaceError as e:
        console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


def output(data: dict, message: str = ""):
    """Output data in configured format."""
    if config.format == "json":
        console.print_json(json.dumps(data))
    elif config.format == "markdown":
        # Convert to markdown representation
        console.print(f"```\n{json.dumps(data, indent=2)}\n```")
    else:
        if message:
            console.print(message)
        elif config.verbose and data:
            console.print(data)


def error(msg: str):
    """Print error message."""
    console.print(f"[red]ERROR:[/red] {msg}")


def success(msg: str):
    """Print success message."""
    console.print(f"[green]OK:[/green] {msg}")


def warn(msg: str):
    """Print warning message."""
    console.print(f"[yellow]WARNING:[/yellow] {msg}")


def _version_callback(value: bool) -> None:
    """Print version and exit. Eager so --version works without a subcommand."""
    if value:
        typer.echo("cc-reddit version 0.1.0")
        raise typer.Exit()


@app.callback()
def main(
    version: Optional[bool] = typer.Option(
        None, "--version", help="Show version and exit.",
        callback=_version_callback, is_eager=True,
    ),
    connection: Optional[str] = typer.Option(None, "--connection", "-c", help="cc-browser connection name"),
    workspace: Optional[str] = typer.Option(None, "--workspace", "-w", hidden=True, help="Deprecated: use --connection"),
    format: str = typer.Option("text", help="Output format: text, json, markdown"),
    delay: float = typer.Option(1.0, help="Delay between actions (seconds)"),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Verbose output"),
):
    """Reddit CLI via browser automation.

    Requires cc-browser daemon to be running.
    Start it with: cc-browser daemon
    """
    # Resolve connection: explicit flag > config > auto-resolve by tool binding
    resolved = connection or workspace or load_default_connection()

    config.workspace = resolved  # May be None; BrowserClient auto-resolves
    config.format = format
    config.delay = delay
    config.verbose = verbose


# =============================================================================
# Status Commands
# =============================================================================

@app.command()
def status():
    """Check cc-browser daemon and Reddit login status."""
    try:
        client = get_client()

        # Check daemon status
        result = client.status()
        console.print("[green]cc-browser daemon:[/green] running")

        browser_status = result.get("browser", "unknown")
        console.print(f"[green]Browser:[/green] {browser_status}")

        # Check if on Reddit
        try:
            info = client.info()
            url = info.get("url", "")
            if "reddit.com" in url:
                console.print(f"[green]Current page:[/green] {url}")
            else:
                console.print(f"[yellow]Current page:[/yellow] {url} (not on Reddit)")
        except BrowserError:
            console.print("[yellow]Browser:[/yellow] no page loaded")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def whoami():
    """Show current logged-in Reddit username."""
    try:
        client = get_client()

        # Navigate to Reddit if not already there
        info = client.info()
        url = info.get("url", "")
        if "reddit.com" not in url:
            client.navigate(RedditURLs.home())
            human_delay(2)  # Wait for page load

        # Get snapshot to find user element
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Method 1: Look for user menu in snapshot
        # Format: button "User Avatar Expand user menu" [ref=e9]
        lines = snapshot_text.split('\n')
        user_menu_ref = None
        for line in lines:
            if 'user avatar' in line.lower() or 'expand user menu' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    user_menu_ref = match.group(1)
                    break

        # Method 2: Try JavaScript to get username from various locations
        js_code = """
        (() => {
            // Try new Reddit user menu
            const userMenu = document.querySelector('faceplate-dropdown-menu[name="account"]');
            if (userMenu) {
                const spans = userMenu.querySelectorAll('span');
                for (const span of spans) {
                    const text = span.textContent?.trim();
                    if (text && text.startsWith('u/')) {
                        return text.replace('u/', '');
                    }
                }
            }

            // Try shreddit-header user link
            const userLink = document.querySelector('a[href*="/user/"][data-testid]');
            if (userLink) {
                const href = userLink.getAttribute('href');
                const match = href.match(/\\/user\\/([^/]+)/);
                if (match) return match[1];
            }

            // Try old Reddit
            const oldUser = document.querySelector('.user a');
            if (oldUser) return oldUser.textContent;

            // Try profile link in header
            const profileLinks = document.querySelectorAll('a[href^="/user/"]');
            for (const link of profileLinks) {
                const href = link.getAttribute('href');
                if (href && !href.includes('/user/me')) {
                    const match = href.match(/\\/user\\/([^/]+)/);
                    if (match) return match[1];
                }
            }

            // Check if login button exists (means not logged in)
            const loginBtn = document.querySelector('a[href*="login"]');
            if (loginBtn) return '__NOT_LOGGED_IN__';

            return '__UNKNOWN__';
        })()
        """

        try:
            result = client.evaluate(js_code)
            username = result.get("result", "")

            if username == "__NOT_LOGGED_IN__":
                warn("Not logged in")
                console.print("Tip: Log into Reddit in the browser first")
            elif username == "__UNKNOWN__" or not username:
                # Fallback: check snapshot for "Open inbox" which indicates logged in
                if "Open inbox" in snapshot_text:
                    console.print("Logged in (username detection failed)")
                    console.print("Tip: Click your profile icon in the browser to see username")
                else:
                    warn("Not logged in or unable to detect username")
            else:
                console.print(f"Logged in as: [green]u/{username}[/green]")

        except (BrowserError, json.JSONDecodeError, KeyError) as e:
            if config.verbose:
                error(f"JavaScript evaluation failed: {e}")
            # Fallback check
            if "Open inbox" in snapshot_text:
                console.print("Logged in (username detection failed)")
            else:
                warn("Could not determine login status")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def me(
    posts: bool = typer.Option(True, "--posts/--no-posts", help="Show posts"),
    comments: bool = typer.Option(True, "--comments/--no-comments", help="Show comments"),
    limit: int = typer.Option(10, help="Max items to show per category"),
):
    """View your Reddit profile activity (posts and comments)."""
    try:
        client = get_client()

        # First get the username
        info = client.info()
        url = info.get("url", "")
        if "reddit.com" not in url:
            client.navigate(RedditURLs.home())
            human_delay(2)

        # Get username via JS
        js_username = """
        (() => {
            const userMenu = document.querySelector('faceplate-dropdown-menu[name="account"]');
            if (userMenu) {
                const spans = userMenu.querySelectorAll('span');
                for (const span of spans) {
                    const text = span.textContent?.trim();
                    if (text && text.startsWith('u/')) {
                        return text.replace('u/', '');
                    }
                }
            }
            const profileLinks = document.querySelectorAll('a[href^="/user/"]');
            for (const link of profileLinks) {
                const href = link.getAttribute('href');
                if (href && !href.includes('/user/me')) {
                    const match = href.match(/\\/user\\/([^/]+)/);
                    if (match) return match[1];
                }
            }
            return '';
        })()
        """
        result = client.evaluate(js_username)
        username = result.get("result", "")

        if not username:
            error("Could not detect username. Make sure you're logged into Reddit.")
            raise typer.Exit(1)

        console.print(f"\n[bold]Profile: u/{username}[/bold]\n")

        # Get posts
        if posts:
            client.navigate(f"https://www.reddit.com/user/{username}/submitted")
            human_delay(2)

            js_posts = """
            (() => {
                const posts = document.querySelectorAll("shreddit-post");
                const data = [];
                for (const post of posts) {
                    const title = post.getAttribute("post-title") || "";
                    const subreddit = post.getAttribute("subreddit-prefixed-name") || "";
                    const score = post.getAttribute("score") || "0";
                    const comments = post.getAttribute("comment-count") || "0";
                    const permalink = post.getAttribute("permalink") || "";

                    if (title) {
                        data.push({
                            title: title,
                            subreddit: subreddit,
                            score: score,
                            comments: comments,
                            url: permalink
                        });
                    }
                }
                return JSON.stringify(data);
            })()
            """
            result = client.evaluate(js_posts)
            posts_data = json.loads(result.get("result", "[]"))

            console.print(f"[cyan]Posts ({len(posts_data)} total):[/cyan]")
            if posts_data:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Title", width=45, no_wrap=True, overflow="ellipsis")
                table.add_column("Subreddit", width=18, no_wrap=True, overflow="ellipsis")
                table.add_column("Pts", width=4, justify="right")
                table.add_column("Cmt", width=4, justify="right")

                for i, p in enumerate(posts_data[:limit], 1):
                    table.add_row(
                        str(i),
                        p["title"],
                        p["subreddit"],
                        p["score"],
                        p["comments"]
                    )
                console.print(table)
            else:
                console.print("  No posts found.")
            console.print()

        # Get comments
        if comments:
            client.navigate(f"https://www.reddit.com/user/{username}/comments")
            human_delay(2)

            # Comments page has a different structure - extract from snapshot
            snapshot = client.snapshot()
            snapshot_text = snapshot.get("snapshot", "")

            # Parse comment threads
            lines = snapshot_text.split('\n')
            comments_data = []
            i = 0
            while i < len(lines):
                line = lines[i]
                if f'Thread for {username}' in line:
                    # Extract thread title
                    match = re.search(r'"Thread for .+? on (.+?)"', line)
                    thread_title = match.group(1) if match else "Unknown"

                    # Look for subreddit in next few lines
                    subreddit = ""
                    for j in range(i+1, min(i+5, len(lines))):
                        sub_match = re.search(r'"(r/[^"]+)"', lines[j])
                        if sub_match and "icon" not in lines[j]:
                            subreddit = sub_match.group(1)
                            break
                        elif sub_match:
                            subreddit = sub_match.group(1).replace(" icon", "")
                            break

                    comments_data.append({
                        "thread": thread_title,
                        "subreddit": subreddit
                    })
                i += 1

            console.print(f"[cyan]Comments ({len(comments_data)} found):[/cyan]")
            if comments_data:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Thread", width=50, no_wrap=True, overflow="ellipsis")
                table.add_column("Subreddit", width=20, no_wrap=True, overflow="ellipsis")

                for i, c in enumerate(comments_data[:limit], 1):
                    table.add_row(str(i), c["thread"], c["subreddit"])
                console.print(table)
            else:
                console.print("  No comments found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def saved(
    limit: int = typer.Option(10, help="Max items to show"),
):
    """View your saved posts and comments."""
    try:
        client = get_client()

        # Navigate to saved page
        client.navigate("https://www.reddit.com/user/me/saved")
        human_delay(2)

        # Extract saved items using JavaScript
        js_saved = """
        (() => {
            const posts = document.querySelectorAll("shreddit-post");
            const data = [];
            for (const post of posts) {
                const title = post.getAttribute("post-title") || "";
                const subreddit = post.getAttribute("subreddit-prefixed-name") || "";
                const author = post.getAttribute("author") || "";
                const score = post.getAttribute("score") || "0";
                const comments = post.getAttribute("comment-count") || "0";
                const permalink = post.getAttribute("permalink") || "";

                if (title) {
                    data.push({
                        title: title,
                        subreddit: subreddit,
                        author: author,
                        score: score,
                        comments: comments,
                        url: permalink
                    });
                }
            }
            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_saved)
        saved_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]Saved Items[/bold] ({len(saved_data)} found)\n")

        if saved_data:
            if config.format == "json":
                console.print_json(json.dumps(saved_data[:limit]))
            else:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Title", width=45, no_wrap=True, overflow="ellipsis")
                table.add_column("Subreddit", width=18, no_wrap=True, overflow="ellipsis")
                table.add_column("Pts", width=5, justify="right")

                for i, p in enumerate(saved_data[:limit], 1):
                    table.add_row(
                        str(i),
                        p["title"],
                        p["subreddit"],
                        p["score"]
                    )
                console.print(table)
        else:
            console.print("No saved items found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def karma():
    """Show your karma breakdown."""
    try:
        client = get_client()

        # Navigate to profile
        client.navigate("https://www.reddit.com/user/me")
        human_delay(2)

        # Extract karma using JavaScript - target specific Reddit elements
        js_karma = """
        (() => {
            let postKarma = '';
            let commentKarma = '';
            let totalKarma = '';
            let username = '';
            let cakeDay = '';

            // Get username from h1 or profile header
            const h1 = document.querySelector('h1');
            if (h1) {
                const text = h1.textContent?.trim() || '';
                if (text && !text.startsWith('r/')) {
                    username = text;
                }
            }

            // Method 1: Look for karma in profile sidebar/stats elements
            // Reddit often puts karma in elements with data attributes or specific classes
            const allElements = document.querySelectorAll('*');
            for (const el of allElements) {
                const text = el.textContent?.trim() || '';

                // Look for "X post karma" pattern (with flexible whitespace)
                if (!postKarma && /^\\s*\\d+\\s+post\\s+karma/i.test(text)) {
                    const match = text.match(/^\\s*(\\d+)\\s+post/i);
                    if (match) postKarma = match[1];
                }

                // Look for "X comment karma" pattern
                if (!commentKarma && /^\\s*\\d+\\s+comment\\s+karma/i.test(text)) {
                    const match = text.match(/^\\s*(\\d+)\\s+comment/i);
                    if (match) commentKarma = match[1];
                }
            }

            // Method 2: Parse full page text with flexible patterns
            const pageText = document.body.innerText || '';

            if (!postKarma) {
                // Try various patterns for post karma
                const patterns = [
                    /(\\d+)\\s+post\\s+karma/i,
                    /(\\d+)\\s+Post\\s+Karma/,
                    /Post\\s+karma[:\\s]+(\\d+)/i
                ];
                for (const pat of patterns) {
                    const m = pageText.match(pat);
                    if (m) { postKarma = m[1]; break; }
                }
            }

            if (!commentKarma) {
                const patterns = [
                    /(\\d+)\\s+comment\\s+karma/i,
                    /(\\d+)\\s+Comment\\s+Karma/,
                    /Comment\\s+karma[:\\s]+(\\d+)/i
                ];
                for (const pat of patterns) {
                    const m = pageText.match(pat);
                    if (m) { commentKarma = m[1]; break; }
                }
            }

            // Method 3: Look for the main Karma stat (total)
            // Format is often "24\\n\\nKarma" or just a big number near "Karma"
            const karmaMatch = pageText.match(/(\\d+)\\s*\\n+\\s*Karma/);
            if (karmaMatch) {
                totalKarma = karmaMatch[1];
            }

            // Match cake day
            const cakeMatch = pageText.match(/Cake\\s*day[:\\s]+([A-Za-z]+\\s+\\d+,?\\s*\\d*)/i);
            if (cakeMatch) {
                cakeDay = cakeMatch[1];
            }

            return JSON.stringify({
                username: username,
                postKarma: postKarma,
                commentKarma: commentKarma,
                totalKarma: totalKarma,
                cakeDay: cakeDay
            });
        })()
        """

        result = client.evaluate(js_karma)
        karma_data = json.loads(result.get("result", "{}"))

        if karma_data.get("username"):
            console.print(f"\n[bold]u/{karma_data['username']}[/bold]")

        post_k = karma_data.get("postKarma", "")
        comment_k = karma_data.get("commentKarma", "")
        total_k = karma_data.get("totalKarma", "")

        if post_k or comment_k:
            total = int(post_k or 0) + int(comment_k or 0)
            console.print(f"Total Karma: [green]{total}[/green]")
            console.print(f"  Post Karma: {post_k or '0'}")
            console.print(f"  Comment Karma: {comment_k or '0'}")
        elif total_k:
            console.print(f"Total Karma: [green]{total_k}[/green]")
            console.print("  (Detailed breakdown not available)")
        else:
            warn("Could not extract karma. Try viewing your profile in the browser.")

        if karma_data.get("cakeDay"):
            console.print(f"Cake Day: {karma_data['cakeDay']}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Navigation Commands
# =============================================================================

@app.command()
def goto(url: str = typer.Argument(..., help="URL to navigate to")):
    """Navigate to a Reddit URL."""
    try:
        client = get_client()

        # Ensure URL is a Reddit URL
        if not url.startswith("http"):
            if url.startswith("r/"):
                url = f"{RedditURLs.BASE}/{url}"
            elif url.startswith("u/") or url.startswith("user/"):
                url = f"{RedditURLs.BASE}/{url}"
            else:
                url = f"{RedditURLs.BASE}/r/{url}"

        client.navigate(url)
        success(f"Navigated to {url}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Reading Commands
# =============================================================================

@app.command()
def feed(
    subreddit: str = typer.Argument("home", help="Subreddit name (or 'home' for front page)"),
    sort: str = typer.Option("hot", help="Sort: hot, new, top, rising"),
    limit: int = typer.Option(10, help="Number of posts to show"),
):
    """View subreddit feed."""
    try:
        client = get_client()

        # Navigate to subreddit
        if subreddit.lower() == "home":
            url = RedditURLs.home()
            display_name = "Front Page"
        else:
            url = RedditURLs.subreddit(subreddit, sort)
            display_name = f"r/{subreddit}"

        client.navigate(url)
        human_delay(2)  # Wait for page load

        # Extract posts using JavaScript
        js_posts = """
        (() => {
            const posts = document.querySelectorAll("shreddit-post");
            const data = [];
            for (const post of posts) {
                const title = post.getAttribute("post-title") || "";
                const subreddit = post.getAttribute("subreddit-prefixed-name") || "";
                const author = post.getAttribute("author") || "";
                const score = post.getAttribute("score") || "0";
                const comments = post.getAttribute("comment-count") || "0";
                const permalink = post.getAttribute("permalink") || "";
                const isPromoted = post.getAttribute("is-promoted") === "true";

                // Skip promoted posts
                if (title && !isPromoted) {
                    data.push({
                        title: title,
                        subreddit: subreddit,
                        author: author,
                        score: score,
                        comments: comments,
                        url: permalink
                    });
                }
            }
            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_posts)
        posts_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]{display_name}[/bold] - {sort}\n")

        if posts_data:
            # Output based on format
            if config.format == "json":
                console.print_json(json.dumps(posts_data[:limit]))
            else:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Title", width=45, no_wrap=True, overflow="ellipsis")
                table.add_column("Subreddit", width=16, no_wrap=True, overflow="ellipsis")
                table.add_column("Pts", width=5, justify="right")
                table.add_column("Cmt", width=4, justify="right")

                for i, p in enumerate(posts_data[:limit], 1):
                    table.add_row(
                        str(i),
                        p["title"],
                        p["subreddit"],
                        p["score"],
                        p["comments"]
                    )
                console.print(table)
        else:
            console.print("No posts found.")
            if config.verbose:
                snapshot = client.snapshot()
                console.print(snapshot.get("snapshot", ""))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def post(
    url_or_id: str = typer.Argument(..., help="Post URL or ID"),
):
    """View a Reddit post."""
    try:
        client = get_client()

        # Determine URL
        if url_or_id.startswith("http"):
            url = url_or_id
        else:
            # Assume it's a post ID
            url = RedditURLs.post_by_id(url_or_id)

        client.navigate(url)
        human_delay(3)  # Give more time for Reddit to load

        # Get page info
        info = client.info()
        page_title = info.get("title", "")

        # Get snapshot
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Extract post content using JavaScript
        js_code = """
        (() => {
            // Get post title - try multiple selectors
            let title = '';
            const h1 = document.querySelector('h1');
            if (h1) title = h1.textContent?.trim() || '';
            if (!title) {
                const titleEl = document.querySelector('[data-testid="post-title"]');
                if (titleEl) title = titleEl.textContent?.trim() || '';
            }
            if (!title) {
                const shredditTitle = document.querySelector('shreddit-post')?.getAttribute('post-title');
                if (shredditTitle) title = shredditTitle;
            }

            // Get author
            let author = '';
            const authorLink = document.querySelector('a[data-testid="post_author_link"]');
            if (authorLink) {
                author = authorLink.textContent?.trim() || '';
            }
            if (!author) {
                const oldAuthor = document.querySelector('.author');
                if (oldAuthor) author = oldAuthor.textContent?.trim() || '';
            }
            if (!author) {
                // Try shreddit-post attribute
                const shredditPost = document.querySelector('shreddit-post');
                if (shredditPost) {
                    author = shredditPost.getAttribute('author') || '';
                }
            }

            // Get subreddit
            let subreddit = '';
            const subLink = document.querySelector('a[data-testid="subreddit-name"]');
            if (subLink) {
                subreddit = subLink.textContent?.trim() || '';
            }
            if (!subreddit) {
                const shredditPost = document.querySelector('shreddit-post');
                if (shredditPost) {
                    subreddit = shredditPost.getAttribute('subreddit-prefixed-name') || '';
                }
            }

            // Get post body/content
            let body = '';
            // New Reddit text post
            const textBody = document.querySelector('div[slot="text-body"]');
            if (textBody) {
                body = textBody.textContent?.trim() || '';
            }
            // Old Reddit
            if (!body) {
                const usertext = document.querySelector('.usertext-body .md');
                if (usertext) body = usertext.textContent?.trim() || '';
            }
            // Shreddit post
            if (!body) {
                const shredditBody = document.querySelector('shreddit-post div[slot="text-body"]');
                if (shredditBody) body = shredditBody.textContent?.trim() || '';
            }

            // Get link URL if it's a link post
            let linkUrl = '';
            const linkPost = document.querySelector('a[data-testid="outbound-link"]');
            if (linkPost) {
                linkUrl = linkPost.getAttribute('href') || '';
            }

            // Get score
            let score = '';
            const scoreEl = document.querySelector('shreddit-post')?.getAttribute('score');
            if (scoreEl) score = scoreEl;

            // Get comment count
            let comments = '';
            const commentsEl = document.querySelector('shreddit-post')?.getAttribute('comment-count');
            if (commentsEl) comments = commentsEl;

            return JSON.stringify({
                title: title,
                author: author,
                subreddit: subreddit,
                body: body,
                linkUrl: linkUrl,
                score: score,
                comments: comments
            });
        })()
        """

        try:
            result = client.evaluate(js_code)
            result_str = result.get("result", "{}")
            post_data = json.loads(result_str) if isinstance(result_str, str) else result_str

            # Display post info
            title = post_data.get("title") or page_title.split(" : ")[0]
            console.print(f"\n[bold]{title}[/bold]")

            if post_data.get("subreddit"):
                console.print(f"[cyan]{post_data['subreddit']}[/cyan]", end="")
            if post_data.get("author"):
                console.print(f" | u/{post_data['author']}", end="")
            if post_data.get("score"):
                console.print(f" | {post_data['score']} points", end="")
            if post_data.get("comments"):
                console.print(f" | {post_data['comments']} comments", end="")
            console.print()  # newline

            if post_data.get("linkUrl"):
                console.print(f"\n[blue]Link:[/blue] {post_data['linkUrl']}")

            if post_data.get("body"):
                console.print(f"\n{post_data['body']}")
            elif not post_data.get("linkUrl"):
                # No body and no link - might be image/video post
                console.print("\n[dim](Image/video post - view in browser)[/dim]")

        except (BrowserError, json.JSONDecodeError, KeyError, TypeError) as e:
            if config.verbose:
                error(f"Content extraction failed: {e}")
            # Fallback: just show the page title
            console.print(f"\n[bold]{page_title}[/bold]")
            console.print("[dim]Content extraction failed. View in browser.[/dim]")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Writing Commands
# =============================================================================

@app.command()
def create(
    subreddit: str = typer.Argument(..., help="Subreddit to post in (e.g. 'python')"),
    title: str = typer.Option(..., "--title", "-t", help="Post title"),
    body: str = typer.Option("", "--body", "-b", help="Post body text"),
    url: str = typer.Option("", "--url", "-u", help="Link URL (for link posts)"),
    flair: str = typer.Option("", "--flair", "-f", help="Post flair text"),
):
    """Create a new post in a subreddit."""
    try:
        client = get_client()

        if not title:
            error("Post title is required")
            raise typer.Exit(1)

        # Navigate to submit page
        submit_url = RedditURLs.submit(subreddit)
        client.navigate(submit_url)
        human_delay(3)

        # Get snapshot to find form elements
        snapshot_data = client.snapshot()
        snapshot_text = snapshot_data.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # If this is a link post, select the Link tab first
        if url:
            lines = snapshot_text.split('\n')
            for line in lines:
                if 'link' in line.lower() and ('tab' in line.lower() or 'button' in line.lower()):
                    match = re.search(r'\[ref=(\w+)\]', line)
                    if match:
                        client.click(match.group(1))
                        human_delay(1)
                        # Refresh snapshot after tab switch
                        snapshot_data = client.snapshot()
                        snapshot_text = snapshot_data.get("snapshot", "")
                        break

        # Find and fill title input
        lines = snapshot_text.split('\n')
        title_ref = None
        for line in lines:
            if 'title' in line.lower() and ('textbox' in line.lower() or 'textarea' in line.lower() or 'input' in line.lower()):
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    title_ref = match.group(1)
                    break

        if not title_ref:
            error("Could not find title input. Try using 'cc-reddit snapshot' to see page elements.")
            raise typer.Exit(1)

        client.click(title_ref)
        human_delay(0.5)
        type_slowly(client, title_ref, title)
        human_delay(0.5)

        # Fill URL if link post
        if url:
            snapshot_data = client.snapshot()
            snapshot_text = snapshot_data.get("snapshot", "")
            lines = snapshot_text.split('\n')
            url_ref = None
            for line in lines:
                if 'url' in line.lower() and ('textbox' in line.lower() or 'input' in line.lower()):
                    match = re.search(r'\[ref=(\w+)\]', line)
                    if match:
                        url_ref = match.group(1)
                        break

            if url_ref:
                client.click(url_ref)
                human_delay(0.3)
                type_slowly(client, url_ref, url)
                human_delay(0.5)
            else:
                warn("Could not find URL input field")

        # Fill body if text post
        if body and not url:
            snapshot_data = client.snapshot()
            snapshot_text = snapshot_data.get("snapshot", "")
            lines = snapshot_text.split('\n')
            body_ref = None
            for line in lines:
                if ('textbox' in line.lower() or 'contenteditable' in line.lower()) and 'title' not in line.lower():
                    match = re.search(r'\[ref=(\w+)\]', line)
                    if match:
                        body_ref = match.group(1)
                        break

            if body_ref:
                client.click(body_ref)
                human_delay(0.3)
                type_slowly(client, body_ref, body)
                human_delay(0.5)
            else:
                warn("Could not find body text input")

        # Select flair if specified
        if flair:
            snapshot_data = client.snapshot()
            snapshot_text = snapshot_data.get("snapshot", "")
            lines = snapshot_text.split('\n')
            flair_ref = None
            for line in lines:
                if 'flair' in line.lower() and ('button' in line.lower() or 'select' in line.lower()):
                    match = re.search(r'\[ref=(\w+)\]', line)
                    if match:
                        flair_ref = match.group(1)
                        break

            if flair_ref:
                client.click(flair_ref)
                human_delay(1)
                # Look for matching flair option
                snapshot_data = client.snapshot()
                snapshot_text = snapshot_data.get("snapshot", "")
                lines = snapshot_text.split('\n')
                for line in lines:
                    if flair.lower() in line.lower():
                        match = re.search(r'\[ref=(\w+)\]', line)
                        if match:
                            client.click(match.group(1))
                            human_delay(0.5)
                            break

        # Find and click submit/post button
        human_delay(1)
        snapshot_data = client.snapshot()
        snapshot_text = snapshot_data.get("snapshot", "")
        lines = snapshot_text.split('\n')
        submit_ref = None
        for line in lines:
            lower_line = line.lower()
            if ('post' in lower_line or 'submit' in lower_line) and 'button' in lower_line:
                # Skip "create post" navigation buttons
                if 'create' in lower_line:
                    continue
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    submit_ref = match.group(1)

        if submit_ref:
            client.click(submit_ref)
            human_delay(2)
            success(f"Post submitted to r/{subreddit}")
        else:
            warn("Could not find submit button. Post may not have been created.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def comment(
    post_url: str = typer.Argument(..., help="Post URL to comment on"),
    text: str = typer.Option(..., "--text", "-t", help="Comment text"),
):
    """Add a comment to a post."""
    try:
        client = get_client()

        # Navigate to post if not already there
        info = client.info()
        if post_url not in info.get("url", ""):
            client.navigate(post_url)
            human_delay(2)

        # Get snapshot to find comment input
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Find comment input ref
        # Look for textbox or contenteditable element
        lines = snapshot_text.split('\n')
        comment_ref = None
        for line in lines:
            if 'textbox' in line.lower() or 'comment' in line.lower():
                # Extract ref from line like "- textbox 'Add a comment' [ref=e5]"
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    comment_ref = match.group(1)
                    if config.verbose:
                        console.print(f"Found comment input: {comment_ref}")
                    break

        if not comment_ref:
            error("Could not find comment input. Try using 'cc-reddit snapshot' to see page elements.")
            raise typer.Exit(1)

        # Type comment (human-like speed)
        client.click(comment_ref)
        human_delay(0.5)
        type_slowly(client, comment_ref, text)

        # Find and click submit button
        snapshot = client.snapshot()
        lines = snapshot.get("snapshot", "").split('\n')
        submit_ref = None
        for line in lines:
            if 'submit' in line.lower() or 'comment' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    submit_ref = match.group(1)

        if submit_ref:
            client.click(submit_ref)
            success("Comment submitted")
        else:
            warn("Could not find submit button. Comment may not have been posted.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def reply(
    comment_url: str = typer.Argument(..., help="Comment URL to reply to"),
    text: str = typer.Option(..., "--text", "-t", help="Reply text"),
):
    """Reply to a comment."""
    try:
        client = get_client()

        # Navigate to comment
        client.navigate(comment_url)
        human_delay(2)

        # Get snapshot to find reply button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Find and click Reply button
        lines = snapshot_text.split('\n')
        reply_ref = None
        for line in lines:
            if 'reply' in line.lower() and 'button' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    reply_ref = match.group(1)
                    break

        if not reply_ref:
            error("Could not find reply button")
            raise typer.Exit(1)

        # Click reply to open input
        client.click(reply_ref)
        human_delay(1)

        # Get new snapshot to find text input
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")
        lines = snapshot_text.split('\n')

        # Find textbox for reply
        textbox_ref = None
        for line in lines:
            if 'textbox' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    textbox_ref = match.group(1)
                    break

        if not textbox_ref:
            error("Could not find reply text input")
            raise typer.Exit(1)

        # Type reply (human-like speed)
        client.click(textbox_ref)
        human_delay(0.3)
        type_slowly(client, textbox_ref, text)
        human_delay(0.5)

        # Find and click submit/comment button
        snapshot = client.snapshot()
        lines = snapshot.get("snapshot", "").split('\n')
        submit_ref = None
        for line in lines:
            # Look for Comment button (submit for replies)
            if ('comment' in line.lower() or 'submit' in line.lower()) and 'button' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    submit_ref = match.group(1)

        if submit_ref:
            client.click(submit_ref)
            success("Reply submitted")
        else:
            warn("Could not find submit button. Reply may not have been posted.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Voting Commands
# =============================================================================

@app.command()
def upvote(
    url_or_id: str = typer.Argument(..., help="Post or comment URL/ID"),
):
    """Upvote a post or comment."""
    try:
        client = get_client()

        # Navigate if URL provided
        if url_or_id.startswith("http"):
            client.navigate(url_or_id)
            human_delay(2)

        # Get snapshot
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Find upvote button
        lines = snapshot_text.split('\n')
        upvote_ref = None
        for line in lines:
            if 'upvote' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    upvote_ref = match.group(1)
                    break

        if upvote_ref:
            client.click(upvote_ref)
            success("Upvoted")
        else:
            error("Could not find upvote button")
            raise typer.Exit(1)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def downvote(
    url_or_id: str = typer.Argument(..., help="Post or comment URL/ID"),
):
    """Downvote a post or comment."""
    try:
        client = get_client()

        # Navigate if URL provided
        if url_or_id.startswith("http"):
            client.navigate(url_or_id)
            human_delay(2)

        # Get snapshot
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        # Find downvote button
        lines = snapshot_text.split('\n')
        downvote_ref = None
        for line in lines:
            if 'downvote' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    downvote_ref = match.group(1)
                    break

        if downvote_ref:
            client.click(downvote_ref)
            success("Downvoted")
        else:
            error("Could not find downvote button")
            raise typer.Exit(1)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Subreddit Commands
# =============================================================================

@app.command()
def join(
    subreddit: str = typer.Argument(..., help="Subreddit to join"),
):
    """Join a subreddit."""
    try:
        client = get_client()

        # Navigate to subreddit
        url = RedditURLs.subreddit(subreddit)
        client.navigate(url)
        human_delay(2)

        # Get snapshot
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        # Find join button
        lines = snapshot_text.split('\n')
        join_ref = None
        for line in lines:
            if 'join' in line.lower() and 'button' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    join_ref = match.group(1)
                    break

        if join_ref:
            client.click(join_ref)
            success(f"Joined r/{subreddit}")
        else:
            warn(f"Could not find join button. You may already be a member of r/{subreddit}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def leave(
    subreddit: str = typer.Argument(..., help="Subreddit to leave"),
):
    """Leave a subreddit."""
    try:
        client = get_client()

        # Navigate to subreddit
        url = RedditURLs.subreddit(subreddit)
        client.navigate(url)
        human_delay(2)

        # Get snapshot
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        # Find leave/joined button
        lines = snapshot_text.split('\n')
        leave_ref = None
        for line in lines:
            if 'joined' in line.lower() or 'leave' in line.lower():
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    leave_ref = match.group(1)
                    break

        if leave_ref:
            client.click(leave_ref)
            success(f"Left r/{subreddit}")
        else:
            warn(f"Could not find leave button. You may not be a member of r/{subreddit}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Utility Commands
# =============================================================================

@app.command()
def snapshot():
    """Get current page snapshot (for debugging)."""
    try:
        client = get_client()
        result = client.snapshot()
        console.print(result.get("snapshot", "No snapshot available"))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def screenshot(
    output: str = typer.Option("reddit_screenshot.png", help="Output filename"),
):
    """Take a screenshot of the current page."""
    try:
        client = get_client()
        result = client.screenshot()

        # Save base64 image
        import base64
        image_data = result.get("screenshot", "")
        if image_data:
            with open(output, "wb") as f:
                f.write(base64.b64decode(image_data))
            success(f"Screenshot saved to {output}")
        else:
            error("No screenshot data received")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

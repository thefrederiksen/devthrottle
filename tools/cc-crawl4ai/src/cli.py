"""CLI for cc-crawl4ai - AI-ready web crawler."""

import sys
import asyncio
from pathlib import Path
from typing import Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from .crawler import CrawlResult
    from .batch import BatchResult

# Fix Windows console encoding for Crawl4AI Unicode output
if sys.platform == 'win32':
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    if hasattr(sys.stderr, 'reconfigure'):
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')

import typer
from rich.console import Console
from rich.table import Table
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TaskProgressColumn
from crawl4ai import AsyncWebCrawler, BrowserConfig

try:
    from . import __version__
    from .crawler import CrawlOptions, crawl
    from .batch import crawl_batch, load_urls_from_file
    from .sessions import SessionManager
except ImportError:
    from src import __version__
    from src.crawler import CrawlOptions, crawl
    from src.batch import crawl_batch, load_urls_from_file
    from src.sessions import SessionManager

app = typer.Typer(
    name="cc-crawl4ai",
    help="AI-ready web crawler: crawl pages to clean markdown, batch processing, sessions.",
    add_completion=False,
)
session_app = typer.Typer(help="Manage browser sessions")
app.add_typer(session_app, name="session")

console = Console()


def version_callback(value: bool) -> None:
    """Handle --version flag."""
    if value:
        console.print(f"cc-crawl4ai version {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
) -> None:
    """AI-ready web crawler with LLM-friendly output."""
    pass


# =============================================================================
# CRAWL COMMAND HELPERS
# =============================================================================

def _parse_viewport(viewport: str) -> tuple[int, int]:
    """Parse viewport string into width and height."""
    try:
        vw, vh = viewport.split("x")
        return int(vw), int(vh)
    except ValueError:
        console.print("[red]Error:[/red] Invalid viewport format. Use WIDTHxHEIGHT (e.g., 1920x1080)")
        raise typer.Exit(1)


def _display_crawl_extras(result: "CrawlResult", show_links: bool, show_media: bool) -> None:
    """Display additional crawl result info (status, screenshot, pdf, links, media)."""
    if result.status_code:
        console.print(f"[cyan]Status:[/cyan] {result.status_code}")

    if result.screenshot_path:
        console.print(f"[cyan]Screenshot:[/cyan] {result.screenshot_path}")

    if result.pdf_path:
        console.print(f"[cyan]PDF:[/cyan] {result.pdf_path}")

    if show_links and result.links:
        console.print(f"\n[cyan]Links ({len(result.links)}):[/cyan]")
        for link in result.links[:20]:
            console.print(f"  {link}")
        if len(result.links) > 20:
            console.print(f"  ... and {len(result.links) - 20} more")

    if show_media and result.media:
        console.print(f"\n[cyan]Media ({len(result.media)}):[/cyan]")
        for media_url in result.media[:10]:
            console.print(f"  {media_url}")
        if len(result.media) > 10:
            console.print(f"  ... and {len(result.media) - 10} more")


# =============================================================================
# CRAWL COMMAND
# =============================================================================

@app.command("crawl")
def crawl_cmd(
    url: str = typer.Argument(..., help="URL to crawl"),
    # Output options
    output: Optional[Path] = typer.Option(None, "-o", "--output", help="Output file path"),
    format: str = typer.Option("markdown", "-f", "--format", help="Output format: markdown, json, html, raw, cleaned"),
    fit: bool = typer.Option(False, "--fit", help="Use fit markdown (noise filtered)"),
    query: Optional[str] = typer.Option(None, "-q", "--query", help="BM25 content filter query"),
    # Browser options
    browser: str = typer.Option("chromium", "-b", "--browser", help="Browser: chromium, firefox, webkit"),
    stealth: bool = typer.Option(False, "--stealth", help="Enable stealth mode (evade bot detection)"),
    proxy: Optional[str] = typer.Option(None, "--proxy", help="Proxy URL (http://user:pass@host:port)"),
    viewport: str = typer.Option("1920x1080", "--viewport", help="Viewport size (WIDTHxHEIGHT)"),
    timeout: int = typer.Option(30000, "--timeout", help="Page timeout in milliseconds"),
    headless: bool = typer.Option(True, "--headless/--no-headless", help="Run browser headless"),
    text_only: bool = typer.Option(False, "--text-only", help="Disable images for faster text-only crawl"),
    # Session options
    session: Optional[str] = typer.Option(None, "-s", "--session", help="Use saved session"),
    # Dynamic content options
    wait_for: Optional[str] = typer.Option(None, "--wait-for", help="CSS selector to wait for"),
    wait_until: str = typer.Option("domcontentloaded", "--wait-until", help="Wait condition: domcontentloaded or networkidle"),
    scroll: bool = typer.Option(False, "--scroll", help="Scroll full page before extraction"),
    scroll_delay: int = typer.Option(500, "--scroll-delay", help="Delay between scrolls in ms"),
    lazy_load: bool = typer.Option(False, "--lazy-load", help="Wait for lazy-loaded images"),
    js: Optional[str] = typer.Option(None, "--js", help="JavaScript to execute before extraction"),
    remove_overlays: bool = typer.Option(False, "--remove-overlays", help="Remove popups and cookie banners"),
    # Extraction options
    css: Optional[str] = typer.Option(None, "--css", help="CSS selector for extraction"),
    xpath: Optional[str] = typer.Option(None, "--xpath", help="XPath for extraction"),
    schema: Optional[Path] = typer.Option(None, "--schema", help="JSON schema file for structured extraction"),
    llm_extract: bool = typer.Option(False, "--llm-extract", help="Use LLM for extraction"),
    llm_model: str = typer.Option("gpt-4o-mini", "--llm-model", help="LLM model for extraction"),
    llm_prompt: Optional[str] = typer.Option(None, "--llm-prompt", help="Prompt for LLM extraction"),
    # Media options
    screenshot: bool = typer.Option(False, "--screenshot", help="Capture screenshot"),
    screenshot_path: Optional[Path] = typer.Option(None, "--screenshot-path", help="Screenshot output path"),
    pdf: bool = typer.Option(False, "--pdf", help="Generate PDF of page"),
    pdf_path: Optional[Path] = typer.Option(None, "--pdf-path", help="PDF output path"),
    extract_media: bool = typer.Option(False, "--extract-media", help="Extract images/video URLs"),
    # Cache options
    cache: str = typer.Option("on", "--cache", help="Cache mode: on, off, refresh, read-only"),
    # Link options
    links: bool = typer.Option(False, "--links", help="Extract and show links"),
    internal_links_only: bool = typer.Option(False, "--internal-links-only", help="Exclude external links"),
) -> None:
    """Crawl a single URL and extract content."""
    viewport_width, viewport_height = _parse_viewport(viewport)

    options = CrawlOptions(
        output_format=format, fit_markdown=fit, query=query, browser=browser,
        stealth=stealth, proxy=proxy, viewport_width=viewport_width,
        viewport_height=viewport_height, headless=headless, timeout=timeout,
        text_only=text_only, session=session, wait_for=wait_for,
        wait_until=wait_until, scroll=scroll, scroll_delay=scroll_delay,
        lazy_load=lazy_load, execute_js=js, remove_overlays=remove_overlays,
        css_selector=css, xpath=xpath, schema_path=str(schema) if schema else None,
        llm_extract=llm_extract, llm_model=llm_model, llm_prompt=llm_prompt,
        screenshot=screenshot, screenshot_path=str(screenshot_path) if screenshot_path else None,
        pdf=pdf, pdf_path=str(pdf_path) if pdf_path else None,
        extract_media=extract_media, cache=cache, extract_links=links,
        internal_links_only=internal_links_only,
    )

    with console.status("[blue]Crawling...[/blue]"):
        result = crawl(url, options)

    if not result.success:
        console.print(f"[red]Error:[/red] {result.error}")
        raise typer.Exit(1)

    if output:
        output.write_text(result.content, encoding="utf-8")
        console.print(f"[green]Saved:[/green] {output}")
    else:
        console.print(result.content)

    _display_crawl_extras(result, links, extract_media)


# =============================================================================
# BATCH COMMAND HELPERS
# =============================================================================

def _display_batch_summary(result: "BatchResult", output_dir: Path) -> None:
    """Display batch crawl summary table."""
    table = Table(title="Batch Crawl Results")
    table.add_column("Metric", style="cyan")
    table.add_column("Value")

    table.add_row("Total URLs", str(result.total))
    table.add_row("Successful", f"[green]{result.successful}[/green]")
    table.add_row("Failed", f"[red]{result.failed}[/red]" if result.failed > 0 else "0")
    table.add_row("Duration", f"{result.duration_seconds:.1f}s")
    table.add_row("Output Directory", str(output_dir))

    console.print(table)

    if result.failed > 0:
        console.print("\n[red]Failed URLs:[/red]")
        for r in result.results:
            if not r.success:
                console.print(f"  {r.url}: {r.error}")


# =============================================================================
# BATCH COMMAND
# =============================================================================

@app.command("batch")
def batch_cmd(
    urls_file: Path = typer.Argument(..., help="File containing URLs (one per line)", exists=True),
    output_dir: Path = typer.Option(..., "-o", "--output-dir", help="Output directory for results"),
    format: str = typer.Option("markdown", "-f", "--format", help="Output format: markdown, json, html, raw, cleaned"),
    fit: bool = typer.Option(False, "--fit", help="Use fit markdown (noise filtered)"),
    query: Optional[str] = typer.Option(None, "-q", "--query", help="BM25 content filter query"),
    parallel: int = typer.Option(5, "-p", "--parallel", help="Number of parallel crawls"),
    browser: str = typer.Option("chromium", "-b", "--browser", help="Browser: chromium, firefox, webkit"),
    stealth: bool = typer.Option(False, "--stealth", help="Enable stealth mode"),
    proxy: Optional[str] = typer.Option(None, "--proxy", help="Proxy URL"),
    timeout: int = typer.Option(30000, "--timeout", help="Page timeout in milliseconds"),
    text_only: bool = typer.Option(False, "--text-only", help="Disable images for faster crawl"),
    wait_for: Optional[str] = typer.Option(None, "--wait-for", help="CSS selector to wait for"),
    wait_until: str = typer.Option("domcontentloaded", "--wait-until", help="Wait condition: domcontentloaded or networkidle"),
    scroll: bool = typer.Option(False, "--scroll", help="Scroll full page before extraction"),
    remove_overlays: bool = typer.Option(False, "--remove-overlays", help="Remove popups and cookie banners"),
    screenshot: bool = typer.Option(False, "--screenshot", help="Capture screenshots"),
    cache: str = typer.Option("on", "--cache", help="Cache mode: on, off, refresh, read-only"),
    links: bool = typer.Option(False, "--links", help="Extract links"),
) -> None:
    """Crawl multiple URLs in parallel."""
    output_dir.mkdir(parents=True, exist_ok=True)

    urls = load_urls_from_file(urls_file)
    if not urls:
        console.print("[red]Error:[/red] No URLs found in file")
        raise typer.Exit(1)

    console.print(f"[blue]Crawling {len(urls)} URLs with {parallel} parallel workers...[/blue]")

    options = CrawlOptions(
        output_format=format, fit_markdown=fit, query=query, browser=browser,
        stealth=stealth, proxy=proxy, timeout=timeout, text_only=text_only,
        wait_for=wait_for, wait_until=wait_until, scroll=scroll,
        remove_overlays=remove_overlays, screenshot=screenshot, cache=cache,
        extract_links=links,
    )

    with Progress(
        SpinnerColumn(), TextColumn("[progress.description]{task.description}"),
        BarColumn(), TaskProgressColumn(), console=console,
    ) as progress:
        task = progress.add_task("Crawling", total=len(urls))

        def on_progress(completed: int, total: int, url: str) -> None:
            progress.update(task, completed=completed, description=f"[{completed}/{total}] {url[:50]}...")

        result = crawl_batch(
            urls=urls, options=options, parallel=parallel,
            output_dir=output_dir, progress_callback=on_progress,
        )

    console.print()
    _display_batch_summary(result, output_dir)


# =============================================================================
# SESSION COMMAND HELPERS
# =============================================================================

def _run_interactive_session(manager: SessionManager, name: str, url: str, browser: str) -> None:
    """Run interactive browser session for manual login."""
    console.print("[blue]Opening browser for manual login...[/blue]")
    console.print("[yellow]Close the browser when done to save session state.[/yellow]")

    async def interactive_session() -> None:
        config = BrowserConfig(
            browser_type=browser, headless=False,
            user_data_dir=str(manager.get_profile_path(name)),
        )
        async with AsyncWebCrawler(config=config) as crawler:
            await crawler.arun(url=url)
            console.print("[blue]Waiting for you to close the browser...[/blue]")
            while True:
                try:
                    await asyncio.sleep(1)
                except (asyncio.CancelledError, KeyboardInterrupt):
                    break

    try:
        asyncio.run(interactive_session())
    except KeyboardInterrupt:
        pass

    console.print(f"[green]Session saved:[/green] {name}")


# =============================================================================
# SESSION COMMANDS
# =============================================================================

@session_app.command("list")
def session_list() -> None:
    """List all saved sessions."""
    manager = SessionManager()
    sessions = manager.list_sessions()

    if not sessions:
        console.print("[yellow]No sessions found.[/yellow]")
        console.print("Create one with: cc-crawl4ai session create <name>")
        return

    table = Table(title="Saved Sessions")
    table.add_column("Name", style="cyan")
    table.add_column("Browser")
    table.add_column("Created")
    table.add_column("Last Used")
    table.add_column("Description")

    for s in sessions:
        table.add_row(s.name, s.browser, s.created_at[:10], s.last_used[:10], s.description or "-")

    console.print(table)


@session_app.command("create")
def session_create(
    name: str = typer.Argument(..., help="Session name"),
    url: Optional[str] = typer.Option(None, "-u", "--url", help="Initial URL to visit"),
    browser: str = typer.Option("chromium", "-b", "--browser", help="Browser: chromium, firefox, webkit"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="Session description"),
    interactive: bool = typer.Option(False, "-i", "--interactive", help="Open browser for manual login"),
) -> None:
    """Create a new session."""
    manager = SessionManager()

    if manager.exists(name):
        console.print(f"[red]Error:[/red] Session '{name}' already exists")
        raise typer.Exit(1)

    manager.create(name, url=url, browser=browser, description=description)
    console.print(f"[green]Created session:[/green] {name}")

    if interactive and url:
        _run_interactive_session(manager, name, url, browser)
    else:
        console.print(f"Use with: cc-crawl4ai crawl <url> --session {name}")


@session_app.command("delete")
def session_delete(
    name: str = typer.Argument(..., help="Session name"),
    force: bool = typer.Option(False, "-f", "--force", help="Skip confirmation"),
) -> None:
    """Delete a session."""
    manager = SessionManager()

    if not manager.exists(name):
        console.print(f"[red]Error:[/red] Session '{name}' not found")
        raise typer.Exit(1)

    if not force:
        confirm = typer.confirm(f"Delete session '{name}'?")
        if not confirm:
            raise typer.Abort()

    manager.delete(name)
    console.print(f"[green]Deleted:[/green] {name}")


@session_app.command("rename")
def session_rename(
    old_name: str = typer.Argument(..., help="Current session name"),
    new_name: str = typer.Argument(..., help="New session name"),
) -> None:
    """Rename a session."""
    manager = SessionManager()

    if not manager.exists(old_name):
        console.print(f"[red]Error:[/red] Session '{old_name}' not found")
        raise typer.Exit(1)

    if manager.exists(new_name):
        console.print(f"[red]Error:[/red] Session '{new_name}' already exists")
        raise typer.Exit(1)

    manager.rename(old_name, new_name)
    console.print(f"[green]Renamed:[/green] {old_name} -> {new_name}")


@session_app.command("info")
def session_info(name: str = typer.Argument(..., help="Session name")) -> None:
    """Show session details."""
    manager = SessionManager()
    info = manager.get(name)

    if not info:
        console.print(f"[red]Error:[/red] Session '{name}' not found")
        raise typer.Exit(1)

    table = Table(title=f"Session: {name}")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("Name", info.name)
    table.add_row("Browser", info.browser)
    table.add_row("Created", info.created_at)
    table.add_row("Last Used", info.last_used)
    table.add_row("URL", info.url or "-")
    table.add_row("Description", info.description or "-")
    table.add_row("Profile Path", str(manager.get_profile_path(name)))

    console.print(table)


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(app, "cc-crawl4ai", app=app)


if __name__ == "__main__":
    tool_main()

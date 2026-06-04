"""Orchestrates all data sources for person research."""

import concurrent.futures
import json
import time
from pathlib import Path
from typing import Optional

from rich.console import Console
from rich.table import Table

from cc_personresearch.browser_client import BrowserClient, BrowserError, ConnectionError, WorkspaceError
from cc_personresearch.models import PersonReport, SearchParams, SourceResult

# API sources (no browser needed)
from cc_personresearch.sources.gravatar import GravatarSource
from cc_personresearch.sources.github_search import GitHubSource
from cc_personresearch.sources.fec_donations import FECSource
from cc_personresearch.sources.sec_edgar import SECEdgarSource
from cc_personresearch.sources.wayback import WaybackSource
from cc_personresearch.sources.whois_lookup import WhoisSource

# Browser sources
from cc_personresearch.sources.google_dorking import GoogleDorkingSource
from cc_personresearch.sources.thatsthem import ThatSThemSource
from cc_personresearch.sources.truepeoplesearch import TruePeopleSearchSource
from cc_personresearch.sources.zabasearch import ZabaSearchSource
from cc_personresearch.sources.nuwber import NuwberSource
from cc_personresearch.sources.company_website import CompanyWebsiteSource
from cc_personresearch.sources.opencorporates import OpenCorporatesSource

# Tool sources
from cc_personresearch.sources.linkedin import LinkedInSource

console = Console()

# Source classes in execution order
API_SOURCES = [
    GravatarSource,
    GitHubSource,
    FECSource,
    SECEdgarSource,
    WaybackSource,
    WhoisSource,
]

BROWSER_SOURCES = [
    GoogleDorkingSource,
    CompanyWebsiteSource,
    ThatSThemSource,
    TruePeopleSearchSource,
    ZabaSearchSource,
    NuwberSource,
    OpenCorporatesSource,
]

TOOL_SOURCES = [
    LinkedInSource,
]


def _log(msg: str, verbose: bool):
    """Print status message if verbose."""
    if verbose:
        console.print(f"  {msg}")


def _try_connect_browser(connection: str, verbose: bool) -> Optional[BrowserClient]:
    """Try to connect to a browser connection with health check."""
    try:
        browser = BrowserClient(connection=connection)
        status = browser.status()
        _log(f"Connected to cc-browser on connection '{connection}'", verbose)
        return browser
    except (BrowserError, ConnectionError, WorkspaceError) as e:
        _log(f"Cannot connect to connection '{connection}': {e}", verbose)
        return None


def run_search(
    name: str,
    email: Optional[str] = None,
    location: Optional[str] = None,
    connection: Optional[str] = None,
    linkedin_connection: str = "linkedin",
    api_only: bool = False,
    skip_sources: Optional[list[str]] = None,
    verbose: bool = False,
) -> PersonReport:
    """Run all data sources and build a person report.

    Args:
        name: Person's full name.
        email: Email address (optional but recommended).
        location: Location hint (optional).
        connection: cc-browser connection for general browsing (auto-resolved if None).
        linkedin_connection: Connection for LinkedIn operations (default: "linkedin").
        api_only: If True, skip all browser and tool sources.
        skip_sources: List of source names to skip.
        verbose: Print progress messages.

    Returns:
        PersonReport with all collected data.
    """
    skip = set(skip_sources or [])

    report = PersonReport(
        search_params=SearchParams(name=name, email=email, location=location)
    )

    # --- Phase 1: API sources (parallel) ---
    console.print("[bold]Phase 1:[/bold] API sources (parallel)")
    api_instances = []
    for source_cls in API_SOURCES:
        src = source_cls(
            person_name=name, email=email, location=location, verbose=verbose
        )
        if src.name in skip:
            _log(f"Skipping {src.name}", verbose)
            report.add_result(SourceResult(source=src.name, status="skipped"))
            continue
        api_instances.append(src)

    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        futures = {executor.submit(src.run): src for src in api_instances}
        for future in concurrent.futures.as_completed(futures):
            src = futures[future]
            result = future.result()
            report.add_result(result)
            status_label = "OK" if result.status == "found" else result.status.upper()
            _log(
                f"[{status_label}] {src.name} ({result.query_time_ms}ms)",
                verbose,
            )
            # Collect discovered URLs
            for url in result.data.get("urls", []):
                report.add_url(url)

    if api_only:
        console.print("[dim]--api-only: skipping browser and tool sources[/dim]")
        return report

    # --- Phase 2: LinkedIn (via cc-browser - most valuable professional data) ---
    if "linkedin" not in skip:
        console.print("[bold]Phase 2:[/bold] LinkedIn (via cc-browser)")
        li_src = LinkedInSource(
            person_name=name,
            email=email,
            location=location,
            verbose=verbose,
            linkedin_connection=linkedin_connection,
        )
        result = li_src.run()
        report.add_result(result)
        status_label = "OK" if result.status == "found" else result.status.upper()
        _log(f"[{status_label}] linkedin ({result.query_time_ms}ms)", verbose)
        for url in result.data.get("urls", []):
            report.add_url(url)
    else:
        _log("Skipping linkedin", verbose)
        report.add_result(SourceResult(source="linkedin", status="skipped"))

    # --- Connect to browser for remaining sources ---
    browser = None

    # Try to connect (v2 auto-resolves via tool binding if connection is None)
    if connection is not None:
        browser = _try_connect_browser(connection, verbose)
    else:
        # Try auto-resolve - BrowserClient will find connection by toolBinding
        try:
            browser = BrowserClient()
            browser.status()
            _log(f"Auto-resolved browser connection: {browser.connection}", verbose)
        except (BrowserError, ConnectionError, WorkspaceError) as e:
            _log(f"Cannot auto-resolve browser connection: {e}", verbose)
            browser = None

    if not browser:
        console.print(
            "[bold yellow]WARNING:[/bold yellow] No browser connection available.\n"
            "Create one with: cc-browser connections add research --tool cc-personresearch\n"
            "Skipping all browser sources."
        )
        _mark_browser_sources_skipped(name, email, skip, report)
        return report

    try:
        # --- Phase 3: Google dorking (broadest web presence search) ---
        console.print("[bold]Phase 3:[/bold] Google dorking (browser)")
        _run_browser_sources(
            [GoogleDorkingSource], name, email, location, browser, skip, report, verbose
        )

        # --- Phase 4: Company website (role/title at their company) ---
        console.print("[bold]Phase 4:[/bold] Company website (browser)")
        _run_browser_sources(
            [CompanyWebsiteSource], name, email, location, browser, skip, report, verbose
        )

        # --- Phase 5: People-search sites (personal details) ---
        console.print("[bold]Phase 5:[/bold] People-search sites (browser)")
        _run_browser_sources(
            [ThatSThemSource, TruePeopleSearchSource, ZabaSearchSource, NuwberSource],
            name, email, location, browser, skip, report, verbose,
            delay_between=5,
        )

        # --- Phase 6: Corporate records ---
        console.print("[bold]Phase 6:[/bold] Corporate records (browser)")
        _run_browser_sources(
            [OpenCorporatesSource],
            name, email, location, browser, skip, report, verbose,
        )

    finally:
        browser.close()

    return report


def _mark_browser_sources_skipped(name, email, skip, report):
    """Mark all browser sources as skipped when no browser is available."""
    for source_cls in BROWSER_SOURCES:
        src = source_cls(person_name=name, email=email, browser=None)
        if src.name not in skip:
            report.add_result(SourceResult(
                source=src.name, status="skipped",
                error_message="cc-browser not available"
            ))


def _run_browser_sources(
    source_classes: list,
    name: str,
    email: Optional[str],
    location: Optional[str],
    browser: BrowserClient,
    skip: set,
    report: PersonReport,
    verbose: bool,
    delay_between: int = 0,
):
    """Run a list of browser sources sequentially."""
    for i, source_cls in enumerate(source_classes):
        src = source_cls(
            person_name=name,
            email=email,
            location=location,
            browser=browser,
            verbose=verbose,
        )
        if src.name in skip:
            _log(f"Skipping {src.name}", verbose)
            report.add_result(SourceResult(source=src.name, status="skipped"))
            continue

        result = src.run()
        report.add_result(result)
        status_label = "OK" if result.status == "found" else result.status.upper()
        _log(
            f"[{status_label}] {src.name} ({result.query_time_ms}ms)",
            verbose,
        )

        for url in result.data.get("urls", []):
            report.add_url(url)

        # Delay between sources to avoid rate limiting
        if delay_between > 0 and i < len(source_classes) - 1:
            _log(f"Waiting {delay_between}s before next source...", verbose)
            time.sleep(delay_between)


def print_summary(report: PersonReport) -> None:
    """Print a summary table of results."""
    table = Table(title="Person Research Results")
    table.add_column("Source", style="cyan")
    table.add_column("Status", style="bold")
    table.add_column("Time", justify="right")
    table.add_column("Details")

    for name, result in report.sources.items():
        if result.status == "found":
            status = "[green]FOUND[/green]"
        elif result.status == "not_found":
            status = "[dim]NOT FOUND[/dim]"
        elif result.status == "error":
            status = "[red]ERROR[/red]"
        else:
            status = "[yellow]SKIPPED[/yellow]"

        time_str = f"{result.query_time_ms}ms"
        details = result.error_message or ""
        if result.status == "found":
            data_keys = [k for k in result.data.keys() if k != "urls"]
            details = ", ".join(data_keys[:4])

        table.add_row(name, status, time_str, details)

    console.print(table)

    if report.discovered_urls:
        console.print(f"\n[bold]Discovered URLs ({len(report.discovered_urls)}):[/bold]")
        for url in report.discovered_urls:
            console.print(f"  {url}")

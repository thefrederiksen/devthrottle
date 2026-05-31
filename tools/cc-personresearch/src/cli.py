"""CLI entry point for cc-personresearch."""

import json
import os
import sys
from datetime import date
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

from src.models import PersonReport
from src.runner import run_search, print_summary, API_SOURCES, BROWSER_SOURCES, TOOL_SOURCES
from src.aggregator import aggregate

app = typer.Typer(
    name="cc-personresearch",
    help="Person research CLI - gather OSINT data from public sources.",
    no_args_is_help=True,
)

console = Console()


def _version_callback(value: bool) -> None:
    """Print version and exit. Eager so --version works without a subcommand."""
    if value:
        typer.echo("cc-personresearch version 0.1.0")
        raise typer.Exit()


@app.callback()
def _main(
    version: Optional[bool] = typer.Option(
        None, "--version",
        help="Show version and exit.",
        callback=_version_callback,
        is_eager=True,
    ),
) -> None:
    """Person research CLI - gather OSINT data from public sources."""


def get_default_output_dir(tool_name: str) -> Path:
    """Return ~/Documents/cc-director/<tool_name>/, creating it if needed."""
    home = Path(os.environ.get("USERPROFILE", "") or Path.home())
    out_dir = home / "Documents" / "cc-director" / tool_name
    out_dir.mkdir(parents=True, exist_ok=True)
    return out_dir


def get_default_output_path(person_name: str) -> Path:
    """Generate default output path from person name and today's date."""
    out_dir = get_default_output_dir("cc-personresearch")
    safe_name = "".join(c if c.isalnum() else "_" for c in person_name)
    today = date.today().isoformat()
    return out_dir / f"{safe_name}_{today}.json"


@app.command()
def search(
    name: str = typer.Option(..., "--name", "-n", help="Person's full name"),
    email: Optional[str] = typer.Option(None, "--email", "-e", help="Email address"),
    location: Optional[str] = typer.Option(None, "--location", "-l", help="Location hint"),
    output: Optional[str] = typer.Option(None, "--output", "-o", help="Output JSON file path"),
    connection: Optional[str] = typer.Option(None, "--connection", "-c",
                                            help="cc-browser connection for browsing (auto-detected if omitted)"),
    workspace: Optional[str] = typer.Option(None, "--workspace", "-w", hidden=True,
                                            help="Deprecated: use --connection"),
    linkedin_connection: str = typer.Option("linkedin", "--linkedin-connection",
                                            help="cc-browser connection for LinkedIn (default: linkedin)"),
    linkedin_workspace: Optional[str] = typer.Option(None, "--linkedin-workspace", hidden=True,
                                                     help="Deprecated: use --linkedin-connection"),
    api_only: bool = typer.Option(False, "--api-only", help="Only run API sources (no browser)"),
    skip: Optional[str] = typer.Option(None, "--skip", help="Comma-separated source names to skip"),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Show detailed progress"),
) -> None:
    """Search for a person across multiple public data sources."""
    console.print(f"[bold]Researching:[/bold] {name}")
    if email:
        console.print(f"[bold]Email:[/bold] {email}")
    if location:
        console.print(f"[bold]Location:[/bold] {location}")
    console.print()

    skip_list = [s.strip() for s in skip.split(",")] if skip else []

    resolved_connection = connection or workspace
    resolved_linkedin = linkedin_connection or linkedin_workspace or "linkedin"

    report = run_search(
        name=name,
        email=email,
        location=location,
        connection=resolved_connection,
        linkedin_connection=resolved_linkedin,
        api_only=api_only,
        skip_sources=skip_list,
        verbose=verbose,
    )

    # Post-process
    report = aggregate(report)

    # Print summary
    console.print()
    print_summary(report)

    # Output JSON
    report_json = report.model_dump_json(indent=2)

    # Default output directory when no -o specified
    out_path = Path(output) if output else get_default_output_path(name)
    out_path.write_text(report_json, encoding="utf-8")
    console.print(f"\n[bold green]Report saved:[/bold green] {out_path.resolve()}")


@app.command()
def sources() -> None:
    """List all available data sources."""
    console.print("[bold]API Sources[/bold] (no browser needed):")
    for cls in API_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")

    console.print("\n[bold]Browser Sources[/bold] (require cc-browser):")
    for cls in BROWSER_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")

    console.print("\n[bold]Tool Sources[/bold] (require cc-browser with LinkedIn connection):")
    for cls in TOOL_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")


if __name__ == "__main__":
    app()

"""CLI for cc-dev-reports - publish a dev report to the owner, and reply to the owner in it."""

from __future__ import annotations

import sys
from typing import Optional

import typer

from . import __version__
from . import reports_ops

app = typer.Typer(
    name="cc-dev-reports",
    help="Publish a dev report to the owner through the Gateway, and reply to the owner in it.",
    add_completion=False,
    no_args_is_help=True,
)


def _version(value: bool) -> None:
    if value:
        typer.echo(f"cc-dev-reports {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(False, "--version", callback=_version, is_eager=True,
                                 help="Print the version and exit."),
) -> None:
    """Publish a dev report to the owner through the Gateway, and reply to the owner in it."""


@app.command("open")
def open_command(
    file: str = typer.Argument(..., help="The report file (UTF-8 HTML, at most 10 megabytes)."),
    json_output: bool = typer.Option(False, "--json", help="Print the result as JSON."),
) -> None:
    """Publish FILE as this session's report for it; publishing again makes a new version."""
    result = reports_ops.open_report(file)
    raise typer.Exit(reports_ops.render(result, json_output, sys.stdout))


@app.command("reply")
def reply_command(
    text: str = typer.Argument(..., help="The reply to the owner."),
    report: Optional[str] = typer.Option(None, "--report",
                                         help="The report id. Default: this session's newest report."),
    json_output: bool = typer.Option(False, "--json", help="Print the result as JSON."),
) -> None:
    """Reply to the owner in a report."""
    result = reports_ops.reply(text, report)
    raise typer.Exit(reports_ops.render(result, json_output, sys.stdout))

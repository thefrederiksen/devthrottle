"""CLI for cc-dev-reports - publish a dev report to the owner, reply to the owner in it, and read his answers."""

from __future__ import annotations

import sys
from typing import List, Optional

import typer

from . import __version__
from . import reports_ops

app = typer.Typer(
    name="cc-dev-reports",
    help="Publish a dev report to the owner through the Gateway, reply to the owner in it, and read his stored answers.",
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
    """Publish a dev report to the owner through the Gateway, reply to the owner in it, and read his stored answers."""


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


@app.command("answers")
def answers_command(
    file: Optional[str] = typer.Argument(
        None, help="The report file you published. Finds the newest report and every older report still holding an "
                   "unacted item. A factory seat reads only its own factory and seat, even when paths are shared."),
    report: Optional[str] = typer.Option(None, "--report", help="The report id, instead of FILE."),
    ack: bool = typer.Option(False, "--ack",
                             help="After acting, mark this session's claimed items on --report acted on."),
    claim: bool = typer.Option(False, "--claim",
                               help="Claim unacted answers and notes before acting. Only act on item ids reported as claimed."),
    item: Optional[List[str]] = typer.Option(None, "--item",
                                             help="With --ack or --claim: use only this item id. Repeat for several; requires --report."),
    json_output: bool = typer.Option(False, "--json", help="Print the result as JSON."),
) -> None:
    """Read stored answers and notes. Claim before acting; acknowledge only after the action succeeds."""
    result = reports_ops.answers(file, report, ack, claim, item)
    raise typer.Exit(reports_ops.render(result, json_output, sys.stdout))


def tool_main() -> None:
    """The console-script entry point. The tool runs through the shared failure reporter (issue #3642): a
    failure is reported to the Gateway, and the exit code and the printed error stay exactly as they were."""
    from cc_shared.tool_errors import run_tool

    run_tool(lambda: app(prog_name="cc-dev-reports"), "cc-dev-reports", app=app)

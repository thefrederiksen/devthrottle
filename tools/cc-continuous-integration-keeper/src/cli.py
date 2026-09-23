"""The keeper's command line. Follows the AXI standard (docs/axi-standard.md): compact output by
default, --json on every command with every filter applied to it too, definitive empty states,
help[] lines with real next commands, and a distinct exit code per outcome. It never prompts.
"""

from __future__ import annotations

import argparse
import json
import sys
import tomllib
from datetime import datetime, timedelta, timezone
from importlib import metadata
from pathlib import Path

from cc_shared import axi_output

import keeper
from budget import load_budget
from collect import GitHubRecordSource, collect
from errors import EXIT_ERROR, EXIT_OK, EXIT_RAISED, EXIT_USAGE, KeeperError
from records import RunRecords, load_records, write_time

PROG = "cc-continuous-integration-keeper"
DIST_NAME = "cc-continuous-integration-keeper"
PYPROJECT = Path(__file__).resolve().parent.parent / "pyproject.toml"

# The evidence in a finding is the point of the finding, so the cut is generous. It exists so that
# one enormous value cannot bury the rest of the report, not to hide the numbers.
TEXT_LIMIT = 400

FINDING_FIELDS = ["condition", "subject", "measured", "threshold", "proposal"]
FINDING_DEFAULT_FIELDS = ["condition", "subject", "measured"]
CONDITION_FIELDS = ["condition", "status", "findings", "measured_over"]
CONDITION_DEFAULT_FIELDS = ["condition", "status", "findings", "measured_over"]

HELP = f"""\
Reads a repository's own continuous integration run records and raises a report when any of four
things is true. It PROPOSES and never changes a workflow, a run or a test.

  {PROG} check --budget <file> --records <file> [--condition <name>] [--fields a,b] [--full] [--json]
      Judge a frozen record file. No network, and the answer does not depend on today's date.
  {PROG} check --budget <file> --repository <owner/name> [--save-records <file>] [--json]
      Collect the window off GitHub first, then judge it. Needs the GitHub command line tool.
  {PROG} collect --repository <owner/name> --budget <file> --out <file> [--since <date>] [--until <date>]
      Freeze a window of run records into a file, so the same week can be judged again later.
  {PROG} --version
      The tool's version, the way the Director's Tools page asks every tool for it.

The four conditions, each of which answers raised, clear, or not-measured:
  budget              a pull request waits longer for a result than the budget allows
  suite-growth        a job runs more tests than it did at the start of the window, by more than
                      the share the budget allows
  repeated-red-test   the same test is named red on several finished runs in a row
  skipped-job         a run concluded successfully while a job that runs in other runs on the same
                      branch was skipped

NOT-MEASURED IS NOT A PASS. A window with no pull request runs says nothing about how long a pull
request waits; a failed run whose log could not be read says nothing about which test was red. The
keeper says so and ends with an error, rather than reporting a clean week it never measured.

The budget lives in ONE file, so a person changes what "fast enough" means without touching code.
The repository is an input, not a constant: point the keeper at any repository you can read.

--condition narrows the report to one condition AND narrows the exit code with it, so a run
filtered to one condition says nothing about the other three.

Exit codes:
  0  every condition measured, nothing raised
  1  error, including a condition the keeper could not measure
  2  usage error (unknown flag, missing argument)
  3  something was raised - the report says what, with the numbers
"""


class _UsageError(Exception):
    pass


class _Parser(argparse.ArgumentParser):
    def error(self, message: str) -> None:  # noqa: D102 - argparse's own contract
        raise _UsageError(message)


def build_parser() -> argparse.ArgumentParser:
    parser = _Parser(
        prog=PROG,
        description=HELP,
        formatter_class=argparse.RawDescriptionHelpFormatter,
        allow_abbrev=False,
    )
    parser.add_argument("--version", action="store_true", help="print the tool's version and stop")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    sub = parser.add_subparsers(dest="command", parser_class=_Parser)

    check = sub.add_parser("check", description="Judge run records against the budget.", allow_abbrev=False)
    check.add_argument("--budget", required=True, help="the budget file")
    check.add_argument("--records", help="a frozen record file to judge")
    check.add_argument("--repository", help="owner/name to collect from GitHub and then judge")
    check.add_argument("--save-records", help="where to write what was collected")
    check.add_argument("--condition", help="show one condition: " + ", ".join(keeper.CONDITIONS))
    check.add_argument("--fields", help="fields for the findings list: " + ", ".join(FINDING_FIELDS))
    check.add_argument("--full", action="store_true", help="never shorten a value")
    check.add_argument("--json", action="store_true", help="machine-readable output")

    gather = sub.add_parser("collect", description="Freeze a window of run records.", allow_abbrev=False)
    gather.add_argument("--repository", required=True, help="owner/name")
    gather.add_argument("--budget", required=True, help="the budget file, which names the workflow")
    gather.add_argument("--out", required=True, help="where to write the records")
    gather.add_argument("--since", help="first day of the window, as 2026-09-16")
    gather.add_argument("--until", help="last day of the window, as 2026-09-19")
    gather.add_argument("--json", action="store_true", help="machine-readable output")
    return parser


# ---------------------------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------------------------


def _shorten(value: str, full: bool) -> str:
    if full or len(value) <= TEXT_LIMIT:
        return value
    return f"{value[:TEXT_LIMIT]} (shortened, {len(value)} characters in all - use --full)"


def _day(text: str, name: str) -> datetime:
    try:
        return datetime.strptime(text, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError as ex:
        raise KeeperError(f"{name} must be a day written as 2026-09-16, not {text!r}", EXIT_USAGE) from ex


def tool_version() -> str:
    try:
        return metadata.version(DIST_NAME)
    except metadata.PackageNotFoundError:
        pass
    try:
        text = PYPROJECT.read_text(encoding="utf-8")
    except OSError as ex:
        raise KeeperError(
            f"cannot tell you the version: no installed distribution named {DIST_NAME}, and "
            f"{PYPROJECT} could not be read ({ex})"
        ) from ex
    value = tomllib.loads(text).get("project", {}).get("version")
    if not isinstance(value, str) or not value.strip():
        raise KeeperError(
            f"cannot tell you the version: no installed distribution named {DIST_NAME}, and "
            f"{PYPROJECT} has no project.version"
        )
    return value


def report_as_json(report: keeper.Report, conditions: tuple[keeper.ConditionResult, ...]) -> dict:
    return {
        "repository": report.records.repository,
        "workflow": report.records.workflow,
        "default_branch": report.records.default_branch,
        "budget_file": str(report.budget.path),
        "window": {
            "from": write_time(report.records.window_from),
            "to": write_time(report.records.window_to),
        },
        "collected_at": write_time(report.records.collected_at),
        "raised": sum(len(condition.findings) for condition in conditions),
        "conditions": [
            {
                "condition": condition.condition,
                "status": condition.status,
                "measured_over": condition.measured_over,
                "findings": [
                    {
                        "condition": finding.condition,
                        "subject": finding.subject,
                        "measured": finding.measured,
                        "threshold": finding.threshold,
                        "proposal": finding.proposal,
                    }
                    for finding in condition.findings
                ],
            }
            for condition in conditions
        ],
        "readings": [{"what": what, "value": value} for what, value in report.inventory],
        "notes": list(report.budget.notes),
    }


def render_report(report: keeper.Report, conditions: tuple[keeper.ConditionResult, ...], fields: list[str], full: bool) -> str:
    records = report.records
    findings = [finding for condition in conditions for finding in condition.findings]
    blocks = [
        f"repository: {records.repository}",
        f"workflow: {records.workflow}",
        f"default-branch: {records.default_branch}",
        f"window: {write_time(records.window_from)} to {write_time(records.window_to)}",
        f"collected: {write_time(records.collected_at)}",
        f"budget-file: {report.budget.path}",
        axi_output.format_count(
            len(findings),
            breakdown=[
                (condition.condition, len(condition.findings)) for condition in conditions
            ],
        ),
        axi_output.render_list(
            "conditions",
            CONDITION_DEFAULT_FIELDS,
            [
                {
                    "condition": condition.condition,
                    "status": condition.status,
                    "findings": len(condition.findings),
                    "measured_over": _shorten(condition.measured_over, full),
                }
                for condition in conditions
            ],
        ),
        axi_output.render_list(
            "findings",
            fields,
            [
                {
                    "condition": finding.condition,
                    "subject": finding.subject,
                    "measured": _shorten(finding.measured, full),
                    "threshold": _shorten(finding.threshold, full),
                    "proposal": _shorten(finding.proposal, full),
                }
                for finding in findings
            ],
        ),
        axi_output.render_list(
            "readings",
            ["what", "value"],
            [{"what": what, "value": value} for what, value in report.inventory],
        ),
    ]
    if report.budget.notes:
        blocks.append(
            axi_output.render_list("notes", ["note"], [{"note": note} for note in report.budget.notes])
        )
    blocks.append(
        axi_output.format_help(
            [
                f"{PROG} check --budget {report.budget.path} --records <file> --full",
                f"{PROG} check --budget {report.budget.path} --records <file> --condition <name>",
                f"{PROG} collect --repository {records.repository} --budget {report.budget.path} --out <file>",
            ]
        )
    )
    return "\n".join(blocks)


# ---------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------


def _records_for_check(arguments: argparse.Namespace, budget) -> RunRecords:
    if bool(arguments.records) == bool(arguments.repository):
        raise KeeperError(
            "check needs exactly one of --records (a frozen file) or --repository (collect it now)",
            EXIT_USAGE,
        )
    if arguments.records:
        return load_records(Path(arguments.records))

    now = datetime.now(timezone.utc).replace(microsecond=0)
    window_from = now - timedelta(days=budget.window_days)
    source = GitHubRecordSource(arguments.repository)
    records = collect(source, budget.workflow, window_from, now, now=now)
    if arguments.save_records:
        records.write(Path(arguments.save_records))
    return records


def command_check(arguments: argparse.Namespace, out, err) -> int:
    budget = load_budget(Path(arguments.budget))
    records = _records_for_check(arguments, budget)
    report = keeper.judge(records, budget)

    conditions = report.conditions
    if arguments.condition:
        if arguments.condition not in keeper.CONDITIONS:
            raise KeeperError(
                f"--condition must be one of {', '.join(keeper.CONDITIONS)}, not {arguments.condition!r}",
                EXIT_USAGE,
            )
        conditions = tuple(c for c in conditions if c.condition == arguments.condition)

    fields = axi_output.parse_fields_or_exit(
        arguments.fields, FINDING_FIELDS, FINDING_DEFAULT_FIELDS, err=err
    )

    if arguments.json:
        print(json.dumps(report_as_json(report, conditions), indent=1), file=out)
    else:
        print(render_report(report, conditions, fields, arguments.full), file=out)

    if any(condition.raised for condition in conditions):
        return EXIT_RAISED
    unmeasured = [c for c in conditions if c.status == keeper.NOT_MEASURED]
    if unmeasured:
        print(
            "error: the keeper could not measure "
            + ", ".join(c.condition for c in unmeasured)
            + ". A condition it could not measure is not a condition it passed.",
            file=err,
        )
        return EXIT_ERROR
    return EXIT_OK


def command_collect(arguments: argparse.Namespace, out, err) -> int:
    budget = load_budget(Path(arguments.budget))
    now = datetime.now(timezone.utc).replace(microsecond=0)
    window_to = _day(arguments.until, "--until") + timedelta(days=1) if arguments.until else now
    window_from = _day(arguments.since, "--since") if arguments.since else window_to - timedelta(days=budget.window_days)
    if window_from >= window_to:
        raise KeeperError("--since must be before --until", EXIT_USAGE)

    source = GitHubRecordSource(arguments.repository)
    records = collect(source, budget.workflow, window_from, window_to, now=now)
    path = Path(arguments.out)
    records.write(path)

    progress = source.progress
    record = {
        "repository": records.repository,
        "workflow": records.workflow,
        "default_branch": records.default_branch,
        "window_from": write_time(window_from),
        "window_to": write_time(window_to),
        "out": str(path),
        "runs_listed": progress.runs_listed,
        "runs_in_window": progress.runs_in_window,
        "runs_whose_jobs_were_read": progress.jobs_read_for_runs,
        "job_logs_read": progress.logs_read,
        "job_logs_refused": progress.logs_unavailable,
    }
    if arguments.json:
        print(json.dumps(record, indent=1), file=out)
    else:
        print(
            "\n".join(
                [
                    axi_output.format_count(len(records.runs)),
                    axi_output.render_list(
                        "collected", list(record), [{key: value for key, value in record.items()}]
                    ),
                    axi_output.format_help(
                        [f"{PROG} check --budget {arguments.budget} --records {path}"]
                    ),
                ]
            ),
            file=out,
        )
    if progress.runs_in_window == 0:
        print(
            f"error: no run of the workflow {records.workflow!r} started inside the window. "
            "An empty collection is a broken instrument, not a quiet week.",
            file=err,
        )
        return EXIT_ERROR
    return EXIT_OK


def main(argv: list[str] | None = None, out=None, err=None) -> int:
    out = out or sys.stdout
    err = err or sys.stderr
    parser = build_parser()
    try:
        arguments = parser.parse_args(argv if argv is not None else sys.argv[1:])
    except _UsageError as ex:
        print(f"usage error: {ex}", file=err)
        print(HELP, file=err)
        return EXIT_USAGE

    if arguments.version:
        try:
            version = tool_version()
        except KeeperError as ex:
            print(f"error: {ex}", file=err)
            return ex.exit_code
        if arguments.json:
            print(json.dumps({"tool": DIST_NAME, "version": version}), file=out)
        else:
            print(f"{DIST_NAME} {version}", file=out)
        return EXIT_OK

    if not arguments.command:
        print(HELP, file=err)
        return EXIT_USAGE

    try:
        if arguments.command == "check":
            return command_check(arguments, out, err)
        if arguments.command == "collect":
            return command_collect(arguments, out, err)
    except KeeperError as ex:
        print(f"error: {ex}", file=err)
        return ex.exit_code
    except SystemExit as ex:
        # The shared output helper ends a bad --fields value this way. main() always ANSWERS with
        # an exit code rather than throwing one, so that a caller inside the same process - a test,
        # or another tool - sees the same outcome the shell does.
        return EXIT_USAGE if ex.code is None else int(ex.code)
    raise AssertionError(f"no such command: {arguments.command}")


if __name__ == "__main__":
    sys.exit(main())

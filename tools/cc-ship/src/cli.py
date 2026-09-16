"""cc-ship: take a finished change from a session to merged on main.

Every answer carries `state` and `next_step`. Errors carry `error`, `code` and `help`
and exit non-zero. Unknown flags fail. All output is ASCII.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import engine
import gitops
import runstore
from errors import ShipError
from text import ascii_safe

HELP = """\
Take a finished change from this session to merged on origin/main.

  cc-ship start --intent intent.md [--title "..."]
      Open a run for this branch and take it as far as it can go. intent.md holds the
      owner's words: goal, constraints, what was ruled out, decisions made.
  cc-ship wait [--seconds N]
      Block (up to 8 minutes) while a reviewer or verifier session works.
  cc-ship continue
      Resume after you fixed findings, resolved a conflict, or a step failed.
  cc-ship respond <id> --fix|--keep|--drop [--note "the owner's words"]
      Record the owner's call on one finding (or on "fix-limit"). Relay his
      answer; never make it up.
  cc-ship status [--json]
      The run's one honest state and what to do next.
  cc-ship abort
      End the run and stop the sessions it started. The branch is left alone.

States: working, waiting-on-author, waiting-on-owner, failed, merged, aborted.
When the state is waiting-on-owner, tell the owner (word for word) and END YOUR TURN.
Add --json to any command for machine-readable output.
Setup and rules: tools/cc-ship/README.md
"""

EXIT_OK, EXIT_ERROR, EXIT_USAGE = 0, 1, 2


class _Parser(argparse.ArgumentParser):
    def error(self, message: str) -> None:  # unknown flags and bad usage fail loudly
        raise ShipError("usage", message, "Run: cc-ship --help", EXIT_USAGE)


def build_parser() -> argparse.ArgumentParser:
    parser = _Parser(prog="cc-ship", description=HELP,
                     formatter_class=argparse.RawDescriptionHelpFormatter, allow_abbrev=False)
    sub = parser.add_subparsers(dest="command", required=True, parser_class=_Parser)

    def add(name: str, help_text: str) -> argparse.ArgumentParser:
        p = sub.add_parser(name, help=help_text, allow_abbrev=False)
        p.add_argument("--json", action="store_true", help="machine-readable output")
        return p

    p = add("start", "open a run for this branch")
    p.add_argument("--intent", required=True, type=Path, help="the owner's intent file")
    p.add_argument("--title", help="pull request title (default: the first commit subject)")
    p = add("wait", "wait while a spawned session works")
    p.add_argument("--seconds", type=int, default=engine.WAIT_SLICE_SECONDS,
                   help="longest wait, at most 480")
    add("continue", "resume the run")
    p = add("respond", "record the owner's call on a finding")
    p.add_argument("finding", help="the finding id, e.g. r1-F2")
    choice = p.add_mutually_exclusive_group(required=True)
    choice.add_argument("--fix", dest="decision", action="store_const", const="fix")
    choice.add_argument("--keep", dest="decision", action="store_const", const="keep")
    choice.add_argument("--drop", dest="decision", action="store_const", const="drop")
    p.add_argument("--note", default="", help="the owner's own words")
    add("status", "show the run's state")
    add("abort", "end the run")
    return parser


def _current_run(cwd: Path) -> dict:
    repo = gitops.toplevel(cwd)
    branch = gitops.branch(repo)
    run = runstore.find_active(repo, branch)
    if run is None:
        raise ShipError("no-run", f"No open cc-ship run for {branch} in {repo}.",
                        "Start one: cc-ship start --intent intent.md")
    return run


def _run_state() -> str:
    try:
        return _current_run(Path.cwd())["state"]
    except Exception:
        return runstore.FAILED  # no run to describe: the command itself failed


def summary(run: dict) -> dict:
    session = run.get("session")
    return {
        "run": run["id"],
        "state": run["state"],
        "phase": run["phase"],
        "branch": run["branch"],
        "session": f"{session['name']} ({session['id']})" if session else None,
        "steps": run["steps"],
        "pr": (run.get("pr") or {}).get("url"),
        "risk": (run.get("risk") or {}).get("level"),
        "folder": str(runstore.folder(run)),
        "failure": run.get("failure"),
        "next_step": run["next_step"],
    }


def _print(data: dict, as_json: bool) -> None:
    if as_json:
        print(json.dumps(data, indent=1, ensure_ascii=True))
        return
    for key, value in data.items():
        if value is None or key == "next_step":
            continue
        if isinstance(value, dict):
            value = ", ".join(f"{k}={v}" for k, v in value.items())
        print(ascii_safe(f"{key}: {value}"))
    if "next_step" in data:
        print("next_step:")
        for line in ascii_safe(data["next_step"]).splitlines() or [""]:
            print(f"  {line}")


def main(argv: list[str] | None = None) -> int:
    as_json = "--json" in (argv if argv is not None else sys.argv[1:])
    try:
        args = build_parser().parse_args(argv)
        cwd = Path.cwd()
        if args.command == "start":
            run = engine.start(cwd, args.intent, args.title)
        elif args.command == "wait":
            if not 1 <= args.seconds <= engine.WAIT_SLICE_SECONDS:
                raise ShipError("usage", f"--seconds must be 1 to {engine.WAIT_SLICE_SECONDS}.",
                                "Run: cc-ship wait", EXIT_USAGE)
            run = engine.wait(_current_run(cwd), args.seconds)
        elif args.command == "continue":
            run = engine.resume(_current_run(cwd))
        elif args.command == "respond":
            run = engine.respond(_current_run(cwd), args.finding, args.decision,
                                 ascii_safe(args.note))
        elif args.command == "status":
            run = _current_run(cwd)
        else:
            run = engine.abort(_current_run(cwd))
    except Exception as exc:  # the entry point: never a bare traceback for an agent to parse
        if isinstance(exc, (ShipError, engine.fleet.FleetError, engine.preview.PreviewError)):
            err = engine.as_ship_error(exc)
        else:
            err = engine.internal_error(exc)
        # A refused command leaves the run as it was: report the run's real state.
        _print({"state": _run_state(), **err.as_dict(), "next_step": err.help}, as_json)
        return err.exit_code
    _print(summary(run), as_json)
    return EXIT_ERROR if run["state"] == runstore.FAILED else EXIT_OK


if __name__ == "__main__":
    sys.exit(main())

"""cc-worktrees command line. Follows the AXI standard (docs/axi-standard.md): compact output,
--json on every command, help[] lines, definitive empty states, and a distinct exit code per outcome.
Never prompts."""

from __future__ import annotations

import argparse
import json
import sys
import tomllib
from importlib import metadata
from pathlib import Path

from cc_shared import axi_output

import pool
from errors import EXIT_ERROR, EXIT_OK, EXIT_USAGE, ToolError

PROG = "cc-worktrees"
DIST_NAME = "cc-worktrees"

HELP = f"""\
Pooled git worktrees. A worktree is only reset when its work has provably landed on the remote.

  {PROG} get --repo <path> --holder <text> [--pool-size N]
      Hand out a free worktree (reset to the remote default branch, build output kept),
      or create a new slot beside the repository while under the pool size (default {pool.DEFAULT_POOL_SIZE}).
  {PROG} return <path-or-slot> --lease <id> [--repo <path>]
      Check that the work landed. Landed: reset and free. Otherwise: held, with the reason.
      The lease from get or lease is required: without it nobody can say the holder let go.
  {PROG} list [--repo <path>] [--fields a,b]
      Every slot: slot, state (free, in-use, held), holder, reason.
  {PROG} lease <path-or-slot> --holder <text> [--reclaim-held] [--repo <path>]
      Take a specific free slot. A held slot only with --reclaim-held (it is not reset).
  {PROG} destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] [--repo <path>]
      Checks the work landed now, whatever the state says; unproven means held (exit 3), no flag skips it.
      Dry run unless --yes. Refuses a held or in-use slot without its flag. One slot at a time.
  {PROG} release <path-or-slot> --confirm-abandon [--repo <path>]
      Put a HELD slot back in the pool. Pins every commit it cannot prove landed under
      refs/cc-worktrees/<slot>/, where git gc can never take it, then removes the directory and the
      ignored files in it. Refuses while anything in the slot cannot be pinned.
  {PROG} --version
      The tool's version, the way the Director's Tools page asks every tool for it.

A slot is reset only when: nothing uncommitted or untracked, the remote was fetched just now, the
default branch was read from the remote, and every commit is on a remote branch or already in the
default branch as the same patch, each commit on its own, with that content still in the default
branch's current tip. Anything unproven means held.

Add --json to any command for machine-readable output.

Exit codes:
  0  success
  1  error (the message says what and what to do)
  2  usage error (unknown flag, missing argument)
  3  not returned: the worktree is held, with the reason
  4  pool full: no free slot and the pool is at its size; nothing was created

State lives in the per-user data directory; set {pool.HOME_ENV} to use another directory.
"""

LIST_FIELDS = ["repo", "slot", "path", "state", "holder", "reason", "updated"]
LIST_DEFAULT_ONE_REPO = ["slot", "state", "holder", "reason"]
LIST_DEFAULT_ALL = ["repo", "slot", "state", "holder", "reason"]


class _UsageError(Exception):
    pass


class _Parser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise _UsageError(message)


def build_parser() -> argparse.ArgumentParser:
    parser = _Parser(prog=PROG, description=HELP, formatter_class=argparse.RawDescriptionHelpFormatter,
                     allow_abbrev=False)
    sub = parser.add_subparsers(dest="command", required=True, parser_class=_Parser)

    def add(name: str, text: str) -> argparse.ArgumentParser:
        p = sub.add_parser(name, help=text, description=text, allow_abbrev=False)
        p.add_argument("--json", action="store_true", help="machine-readable output")
        return p

    p = add("get", "hand out a free worktree or create a new slot")
    p.add_argument("--repo", required=True, help="the repository the pool belongs to")
    p.add_argument("--holder", required=True, help="who holds it, e.g. a session id")
    p.add_argument("--pool-size", type=int, default=pool.DEFAULT_POOL_SIZE, help="most slots for this repository")

    p = add("return", "check the work landed, then free it or hold it")
    p.add_argument("target", help="the worktree path or slot name (wt01)")
    p.add_argument("--lease", required=True, help="the lease id from get or lease; refused if it no longer matches")
    p.add_argument("--repo", help="the repository, when target is a slot name")

    p = add("list", "list every slot")
    p.add_argument("--repo", help="only this repository's pool")
    p.add_argument("--fields", help=f"fields to show: {','.join(LIST_FIELDS)}")

    p = add("lease", "take a specific slot")
    p.add_argument("target", help="the worktree path or slot name (wt01)")
    p.add_argument("--holder", required=True, help="who holds it")
    p.add_argument("--reclaim-held", action="store_true", help="take a held slot as it is, without a reset")
    p.add_argument("--repo", help="the repository, when target is a slot name")

    p = add("release", "put a held slot back in the pool, pinning the work it cannot prove landed")
    p.add_argument("target", help="the worktree path or slot name (wt01)")
    p.add_argument("--confirm-abandon", action="store_true",
                   help="required: the slot goes back in the pool and its directory is removed")
    p.add_argument("--repo", help="the repository, when target is a slot name")

    p = add("destroy", "remove one slot (dry run unless --yes)")
    p.add_argument("target", help="the worktree path or slot name (wt01)")
    p.add_argument("--yes", action="store_true", help="really remove it")
    p.add_argument("--allow-held", action="store_true", help="allow removing a held slot")
    p.add_argument("--allow-in-use", action="store_true", help="allow removing an in-use slot")
    p.add_argument("--repo", help="the repository, when target is a slot name")
    return parser


# ---------------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------------


def _pairs(record: dict, keys: list[str]) -> str:
    lines = []
    for key in keys:
        value = record.get(key)
        lines.append(f"{key}: {axi_output.format_value(value)}")
    return "\n".join(lines)


PROOF_FIELDS = ["commit", "host", "detail"]


def _emit(args: argparse.Namespace, record: dict, keys: list[str], help_lines: list[str]) -> None:
    """The plain output is the same answer as `--json`, in the AXI shape. `proved_by` and the
    `host_proof` list are on it for the same reason they are in the JSON: "free" must name what proved
    the work, and a commit the HOST freed must name the pull request that freed it."""
    if args.json:
        sys.stdout.write(json.dumps(record) + "\n")
        return
    blocks = [_pairs(record, keys)]
    if "host_proof" in record:
        blocks.append(axi_output.render_list("host_proof", PROOF_FIELDS, record["host_proof"]))
    if help_lines:
        blocks.append(axi_output.format_help(help_lines))
    axi_output.write_blocks(sys.stdout, *blocks)


def _emit_error(as_json: bool, err: ToolError) -> None:
    """A refusal prints in the same AXI shape as a success: `error:` and `code:` are VALUES, rendered
    by the value renderer, and the help lines are COMMANDS, written exactly as the caller wrote them.

    A help line is not a value and must never be escaped. The value escape is the escaping used inside
    a quoted value, so it turned every backslash in a suggested command into two and a Windows path
    came out as `D:\\\\repo`, which nobody can paste. The value renderer leaves a plain value - a
    Windows path among them - exactly as it is, and quotes and escapes only what needs it.
    """
    if as_json:
        payload = {"error": err.message, "code": err.code, "help": err.help_lines, **err.details}
        sys.stdout.write(json.dumps(payload) + "\n")
        return
    blocks = [f"error: {axi_output.format_value(err.message)}", f"code: {axi_output.format_value(err.code)}"]
    if err.help_lines:
        blocks.append(axi_output.format_help(err.help_lines))
    axi_output.write_blocks(sys.stdout, *blocks)


def _q(text: str) -> str:
    return f'"{text}"' if " " in text else text


# ---------------------------------------------------------------------------------------------------
# Version
# ---------------------------------------------------------------------------------------------------

# `pyproject.toml` sits beside src/ in a checkout and is NOT inside the wheel, because the wheel ships
# only the package directory. That is why the installed answer is asked for first.
PYPROJECT = Path(__file__).resolve().parent.parent / "pyproject.toml"


def tool_version() -> str:
    """The tool's version. There is exactly one place it is written down: `pyproject.toml`.

    Installed, the answer is the distribution's recorded version, which the build wrote out of that
    same `pyproject.toml` and which is the only copy an installed tool has - the wheel ships the
    package directory, not the file. From a checkout, where no distribution of this name exists, the
    file itself is read. Deliberately no ``__version__`` in the source: a second copy in the source
    is a second source of truth, and it goes stale the first time somebody bumps one and not the
    other.

    Not proven by the code here, and worth knowing: a checkout that has had a wheel built in it keeps
    a `cc_worktrees.egg-info` beside this package, and that IS an installed distribution as far as
    the first branch is concerned. So a checkout whose version was bumped without rebuilding answers
    with the version it last built. `tests/test_42_version.py` fails when the two disagree and says
    so in those words.

    Neither source answering is a broken install, not a thing to paper over - it says so and exits 1.
    """
    try:
        return metadata.version(DIST_NAME)
    except metadata.PackageNotFoundError:
        pass
    try:
        text = PYPROJECT.read_text(encoding="utf-8")
    except OSError as ex:
        raise ToolError("version", f"cannot tell you the version: no installed distribution named "
                                   f"{DIST_NAME}, and {PYPROJECT} could not be read ({ex})",
                        [f"{PROG} --help"], exit_code=EXIT_ERROR) from ex
    value = tomllib.loads(text).get("project", {}).get("version")
    if not isinstance(value, str) or not value.strip():
        raise ToolError("version", f"cannot tell you the version: no installed distribution named "
                                   f"{DIST_NAME}, and {PYPROJECT} has no project.version",
                        [f"{PROG} --help"], exit_code=EXIT_ERROR)
    return value


def cmd_version(as_json: bool) -> int:
    """`--version` is the tool's own flag, not a command's. The Director's Tools page runs exactly
    `cc-worktrees --version` on every tool it lists and fails the row on a non-zero exit."""
    record = {"tool": DIST_NAME, "version": tool_version()}
    if as_json:
        sys.stdout.write(json.dumps(record) + "\n")
        return EXIT_OK
    axi_output.write_blocks(sys.stdout, _pairs(record, ["tool", "version"]),
                            axi_output.format_help([f"{PROG} --help"]))
    return EXIT_OK


# ---------------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------------


def cmd_get(args: argparse.Namespace) -> int:
    if not args.holder.strip():
        raise _UsageError("--holder must not be empty")
    if args.pool_size < 1:
        raise _UsageError("--pool-size must be at least 1")
    result = pool.get(args.repo, args.holder, args.pool_size)
    _emit(args, result, ["slot", "path", "lease", "holder", "base", "commit", "reused", "proved_by"],
          [f"{PROG} return {_q(result['path'])} --lease {result['lease']}"])
    return EXIT_OK


def cmd_return(args: argparse.Namespace) -> int:
    result = pool.return_slot(args.target, args.lease, args.repo)
    _emit(args, result, ["slot", "path", "state", "base", "commit", "proved_by"],
          [f"{PROG} get --repo {_q(result['repo'])} --holder <holder>"])
    return EXIT_OK


def cmd_lease(args: argparse.Namespace) -> int:
    if not args.holder.strip():
        raise _UsageError("--holder must not be empty")
    result = pool.lease_slot(args.target, args.holder, args.reclaim_held, args.repo)
    _emit(args, result, ["slot", "path", "lease", "holder", "base", "commit", "proved_by"],
          [f"{PROG} return {_q(result['path'])} --lease {result['lease']}"])
    return EXIT_OK


def cmd_destroy(args: argparse.Namespace) -> int:
    result = pool.destroy_slot(args.target, args.yes, args.allow_held, args.allow_in_use, args.repo)
    help_lines = []
    if result["dry_run"]:
        help_lines.append(f"{PROG} destroy {result['slot']} --repo {_q(result['repo'])} --yes"
                          + (" --allow-held" if result["state"] == pool.HELD else "")
                          + (" --allow-in-use" if result["state"] == pool.IN_USE else ""))
    _emit(args, result, ["slot", "path", "state", "reason", "dry_run", "removed", "proved_by"], help_lines)
    return EXIT_OK


def cmd_release(args: argparse.Namespace) -> int:
    if not args.confirm_abandon:
        raise _UsageError(
            "release needs --confirm-abandon: it puts a held slot back in the pool and removes its "
            "directory, the ignored files in it and all. Every commit it cannot prove landed is pinned "
            "first, and it refuses while anything in the slot cannot be pinned")
    result = pool.release_slot(args.target, args.repo)
    keys = ["slot", "path", "removed", "pinned", "pinned_now", "proven", "note"]
    if args.json:
        sys.stdout.write(json.dumps(result) + "\n")
        return EXIT_OK
    if result["pins"]:
        ref = result["pins"][0]["ref"]
        help_lines = [f"git -C {_q(result['repo'])} log {ref}",
                      f"git -C {_q(result['repo'])} branch <name> {ref}"]
    else:
        help_lines = [f"{PROG} get --repo {_q(result['repo'])} --holder <holder>"]
    axi_output.write_blocks(sys.stdout, _pairs(result, keys),
                            axi_output.render_list("pins", ["ref", "commit"], result["pins"]),
                            axi_output.format_help(help_lines))
    return EXIT_OK


def cmd_list(args: argparse.Namespace) -> int:
    default = LIST_DEFAULT_ONE_REPO if args.repo else LIST_DEFAULT_ALL
    try:
        fields = axi_output.parse_fields(args.fields, LIST_FIELDS, default)
    except axi_output.FieldsError as ex:
        raise _UsageError(str(ex)) from ex
    rows = pool.list_slots(args.repo)
    counts = {state: sum(1 for r in rows if r["state"] == state) for state in pool.STATES}
    if args.json:
        sys.stdout.write(json.dumps({"count": len(rows), "counts": counts, "slots": rows}) + "\n")
        return EXIT_OK
    breakdown = [(state, counts[state]) for state in pool.STATES] if rows else None
    help_lines = [f"{PROG} get --repo <path> --holder <holder>"]
    if counts[pool.HELD]:
        help_lines.append(f"{PROG} return <path> --lease <lease>")
    axi_output.write_blocks(sys.stdout, axi_output.format_count(len(rows), breakdown=breakdown),
                            axi_output.render_list("slots", fields, rows), axi_output.format_help(help_lines))
    return EXIT_OK


COMMANDS = {"get": cmd_get, "return": cmd_return, "list": cmd_list, "lease": cmd_lease,
            "destroy": cmd_destroy, "release": cmd_release}


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    as_json = "--json" in argv
    parser = build_parser()
    try:
        # `--version` takes no subcommand, so argparse (whose subcommand is required) would reject it
        # before ever seeing the flag. It is answered here, and ONLY when it is the whole request:
        # `cc-worktrees list --version` stays the usage error it should be rather than quietly
        # printing a version and dropping the command the caller asked for.
        if [a for a in argv if a != "--json"] == ["--version"]:
            return cmd_version(as_json)
        args = parser.parse_args(argv)
        return COMMANDS[args.command](args)
    except _UsageError as ex:
        _emit_error(as_json, ToolError("usage", str(ex), [f"{PROG} --help"], exit_code=EXIT_USAGE))
        return EXIT_USAGE
    except ToolError as ex:
        _emit_error(as_json, ex)
        return ex.exit_code
    except Exception as ex:  # the entry point: an unexpected failure is reported, never swallowed
        _emit_error(as_json, ToolError("internal", f"{type(ex).__name__}: {ex}", [f"{PROG} --help"],
                                       exit_code=EXIT_ERROR))
        return EXIT_ERROR

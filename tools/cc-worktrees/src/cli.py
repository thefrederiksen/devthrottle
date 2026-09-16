"""cc-worktrees command line. Follows the AXI standard (docs/axi-standard.md): compact output,
--json on every command, help[] lines, definitive empty states, and a distinct exit code per outcome.
Never prompts."""

from __future__ import annotations

import argparse
import json
import sys

from cc_shared import axi_output

import pool
from errors import EXIT_ERROR, EXIT_OK, EXIT_USAGE, ToolError

PROG = "cc-worktrees"

HELP = f"""\
Pooled git worktrees. A worktree is only reset when its work has provably landed on the remote.

  {PROG} get --repo <path> --holder <text> [--pool-size N]
      Hand out a free worktree (reset to the remote default branch, build output kept),
      or create a new slot beside the repository while under the pool size (default {pool.DEFAULT_POOL_SIZE}).
  {PROG} return <path-or-slot> [--lease <id>] [--repo <path>]
      Check that the work landed. Landed: reset and free. Otherwise: held, with the reason.
  {PROG} list [--repo <path>] [--fields a,b]
      Every slot: slot, state (free, in-use, held), holder, reason.
  {PROG} lease <path-or-slot> --holder <text> [--reclaim-held] [--repo <path>]
      Take a specific free slot. A held slot only with --reclaim-held (it is not reset).
  {PROG} destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] [--repo <path>]
      Dry run unless --yes. Refuses a held or in-use slot without its flag. One slot at a time.

A slot is reset only when: nothing uncommitted or untracked, the remote was fetched just now, the
default branch was read from the remote, and every commit is on a remote branch or already in the
default branch (same patch or same content). Anything unproven means held.

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
    p.add_argument("--lease", help="the lease id from get; refused if it no longer matches")
    p.add_argument("--repo", help="the repository, when target is a slot name")

    p = add("list", "list every slot")
    p.add_argument("--repo", help="only this repository's pool")
    p.add_argument("--fields", help=f"fields to show: {','.join(LIST_FIELDS)}")

    p = add("lease", "take a specific slot")
    p.add_argument("target", help="the worktree path or slot name (wt01)")
    p.add_argument("--holder", required=True, help="who holds it")
    p.add_argument("--reclaim-held", action="store_true", help="take a held slot as it is, without a reset")
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


def _emit(args: argparse.Namespace, record: dict, keys: list[str], help_lines: list[str]) -> None:
    if args.json:
        sys.stdout.write(json.dumps(record) + "\n")
        return
    blocks = [_pairs(record, keys)]
    if help_lines:
        blocks.append(axi_output.format_help(help_lines))
    axi_output.write_blocks(sys.stdout, *blocks)


def _emit_error(as_json: bool, err: ToolError) -> None:
    if as_json:
        payload = {"error": err.message, "code": err.code, "help": err.help_lines, **err.details}
        sys.stdout.write(json.dumps(payload) + "\n")
        return
    blocks = [f"error: {axi_output.escape_ascii(err.message)}", f"code: {err.code}"]
    if err.help_lines:
        blocks.append(axi_output.format_help([axi_output.escape_ascii(h) for h in err.help_lines]))
    axi_output.write_blocks(sys.stdout, *blocks)


def _q(text: str) -> str:
    return f'"{text}"' if " " in text else text


# ---------------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------------


def cmd_get(args: argparse.Namespace) -> int:
    if not args.holder.strip():
        raise _UsageError("--holder must not be empty")
    if args.pool_size < 1:
        raise _UsageError("--pool-size must be at least 1")
    result = pool.get(args.repo, args.holder, args.pool_size)
    _emit(args, result, ["slot", "path", "lease", "holder", "base", "commit", "reused"],
          [f"{PROG} return {_q(result['path'])} --lease {result['lease']}"])
    return EXIT_OK


def cmd_return(args: argparse.Namespace) -> int:
    result = pool.return_slot(args.target, args.lease, args.repo)
    _emit(args, result, ["slot", "path", "state", "base", "commit"],
          [f"{PROG} get --repo {_q(result['repo'])} --holder <holder>"])
    return EXIT_OK


def cmd_lease(args: argparse.Namespace) -> int:
    if not args.holder.strip():
        raise _UsageError("--holder must not be empty")
    result = pool.lease_slot(args.target, args.holder, args.reclaim_held, args.repo)
    _emit(args, result, ["slot", "path", "lease", "holder", "base", "commit"],
          [f"{PROG} return {_q(result['path'])} --lease {result['lease']}"])
    return EXIT_OK


def cmd_destroy(args: argparse.Namespace) -> int:
    result = pool.destroy_slot(args.target, args.yes, args.allow_held, args.allow_in_use, args.repo)
    help_lines = []
    if result["dry_run"]:
        help_lines.append(f"{PROG} destroy {result['slot']} --repo {_q(result['repo'])} --yes"
                          + (" --allow-held" if result["state"] == pool.HELD else "")
                          + (" --allow-in-use" if result["state"] == pool.IN_USE else ""))
    _emit(args, result, ["slot", "path", "state", "reason", "dry_run", "removed"], help_lines)
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


COMMANDS = {"get": cmd_get, "return": cmd_return, "list": cmd_list, "lease": cmd_lease, "destroy": cmd_destroy}


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    as_json = "--json" in argv
    parser = build_parser()
    try:
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

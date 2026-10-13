"""`cc-devthrottle license sign-in | approve | renew | set-browser`: the command-line layer over `license_signin`,
in the AXI shape (docs/axi-standard.md).

- `license sign-in <email> --browser <profile>` signs an account in on this machine and keeps it as a spare, approving
  in one of this machine's browser profiles. When the account is held already - as a spare or as the active
  account - its sign-in is RENEWED in place.
- `license sign-in <email> --remote-approval` does the same with the approval done on ANOTHER machine: it prints
  `authorize-url: <url>` and reads the code back as one line on standard input.
- `license approve --email <email> --browser <profile> <url>` is that other machine's half: it approves the URL in
  its browser profile and prints the code alone, for the caller to hand back.
- With `--json`, `sign-in` and `approve` also answer a FAILURE as one JSON object on standard output
  (`result` "failed" or "refused", and the `reason` word), because the Director reads these answers and writes its
  own sentences. The sentence for a person still goes to standard error.
- `license renew` signs in again every account held (the active one too) whose sign-in ends within five days, with
  the profile set for it by
  `license set-browser`. It exits non-zero when any renewal failed, so a scheduler sees the failure.
- `license renew --check` signs nothing in: it answers which accounts are `due`, and the profile set for each here.
- `license sign-in <email> --ask-when-no-browser` approves in the profile set for the account on this machine when
  there is one, and otherwise behaves as `--remote-approval`. The Director runs these two: it asks what is due once
  a day and signs each due account in, so an account with no profile here is approved on another machine and no
  call is made that is expected to fail.
- `license get-browser <email>` says which profile is set for an account on this machine, or that none is; exit 0
  either way.
- A caller that CLOSES standard input while a sign-in waits for its code has called the sign-in off: the approval
  did not come, and the caller is the one who knows why. That ends as a stopped command - the same `failed` answer
  under `--json`, exit 1 - and not as a tool failure, so it files no error report. A blank line is different: it is
  a caller that answered with nothing, and it is reported like any other failure.

The store is opened exactly as `license list` opens it (`license_ops._open`).
"""

from __future__ import annotations

import json
import sys
from typing import List, Optional

import click

from cc_shared import axi_output

from . import axi_cli, license_ops, license_signin, usage_errors
from .license_signin import SignInError

RENEW_FIELDS = ["account", "role", "result", "sign-in-expires", "signed-in", "browser", "detail"]
RENEW_DEFAULT_FIELDS = ["account", "role", "result", "sign-in-expires"]

_LIST = license_ops.LIST
_USAGE_SIGNED_IN = "cc-devthrottle license usage --fields account,state,sign-in-expires"


def _fail(ex: SignInError, json_answer: Optional[dict] = None) -> None:
    """Say why it failed and exit 1. With `json_answer`, standard output first carries the failure as one JSON
    object - `result` and the `reason` word, never the sentence - so the Director reads it and writes its own."""
    if json_answer is not None:
        print(json.dumps({**json_answer, "reason": ex.kind}, indent=2), flush=True)
    axi_cli.fail(f"{ex} (reason: {ex.kind})", ex.next_steps or [_LIST])


class _CalledOff(SignInError):
    """The caller closed standard input instead of sending a code: it called the sign-in off."""

    def __init__(self):
        super().__init__(license_signin.APPROVAL_FAILED, "standard input was closed before a code arrived: the "
                         "caller called this sign-in off")


def _stdin_approval(url: str, email: str) -> str:
    """The approval done elsewhere: say the URL, read the code back as one line."""
    print(f"authorize-url: {url}", flush=True)
    print(f"waiting: approve it for {email} on the machine with the browser profile, then send the code as one "
          "line on standard input", file=sys.stderr, flush=True)
    line = sys.stdin.readline()
    if line == "":
        raise _CalledOff()
    if not line.strip():
        raise SignInError(license_signin.APPROVAL_FAILED, "no code arrived on standard input")
    return line.strip()


def sign_in(email: str, browser: Optional[str], remote_approval: bool, default_config_dir: Optional[str],
            store_dir: Optional[str], json_output: bool, ask_when_no_browser: bool = False) -> None:
    if browser and remote_approval:
        axi_cli.usage_error("--browser and --remote-approval cannot be used together: the approval happens either "
                            "here in a browser profile, or on another machine.")
    if ask_when_no_browser and (browser or remote_approval):
        axi_cli.usage_error("--ask-when-no-browser chooses between the profile set here and another machine by "
                            "itself; it cannot be used with --browser or --remote-approval.")
    store = license_ops._open(default_config_dir, store_dir)
    try:
        if remote_approval:
            approve = _stdin_approval
        elif ask_when_no_browser:
            # The owner's setting on this machine wins (contract 8.3); with none, the approval is asked for.
            profile = license_signin.load_browsers(store).get(email.strip().lower())
            approve = license_signin.browser_approver(profile) if profile else _stdin_approval
        else:
            approve = license_signin.browser_approver(browser or license_signin.browser_for(store, email))
        result = license_signin.sign_in(store, email, approve)
    except _CalledOff as ex:
        # Not a failure of this tool, and not reported as one: the caller stopped it, as Ctrl+C would.
        if json_output:
            print(json.dumps({"accountEmail": email.strip().lower(), "result": "failed", "reason": ex.kind},
                             indent=2), flush=True)
        print(f"Stopped: {ex} (reason: {ex.kind})", file=sys.stderr, flush=True)
        raise click.Abort() from None
    except SignInError as ex:
        _fail(ex, {"accountEmail": email.strip().lower(), "result": "failed"} if json_output else None)
    license_ops._report_recovery(store)
    if json_output:
        print(json.dumps(result.to_json(), indent=2))
        return
    verb = "Renewed" if result.renewed else "Added"
    ends = f" Its sign-in ends about {result.sign_in_expires_at_utc}." if result.sign_in_expires_at_utc else \
        " The sign-in carries no end date."
    axi_cli.write_lines(f"{verb}: {result.email} is held as a spare in {result.config_dir}, signed in at "
                        f"{result.signed_in_at_utc}.{ends}")
    axi_cli.print_next([_LIST, _USAGE_SIGNED_IN])


def approve(url: str, email: str, browser: str, json_output: bool = False) -> None:
    try:
        code = license_signin.approve_in_browser(url, email, browser)
    except SignInError as ex:
        _fail(ex, {"accountEmail": email.strip().lower(), "result": "refused"} if json_output else None)
    if json_output:
        print(json.dumps({"accountEmail": email.strip().lower(), "result": "approved", "code": code}, indent=2),
              flush=True)
        return
    # Only the code, so the caller can hand it to the waiting login as it is.
    print(code, flush=True)


def set_browser(email: str, profile: str, default_config_dir: Optional[str], store_dir: Optional[str],
                json_output: bool) -> None:
    store = license_ops._open(default_config_dir, store_dir)
    try:
        path = license_signin.set_browser(store, email, profile)
    except SignInError as ex:
        _fail(ex)
    if json_output:
        print(json.dumps({"accountEmail": email.strip().lower(), "browser": profile.strip(), "file": path}, indent=2))
        return
    axi_cli.write_lines(f"Set: {email.strip().lower()} is signed in with the browser profile \"{profile.strip()}\" "
                        f"(kept in {path}).")
    axi_cli.print_next([f"cc-devthrottle license sign-in {email.strip().lower()}", "cc-devthrottle license renew"])


def get_browser(email: str, default_config_dir: Optional[str], store_dir: Optional[str], json_output: bool) -> None:
    """Which profile is set for the account here. None set is an answer, not a failure: the Director asks this
    before it starts a sign-in that only a profile on this machine could approve."""
    store = license_ops._open(default_config_dir, store_dir)
    account = email.strip().lower()
    try:
        profile = license_signin.load_browsers(store).get(account)
    except SignInError as ex:
        _fail(ex)
    if json_output:
        print(json.dumps({"accountEmail": account, "browser": profile}, indent=2))
        return
    if profile:
        axi_cli.write_lines(f"{account} is signed in with the browser profile \"{profile}\" on this machine.")
        axi_cli.print_next([f"cc-devthrottle license sign-in {account}"])
    else:
        axi_cli.write_lines(f"No browser profile is set for {account} on this machine.")
        axi_cli.print_next([f"cc-devthrottle license set-browser {account} \"<profile name>\"",
                            "cc-devthrottle browser list"])


def renew(account: Optional[str], force: bool, json_output: bool, fields: Optional[str],
          default_config_dir: Optional[str], store_dir: Optional[str], check: bool = False) -> None:
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, RENEW_FIELDS, RENEW_DEFAULT_FIELDS)
    store = license_ops._open(default_config_dir, store_dir)
    try:
        rows = license_signin.renew_due(store, account=account, force=force, check=check)
    except SignInError as ex:
        _fail(ex)
    license_ops._report_recovery(store)
    failed = [r for r in rows if r.failed]
    if json_output:
        print(json.dumps({"count": len(rows), "failed": len(failed), "accounts": [r.to_json() for r in rows]},
                         indent=2))
    else:
        results = ("renewed", "due", "not-due", "unknown-expiry")
        breakdown = [(label, n) for label in results if (n := sum(1 for r in rows if r.result == label))]
        if failed:
            breakdown.append(("failed", len(failed)))
        records = [{
            "account": r.account, "role": r.role, "result": r.result, "sign-in-expires": r.sign_in_expires_at_utc,
            "signed-in": r.signed_in_at_utc, "browser": r.browser, "detail": r.detail,
        } for r in rows]
        blocks: List[str] = [axi_output.format_count(len(rows), breakdown=breakdown or None)]
        if rows:
            blocks.append(axi_output.render_list("accounts", chosen, [{f: r[f] for f in chosen} for r in records]))
        else:
            blocks.append("This machine holds no account to renew.")
        help_lines = []
        if failed:
            help_lines.append("cc-devthrottle license renew --fields account,result,detail")
        help_lines.append("cc-devthrottle license renew --json")
        blocks.append(axi_output.format_help(help_lines))
        axi_output.write_blocks(sys.stdout, *blocks)
    if failed:
        axi_cli.fail(
            f"{len(failed)} of {len(rows)} renewals failed: "
            + " ".join(f"{r.account}: {r.detail}" for r in failed),
            ["cc-devthrottle license renew --fields account,result,detail", "cc-devthrottle browser list"],
        )

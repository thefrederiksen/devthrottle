"""Automation-browser operations for cc-devthrottle.

These verbs manage DevThrottle's OWN drivable browsers - the signed-in-once Chromium instances an
agent attaches to through browser-harness. They act on the Director this session belongs to
(named by CC_DIRECTOR_ID, reached through the Gateway): a browser's debug port is loopback and its
data directory is on this machine, so browsers are machine-local and only an agent on the same
machine can drive them.

All rendering decisions the user sees (status label, account, the attach environment) are FOLDED by
the Director and returned on the DTO; this module only lays them out.
"""

from __future__ import annotations

import functools
import json
from typing import Any, Dict, List, Optional

from rich import box
from rich.console import Console
from rich.table import Table

# session_ops installs the ASCII-only Rich patch at import; cli imports it eagerly, so tables here
# render with plain ASCII too. cc_shared.gateway is the one door to the fleet.
from cc_shared import gateway

from . import axi_cli


def _mine() -> str:
    """The path prefix for the browsers on THIS session's own machine.

    A browser is machine-local by construction - a loopback debug port and a profile directory on one
    disk - so the only thing that can drive it is the Director on that machine. Before the
    Remove-the-network-port mission that was implicit: the command line called its own Director over
    loopback, so "this machine" was wherever the call landed. Going through the Gateway makes the
    machine something that has to be NAMED, and the honest name is the Director this session belongs
    to, which the session is told at launch. It is not resolved from a machine name the caller typed,
    so `browser` can only ever mean the browsers beside the agent asking.
    """
    import os
    director_id = (os.environ.get("CC_DIRECTOR_ID") or "").strip()
    if not director_id:
        raise gateway.GatewayError(
            "CC_DIRECTOR_ID is not set, so this session cannot say which machine's browsers to use. "
            "The automation browsers belong to one machine and are reached through the Director that "
            "owns it; these commands only work inside a DevThrottle session."
        )
    return f"directors/{director_id}/browsers"

console = Console()

_CREATE_USAGE = 'cc-devthrottle browser create --name "<name>" --browser chrome'
_LIST = "cc-devthrottle browser list"


def _name_arg(name: str) -> str:
    return axi_cli.quoted(name, "<name>")


def _browsers() -> List[Dict[str, Any]]:
    """Every automation browser on this machine, each already folded (status/account/attach)."""
    payload = gateway.get_json(_mine()) or {}
    return payload.get("browsers", payload.get("Browsers", [])) or []


def _resolve(target: str) -> Dict[str, Any]:
    """Resolve a user-typed id or name to its browser DTO, or exit with a helpful list.

    Resolving here (rather than passing a raw name into the URL) keeps every subsequent call on the
    URL-safe slug id and lets us name the available browsers when nothing matches.
    """
    browsers = _browsers()
    key = target.strip().lower()
    for b in browsers:
        if gateway.field(b, "id", "Id").lower() == key:
            return b
    for b in browsers:
        if gateway.field(b, "name", "Name").lower() == key:
            return b

    if browsers:
        names = ", ".join(f'"{gateway.field(b, "name", "Name")}"' for b in browsers)
        axi_cli.fail(
            f'No automation browser matching "{target}". On this machine: {names}.',
            ["cc-devthrottle browser list"],
        )
    axi_cli.fail(
        f'No automation browser matching "{target}". There are none on this machine yet.',
        [_CREATE_USAGE],
    )


def _reports_gateway_failures(fn):
    """Turn a Gateway failure into the sentence the owner accepted, never a traceback.

    Every function below calls the shared transport, which raises `gateway.GatewayError` for a missing
    CC_GATEWAY_URL, a missing session key, or an unreachable Gateway. Nothing caught it, so `browser
    list` on a machine with no Gateway printed a Rich stack trace and exited 1. The mission's accepted
    cost is "no Gateway means no agent tooling" - accepted specifically because the user would be told
    so in one clear sentence naming the remedy.

    One decorator rather than eight try/except blocks: the defect was that a handler could be written
    without remembering the catch, and eight copies would leave that same trap for the ninth.
    """

    @functools.wraps(fn)
    def wrapper(*args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except gateway.GatewayError as err:
            axi_cli.fail(str(err), ["cc-devthrottle session whoami", "cc-devthrottle setup status"])

    return wrapper

@_reports_gateway_failures
def list_browsers(json_output: bool) -> None:
    """List the automation browsers on this machine."""
    browsers = _browsers()

    if json_output:
        print(json.dumps(browsers, indent=2))
        return

    if not browsers:
        console.print(
            "No automation browsers on this machine yet. Create one with:\n"
            '  cc-devthrottle browser create --name "Center Consulting" --browser chrome'
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NAME")
    table.add_column("BROWSER")
    table.add_column("STATUS")
    table.add_column("ACCOUNT")
    for b in browsers:
        name = gateway.field(b, "name", "Name") or "(unnamed)"
        kind = gateway.field(b, "browser", "Browser") or "-"
        status = gateway.field(b, "statusLabel", "StatusLabel") or gateway.field(b, "status", "Status") or "-"
        account = gateway.field(b, "account", "Account") or "-"
        table.add_row(name, kind, status, account)

    console.print(table)


@_reports_gateway_failures
def create_browser(name: str, browser: str, json_output: bool) -> None:
    """Register a new drivable browser (does not launch it)."""
    dto = gateway.post_json(_mine(), {"name": name, "browser": browser})
    if json_output:
        print(json.dumps(dto, indent=2))
        return
    bname = gateway.field(dto, "name", "Name")
    kind = gateway.field(dto, "browser", "Browser")
    axi_cli.write_lines(
        f'Created browser "{bname}" ({kind}).',
        f"Sign it in once with:  cc-devthrottle browser signin {_name_arg(bname)}",
    )
    axi_cli.print_next([f"cc-devthrottle browser signin {_name_arg(bname)}", _LIST])


@_reports_gateway_failures
def signin_browser(target: str, done: bool, json_output: bool) -> None:
    """Open the account page for a one-time human sign-in, or (with --done) record it complete."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    dto = gateway.post_json(f"{_mine()}/{bid}/signin", {"done": bool(done)})
    if json_output:
        print(json.dumps(dto, indent=2))
        return

    bname = gateway.field(dto, "name", "Name")
    if done:
        axi_cli.write_lines(f'Recorded: "{bname}" is signed in and ready to drive.')
        axi_cli.print_next([f"cc-devthrottle browser start {_name_arg(bname)}"])
    else:
        axi_cli.write_lines(
            f'Opened the sign-in page in "{bname}". Sign in BY HAND in that window (credentials are '
            "never automated).",
            f"When the account is signed in, run:  cc-devthrottle browser signin {_name_arg(bname)} --done",
        )
        axi_cli.print_next([f"cc-devthrottle browser signin {_name_arg(bname)} --done"])


@_reports_gateway_failures
def start_browser(target: str, json_output: bool) -> None:
    """Launch the browser if it is down, then print how to attach to it."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    dto = gateway.post_json(f"{_mine()}/{bid}/start", {})
    if json_output:
        print(json.dumps(dto, indent=2))
        return

    bname = gateway.field(dto, "name", "Name")
    status = gateway.field(dto, "statusLabel", "StatusLabel")
    bu_name = gateway.field(dto, "buName", "BuName")
    bu_url = gateway.field(dto, "buCdpUrl", "BuCdpUrl")
    attach_line = _attach_line(bname)
    axi_cli.write_lines(
        f'Started "{bname}" ({status}). Attach the harness with:',
        f"  {attach_line}",
        f"    BU_NAME={bu_name}",
        f"    BU_CDP_URL={bu_url}",
    )
    axi_cli.print_next([attach_line, f"cc-devthrottle browser stop {_name_arg(bname)}"])


def _attach_line(name: object) -> str:
    """The line that points the harness at a started browser. The name sits in single quotes inside
    double quotes, so it is written in only when `_name_arg` would write it AND it holds no single
    quote; otherwise the line carries the placeholder."""
    safe = isinstance(name, str) and _name_arg(name) == f'"{name}"' and "'" not in name
    return f"eval \"$(cc-devthrottle browser attach '{name if safe else '<name>'}')\""


@_reports_gateway_failures
def attach_browser(target: str) -> None:
    """Print ONLY the two export lines, so `eval "$(... attach 'X')"` points the harness at it."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    info = gateway.get_json(f"{_mine()}/{bid}/attach")
    bu_name = gateway.field(info, "buName", "BuName")
    bu_url = gateway.field(info, "buCdpUrl", "BuCdpUrl")
    # Plain print, no Rich: this output is meant to be eval'd by a shell.
    print(f"export BU_NAME={bu_name}")
    print(f"export BU_CDP_URL={bu_url}")


@_reports_gateway_failures
def stop_browser(target: str, json_output: bool) -> None:
    """Close a running browser cleanly. Its login and folder are kept; only the process exits."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    dto = gateway.post_json(f"{_mine()}/{bid}/stop", {})
    if json_output:
        print(json.dumps(dto, indent=2))
        return
    bname = gateway.field(dto, "name", "Name")
    status = gateway.field(dto, "statusLabel", "StatusLabel")
    axi_cli.write_lines(f'Stopped "{bname}" ({status}). Its login is kept - start it again any time.')
    axi_cli.print_next([f"cc-devthrottle browser start {_name_arg(bname)}", _LIST])


@_reports_gateway_failures
def rename_browser(target: str, to: str, json_output: bool) -> None:
    """Rename a browser's label (its id, port, and folder are unchanged)."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    dto = gateway.post_json(f"{_mine()}/{bid}/rename", {"name": to})
    if json_output:
        print(json.dumps(dto, indent=2))
        return
    new_name = gateway.field(dto, "name", "Name")
    axi_cli.write_lines(f'Renamed to "{new_name}".')
    axi_cli.print_next([f"cc-devthrottle browser start {_name_arg(new_name)}", _LIST])


@_reports_gateway_failures
def remove_browser(target: str, json_output: bool) -> None:
    """Stop the browser, delete its folder, and drop it from the registry."""
    browser = _resolve(target)
    bid = gateway.field(browser, "id", "Id")
    bname = gateway.field(browser, "name", "Name")
    result = gateway.delete(f"{_mine()}/{bid}")
    if json_output:
        print(json.dumps(result, indent=2))
        return
    axi_cli.write_lines(f'Removed "{bname}".')
    axi_cli.print_next([_LIST, _CREATE_USAGE])

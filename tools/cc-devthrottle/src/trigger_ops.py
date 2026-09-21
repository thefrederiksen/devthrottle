"""Gateway factory trigger operations for cc-devthrottle.

A trigger is a check with no model in it that a Director runs on an interval. When the check counts work,
the Gateway starts a named session; every check is recorded, whatever it came to. The Gateway decides a
trigger's status (OK or RED) and this module only prints it.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

from . import axi_cli  # noqa: E402

TIMEOUT_SECONDS = 10
MINIMUM_INTERVAL_SECONDS = 60
DEFAULT_RUN_LIMIT = 20

GatewayError = gateway.GatewayError
gateway_override: Optional[str] = None

_LIST = "cc-devthrottle trigger list"
_ADD_USAGE = (
    'cc-devthrottle trigger add --name <name> --factory <factory> --agent "<factory agent>" '
    '--machine <machine> --repo "<path>" --check "<command>" --every 5m --prompt "<prompt with {count}>"'
)
_OFF_HINT = (
    "Its factory agents switch is off: "
    'set "factoryAgents": { "enabled": true } in the Gateway\'s config.json and restart it.'
)

_INTERVAL = re.compile(r"^\s*(\d+)\s*([smh]?)\s*$", re.IGNORECASE)


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def parse_interval(text: str) -> int:
    """'5m', '90s', '1h' or a bare number of seconds, as seconds. Raises ValueError with the reason."""
    match = _INTERVAL.match(text or "")
    if not match:
        raise ValueError(f"'{text}' is not an interval; write it as 90s, 5m, 1h, or a number of seconds")
    number, unit = int(match.group(1)), match.group(2).lower()
    seconds = number * {"": 1, "s": 1, "m": 60, "h": 3600}[unit]
    if seconds < MINIMUM_INTERVAL_SECONDS:
        raise ValueError(f"'{text}' is shorter than the minimum interval of one minute")
    return seconds


def format_interval(seconds: Any) -> str:
    if not isinstance(seconds, int) or seconds <= 0:
        return "-"
    if seconds % 3600 == 0:
        return f"{seconds // 3600}h"
    if seconds % 60 == 0:
        return f"{seconds // 60}m"
    return f"{seconds}s"


class TriggerClient:
    """Talks to one Gateway's /triggers surface with this session's own key."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or gateway.gateway_base_url()).rstrip("/")
        self._token = gateway.session_key()

    def _request(self, method: str, path: str, body: Optional[Dict[str, Any]] = None) -> Any:
        url = f"{self.base_url}{path}"
        headers = {"Accept": "application/json", "Authorization": f"Bearer {self._token}"}
        try:
            resp = requests.request(method, url, json=body, headers=headers, timeout=TIMEOUT_SECONDS)
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(f"Gateway not reachable at {self.base_url}.") from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(f"Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s.") from exc

        if 200 <= resp.status_code < 300:
            # The shared guard, not a bare resp.json(): a path no route matches can fall through to the
            # Gateway's web app and answer HTTP 200 with a page.
            return gateway.parse_json_body(resp, self.base_url)
        raise GatewayError(_gateway_message(resp))

    def list(self) -> List[Dict[str, Any]]:
        data = self._request("GET", "/triggers")
        triggers = data.get("triggers") if isinstance(data, dict) else None
        if not isinstance(triggers, list):
            # Absent is not empty: an answer with no list must never read as "no triggers".
            raise GatewayError(f"the Gateway at {self.base_url} answered /triggers with no list of triggers")
        return triggers

    def get(self, key: str) -> Dict[str, Any]:
        return self._request("GET", f"/triggers/{_path(key)}")

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._request("POST", "/triggers", body)

    def set_paused(self, key: str, paused: bool) -> Dict[str, Any]:
        return self._request("POST", f"/triggers/{_path(key)}/{'pause' if paused else 'resume'}")

    def runs(self, key: str, limit: int) -> Dict[str, Any]:
        return self._request("GET", f"/triggers/{_path(key)}/runs?limit={limit}")


def _path(key: str) -> str:
    return requests.utils.quote(key.strip(), safe="")


def _gateway_message(resp: requests.Response) -> str:
    try:
        data = resp.json()
        if isinstance(data, dict) and data.get("error"):
            return f"{data['error']} (HTTP {resp.status_code})"
    except ValueError:
        pass
    if resp.status_code == 404:
        # No route answered at all - not "no such trigger", which the Gateway says in words.
        return f"the Gateway has no trigger routes (HTTP 404). {_OFF_HINT}"
    text = (resp.text or "").strip()
    return f"Gateway returned HTTP {resp.status_code}" + (f": {text[:200]}" if text else "")


def _client() -> TriggerClient:
    return TriggerClient(base_url=gateway_override)


def _fail(error: Exception, key: Optional[str] = None) -> None:
    message = str(error)
    next_commands = [_LIST]
    if key:
        next_commands.append(f"cc-devthrottle trigger show {axi_cli.quoted(key, '<name>')}")
    axi_cli.fail(message, next_commands)


def _v(value: Any) -> str:
    return "-" if value is None or value == "" else str(value)


def _status(trigger: Dict[str, Any]) -> str:
    kind = str(trigger.get("status") or "").upper() or "?"
    text = trigger.get("statusText") or ""
    return kind if not text or text == "OK" else f"{kind} - {text}"


def add(
    name: str, factory: str, agent: str, machine: str, repo: str, check: str, every: str, prompt: str,
    paused: bool, json_output: bool,
) -> None:
    try:
        interval = parse_interval(every)
    except ValueError as ex:
        axi_cli.usage_error(f"--every: {ex}. Full form: {_ADD_USAGE}")
        return

    body = {
        "name": name, "factory": factory, "factoryAgent": agent, "machine": machine, "repoPath": repo,
        "checkCommand": check, "intervalSeconds": interval, "prompt": prompt, "paused": paused,
    }
    try:
        created = _client().create(body)
    except GatewayError as ex:
        _fail(ex)
        return

    if json_output:
        print(json.dumps(created, indent=2))
        return
    axi_cli.write_lines(
        f"Created trigger {_v(created.get('name'))} ({_v(created.get('id'))}): checks every "
        f"{format_interval(created.get('intervalSeconds'))} on {_v(created.get('machine'))}"
        + (", paused." if created.get("paused") else "."))
    ref = axi_cli.quoted(created.get("name"), "<name>")
    axi_cli.print_next([f"cc-devthrottle trigger show {ref}", f"cc-devthrottle trigger runs {ref}"])


def list_triggers(json_output: bool) -> None:
    try:
        triggers = _client().list()
    except GatewayError as ex:
        _fail(ex)
        return

    if json_output:
        print(json.dumps(triggers, indent=2))
        return
    if not triggers:
        axi_cli.write_lines("No triggers in this account.")
        axi_cli.print_next([_ADD_USAGE])
        return

    lines = [f"{len(triggers)} trigger(s):"]
    for t in triggers:
        lines.append(
            f"  {_v(t.get('name'))}  [{_status(t)}]"
            f"  {'PAUSED  ' if t.get('paused') else ''}every {format_interval(t.get('intervalSeconds'))}"
            f" on {_v(t.get('machine'))}  last check: {_v(t.get('lastCheckUtc'))} ({_v(t.get('lastOutcome'))})")
    axi_cli.write_lines(*lines)
    axi_cli.print_next(["cc-devthrottle trigger show <name>", "cc-devthrottle trigger runs <name>"])


def show(key: str, json_output: bool) -> None:
    try:
        t = _client().get(key)
    except GatewayError as ex:
        _fail(ex, key)
        return

    if json_output:
        print(json.dumps(t, indent=2))
        return
    axi_cli.write_lines(
        f"{_v(t.get('name'))} ({_v(t.get('id'))})",
        f"  Status:        {_status(t)}",
        f"  Paused:        {'yes' if t.get('paused') else 'no'}",
        f"  Factory:       {_v(t.get('factory'))}",
        f"  Factory agent: {_v(t.get('factoryAgent'))}",
        f"  Machine:       {_v(t.get('machine'))}",
        f"  Repository:    {_v(t.get('repoPath'))}",
        f"  Check:         {_v(t.get('checkCommand'))}",
        f"  Every:         {format_interval(t.get('intervalSeconds'))}",
        f"  Prompt:        {_v(t.get('prompt'))}",
        f"  Last check:    {_v(t.get('lastCheckUtc'))} ({_v(t.get('lastOutcome'))})",
        f"  Last session:  {_v(t.get('lastSessionId'))}",
        f"  Created:       {_v(t.get('createdUtc'))} by {_v(t.get('createdBy'))}",
    )
    ref = axi_cli.quoted(t.get("name"), "<name>")
    axi_cli.print_next([
        f"cc-devthrottle trigger runs {ref}",
        f"cc-devthrottle trigger {'resume' if t.get('paused') else 'pause'} {ref}",
    ])


def set_paused(key: str, paused: bool) -> None:
    try:
        t = _client().set_paused(key, paused)
    except GatewayError as ex:
        _fail(ex, key)
        return
    word = "Paused" if paused else "Resumed"
    tail = (" Its checks still run and are recorded; it starts no session until resumed." if paused
            else " A check that counts work starts a session again.")
    axi_cli.write_lines(f"{word} trigger {_v(t.get('name'))}.{tail}")
    ref = axi_cli.quoted(t.get("name"), "<name>")
    axi_cli.print_next([
        f"cc-devthrottle trigger {'resume' if paused else 'pause'} {ref}",
        f"cc-devthrottle trigger show {ref}",
    ])


def runs(key: str, limit: int, json_output: bool) -> None:
    if limit < 1:
        axi_cli.usage_error("--limit must be at least 1.")
        return
    try:
        data = _client().runs(key, limit)
    except GatewayError as ex:
        _fail(ex, key)
        return

    history = data.get("runs") if isinstance(data, dict) else None
    if not isinstance(history, list):
        axi_cli.fail(f"the Gateway answered with no run history for trigger '{key}'", [_LIST])
        return
    if json_output:
        print(json.dumps(data, indent=2))
        return
    if not history:
        axi_cli.write_lines(f"No checks recorded yet for trigger {key}.")
        axi_cli.print_next([f"cc-devthrottle trigger show {axi_cli.quoted(key, '<name>')}"])
        return

    lines = [f"{len(history)} most recent check(s) of {key}, newest first:"]
    for r in history:
        detail = ""
        if r.get("sessionId"):
            detail = f" session {r['sessionId']}"
        if r.get("reason"):
            detail += f" reason: {r['reason']}"
        lines.append(f"  {_v(r.get('checkedUtc'))}  {_v(r.get('outcome'))}  count={_v(r.get('count'))}{detail}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"cc-devthrottle trigger show {axi_cli.quoted(key, '<name>')}"])

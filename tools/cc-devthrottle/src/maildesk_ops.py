"""The Mail Desk, as the command line shows it (Mail Desk mission, phase 1).

The Mail Desk reads an account's mailbox, decides what each message is - a rule's route, an allowed sender, a
blocked one, a question to a model - and records every decision. Phase 1 holds the channels, the rules document and
the decisions on the Gateway; this module reads and changes them under `/gateway/mail-desk`:

- `maildesk status`                             the switch, the rules version, every channel, and today's decisions
- `maildesk channel list|add|remove`            the mailboxes the desk reads
- `maildesk mode <address> observe|live`        whether a channel only records, or also acts
- `maildesk rules show|history`                 the rules document by version, and every change with who and why
- `maildesk allow|block|rule|route add|remove`  one entry of the rules document; each change is a new version
- `maildesk decisions`                          what the desk decided, newest first, with every filter on the Gateway
- `maildesk settings`                           the two thresholds, the spend limit, what was spent (phase 3)
- `maildesk set <name> <value>`                 change one setting; every change is recorded with who and why

THE GATEWAY RULES, THIS MODULE PRINTS (repository rule 7). Every label - a channel's mode, whether it is signed in,
how its last check went - is printed as the Gateway wrote it, and every refusal is the Gateway's own `error` and
`detail`. Every filter is sent to the Gateway, so `--json` answers the same question the plain output does.

TWO CHECKS ARE MADE HERE, BEFORE ANY REQUEST. A change with no `--why`, or a blank one, is refused with exit 2:
nothing is recorded without a reason, and a request that the Gateway would refuse anyway is not worth sending. And a
value from a fixed set (`--layer`, `--decision`, `--kind`, a mode, `--do`) or a `<factory>/<seat>` is checked, so a
typing mistake names the valid values at once.

THE SWITCH. Every Mail Desk route answers 404 with no body while the switch is off for the calling account. That
answer is said in one sentence here; a 404 that carries a body (`no_such_channel`, `no_such_entry`) is the Gateway's
refusal and is printed as such.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple
from urllib.parse import urlencode

import requests

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

TIMEOUT_SECONDS = 10
PREFIX = "/gateway/mail-desk"

# The vocabulary, as the Gateway holds it in MailDeskWords. Checked here only to name the valid values at once.
LAYERS = ("rule", "allow", "block", "sender-screen", "route-model")
DECISIONS = ("route", "archive", "junk", "keep-for-owner", "mail-desk-agent", "failed")
MODES = ("observe", "live")
CHANGE_KINDS = ("rule", "allow", "block", "route", "channel", "mode", "setting")
DO_WORDS = ("archive", "keep-for-owner")

# The settings, as MailDeskSettingNames holds them, and each one's range (MailDeskSettings): checked here only so a
# value out of range is refused before any request, naming the range at once.
SETTING_RANGES = {
    "route-threshold": (0.5, 1.0),
    "spam-threshold": (0.5, 1.0),
    "spend-limit": (0.0, 100.0),
}
SETTING_NAMES = tuple(SETTING_RANGES)

#: The one sentence for a Mail Desk route that answers 404 with no body.
SWITCHED_OFF = "Mail Desk is not switched on for this account."

GatewayError = gateway.GatewayError
gateway_override: Optional[str] = None

_TOOL = "cc-devthrottle maildesk"
_STATUS = f"{_TOOL} status"
_CHANNELS = f"{_TOOL} channel list"
_RULES = f"{_TOOL} rules show"
_HISTORY = f"{_TOOL} rules history"
_DECISIONS = f"{_TOOL} decisions"
_SETTINGS = f"{_TOOL} settings"
_ROUTE_FORM = "<factory>/<seat>"


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


class MailDeskClient:
    """Talks to one Gateway's /gateway/mail-desk surface with this session's own key."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or gateway.gateway_base_url()).rstrip("/")
        self._token = gateway.session_key()

    def get(self, path: str, query: Optional[Dict[str, Any]] = None) -> Any:
        return self._request("GET", path, query=query)

    def post(self, path: str, body: Dict[str, Any]) -> Any:
        return self._request("POST", path, body=body)

    def _request(
        self, method: str, path: str, body: Optional[Dict[str, Any]] = None, query: Optional[Dict[str, Any]] = None,
    ) -> Any:
        url = f"{self.base_url}{PREFIX}{path}"
        sent = {k: v for k, v in (query or {}).items() if v is not None}
        if sent:
            url += "?" + urlencode(sent)
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
        raise GatewayError(_refusal(resp), status=resp.status_code)


def _refusal(resp: requests.Response) -> str:
    """The Gateway's own words for a refused request: its `error`, and its `detail` when it gave one."""
    if resp.status_code == 404 and not (resp.content or b"").strip():
        return SWITCHED_OFF
    try:
        data = resp.json()
    except ValueError:
        data = None
    if isinstance(data, dict) and data.get("error"):
        detail = data.get("detail")
        words = str(data["error"]) + (f" - {detail}" if detail else "")
        return f"{words} (HTTP {resp.status_code})"
    text = (resp.text or "").strip()
    return f"Gateway returned HTTP {resp.status_code}" + (f": {text[:200]}" if text else "")


def _client() -> MailDeskClient:
    return MailDeskClient(base_url=gateway_override)


def _call(fn, next_commands: Sequence[str]) -> Any:
    """`fn()`, or exit 1 with the Gateway's own sentence and what to run next."""
    try:
        return fn()
    except GatewayError as ex:
        message = str(ex)
        axi_cli.fail(message, [axi_cli.CHECK_GATEWAY] if message == SWITCHED_OFF else list(next_commands))


# ---------- the checks made before any request ----------


def require_why(why: Optional[str]) -> str:
    """The reason for a change, or exit 2: nothing is changed without one."""
    if why is None or not why.strip():
        axi_cli.usage_error('--why is required and may not be blank: say why this change is made, --why "<text>".')
    return why.strip()


def require_one_of(flag: str, value: Optional[str], valid: Sequence[str]) -> Optional[str]:
    """`value` when it is one of `valid` (or not given), else exit 2 listing the valid values."""
    if value is None or value in valid:
        return value
    axi_cli.usage_error(f"unknown {flag} value '{value}'. Valid values: {', '.join(valid)}")
    return None


def require_count(flag: str, value: Optional[int], minimum: int) -> Optional[int]:
    if value is not None and value < minimum:
        axi_cli.usage_error(f"{flag} must be at least {minimum}.")
    return value


def parse_route(text: str, flag: str) -> Dict[str, str]:
    """`<factory>/<seat>` as `{factory, seat}`, or exit 2 with the expected form."""
    parts = (text or "").split("/")
    if len(parts) != 2 or not parts[0].strip() or not parts[1].strip():
        axi_cli.usage_error(f"{flag} '{text}' is not a route; write it as {_ROUTE_FORM}, for example mail-desk/intake.")
    return {"factory": parts[0].strip(), "seat": parts[1].strip()}


def parse_do(text: str) -> Any:
    """`--do`: `archive`, `keep-for-owner`, or `route:<factory>/<seat>` as `{route: {factory, seat}}`."""
    if text in DO_WORDS:
        return text
    if text.startswith("route:"):
        return {"route": parse_route(text[len("route:"):], "--do route:")}
    axi_cli.usage_error(f"unknown --do value '{text}'. Valid values: archive, keep-for-owner, route:{_ROUTE_FORM}")
    return None


# ---------- printing what the Gateway answered ----------


def _cell(value: Any) -> Any:
    """One value for a list row, in full: a scalar as it is, anything else as its JSON."""
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    return json.dumps(value)


def _label(obj: Any) -> Any:
    return obj.get("label") if isinstance(obj, dict) else None


def _by_decision(by_decision: Any) -> str:
    """The Gateway's per-decision counts in the order it gave them, zeros included."""
    if not isinstance(by_decision, dict) or not by_decision:
        return "-"
    return ", ".join(f"{name} {count}" for name, count in by_decision.items())


def _route_text(route: Any) -> Optional[str]:
    if isinstance(route, dict) and route.get("factory") is not None:
        return f"{route.get('factory')}/{route.get('seat')}"
    return None


def _today(channel: Dict[str, Any]) -> Any:
    today = channel.get("today")
    return today.get("total") if isinstance(today, dict) else None


def _channel_records(channels: List[Any], with_removed: bool) -> Tuple[List[str], List[Dict[str, Any]]]:
    fields = ["address", "mode", "mode-label", "signed-in", "last-check", "today", "machines", "provider"]
    if with_removed:
        fields.append("removed")
    records = []
    for c in channels:
        c = c if isinstance(c, dict) else {}
        machines = c.get("machines")
        record = {
            "address": _cell(c.get("address")),
            "mode": _cell(c.get("mode")),
            "mode-label": _cell(c.get("modeLabel")),
            "signed-in": _cell(_label(c.get("signedIn"))),
            "last-check": _cell(_label(c.get("lastCheck"))),
            "today": _cell(_today(c)),
            "machines": " ".join(str(m) for m in machines) if isinstance(machines, list) else _cell(machines),
            "provider": _cell(c.get("provider")),
        }
        if with_removed:
            record["removed"] = _cell(c.get("removedUtc"))
        records.append(record)
    return fields, records


def _line(text: str) -> str:
    """One result line, as one line of ASCII whatever the Gateway's values in it carry."""
    return axi_cli.ascii_text(text)


def _write(*blocks: str) -> None:
    """Write result blocks to standard output: `_line` lines and `render_list` blocks, both already ASCII."""
    axi_output.write_blocks(sys.stdout, *blocks)


def _list_of(answer: Any, key: str, what: str) -> List[Any]:
    """The list under `key`. Absent is not empty: an answer with no list must never read as "none"."""
    items = answer.get(key) if isinstance(answer, dict) else None
    if not isinstance(items, list):
        axi_cli.fail(f"the Gateway answered {what} with no list of {key}", [axi_cli.CHECK_GATEWAY])
    return items


def _count(answer: Dict[str, Any], items: List[Any]) -> str:
    shown = answer.get("count")
    shown = shown if isinstance(shown, int) and not isinstance(shown, bool) else len(items)
    total = answer.get("total")
    if isinstance(total, int) and not isinstance(total, bool) and total > shown:
        return f"count: {shown} of {total} total"
    return f"count: {shown}"


# ---------- reads ----------


def status(json_output: bool) -> None:
    answer = _call(lambda: _client().get("/status"), [_STATUS])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    answer = answer if isinstance(answer, dict) else {}
    channels = _list_of(answer, "channels", "/status")
    today = answer.get("today") if isinstance(answer.get("today"), dict) else {}
    fields, records = _channel_records(channels, with_removed=False)
    _write(
        _line(f"enabled: {'yes' if answer.get('enabled') is True else 'no'}"),
        _line(f"rules-version: {answer.get('rulesVersion')}"),
        _line(f"today: {today.get('date')} ({today.get('timeZone')}) total {today.get('total')}"),
        _line(f"by-decision: {_by_decision(today.get('byDecision'))}"),
        _line(f"spend: {_label(answer.get('spend')) or '-'}"),
        axi_output.render_list("channels", fields, records),
    )
    lines = [axi_cli.ascii_text(str(h)).strip() for h in (answer.get("help") or []) if str(h).strip()]
    axi_cli.print_next(lines or [_DECISIONS, _CHANNELS, _RULES])


def channel_list(all_channels: bool, json_output: bool) -> None:
    answer = _call(lambda: _client().get("/channels", {"all": "true" if all_channels else None}), [_STATUS])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    channels = _list_of(answer, "channels", "/channels")
    fields, records = _channel_records(channels, with_removed=all_channels)
    _write(_count(answer, channels), axi_output.render_list("channels", fields, records))
    if not channels:
        # The Gateway names the command that adds one, with every provider it accepts (rule 7).
        lines = [axi_cli.ascii_text(str(h)).strip() for h in (answer.get("help") or []) if str(h).strip()]
        if lines:
            axi_cli.print_next(lines)
        return
    axi_cli.print_next([_STATUS, f"{_DECISIONS} --channel <address>"])


def rules_show(version: Optional[int], json_output: bool) -> None:
    require_count("--version", version, 0)
    answer = _call(lambda: _client().get("/rules", {"version": version}), [_HISTORY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    answer = answer if isinstance(answer, dict) else {}
    document = answer.get("document") if isinstance(answer.get("document"), dict) else {}

    def entries(key: str) -> List[Dict[str, Any]]:
        items = document.get(key)
        return [e for e in items if isinstance(e, dict)] if isinstance(items, list) else []

    rules = [{
        "name": _cell(r.get("name")),
        "subject-contains": _joined((r.get("when") or {}).get("subjectContains")),
        "from": _joined((r.get("when") or {}).get("fromAny")),
        "route": _route_text(r.get("route")),
    } for r in entries("rules")]
    allow = [{"from": _cell(a.get("from")), "what": _cell(a.get("what")), "do": _do_text(a.get("do"))}
             for a in entries("allow")]
    block = [{"from": _cell(b.get("from")), "why": _cell(b.get("why"))} for b in entries("block")]
    routes = [{"route": _route_text(r), "handles": _cell(r.get("handles"))} for r in entries("routes")]
    _write(
        _line(f"version: {answer.get('version')}"),
        _line(f"saved: {answer.get('savedUtc') or '-'} by {answer.get('savedBy') or '-'}"),
        axi_output.render_list("rules", ["name", "subject-contains", "from", "route"], rules),
        axi_output.render_list("allow", ["from", "what", "do"], allow),
        axi_output.render_list("block", ["from", "why"], block),
        axi_output.render_list("routes", ["route", "handles"], routes),
    )
    axi_cli.print_next([_HISTORY, f'{_TOOL} allow add <pattern> --what "<text>" --do archive --why "<text>"'])


def _do_text(do: Any) -> Any:
    """An allow entry's `do` as it is typed: `archive`, `keep-for-owner`, or `route:<factory>/<seat>`."""
    if isinstance(do, dict) and _route_text(do.get("route")):
        return f"route:{_route_text(do.get('route'))}"
    return _cell(do)


def _joined(values: Any) -> Optional[str]:
    if not isinstance(values, list) or not values:
        return None
    return " | ".join(str(v) for v in values)


def rules_history(count: Optional[int], kind: Optional[str], json_output: bool) -> None:
    require_count("-n", count, 1)
    require_one_of("--kind", kind, CHANGE_KINDS)
    answer = _call(lambda: _client().get("/rules/history", {"n": count, "kind": kind}), [_RULES])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    changes = _list_of(answer, "changes", "/rules/history")
    records = []
    for c in changes:
        c = c if isinstance(c, dict) else {}
        before, after = c.get("versionBefore"), c.get("versionAfter")
        records.append({
            "id": _cell(c.get("id")),
            "at": _cell(c.get("atUtc")),
            "kind": _cell(c.get("kind")),
            "action": _cell(c.get("action")),
            "subject": _cell(c.get("subject")),
            "version": f"{before}->{after}" if after is not None else None,
            "who": _cell(c.get("whoInWords")),
            "why": _cell(c.get("why")),
        })
    _write(
        _count(answer, changes),
        axi_output.render_list("changes", ["id", "at", "kind", "action", "subject", "version", "who", "why"], records),
    )
    axi_cli.print_next([_RULES, f"{_RULES} --version <N>"])


def decisions(
    since: Optional[str], layer: Optional[str], decision: Optional[str], channel: Optional[str],
    count: Optional[int], json_output: bool,
) -> None:
    require_one_of("--layer", layer, LAYERS)
    require_one_of("--decision", decision, DECISIONS)
    require_count("-n", count, 1)
    query = {"since": since, "layer": layer, "decision": decision, "channel": channel, "n": count}
    answer = _call(lambda: _client().get("/decisions", query), [_STATUS])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    rows = _list_of(answer, "decisions", "/decisions")
    records = []
    for d in rows:
        d = d if isinstance(d, dict) else {}
        records.append({
            "id": _cell(d.get("id")),
            "received": _cell(d.get("receivedUtc")),
            "channel": _cell(d.get("channel")),
            "sender": _cell(d.get("sender")),
            "subject": _cell(d.get("subject")),
            "layer": _cell(d.get("layer")),
            "decision": _cell(d.get("decision")),
            "route": _route_text(d.get("route")),
            "mode": _cell(d.get("mode")),
            "outcome": _cell(d.get("outcome")),
            "reason": _cell(d.get("reason")),
        })
    fields = ["id", "received", "channel", "sender", "subject", "layer", "decision", "route", "mode", "outcome",
              "reason"]
    _write(
        _count(answer, rows),
        _line(f"by-decision: {_by_decision(answer.get('byDecision'))}"),
        axi_output.render_list("decisions", fields, records),
    )
    axi_cli.print_next([f"{_DECISIONS} --decision <decision>", f"{_DECISIONS} --since <iso-time>", _STATUS])


# ---------- changes ----------


def _channel_changed(
    answer: Any, verb: str, address: str, proof: str, accept: Optional[Callable[[Any], bool]] = None,
) -> None:
    """Print a channel change - but only once the answered row carries `proof`, the field the change sets.

    The address is on every channel row, changed or not, so it proves only an addition. A removal is proved by
    `removedUtc` being set, and a mode change by `mode` being the mode asked for; a row that came back unchanged
    exits 1 rather than being announced as a change."""
    axi_cli.confirmed(answer, [proof], f"the {verb} of channel {address}", [_CHANNELS], accept=accept)
    confirmed = axi_cli.confirmed(answer, ["address"], f"the {verb} of channel {address}", [_CHANNELS])
    fields, records = _channel_records([answer], with_removed=verb == "removal")
    _write(
        _line(f"{verb.capitalize()} of channel {confirmed} recorded."),
        axi_output.render_list("channel", fields, records),
    )
    axi_cli.print_next([_CHANNELS, f"{_HISTORY} --kind {'mode' if verb == 'mode change' else 'channel'}"])


def channel_add(address: str, provider: str, machines: List[str], why: Optional[str]) -> None:
    reason = require_why(why)
    body = {"address": address, "provider": provider, "machines": list(machines or []), "why": reason}
    answer = _call(lambda: _client().post("/channels", body), [_CHANNELS])
    _channel_changed(answer, "addition", address, "address")


def channel_remove(address: str, why: Optional[str]) -> None:
    reason = require_why(why)
    answer = _call(lambda: _client().post("/channels/remove", {"address": address, "why": reason}), [_CHANNELS])
    _channel_changed(answer, "removal", address, "removedUtc")


def mode(address: str, new_mode: str, why: Optional[str]) -> None:
    reason = require_why(why)
    require_one_of("mode", new_mode, MODES)
    body = {"address": address, "mode": new_mode, "why": reason}
    answer = _call(lambda: _client().post("/channels/mode", body), [_CHANNELS])
    _channel_changed(answer, "mode change", address, "mode", accept=lambda answered: answered == new_mode)


def _rules_changed(answer: Any, what: str) -> None:
    version = axi_cli.confirmed(answer, ["version"], what, [_HISTORY])
    change = answer.get("change") if isinstance(answer.get("change"), dict) else {}
    _write(
        _line(f"Saved rules version {version}: {change.get('kind')} {change.get('action')} {change.get('subject')}."),
        _line(f"who: {change.get('whoInWords') or '-'}"),
        _line(f"why: {change.get('why') or '-'}"),
    )
    axi_cli.print_next([_RULES, _HISTORY])


def rules_add(list_name: str, entry: Dict[str, Any], reason: str) -> None:
    """Add one entry to the `list_name` list (`allow`, `block`, `rule`, `route`) of the rules document."""
    body = dict(entry, why=reason)
    answer = _call(lambda: _client().post(f"/rules/{list_name}/add", body), [_RULES])
    _rules_changed(answer, f"the {list_name} addition")


def rules_remove(list_name: str, key: str, why: Optional[str]) -> None:
    """Remove the entry named `key` - a pattern, a rule's name, or `factory/seat` - from `list_name`."""
    body = {"key": key, "why": require_why(why)}
    answer = _call(lambda: _client().post(f"/rules/{list_name}/remove", body), [_RULES])
    _rules_changed(answer, f"the {list_name} removal of {key}")


def allow_add(pattern: str, what: str, do: str, why: Optional[str]) -> None:
    reason = require_why(why)
    rules_add("allow", {"from": pattern, "what": what, "do": parse_do(do)}, reason)


def block_add(pattern: str, why: Optional[str]) -> None:
    # The block entry's own `why` is the change's --why: one reason, said once.
    rules_add("block", {"from": pattern}, require_why(why))


def rule_add(name: str, subject_contains: List[str], senders: List[str], route: str, why: Optional[str]) -> None:
    reason = require_why(why)
    when: Dict[str, Any] = {}
    if subject_contains:
        when["subjectContains"] = list(subject_contains)
    if senders:
        when["fromAny"] = list(senders)
    rules_add("rule", {"name": name, "when": when, "route": parse_route(route, "--route")}, reason)


def route_add(route: str, handles: str, why: Optional[str]) -> None:
    reason = require_why(why)
    rules_add("route", dict(parse_route(route, "route"), handles=handles), reason)


def route_remove(route: str, why: Optional[str]) -> None:
    require_why(why)
    parsed = parse_route(route, "route")
    rules_remove("route", f"{parsed['factory']}/{parsed['seat']}", why)


# ---------- the settings (phase 3) ----------


def settings(json_output: bool) -> None:
    answer = _call(lambda: _client().get("/settings"), [_STATUS])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    answer = answer if isinstance(answer, dict) else {}
    _write(
        _line(f"route-threshold: {answer.get('routeThreshold')}"),
        _line(f"spam-threshold: {answer.get('spamThreshold')}"),
        _line(f"spend-limit: {answer.get('spendLimitUsd')}"),
        _line(f"spent: {answer.get('spentUsd')}"),
        _line(f"stopped: {'yes' if answer.get('stopped') is True else 'no'}"),
        _line(f"spend: {answer.get('label') or '-'}"),
    )
    axi_cli.print_next([f'{_TOOL} set <name> <value> --why "<text>"', f"{_HISTORY} --kind setting"])


def parse_setting(name: str, value: str) -> float:
    """The value for setting `name`, as a number in its range, or exit 2 naming the names or the range."""
    require_one_of("setting", name, SETTING_NAMES)
    try:
        number = float(value)
    except (TypeError, ValueError):
        number = float("nan")
    low, high = SETTING_RANGES[name]
    if not (low <= number <= high):  # also refuses nan and inf
        axi_cli.usage_error(f"{name} is a number from {low} to {high}; '{value}' is not.")
    return number


def set_setting(name: str, value: str, why: Optional[str]) -> None:
    reason = require_why(why)
    number = parse_setting(name, value)
    body = {"name": name, "value": number, "why": reason}
    answer = _call(lambda: _client().post("/settings", body), [_SETTINGS])
    change = axi_cli.confirmed(answer, ["change"], f"the change of {name}", [_SETTINGS],
                               accept=lambda c: isinstance(c, dict) and c.get("subject") == name)
    detail = change.get("detail") if isinstance(change.get("detail"), dict) else {}
    after = answer.get("settings") if isinstance(answer.get("settings"), dict) else {}
    _write(
        _line(f"Setting {name} changed from {detail.get('from')} to {detail.get('to')}."),
        _line(f"who: {change.get('whoInWords') or '-'}"),
        _line(f"why: {change.get('why') or '-'}"),
        _line(f"spend: {after.get('label') or '-'}"),
    )
    axi_cli.print_next([_SETTINGS, f"{_HISTORY} --kind setting"])

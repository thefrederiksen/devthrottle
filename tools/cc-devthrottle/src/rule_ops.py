"""Session Rules operations for cc-devthrottle (Session Rules part 2, phase 2).

A rule is a standing instruction the account gives about its sessions. Owner decision D7: "Any agent should
be able to change and set up the rules." So a session reaches /gateway/rules with its own key, and every
create, change, promote and delete is recorded by the Gateway with the session and agent that made it -
`rules history` reads that record back.

The Gateway is the authority on what a rule may be: a check the product does not ship, a missing scope or a
ceiling of zero is refused there, in words, and this module prints the refusal verbatim. A rule is ALWAYS
created in dry run, and a change puts it back in dry run; only `rules promote` makes one live.

A CHANGE CARRIES THE RULE'S BODY GENERICALLY. `rules change` sends only the fields it was given, and the
Gateway lays them over the rule's current body. `--body` takes any field of that body as JSON, so a field the
Gateway learns (a kind, a new scope) can be changed without this module learning its name first.
"""

from __future__ import annotations

import json
import re
import sys
import uuid
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

import requests

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402

from . import axi_cli  # noqa: E402
from . import usage_errors  # noqa: E402

TIMEOUT_SECONDS = 10
DEFAULT_HISTORY_COUNT = 20
#: The most changes the Gateway answers in one read (SessionRuleEndpoints.MaxHistoryLimit).
MAX_HISTORY_COUNT = 500

GatewayError = gateway.GatewayError
gateway_override: Optional[str] = None

#: The states a rule can be in, as the Gateway writes them, in the order a rule meets them.
RULE_STATES = ["dry_run", "live"]

#: The changes the history records, as the Gateway writes them.
CHANGE_ACTIONS = ["create", "change", "promote", "delete"]

RULE_LIST_FIELDS = [
    "id", "state", "kind", "scope", "instruction", "screen-description", "trigger-words", "checks",
    "cooldown", "daily-cap", "applies-to", "event", "tool", "handler", "match", "result",
    "promoted-by", "created", "updated",
]
RULE_LIST_DEFAULT_FIELDS = ["id", "state", "kind", "scope", "instruction"]

#: The kind of rule that listens to an agent's hook rather than reading the screen (Session Rules part 2, phase 1).
HOOK_KIND = "hook"

#: The appliesTo parts that are lists of machines, written in --applies-to separated by ';'.
_APPLIES_TO_LISTS = ("machines", "exceptMachines")

HISTORY_FIELDS = ["when", "action", "rule", "session", "agent", "changed-by", "changed", "id"]
HISTORY_DEFAULT_FIELDS = ["when", "action", "rule", "session", "agent"]

_LIST = "cc-devthrottle rules list"
_CREATE_USAGE = (
    'cc-devthrottle rules create --instruction "<sentence>" --screen-description "<what it watches for>" '
    '--trigger-word <word> --scope all-sessions --cooldown 5m --daily-cap 5'
)
_CREATE_HOOK_USAGE = (
    'cc-devthrottle rules create --kind hook --instruction "<sentence>" --applies-to everywhere '
    '--event before_command --tool shell --handler match --match "<text>" --result deny'
)
_DURATION = re.compile(r"^\s*(\d+)\s*([smh]?)\s*$", re.IGNORECASE)


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


class RuleClient:
    """Talks to one Gateway's /gateway/rules surface with this session's own key."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or gateway.gateway_base_url()).rstrip("/")
        self._token = gateway.session_key()

    def _request(self, method: str, path: str, body: Optional[Dict[str, Any]] = None,
                 params: Optional[Dict[str, Any]] = None) -> Any:
        url = f"{self.base_url}{path}"
        headers = {"Accept": "application/json", "Authorization": f"Bearer {self._token}"}
        try:
            resp = requests.request(method, url, json=body, params=params, headers=headers, timeout=TIMEOUT_SECONDS)
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
        data = self._request("GET", "/gateway/rules")
        rules = data.get("rules") if isinstance(data, dict) else None
        if not isinstance(rules, list):
            # Absent is not empty: an answer with no list must never read as "no rules".
            raise GatewayError(f"the Gateway at {self.base_url} answered /gateway/rules with no list of rules")
        return rules

    def get(self, rule_id: str) -> Dict[str, Any]:
        return _rule_of(self._request("GET", f"/gateway/rules/{rule_id}"), self.base_url)

    def firings(self, rule_id: str) -> List[Dict[str, Any]]:
        data = self._request("GET", f"/gateway/rules/{rule_id}/firings")
        firings = data.get("firings") if isinstance(data, dict) else None
        if not isinstance(firings, list):
            raise GatewayError(f"the Gateway at {self.base_url} answered with no firing record for rule {rule_id}")
        return firings

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._request("POST", "/gateway/rules", body)

    def change(self, rule_id: str, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._request("PATCH", f"/gateway/rules/{rule_id}", body)

    def promote(self, rule_id: str, acknowledgement: str) -> Dict[str, Any]:
        return self._request("POST", f"/gateway/rules/{rule_id}/promote", {"acknowledgement": acknowledgement})

    def delete(self, rule_id: str) -> Dict[str, Any]:
        return self._request("DELETE", f"/gateway/rules/{rule_id}")

    def history(self, rule_id: Optional[str], session: Optional[str], action: Optional[str], limit: int) -> Dict[str, Any]:
        params: Dict[str, Any] = {"limit": limit}
        if rule_id:
            params["rule"] = rule_id
        if session:
            params["session"] = session
        if action:
            params["action"] = action
        data = self._request("GET", "/gateway/rules/history", params=params)
        if not isinstance(data, dict) or not isinstance(data.get("changes"), list):
            raise GatewayError(f"the Gateway at {self.base_url} answered /gateway/rules/history with no list of changes")
        return data


def _rule_of(data: Any, base_url: str) -> Dict[str, Any]:
    rule = data.get("rule") if isinstance(data, dict) else None
    if not isinstance(rule, dict) or not rule.get("id"):
        raise GatewayError(f"the Gateway at {base_url} answered with no rule")
    return rule


def _gateway_message(resp: requests.Response) -> str:
    try:
        data = resp.json()
        if isinstance(data, dict) and data.get("error"):
            return f"{data['error']} (HTTP {resp.status_code})"
    except ValueError:
        pass
    if resp.status_code == 404:
        # No route answered at all - not "no such rule", which the Gateway says in words.
        return "the Gateway has no rule routes at that address (HTTP 404). Is it a build with Session Rules?"
    text = (resp.text or "").strip()
    return f"Gateway returned HTTP {resp.status_code}" + (f": {text[:200]}" if text else "")


def _client() -> RuleClient:
    return RuleClient(base_url=gateway_override)


def _fail(error: Exception, rule_id: Optional[str] = None) -> None:
    next_commands = [_LIST]
    if rule_id:
        next_commands.append(f"cc-devthrottle rules show {axi_cli.bare(rule_id, '<rule-id>')}")
    axi_cli.fail(str(error), next_commands)


# ---------------------------------------------------------------------------------------------------
# Reading values the caller typed
# ---------------------------------------------------------------------------------------------------


def parse_duration(text: str, flag: str) -> int:
    """'5m', '90s', '1h' or a bare number of seconds, as seconds - a usage error when it is not one."""
    match = _DURATION.match(text or "")
    if not match:
        axi_cli.usage_error(f"{flag}: '{text}' is not a duration; write it as 90s, 5m, 1h, or a number of seconds.")
    number, unit = int(match.group(1)), match.group(2).lower()
    return number * {"": 1, "s": 1, "m": 60, "h": 3600}[unit]


def parse_scope(text: str) -> Any:
    """'all-sessions', or key=value pairs separated by commas, as the body's scope.

    The keys are passed through as written and the Gateway decides what they mean, so a scope the Gateway
    learns does not need this module changed. A pair without '=' is a usage error here."""
    value = (text or "").strip()
    if value.lower() == "all-sessions":
        return "all-sessions"
    scope: Dict[str, str] = {}
    for part in value.split(","):
        key, sep, item = part.partition("=")
        if not sep or not key.strip() or not item.strip():
            axi_cli.usage_error(
                f"--scope: '{text}' is not a scope. Write all-sessions, or key=value pairs such as "
                "repository=github.com/acme/app,machine=devlinux.")
        scope[key.strip()] = item.strip()
    return scope


def parse_applies_to(text: str) -> Dict[str, Any]:
    """'everywhere', or key=value pairs separated by commas, as the body's appliesTo.

    The keys are passed through as written and the Gateway decides what they mean and which shapes hold, as with
    --scope. machines and exceptMachines are lists, their names separated by ';'. A pair without '=' is a usage
    error here."""
    value = (text or "").strip()
    if value.lower() == "everywhere":
        return {"kind": "everywhere"}
    applies_to: Dict[str, Any] = {}
    for part in value.split(","):
        key, sep, item = part.partition("=")
        if not sep or not key.strip() or not item.strip():
            axi_cli.usage_error(
                f"--applies-to: '{text}' is not a place a rule applies. Write everywhere, or key=value pairs such as "
                "kind=repo_on_machine,repo=github.com/acme/app,machines=devlinux;laptop.")
        key = key.strip()
        applies_to[key] = ([m.strip() for m in item.split(";") if m.strip()] if key in _APPLIES_TO_LISTS
                           else item.strip())
    return applies_to


def _parse_json_object(text: str, flag: str) -> Dict[str, Any]:
    try:
        value = json.loads(text)
    except json.JSONDecodeError as exc:
        axi_cli.usage_error(f"{flag} is not valid JSON: {exc.msg} at line {exc.lineno} column {exc.colno}.")
    if not isinstance(value, dict):
        axi_cli.usage_error(f"{flag} has to be a JSON object naming the rule's fields, such as {{\"dailyCap\": 3}}.")
    return value


def build_body(
    body: Optional[str], body_file: Optional[str], instruction: Optional[str], screen_description: Optional[str],
    trigger_words: Optional[Sequence[str]], checks: Optional[str], scope: Optional[str], cooldown: Optional[str],
    daily_cap: Optional[int], kind: Optional[str] = None, applies_to: Optional[str] = None,
    event: Optional[str] = None, tool: Optional[str] = None, handler: Optional[str] = None,
    match: Optional[Sequence[str]] = None, result: Optional[str] = None,
) -> Dict[str, Any]:
    """The rule fields to send: --body or --file first, then each flag laid over it. Only what was given."""
    if body is not None and body_file is not None:
        axi_cli.usage_error("give the body once: --body or --file, not both.")
    fields: Dict[str, Any] = {}
    if body is not None:
        fields = _parse_json_object(body, "--body")
    elif body_file is not None:
        try:
            text = Path(body_file).read_text(encoding="utf-8")
        except OSError as exc:
            axi_cli.usage_error(f"--file: could not read '{body_file}': {exc.strerror or exc}.")
        fields = _parse_json_object(text, f"--file {body_file}")

    if instruction is not None:
        fields["instruction"] = instruction
    if screen_description is not None:
        fields["screenDescription"] = screen_description
    if trigger_words:
        fields["triggerWords"] = list(trigger_words)
    if checks is not None:
        try:
            parsed = json.loads(checks)
        except json.JSONDecodeError as exc:
            axi_cli.usage_error(f"--checks is not valid JSON: {exc.msg}.")
        if not isinstance(parsed, list):
            axi_cli.usage_error('--checks has to be a JSON list, such as [{"name": "matches_any", "arguments": {...}}], or [] for none.')
        fields["checks"] = parsed
    if scope is not None:
        fields["scope"] = parse_scope(scope)
    if cooldown is not None:
        fields["cooldownSeconds"] = parse_duration(cooldown, "--cooldown")
    if daily_cap is not None:
        fields["dailyCap"] = daily_cap
    # A hook rule's fields (Session Rules part 2, phase 1). Which kind may carry which is the Gateway's to rule.
    if kind is not None:
        fields["kind"] = kind
    if applies_to is not None:
        fields["appliesTo"] = parse_applies_to(applies_to)
    if event is not None:
        fields["event"] = event
    if tool is not None:
        fields["tool"] = tool
    if handler is not None:
        fields["handler"] = handler
    if match:
        # Literal text, sent exactly as typed: a space at the edge is part of what is matched.
        fields["match"] = list(match)
    if result is not None:
        fields["result"] = result
    return fields


def resolve_rule(reference: str, allow_unknown_id: bool = False) -> str:
    """A rule's full id from its full id or an unambiguous prefix of one. Ids are never guessed: a prefix that
    matches no rule or several is an error that lists what it matched.

    `allow_unknown_id` lets a full id through without asking the Gateway, for the history of a deleted rule."""
    ref = (reference or "").strip().lower()
    if not ref:
        axi_cli.usage_error("a rule id is needed; 'cc-devthrottle rules list' shows them.")
    try:
        return str(uuid.UUID(ref))
    except ValueError:
        pass
    if allow_unknown_id and len(ref) == 36:
        return ref
    try:
        rules = _client().list()
    except GatewayError as ex:
        _fail(ex)
    matches = [str(r.get("id")) for r in rules if str(r.get("id", "")).lower().startswith(ref)]
    if len(matches) == 1:
        return matches[0]
    if not matches:
        axi_cli.fail(f"no rule id starts with '{axi_output.escape_ascii(ref)}'.", [_LIST])
    axi_cli.fail(
        f"'{axi_output.escape_ascii(ref)}' starts {len(matches)} rule ids: {', '.join(matches)}. Give more of the id.",
        [_LIST])
    raise AssertionError("unreachable")


# ---------------------------------------------------------------------------------------------------
# Rendering
# ---------------------------------------------------------------------------------------------------


def _scope_text(scope: Any) -> str:
    if not isinstance(scope, dict):
        return str(scope) if scope else "-"
    parts = [f"{k}={v}" for k, v in scope.items() if v not in (None, "")]
    return ";".join(parts) if parts else "all-sessions"


def _is_hook(rule: Dict[str, Any]) -> bool:
    return rule.get("kind") == HOOK_KIND


def _rule_scope_text(rule: Dict[str, Any]) -> str:
    # A hook rule has no session scope - it says where it applies with appliesTo - so "all-sessions" would claim
    # a scope it does not have.
    return "-" if _is_hook(rule) else _scope_text(rule.get("scope"))


def _applies_to_text(applies_to: Any) -> str:
    """The appliesTo as the Gateway sent it: its kind, then each part that is set."""
    if not isinstance(applies_to, dict):
        return "-"
    parts = [str(applies_to.get("kind") or "-")]
    if applies_to.get("repo"):
        parts.append(f"repo={applies_to['repo']}")
    for key in _APPLIES_TO_LISTS:
        if applies_to.get(key):
            parts.append(f"{key}={';'.join(applies_to[key])}")
    return " ".join(parts)


def _rule_record(rule: Dict[str, Any], fields: Sequence[str]) -> Dict[str, Any]:
    readers = {
        "id": lambda r: r.get("id"),
        "state": lambda r: r.get("state"),
        "kind": lambda r: r.get("kind") or "-",
        "scope": _rule_scope_text,
        "applies-to": lambda r: _applies_to_text(r.get("appliesTo")),
        "event": lambda r: r.get("event") or "-",
        "tool": lambda r: r.get("tool") or "-",
        "handler": lambda r: r.get("handler") or "-",
        "match": lambda r: ";".join(r.get("match") or []) or "-",
        "result": lambda r: r.get("result") or "-",
        "instruction": lambda r: r.get("instruction"),
        "screen-description": lambda r: r.get("screenDescription"),
        "trigger-words": lambda r: ";".join(r.get("triggerWords") or []) or "-",
        "checks": lambda r: ";".join(r.get("checks") or []) or "-",
        "cooldown": lambda r: r.get("cooldownSeconds"),
        "daily-cap": lambda r: r.get("dailyCap"),
        "promoted-by": lambda r: r.get("promotedBy") or "-",
        "created": lambda r: r.get("createdUtc"),
        "updated": lambda r: r.get("updatedUtc"),
    }
    return {field: readers[field](rule) for field in fields}


def _changed_fields(change: Dict[str, Any]) -> str:
    before = change.get("before") if isinstance(change.get("before"), dict) else {}
    after = change.get("after") if isinstance(change.get("after"), dict) else {}
    if not before or not after:
        return "-"
    names = [k for k in after if before.get(k) != after.get(k)]
    return ";".join(names) if names else "-"


def _history_record(change: Dict[str, Any], fields: Sequence[str]) -> Dict[str, Any]:
    readers = {
        "id": lambda c: c.get("id"),
        "when": lambda c: c.get("occurredUtc"),
        "action": lambda c: c.get("action"),
        "rule": lambda c: c.get("ruleId"),
        "session": lambda c: c.get("sessionId") or "-",
        "agent": lambda c: c.get("agent") or "-",
        "changed-by": lambda c: c.get("changedBy"),
        "changed": _changed_fields,
    }
    return {field: readers[field](change) for field in fields}


def _state_after_write(rule: Dict[str, Any]) -> str:
    return "live" if rule.get("state") == "live" else "dry run"


# ---------------------------------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------------------------------


def list_rules(json_output: bool, state: Optional[str], fields: Optional[str]) -> None:
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, RULE_LIST_FIELDS, RULE_LIST_DEFAULT_FIELDS)
    wanted = None
    if state is not None:
        wanted = state.strip().lower().replace("-", "_")
        if wanted not in RULE_STATES:
            axi_cli.usage_error(
                f"unknown --state value '{axi_output.escape_ascii(state)}'. Valid states: {', '.join(RULE_STATES)}")

    try:
        everything = _client().list()
    except GatewayError as ex:
        _fail(ex)
    rows = [r for r in everything if wanted is None or r.get("state") == wanted]

    if json_output:
        # A bare array of the Gateway's rules, every field, filtered by --state and nothing else.
        print(json.dumps(rows, indent=2))
        return

    breakdown = [(s, n) for s in RULE_STATES if (n := sum(1 for r in rows if r.get("state") == s))] or None
    blocks = [
        axi_output.format_count(len(rows), total=len(everything) if wanted else None, breakdown=breakdown),
        axi_output.render_list("rules", chosen, [_rule_record(r, chosen) for r in rows]),
    ]
    if not rows:
        blocks.append("No rules in this account." if not everything else f"No rules in state '{wanted}'.")
        blocks.append(axi_output.format_help([_CREATE_USAGE]))
    else:
        blocks.append(axi_output.format_help([
            "cc-devthrottle rules show <rule-id>",
            "cc-devthrottle rules history",
        ]))
    axi_output.write_blocks(sys.stdout, *blocks)


def show(reference: str, json_output: bool) -> None:
    rule_id = resolve_rule(reference)
    try:
        client = _client()
        rule = client.get(rule_id)
        firings = client.firings(rule_id)
    except GatewayError as ex:
        _fail(ex, rule_id)

    if json_output:
        print(json.dumps({"rule": rule, "firings": firings}, indent=2))
        return

    last = firings[0] if firings else None
    # Each kind is shown with the fields it is written with: a hook rule's screen fields are empty and a change to
    # them is refused, and a screen rule has no trigger.
    if _is_hook(rule):
        kind_lines = [
            f"event: {rule.get('event')}",
            f"tool: {rule.get('tool')}",
            f"handler: {rule.get('handler')}",
            f"match: {'; '.join(rule.get('match') or []) or '-'}",
            f"result: {rule.get('result')}",
        ]
        change_example = f'cc-devthrottle rules change {rule_id} --match "<text>"'
    else:
        kind_lines = [
            f"screen-description: {rule.get('screenDescription')}",
            f"trigger-words: {', '.join(rule.get('triggerWords') or []) or '-'}",
            f"checks: {'; '.join(rule.get('checks') or []) or 'none'}",
            f"scope: {_scope_text(rule.get('scope'))}",
            f"cooldown: {rule.get('cooldownSeconds')}s",
            f"daily-cap: {rule.get('dailyCap')}",
        ]
        change_example = f"cc-devthrottle rules change {rule_id} --daily-cap <n>"
    axi_cli.write_lines(
        f"id: {rule.get('id')}",
        f"state: {rule.get('state')}",
        f"kind: {rule.get('kind') or '-'}",
        f"instruction: {rule.get('instruction')}",
        f"applies-to: {_applies_to_text(rule.get('appliesTo'))}",
        *kind_lines,
        f"promoted-by: {rule.get('promotedBy') or '-'}",
        f"created: {rule.get('createdUtc')}",
        f"updated: {rule.get('updatedUtc')}",
        f"firings: {len(firings)}" + (f" (last {last.get('occurredUtc')}: {last.get('decision')})" if last else ""),
    )
    commands = [f"cc-devthrottle rules history {rule_id}", change_example]
    if rule.get("state") != "live":
        commands.append(f'cc-devthrottle rules promote {rule_id} --acknowledgement "<what you are agreeing to>"')
    axi_cli.print_next(commands)


def create(fields: Dict[str, Any], json_output: bool) -> None:
    if not fields:
        axi_cli.usage_error(
            f"a rule needs its fields. Full form: {_CREATE_USAGE}; a hook rule: {_CREATE_HOOK_USAGE}; "
            "or --body '<json>'.")
    # A screen rule's checks are required by the Gateway as a list; none given means none asked for, which is said
    # out loud. A hook rule has no checks, so it is sent none.
    if not _is_hook(fields):
        fields.setdefault("checks", [])
    try:
        answer = _client().create(fields)
        rule = _rule_of(answer, gateway_override or "the Gateway")
    except GatewayError as ex:
        _fail(ex)

    if json_output:
        print(json.dumps(answer, indent=2))
        return
    rule_id = rule["id"]
    axi_cli.write_lines(f"Created rule {rule_id} in dry run: it records what it would do and types nothing.")
    axi_cli.print_next([
        f"cc-devthrottle rules show {rule_id}",
        f'cc-devthrottle rules promote {rule_id} --acknowledgement "<what you are agreeing to>"',
        f"cc-devthrottle rules history {rule_id}",
    ])


def change(reference: str, fields: Dict[str, Any], json_output: bool) -> None:
    if not fields:
        axi_cli.usage_error(
            "a change needs at least one field: --instruction, --screen-description, --trigger-word, --checks, "
            "--scope, --cooldown, --daily-cap, --applies-to, --event, --tool, --handler, --match, --result, "
            "or --body '<json>' for any field of the rule.")
    rule_id = resolve_rule(reference)
    try:
        answer = _client().change(rule_id, fields)
        rule = _rule_of(answer, gateway_override or "the Gateway")
    except GatewayError as ex:
        _fail(ex, rule_id)

    if json_output:
        print(json.dumps(answer, indent=2))
        return
    was_live = isinstance(answer, dict) and answer.get("wasLive") is True
    tail = (" It was live and is back in dry run: promote it again to make it live." if was_live
            else f" It is in {_state_after_write(rule)}.")
    axi_cli.write_lines(f"Changed rule {rule_id} ({', '.join(fields)}).{tail}")
    axi_cli.print_next([f"cc-devthrottle rules show {rule_id}", f"cc-devthrottle rules history {rule_id}"])


def promote(reference: str, acknowledgement: str, json_output: bool) -> None:
    if not (acknowledgement or "").strip():
        axi_cli.usage_error("--acknowledgement needs a sentence saying what you are agreeing to.")
    rule_id = resolve_rule(reference)
    try:
        answer = _client().promote(rule_id, acknowledgement)
        rule = _rule_of(answer, gateway_override or "the Gateway")
    except GatewayError as ex:
        _fail(ex, rule_id)

    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Rule {rule_id} is live, promoted by {rule.get('promotedBy') or '-'}.")
    axi_cli.print_next([f"cc-devthrottle rules show {rule_id}", f"cc-devthrottle rules history {rule_id}"])


def delete(reference: str, yes: bool, json_output: bool) -> None:
    rule_id = resolve_rule(reference)
    if not axi_cli.confirm_or_fail(f"Delete rule {rule_id}?", yes, "--yes"):
        axi_cli.fail("not deleted.", [f"cc-devthrottle rules delete {rule_id} --yes"])
    try:
        answer = _client().delete(rule_id)
    except GatewayError as ex:
        _fail(ex, rule_id)

    deleted = axi_cli.confirmed(answer, ["deleted"], f"the delete of rule {rule_id}", [_LIST],
                                accept=lambda v: isinstance(v, bool))
    if not deleted:
        axi_cli.fail(f"there is no rule with the id {rule_id}, so nothing was deleted.", [_LIST])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Deleted rule {rule_id}. Its firing record and its history are kept.")
    axi_cli.print_next([f"cc-devthrottle rules history {rule_id}"])


def history(reference: Optional[str], session: Optional[str], action: Optional[str], count: int,
            json_output: bool, fields: Optional[str]) -> None:
    if json_output and fields is not None:
        axi_cli.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, HISTORY_FIELDS, HISTORY_DEFAULT_FIELDS)
    if count < 1 or count > MAX_HISTORY_COUNT:
        axi_cli.usage_error(f"--count must be from 1 to {MAX_HISTORY_COUNT}.")
    if action is not None and action not in CHANGE_ACTIONS:
        axi_cli.usage_error(
            f"unknown --action value '{axi_output.escape_ascii(action)}'. Valid actions: {', '.join(CHANGE_ACTIONS)}")
    if session is not None and session.strip().lower() == "me":
        session = gateway.session_id()
        if not session:
            axi_cli.usage_error("--session me needs CC_SESSION_ID, which is set only inside a session.")
    rule_id = resolve_rule(reference, allow_unknown_id=True) if reference else None

    try:
        data = _client().history(rule_id, session, action, count)
    except GatewayError as ex:
        _fail(ex, rule_id)

    if json_output:
        # The Gateway's answer, filtered by the Gateway with every filter given.
        print(json.dumps(data, indent=2))
        return

    changes = data["changes"]
    breakdown = [(a, n) for a in CHANGE_ACTIONS if (n := sum(1 for c in changes if c.get("action") == a))] or None
    blocks = [
        axi_output.format_count(len(changes), breakdown=breakdown),
        axi_output.render_list("changes", chosen, [_history_record(c, chosen) for c in changes]),
    ]
    if not changes:
        blocks.append("No changes match." if (rule_id or session or action) else "No rule has been changed yet.")
        blocks.append(axi_output.format_help([_LIST]))
    else:
        blocks.append(axi_output.format_help([
            "cc-devthrottle rules history --json",
            "cc-devthrottle rules show <rule-id>",
        ]))
    axi_output.write_blocks(sys.stdout, *blocks)

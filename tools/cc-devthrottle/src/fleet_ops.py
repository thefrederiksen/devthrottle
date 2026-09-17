"""The Fleet Manager's commands: stored news (Ready, Finding, Decision), standing preferences, the
one digest it reads at the start of every conversation (the Fleet Manager mission, step 3), the
events about sessions it owns - each stop or death, kept until it is acknowledged (step 4), and the
one line of advice (and pick) it writes on a record for the owner's walkthrough (step 7).

Everything is kept on the Gateway, under /gateway/fleet-manager, and belongs to the account - so a
restarted or moved Fleet Manager reads back exactly what the old one filed.

OUTPUT FOLLOWS THE AXI STANDARD (docs/axi-standard.md). The default is compact: a count line, rows of
a few fields with full ids and names (never cut short), `count: 0` for an empty answer, and `help[]`
lines naming the next command. `--json` prints the Gateway's answer unchanged, with every filter
applied by the Gateway itself. Everything printed is ASCII: a value that is not plain is written as a
JSON string, which is ASCII by construction.
"""

from __future__ import annotations

import json
import re
import sys
import urllib.parse
from typing import Any, Dict, List, Optional

import typer

from . import session_ops
from .session_ops import gateway

PREFIX = "gateway/fleet-manager"

KINDS = ("ready", "finding", "decision")
STATUSES = ("open", "answered", "all")
RISKS = ("low", "medium", "high")
CHECKS = ("passed", "failed", "none")
EVENT_KINDS = ("stop", "died")
# Counted only when present, so the usual line reads as it always has.
EVENT_KINDS_WHEN_PRESENT = ("answered", "marked")

_GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
_PLAIN = re.compile(r"^[A-Za-z0-9 _./:@+()'?!;=<>#%&*~^|\[\]{}$-]*$")


# ---- output helpers ---------------------------------------------------------------------------------


def cell(value: Any) -> str:
    """One value in a compact row. Plain text is written as it is; anything else - a comma, a quote,
    a line break, text that is not ASCII, spaces at either end, or nothing at all - is written as a
    JSON string, so a reader can always split the row and read the value back exactly."""
    if value is None:
        return "-"
    text = str(value)
    if text and _PLAIN.match(text) and text == text.strip() and text != "-":
        return text
    return json.dumps(text, ensure_ascii=True)


def _out(line: str = "") -> None:
    print(line)


def _table(name: str, fields: List[str], rows: List[List[Any]]) -> None:
    _out(f"{name}[{len(rows)}]{{{','.join(fields)}}}:")
    for row in rows:
        _out("  " + ",".join(cell(v) for v in row))


def _help(lines: List[str]) -> None:
    if not lines:
        return
    _out(f"help[{len(lines)}]:")
    for line in lines:
        _out(f"  {line}")


def _print_json(obj: Any) -> None:
    # Plain print, not Rich: Rich rewraps long values and would break the parse.
    print(json.dumps(obj, indent=2, ensure_ascii=True))


def _fail(message: str, code: int = 1) -> None:
    print(f"error: {message}", file=sys.stderr)
    raise typer.Exit(code)


def _call(fn, *args, **kwargs) -> Any:
    try:
        return fn(*args, **kwargs)
    except gateway.GatewayError as err:
        _fail(str(err))


def _choice(value: Optional[str], valid: tuple, option: str) -> Optional[str]:
    """A closed value, checked before anything is sent. A wrong one is a usage error: exit 2, with the
    valid values named."""
    if value is None:
        return None
    v = value.strip().lower()
    if v not in valid:
        _fail(f"{option} '{value}' is not valid; use one of: {', '.join(valid)}", code=2)
    return v


def _counts_by_kind(outcomes: List[Dict[str, Any]]) -> str:
    return ", ".join(f"{k} {sum(1 for o in outcomes if o.get('kind') == k)}" for k in KINDS)


# ---- resolving ids ----------------------------------------------------------------------------------


def _resolve_id(given: str, rows: List[Dict[str, Any]], what: str, list_command: str) -> str:
    """A full id is used as it is. Anything else must be the start of exactly one id in `rows`."""
    text = (given or "").strip()
    if _GUID.match(text):
        return text.lower()
    if not text:
        _fail(f"give the {what} id; list them with {list_command}", code=2)
    matches = [r["id"] for r in rows if str(r.get("id", "")).lower().startswith(text.lower())]
    if not matches:
        _fail(f"no {what} id starts with '{text}'; list them with {list_command}")
    if len(matches) > 1:
        _fail(f"'{text}' matches {len(matches)} {what}s: {', '.join(matches)}; give more of the id")
    return matches[0]


def _pages(status: str, kind: Optional[str], count: int, cursor: Optional[str] = None):
    """Every page of the list, following the Gateway's nextCursor until it says none remain."""
    seen = set()
    while True:
        query = {"status": status, "count": str(count)}
        if kind:
            query["kind"] = kind
        if cursor:
            query["cursor"] = cursor
        page = _call(gateway.get_json, f"{PREFIX}/outcomes?{urllib.parse.urlencode(query)}")
        yield page
        cursor = page.get("nextCursor")
        if not page.get("hasMore") or not cursor:
            return
        if cursor in seen:
            _fail(f"the Gateway handed back cursor '{cursor}' twice; the list cannot be followed to its end")
        seen.add(cursor)


def _outcome_id(given: str) -> str:
    """A full id as it is; otherwise the start of exactly one id among EVERY record, every page followed."""
    if _GUID.match((given or "").strip()):
        return given.strip().lower()
    rows: List[Dict[str, Any]] = []
    if (given or "").strip():
        for page in _pages("all", None, 200):
            rows.extend(page.get("outcomes", []))
    return _resolve_id(given, rows, "outcome", "cc-devthrottle fleet outcomes --status all --all")


def _preference_id(given: str) -> str:
    if _GUID.match((given or "").strip()):
        return given.strip().lower()
    listed = _call(gateway.get_json, f"{PREFIX}/preferences")
    return _resolve_id(given, listed.get("preferences", []), "preference", "cc-devthrottle fleet preferences")


def _about_session(target: Optional[str]) -> Optional[str]:
    """The session a record is about, resolved the way every session command resolves a target."""
    if target is None or not target.strip():
        return None
    return session_ops.resolve_target_or_current(target)


# ---- filing -----------------------------------------------------------------------------------------


def _with_advice(body: Dict[str, Any], advice: Optional[str], pick: Optional[str]) -> Dict[str, Any]:
    """Add the advice and pick to a filing body - only when given, so a filing without them sends exactly the body
    it always sent. The one-line rule is the Gateway's: its refusal is printed as it comes."""
    if pick is not None and advice is None:
        _fail("--pick goes with --advice: give the one line of advice that explains the pick", code=2)
    if advice is not None:
        body["advice"] = advice
    if pick is not None:
        body["fleetManagerPick"] = pick
    return body


def _file(body: Dict[str, Any], json_output: bool) -> None:
    filed = _call(gateway.post_json, f"{PREFIX}/outcomes", body)
    if json_output:
        _print_json(filed)
        return
    oid = filed.get("id", "")
    _out(f"filed: {oid}")
    _out(f"kind: {filed.get('kind')}")
    _out(f"status: {filed.get('status')}")
    _out(f"title: {cell(filed.get('title'))}")
    if filed.get("advice") is not None:
        _out(f"advice: {cell(filed.get('advice'))}")
        _out(f"pick: {cell(filed.get('fleetManagerPick'))}")
    _help([
        f"cc-devthrottle fleet show {oid}",
        f'cc-devthrottle fleet answer {oid} "<the owner\'s words, exactly>"',
    ])


def file_ready(title: str, pr: str, risk: str, checks: str, tested: str, reviewed_by: str,
               change: str, session: Optional[str], json_output: bool,
               advice: Optional[str] = None, pick: Optional[str] = None) -> None:
    """File a READY record: work that is ready for the owner."""
    body = {
        "kind": "ready",
        "title": title,
        "sessionId": _about_session(session),
        "ready": {
            "pullRequest": pr,
            "risk": _choice(risk, RISKS, "--risk"),
            "checks": _choice(checks, CHECKS, "--checks"),
            "tested": tested,
            "reviewedBy": reviewed_by,
            "change": change,
        },
    }
    _file(_with_advice(body, advice, pick), json_output)


def file_finding(title: str, answer: str, reason: Optional[str], links: Optional[List[str]],
                 session: Optional[str], json_output: bool,
                 advice: Optional[str] = None, pick: Optional[str] = None) -> None:
    """File a FINDING record: a report or investigation that is finished."""
    body = {
        "kind": "finding",
        "title": title,
        "sessionId": _about_session(session),
        "finding": {"answer": answer, "reason": reason, "links": list(links or [])},
    }
    _file(_with_advice(body, advice, pick), json_output)


def file_decision(title: str, question: str, options: Optional[List[str]], recommend: Optional[str],
                  why: Optional[str], session: Optional[str], json_output: bool,
                  advice: Optional[str] = None, pick: Optional[str] = None) -> None:
    """File a DECISION record: something only the owner can settle."""
    opts = list(options or [])
    if len(opts) < 2:
        _fail(f"a decision needs at least two --option values, got {len(opts)}", code=2)
    if recommend is not None and recommend not in opts:
        _fail(f"--recommend '{recommend}' is not one of the options; use one of: "
              + ", ".join(opts), code=2)
    body = {
        "kind": "decision",
        "title": title,
        "sessionId": _about_session(session),
        "decision": {"question": question, "options": opts, "recommended": recommend, "why": why},
    }
    _file(_with_advice(body, advice, pick), json_output)


# ---- reading ----------------------------------------------------------------------------------------


def list_outcomes(status: str, kind: Optional[str], count: int, json_output: bool,
                  cursor: Optional[str] = None, every_page: bool = False) -> None:
    """The account's records, newest first. Default: the open ones, one page. `cursor` continues after an earlier
    page; `every_page` follows the cursor to the end, so every record is listed."""
    status = _choice(status, STATUSES, "--status") or "open"
    kind = _choice(kind, KINDS, "--kind")
    if count < 1 or count > 200:
        _fail(f"--count must be between 1 and 200, got {count}", code=2)
    if cursor is not None and not cursor.strip():
        _fail("--cursor is empty; give the nextCursor an earlier page printed", code=2)

    if every_page:
        rows: List[Dict[str, Any]] = []
        total = 0
        for page in _pages(status, kind, count, cursor):
            rows.extend(page.get("outcomes", []))
            total = page.get("total", total)
        answer = {"count": len(rows), "total": total, "hasMore": False, "nextCursor": None, "outcomes": rows}
    else:
        answer = next(_pages(status, kind, count, cursor))
    if json_output:
        _print_json(answer)
        return

    rows = answer.get("outcomes", [])
    base = f"cc-devthrottle fleet outcomes --status {status}" + (f" --kind {kind}" if kind else "")
    if not rows:
        filtered = status != "all" or kind is not None
        if filtered:
            total = _call(gateway.get_json, f"{PREFIX}/outcomes?status=all&count=1").get("total", 0)
            _out(f"count: 0 of {total} total (status {status}{', kind ' + kind if kind else ''})")
        else:
            _out("count: 0")
        _help(["cc-devthrottle fleet outcomes --status all"] if filtered else [])
        return

    # The Gateway counts every matching record; a page smaller than that says so, never silently.
    total = answer.get("total", len(rows))
    shown = f"{len(rows)} of {total}" if total > len(rows) else f"{len(rows)}"
    _out(f"count: {shown} ({_counts_by_kind(rows)}) status: {status}")
    next_cursor = answer.get("nextCursor") if answer.get("hasMore") else None
    if next_cursor:
        _out(f"nextCursor: {next_cursor}")
    _table("outcomes", ["id", "kind", "status", "title"],
           [[o.get("id"), o.get("kind"), o.get("status"), o.get("title")] for o in rows])
    first = rows[0].get("id")
    hints = []
    if next_cursor:
        hints.append(f"{base} --count {count} --cursor {next_cursor}   (the next page)")
        hints.append(f"{base} --all   (every page)")
    hints += [
        f"cc-devthrottle fleet show {first}",
        f'cc-devthrottle fleet answer {first} "<the owner\'s words, exactly>"',
    ]
    _help(hints)


def _print_outcome(o: Dict[str, Any]) -> None:
    _out(f"id: {o.get('id')}")
    _out(f"kind: {o.get('kind')}")
    _out(f"status: {o.get('status')}")
    _out(f"title: {cell(o.get('title'))}")
    _out(f"filedBy: {o.get('filedBy')}")
    _out(f"session: {cell(o.get('sessionId'))}")
    _out(f"createdAt: {o.get('createdAtUtc')}")
    ready, finding, decision = o.get("ready"), o.get("finding"), o.get("decision")
    if ready:
        _out(f"pullRequest: {cell(ready.get('pullRequest'))}")
        _out(f"risk: {ready.get('risk')}")
        _out(f"checks: {ready.get('checks')}")
        _out(f"change: {cell(ready.get('change'))}")
        _out(f"tested: {cell(ready.get('tested'))}")
        _out(f"reviewedBy: {cell(ready.get('reviewedBy'))}")
    if finding:
        _out(f"answer: {cell(finding.get('answer'))}")
        _out(f"reason: {cell(finding.get('reason'))}")
        links = finding.get("links") or []
        _out(f"links[{len(links)}]: {','.join(cell(x) for x in links)}")
    if decision:
        _out(f"question: {cell(decision.get('question'))}")
        options = decision.get("options") or []
        _out(f"options[{len(options)}]: {','.join(cell(x) for x in options)}")
        _out(f"recommended: {cell(decision.get('recommended'))}")
        _out(f"why: {cell(decision.get('why'))}")
    _out(f"advice: {cell(o.get('advice'))}")
    _out(f"pick: {cell(o.get('fleetManagerPick'))}")
    if o.get("ownerNote") is not None:
        _out(f"ownerNote: {cell(o.get('ownerNote'))}")
    if o.get("status") == "answered":
        _out(f"ownerAnswer: {cell(o.get('answer'))}")
        _out(f"answeredBy: {o.get('answeredBy')}")
        _out(f"answeredByRole: {cell(o.get('answeredByRole'))}")
        _out(f"answeredAt: {o.get('answeredAtUtc')}")
        if o.get("kind") == "decision":
            _out(f"answerMatchedOption: {'yes' if o.get('answerMatchedOption') else 'no'}")


def show_outcome(given: str, json_output: bool) -> None:
    """One record in full."""
    oid = _outcome_id(given)
    o = _call(gateway.get_json, f"{PREFIX}/outcomes/{gateway.path_segment(oid)}")
    if json_output:
        _print_json(o)
        return
    _print_outcome(o)
    if o.get("status") == "open":
        _help([
            f'cc-devthrottle fleet answer {oid} "<the owner\'s words, exactly>"',
            f'cc-devthrottle fleet advise {oid} "<one line of advice>" [--pick "<option key>"]',
        ])


def answer_outcome(given: str, words: str, json_output: bool) -> None:
    """Close a record with the owner's words, exactly as given. An answered record is never re-answered."""
    if words is None or not words.strip():
        _fail("give the owner's words, exactly as they said them", code=2)
    oid = _outcome_id(given)
    o = _call(gateway.post_json, f"{PREFIX}/outcomes/{gateway.path_segment(oid)}/answer", {"answer": words})
    if json_output:
        _print_json(o)
        return
    _out(f"answered: {o.get('id')}")
    _out(f"answeredByRole: {cell(o.get('answeredByRole'))}")
    _out(f"kind: {o.get('kind')}")
    _out(f"title: {cell(o.get('title'))}")
    if o.get("kind") == "decision":
        _out(f"answerMatchedOption: {'yes' if o.get('answerMatchedOption') else 'no'}")
    _help(["cc-devthrottle fleet outcomes"])


def advise_outcome(given: str, advice: str, pick: Optional[str], json_output: bool) -> None:
    """Write the Fleet Manager's one line of advice on an open record, and optionally its pick (step 7). Replaces what
    was there; leaving --pick out clears the pick. Only the marked Fleet Manager may; the Gateway says why otherwise."""
    if advice is None or not advice.strip():
        _fail("give the one line of advice, using what you know and the Wingman does not", code=2)
    oid = _outcome_id(given)
    o = _call(gateway.put_json, f"{PREFIX}/outcomes/{gateway.path_segment(oid)}/advice",
              {"advice": advice, "pick": pick})
    if json_output:
        _print_json(o)
        return
    _out(f"advised: {o.get('id')}")
    _out(f"title: {cell(o.get('title'))}")
    _out(f"advice: {cell(o.get('advice'))}")
    _out(f"pick: {cell(o.get('fleetManagerPick'))}")
    _help([f"cc-devthrottle fleet show {oid}"])


# ---- preferences ------------------------------------------------------------------------------------


def add_preference(text: str, json_output: bool) -> None:
    """Keep one standing preference, in the owner's words, exactly."""
    if text is None or not text.strip():
        _fail("give the owner's preference, in their own words", code=2)
    p = _call(gateway.post_json, f"{PREFIX}/preferences", {"text": text})
    if json_output:
        _print_json(p)
        return
    _out(f"kept: {p.get('id')}")
    _out(f"text: {cell(p.get('text'))}")
    _help(["cc-devthrottle fleet preferences", f"cc-devthrottle fleet forget {p.get('id')}"])


def _print_preferences(rows: List[Dict[str, Any]]) -> None:
    _table("preferences", ["id", "text"], [[p.get("id"), p.get("text")] for p in rows])


def list_preferences(json_output: bool) -> None:
    """Every standing preference, oldest first."""
    answer = _call(gateway.get_json, f"{PREFIX}/preferences")
    if json_output:
        _print_json(answer)
        return
    rows = answer.get("preferences", [])
    _out(f"count: {len(rows)}")
    if rows:
        _print_preferences(rows)
        _help([f"cc-devthrottle fleet forget {rows[0].get('id')}"])
    else:
        _help(['cc-devthrottle fleet prefer "<the owner\'s preference, verbatim>"'])


def forget_preference(given: str, json_output: bool) -> None:
    """Remove one standing preference."""
    pid = _preference_id(given)
    answer = _call(gateway.delete, f"{PREFIX}/preferences/{gateway.path_segment(pid)}")
    if json_output:
        _print_json(answer)
        return
    _out(f"forgot: {pid}")


# ---- events -----------------------------------------------------------------------------------------


def _event_verdict(e: Dict[str, Any]) -> List[Any]:
    """The verdict word and its label for one event row: the reading, why there is none, or - for a stop
    still waiting for its reading - the Gateway's own words for that."""
    if e.get("kind") == "died":
        return ["crashed" if e.get("crashed") else "exited", None]
    if e.get("kind") == "answered":
        # The owner's words, exactly: the record is already answered and the Fleet Manager acts on them.
        return ["owner answered", e.get("words")]
    if e.get("kind") == "marked":
        return ["now yours", e.get("detail")]
    if e.get("readingPending"):
        return ["waiting", e.get("readingNote")]
    if e.get("verdictWithheld"):
        return ["withheld", "this account's readings are still a shadow record"]
    v = e.get("verdict")
    if not v:
        return ["none", e.get("noVerdictReason")]
    if v.get("failed"):
        return ["failed", v.get("failureReason")]
    return [v.get("verdict"), v.get("label")]


def _events_table(rows: List[Dict[str, Any]]) -> None:
    _table("events", ["id", "kind", "sessionId", "name", "verdict", "label", "deliveredTo", "acknowledged"],
           # An answered event is about a record: its title stands in the name column.
           [[e.get("id"), e.get("kind"), e.get("sessionId"), e.get("outcomeTitle") if e.get("kind") == "answered" else e.get("sessionName"), *_event_verdict(e),
             e.get("deliveredTo"), "yes" if e.get("acknowledgedAtUtc") else "no"] for e in rows])


def _counts_by_event_kind(rows: List[Dict[str, Any]]) -> str:
    kinds = list(EVENT_KINDS) + [k for k in EVENT_KINDS_WHEN_PRESENT if any(e.get("kind") == k for e in rows)]
    return ", ".join(f"{k} {sum(1 for e in rows if e.get('kind') == k)}" for k in kinds)


def _event_pages(status: str, count: int, cursor: Optional[str] = None):
    """Every page of the event list, following the Gateway's nextCursor until it says none remain."""
    seen = set()
    while True:
        query = {"status": status, "count": str(count)}
        if cursor:
            query["cursor"] = cursor
        page = _call(gateway.get_json, f"{PREFIX}/events?{urllib.parse.urlencode(query)}")
        yield page
        cursor = page.get("nextCursor")
        if not page.get("hasMore") or not cursor:
            return
        if cursor in seen:
            _fail(f"the Gateway handed back cursor '{cursor}' twice; the list cannot be followed to its end")
        seen.add(cursor)


def list_events(show_all: bool, count: int, json_output: bool,
                cursor: Optional[str] = None, every_page: bool = False) -> None:
    """The events about sessions a Fleet Manager owns. Default: the unacknowledged ones, oldest first, one page.
    With --all, every event, newest first. `cursor` continues after an earlier page; `every_page` follows the
    cursor to the end."""
    if count < 1 or count > 200:
        _fail(f"--count must be between 1 and 200, got {count}", code=2)
    if cursor is not None and not cursor.strip():
        _fail("--cursor is empty; give the nextCursor an earlier page printed", code=2)
    status = "all" if show_all else "unacknowledged"
    if every_page:
        rows: List[Dict[str, Any]] = []
        total = 0
        note = None
        for page in _event_pages(status, count, cursor):
            rows.extend(page.get("events", []))
            total = page.get("total", total)
            note = page.get("deliveryNote")
        answer = {"count": len(rows), "total": total, "hasMore": False, "nextCursor": None,
                  "deliveryNote": note, "events": rows}
    else:
        answer = next(_event_pages(status, count, cursor))
    if json_output:
        _print_json(answer)
        return

    rows = answer.get("events", [])
    if not rows:
        if show_all:
            _out("count: 0")
            _help([])
        else:
            total = _call(gateway.get_json, f"{PREFIX}/events?status=all&count=1").get("total", 0)
            _out(f"count: 0 of {total} total (status unacknowledged)")
            _help(["cc-devthrottle fleet events --all"] if total else [])
        return

    total = answer.get("total", len(rows))
    shown = f"{len(rows)} of {total}" if total > len(rows) else f"{len(rows)}"
    waiting = sum(1 for e in rows if e.get("readingPending"))
    _out(f"count: {shown} ({_counts_by_event_kind(rows)}) status: {status}"
         + (f" waitingForReading: {waiting}" if waiting else ""))
    # Why the events are not reaching the Fleet Manager right now, in the Gateway's own words.
    if answer.get("deliveryNote"):
        _out(f"deliveryNote: {answer['deliveryNote']}")
    next_cursor = answer.get("nextCursor") if answer.get("hasMore") else None
    if next_cursor:
        _out(f"nextCursor: {next_cursor}")
    _events_table(rows)
    hints = []
    if next_cursor:
        base = "cc-devthrottle fleet events" + (" --all" if show_all else "")
        hints.append(f"{base} --count {count} --cursor {next_cursor}   (the next page)")
        hints.append(f"{base} --every-page   (every page)")
    # A stop still waiting for its reading is not acted on or acknowledged yet, so it is never the example.
    ready = [e for e in rows if not e.get("acknowledgedAtUtc") and not e.get("readingPending")]
    about_a_session = [e for e in ready if e.get("kind") in EVENT_KINDS and e.get("sessionId")]
    if about_a_session:
        hints.append(f"cc-devthrottle session buffer {about_a_session[0].get('sessionId')}")
    if ready:
        hints.append(f"cc-devthrottle fleet ack {ready[0].get('id')}")
    _help(hints)


def acknowledge_events(given: Optional[List[str]], ack_all: bool, json_output: bool) -> None:
    """Acknowledge events by id (or the start of one), or every unacknowledged one delivered to this session with
    --all. The Gateway refuses a stop still waiting for its reading, and then acknowledges nothing."""
    named = [g for g in (given or []) if g is not None]
    if ack_all and named:
        _fail("give event ids or --all, not both", code=2)
    if not ack_all and not named:
        _fail("give the event ids to acknowledge, or --all; list them with cc-devthrottle fleet events", code=2)

    if ack_all:
        body: Dict[str, Any] = {"all": True}
    else:
        listed: Optional[List[Dict[str, Any]]] = None
        ids = []
        for g in named:
            if _GUID.match(g.strip()):
                ids.append(g.strip().lower())
                continue
            if listed is None:
                # Every event, every page followed, so the start of an id is matched against all of them.
                listed = [e for page in _event_pages("all", 200) for e in page.get("events", [])]
            ids.append(_resolve_id(g, listed, "event", "cc-devthrottle fleet events --all"))
        body = {"ids": ids}

    answer = _call(gateway.post_json, f"{PREFIX}/events/ack", body)
    if json_output:
        _print_json(answer)
        return
    acked = answer.get("ids", [])
    _out(f"acknowledged: {answer.get('acknowledged', len(acked))}")
    _out(f"alreadyAcknowledged: {answer.get('alreadyAcknowledged', 0)}")
    _out(f"ids[{len(acked)}]: {','.join(acked)}")
    _help(["cc-devthrottle fleet events"])


# ---- digest -----------------------------------------------------------------------------------------


def digest(session: Optional[str], json_output: bool) -> None:
    """Everything the Fleet Manager reads at the start of a conversation, in one answer.

    The Gateway serves EVERY open record and counts them itself; if the two ever disagree, this fails
    rather than print part of the outstanding work as if it were all of it."""
    sid = session_ops.resolve_target_or_current(session)
    d = _call(gateway.get_json, f"{PREFIX}/digest?{urllib.parse.urlencode({'session': sid})}")
    outcomes = d.get("outcomes", [])
    oc = d.get("outcomeCounts", {})
    if oc.get("total", len(outcomes)) != len(outcomes):
        _fail(f"the digest carried {len(outcomes)} open records but the Gateway counts {oc.get('total')}; "
              "not printing a partial list")
    if json_output:
        _print_json(d)
        return

    _out(f"session: {d.get('sessionId')}")
    _out(f"fleetManager: {'yes' if d.get('isFleetManager') else 'no'}")
    _out(f"markedFleetManager: {cell(d.get('fleetManagerSessionId'))}")
    managers = d.get("fleetManagerSessionIds", [])
    _out(f"fleetManagerSessions[{len(managers)}]: {','.join(cell(m) for m in managers)}")

    _out(f"outcomes: {len(outcomes)} open (ready {oc.get('ready', 0)}, "
         f"finding {oc.get('finding', 0)}, decision {oc.get('decision', 0)})")
    if outcomes:
        _table("outcomes", ["id", "kind", "title", "advice", "ownerNote"],
               [[o.get("id"), o.get("kind"), o.get("title"), o.get("advice"), o.get("ownerNote")] for o in outcomes])

    answered = d.get("recentlyAnswered", [])
    _out(f"answered: {len(answered)} in the last {d.get('answeredWithinHours', 24)} hours")
    if answered:
        _table("answered", ["id", "kind", "by", "answer", "title"],
               [[o.get("id"), o.get("kind"), o.get("answeredByRole"), o.get("answer"), o.get("title")] for o in answered])

    owned = d.get("ownedSessions", [])
    sc = d.get("ownedSessionCounts", {})
    _out(f"sessions: {sc.get('total', len(owned))} owned (needs-you {sc.get('needsYou', 0)}, "
         f"working {sc.get('working', 0)}, stopped {sc.get('stopped', 0)})")
    if owned:
        rows = []
        for s in owned:
            v = s.get("turnVerdict") or {}
            rows.append([s.get("sessionId"), s.get("name"), s.get("state"), s.get("ownerSessionId"),
                         v.get("verdict") or "none", v.get("label")])
        _table("sessions", ["id", "name", "state", "owner", "verdict", "label"], rows)

    prefs = d.get("preferences", [])
    _out(f"preferences: {len(prefs)}")
    if prefs:
        _print_preferences(prefs)

    events = d.get("events", [])
    events_total = d.get("eventsTotal", len(events))
    shown = f"{len(events)} of {events_total}" if events_total > len(events) else f"{len(events)}"
    waiting = d.get("eventsWaitingForReading", 0)
    _out(f"events: {shown} unacknowledged" + (f" ({waiting} waiting for their reading)" if waiting else ""))
    if d.get("eventsDeliveryNote"):
        _out(f"eventsDeliveryNote: {d['eventsDeliveryNote']}")
    more_cursor = d.get("eventsNextCursor") if d.get("eventsHasMore") else None
    if more_cursor:
        _out(f"eventsMoreRemain: {events_total - len(events)} (oldest first; the rest: "
             f"cc-devthrottle fleet events --cursor {more_cursor})")
    if events:
        _events_table(events)

    hints = []
    if more_cursor:
        hints.append(f"cc-devthrottle fleet events --count 200 --cursor {more_cursor}   (the events after these)")
    ready = [e for e in events if not e.get("readingPending")]
    if ready:
        hints.append(f"cc-devthrottle fleet ack {ready[0].get('id')}")
    if outcomes:
        hints.append(f"cc-devthrottle fleet show {outcomes[0].get('id')}")
    if owned:
        hints.append(f"cc-devthrottle session buffer {owned[0].get('sessionId')}")
    hints.append("cc-devthrottle fleet digest --json")
    _help(hints)

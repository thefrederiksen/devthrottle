"""The factory registry and goal numbers for cc-devthrottle (Factories screen mission, phase A).

Four verbs:

- `factory register --manifest <file>` registers a factory on the Gateway from a JSON manifest: its title,
  folder, computer, boss, goal and seats. Registering again replaces the whole registration, seats included.
  When the manifest names a goal file, this command reads it from the factory's folder and sends its text,
  so it runs on the factory's own computer.
- `factory list` lists the registered factories.
- `factory purpose <factory> "<line>"` sets the one line that says what a factory is for, shown under its name
  on the owner's Factories cards, without registering it again; `--clear` removes it.
- `factory goal-number post` posts the number a factory's goal is measured by. A boss runs it on every run.
- `factory goal-number show` reads a factory's goal numbers back, newest first.

THE MANIFEST is JSON (no extra dependency, and the Gateway's own shape), with exactly these keys:

    {
      "factory": "warmforward",                       the factory id: lower-case letters, digits, hyphens
      "title": "WarmForward",                         its name as the owner reads it
      "folder": "D:\\ReposFred\\cc-consult\\...",      absolute path on its computer
      "computer": "SOREN_NORTH",                      the machine it runs on
      "bossSeat": "boss",                             optional: the seat that is the boss
      "goalFile": "GOAL.md",                          optional: relative to the folder; its text is sent
      "goalApprovedOn": "2026-10-04",                 optional: the day the owner approved the goal
      "purpose": "Heating monitoring for homeowners",  optional: one line on what it is for (max 120 chars);
                                                      left out, a line set with `factory purpose` is kept
      "seats": [
        {"id": "boss", "name": "Boss", "role": "Boss",   the boss has no name of its own: its name is Boss
         "briefFile": "agents/boss.yaml",              relative to the folder
         "schedules": ["cj_a721e6"],                   the Gateway schedules that run this seat
         "computer": "SOREN_NORTH"}                    optional: defaults to the factory's computer
      ]
    }

An unknown key is refused rather than ignored, because a misspelt key ("goalfile") that was silently dropped
would register a factory with no goal and no sign anything went wrong.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
from pathlib import Path
from typing import Any, Dict, List, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli, usage_errors  # noqa: E402

REGISTRY_ROUTE = "gateway/factory/registry"
GOAL_NUMBERS_ROUTE = "gateway/factory/goal-numbers"

#: The keys a manifest may hold, and a seat inside it. Anything else is refused.
MANIFEST_KEYS = ("factory", "title", "folder", "computer", "bossSeat", "goalFile", "goalApprovedOn", "purpose", "seats")
SEAT_KEYS = ("id", "name", "role", "briefFile", "schedules", "computer", "line")

#: `factory list`: every field it can show, and the few it shows unless asked (docs/axi-standard.md).
LIST_FIELDS = ("id", "title", "boss", "seats", "computer", "goal", "purpose", "folder")
LIST_DEFAULT_FIELDS = ("id", "title", "boss", "seats")

#: `factory goal-number show`: every field, and the default few.
GOAL_FIELDS = ("asOf", "value", "unit", "postedBy", "postedAtUtc", "link", "id")
GOAL_DEFAULT_FIELDS = ("asOf", "value", "unit", "postedBy")

#: Free text longer than this is cut in plain output, with its length and --full.
PREVIEW_CHARS = 80

#: What a 404 from these routes means: they are not mapped while the switch is off, so the Gateway cannot say
#: it in its own words.
FEATURE_OFF = (
    "factory agents are switched off for this account, so the Gateway does not serve the factory registry "
    "(it answered 404). A Gateway older than this command answers the same way."
)

_LIST = "cc-devthrottle factory list"
_PURPOSE = 'cc-devthrottle factory purpose <factory> "<one line>"'
_REGISTER = "cc-devthrottle factory register --manifest <file>"
_SHOW = "cc-devthrottle factory goal-number show --factory <id>"
_POST = ("cc-devthrottle factory goal-number post --factory <id> --value <text> --unit <text> "
         "--date <YYYY-MM-DD> --link <url>")


def _reason(exc: gateway.GatewayError) -> str:
    return FEATURE_OFF if exc.status == 404 else str(exc)


def _seat_cells(seat: Dict[str, Any]) -> Dict[str, Any]:
    return {
        "id": seat.get("id"),
        "name": seat.get("name"),
        "role": seat.get("role"),
        "computer": seat.get("computer"),
        "schedules": " ".join(seat.get("schedules") or []) or None,
    }


# ---------- register ----------


def _contained_goal_file(folder: str, goal_file: str) -> Path:
    """The goal file, resolved, and only if it stays inside the factory's folder. Exits 1 otherwise.

    The goal's text is SENT to the Gateway and kept in the account, so this command must never read a file
    outside the folder the manifest names: not by an absolute path, not by "..", and not through a link.
    Links are followed and the FINAL target is what is checked - a GOAL.md that is a link to a file inside
    the folder is fine, one that points outside it is refused - because the bytes read are the target's.
    The check happens before anything is read.
    """
    raw = Path(goal_file)
    parts = goal_file.replace("\\", "/").split("/")
    if raw.is_absolute() or raw.anchor or goal_file.startswith(("/", "\\")) or ".." in parts:
        axi_cli.fail(
            f"Not registered: the goal file {axi_cli.ascii_text(goal_file)} must be a path inside the factory's "
            "folder, relative to it, with no '..'.",
            ["cc-devthrottle factory register --help"],
        )
    base = Path(folder).resolve()
    target = (base / raw).resolve()
    if target != base and base not in target.parents:
        axi_cli.fail(
            f"Not registered: the goal file {axi_cli.ascii_text(goal_file)} leads outside the factory's folder "
            f"(to {axi_cli.ascii_text(str(target))}), so it is not read. The goal must live in the factory's folder.",
            ["cc-devthrottle factory register --help"],
        )
    return target


def read_manifest(path: str) -> Dict[str, Any]:
    """Read and check the manifest, and read the goal file it names. Exits 1 with the reason when it cannot."""
    file = Path(path)
    if not file.is_file():
        axi_cli.fail(f"Not registered: there is no manifest file at {path}.", [_REGISTER])
    try:
        manifest = json.loads(file.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, ValueError) as exc:
        axi_cli.fail(f"Not registered: the manifest {path} is not readable JSON ({exc}).", [_REGISTER])
    if not isinstance(manifest, dict):
        axi_cli.fail(f"Not registered: the manifest {path} holds {type(manifest).__name__}, not a JSON object.", [_REGISTER])

    unknown = sorted(set(manifest) - set(MANIFEST_KEYS))
    if unknown:
        axi_cli.fail(
            f"Not registered: the manifest has key(s) this command does not know: {', '.join(unknown)}. "
            f"A manifest holds only: {', '.join(MANIFEST_KEYS)}.",
            ["cc-devthrottle factory register --help"],
        )
    seats = manifest.get("seats")
    if not isinstance(seats, list):
        axi_cli.fail("Not registered: the manifest's \"seats\" must be a list of seats.", ["cc-devthrottle factory register --help"])
    for index, seat in enumerate(seats):
        if not isinstance(seat, dict):
            axi_cli.fail(f"Not registered: seat {index + 1} is not a JSON object.", ["cc-devthrottle factory register --help"])
        unknown = sorted(set(seat) - set(SEAT_KEYS))
        if unknown:
            axi_cli.fail(
                f"Not registered: seat {seat.get('id', index + 1)} has key(s) this command does not know: "
                f"{', '.join(unknown)}. A seat holds only: {', '.join(SEAT_KEYS)}.",
                ["cc-devthrottle factory register --help"],
            )

    body = dict(manifest)
    goal_file = manifest.get("goalFile")
    if goal_file is not None:
        folder = manifest.get("folder")
        if not isinstance(folder, str) or not isinstance(goal_file, str):
            axi_cli.fail("Not registered: \"folder\" and \"goalFile\" must both be text.", [_REGISTER])
        goal_path = _contained_goal_file(folder, goal_file)
        if not goal_path.is_file():
            axi_cli.fail(
                f"Not registered: the goal file {goal_path} does not exist on this computer. The command reads it "
                f"from the factory's folder, so run it on the factory's computer ({manifest.get('computer')}), "
                "or leave goalFile out until the factory has a goal.",
                [_REGISTER],
            )
        try:
            body["goalText"] = goal_path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as exc:
            axi_cli.fail(f"Not registered: the goal file {goal_path} could not be read ({exc}).", [_REGISTER])
    return body


def register(manifest_path: str, json_output: bool) -> None:
    """Register the factory the manifest describes and print what the Gateway holds now."""
    body = read_manifest(manifest_path)
    try:
        answer = gateway.put_json(REGISTRY_ROUTE, body)
    except gateway.GatewayError as exc:
        axi_cli.fail(f"Not registered: {_reason(exc)}", [axi_cli.CHECK_GATEWAY])

    if not isinstance(answer, dict) or not answer.get("factory"):
        axi_cli.fail(
            "Not registered: the Gateway answered without the registered factory, so this command cannot show "
            "it was registered. Treat it as not registered.",
            [_LIST],
        )
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    goal = "none"
    if answer.get("goalText"):
        goal = "set"
        if answer.get("goalApprovedOn"):
            goal += f", approved {answer['goalApprovedOn']}"
        if answer.get("goalFile"):
            goal += f" ({answer['goalFile']})"
    goal = axi_cli.ascii_text(goal)
    seats = answer.get("seats") or []
    axi_output.write_blocks(
        sys.stdout,
        "\n".join([
            f"registered: {axi_cli.ascii_text(str(answer['factory']))}",
            f"title: {axi_output.format_value(answer.get('title'))}",
            f"computer: {axi_output.format_value(answer.get('computer'))}",
            f"folder: {axi_cli.ascii_text(str(answer.get('folder')))}",
            f"boss: {axi_output.format_value(answer.get('bossSeat') or 'none')}",
            f"goal: {goal}",
            f"purpose: {axi_output.format_value(answer.get('purpose') or 'none')}",
        ]),
        axi_output.render_list("seats", ["id", "name", "role", "computer", "schedules"], [_seat_cells(s) for s in seats]),
        axi_output.format_help([_LIST, f"cc-devthrottle factory goal-number show --factory {axi_cli.bare(answer['factory'], '<id>')}"]),
    )


# ---------- list ----------


def list_factories(json_output: bool, fields: Optional[str] = None) -> None:
    """Every registered factory in the account."""
    if json_output and fields is not None:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, LIST_FIELDS, LIST_DEFAULT_FIELDS)
    try:
        answer = gateway.get_json(REGISTRY_ROUTE)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [axi_cli.CHECK_GATEWAY])

    factories = answer.get("factories") if isinstance(answer, dict) else None
    if not isinstance(factories, list):
        # Absent is not empty: an answer with no list must never read as "no factories registered".
        axi_cli.fail("the Gateway answered with no list of factories; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records = [
        {
            "id": f.get("factory"),
            "title": f.get("title"),
            "computer": f.get("computer"),
            "boss": f.get("bossSeat"),
            "seats": len(f.get("seats") or []),
            "goal": "yes" if f.get("goalText") else "no",
            "purpose": f.get("purpose"),
            "folder": f.get("folder"),
        }
        for f in factories
    ]
    axi_output.write_blocks(
        sys.stdout,
        axi_output.format_count(len(records)),
        axi_output.render_list("factories", chosen, [{k: r[k] for k in chosen} for r in records]),
        axi_output.format_help([_REGISTER] if not records else [_SHOW, _REGISTER]),
    )


# ---------- purpose ----------


def set_purpose(factory: str, line: Optional[str], clear: bool, json_output: bool) -> None:
    """Set or clear a registered factory's one-line purpose. Exits non-zero whenever it was not kept."""
    if clear and line is not None:
        usage_errors.usage_error("give the purpose line or --clear, not both.")
    if not clear and (line is None or not line.strip()):
        usage_errors.usage_error("give the purpose as one line in quotes, or --clear to remove it: " + _PURPOSE)
    body: Dict[str, Any] = {"purpose": None if clear else line.strip()}
    path = f"{REGISTRY_ROUTE}/{gateway.path_segment(factory)}/purpose"
    try:
        answer = gateway.put_json(path, body)
    except gateway.GatewayError as exc:
        axi_cli.fail(f"Not set: {_reason(exc)}", [_LIST])

    if not isinstance(answer, dict) or not answer.get("factory"):
        axi_cli.fail("Not set: the Gateway answered without the factory, so this command cannot show the purpose "
                     "was kept. Treat it as not set.", [_LIST])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    kept = answer.get("purpose")
    axi_output.write_blocks(
        sys.stdout,
        "\n".join([
            f"factory: {axi_cli.ascii_text(str(answer['factory']))}",
            f"title: {axi_output.format_value(answer.get('title'))}",
            f"purpose: {axi_output.format_value(kept) if kept else 'none (cleared)'}",
        ]),
        axi_output.format_help([_LIST]),
    )


# ---------- goal numbers ----------


def post_goal_number(factory: str, value: str, unit: str, date: str, link: str, by: Optional[str], json_output: bool) -> None:
    """Post one goal number. Exits non-zero whenever it was not kept."""
    body: Dict[str, Any] = {"factory": factory, "value": value, "unit": unit, "asOf": date, "link": link}
    if by is not None:
        body["postedBy"] = by
    try:
        answer = gateway.post_json(GOAL_NUMBERS_ROUTE, body)
    except gateway.GatewayError as exc:
        axi_cli.fail(f"Not posted: {_reason(exc)}", [_SHOW, _LIST])

    if not isinstance(answer, dict) or not answer.get("id"):
        axi_cli.fail("Not posted: the Gateway answered without the id of a kept number, so this command cannot "
                     "show it was posted. Treat it as not posted.", [_SHOW])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_output.write_blocks(
        sys.stdout,
        "\n".join([
            f"posted: {axi_cli.ascii_text(str(answer['id']))}",
            f"factory: {axi_output.format_value(answer.get('factory'))}",
            f"value: {axi_output.format_value(answer.get('value'))}",
            f"unit: {axi_output.format_value(answer.get('unit'))}",
            f"as of: {axi_output.format_value(answer.get('asOf'))}",
            f"posted by: {axi_output.format_value(answer.get('postedBy'))}",
        ]),
        axi_output.format_help([f"cc-devthrottle factory goal-number show --factory {axi_cli.bare(answer.get('factory'), '<id>')}"]),
    )


def _preview(text: Any, full: bool) -> Any:
    """Long free text cut to a preview with its size and the way to see it whole."""
    if full or not isinstance(text, str) or len(text) <= PREVIEW_CHARS:
        return text
    return f"{text[:PREVIEW_CHARS]}... (truncated, {len(text)} chars total - use --full)"


def show_goal_numbers(factory: str, count: int, json_output: bool, fields: Optional[str] = None,
                      full: bool = False) -> None:
    """A factory's goal numbers, newest first."""
    if json_output and fields is not None:
        usage_errors.usage_error(axi_cli.FIELDS_WITH_JSON)
    chosen = usage_errors.parse_fields(fields, GOAL_FIELDS, GOAL_DEFAULT_FIELDS)
    path = f"{GOAL_NUMBERS_ROUTE}?{urllib.parse.urlencode([('factory', factory), ('count', str(count))])}"
    try:
        answer = gateway.get_json(path)
    except gateway.GatewayError as exc:
        axi_cli.fail(_reason(exc), [_LIST])

    posts = answer.get("posts") if isinstance(answer, dict) else None
    total = answer.get("count") if isinstance(answer, dict) else None
    if not isinstance(posts, list) or not isinstance(total, int):
        axi_cli.fail("the Gateway answered with no list of goal numbers; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    if json_output:
        print(json.dumps(answer, indent=2))
        return

    records: List[Dict[str, Any]] = [
        {
            "asOf": p.get("asOf"),
            "value": _preview(p.get("value"), full),
            "unit": _preview(p.get("unit"), full),
            "postedBy": p.get("postedBy"),
            "postedAtUtc": p.get("postedAtUtc"),
            "link": _preview(p.get("link"), full),
            "id": p.get("id"),
        }
        for p in posts
    ]
    blocks = [f"factory: {axi_output.format_value(answer.get('factory'))}",
              axi_output.format_count(len(records), total=total if total != len(records) else None)]
    if not records:
        blocks.append("No goal number posted yet.")
    else:
        blocks.append(axi_output.render_list("goalNumbers", chosen, [{k: r[k] for k in chosen} for r in records]))
    shown_id = axi_cli.bare(answer.get("factory"), "<id>")
    more = [f"cc-devthrottle factory goal-number show --factory {shown_id} --count {min(total, 200)}",
            f"cc-devthrottle factory goal-number show --factory {shown_id} --fields {','.join(GOAL_FIELDS)}"]
    blocks.append(axi_output.format_help([_POST] if not records else more))
    axi_output.write_blocks(sys.stdout, *blocks)

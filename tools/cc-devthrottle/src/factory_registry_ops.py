"""The factory registry and goal numbers for cc-devthrottle (Factories screen mission, phase A).

Four verbs:

- `factory register --manifest <file>` registers a factory on the Gateway from a JSON manifest: its title,
  folder, computer, CEO, goal and seats. Registering again replaces the whole registration, seats included.
  When the manifest names a goal file, this command reads it from the factory's folder and sends its text,
  so it runs on the factory's own computer.
- `factory list` lists the registered factories.
- `factory goal-number post` posts the number a factory's goal is measured by. A CEO runs it on every run.
- `factory goal-number show` reads a factory's goal numbers back, newest first.

THE MANIFEST is JSON (no extra dependency, and the Gateway's own shape), with exactly these keys:

    {
      "factory": "warmforward",                       the factory id: lower-case letters, digits, hyphens
      "title": "WarmForward",                         its name as the owner reads it
      "folder": "D:\\ReposFred\\cc-consult\\...",      absolute path on its computer
      "computer": "SOREN_NORTH",                      the machine it runs on
      "ceoSeat": "nora-hale",                         optional: the seat that is the CEO
      "goalFile": "GOAL.md",                          optional: relative to the folder; its text is sent
      "goalApprovedOn": "2026-10-04",                 optional: the day the owner approved the goal
      "seats": [
        {"id": "nora-hale", "name": "Nora Hale", "role": "CEO",
         "briefFile": "agents/ceo.yaml",               relative to the folder
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

from . import axi_cli  # noqa: E402

REGISTRY_ROUTE = "gateway/factory/registry"
GOAL_NUMBERS_ROUTE = "gateway/factory/goal-numbers"

#: The keys a manifest may hold, and a seat inside it. Anything else is refused.
MANIFEST_KEYS = ("factory", "title", "folder", "computer", "ceoSeat", "goalFile", "goalApprovedOn", "seats")
SEAT_KEYS = ("id", "name", "role", "briefFile", "schedules", "computer")

#: What a 404 from these routes means: they are not mapped while the switch is off, so the Gateway cannot say
#: it in its own words.
FEATURE_OFF = (
    "factory agents are switched off for this account, so the Gateway does not serve the factory registry "
    "(it answered 404). A Gateway older than this command answers the same way."
)

_LIST = "cc-devthrottle factory list"
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
        goal_path = Path(folder) / goal_file
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
    seats = answer.get("seats") or []
    axi_output.write_blocks(
        sys.stdout,
        "\n".join([
            f"registered: {answer['factory']}",
            f"title: {axi_output.format_value(answer.get('title'))}",
            f"computer: {axi_output.format_value(answer.get('computer'))}",
            f"folder: {axi_cli.ascii_text(str(answer.get('folder')))}",
            f"ceo: {axi_output.format_value(answer.get('ceoSeat') or 'none')}",
            f"goal: {goal}",
        ]),
        axi_output.render_list("seats", ["id", "name", "role", "computer", "schedules"], [_seat_cells(s) for s in seats]),
        axi_output.format_help([_LIST, f"cc-devthrottle factory goal-number show --factory {answer['factory']}"]),
    )


# ---------- list ----------


def list_factories(json_output: bool) -> None:
    """Every registered factory in the account."""
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
            "ceo": f.get("ceoSeat"),
            "seats": len(f.get("seats") or []),
            "goal": "yes" if f.get("goalText") else "no",
        }
        for f in factories
    ]
    axi_output.write_blocks(
        sys.stdout,
        axi_output.format_count(len(records)),
        axi_output.render_list("factories", ["id", "title", "computer", "ceo", "seats", "goal"], records),
        axi_output.format_help([_REGISTER] if not records else [_SHOW, _REGISTER]),
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
            f"posted: {answer['id']}",
            f"factory: {answer.get('factory')}",
            f"value: {axi_output.format_value(answer.get('value'))}",
            f"unit: {axi_output.format_value(answer.get('unit'))}",
            f"as of: {answer.get('asOf')}",
            f"posted by: {answer.get('postedBy')}",
        ]),
        axi_output.format_help([f"cc-devthrottle factory goal-number show --factory {answer.get('factory')}"]),
    )


def show_goal_numbers(factory: str, count: int, json_output: bool) -> None:
    """A factory's goal numbers, newest first."""
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
            "value": p.get("value"),
            "unit": p.get("unit"),
            "postedBy": p.get("postedBy"),
            "postedAtUtc": p.get("postedAtUtc"),
            "link": p.get("link"),
            "id": p.get("id"),
        }
        for p in posts
    ]
    blocks = [f"factory: {answer.get('factory')}",
              axi_output.format_count(len(records), total=total if total != len(records) else None)]
    if not records:
        blocks.append("No goal number posted yet.")
    else:
        blocks.append(axi_output.render_list("goalNumbers", ["asOf", "value", "unit", "postedBy", "postedAtUtc", "link", "id"], records))
    more = f"cc-devthrottle factory goal-number show --factory {answer.get('factory')} --count {min(total, 200)}"
    blocks.append(axi_output.format_help([_POST] if not records else [more, _POST]))
    axi_output.write_blocks(sys.stdout, *blocks)

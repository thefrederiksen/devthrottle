"""The state a factory's checks keep between runs, for cc-devthrottle (Factory Definition mission, phase 5b).

A check from a factory's definition may keep a small state file between runs - the last message it saw, the last
row it counted. The Gateway holds it, one history per checked start, and the newest row is the state. Two verbs:

- `factory check-state show <factory> [<seat>] [--start N]`  every checked start of the published definition, by seat
                                                             and start number, with the newest state its check left
- `factory check-state reset <factory> <seat> [--start N]`   append an empty state for one start, so its check's next
                                                             run is a first run - the way out of a stuck state. Only a
                                                             person resets; a session is refused by the Gateway.

A start is named by its seat and its number: the Nth start with a check in that seat's file, counting from 1. A seat
with one checked start needs no --start.

THE GATEWAY RULES, THIS MODULE PRINTS (rule 7): the starts, their numbers and every refusal are the Gateway's.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Gateway's route for a factory's definition; the check state lives under it.
ROUTE = "factory-definitions"

_TOOL = "cc-devthrottle factory check-state"

#: The longest piece of a state `show` prints on one line; --json gives it whole.
SHOWN_CHARS = 120


def _route(factory: str) -> str:
    return f"{ROUTE}/{gateway.path_segment(factory)}/check-state"


def _f(value: str, placeholder: str = "<factory>") -> str:
    return axi_cli.bare(value, placeholder)


def _states(factory: str) -> Dict[str, Any]:
    """Every checked start of the factory, as the Gateway lists them. A refusal exits non-zero with its words."""
    try:
        answer = gateway.get_json(_route(factory))
    except gateway.GatewayError as exc:
        axi_cli.fail(str(exc), [axi_cli.CHECK_GATEWAY] if exc.status >= 500 else [f"{_TOOL} show {_f(factory)}"])
    states = answer.get("states") if isinstance(answer, dict) else None
    if not isinstance(states, list):
        axi_cli.fail("the Gateway answered with no list of checked starts; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
    return answer


def _pick(factory: str, states: List[Dict[str, Any]], seat: str, start: Optional[int]) -> Dict[str, Any]:
    """The one start a reset names: the seat's only checked start, or the one numbered --start."""
    of_seat = [s for s in states if s.get("seat") == seat]
    if not of_seat:
        axi_cli.fail(f"{seat} has no checked start in {factory}'s published definition.",
                     [f"{_TOOL} show {_f(factory)}"])
    if start is None:
        if len(of_seat) > 1:
            numbers = ", ".join(str(s.get("start")) for s in of_seat)
            axi_cli.fail(f"{seat} has {len(of_seat)} checked starts ({numbers}); name one with --start.",
                         [f"{_TOOL} show {_f(factory)} {_f(seat, '<seat>')}"])
        return of_seat[0]
    for s in of_seat:
        if s.get("start") == start:
            return s
    axi_cli.fail(f"{seat} has no checked start numbered {start}.", [f"{_TOOL} show {_f(factory)} {_f(seat, '<seat>')}"])
    raise AssertionError("unreachable")


def _shown(content: Any) -> str:
    text = content if isinstance(content, str) else ""
    if not text:
        return "(empty)"
    flat = " ".join(text.split())
    return json.dumps(flat if len(flat) <= SHOWN_CHARS else flat[:SHOWN_CHARS] + "...")


def _row(s: Dict[str, Any]) -> str:
    written = axi_output.format_value(s.get("writtenUtc")) if s.get("writtenUtc") else "never"
    by = ("reset by " + str(s.get("writtenBy"))) if s.get("reset") else (s.get("writtenBy") or "-")
    return (f"  {s.get('seat')},{s.get('start')},{s.get('name')},{s.get('checkPath')},{s.get('ownerKey')},"
            f"{written},{by},{_shown(s.get('content'))}")


def show(factory: str, seat: Optional[str], start: Optional[int], json_output: bool) -> None:
    """Every checked start of the published definition with its newest state, or one seat's, or one start's."""
    answer = _states(factory)
    states = [s for s in answer["states"] if isinstance(s, dict)]
    if seat is not None:
        states = [s for s in states if s.get("seat") == seat]
    if start is not None:
        states = [s for s in states if s.get("start") == start]
    if json_output:
        print(json.dumps({**answer, "count": len(states), "states": states}, indent=2))
        return
    name = answer.get("factory", factory)
    if not states:
        which = f" for seat {seat}" if seat else ""
        which += f" numbered {start}" if start is not None else ""
        axi_cli.write_lines(f"count: 0. {name} has no checked start{which} in its published definition.")
        axi_cli.print_next([f"cc-devthrottle factory definition get {_f(factory)}"])
        return
    axi_cli.write_lines(f"count: {len(states)}",
                        f"states[{len(states)}]{{seat,start,name,check,ownerKey,written,by,state}}:",
                        *(_row(s) for s in states))
    axi_cli.print_next([f"{_TOOL} reset {_f(factory)} <seat> --start <start>",
                        f"{_TOOL} show {_f(factory)} <seat> --json"])


def reset(factory: str, seat: str, start: Optional[int], json_output: bool) -> None:
    """Append an empty state for one checked start. A person's act: a session's key is refused by the Gateway."""
    states = [s for s in _states(factory)["states"] if isinstance(s, dict)]
    target = _pick(factory, states, seat, start)
    try:
        answer = gateway.post_json(_route(factory) + "/reset", {"ownerKey": target.get("ownerKey")})
    except gateway.GatewayError as exc:
        axi_cli.fail(str(exc), [f"{_TOOL} show {_f(factory)}"] if exc.status < 500 else [axi_cli.CHECK_GATEWAY])
    if not isinstance(answer, dict) or answer.get("reset") is not True:
        axi_cli.fail("the Gateway answered without the reset state, so this command cannot show it was reset. "
                     "Read it back before relying on it.", [f"{_TOOL} show {_f(factory)} {_f(seat, '<seat>')}"])
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Reset: the state of {answer.get('name')} (seat {answer.get('seat')}, start {answer.get('start')}, "
                        f"{answer.get('checkPath')}) is now empty. Its check's next run is a first run.")
    axi_cli.print_next([f"{_TOOL} show {_f(factory)} {_f(seat, '<seat>')}"])

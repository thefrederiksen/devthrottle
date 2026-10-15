"""A seat's brief, held on the Gateway, for cc-devthrottle (Factory Seats mission, phase 2a).

A brief is what a seat of a factory is told to do. Five verbs, each naming the factory and the seat:

- `factory brief get`       the published brief - what a talk or a run is given; `--draft` for the newest version;
                            `--version N` for one version by its number
- `factory brief history`   one page of the versions, newest first, without their text
- `factory brief set --file` save the file's text as a new DRAFT version
- `factory brief publish`   make one version the published one - a person's act
- `factory brief restore`   bring an older version back as the newest, published - a person's act

DRAFT, THEN PUBLISHED. `set` never changes what a run is given: it adds a draft. A person publishes it. This
command runs with a session's key, so `publish` and `restore` are refused by the Gateway with the sentence that
says so, and this module prints that sentence rather than one of its own.

EVERY VERSION IS KEPT, SO THE WRITING AND THE READING ARE BOUNDED. A session is refused another draft while twenty
wait above the published version; `set` prints the Gateway's sentence, which says to ask the owner to publish. And
`history` reads one page and never a version's text: `get --version N` reads one version's text.

A DEFINED FACTORY'S BRIEF IS PART OF ITS DEFINITION (Factory Definition, phase 4). For a factory with a published
definition the Gateway answers every verb from the definition: a version is a definition version, `set` saves a new
definition draft, and `publish` and `restore` are answered with the definition's own command to run. The Gateway says
so in one sentence (`definition`), which this module prints exactly as it is sent; it decides nothing itself.

TWO WRITERS ON ONE BRIEF. `set` sends the version the caller last READ (`--expected-version`, 0 when the seat has no
brief on the Gateway yet). It is never looked up here just before writing: a version nobody was shown is not a
version anybody read, and sending it would let this command replace text its caller never saw. A stale save is
refused by the Gateway with the newest version attached; this module says which version that is and how to read it.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlencode

import typer

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Gateway's routes for a seat's brief.
ROUTE = "factory-briefs"

_TOOL = "cc-devthrottle factory brief"


def _seat_route(factory: str, seat: str) -> str:
    return f"{ROUTE}/{gateway.path_segment(factory)}/{gateway.path_segment(seat)}"


def _where(factory: str, seat: str) -> str:
    """The `--factory <id> --seat <id>` a next step carries, with placeholders for anything that cannot be pasted."""
    return f"--factory {axi_cli.bare(factory, '<factory>')} --seat {axi_cli.bare(seat, '<seat>')}"


def _by(brief: Dict[str, Any]) -> str:
    kind = brief.get("authorKind") or "?"
    who = brief.get("authorId")
    return f"{kind} {who}" if who else str(kind)


def _text_lines(text: Optional[str]) -> List[str]:
    """A brief's text as lines of printable ASCII. Every character that is not is escaped, never dropped;
    `--json` gives the exact text."""
    if not text:
        return ["(empty)"]
    return [axi_cli.ascii_text(line) for line in text.split("\n")]


def _is_brief(answer: Any) -> bool:
    return isinstance(answer, dict) and isinstance(answer.get("version"), int) and isinstance(answer.get("state"), str)


def _fail_gateway(exc: gateway.GatewayError, next_commands: Optional[List[str]] = None) -> None:
    axi_cli.fail(str(exc), next_commands or [axi_cli.CHECK_GATEWAY])


def _outcome(exc: gateway.GatewayError) -> Optional[str]:
    return exc.body.get("outcome") if isinstance(exc.body, dict) else None


def _definition_lines(answer: Any) -> List[str]:
    """The Gateway's sentence that a defined factory's brief is part of its definition, and the whole-definition
    check's notices on a saved version, each printed as the Gateway sent it. Nothing for any other factory."""
    if not isinstance(answer, dict):
        return []
    lines = []
    definition = answer.get("definition")
    if isinstance(definition, str) and definition:
        lines.append(axi_cli.ascii_text(definition))
    for finding in answer.get("findings") or []:
        if isinstance(finding, dict) and isinstance(finding.get("message"), str):
            where = finding.get("file") or ""
            if isinstance(finding.get("line"), int) and finding["line"] > 0:
                where = f"{where}:{finding['line']}"
            lines.append(axi_cli.ascii_text(f"{finding.get('severity') or 'notice'}: {where}: {finding['message']}"))
    return lines


def _standing_line(brief: Dict[str, Any]) -> Optional[str]:
    """What else is true of the seat's brief beside the version shown: a newer draft waiting, or nothing published."""
    version = brief["version"]
    newest = brief.get("newestVersion")
    published = brief.get("publishedVersion")
    if isinstance(newest, int) and newest > version:
        return f"A newer version, {newest}, is waiting and is not published. Read it with 'get --draft'."
    if brief["state"] == "draft":
        return ("No version is published yet: a talk or a run is not given this text." if published is None
                else f"This is a draft. A talk or a run is given version {published} until a person publishes this one.")
    return None


# ---------------------------------------------------------------------------------------------------
# The verbs.
# ---------------------------------------------------------------------------------------------------

def get_brief(factory: str, seat: str, draft: bool, json_output: bool, version: Optional[int] = None) -> None:
    """The published brief; with `draft` the newest version whether or not it is published; with `version` that one."""
    where = _where(factory, seat)
    if draft and version is not None:
        axi_cli.usage_error("--draft reads the newest version and --version reads the one you name; pass one of them.")
        return
    which = "/draft" if draft else f"/versions/{version}" if version is not None else ""
    try:
        brief = gateway.get_json(_seat_route(factory, seat) + which)
    except gateway.GatewayError as exc:
        if exc.status == 404:
            axi_cli.fail(str(exc), [f"{_TOOL} history {where}"] if version is not None
                         else [f"{_TOOL} get {where} --draft", f"{_TOOL} set {where} --file <path>",
                               "cc-devthrottle factory list"])
        _fail_gateway(exc)
        return
    if not _is_brief(brief):
        # Absent is not empty: an answer without a brief must never read as "this seat is told nothing".
        axi_cli.fail("the Gateway's answer carried no brief; this command will not report that as an empty one.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(brief, indent=2))
        return
    head = (f"{brief.get('factory')}/{brief.get('seat')}  version {brief['version']}  {brief['state']}  "
            f"written by {_by(brief)}  {axi_output.format_value(brief.get('writtenAtUtc'))}")
    lines = [head]
    standing = _standing_line(brief)
    if standing:
        lines.append(standing)
    lines.extend(_definition_lines(brief))
    axi_cli.write_lines(*lines)
    sys.stdout.flush()
    axi_output.write_blocks(sys.stdout, *_text_lines(brief.get("text")))
    axi_cli.print_next([f"{_TOOL} set {where} --file <path> --expected-version {brief.get('newestVersion', brief['version'])}",
                        f"{_TOOL} history {where}"])


def history(factory: str, seat: str, json_output: bool, count: Optional[int] = None,
            before: Optional[int] = None) -> None:
    """One page of a seat's versions, newest first, without their text. `count` is how many the page holds and
    `before` the version it starts below; the Gateway says where the next page starts."""
    where = _where(factory, seat)
    query = urlencode([(name, value) for name, value in (("limit", count), ("before", before)) if value is not None])
    try:
        page = gateway.get_json(_seat_route(factory, seat) + "/history" + (f"?{query}" if query else ""))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, ["cc-devthrottle factory list"] if exc.status == 404
                      else [f"{_TOOL} history {where}"] if exc.status == 400 else None)
        return
    versions = page.get("versions") if isinstance(page, dict) else None
    if not isinstance(versions, list):
        axi_cli.fail("the Gateway answered with no list of versions; this command will not report that as no versions.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    total = page.get("total")
    if not versions and not total:
        axi_cli.write_lines(f"count: 0. '{seat}' of {factory} has no brief on the Gateway.")
        axi_cli.print_next([f"{_TOOL} set {where} --file <path>"])
        return
    published = page.get("publishedVersion")
    next_before = page.get("nextBefore")
    lines = [f"count: {len(versions)} of {total}. " + ("No version is published." if published is None
                                                      else f"Version {published} is the published brief.")]
    lines.extend(_definition_lines(page))
    for v in versions:
        mark = "published" if v.get("state") == "published" else "draft"
        if v.get("version") == published:
            mark = "PUBLISHED NOW"
        lines.append(f"v{v.get('version')}  {mark}  {axi_output.format_value(v.get('writtenAtUtc'))}  {_by(v)}  "
                     f"{v.get('textBytes')} bytes")
    if isinstance(next_before, int):
        lines.append(f"Older versions follow, below version {next_before}.")
    axi_cli.write_lines(*lines)
    older = [f"{_TOOL} history {where} --before {next_before}"] if isinstance(next_before, int) else []
    axi_cli.print_next(older + [f"{_TOOL} get {where} --version <version>"])


def _read_file(path: str) -> str:
    """The brief's text from a file, or from standard input when the path is `-`."""
    if path == "-":
        return sys.stdin.read()
    file = Path(path)
    if not file.is_file():
        axi_cli.fail(f"there is no file at {file}, so there is no brief to save.", [f"{_TOOL} set --help"])
    try:
        return file.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        axi_cli.fail(f"the file {file} could not be read as UTF-8 text: {exc}.", [f"{_TOOL} set --help"])


def set_draft(factory: str, seat: str, file: str, expected_version: int, json_output: bool) -> None:
    """Save a file's text as a new draft version, sending the version the caller last read."""
    where = _where(factory, seat)
    text = _read_file(file)
    try:
        brief = gateway.put_json(_seat_route(factory, seat) + "/draft", {"text": text, "expectedVersion": expected_version})
    except gateway.GatewayError as exc:
        if exc.status == 409 and _outcome(exc) == "Stale":
            current = exc.body.get("current")
            now = current.get("version") if isinstance(current, dict) else None
            if not isinstance(now, int):
                # No current version on a stale refusal is the seat having no brief at all: the save expected one.
                axi_cli.fail(f"Not saved: {exc}.", [f"{_TOOL} set {where} --file <path> --expected-version 0"])
            axi_cli.fail(
                f"Not saved: another writer got there first. The brief is now at version {now} "
                f"(by {_by(current)}); you sent version {expected_version}. Read version {now}, merge your change "
                "into it, and save again with that version.",
                [f"{_TOOL} get {where} --draft", f"{_TOOL} set {where} --file <path> --expected-version {now}"])
        if exc.status == 409 and _outcome(exc) == "TooManyDrafts":
            # The Gateway's own sentence says how many wait and that the owner publishing makes room. Saving again
            # would be refused again, so the next step is to look at what waits, never to retry.
            axi_cli.fail(f"Not saved: {exc}", [f"{_TOOL} history {where}"])
        _fail_gateway(exc, [f"{_TOOL} get {where} --draft"] if exc.status in (400, 404) else None)
        return
    if not _is_brief(brief):
        axi_cli.fail("the Gateway answered without the version it saved, so this command cannot show the draft was "
                     "saved. Read it back before relying on it.", [f"{_TOOL} get {where} --draft"])
        return
    if json_output:
        print(json.dumps(brief, indent=2))
        return
    published = brief.get("publishedVersion")
    axi_cli.write_lines(
        f"Saved: version {brief['version']} of the brief of '{brief.get('seat')}' is a draft.",
        "A talk or a run is given " + ("nothing from the Gateway" if published is None else f"version {published}")
        + " until a person publishes this draft.",
        *_definition_lines(brief),
    )
    axi_cli.print_next([f"{_TOOL} get {where} --draft"])


def publish(factory: str, seat: str, version: int, json_output: bool) -> None:
    """Publish one version. A person's act: with a session's key the Gateway refuses it and says so."""
    where = _where(factory, seat)
    try:
        brief = gateway.post_json(_seat_route(factory, seat) + "/publish", {"version": version})
    except gateway.GatewayError as exc:
        _fail_gateway(exc, _after_refusal(exc, where, factory))
        return
    _published(brief, where, json_output, f"Published: version {version}")


def restore(factory: str, seat: str, version: int, expected_version: int, json_output: bool) -> None:
    """Bring an older version back as the newest, published. A person's act, refused for a session's key."""
    where = _where(factory, seat)
    try:
        brief = gateway.post_json(_seat_route(factory, seat) + "/restore",
                                  {"version": version, "expectedVersion": expected_version})
    except gateway.GatewayError as exc:
        _fail_gateway(exc, _after_refusal(exc, where, factory))
        return
    _published(brief, where, json_output, f"Brought back: version {version}")


def _after_refusal(exc: gateway.GatewayError, where: str, factory: str) -> List[str]:
    """What to run after a refused publish or restore. A session is told to save a draft, because that is the one
    thing it may do; everything else starts from the history."""
    if _outcome(exc) == "OnlyAPersonPublishes":
        return [f"{_TOOL} set {where} --file <path> --expected-version <newest version>", f"{_TOOL} history {where}"]
    if _outcome(exc) == "PublishTheDefinition":
        # The Gateway's sentence names the definition's own command; what a person reads first is what it changes.
        return [f"cc-devthrottle factory definition diff {axi_cli.bare(factory, '<factory>')}", f"{_TOOL} history {where}"]
    if exc.status in (400, 404, 409):
        return [f"{_TOOL} history {where}"]
    return [axi_cli.CHECK_GATEWAY]


def _published(brief: Any, where: str, json_output: bool, what: str) -> None:
    if not _is_brief(brief):
        axi_cli.fail("the Gateway answered without the published version, so this command cannot show it was "
                     "published. Read it back before relying on it.", [f"{_TOOL} get {where}"])
        return
    if json_output:
        print(json.dumps(brief, indent=2))
        return
    axi_cli.write_lines(f"{what} of the brief of '{brief.get('seat')}'. Version {brief['version']} is now the brief "
                        "a talk or a run is given.")
    axi_cli.print_next([f"{_TOOL} get {where}"])

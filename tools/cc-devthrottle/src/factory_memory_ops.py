"""A factory's memory for cc-devthrottle (Factory Memory mission, phase 3a, section 5.4).

Five verbs, all run with this session's own Gateway key, all about THIS session's own factory - a session never
names the factory, the Gateway reads it from the session's own record:

- `factory memory list`            the factory's notes as they stand
- `factory memory get <name>`      one note, including one that was deleted
- `factory memory set <name> <text>` write a note, sending the version this session last read
- `factory memory delete <name>`   delete a note, on the same terms
- `factory memory history <name>`  every kept version of a note

TWO WRITERS ON ONE NOTE. `set` and `delete` send the version this session last READ, so an agent that learned
something cannot silently overwrite what another agent wrote in the meantime. The version comes from the notes
folder the Director put in place before the agent started ($CC_FACTORY_MEMORY_DIR), whose `.read-versions.json`
this module keeps current as the agent reads and writes. A stale write is refused by the Gateway with the current
note attached; this module prints that text and says plainly that another writer got there first and the caller
should merge and write again. It records the version it just showed, so the next `set` is the merge.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import typer

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Gateway's routes for a factory's notes.
ROUTE = "factory-memory/notes"

#: Where the Director put this session's copy of its factory's notes. Must match FactoryMemoryFiles.DirectoryEnvVar.
DIR_ENV = "CC_FACTORY_MEMORY_DIR"

#: The version of each note as this session last read it. Must match FactoryMemoryFiles.ReadVersionsFileName.
VERSIONS_FILE = ".read-versions.json"

_TOOL = "cc-devthrottle factory memory"


# ---------------------------------------------------------------------------------------------------
# The record of what this session last read.
# ---------------------------------------------------------------------------------------------------

def memory_dir() -> Optional[Path]:
    """This session's notes folder, or None when the Director gave it none (a session in no factory)."""
    value = os.environ.get(DIR_ENV, "").strip()
    return Path(value) if value else None


def _read_record(folder: Path) -> Dict[str, Any]:
    path = folder / VERSIONS_FILE
    if not path.exists():
        return {"versions": {}}
    try:
        record = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        axi_cli.fail(
            f"the record of which note versions this session has read ({path}) could not be read: {exc}. "
            "Without it this command cannot say which version you last read.",
            [f"{_TOOL} list"],
        )
    if not isinstance(record, dict) or not isinstance(record.get("versions"), dict):
        axi_cli.fail(
            f"the record of which note versions this session has read ({path}) is not in the expected shape.",
            [f"{_TOOL} list"],
        )
    return record


def _remember(versions: Dict[str, int]) -> None:
    """Record the versions just read or written. Nothing to do for a session with no notes folder."""
    folder = memory_dir()
    if folder is None or not versions:
        return
    record = _read_record(folder)
    record["versions"].update(versions)
    path = folder / VERSIONS_FILE
    tmp = path.with_suffix(".tmp")
    try:
        folder.mkdir(parents=True, exist_ok=True)
        tmp.write_text(json.dumps(record, indent=2), encoding="utf-8")
        os.replace(tmp, path)
    except OSError as exc:
        axi_cli.fail(
            f"the note was read but the version could not be recorded in {path}: {exc}. A later set would send "
            "the wrong version.",
            [f"{_TOOL} list"],
        )


def last_read_version(name: str) -> int:
    """The version of `name` this session last read, or 0 when it never read one ("I believe it does not exist")."""
    folder = memory_dir()
    if folder is None:
        axi_cli.fail(
            f"this session has no factory memory folder ({DIR_ENV} is not set), so this command cannot tell which "
            f"version of '{name}' you last read. A session in a factory is given one when it starts. Read the note "
            "and pass the version it shows with --expected-version.",
            [f"{_TOOL} get <name>", f"{_TOOL} set <name> <text> --expected-version <n>"],
        )
    value = _read_record(folder)["versions"].get(name, 0)
    return value if isinstance(value, int) and value >= 0 else 0


# ---------------------------------------------------------------------------------------------------
# Output.
# ---------------------------------------------------------------------------------------------------

def _by(note: Dict[str, Any]) -> str:
    kind = note.get("authorKind") or "?"
    who = note.get("authorId")
    return f"{kind} {who}" if who else str(kind)


def _text_lines(text: Optional[str]) -> List[str]:
    """A note's text as lines of printable ASCII. Every character that is not is escaped, never dropped;
    `--json` gives the exact text."""
    if not text:
        return ["(empty)"]
    return [axi_cli.ascii_text(line) for line in text.split("\n")]


def _first_line(text: Optional[str], width: int = 70) -> str:
    line = (text or "").split("\n", 1)[0]
    return line if len(line) <= width else line[: width - 3] + "..."


def _fail_gateway(exc: gateway.GatewayError) -> None:
    axi_cli.fail(str(exc), [axi_cli.CHECK_GATEWAY])


def _stale(name: str, exc: gateway.GatewayError, sent: int, verb: str) -> None:
    """A 409 whose outcome is Stale: another writer got there first. Print the current note, record its version
    so the next write is the merge, and exit non-zero."""
    current = exc.body.get("current") if isinstance(exc.body, dict) else None
    if not isinstance(current, dict) or not isinstance(current.get("version"), int):
        axi_cli.fail(
            f"Not {verb}: another writer got there first ({exc}), and the Gateway did not send the current note. "
            "Read it, merge, and try again.",
            [f"{_TOOL} get {axi_cli.bare(name, '<name>')}"],
        )
    now = current["version"]
    _remember({name: now})
    lines = [
        f"Not {verb}: another writer got there first. '{name}' is now version {now}; you last read version {sent}.",
    ]
    if current.get("deleted"):
        lines.append(f"Version {now} deleted the note (by {_by(current)}).")
    else:
        lines.append(f"Its current text (version {now}, by {_by(current)}):")
        lines.append("---")
        lines.extend(_text_lines(current.get("text")))
        lines.append("---")
    lines.append(
        f"Merge what you learned into that text and write again; the next set sends version {now}."
    )
    sys.stdout.flush()
    axi_output.write_blocks(sys.stderr, *(axi_cli.ascii_text(line) for line in lines))
    axi_output.write_blocks(sys.stderr, axi_output.format_help([f"{_TOOL} set {axi_cli.bare(name, '<name>')} <merged text>"]))
    raise typer.Exit(1)


# ---------------------------------------------------------------------------------------------------
# The verbs.
# ---------------------------------------------------------------------------------------------------

def list_notes(json_output: bool) -> None:
    """The factory's notes as they stand."""
    try:
        page = gateway.get_json(ROUTE)
    except gateway.GatewayError as exc:
        _fail_gateway(exc)
        return
    notes = page.get("notes") if isinstance(page, dict) else None
    if not isinstance(notes, list):
        # Absent is not empty: an answer without the list must never read as "this factory knows nothing".
        axi_cli.fail("the Gateway answered with no list of notes; this command will not report that as no notes.",
                     [axi_cli.CHECK_GATEWAY])
        return
    _remember({n["name"]: n["version"] for n in notes
               if isinstance(n, dict) and isinstance(n.get("name"), str) and isinstance(n.get("version"), int)})

    if json_output:
        print(json.dumps(page, indent=2))
        return
    factory = page.get("factory") or "?"
    lines = [f"Factory {factory}: {len(notes)} notes, {page.get('bytes', 0)} of {page.get('maxBytes', '?')} bytes "
             f"(at most {page.get('maxNotes', '?')} notes)."]
    for note in notes:
        lines.append(f"{note.get('name')}  v{note.get('version')}  {axi_output.format_value(note.get('writtenAtUtc'))}  "
                     f"{_by(note)}  {_first_line(note.get('text'))}")
    folder = memory_dir()
    if folder is not None:
        lines.append(f"The copy this session started with is in {folder}.")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_TOOL} get <name>", f"{_TOOL} set <name> <text>"])


def get_note(name: str, json_output: bool) -> None:
    """One note, including a deleted one."""
    try:
        note = gateway.get_json(f"{ROUTE}/{gateway.path_segment(name)}")
    except gateway.GatewayError as exc:
        if exc.status == 404:
            axi_cli.fail(f"{exc} To write it for the first time, set it; nothing needs reading first.",
                         [f"{_TOOL} set {axi_cli.bare(name, '<name>')} <text>", f"{_TOOL} list"])
        _fail_gateway(exc)
        return
    if not isinstance(note, dict) or not isinstance(note.get("version"), int):
        axi_cli.fail("the Gateway's answer carried no note.", [axi_cli.CHECK_GATEWAY])
        return
    _remember({name: note["version"]})

    if json_output:
        print(json.dumps(note, indent=2))
        return
    if note.get("deleted"):
        axi_cli.write_lines(
            f"'{name}' was deleted in version {note['version']} by {_by(note)}. A person can restore an earlier "
            "version in the Cockpit; writing it again continues from this version."
        )
        axi_cli.print_next([f"{_TOOL} history {axi_cli.bare(name, '<name>')}"])
        return
    axi_cli.write_lines(f"{name}  version {note['version']}  by {_by(note)}  "
                        f"{axi_output.format_value(note.get('writtenAtUtc'))}")
    sys.stdout.flush()
    axi_output.write_blocks(sys.stdout, *_text_lines(note.get("text")))
    axi_cli.print_next([f"{_TOOL} set {axi_cli.bare(name, '<name>')} <text>"])


def set_note(name: str, text: str, expected_version: Optional[int], json_output: bool) -> None:
    """Write a note, sending the version this session last read."""
    if text == "-":
        text = sys.stdin.read()
    sent = expected_version if expected_version is not None else last_read_version(name)
    try:
        note = gateway.put_json(f"{ROUTE}/{gateway.path_segment(name)}", {"text": text, "expectedVersion": sent})
    except gateway.GatewayError as exc:
        if exc.status == 409 and isinstance(exc.body, dict) and exc.body.get("outcome") == "Stale":
            _stale(name, exc, sent, "written")
        _fail_gateway(exc)
        return
    if not isinstance(note, dict) or not isinstance(note.get("version"), int):
        axi_cli.fail("the Gateway answered without the version it wrote, so this command cannot show the note was "
                     "written. Read it back before relying on it.", [f"{_TOOL} get {axi_cli.bare(name, '<name>')}"])
        return
    _remember({name: note["version"]})
    if json_output:
        print(json.dumps(note, indent=2))
        return
    axi_cli.write_lines(f"Written: '{name}' is now version {note['version']}.")


def delete_note(name: str, expected_version: Optional[int], json_output: bool) -> None:
    """Delete a note, sending the version this session last read. A person can restore it."""
    sent = expected_version if expected_version is not None else last_read_version(name)
    try:
        note = gateway.delete(f"{ROUTE}/{gateway.path_segment(name)}", body={"expectedVersion": sent})
    except gateway.GatewayError as exc:
        if exc.status == 409 and isinstance(exc.body, dict) and exc.body.get("outcome") == "Stale":
            _stale(name, exc, sent, "deleted")
        _fail_gateway(exc)
        return
    if not isinstance(note, dict) or not isinstance(note.get("version"), int):
        axi_cli.fail("the Gateway answered without the version of the delete, so this command cannot show the note "
                     "was deleted.", [f"{_TOOL} get {axi_cli.bare(name, '<name>')}"])
        return
    _remember({name: note["version"]})
    if json_output:
        print(json.dumps(note, indent=2))
        return
    axi_cli.write_lines(f"Deleted: '{name}' (version {note['version']}). A person can restore it in the Cockpit.")


def history(name: str, json_output: bool) -> None:
    """Every kept version of a note, newest first."""
    try:
        page = gateway.get_json(f"{ROUTE}/{gateway.path_segment(name)}/history")
    except gateway.GatewayError as exc:
        _fail_gateway(exc)
        return
    versions = page.get("versions") if isinstance(page, dict) else None
    if not isinstance(versions, list):
        axi_cli.fail("the Gateway answered with no list of versions.", [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    if not versions:
        axi_cli.write_lines(f"'{name}' has no kept versions: it was never written.")
        return
    lines = []
    for v in versions:
        what = "(deleted)" if v.get("deleted") else _first_line(v.get("text"))
        lines.append(f"v{v.get('version')}  {axi_output.format_value(v.get('writtenAtUtc'))}  {_by(v)}  {what}")
    axi_cli.write_lines(*lines)

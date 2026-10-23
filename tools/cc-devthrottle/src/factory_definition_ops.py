"""A factory's definition, held on the Gateway, for cc-devthrottle (Factory Definition mission, phase 1).

A definition is everything that says what a factory is - factory.yaml, one file per seat, its grants, rule sets,
files, checks and its own skills - held on the Gateway as one versioned map of path to text. Six verbs, each naming
the factory:

- `factory definition get`      the published version: a summary and the file list; `--draft` for the newest
                                version, `--version N` for one by its number, `--file <path>` for one file's text
- `factory definition history`  one page of the versions, newest first, without their content
- `factory definition save`     save every file under a folder as a new DRAFT version
- `factory definition publish`  make one version the published definition - a person's act
- `factory definition restore`  publish a copy of an older version as the newest - a person's act
- `factory definition diff`     what changes from one version to another, as the Gateway words it
- `factory definition schemas`  the JSON Schemas the Gateway's check enforces, to check a folder without saving it

And two that move a whole definition between the Gateway and a folder (phase 3):

- `factory export`  write a factory's definition to a folder, byte for byte as the Gateway holds it; a factory with no
                    definition is written from what is live, and what could not be written is listed
- `factory import`  send a folder; the Gateway checks it exactly as a save and stores it as the next draft. It never
                    publishes.

THE GATEWAY RULES, THIS MODULE PRINTS (rule 7). Every refusal and every finding of the whole-definition check is
printed as the Gateway wrote it - `file:line: message` - and "what changes" is printed sentence by sentence and line
by line as the Gateway composed it. Nothing here decides what a finding or a change means. A refusal exits non-zero.

A PUBLISH DRIVES THE FACTORY (phase 4). `publish` and `restore` print what changes and then `applied`: one sentence
per step the Gateway's transaction took - the registry row, each schedule, each trigger - verbatim. `diff` prints the
published version it compared against and `plan`: what a publish WOULD do to the live factory now. Pass that
published version as `--replaces` and a publish that another one overtook is refused instead of shown against the
wrong version.

TWO WRITERS ON ONE DEFINITION. `save` sends the version the caller last READ (`--expected-version`, 0 when the
factory has no definition on the Gateway yet). It is required and never looked up here just before writing: a
version nobody was shown is not a version anybody read. A stale save is refused with the newest version attached;
this module says which version that is and how to read it.
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
from cc_shared.tool_errors import note_failure  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Gateway's routes for a factory's definition.
ROUTE = "factory-definitions"

_TOOL = "cc-devthrottle factory definition"


def _route(factory: str) -> str:
    return f"{ROUTE}/{gateway.path_segment(factory)}"


def _f(factory: str) -> str:
    """The factory as a next step carries it, or a placeholder when it cannot be pasted."""
    return axi_cli.bare(factory, "<factory>")


def _by(v: Dict[str, Any]) -> str:
    kind = v.get("authorKind") or "?"
    who = v.get("authorId")
    return f"{kind} {who}" if who else str(kind)


def _is_version(answer: Any) -> bool:
    return isinstance(answer, dict) and isinstance(answer.get("version"), int) and isinstance(answer.get("state"), str)


def _outcome(exc: gateway.GatewayError) -> Optional[str]:
    return exc.body.get("outcome") if isinstance(exc.body, dict) else None


def _finding_line(f: Dict[str, Any]) -> str:
    """One finding exactly as the Gateway gave it: `file:line: message` (no line for a finding about a whole file)."""
    where = f.get("file") or ""
    line = f.get("line")
    if isinstance(line, int) and line > 0:
        where = f"{where}:{line}"
    return f"  {where}: {f.get('message', '')}"


def _findings_lines(findings: Any) -> List[str]:
    """Errors, then notices, each under its count: the Gateway's order kept inside each."""
    if not isinstance(findings, list):
        return []
    lines: List[str] = []
    for severity, label in (("error", "errors"), ("notice", "notices")):
        these = [f for f in findings if isinstance(f, dict) and f.get("severity") == severity]
        if these:
            lines.append(f"{label}[{len(these)}]:")
            lines.extend(_finding_line(f) for f in these)
    return lines


def _fail_gateway(exc: gateway.GatewayError, next_commands: Optional[List[str]] = None) -> None:
    """A refusal, printed verbatim: the Gateway's sentence, and every finding of the check when it carries them."""
    findings = exc.body.get("findings") if isinstance(exc.body, dict) else None
    commands = next_commands or [axi_cli.CHECK_GATEWAY]
    if not findings:
        axi_cli.fail(str(exc), commands)
        return
    sys.stdout.flush()
    axi_output.write_blocks(
        sys.stderr,
        axi_cli.ascii_text(f"Error: {str(exc).strip()}"),
        *(axi_cli.ascii_text(line) for line in _findings_lines(findings)),
        axi_output.format_help(commands),
    )
    note_failure(str(exc).strip())
    raise typer.Exit(1)


def _standing_line(v: Dict[str, Any]) -> Optional[str]:
    """What else is true of the definition beside the version shown: a newer draft waiting, or nothing published."""
    newest = v.get("newestVersion")
    published = v.get("publishedVersion")
    if isinstance(newest, int) and newest > v["version"]:
        return f"A newer version, {newest}, is waiting and is not published. Read it with 'get --draft'."
    if v["state"] == "draft":
        return ("No version is published yet." if published is None
                else f"This is a draft. Version {published} is the published definition until a person publishes this one.")
    return None


def _changes_lines(changes: Any) -> List[str]:
    """"What changes", as the Gateway composed it: the summary, then every sentence, detail and line difference."""
    if not isinstance(changes, dict):
        return []
    lines = [f"what changes: {changes.get('summary', '')}"]

    def diff(d: Any) -> None:
        if not isinstance(d, dict):
            return
        for row in d.get("lines") or []:
            lines.append(f"      {row.get('marker', '')}{' ' if row.get('marker') else ''}{row.get('text', '')}")
        if d.get("note"):
            lines.append(f"      {d['note']}")

    for section in ("factory", "seats", "starts", "rules", "grants", "files"):
        for item in changes.get(section) or []:
            if not isinstance(item, dict):
                continue
            lines.append(f"  {item.get('sentence', '')}")
            for detail in item.get("details") or []:
                lines.append(f"    {detail}")
            diff(item.get("diff"))
            diff(item.get("briefDiff"))
    return lines


# ---------------------------------------------------------------------------------------------------
# The verbs.
# ---------------------------------------------------------------------------------------------------

def get_definition(factory: str, draft: bool, version: Optional[int], file: Optional[str], json_output: bool) -> None:
    """The published version; with `draft` the newest; with `version` that one. A summary and the file list, or
    with `file` that one file's text."""
    if draft and version is not None:
        axi_cli.usage_error("--draft reads the newest version and --version reads the one you name; pass one of them.")
        return
    which = "/draft" if draft else f"/versions/{version}" if version is not None else ""
    try:
        found = gateway.get_json(_route(factory) + which)
    except gateway.GatewayError as exc:
        if exc.status == 404:
            _fail_gateway(exc, [f"{_TOOL} history {_f(factory)}"] if version is not None
                          else [f"{_TOOL} get {_f(factory)} --draft",
                                f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version 0"])
        _fail_gateway(exc)
        return
    files = found.get("files") if isinstance(found, dict) else None
    if not _is_version(found) or not isinstance(files, dict):
        # Absent is not empty: an answer without files must never read as "this definition holds nothing".
        axi_cli.fail("the Gateway's answer carried no definition; this command will not report that as an empty one.",
                     [axi_cli.CHECK_GATEWAY])
        return
    v = found["version"]
    if file is not None:
        if file not in files:
            axi_cli.fail(f"version {v} of the definition of {found.get('factory')} holds no file '{file}'; it holds "
                         f"{len(files)} files, listed by 'get' without --file.",
                         [f"{_TOOL} get {_f(factory)} --version {v}"])
            return
        if json_output:
            # The filter applies to --json too: one file, never the whole definition.
            print(json.dumps({"factory": found.get("factory"), "version": v, "path": file, "text": files[file]}, indent=2))
            return
        sys.stdout.flush()
        text = files[file]
        # write_blocks ends the output with a newline of its own, so a file that ends with one is not given a second.
        lines = (text[:-1] if text.endswith("\n") else text).split("\n")
        axi_output.write_blocks(sys.stdout, *([axi_cli.ascii_text(line) for line in lines] if text else ["(empty)"]))
        return
    if json_output:
        print(json.dumps(found, indent=2))
        return
    lines = [f"{found.get('factory')}  version {v}  {found['state']}  written by {_by(found)}  "
             f"{axi_output.format_value(found.get('writtenAtUtc'))}  {found.get('fileCount')} files  "
             f"{found.get('contentBytes')} bytes"]
    if found.get("changeNote"):
        lines.append(f"note: {found['changeNote']}")
    if isinstance(found.get("restoredFrom"), int):
        lines.append(f"A copy of version {found['restoredFrom']}, restored.")
    standing = _standing_line(found)
    if standing:
        lines.append(standing)
    lines.append(f"files[{len(files)}]{{path,bytes}}:")
    for path in sorted(files):
        lines.append(f"  {path},{len(files[path].encode('utf-8'))}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_TOOL} get {_f(factory)} --version {v} --file <path>",
                        f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version {found.get('newestVersion', v)}",
                        f"{_TOOL} history {_f(factory)}"])


def history(factory: str, json_output: bool, count: Optional[int] = None, before: Optional[int] = None) -> None:
    """One page of a factory's versions, newest first, without their content."""
    query = urlencode([(name, value) for name, value in (("limit", count), ("before", before)) if value is not None])
    try:
        page = gateway.get_json(_route(factory) + "/history" + (f"?{query}" if query else ""))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_TOOL} history {_f(factory)}"] if exc.status == 400 else None)
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
        axi_cli.write_lines(f"count: 0. {page.get('factory', factory)} has no definition on the Gateway.")
        axi_cli.print_next([f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version 0"])
        return
    if not versions:
        axi_cli.write_lines(f"count: 0 of {total}. No version is below version {before}.")
        axi_cli.print_next([f"{_TOOL} history {_f(factory)}"])
        return
    published = page.get("publishedVersion")
    next_before = page.get("nextBefore")
    lines = [f"count: {len(versions)} of {total}. " + ("No version is published." if published is None
                                                      else f"Version {published} is the published definition."),
             f"versions[{len(versions)}]{{version,state,written,by,files,bytes,note}}:"]
    for v in versions:
        mark = "published" if v.get("state") == "published" else "draft"
        if v.get("version") == published:
            mark = "PUBLISHED NOW"
        note = v.get("changeNote") or ""
        if isinstance(v.get("restoredFrom"), int):
            note = (f"restored from version {v['restoredFrom']}. " + note).strip()
        lines.append(f"  v{v.get('version')},{mark},{axi_output.format_value(v.get('writtenAtUtc'))},{_by(v)},"
                     f"{v.get('fileCount')},{v.get('contentBytes')},{json.dumps(note) if note else '-'}")
    if isinstance(next_before, int):
        lines.append(f"Older versions follow, below version {next_before}.")
    axi_cli.write_lines(*lines)
    older = [f"{_TOOL} history {_f(factory)} --before {next_before}"] if isinstance(next_before, int) else []
    axi_cli.print_next(older + [f"{_TOOL} get {_f(factory)} --version <version>",
                                f"{_TOOL} diff {_f(factory)} --to <version>"])


def read_folder(folder: str, verb: str = "save", help_command: str = f"{_TOOL} save --help") -> Dict[str, str]:
    """Every file under `folder`, by its path relative to the folder with forward slashes, as UTF-8 text. Nothing is
    skipped: a file the Gateway does not take is refused there, by name, rather than quietly left out here."""
    root = Path(folder)
    if not root.is_dir():
        axi_cli.fail(f"there is no folder at {root}, so there is no definition to {verb}.", [help_command])
    content: Dict[str, str] = {}
    for path in sorted(p for p in root.rglob("*") if p.is_file()):
        relative = path.relative_to(root).as_posix()
        try:
            # newline="" keeps the file's own line endings: the Gateway stores exactly what is on disk.
            with path.open("r", encoding="utf-8", newline="") as handle:
                content[relative] = handle.read()
        except (OSError, UnicodeDecodeError) as exc:
            axi_cli.fail(f"the file {relative} could not be read as UTF-8 text: {exc}. Every file in a definition is "
                         "text.", [help_command])
    if not content:
        axi_cli.fail(f"the folder {root} holds no files, so there is no definition to {verb}.", [help_command])
    return content


def save(factory: str, folder: str, expected_version: int, note: Optional[str], json_output: bool) -> None:
    """Save every file under a folder as a new draft version, sending the version the caller last read."""
    content = read_folder(folder)
    body: Dict[str, Any] = {"expectedVersion": expected_version, "content": content}
    if note is not None:
        body["note"] = note
    try:
        saved = gateway.put_json(_route(factory) + "/draft", body)
    except gateway.GatewayError as exc:
        if exc.status == 409 and _outcome(exc) == "Stale":
            current = exc.body.get("current")
            now = current.get("version") if isinstance(current, dict) else None
            if not isinstance(now, int):
                # No current version on a stale refusal is the factory having no definition at all.
                axi_cli.fail(f"Not saved: {exc}.", [f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version 0"])
            axi_cli.fail(
                f"Not saved: {exc}. Version {now} was written by {_by(current)}.",
                [f"{_TOOL} get {_f(factory)} --draft",
                 f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version {now}"])
        if exc.status == 409 and _outcome(exc) == "TooManyDrafts":
            axi_cli.fail(f"Not saved: {exc}", [f"{_TOOL} history {_f(factory)}"])
        _fail_gateway(exc, [f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version {expected_version}"]
                      if exc.status in (400, 422) else None)
        return
    if not _is_version(saved):
        axi_cli.fail("the Gateway answered without the version it saved, so this command cannot show the draft was "
                     "saved. Read it back before relying on it.", [f"{_TOOL} get {_f(factory)} --draft"])
        return
    if json_output:
        print(json.dumps(saved, indent=2))
        return
    published = saved.get("publishedVersion")
    axi_cli.write_lines(
        f"Saved: version {saved['version']} of the definition of {saved.get('factory')} is a draft "
        f"({saved.get('fileCount')} files, {saved.get('contentBytes')} bytes).",
        ("No version is published yet." if published is None else f"Version {published} stays the published definition")
        + (" until a person publishes this draft." if published is not None else ""),
        *_findings_lines(saved.get("findings")),
    )
    axi_cli.print_next([f"{_TOOL} diff {_f(factory)} --to {saved['version']}",
                        f"{_TOOL} publish {_f(factory)} --version {saved['version']}"])


def _with_replaces(body: Dict[str, Any], replaces: Optional[int]) -> Dict[str, Any]:
    """The body, with the published version the person saw when they give one (0 = none was published)."""
    if replaces is not None:
        body["replaces"] = replaces
    return body


def publish(factory: str, version: int, json_output: bool, replaces: Optional[int] = None) -> None:
    """Publish one version. A person's act: with a session's key the Gateway refuses it and says so."""
    try:
        answer = gateway.post_json(_route(factory) + "/publish", _with_replaces({"version": version}, replaces))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, _after_refusal(exc, factory))
        return
    _published(answer, factory, json_output, f"Published: version {version}")


def restore(factory: str, version: int, expected_version: int, json_output: bool, replaces: Optional[int] = None) -> None:
    """Publish a copy of an older version as the newest. A person's act, refused for a session's key."""
    try:
        answer = gateway.post_json(_route(factory) + "/restore",
                                   _with_replaces({"version": version, "expectedVersion": expected_version}, replaces))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, _after_refusal(exc, factory))
        return
    _published(answer, factory, json_output, f"Restored: version {version}")


def _after_refusal(exc: gateway.GatewayError, factory: str) -> List[str]:
    """What to run after a refused publish or restore. A session is told to save a draft, because that is the one
    thing it may do; everything else starts from the history."""
    if _outcome(exc) == "OnlyAPersonPublishes":
        return [f"{_TOOL} save {_f(factory)} --dir <folder> --expected-version <newest version>",
                f"{_TOOL} history {_f(factory)}"]
    if _outcome(exc) == "Stale":
        return [f"{_TOOL} diff {_f(factory)}", f"{_TOOL} history {_f(factory)}"]
    if exc.status in (400, 404, 409, 422):
        return [f"{_TOOL} history {_f(factory)}"]
    return [axi_cli.CHECK_GATEWAY]


def _published(answer: Any, factory: str, json_output: bool, what: str) -> None:
    if not _is_version(answer):
        axi_cli.fail("the Gateway answered without the published version, so this command cannot show it was "
                     "published. Read it back before relying on it.", [f"{_TOOL} get {_f(factory)}"])
        return
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"{what} of the definition of {answer.get('factory')}. Version {answer['version']} is now "
                        "the published definition.",
                        *_changes_lines(answer.get("changes")),
                        *_applied_lines(answer.get("applied")),
                        *_findings_lines(answer.get("findings")))
    axi_cli.print_next([f"{_TOOL} get {_f(factory)}", f"{_TOOL} history {_f(factory)}"])


def _applied_lines(applied: Any) -> List[str]:
    """What the publish's transaction did to the live factory, one sentence per step, as the Gateway wrote them."""
    if not isinstance(applied, list):
        return []
    return ["applied:", *(f"  {step}" for step in applied)]


def _plan_lines(plan: Any) -> List[str]:
    """What a publish WOULD do to the live factory now, as the Gateway composed it: its heading and its steps, or why
    a publish would be refused and where."""
    if not isinstance(plan, dict):
        return []
    lines = [str(plan.get("heading", ""))]
    if plan.get("refused"):
        lines.append(f"  Refused: {plan['refused']}")
        lines.extend(f"  {line}" for line in _findings_lines(plan.get("findings")))
    lines.extend(f"  {step}" for step in plan.get("steps") or [])
    return lines


def diff(factory: str, from_version: Optional[int], to_version: Optional[int], json_output: bool) -> None:
    """What changes from one version to another: by default from the published version to the newest."""
    query = urlencode([(name, value) for name, value in (("from", from_version), ("to", to_version)) if value is not None])
    try:
        answer = gateway.get_json(_route(factory) + "/diff" + (f"?{query}" if query else ""))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_TOOL} history {_f(factory)}"] if exc.status in (400, 404) else None)
        return
    changes = answer.get("changes") if isinstance(answer, dict) else None
    if not isinstance(changes, dict) or not isinstance(answer.get("to"), int):
        # Absent is not "nothing changes": an answer without the changes must never read as an empty difference.
        axi_cli.fail("the Gateway's answer carried no difference; this command will not report that as no change.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    start = answer.get("from")
    published = answer.get("publishedVersion")
    axi_cli.write_lines(
        f"{answer.get('factory')}: from " + (f"version {start}" if isinstance(start, int) else "nothing published")
        + f" to version {answer['to']}.",
        f"published version: {published}" if isinstance(published, int) else "published version: none",
        *_changes_lines(changes),
        *_plan_lines(answer.get("plan")))
    replaces = published if isinstance(published, int) else 0
    plan = answer.get("plan")
    act = "restore" if isinstance(plan, dict) and plan.get("act") == "restore" else "publish"
    axi_cli.print_next([f"{_TOOL} {act} {_f(factory)} --version {answer['to']} --replaces {replaces}",
                        f"{_TOOL} history {_f(factory)}"])


# ---------------------------------------------------------------------------------------------------
# Export, import and the schemas (phase 3). The Gateway composes the folder and rules on the import; this module moves
# the files, byte for byte, and prints what the Gateway said.
# ---------------------------------------------------------------------------------------------------

_FACTORY_TOOL = "cc-devthrottle factory"


def _out_path(root: Path, relative: str) -> Path:
    """Where one file of the folder goes. The Gateway holds only paths inside a definition; a path that would land
    outside the folder is refused here too, because this is the code that writes to the disk."""
    target = (root / relative).resolve()
    if (not relative or relative.startswith("/") or "\\" in relative or ".." in relative.split("/")
            or root.resolve() not in target.parents):
        axi_cli.fail(f"the Gateway's answer holds the path '{relative}', which is not inside the folder; nothing was "
                     "written.", [axi_cli.CHECK_GATEWAY])
    return target


def export(factory: str, out: str, version: Optional[int], json_output: bool) -> None:
    """Write a factory's definition into a new or empty folder, every file exactly as the Gateway sent it: UTF-8 with no
    byte-order mark and the file's own line endings, so two exports of one version are the same bytes."""
    root = Path(out)
    if root.exists() and (not root.is_dir() or any(root.iterdir())):
        axi_cli.fail(f"{root} is not an empty folder. An export writes a whole definition, so it goes into a new or "
                     "empty folder: a file left over from before would be imported with it.",
                     [f"{_FACTORY_TOOL} export {_f(factory)} --out <new folder>"])
        return
    query = f"?version={version}" if version is not None else ""
    try:
        answer = gateway.get_json(_route(factory) + "/export" + query)
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_TOOL} history {_f(factory)}"] if exc.status in (400, 404) else None)
        return
    files = answer.get("files") if isinstance(answer, dict) else None
    reported = answer.get("reported") if isinstance(answer, dict) else None
    if (not isinstance(files, dict) or not files or not all(isinstance(t, str) for t in files.values())
            or not isinstance(reported, list)):
        # Absent is not empty: an answer without files must never be written as an empty folder.
        axi_cli.fail("the Gateway's answer carried no files to write; nothing was written.", [axi_cli.CHECK_GATEWAY])
        return

    targets = {path: _out_path(root, path) for path in sorted(files)}
    written: List[Dict[str, Any]] = []
    for path, target in targets.items():
        data = files[path].encode("utf-8")
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("wb") as handle:
                handle.write(data)
        except OSError as exc:
            axi_cli.fail(f"{path} could not be written to {root}: {exc}. {len(written)} of {len(targets)} files were "
                         "written before it; the folder is incomplete, so delete it before exporting again.",
                         [f"{_FACTORY_TOOL} export {_f(factory)} --out <new folder>"])
        written.append({"path": path, "bytes": len(data)})

    if json_output:
        shown = {key: answer.get(key) for key in ("factory", "source", "version", "state", "summary")}
        shown.update({"out": str(root), "files": written, "reported": reported})
        print(json.dumps(shown, indent=2))
        return
    lines = [axi_cli.ascii_text(str(answer.get("summary", ""))),
             f"Written to {root}.",
             f"files[{len(written)}]{{path,bytes}}:"]
    lines.extend(f"  {w['path']},{w['bytes']}" for w in written)
    if reported:
        lines.append(f"reported[{len(reported)}]{{file,message}}:")
        lines.extend(axi_cli.ascii_text(f"  {r.get('file', '')}: {r.get('message', '')}") for r in reported
                     if isinstance(r, dict))
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_FACTORY_TOOL} import {root}", f"{_TOOL} schemas"])


def import_folder(folder: str, note: Optional[str], json_output: bool) -> None:
    """Send every file under a folder; the Gateway checks it exactly as a draft save and stores it as the next draft of
    the factory its factory.yaml names. It never publishes: the published version is the same after as before."""
    content = read_folder(folder, "import", f"{_FACTORY_TOOL} import --help")
    body: Dict[str, Any] = {"content": content}
    if note is not None:
        body["note"] = note
    named = "<factory>"
    try:
        saved = gateway.post_json(f"{ROUTE}/import", body)
    except gateway.GatewayError as exc:
        if exc.status == 409 and _outcome(exc) == "TooManyDrafts":
            axi_cli.fail(f"Not imported: {exc}", [f"{_TOOL} history {named}"])
        _fail_gateway(exc, [f"{_FACTORY_TOOL} import {folder}", f"{_TOOL} schemas"]
                      if exc.status in (400, 409, 422) else None)
        return
    if not _is_version(saved):
        axi_cli.fail("the Gateway answered without the version it stored, so this command cannot show the folder was "
                     "imported. Read it back before relying on it.", [f"{_TOOL} get {named} --draft"])
        return
    if json_output:
        print(json.dumps(saved, indent=2))
        return
    target = _f(str(saved.get("factory") or ""))
    published = saved.get("publishedVersion")
    axi_cli.write_lines(
        f"Imported: version {saved['version']} of the definition of {saved.get('factory')} is a draft "
        f"({saved.get('fileCount')} files, {saved.get('contentBytes')} bytes).",
        *(["This import created the factory's definition."] if saved.get("created") is True else []),
        "Nothing was published: " + ("no version is published yet." if published is None
                                     else f"version {published} stays the published definition."),
        *_findings_lines(saved.get("findings")),
    )
    axi_cli.print_next([f"{_TOOL} diff {target} --to {saved['version']}",
                        f"{_TOOL} publish {target} --version {saved['version']}"])


def schemas(out: Optional[str], json_output: bool) -> None:
    """The JSON Schemas the Gateway's check enforces, one per file type; with `out`, each written as its own file."""
    try:
        answer = gateway.get_json("factory-definition-schemas")
    except gateway.GatewayError as exc:
        _fail_gateway(exc)
        return
    items = answer.get("schemas") if isinstance(answer, dict) else None
    if not isinstance(items, list) or not items or not all(isinstance(i, dict) for i in items):
        axi_cli.fail("the Gateway answered with no schemas; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if out is not None:
        root = Path(out)
        root.mkdir(parents=True, exist_ok=True)
        for item in items:
            target = _out_path(root, str(item.get("name", "")))
            with target.open("wb") as handle:
                handle.write((json.dumps(item.get("schema"), indent=2) + "\n").encode("utf-8"))
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    lines = [f"count: {len(items)}. " + axi_cli.ascii_text(str(answer.get("note", ""))),
             f"schemas[{len(items)}]{{name,applies}}:"]
    lines.extend(f"  {item.get('name')},{item.get('applies')}" for item in items)
    if out is not None:
        lines.append(f"Written to {out}.")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_TOOL} schemas --out <folder>", f"{_FACTORY_TOOL} import <folder>"])

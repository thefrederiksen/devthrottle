"""A factory's records, held on the Gateway, for cc-devthrottle (Factory Definition mission, phase 2).

Three kinds of record, each beside the factory's memory and its activity record:

- `factory journal`  what a seat wrote down about a day. Append-only: `add` and the two reads, never an edit.
- `factory doc`      a named working document a seat reads and rewrites - the outreach ledger, the playbook.
                     Every version kept. `write` sends the version the caller last READ, and a write against a
                     version that is no longer the newest is refused with the CURRENT text, so the caller can merge
                     and write again. Two runs writing the ledger at once: one is refused, nothing is lost.
- `factory attach`   a file a factory kept - a screenshot, a walk's JSON. Bytes kept apart from any journal.

And the one-time upload (`factory records upload`): it reads every factory records folder under the machine's local
application data folder, stores each file as the record it is, and prints how many files it read against how many
it stored. It NEVER deletes or changes anything locally.

Every command names its factory. A session may read and write only its own factory's records: the Gateway refuses
any other, in its own words, and this module prints those words.
"""

from __future__ import annotations

import base64
import hashlib
import json
import mimetypes
import os
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple
from urllib.parse import urlencode

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output, gateway  # noqa: E402

from . import axi_cli  # noqa: E402

#: The Gateway's routes for a factory's records.
ROUTE = "factory-records"

_JOURNAL = "cc-devthrottle factory journal"
_DOC = "cc-devthrottle factory doc"
_ATTACH = "cc-devthrottle factory attach"
_UPLOAD = "cc-devthrottle factory records upload"


def _root(factory: str) -> str:
    return f"{ROUTE}/{gateway.path_segment(factory)}"


def _f(factory: str) -> str:
    return f"--factory {axi_cli.bare(factory, '<factory>')}"


def _by(record: Dict[str, Any]) -> str:
    kind = record.get("authorKind") or "?"
    who = record.get("authorId")
    return f"{kind} {who}" if who else str(kind)


def _text_lines(text: Optional[str]) -> List[str]:
    """Text as lines of printable ASCII: every character that is not is escaped, never dropped. `--json` gives the
    exact text."""
    if not text:
        return ["(empty)"]
    return [axi_cli.ascii_text(line) for line in text.split("\n")]


def _outcome(exc: gateway.GatewayError) -> Optional[str]:
    return exc.body.get("outcome") if isinstance(exc.body, dict) else None


def _fail_gateway(exc: gateway.GatewayError, next_commands: Optional[List[str]] = None) -> None:
    axi_cli.fail(str(exc), next_commands or [axi_cli.CHECK_GATEWAY])


def _read_text_file(path: str, what: str, usage: str) -> str:
    """A file's text, or standard input when the path is `-`."""
    if path == "-":
        return sys.stdin.read()
    file = Path(path)
    if not file.is_file():
        axi_cli.fail(f"there is no file at {file}, so there is no {what} to send.", [usage])
    try:
        return file.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        axi_cli.fail(f"the file {file} could not be read as UTF-8 text: {exc}.", [usage])


def _written(answer: Any, key: str) -> Optional[Dict[str, Any]]:
    """The record a write answered with, or None when the answer carried none."""
    if isinstance(answer, dict) and answer.get("outcome") in ("Written", "AlreadyStored") and isinstance(answer.get(key), dict):
        return answer[key]
    return None


# ---------------------------------------------------------------------------------------------------
# The journal.
# ---------------------------------------------------------------------------------------------------

def journal_list(factory: str, seat: Optional[str], factory_only: bool, since: Optional[str], until: Optional[str],
                 count: Optional[int], json_output: bool) -> None:
    """The factory's journal entries, newest day first, without their text."""
    if seat and factory_only:
        axi_cli.usage_error("--seat reads one seat's journal and --factory-only the factory's own; pass one of them.")
    params = [(k, v) for k, v in (("seat", seat), ("since", since), ("until", until), ("limit", count)) if v is not None]
    if factory_only:
        params.append(("factoryOnly", "true"))
    query = urlencode(params)
    try:
        page = gateway.get_json(_root(factory) + "/journal" + (f"?{query}" if query else ""))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_JOURNAL} list {_f(factory)}"] if exc.status == 400 else None)
        return
    entries = page.get("entries") if isinstance(page, dict) else None
    if not isinstance(entries, list):
        axi_cli.fail("the Gateway answered with no list of entries; this command will not report that as no entries.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    total = page.get("total")
    if not entries:
        axi_cli.write_lines(f"count: 0. {factory} has no journal entries" + (" that match." if params else "."))
        axi_cli.print_next([f"{_JOURNAL} add {_f(factory)} --seat <seat> --file <path>"])
        return
    lines = [f"count: {len(entries)} of {total}."]
    for e in entries:
        files = len(e.get("attachmentIds") or [])
        lines.append(f"{e.get('id')}  {e.get('date')}  {e.get('seat') or '(factory)'}  {e.get('textBytes')} bytes"
                     + (f"  {files} attachment(s)" if files else "") + f"  {_by(e)}"
                     + (f"  from {axi_cli.ascii_text(e['source'])}" if e.get("source") else ""))
    if isinstance(total, int) and total > len(entries):
        lines.append(f"{total - len(entries)} more match. Narrow with --since and --until, or raise --count.")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_JOURNAL} get {_f(factory)} --id <id>"])


def journal_get(factory: str, entry_id: str, json_output: bool) -> None:
    """One journal entry, with its text."""
    try:
        entry = gateway.get_json(f"{_root(factory)}/journal/{gateway.path_segment(entry_id)}")
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_JOURNAL} list {_f(factory)}"] if exc.status in (400, 404) else None)
        return
    if not isinstance(entry, dict) or not isinstance(entry.get("text"), str):
        axi_cli.fail("the Gateway's answer carried no entry text; this command will not report that as an empty entry.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(entry, indent=2))
        return
    head = f"{entry.get('factory')}/{entry.get('seat') or '(factory)'}  {entry.get('date')}  written by {_by(entry)}  " \
           f"{axi_output.format_value(entry.get('writtenAtUtc'))}"
    files = entry.get("attachmentIds") or []
    lines = [head] + ([f"attachments: {', '.join(files)}"] if files else [])
    axi_cli.write_lines(*lines)
    sys.stdout.flush()
    axi_output.write_blocks(sys.stdout, *_text_lines(entry.get("text")))
    axi_cli.print_next([f"{_ATTACH} get {_f(factory)} --id <attachment id> --out <path>"] if files
                       else [f"{_JOURNAL} list {_f(factory)}"])


def journal_add(factory: str, seat: Optional[str], date: Optional[str], file: str, attachments: Sequence[str],
                json_output: bool) -> None:
    """Add one entry to a seat's journal, or to the factory's own when no seat is named."""
    text = _read_text_file(file, "journal entry", f"{_JOURNAL} add --help")
    body: Dict[str, Any] = {"seat": seat, "date": date, "text": text, "attachmentIds": list(attachments)}
    try:
        answer = gateway.post_json(_root(factory) + "/journal", body)
    except gateway.GatewayError as exc:
        if _outcome(exc) == "NoSuchAttachment":
            axi_cli.fail(f"Not added: {exc}", [f"{_ATTACH} add {_f(factory)} --file <path>", f"{_ATTACH} list {_f(factory)}"])
        _fail_gateway(exc, [f"{_JOURNAL} add --help"] if exc.status in (400, 413) else None)
        return
    entry = _written(answer, "entry")
    if entry is None:
        axi_cli.fail("the Gateway answered without the entry it added, so this command cannot show it was added. "
                     "List the journal before relying on it.", [f"{_JOURNAL} list {_f(factory)}"])
        return
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Added: entry {entry.get('id')} for {entry.get('date')} to the journal of "
                        f"{entry.get('seat') or 'the factory'} ({entry.get('textBytes')} bytes).")
    axi_cli.print_next([f"{_JOURNAL} get {_f(factory)} --id {entry.get('id')}"])


# ---------------------------------------------------------------------------------------------------
# Working documents.
# ---------------------------------------------------------------------------------------------------

def _doc_where(factory: str, name: str) -> str:
    return f"{_f(factory)} --name {axi_cli.bare(name, '<name>')}"


def doc_list(factory: str, json_output: bool) -> None:
    """The factory's working documents as they stand, without their text."""
    try:
        page = gateway.get_json(_root(factory) + "/documents")
    except gateway.GatewayError as exc:
        _fail_gateway(exc)
        return
    docs = page.get("documents") if isinstance(page, dict) else None
    if not isinstance(docs, list):
        axi_cli.fail("the Gateway answered with no list of documents; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    if not docs:
        axi_cli.write_lines(f"count: 0. {factory} has no working documents on the Gateway.")
        axi_cli.print_next([f"{_DOC} write {_f(factory)} --name <name> --file <path> --expected-version 0"])
        return
    lines = [f"count: {len(docs)}."]
    for d in docs:
        lines.append(f"{d.get('name')}  v{d.get('version')}  {d.get('textBytes')} bytes  {_by(d)}  "
                     f"{axi_output.format_value(d.get('writtenAtUtc'))}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_DOC} get {_f(factory)} --name <name>"])


def doc_get(factory: str, name: str, version: Optional[int], out: Optional[str], json_output: bool) -> None:
    """A document's newest version, or one version, with its text - printed, or written to `out`."""
    where = _doc_where(factory, name)
    params = [("name", name)] + ([("version", version)] if version is not None else [])
    try:
        doc = gateway.get_json(_root(factory) + "/documents/read?" + urlencode(params))
    except gateway.GatewayError as exc:
        if exc.status == 404:
            axi_cli.fail(str(exc), [f"{_DOC} history {where}"] if version is not None
                         else [f"{_DOC} list {_f(factory)}", f"{_DOC} write {where} --file <path> --expected-version 0"])
        _fail_gateway(exc)
        return
    if not isinstance(doc, dict) or not isinstance(doc.get("text"), str) or not isinstance(doc.get("version"), int):
        axi_cli.fail("the Gateway's answer carried no document text; this command will not report that as an empty "
                     "document.", [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(doc, indent=2))
        return
    head = f"{doc.get('factory')}/{doc.get('name')}  version {doc['version']}  written by {_by(doc)}  " \
           f"{axi_output.format_value(doc.get('writtenAtUtc'))}"
    if out:
        Path(out).write_text(doc["text"], encoding="utf-8", newline="")
        axi_cli.write_lines(head, f"Wrote its {doc.get('textBytes')} bytes to {out}.")
    else:
        axi_cli.write_lines(head)
        sys.stdout.flush()
        axi_output.write_blocks(sys.stdout, *_text_lines(doc["text"]))
    axi_cli.print_next([f"{_DOC} write {where} --file <path> --expected-version {doc['version']}"])


def doc_history(factory: str, name: str, count: Optional[int], before: Optional[int], json_output: bool) -> None:
    """One page of a document's versions, newest first, without their text."""
    where = _doc_where(factory, name)
    params = [("name", name)] + [(k, v) for k, v in (("limit", count), ("before", before)) if v is not None]
    try:
        page = gateway.get_json(_root(factory) + "/documents/history?" + urlencode(params))
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_DOC} history {where}"] if exc.status == 400 else None)
        return
    versions = page.get("versions") if isinstance(page, dict) else None
    if not isinstance(versions, list):
        axi_cli.fail("the Gateway answered with no list of versions; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    total = page.get("total")
    if not versions:
        axi_cli.write_lines(f"count: 0. {factory} has no working document called '{name}'.")
        return
    lines = [f"count: {len(versions)} of {total}."]
    for v in versions:
        lines.append(f"v{v.get('version')}  {axi_output.format_value(v.get('writtenAtUtc'))}  {_by(v)}  {v.get('textBytes')} bytes")
    next_before = page.get("nextBefore")
    if isinstance(next_before, int):
        lines.append(f"Older versions follow, below version {next_before}.")
    axi_cli.write_lines(*lines)
    older = [f"{_DOC} history {where} --before {next_before}"] if isinstance(next_before, int) else []
    axi_cli.print_next(older + [f"{_DOC} get {where} --version <version>"])


def doc_write(factory: str, name: str, file: str, expected_version: int, json_output: bool) -> None:
    """Write a file's text as the next version of a document, sending the version the caller last read.

    The version is NEVER looked up here just before writing: a version nobody was shown is not a version anybody
    read, and sending it would let this command replace text its caller never saw - the very loss the refusal exists
    to prevent.
    """
    where = _doc_where(factory, name)
    text = _read_text_file(file, "document", f"{_DOC} write --help")
    try:
        answer = gateway.put_json(_root(factory) + "/documents",
                                  {"name": name, "text": text, "expectedVersion": expected_version})
    except gateway.GatewayError as exc:
        if exc.status == 409 and _outcome(exc) == "Stale":
            current = exc.body.get("current") if isinstance(exc.body, dict) else None
            now = current.get("version") if isinstance(current, dict) else None
            if not isinstance(now, int):
                axi_cli.fail(f"Not written: {exc}.", [f"{_DOC} write {where} --file <path> --expected-version 0"])
            # THE CURRENT TEXT, PRINTED. The refusal carries it so the caller can merge without racing again.
            axi_cli.write_lines(
                f"Not written: another writer got there first. '{name}' is now at version {now} (by {_by(current)}); "
                f"you sent version {expected_version}. Its current text follows: merge your change into it and write "
                f"again with --expected-version {now}.")
            sys.stdout.flush()
            axi_output.write_blocks(sys.stdout, *_text_lines(current.get("text")))
            axi_cli.fail(str(exc), [f"{_DOC} get {where} --out <path>",
                                    f"{_DOC} write {where} --file <path> --expected-version {now}"])
        _fail_gateway(exc, [f"{_DOC} get {where}"] if exc.status in (400, 404, 409, 413) else None)
        return
    doc = _written(answer, "document")
    if doc is None:
        axi_cli.fail("the Gateway answered without the version it wrote, so this command cannot show it was written. "
                     "Read it back before relying on it.", [f"{_DOC} get {where}"])
        return
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Written: version {doc.get('version')} of '{doc.get('name')}' ({doc.get('textBytes')} bytes).")
    axi_cli.print_next([f"{_DOC} get {where}"])


# ---------------------------------------------------------------------------------------------------
# Attachments.
# ---------------------------------------------------------------------------------------------------

def attach_list(factory: str, seat: Optional[str], count: Optional[int], json_output: bool) -> None:
    """The factory's attachments, newest first, without their bytes."""
    query = urlencode([(k, v) for k, v in (("seat", seat), ("limit", count)) if v is not None])
    try:
        page = gateway.get_json(_root(factory) + "/attachments" + (f"?{query}" if query else ""))
    except gateway.GatewayError as exc:
        _fail_gateway(exc)
        return
    files = page.get("attachments") if isinstance(page, dict) else None
    if not isinstance(files, list):
        axi_cli.fail("the Gateway answered with no list of attachments; this command will not report that as none.",
                     [axi_cli.CHECK_GATEWAY])
        return
    if json_output:
        print(json.dumps(page, indent=2))
        return
    held = f"The factory holds {page.get('factoryBytes')} of {page.get('maxFactoryBytes')} bytes of attachments."
    if not files:
        axi_cli.write_lines(f"count: 0. {held}")
        axi_cli.print_next([f"{_ATTACH} add {_f(factory)} --file <path>"])
        return
    lines = [f"count: {len(files)} of {page.get('total')}. {held}"]
    for a in files:
        lines.append(f"{a.get('id')}  {a.get('name')}  {a.get('bytes')} bytes  {a.get('contentType')}  "
                     f"{a.get('seat') or '(factory)'}  {_by(a)}")
    axi_cli.write_lines(*lines)
    axi_cli.print_next([f"{_ATTACH} get {_f(factory)} --id <id> --out <path>"])


def attach_get(factory: str, attachment_id: str, out: str, json_output: bool) -> None:
    """One attachment's bytes, written to `out`, checked against the SHA-256 the Gateway stored."""
    try:
        a = gateway.get_json(f"{_root(factory)}/attachments/{gateway.path_segment(attachment_id)}")
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_ATTACH} list {_f(factory)}"] if exc.status in (400, 404) else None)
        return
    content = a.get("contentBase64") if isinstance(a, dict) else None
    if not isinstance(content, str):
        axi_cli.fail("the Gateway's answer carried no bytes; this command will not write an empty file in their place.",
                     [axi_cli.CHECK_GATEWAY])
        return
    data = base64.b64decode(content)
    if hashlib.sha256(data).hexdigest() != a.get("sha256"):
        axi_cli.fail("the bytes received do not match the SHA-256 the Gateway stored for them, so nothing was written.",
                     [f"{_ATTACH} get {_f(factory)} --id {attachment_id} --out {out}"])
    Path(out).write_bytes(data)
    if json_output:
        a = dict(a)
        a.pop("contentBase64", None)
        print(json.dumps({**a, "writtenTo": out}, indent=2))
        return
    axi_cli.write_lines(f"Wrote '{a.get('name')}' ({len(data)} bytes, {a.get('contentType')}) to {out}. "
                        "Its SHA-256 matches what the Gateway stored.")


def attach_add(factory: str, seat: Optional[str], file: str, name: Optional[str], content_type: Optional[str],
               json_output: bool) -> None:
    """Keep one file. Its bytes are never put inside a journal entry: name its id there instead."""
    path = Path(file)
    if not path.is_file():
        axi_cli.fail(f"there is no file at {path}, so there is nothing to keep.", [f"{_ATTACH} add --help"])
    data = path.read_bytes()
    kind = content_type or mimetypes.guess_type(path.name)[0]
    if not kind:
        axi_cli.usage_error(f"the content type of {path.name} cannot be told from its name; pass --content-type, "
                            "such as image/png or application/octet-stream.")
    body = {"seat": seat, "name": name or path.name, "contentType": kind,
            "contentBase64": base64.b64encode(data).decode("ascii")}
    try:
        answer = gateway.post_json(_root(factory) + "/attachments", body, timeout=120)
    except gateway.GatewayError as exc:
        _fail_gateway(exc, [f"{_ATTACH} list {_f(factory)}"] if exc.status in (400, 409, 413) else None)
        return
    a = _written(answer, "attachment")
    if a is None:
        axi_cli.fail("the Gateway answered without the attachment it kept, so this command cannot show it was kept.",
                     [f"{_ATTACH} list {_f(factory)}"])
        return
    if json_output:
        print(json.dumps(answer, indent=2))
        return
    axi_cli.write_lines(f"Kept: attachment {a.get('id')} '{a.get('name')}' ({a.get('bytes')} bytes). Name this id in a "
                        "journal entry with --attachment.")
    axi_cli.print_next([f"{_JOURNAL} add {_f(factory)} --file <path> --attachment {a.get('id')}"])


# ---------------------------------------------------------------------------------------------------
# The one-time upload (section 5.6).
# ---------------------------------------------------------------------------------------------------

#: The environment variable the upload's credential is read from: the account's machine credential, which the
#: Gateway accepts on the three record write routes only, and only for rows the upload names. On a hosted Gateway
#: that is a Director's own device key; on a self-hosted one, the shared machine token.
UPLOAD_TOKEN_VARIABLE = "CC_GATEWAY_UPLOAD_TOKEN"

#: The cc-secrets entry the upload's credential is held in.
UPLOAD_TOKEN_ENTRY = "devthrottle-gateway-upload-token"

#: The header the upload names its machine in. Every row it writes records that machine.
UPLOAD_MACHINE_HEADER = "X-DevThrottle-Upload-Machine"

#: Each records folder on a machine, the factory it belongs to, and the prefix its files' names take there. Taken
#: from the phase 0 inventory (docs/qa/factory-definition/phase-0/inventory/measure_records.py, pull request 3994),
#: so the upload's count of what it read reconciles against that measurement. A folder whose factory is None belongs
#: to no factory: its files are counted, listed and NOT uploaded.
ROOTS: List[Tuple[str, Optional[str], str]] = [
    ("cc-factory/devthrottle", "devthrottle", ""),
    # Two folders belong to cc-factory. The CFO's files keep their folder's name in front, so no file of one folder
    # can take the name of a file of the other.
    ("cc-factory/cfo", "cc-factory", "cfo/"),
    ("cc-factory-factory", "cc-factory", ""),
    ("cc-factory/vercel-release", None, ""),
    ("business-research-factory", "business-research", ""),
    ("clickfunnels-factory", "clickfunnels", ""),
    ("mindzie-web-factory", "mindzie-web", ""),
    ("warmforward-factory", "warmforward", ""),
    ("website-factory", "website-business", ""),
    ("cc-director/factories", "money-saver", ""),
]

# The inventory's kind rules, in its order. Kept identical so a file is the same kind in both.
ATTACHMENT_EXT = {"png", "jpg", "jpeg", "gif", "webp", "bmp", "wav", "mp3", "m4a", "mp4", "webm", "pdf", "zip"}
SCRATCH_DIRS = {"scratch", "transcribe", "tmp", "temp", "cache", "__pycache__"}
JOURNAL_DIRS = {"journal", "memos"}
JOURNAL_EXT = {"log", "jsonl"}
DATABASE_EXT = {"db", "sqlite", "sqlite3", "db-wal", "db-shm", "sqlite-wal", "sqlite-shm"}

JOURNAL = "journal"
DOCUMENT = "working document"
ATTACHMENT = "attachment"
DATABASE = "database"
SCRATCH = "scratch"

_DATE = re.compile(r"(\d{4}-\d{2}-\d{2})")


def kind_of(rel_parts: Sequence[str]) -> str:
    """One kind per file, by the phase 0 inventory's rules in its order."""
    name = rel_parts[-1]
    ext = name.rsplit(".", 1)[-1].lower() if "." in name else ""
    folders = {p.lower() for p in rel_parts[:-1]}
    if folders & SCRATCH_DIRS:
        return SCRATCH
    if ext in ATTACHMENT_EXT:
        return ATTACHMENT
    if ext in DATABASE_EXT or name.lower().endswith((".db-wal", ".db-shm")):
        return DATABASE
    if folders & JOURNAL_DIRS or ext in JOURNAL_EXT:
        return JOURNAL
    return DOCUMENT


def journal_date(rel_parts: Sequence[str], modified: float) -> str:
    """The day a journal file is about: the first yyyy-MM-dd in its name, then in its folders from the deepest up,
    and only when there is none, the day it was last written (UTC) - which is the day a log file was last added to."""
    for part in [rel_parts[-1]] + list(reversed(rel_parts[:-1])):
        m = _DATE.search(part)
        if m:
            try:
                datetime.strptime(m.group(1), "%Y-%m-%d")
                return m.group(1)
            except ValueError:
                continue
    return datetime.fromtimestamp(modified, tz=timezone.utc).strftime("%Y-%m-%d")


@dataclass
class Planned:
    """One file read, what it is, and what the upload will do with it."""
    root: str
    rel: str
    factory: Optional[str]
    kind: str
    size: int
    name: str = ""
    # Set when the file is not uploaded, by design or because it cannot be: the reason, in words.
    not_uploaded: Optional[str] = None
    # Set after sending: Written, AlreadyStored, or the refusal.
    outcome: Optional[str] = None
    refusal: Optional[str] = None

    @property
    def source(self) -> str:
        return f"{self.root}/{self.rel}"


@dataclass
class UploadReport:
    planned: List[Planned] = field(default_factory=list)
    missing_roots: List[str] = field(default_factory=list)


def plan_upload(base: Path) -> UploadReport:
    """Read every records folder and decide what each file is. Reads only; sends nothing; changes nothing."""
    report = UploadReport()
    for rel_root, factory, prefix in ROOTS:
        root = base.joinpath(*rel_root.split("/"))
        if not root.is_dir():
            report.missing_roots.append(rel_root)
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames.sort()
            for n in sorted(filenames):
                full = Path(dirpath) / n
                rel = full.relative_to(root).as_posix()
                parts = rel.split("/")
                item = Planned(root=rel_root, rel=rel, factory=factory, kind=kind_of(parts), size=full.stat().st_size,
                               name=(prefix + rel).lower())
                if factory is None:
                    item.not_uploaded = "its folder belongs to no factory"
                elif item.kind == DATABASE:
                    item.not_uploaded = "a database: the records stores hold journals, documents and files, not databases"
                elif item.kind == SCRATCH:
                    item.not_uploaded = "scratch: a working file the factory makes and throws away, not a record"
                report.planned.append(item)
    # TWO FILES, ONE NAME. Names are folded to lower case on the Gateway, so two files of one factory that differ only
    # in case would be one record there. Both are held back and listed rather than one silently replacing the other.
    seen: Dict[Tuple[str, str, str], Planned] = {}
    for item in report.planned:
        if item.not_uploaded or item.kind == JOURNAL:
            continue
        key = (item.factory or "", "attachment" if item.kind == ATTACHMENT else "document", item.name)
        if key in seen:
            for clash in (seen[key], item):
                clash.not_uploaded = f"its name is the same as another file's once case is folded ({clash.name})"
        else:
            seen[key] = item
    return report


def _send(item: Planned, base: Path, url: str, token: str, machine: str) -> None:
    """Send one file as the record it is, and note the Gateway's answer on it."""
    full = base.joinpath(*item.root.split("/"), *item.rel.split("/"))
    headers = {UPLOAD_MACHINE_HEADER: machine}
    root = _root(item.factory or "")
    try:
        if item.kind == ATTACHMENT:
            data = full.read_bytes()
            body = {"name": item.name, "contentType": mimetypes.guess_type(full.name)[0] or "application/octet-stream",
                    "contentBase64": base64.b64encode(data).decode("ascii"), "importKey": item.source}
            answer = gateway.post_json(root + "/attachments", body, timeout=120, bearer=token, base_url=url, headers=headers)
        else:
            try:
                text = full.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                item.outcome = "Refused"
                item.refusal = "the file is not UTF-8 text, and a journal entry or a working document is text"
                return
            if item.kind == JOURNAL:
                body = {"date": journal_date(item.rel.split("/"), full.stat().st_mtime), "text": text,
                        "importKey": item.source}
                answer = gateway.post_json(root + "/journal", body, bearer=token, base_url=url, headers=headers)
            else:
                body = {"name": item.name, "text": text, "expectedVersion": 0, "importKey": item.source}
                answer = gateway.put_json(root + "/documents", body, bearer=token, base_url=url, headers=headers)
        outcome = answer.get("outcome") if isinstance(answer, dict) else None
        if outcome not in ("Written", "AlreadyStored"):
            item.outcome = "Refused"
            item.refusal = f"the Gateway answered without saying it stored the file: {answer!r}"
            return
        item.outcome = outcome
    except gateway.GatewayError as exc:
        item.outcome = "Refused"
        item.refusal = str(exc)


def upload(base_dir: Optional[str], gateway_url: Optional[str], machine: Optional[str], dry_run: bool,
           json_output: bool) -> None:
    """Read every factory records folder on this machine, store each file on the Gateway, and reconcile the counts.

    Nothing local is deleted or changed. Running it again stores nothing twice: a file stored before is answered
    "already stored", and a file that changed since is refused and listed, never written over.
    """
    base = Path(base_dir) if base_dir else Path(os.environ.get("LOCALAPPDATA", ""))
    if not base_dir and not os.environ.get("LOCALAPPDATA"):
        axi_cli.fail("LOCALAPPDATA is not set, so there is no records folder to read; name one with --root.",
                     [f"{_UPLOAD} --root <folder> --dry-run"])
    if not base.is_dir():
        axi_cli.fail(f"there is no folder at {base}.", [f"{_UPLOAD} --root <folder> --dry-run"])

    token = ""
    url = ""
    machine_name = (machine or os.environ.get("COMPUTERNAME") or "").strip()
    if not dry_run:
        token = os.environ.get(UPLOAD_TOKEN_VARIABLE, "").strip()
        if not token:
            axi_cli.fail(
                f"the upload writes with the account's machine credential, read from {UPLOAD_TOKEN_VARIABLE}, and it is "
                "not set. A session's own key reaches one factory only, so it cannot carry the upload.",
                [f"cc-secrets run {UPLOAD_TOKEN_ENTRY} -- {_UPLOAD} --gateway <url>", f"{_UPLOAD} --dry-run"])
        url = (gateway_url or os.environ.get("CC_GATEWAY_URL", "")).strip()
        if not url:
            axi_cli.fail("no Gateway is named: pass --gateway <url>, or set CC_GATEWAY_URL.", [f"{_UPLOAD} --dry-run"])
        if not machine_name:
            axi_cli.fail("the upload names the machine its files came from, and this one has no COMPUTERNAME; pass --machine.",
                         [f"{_UPLOAD} --machine <name>"])

    report = plan_upload(base)
    for item in report.planned:
        if item.not_uploaded is None and not dry_run:
            _send(item, base, url, token, machine_name)

    _print_upload(report, base, url, machine_name, dry_run, json_output)


def _tally(items: Sequence[Planned]) -> Dict[str, int]:
    t = {"read": len(items), "written": 0, "alreadyStored": 0, "notUploaded": 0, "refused": 0, "toSend": 0}
    for i in items:
        if i.not_uploaded:
            t["notUploaded"] += 1
        elif i.outcome == "Written":
            t["written"] += 1
        elif i.outcome == "AlreadyStored":
            t["alreadyStored"] += 1
        elif i.outcome == "Refused":
            t["refused"] += 1
        else:
            t["toSend"] += 1
    t["stored"] = t["written"] + t["alreadyStored"]
    return t


def _print_upload(report: UploadReport, base: Path, url: str, machine: str, dry_run: bool, json_output: bool) -> None:
    items = report.planned
    total = _tally(items)
    # THE RECONCILIATION. Every file read is accounted for exactly once: stored (now or before), not uploaded by
    # design (listed), or refused (listed). A file in none of the three would be a file silently skipped.
    accounted = total["stored"] + total["notUploaded"] + total["refused"] + total["toSend"]
    reconciles = accounted == total["read"] and total["refused"] == 0 and (dry_run or total["toSend"] == 0)

    if json_output:
        print(json.dumps({
            "base": str(base), "gateway": url or None, "machine": machine or None, "dryRun": dry_run,
            "missingRoots": report.missing_roots, "totals": total, "reconciles": reconciles,
            "files": [{"source": i.source, "factory": i.factory, "kind": i.kind, "bytes": i.size, "name": i.name,
                       "notUploaded": i.not_uploaded, "outcome": i.outcome, "refusal": i.refusal} for i in items],
        }, indent=2))
        if not reconciles:
            sys.exit(1)
        return

    lines = [("DRY RUN - nothing was sent. " if dry_run else "") +
             f"Factory records under {base}" + ("" if dry_run else f", uploaded to {url} as {machine}") + "."]
    for rel_root, factory, _prefix in ROOTS:
        mine = [i for i in items if i.root == rel_root]
        if rel_root in report.missing_roots:
            lines.append(f"  {rel_root}  [{factory or 'no factory'}]  not present on this machine")
            continue
        t = _tally(mine)
        kinds = {}
        for i in mine:
            kinds[i.kind] = kinds.get(i.kind, 0) + 1
        by_kind = ", ".join(f"{n} {k}" for k, n in sorted(kinds.items())) or "no files"
        lines.append(f"  {rel_root}  [{factory or 'no factory'}]  read {t['read']} ({by_kind})")
        if dry_run:
            lines.append(f"      would send {t['toSend']}, not uploaded {t['notUploaded']}")
        else:
            lines.append(f"      stored {t['stored']} (new {t['written']}, already stored {t['alreadyStored']}), "
                         f"not uploaded {t['notUploaded']}, refused {t['refused']}")

    held = [i for i in items if i.not_uploaded]
    if held:
        lines.append(f"Not uploaded, by name ({len(held)}):")
        for i in held:
            lines.append(f"  {axi_cli.ascii_text(i.source)}  [{i.kind}, {i.size} bytes] - {i.not_uploaded}")
    refused = [i for i in items if i.outcome == "Refused"]
    if refused:
        lines.append(f"REFUSED, by name ({len(refused)}):")
        for i in refused:
            lines.append(f"  {axi_cli.ascii_text(i.source)}  [{i.kind}, {i.size} bytes] - {axi_cli.ascii_text(i.refusal or '')}")

    if dry_run:
        lines.append(f"Read {total['read']} = would send {total['toSend']} + not uploaded {total['notUploaded']}.")
    else:
        lines.append(f"Read {total['read']} = stored {total['stored']} + not uploaded {total['notUploaded']} + "
                     f"refused {total['refused']}. " + ("The counts reconcile." if reconciles else
                                                         "The counts DO NOT reconcile: see the refused files above."))
    lines.append("Nothing on this machine was deleted or changed.")
    axi_cli.write_lines(*lines)
    if not reconciles:
        sys.exit(1)

"""A factory's records on the Gateway, from the command line (Factory Definition mission, phase 2).

These run the real command line against a local HTTP server that answers like the Gateway and keeps every request,
so each test can say exactly what was SENT - the version a write states, the upload's credential, machine and import
key - and exactly what was PRINTED for each answer. A refusal is proven by the words printed and a non-zero exit,
never by the absence of an error.
"""

from __future__ import annotations

import base64
import json
import os
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src.cli import app  # noqa: E402
from src import factory_record_ops as records  # noqa: E402

runner = CliRunner()

FACTORY = "devthrottle"
LEDGER = "outreach/ledger.json"


class _Gateway:
    """A local HTTP server. Each request is answered by `respond(call)` - a function of what was sent - or by the next
    scripted (status, body), and every call is kept."""

    def __init__(self, respond):
        self.respond = respond
        self.calls = []
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                call = {
                    "method": self.command,
                    "path": self.path,
                    "auth": self.headers.get("Authorization"),
                    "machine": self.headers.get(records.UPLOAD_MACHINE_HEADER),
                    "body": json.loads(raw) if raw else None,
                }
                owner.calls.append(call)
                status, body = owner.respond(call)
                payload = json.dumps(body).encode("utf-8") if body is not None else b""
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = _answer
            do_PUT = _answer
            do_POST = _answer

            def log_message(self, *args):
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}"

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def gateway(monkeypatch):
    servers = []

    def start(*answers, respond=None):
        scripted = list(answers)
        server = _Gateway(respond or (lambda call: scripted.pop(0) if scripted else (500, {"error": "no answer scripted"})))
        servers.append(server)
        monkeypatch.setenv("CC_GATEWAY_URL", server.url)
        return server

    yield start
    for server in servers:
        server.close()


def doc(version, text=None, author="7f0c1a2b"):
    return {"factory": FACTORY, "name": LEDGER, "version": version, "text": text, "textBytes": len(text or ""),
            "authorKind": "session", "authorId": author, "writtenAtUtc": "2026-10-14T12:00:00Z"}


# ---------------------------------------------------------------------------------------------------
# Working documents: the two-writer refusal
# ---------------------------------------------------------------------------------------------------

def test_a_write_sends_the_version_the_caller_read_and_says_what_it_wrote(gateway, tmp_path):
    server = gateway((200, {"outcome": "Written", "document": doc(3)}))
    file = tmp_path / "ledger.json"
    file.write_text("row-a\nrow-b", encoding="utf-8")

    result = runner.invoke(app, ["factory", "doc", "write", "--factory", FACTORY, "--name", LEDGER,
                                 "--file", str(file), "--expected-version", "2"])

    assert result.exit_code == 0, result.output
    assert server.calls[0]["method"] == "PUT"
    assert server.calls[0]["path"] == "/factory-records/devthrottle/documents"
    assert server.calls[0]["body"] == {"name": LEDGER, "text": "row-a\nrow-b", "expectedVersion": 2}
    assert server.calls[0]["auth"] == "Bearer test-session-key"
    assert "Written: version 3 of 'outreach/ledger.json'" in result.output


def test_a_stale_write_prints_the_current_text_and_its_version_and_exits_non_zero(gateway, tmp_path):
    refusal = "'outreach/ledger.json' is at version 2, not 1; read it again, merge, and write once more"
    gateway((409, {"error": refusal, "outcome": "Stale", "current": doc(2, "row-a\nrow-b (run one)")}))
    file = tmp_path / "ledger.json"
    file.write_text("row-a\nrow-c (run two)", encoding="utf-8")

    result = runner.invoke(app, ["factory", "doc", "write", "--factory", FACTORY, "--name", LEDGER,
                                 "--file", str(file), "--expected-version", "1"])

    assert result.exit_code != 0
    assert "Not written: another writer got there first. 'outreach/ledger.json' is now at version 2" in result.output
    # The text the caller must merge into, printed in full.
    assert "row-b (run one)" in result.output
    assert refusal in result.output
    assert "--expected-version 2" in result.output


def test_get_prints_the_newest_version_and_the_version_to_write_against(gateway):
    server = gateway((200, doc(4, "the playbook")))

    result = runner.invoke(app, ["factory", "doc", "get", "--factory", FACTORY, "--name", "outreach/playbook.md"])

    assert result.exit_code == 0, result.output
    assert server.calls[0]["path"] == "/factory-records/devthrottle/documents/read?name=outreach%2Fplaybook.md"
    assert "the playbook" in result.output
    assert "--expected-version 4" in result.output


def test_an_answer_without_a_document_is_never_shown_as_an_empty_one(gateway):
    gateway((200, {"factory": FACTORY, "name": LEDGER}))

    result = runner.invoke(app, ["factory", "doc", "get", "--factory", FACTORY, "--name", LEDGER])

    assert result.exit_code != 0
    assert "carried no document text" in result.output


# ---------------------------------------------------------------------------------------------------
# Attachments and the journal
# ---------------------------------------------------------------------------------------------------

def test_an_oversized_attachment_is_refused_with_the_gateways_words_and_exits_non_zero(gateway, tmp_path):
    sentence = ("'big.png' holds 4194305 bytes; one attachment holds at most 4194304 (4 MB). Nothing was stored. Make "
                "the file smaller - a screenshot at a lower resolution, or as a JPEG - and add it again")
    server = gateway((413, {"error": sentence, "outcome": "TooLarge", "current": None}))
    file = tmp_path / "big.png"
    file.write_bytes(b"\x89PNG" + b"\0" * 16)

    result = runner.invoke(app, ["factory", "attach", "add", "--factory", FACTORY, "--file", str(file)])

    assert result.exit_code != 0
    assert "Nothing was stored" in result.output
    body = server.calls[0]["body"]
    assert body["contentType"] == "image/png"
    assert base64.b64decode(body["contentBase64"]) == b"\x89PNG" + b"\0" * 16


def test_a_fetched_attachment_is_written_only_when_its_bytes_match_the_stored_hash(gateway, tmp_path):
    import hashlib
    data = b"walk record"
    good = {"id": "a1", "name": "walk.json", "contentType": "application/json", "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "contentBase64": base64.b64encode(data).decode()}
    bad = dict(good, sha256="0" * 64)
    gateway((200, good), (200, bad))

    ok = runner.invoke(app, ["factory", "attach", "get", "--factory", FACTORY, "--id", "a1", "--out", str(tmp_path / "w.json")])
    refused = runner.invoke(app, ["factory", "attach", "get", "--factory", FACTORY, "--id", "a1", "--out", str(tmp_path / "x.json")])

    assert ok.exit_code == 0, ok.output
    assert (tmp_path / "w.json").read_bytes() == data
    assert refused.exit_code != 0
    assert not (tmp_path / "x.json").exists()


def test_a_journal_entry_names_its_seat_day_and_attachments(gateway, tmp_path):
    entry = {"id": "e1", "factory": FACTORY, "seat": "outreach", "date": "2026-10-14", "textBytes": 9,
             "attachmentIds": ["a1"], "authorKind": "session"}
    server = gateway((200, {"outcome": "Written", "entry": entry}))
    file = tmp_path / "day.md"
    file.write_text("Sent two.", encoding="utf-8")

    result = runner.invoke(app, ["factory", "journal", "add", "--factory", FACTORY, "--seat", "outreach",
                                 "--date", "2026-10-14", "--file", str(file), "--attachment", "a1"])

    assert result.exit_code == 0, result.output
    assert server.calls[0]["body"] == {"seat": "outreach", "date": "2026-10-14", "text": "Sent two.", "attachmentIds": ["a1"]}
    assert "Added: entry e1 for 2026-10-14" in result.output


def test_an_empty_journal_says_count_zero_rather_than_printing_nothing(gateway):
    gateway((200, {"factory": FACTORY, "entries": [], "total": 0}))

    result = runner.invoke(app, ["factory", "journal", "list", "--factory", FACTORY])

    assert result.exit_code == 0, result.output
    assert "count: 0" in result.output


def test_seat_and_factory_only_together_is_a_usage_error_and_sends_nothing(gateway):
    server = gateway()

    result = runner.invoke(app, ["factory", "journal", "list", "--factory", FACTORY, "--seat", "a", "--factory-only"])

    assert result.exit_code != 0
    assert server.calls == []


# ---------------------------------------------------------------------------------------------------
# The upload
# ---------------------------------------------------------------------------------------------------

def _tree(base: Path):
    """A small machine: factory folders, a folder of no factory, a database, scratch, and two files whose names are one
    once case is folded."""
    def put(rel, data):
        path = base.joinpath(*rel.split("/"))
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(data, bytes):
            path.write_bytes(data)
        else:
            path.write_text(data, encoding="utf-8")
    put("cc-factory/devthrottle/outreach/journal/2026-10-07.md", "the day")
    put("cc-factory/devthrottle/outreach/ledger.json", "[]")
    put("cc-factory/devthrottle/onboarding/journal/2026-10-05/01-home.png", b"\x89PNG")
    put("cc-factory/devthrottle/runs.log", "a log line")
    put("cc-factory/cfo/register.json", "{}")
    put("cc-factory/vercel-release/releases.log", "lock")
    put("website-factory/factory.db", b"SQLite format 3")
    put("clickfunnels-factory/transcribe/a.wav", b"RIFF")
    # Two real files in two folders of one factory whose names are one once case is folded.
    put("cc-factory-factory/CFO/register.json", "{}")


def _plan(tmp_path):
    _tree(tmp_path)
    return {i.source: i for i in records.plan_upload(tmp_path).planned}


def test_each_file_is_the_kind_the_phase_0_inventory_says_it_is(tmp_path):
    plan = _plan(tmp_path)

    assert plan["cc-factory/devthrottle/outreach/journal/2026-10-07.md"].kind == records.JOURNAL
    assert plan["cc-factory/devthrottle/runs.log"].kind == records.JOURNAL
    assert plan["cc-factory/devthrottle/outreach/ledger.json"].kind == records.DOCUMENT
    assert plan["cc-factory/devthrottle/onboarding/journal/2026-10-05/01-home.png"].kind == records.ATTACHMENT
    assert plan["website-factory/factory.db"].kind == records.DATABASE
    assert plan["clickfunnels-factory/transcribe/a.wav"].kind == records.SCRATCH
    # The CFO's folder belongs to cc-factory, and its files keep the folder's name in front.
    assert plan["cc-factory/cfo/register.json"].factory == "cc-factory"
    assert plan["cc-factory/cfo/register.json"].name == "cfo/register.json"


def test_what_is_not_uploaded_is_held_back_by_name_with_its_reason_never_skipped(tmp_path):
    plan = _plan(tmp_path)

    assert plan["cc-factory/vercel-release/releases.log"].not_uploaded == "its folder belongs to no factory"
    assert plan["website-factory/factory.db"].not_uploaded.startswith("a database")
    assert plan["clickfunnels-factory/transcribe/a.wav"].not_uploaded.startswith("scratch")
    assert "same as another file's once case is folded" in plan["cc-factory/cfo/register.json"].not_uploaded
    assert "same as another file's once case is folded" in plan["cc-factory-factory/CFO/register.json"].not_uploaded
    assert plan["cc-factory/devthrottle/outreach/ledger.json"].not_uploaded is None


def test_a_journal_files_day_comes_from_its_name_then_its_folders_then_when_it_was_last_written():
    assert records.journal_date(["journal", "2026-10-07.md"], 0) == "2026-10-07"
    assert records.journal_date(["journal", "2026-10-05", "walk", "walk.json"], 0) == "2026-10-05"
    assert records.journal_date(["runs.log"], 1760400000) == "2025-10-14"


def test_a_dry_run_reads_and_counts_everything_and_sends_nothing(gateway, tmp_path):
    server = gateway()
    _tree(tmp_path)

    result = runner.invoke(app, ["factory", "records", "upload", "--root", str(tmp_path), "--dry-run"])

    assert result.exit_code == 0, result.output
    assert server.calls == []
    assert "Read 9 = would send 4 + not uploaded 5." in result.output
    assert "website-factory/factory.db" in result.output


def test_the_upload_refuses_to_start_without_the_machine_credential_and_names_the_command(gateway, tmp_path, monkeypatch):
    server = gateway()
    _tree(tmp_path)
    monkeypatch.delenv(records.UPLOAD_TOKEN_VARIABLE, raising=False)

    result = runner.invoke(app, ["factory", "records", "upload", "--root", str(tmp_path)])

    assert result.exit_code != 0
    assert server.calls == []
    assert f"cc-secrets run {records.UPLOAD_TOKEN_ENTRY} -- cc-devthrottle factory records upload" in result.output


def _storing_gateway():
    """Answers like the Gateway: the first time an import key is sent it is Written, every later time AlreadyStored."""
    stored = set()

    def respond(call):
        key = call["body"]["importKey"]
        outcome = "AlreadyStored" if key in stored else "Written"
        stored.add(key)
        record = {"version": 1, "id": "x", "authorKind": "upload", "authorId": call["machine"]}
        return 200, {"outcome": outcome, "entry": record, "document": record, "attachment": record}

    return respond


def test_the_upload_sends_every_file_with_the_credential_machine_and_import_key_and_the_counts_reconcile(gateway, tmp_path, monkeypatch):
    server = gateway(respond=_storing_gateway())
    _tree(tmp_path)
    monkeypatch.setenv(records.UPLOAD_TOKEN_VARIABLE, "the-machine-credential")

    result = runner.invoke(app, ["factory", "records", "upload", "--root", str(tmp_path), "--machine", "SOREN_NORTH"])

    assert result.exit_code == 0, result.output
    assert "Read 9 = stored 4 + not uploaded 5 + refused 0. The counts reconcile." in result.output
    assert len(server.calls) == 4
    assert all(c["auth"] == "Bearer the-machine-credential" for c in server.calls)
    assert all(c["machine"] == "SOREN_NORTH" for c in server.calls)
    by_key = {c["body"]["importKey"]: c for c in server.calls}
    journal = by_key["cc-factory/devthrottle/outreach/journal/2026-10-07.md"]
    assert journal["path"] == "/factory-records/devthrottle/journal"
    assert journal["body"]["date"] == "2026-10-07"
    assert journal["body"]["text"] == "the day"
    ledger = by_key["cc-factory/devthrottle/outreach/ledger.json"]
    assert ledger["method"] == "PUT"
    assert ledger["body"]["name"] == "outreach/ledger.json"
    assert ledger["body"]["expectedVersion"] == 0
    picture = by_key["cc-factory/devthrottle/onboarding/journal/2026-10-05/01-home.png"]
    assert picture["path"] == "/factory-records/devthrottle/attachments"
    assert base64.b64decode(picture["body"]["contentBase64"]) == b"\x89PNG"
    assert "cc-factory/cfo/register.json" not in by_key
    # Nothing on the machine was touched.
    assert (tmp_path / "cc-factory/devthrottle/outreach/ledger.json").read_text(encoding="utf-8") == "[]"
    assert (tmp_path / "website-factory/factory.db").exists()


def test_a_second_upload_stores_nothing_new_and_still_reconciles(gateway, tmp_path, monkeypatch):
    gateway(respond=_storing_gateway())
    _tree(tmp_path)
    monkeypatch.setenv(records.UPLOAD_TOKEN_VARIABLE, "the-machine-credential")
    args = ["factory", "records", "upload", "--root", str(tmp_path), "--machine", "SOREN_NORTH"]

    first = runner.invoke(app, args)
    second = runner.invoke(app, args)

    assert first.exit_code == 0 and second.exit_code == 0, second.output
    assert "stored 4 (new 4, already stored 0)" in first.output
    assert "stored 4 (new 0, already stored 4)" in second.output
    assert "Read 9 = stored 4 + not uploaded 5 + refused 0. The counts reconcile." in second.output


def test_a_refused_file_is_listed_by_name_and_the_upload_exits_non_zero(gateway, tmp_path, monkeypatch):
    def respond(call):
        if call["body"]["importKey"].endswith("ledger.json"):
            return 409, {"error": "the upload stored it before, and the file now says something else", "outcome": "ImportConflict"}
        return 200, {"outcome": "Written", "entry": {}, "document": {}, "attachment": {}}

    gateway(respond=respond)
    _tree(tmp_path)
    monkeypatch.setenv(records.UPLOAD_TOKEN_VARIABLE, "the-machine-credential")

    result = runner.invoke(app, ["factory", "records", "upload", "--root", str(tmp_path), "--machine", "SOREN_NORTH"])

    assert result.exit_code != 0
    assert "REFUSED, by name (1):" in result.output
    assert "cc-factory/devthrottle/outreach/ledger.json" in result.output
    assert "Read 9 = stored 3 + not uploaded 5 + refused 1. The counts DO NOT reconcile" in result.output

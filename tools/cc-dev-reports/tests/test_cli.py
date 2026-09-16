"""Tests for cc-dev-reports, driven through the real command line with only the network faked.

The fake sits at the shared Gateway client's opener (`gateway._OPENER.open`), the last line before a
socket. So everything above it is the real code: the path, the body, the bearer key, the timeout the
request is actually opened with, and the parsing of a refused answer's JSON into the errors printed.
"""

import io
import json
import sys
import urllib.error
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))

from src import reports_ops  # noqa: E402
from src.cli import app  # noqa: E402
from cc_shared import gateway  # noqa: E402

SESSION_ID = "40490db1-ba99-44fc-9013-debdb708d460"
REPORT_ID = "7d3c1e8a-0000-4000-8000-000000000001"
OLDER_ID = "7d3c1e8a-0000-4000-8000-000000000002"

runner = CliRunner()


def _summary(report_id=REPORT_ID, version=1, title="Phase 2 report", status="waiting-on-you"):
    return {"id": report_id, "sessionId": SESSION_ID, "key": "c:\\reports\\r.html", "title": title,
            "status": status, "version": version, "publishedAtUtc": "2026-09-16T10:00:00Z",
            "updatedAtUtc": "2026-09-16T11:00:00Z", "sessionEnded": False, "openItems": 0}


class _Answer:
    def __init__(self, body):
        self._raw = json.dumps(body).encode("utf-8")

    def read(self):
        return self._raw

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


@pytest.fixture
def wire(monkeypatch):
    """Serve scripted answers in order and record every request that went out."""
    monkeypatch.setenv("CC_GATEWAY_URL", "http://gateway.invalid")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "test-session-key")
    monkeypatch.setenv("CC_SESSION_ID", SESSION_ID)
    calls = []
    answers = []

    def open_(req, timeout=None):
        calls.append({"method": req.get_method(), "url": req.full_url, "timeout": timeout,
                      "auth": req.get_header("Authorization"),
                      "body": json.loads(req.data.decode("utf-8")) if req.data else None})
        status, body = answers.pop(0)
        if status >= 400:
            raise urllib.error.HTTPError(req.full_url, status, "refused", {},
                                         io.BytesIO(json.dumps(body).encode("utf-8")))
        return _Answer(body)

    monkeypatch.setattr(gateway._OPENER, "open", open_)

    def serve(*scripted):
        answers.extend(scripted)
        return calls

    return serve


@pytest.fixture
def report_file(tmp_path):
    path = tmp_path / "Report.html"
    path.write_text("<html><title>Phase 2 report</title></html>", encoding="utf-8")
    return path


def test_open_success_prints_id_version_title_status_and_owner_route(wire, report_file):
    calls = wire((200, {"report": _summary(version=3), "created": False}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 0, result.output
    assert f"  id: {REPORT_ID}" in result.output
    assert "  version: 3" in result.output
    assert "  title: Phase 2 report" in result.output
    assert "  status: waiting-on-you" in result.output
    assert "Reports view" in result.output
    assert f"  route: /dev-reports/{REPORT_ID}" in result.output
    assert result.output.isascii()
    assert calls[0]["method"] == "POST"
    assert calls[0]["url"] == f"http://gateway.invalid/sessions/{SESSION_ID}/dev-reports"
    assert calls[0]["auth"] == "Bearer test-session-key"
    assert calls[0]["body"]["html"] == "<html><title>Phase 2 report</title></html>"


def test_open_key_is_the_absolute_path_lower_cased_on_windows(wire, report_file, monkeypatch):
    calls = wire((200, {"report": _summary(), "created": True}))
    monkeypatch.chdir(report_file.parent)

    runner.invoke(app, ["open", report_file.name])

    import os
    expected = str(report_file.resolve())
    expected = expected.lower() if os.name == "nt" else expected
    assert calls[0]["body"]["key"] == expected


def test_open_422_prints_every_shape_check_error_one_per_line_and_exits_1(wire, report_file):
    errors = ["The report has no header marker.", "Question 2 has no recommended option, pick one.",
              "Evidence section is empty."]
    wire((422, {"error": "The report failed the shape check.", "code": "shape_check_failed", "errors": errors}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 1
    lines = result.output.splitlines()
    for error in errors:
        assert f"  {error}" in lines
    assert "errors[3]:" in lines
    assert "code: shape_check_failed" in lines


def test_open_413_prints_the_size_error_and_exits_1(wire, report_file):
    sentence = "This report is 10485761 bytes. A dev report can be at most 10485760 bytes (10 megabytes)."
    wire((413, {"error": sentence, "code": "report_too_large", "bytes": 10485761, "limitBytes": 10485760}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 1
    assert f"error: {sentence}" in result.output
    assert "code: report_too_large" in result.output


def test_open_over_the_limit_is_refused_locally_with_the_gateway_sentence_and_nothing_sent(wire, tmp_path):
    calls = wire()
    big = tmp_path / "big.html"
    big.write_bytes(b"a" * (reports_ops.MAX_REPORT_BYTES + 1))

    result = runner.invoke(app, ["open", str(big)])

    assert result.exit_code == 1
    assert ("error: This report is 10485761 bytes. A dev report can be at most 10485760 bytes "
            "(10 megabytes).") in result.output
    assert "code: report_too_large" in result.output
    assert calls == []


def test_open_exactly_at_the_limit_is_sent(wire, tmp_path):
    calls = wire((200, {"report": _summary(), "created": True}))
    edge = tmp_path / "edge.html"
    edge.write_bytes(b"a" * reports_ops.MAX_REPORT_BYTES)

    result = runner.invoke(app, ["open", str(edge)])

    assert result.exit_code == 0, result.output
    assert len(calls) == 1


def test_open_missing_file_fails_without_sending(wire, tmp_path):
    calls = wire()
    result = runner.invoke(app, ["open", str(tmp_path / "nope.html")])
    assert result.exit_code == 1
    assert "code: file_not_found" in result.output
    assert calls == []


def test_reply_without_report_picks_the_newest_report(wire):
    reply = {"id": "r-1", "text": "Done, see version 4.", "at": "2026-09-16T12:00:00Z"}
    calls = wire((200, {"count": 2, "reports": [_summary(REPORT_ID), _summary(OLDER_ID)]}),
                 (200, {"reply": reply}))

    result = runner.invoke(app, ["reply", "Done, see version 4."])

    assert result.exit_code == 0, result.output
    assert calls[0]["method"] == "GET"
    assert calls[0]["url"] == f"http://gateway.invalid/sessions/{SESSION_ID}/dev-reports"
    assert calls[1]["url"] == f"http://gateway.invalid/sessions/{SESSION_ID}/dev-reports/{REPORT_ID}/replies"
    assert calls[1]["body"] == {"text": "Done, see version 4."}
    assert f"  report: {REPORT_ID}" in result.output


def test_reply_with_report_goes_straight_to_it(wire):
    calls = wire((200, {"reply": {"id": "r-2", "text": "x", "at": "2026-09-16T12:00:00Z"}}))

    result = runner.invoke(app, ["reply", "x", "--report", OLDER_ID])

    assert result.exit_code == 0, result.output
    assert len(calls) == 1
    assert calls[0]["url"].endswith(f"/dev-reports/{OLDER_ID}/replies")


def test_reply_when_the_session_has_no_report_errors_and_exits_1(wire):
    calls = wire((200, {"count": 0, "reports": []}))

    result = runner.invoke(app, ["reply", "hello"])

    assert result.exit_code == 1
    assert ("error: this session has no dev report yet - run cc-dev-reports open <file> first"
            in result.output)
    assert len(calls) == 1


@pytest.mark.parametrize("args", [["open", "x.html", "--bogus"], ["reply", "hi", "--nope"], ["--wat"]])
def test_an_unknown_flag_fails(wire, args):
    calls = wire()
    result = runner.invoke(app, args)
    assert result.exit_code != 0
    assert "No such option" in result.output
    assert calls == []


@pytest.mark.parametrize("missing", ["CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY", "CC_SESSION_ID"])
@pytest.mark.parametrize("args", [["open", "__FILE__"], ["reply", "hi"]])
def test_missing_environment_fails_naming_the_variable(wire, report_file, monkeypatch, missing, args):
    calls = wire()
    monkeypatch.delenv(missing)
    args = [str(report_file) if a == "__FILE__" else a for a in args]

    result = runner.invoke(app, args)

    assert result.exit_code == 1
    assert f"error: {missing} is not set" in result.output
    assert "code: missing_environment" in result.output
    assert calls == []


def test_every_request_is_opened_with_the_timeout(wire, report_file):
    reply = {"id": "r-1", "text": "t", "at": "2026-09-16T12:00:00Z"}
    calls = wire((200, {"report": _summary(), "created": True}),
                 (200, {"count": 1, "reports": [_summary()]}),
                 (200, {"reply": reply}),
                 (200, {"reply": reply}))

    runner.invoke(app, ["open", str(report_file)])
    runner.invoke(app, ["reply", "t"])
    runner.invoke(app, ["reply", "t", "--report", REPORT_ID])

    assert len(calls) == 4
    assert [c["timeout"] for c in calls] == [reports_ops.HTTP_TIMEOUT_SECONDS] * 4
    assert reports_ops.HTTP_TIMEOUT_SECONDS == 30.0


def test_json_shape_is_the_same_keys_for_success_and_failure_on_both_commands(wire, report_file):
    reply = {"id": "r-1", "text": "t", "at": "2026-09-16T12:00:00Z"}
    wire((200, {"report": _summary(version=2), "created": False}),
         (422, {"error": "failed", "code": "shape_check_failed", "errors": ["one", "two"]}),
         (200, {"reply": reply}),
         (200, {"count": 0, "reports": []}))

    outputs = [
        runner.invoke(app, ["open", str(report_file), "--json"]),
        runner.invoke(app, ["open", str(report_file), "--json"]),
        runner.invoke(app, ["reply", "t", "--report", REPORT_ID, "--json"]),
        runner.invoke(app, ["reply", "t", "--json"]),
    ]
    parsed = [json.loads(o.output) for o in outputs]

    assert [o.exit_code for o in outputs] == [0, 1, 0, 1]
    for p in parsed:
        assert tuple(p.keys()) == reports_ops.RESULT_KEYS
    assert parsed[0]["ok"] is True
    assert parsed[0]["report"]["version"] == 2
    assert parsed[0]["created"] is False
    assert parsed[0]["ownerRoute"] == f"/dev-reports/{REPORT_ID}"
    assert parsed[1]["ok"] is False
    assert parsed[1]["code"] == "shape_check_failed"
    assert parsed[1]["errors"] == ["one", "two"]
    assert parsed[2]["reply"] == reply
    assert parsed[3]["code"] == "no_report"


def test_non_ascii_from_the_gateway_is_escaped_not_printed(wire, report_file):
    wire((200, {"report": _summary(title="Rapport \u00e9t\u00e9"), "created": True}))
    result = runner.invoke(app, ["open", str(report_file)])
    assert result.exit_code == 0
    assert result.output.isascii()
    assert '  title: "Rapport \\u00e9t\\u00e9"' in result.output

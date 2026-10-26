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
    assert "  opens this address and lands in the report" in result.output
    assert f"  route: http://gateway.invalid/r/{REPORT_ID}" in result.output
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


def test_open_422_also_hands_over_the_whole_shape_not_only_the_broken_rule(wire, report_file):
    """Issue #3240. An agent that reaches a shape-check refusal guessed the format - the specimen
    invented a status word that does not exist. Naming only the rule it broke teaches the contract one
    refusal at a time, so the refusal must also say where the whole shape is."""
    wire((422, {"error": "The report failed the shape check.", "code": "shape_check_failed",
                "errors": ["The header's status \"design\" is not allowed."]}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 1
    assert "cc-devthrottle skill get dev-reports" in result.output
    assert "the whole shape, including the only three allowed status words:" in result.output
    assert result.output.isascii()


def test_a_refusal_that_is_not_the_shape_check_does_not_mention_the_shape(wire, report_file):
    """The pointer belongs to the shape check alone - a size or authentication refusal that sent an
    agent to read the markup would be sending it to fix the wrong thing."""
    wire((413, {"error": "This report is too large.", "code": "report_too_large"}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 1
    assert "skill get dev-reports" not in result.output


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


def test_open_key_over_512_characters_is_refused_locally_with_the_gateway_sentence(wire, report_file, monkeypatch):
    calls = wire()
    monkeypatch.setattr(reports_ops, "report_key", lambda _path: "c:\\" + "a" * 510)

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 1
    assert "error: The report key is 513 characters; the limit is 512." in result.output
    assert "code: key_too_long" in result.output
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


def test_every_request_is_opened_with_the_timeout(wire, report_file, monkeypatch):
    # A value no default could produce, so the assertion watches the tool's own setting reach the socket.
    monkeypatch.setattr(reports_ops, "HTTP_TIMEOUT_SECONDS", 7.25)
    reply = {"id": "r-1", "text": "t", "at": "2026-09-16T12:00:00Z"}
    calls = wire((200, {"report": _summary(), "created": True}),
                 (200, {"count": 1, "reports": [_summary()]}),
                 (200, {"reply": reply}),
                 (200, {"reply": reply}))

    runner.invoke(app, ["open", str(report_file)])
    runner.invoke(app, ["reply", "t"])
    runner.invoke(app, ["reply", "t", "--report", REPORT_ID])

    assert len(calls) == 4
    assert [c["timeout"] for c in calls] == [7.25] * 4


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
    assert parsed[0]["ownerRoute"] == f"http://gateway.invalid/r/{REPORT_ID}"
    assert parsed[1]["ok"] is False
    assert parsed[1]["code"] == "shape_check_failed"
    assert parsed[1]["errors"] == ["one", "two"]
    assert parsed[2]["reply"] == reply
    assert parsed[3]["code"] == "no_report"


# ONE ADDRESS THE OWNER CLICKS (dev reports mission, phase 3b, proof 6). The tool prints
# `<gateway>/r/<report id>` - built from the Gateway this session was launched against, with the report id
# WHOLE - in the human output and in --json, from BOTH commands. A path, a shortened id, or a base this
# tool invented instead of read would fail here.


def test_open_prints_the_whole_gateway_address_for_the_report(wire, report_file, monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "https://gw.example.test/")
    wire((200, {"report": _summary(), "created": True}))

    result = runner.invoke(app, ["open", str(report_file)])

    assert result.exit_code == 0, result.output
    assert f"  route: https://gw.example.test/r/{REPORT_ID}" in result.output
    # Not a bare path, and not a shortened identifier.
    assert "/dev-reports/" not in result.output
    assert REPORT_ID[:8] + "..." not in result.output
    assert result.output.isascii()


def test_open_json_carries_the_same_whole_address(wire, report_file, monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "https://gw.example.test")
    wire((200, {"report": _summary(), "created": True}))

    result = runner.invoke(app, ["open", str(report_file), "--json"])

    parsed = json.loads(result.output)
    assert parsed["ownerRoute"] == f"https://gw.example.test/r/{REPORT_ID}"
    assert tuple(parsed.keys()) == reports_ops.RESULT_KEYS


def test_reply_prints_the_same_whole_gateway_address(wire, monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "https://gw.example.test")
    wire((200, {"reply": {"id": "y-1", "text": "x", "at": "2026-09-16T12:00:00Z"}}))

    result = runner.invoke(app, ["reply", "x", "--report", REPORT_ID])

    assert result.exit_code == 0, result.output
    assert f"  route: https://gw.example.test/r/{REPORT_ID}" in result.output


def test_error_lines_keep_quotes_readable_and_escape_only_non_ascii(wire, report_file):
    wire((422, {"error": "failed", "code": "shape_check_failed",
                "errors": ['Question "deploy" has two recommended options.', "Caf\u00e9 marker"]}))
    result = runner.invoke(app, ["open", str(report_file)])
    lines = result.output.splitlines()
    assert '  Question "deploy" has two recommended options.' in lines
    assert r"  Caf\u00e9 marker" in lines
    assert result.output.isascii()


def test_a_multi_line_gateway_sentence_stays_on_separate_lines(wire, report_file):
    wire((401, {"error": "missing or invalid token"}))
    result = runner.invoke(app, ["open", str(report_file)])
    assert result.exit_code == 1
    assert "error: missing or invalid token" in result.output.splitlines()
    assert r"\n" not in result.output  # never an escaped newline squeezed onto one line
    assert "  A 401 here has more than one cause and this answer cannot tell them apart." in result.output.splitlines()
    assert "code: http_401" in result.output


def test_non_ascii_from_the_gateway_is_escaped_not_printed(wire, report_file):
    wire((200, {"report": _summary(title="Rapport \u00e9t\u00e9"), "created": True}))
    result = runner.invoke(app, ["open", str(report_file)])
    assert result.exit_code == 0
    assert result.output.isascii()
    assert '  title: "Rapport \\u00e9t\\u00e9"' in result.output


# --- answers: the owner's stored answers, read by any session of the account -------------------------


def _answers_view(report_id=REPORT_ID, acted=None, by=None, claimed_at=None, claimed_by=None):
    return {
        "id": report_id, "title": "Outreach run", "key": "d:\factory\report.html", "sessionId": "run-one-session",
        "version": 1, "updatedAtUtc": "2026-10-26T07:00:00Z", "answered": 1, "notAnswered": 1, "noteCount": 1,
        "notActedOn": 0 if acted else 2,
        "questions": [
            {"id": "deploy", "question": "When should we send?", "inLatestVersion": True, "answered": True,
             "itemId": "a1", "optionValue": "monday", "optionLabel": "Monday", "comment": "after the holiday",
             "answeredAtUtc": "2026-10-26T09:00:00Z", "status": "saved", "statusLabel": "Saved",
             "actedOnAtUtc": acted, "actedOnSessionId": by,
             "actionClaimedAtUtc": claimed_at, "actionClaimedSessionId": claimed_by},
            {"id": "scope", "question": "Which list?", "inLatestVersion": True, "answered": False, "itemId": None,
             "optionValue": None, "optionLabel": None, "comment": None, "answeredAtUtc": None, "status": None,
             "statusLabel": None, "actedOnAtUtc": None, "actedOnSessionId": None},
        ],
        "notes": [{"itemId": "n1", "text": "Drop the second paragraph", "quote": "the quoted words",
                   "atUtc": "2026-10-26T09:00:00Z", "status": "saved", "statusLabel": "Saved",
                   "actedOnAtUtc": acted, "actedOnSessionId": by,
                   "actionClaimedAtUtc": claimed_at, "actionClaimedSessionId": claimed_by}],
    }


def test_answers_by_report_prints_each_answer_and_each_open_question(wire):
    calls = wire((200, {"count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID])

    assert result.exit_code == 0, result.output
    assert calls[0]["method"] == "GET"
    assert calls[0]["url"] == (f"http://gateway.invalid/sessions/{SESSION_ID}/dev-report-answers?report={REPORT_ID}")
    lines = result.output.splitlines()
    assert lines[0] == "count: 1"
    assert "    deploy: When should we send?" in lines
    assert "      answer: Monday (value monday)" in lines
    assert "      comment: after the holiday" in lines
    assert "      answeredAt: 2026-10-26T09:00:00Z" in lines
    assert "      actedOn: no" in lines
    assert "    scope: Which list?" in lines
    assert "      answer: not answered yet" in lines
    assert "    n1: Drop the second paragraph" in lines
    assert "      on: the quoted words" in lines
    assert f"  cc-dev-reports answers --report {REPORT_ID} --claim" in lines
    assert f"  route: http://gateway.invalid/r/{REPORT_ID}" in lines
    assert result.output.isascii()


def test_answers_by_file_sends_the_same_key_open_publishes_under(wire, report_file):
    calls = wire((200, {"count": 2, "reports": [_answers_view(OLDER_ID), _answers_view()]}))

    result = runner.invoke(app, ["answers", str(report_file), "--json"])

    assert result.exit_code == 0, result.output
    query = calls[0]["url"].split("?", 1)[1]
    assert query == "key=" + __import__("urllib.parse").parse.quote_plus(reports_ops.report_key(str(report_file)))
    body = json.loads(result.output)
    assert body["ok"] is True and body["command"] == "answers" and body["count"] == 2
    assert [r["id"] for r in body["reports"]] == [OLDER_ID, REPORT_ID]
    assert body["reports"][1]["ownerRoute"] == f"http://gateway.invalid/r/{REPORT_ID}"
    assert body["marked"] is None


def test_answers_nothing_under_that_file_says_so(wire, report_file):
    wire((200, {"count": 0, "reports": []}))

    result = runner.invoke(app, ["answers", str(report_file)])

    assert result.exit_code == 0
    assert result.output.splitlines()[0] == "count: 0"
    assert "no dev report in this account was published from that file" in result.output


def test_answers_ack_posts_to_the_report_and_prints_what_it_marked(wire):
    calls = wire((200, {"marked": [{"itemId": "a1", "kind": "answer", "questionId": "deploy"},
                                   {"itemId": "n1", "kind": "note", "questionId": None}],
                        "count": 1, "reports": [_answers_view(acted="2026-10-26T10:00:00Z", by=SESSION_ID)]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack"])

    assert result.exit_code == 0, result.output
    assert calls[0]["method"] == "POST"
    assert calls[0]["url"] == f"http://gateway.invalid/sessions/{SESSION_ID}/dev-report-answers/{REPORT_ID}/ack"
    assert calls[0]["body"] == {}
    lines = result.output.splitlines()
    assert "marked acted on: 2" in lines
    assert f'      actedOn: 2026-10-26T10:00:00Z by session {SESSION_ID}' in lines
    assert not any("--ack" in line for line in lines)  # nothing left to mark, so no help line offering it


def test_answers_ack_named_items_sends_only_those(wire):
    calls = wire((200, {"marked": [{"itemId": "a1"}], "count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack", "--item", "a1", "--item", "a2", "--json"])

    assert result.exit_code == 0, result.output
    assert calls[0]["body"] == {"items": ["a1", "a2"]}
    assert json.loads(result.output)["marked"] == [{"itemId": "a1"}]


def test_answers_ack_that_marks_nothing_while_items_stay_unacted_fails_and_names_them(wire):
    # The run acted without claiming: the Gateway marks nothing, and both items are still open.
    wire((200, {"marked": [], "count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack", "--json"])

    assert result.exit_code == 1, result.output
    body = json.loads(result.output)
    assert body["ok"] is False and body["code"] == "not_marked"
    assert "left 2 still not acted on" in body["error"]
    assert body["errors"] == ["a1 (not claimed)", "n1 (not claimed)"]


def test_answers_ack_that_marks_nothing_names_the_session_holding_the_claim(wire):
    other = _answers_view(claimed_at="2026-10-26T10:00:00Z", claimed_by="other-session")
    wire((200, {"marked": [], "count": 1, "reports": [other]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack"])

    assert result.exit_code == 1, result.output
    lines = result.output.splitlines()
    assert "code: not_marked" in lines
    assert "  a1 (claimed by session other-session)" in lines


def test_answers_ack_that_marks_nothing_because_nothing_is_left_succeeds(wire):
    done = _answers_view(acted="2026-10-26T10:00:00Z", by=SESSION_ID)
    wire((200, {"marked": [], "count": 1, "reports": [done]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack", "--json"])

    assert result.exit_code == 0, result.output
    assert json.loads(result.output)["marked"] == []


def test_answers_ack_that_marks_some_while_another_session_holds_the_rest_succeeds(wire):
    view = _answers_view()
    view["notActedOn"] = 1
    view["questions"][0].update(actedOnAtUtc="2026-10-26T10:00:00Z", actedOnSessionId=SESSION_ID)
    view["notes"][0].update(actionClaimedAtUtc="2026-10-26T10:00:00Z", actionClaimedSessionId="other-session")
    wire((200, {"marked": [{"itemId": "a1"}], "count": 1, "reports": [view]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack"])

    assert result.exit_code == 0, result.output
    assert "marked acted on: 1" in result.output.splitlines()


def test_answers_ack_named_item_left_unacted_fails(wire):
    wire((200, {"marked": [{"itemId": "a1"}], "count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--ack", "--item", "a1", "--item", "n1", "--json"])

    assert result.exit_code == 1, result.output
    body = json.loads(result.output)
    assert body["code"] == "not_marked"
    assert body["errors"] == ["n1 (not claimed)"]


def test_answers_claim_by_report_lists_only_items_returned_as_claimed(wire):
    claimed_view = _answers_view(claimed_at="2026-10-26T10:00:00Z", claimed_by=SESSION_ID)
    calls = wire((200, {"claimed": [{"itemId": "a1", "kind": "answer", "questionId": "deploy"},
                                    {"itemId": "n1", "kind": "note", "questionId": None}],
                  "count": 1, "reports": [claimed_view]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--claim", "--json"])

    assert result.exit_code == 0, result.output
    assert calls[0]["method"] == "POST"
    assert calls[0]["url"] == f"http://gateway.invalid/sessions/{SESSION_ID}/dev-report-answers/{REPORT_ID}/claim"
    body = json.loads(result.output)
    assert body["claimed"] == [
        {"itemId": "a1", "kind": "answer", "questionId": "deploy", "reportId": REPORT_ID},
        {"itemId": "n1", "kind": "note", "questionId": None, "reportId": REPORT_ID},
    ]
    assert body["reports"][0]["questions"][0]["actionClaimedSessionId"] == SESSION_ID
    assert body["marked"] is None


def test_answers_claim_named_items_sends_only_those(wire):
    calls = wire((200, {"claimed": [{"itemId": "a1", "kind": "answer", "questionId": "deploy"}],
                    "count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--claim", "--item", "a1", "--item", "n1", "--json"])

    assert result.exit_code == 0, result.output
    assert calls[0]["body"] == {"items": ["a1", "n1"]}
    assert json.loads(result.output)["claimed"] == [
        {"itemId": "a1", "kind": "answer", "questionId": "deploy", "reportId": REPORT_ID}
    ]


def test_answers_claim_by_file_claims_every_report_with_unacted_items(wire, report_file):
    older = _answers_view(OLDER_ID)
    newest = _answers_view()
    old_claim = _answers_view(OLDER_ID, claimed_at="2026-10-26T10:00:00Z", claimed_by=SESSION_ID)
    new_claim = _answers_view(claimed_at="2026-10-26T10:00:00Z", claimed_by=SESSION_ID)
    calls = wire(
        (200, {"count": 2, "reports": [older, newest]}),
        (200, {"claimed": [{"itemId": "a1", "kind": "answer", "questionId": "deploy"}],
               "count": 1, "reports": [old_claim]}),
        (200, {"claimed": [{"itemId": "n1", "kind": "note", "questionId": None}],
               "count": 1, "reports": [new_claim]}),
    )

    result = runner.invoke(app, ["answers", str(report_file), "--claim", "--json"])

    assert result.exit_code == 0, result.output
    assert [call["method"] for call in calls] == ["GET", "POST", "POST"]
    assert calls[1]["url"].endswith(f"/{OLDER_ID}/claim")
    assert calls[2]["url"].endswith(f"/{REPORT_ID}/claim")
    body = json.loads(result.output)
    assert body["count"] == 2
    assert [claim["reportId"] for claim in body["claimed"]] == [OLDER_ID, REPORT_ID]
    assert [report["questions"][0]["actionClaimedSessionId"] for report in body["reports"]] == [SESSION_ID, SESSION_ID]


def test_answers_claim_does_not_offer_claim_for_item_owned_by_another_session(wire):
    other = _answers_view(claimed_at="2026-10-26T10:00:00Z", claimed_by="other-session")
    wire((200, {"count": 1, "reports": [other]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID])

    assert result.exit_code == 0, result.output
    assert "actionClaimed: 2026-10-26T10:00:00Z by session other-session" in result.output
    assert "--claim" not in result.output


@pytest.mark.parametrize("args", [
    ["answers"],
    ["answers", "some-file.html", "--report", REPORT_ID],
    ["answers", "some-file.html", "--ack"],
    ["answers", "some-file.html", "--item", "a1"],
    ["answers", "--report", REPORT_ID, "--item", "a1"],
    ["answers", "--report", REPORT_ID, "--claim", "--ack"],
])
def test_answers_bad_arguments_are_refused_before_any_request(wire, args):
    calls = wire()

    result = runner.invoke(app, args + ["--json"])

    assert result.exit_code == 1
    body = json.loads(result.output)
    assert body["ok"] is False and body["code"] == "bad_arguments"
    assert calls == []


def test_answers_another_accounts_report_prints_the_gateways_refusal(wire):
    wire((404, {"error": f"There is no dev report {REPORT_ID} here.", "code": "report_not_found"}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID])

    assert result.exit_code == 1
    assert f"error: There is no dev report {REPORT_ID} here." in result.output.splitlines()
    assert "code: report_not_found" in result.output.splitlines()


def test_answers_json_has_the_same_keys_as_every_other_command(wire):
    wire((200, {"count": 1, "reports": [_answers_view()]}))

    result = runner.invoke(app, ["answers", "--report", REPORT_ID, "--json"])

    assert tuple(json.loads(result.output).keys()) == reports_ops.RESULT_KEYS

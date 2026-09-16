"""What cc-dev-reports does: publish a report file to the Gateway, and reply to the owner in it.

Dev Reports mission, phase 2. The Gateway owns every ruling - the shape check, the title, the status,
the size limit, which report is newest. This module reads a file, sends it, and prints what came back.

Every command produces ONE result dictionary with the same keys whatever happened (`RESULT_KEYS`), so
`--json` keeps one shape: success fills `report` / `reply`, failure fills `error` / `code` / `errors`.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, TextIO

# Make cc_shared importable when running from source, matching the other cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import axi_output  # noqa: E402
from cc_shared import gateway  # noqa: E402

#: Seconds any one Gateway call may take. Nothing this tool does waits longer than this per request.
HTTP_TIMEOUT_SECONDS = 30.0

#: The Gateway's publish limit, in bytes of UTF-8 (Manager ruling, phase 2): exactly this many is
#: allowed, one more is refused. The local refusal uses the Gateway's own sentence, so an agent reads
#: the same words whichever side refused.
MAX_REPORT_BYTES = 10 * 1024 * 1024

EXIT_OK = 0
EXIT_ERROR = 1

RESULT_KEYS = ("ok", "command", "report", "created", "reply", "ownerRoute", "error", "code", "errors")


def too_large_sentence(byte_count: int) -> str:
    return (f"This report is {byte_count} bytes. A dev report can be at most "
            f"{MAX_REPORT_BYTES} bytes (10 megabytes).")


class CommandError(Exception):
    """A refusal to report: the sentence, a stable code, and any list of errors that came with it."""

    def __init__(self, message: str, code: str, errors: Optional[List[str]] = None):
        super().__init__(message)
        self.code = code
        self.errors = errors or []


def _result(command: str, **values: Any) -> Dict[str, Any]:
    result: Dict[str, Any] = {key: None for key in RESULT_KEYS}
    result["command"] = command
    result["errors"] = []
    result.update(values)
    return result


def owner_route(report_id: str) -> str:
    return f"/dev-reports/{report_id}"


def report_key(file_path: str) -> str:
    """The stable key for a report file: its absolute path, lower-cased on Windows (plan, phase 2).

    Windows paths are case-insensitive, so the same file typed two ways must be ONE report, not two.
    """
    key = os.path.abspath(file_path)
    if os.name == "nt":
        key = key.lower()
    return key


def _require_environment() -> str:
    """Check the three values a session is launched with, each named on its own. Returns the session id."""
    try:
        gateway.gateway_base_url()
        gateway.session_key()
    except gateway.GatewayError as exc:
        raise CommandError(str(exc), "missing_environment") from exc
    sid = gateway.session_id()
    if not sid:
        raise CommandError(
            "CC_SESSION_ID is not set, so there is no session to publish the report for. "
            "cc-dev-reports only works inside a DevThrottle session.",
            "missing_environment",
        )
    return sid


def _gateway_failure(exc: gateway.GatewayError) -> CommandError:
    """Turn a refused Gateway call into the tool's error, keeping the Gateway's own words and list."""
    body = exc.body if isinstance(exc.body, dict) else {}
    code = body.get("code") if isinstance(body.get("code"), str) else None
    raw_errors = body.get("errors")
    errors = [str(e) for e in raw_errors] if isinstance(raw_errors, list) else []
    message = str(exc)
    if exc.status == 422 and errors:
        message = f"The report failed the shape check ({len(errors)} error(s)). Nothing was published."
    return CommandError(message, code or ("gateway_error" if exc.status is None else f"http_{exc.status}"), errors)


def _read_report(file_path: str) -> str:
    path = Path(file_path)
    if not path.is_file():
        raise CommandError(f"No report file at {file_path}.", "file_not_found")
    data = path.read_bytes()
    if len(data) > MAX_REPORT_BYTES:
        raise CommandError(too_large_sentence(len(data)), "report_too_large")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise CommandError(f"{file_path} is not UTF-8 text ({exc}). Save the report as UTF-8.",
                           "not_utf8") from exc


def open_report(file_path: str) -> Dict[str, Any]:
    """Publish the file as a new version of this session's report for that file."""
    try:
        sid = _require_environment()
        html = _read_report(file_path)
        body = {"key": report_key(file_path), "html": html}
        try:
            answer = gateway.post_json(f"sessions/{gateway.path_segment(sid)}/dev-reports", body,
                                       timeout=HTTP_TIMEOUT_SECONDS)
        except gateway.GatewayError as exc:
            raise _gateway_failure(exc) from exc
        report = (answer or {}).get("report")
        if not isinstance(report, dict) or not report.get("id"):
            raise CommandError("The Gateway accepted the report but its answer carried no report id.",
                               "bad_answer")
        return _result("open", ok=True, report=report, created=bool(answer.get("created")),
                       ownerRoute=owner_route(str(report["id"])))
    except CommandError as exc:
        return _result("open", ok=False, error=str(exc), code=exc.code, errors=exc.errors)


def reply(text: str, report_id: Optional[str]) -> Dict[str, Any]:
    """Post a reply to the owner in a report: the one named, else this session's newest report."""
    try:
        sid = _require_environment()
        sid_segment = gateway.path_segment(sid)
        try:
            if not report_id:
                listing = gateway.get_json(f"sessions/{sid_segment}/dev-reports", timeout=HTTP_TIMEOUT_SECONDS)
                reports = (listing or {}).get("reports")
                if not isinstance(reports, list):
                    raise CommandError("The Gateway's report list carried no list of reports.", "bad_answer")
                if not reports:
                    raise CommandError(
                        "this session has no dev report yet - run cc-dev-reports open <file> first",
                        "no_report",
                    )
                # The Gateway orders the list newest update first; the first one is the newest.
                report_id = str(reports[0]["id"])
            answer = gateway.post_json(
                f"sessions/{sid_segment}/dev-reports/{gateway.path_segment(report_id)}/replies",
                {"text": text},
                timeout=HTTP_TIMEOUT_SECONDS,
            )
        except gateway.GatewayError as exc:
            raise _gateway_failure(exc) from exc
        posted = (answer or {}).get("reply")
        if not isinstance(posted, dict):
            raise CommandError("The Gateway accepted the reply but its answer carried no reply.", "bad_answer")
        return _result("reply", ok=True, report={"id": report_id}, reply=posted,
                       ownerRoute=owner_route(report_id))
    except CommandError as exc:
        return _result("reply", ok=False, error=str(exc), code=exc.code, errors=exc.errors)


# --- Output ---------------------------------------------------------------------------------------


def _line(key: str, value: Any) -> str:
    if isinstance(value, bool):
        value = "true" if value else "false"
    return f"  {key}: {axi_output.format_value(value)}"


def render(result: Dict[str, Any], as_json: bool, stream: TextIO) -> int:
    """Print a result and return the exit code."""
    if as_json:
        stream.write(json.dumps(result, indent=1, ensure_ascii=True) + "\n")
        return EXIT_OK if result["ok"] else EXIT_ERROR

    if not result["ok"]:
        blocks = [f"error: {axi_output.escape_ascii(result['error'] or '')}", f"code: {result['code']}"]
        if result["errors"]:
            blocks.append(f"errors[{len(result['errors'])}]:")
            blocks.extend(f"  {axi_output.escape_ascii(e)}" for e in result["errors"])
        if result["code"] == "shape_check_failed":
            blocks.append(axi_output.format_help(["cc-dev-reports open <file>"]))
        axi_output.write_blocks(stream, *blocks)
        return EXIT_ERROR

    report = result["report"]
    if result["command"] == "open":
        blocks = [
            "published:",
            _line("id", report.get("id")),
            _line("version", report.get("version")),
            _line("title", report.get("title")),
            _line("status", report.get("status")),
            _line("created", result["created"]),
            "owner:",
            "  reads it in the Reports view (arrives in phase 3)",
            _line("route", result["ownerRoute"]),
            axi_output.format_help([
                f'cc-dev-reports reply --report {report.get("id")} "<text>"',
                "cc-dev-reports open <file>",
            ]),
        ]
    else:
        posted = result["reply"]
        blocks = [
            "replied:",
            _line("report", report.get("id")),
            _line("id", posted.get("id")),
            _line("at", posted.get("at")),
            _line("route", result["ownerRoute"]),
        ]
    axi_output.write_blocks(stream, *blocks)
    return EXIT_OK

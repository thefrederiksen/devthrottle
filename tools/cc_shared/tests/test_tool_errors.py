"""Tests for cc_shared.tool_errors - the one hook every cc-* tool reports its failures through (issue #3642).

Every test that could send runs against a loopback server of its own, with CC_DIRECTOR_ROOT pointed at a
temporary folder, so nothing here ever reaches a real Gateway or the real outbox.
"""

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import pytest
import typer

TOOLS_DIR = Path(__file__).resolve().parents[2]
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from cc_shared import gateway, tool_errors  # noqa: E402


# --- A loopback Gateway --------------------------------------------------------------------------


class _Gateway:
    """Answers every POST with `status` and records what it was sent."""

    def __init__(self, status=200):
        self.status = status
        self.requests = []
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length") or 0)
                body = json.loads(self.rfile.read(length).decode("utf-8"))
                outer.requests.append({"path": self.path, "auth": self.headers.get("Authorization"), "body": body})
                self.send_response(outer.status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b"{}")

            def log_message(self, *args):
                return

        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


@pytest.fixture
def root(tmp_path, monkeypatch):
    """A throwaway storage root, no session, and a hosted Gateway that answers nothing."""
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(tmp_path))
    for name in ("CC_GATEWAY_URL", "CC_GATEWAY_SESSION_KEY", "CC_SESSION_ID"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv(tool_errors.HOSTED_GATEWAY_ENV, "http://127.0.0.1:0")
    return tmp_path


@pytest.fixture
def gw():
    server = _Gateway()
    yield server
    server.close()


def _session(monkeypatch, url, key="session-key-value"):
    monkeypatch.setenv("CC_GATEWAY_URL", url)
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", key)
    monkeypatch.setenv("CC_SESSION_ID", "3fe6711b-da67-4315-925e-105a7992bdd7")


def _app():
    """A small Typer tool with a group, a command that fails the common way, and one that crashes."""
    app = typer.Typer()
    files = typer.Typer()
    app.add_typer(files, name="files")

    @files.command("convert")
    def convert(path: str, theme: str = typer.Option("paper", "--theme")):
        try:
            raise FileNotFoundError(f"No such file: {path} (theme {theme})")
        except FileNotFoundError as e:
            print(f"Error: {e}")
            raise typer.Exit(1)

    @files.command("crash")
    def crash(prompt: str):
        raise RuntimeError(f"boom while sending '{prompt}'")

    @files.command("ok")
    def ok():
        print("fine")

    return app


def _run(monkeypatch, argv, entry, **kwargs):
    monkeypatch.setattr(sys, "argv", ["cc-test", *argv])
    tool_errors.run_tool(entry, "cc-test", **kwargs)


# --- The scrubber is the Gateway's rule ----------------------------------------------------------


@pytest.mark.parametrize("text, expected", [
    ("/Users/robert/Library/Application Support/cc-director/logs", "~/Library/Application Support/cc-director/logs"),
    ("/home/soren/.local/share/cc-director", "~/.local/share/cc-director"),
    (r"C:\Users\soren\AppData\Local\cc-director", r"~\AppData\Local\cc-director"),
])
def test_scrub_home_folder_becomes_tilde(text, expected):
    assert tool_errors.scrub_text(text) == expected


@pytest.mark.parametrize("text, secret", [
    ("Authorization: Bearer dt_live_abcdef0123456789", "dt_live_abcdef"),
    ("request failed token=abc123secretvalue&x=1", "abc123secretvalue"),
    ("password: hunter2", "hunter2"),
    ("key AKIAabcdEFGHijklMNOPqrstUVWX0123456 leaked", "AKIAabcdEFGHijklMNOPqrstUVWX0123456"),
    ("connect FAILED with Xq3-vN8_kLmP2rT9wYz4bC7dF1gH5jK0sA6eU2iO8pQ at the far end", "Xq3-vN8_kLmP2rT9wYz4bC7dF1gH5jK0sA6eU2iO8pQ"),
])
def test_scrub_credential_shaped_value_is_redacted(text, secret):
    scrubbed = tool_errors.scrub_text(text)
    assert tool_errors.REDACTED in scrubbed
    assert secret not in scrubbed


@pytest.mark.parametrize("text", [
    r"D:\ReposFred\devthrottle-dev-reports-p2-gateway\src",
    "branch feat/centralized-error-logging-3311-and-more-words",
    "fleet-3fe6711b-da67-4315-925e-105a7992bdd7 is gone",
    "abcdef0123456789abcdef0123456789abcdef01 is the commit",
    "boom\n   at CcDirector.Core.InitializeServicesAndShowTheMainWindowAsync()",
    "[MainWindow] RefreshGatewayConfigFieldsThenPaintAsync FAILED: x",
])
def test_scrub_names_ids_and_frames_are_kept(text):
    assert tool_errors.scrub_text(text) == text


@pytest.mark.parametrize("text", [
    r"open C:\\Users\\robert\\x.txt FAILED",
    r"open \\SOREN_NORTH\Users\robert\x.txt FAILED",
    r"open \\SOREN_NORTH\c$\Users\robert\x.txt FAILED",
])
def test_scrub_escaped_and_network_home_paths_lose_the_name(text):
    scrubbed = tool_errors.scrub_text(text)
    assert "robert" not in scrubbed and "SOREN_NORTH" not in scrubbed and "~" in scrubbed


def test_clean_drops_control_characters_and_caps():
    assert tool_errors.clean_text("a\u0007b\nc\td-" + " ".join(["xx"] * 50), 10) == "ab\nc\td-xx"


def test_machine_id_matches_the_director_hash():
    # Computed with .NET's SHA256 over the upper-cased name, as ErrorReportMachineId.Of does.
    assert tool_errors.machine_id("devthrottle-mac-mini") == "9913c0d0dbe519dc"
    assert tool_errors.machine_id(" DEVTHROTTLE-MAC-MINI ") == "9913c0d0dbe519dc"


# --- What is reported, and what never is ---------------------------------------------------------


def test_argument_values_never_reach_the_report(root, gw, monkeypatch):
    """The owner's rule: a value typed on the command line - a path, a prompt's words - is never sent,
    even when the failure message quotes it back."""
    _session(monkeypatch, gw.url)
    secret_path = r"D:\private\board-minutes-q3.md"
    with pytest.raises(SystemExit) as ended:
        _run(monkeypatch, ["files", "convert", secret_path, "--theme=confidential-theme"], _app(), app=_app())
    assert ended.value.code == 1
    sent = json.dumps(gw.requests)
    for leaked in ("board-minutes-q3", "private", "confidential-theme"):
        assert leaked not in sent, f"{leaked!r} reached the Gateway"
    report = gw.requests[0]["body"]["reports"][0]
    assert report["message"] == f"No such file: {tool_errors.ARGUMENT} (theme {tool_errors.ARGUMENT})"
    assert report["exception_type"] == "FileNotFoundError"


def test_a_prompt_in_an_unhandled_exception_never_reaches_the_report(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)
    prompt = "please wire the payroll export to the new bank account"
    with pytest.raises(RuntimeError):
        _run(monkeypatch, ["files", "crash", prompt], _app(), app=_app())
    sent = json.dumps(gw.requests)
    assert "payroll" not in sent and "bank account" not in sent
    report = gw.requests[0]["body"]["reports"][0]
    assert report["kind"] == "unhandled"
    assert report["exception_type"] == "RuntimeError"
    assert "   at " in report["stack"] and "in crash" in report["stack"]


def test_report_names_tool_command_and_verb(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "x.md"], _app(), app=_app())
    request = gw.requests[0]
    report = request["body"]["reports"][0]
    assert request["path"] == tool_errors.DIRECTOR_ERRORS_PATH
    assert request["auth"] == "Bearer session-key-value"
    assert report["component"] == "tool"
    assert report["source"] == "cc-test"
    assert report["surface"] == "cc-test files convert"
    assert report["action"] == "convert"
    assert report["user_visible"] is True
    assert report["session_id"] == "3fe6711b-da67-4315-925e-105a7992bdd7"
    assert len(report["machine_id"]) == 16


def test_noted_failure_is_the_message(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)

    def entry():
        tool_errors.note_failure("the Gateway refused the stop")
        raise SystemExit(1)

    with pytest.raises(SystemExit):
        _run(monkeypatch, [], entry)
    assert gw.requests[0]["body"]["reports"][0]["message"] == "the Gateway refused the stop"


def test_gateway_status_code_and_correlation_travel_with_the_report(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)

    def entry():
        try:
            raise gateway.GatewayError("refused", status=409, body={"code": "stale_version"}, correlation_id="abc123")
        except gateway.GatewayError:
            raise SystemExit(1)

    with pytest.raises(SystemExit):
        _run(monkeypatch, [], entry)
    report = gw.requests[0]["body"]["reports"][0]
    assert (report["http_status"], report["error_code"], report["correlation_id"]) == (409, "stale_version", "abc123")


def test_report_message_off_sends_only_command_and_type(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)

    def entry():
        raise ValueError("the vault value was hunter2-but-longer")

    with pytest.raises(ValueError):
        _run(monkeypatch, [], entry, report_message=False)
    report = gw.requests[0]["body"]["reports"][0]
    assert "hunter2" not in json.dumps(gw.requests)
    assert report["message"] == "cc-test failed with ValueError"


# --- A user mistake is not a defect --------------------------------------------------------------


@pytest.mark.parametrize("argv", [["files", "convert"], ["files", "convert", "x", "--bogus"], ["nope"]])
def test_usage_errors_are_not_reported(root, gw, monkeypatch, argv):
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit) as ended:
        _run(monkeypatch, argv, _app(), app=_app())
    assert ended.value.code == 2
    assert gw.requests == []


def test_exit_two_from_an_argparse_tool_is_not_reported(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["start"], lambda: 2, command_names=["start"])
    assert gw.requests == []


def test_keyboard_interrupt_is_not_reported(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)

    def entry():
        raise KeyboardInterrupt

    with pytest.raises(KeyboardInterrupt):
        _run(monkeypatch, [], entry)
    assert gw.requests == []


def test_success_sends_nothing(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit) as ended:
        _run(monkeypatch, ["files", "ok"], _app(), app=_app())
    assert ended.value.code == 0
    assert gw.requests == []


# --- The tool ends exactly as it would have ------------------------------------------------------


def test_exit_code_and_printed_error_are_unchanged(root, monkeypatch, capsys):
    """Nobody listening: the same code leaves, the same text is printed, and nothing is added."""
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:0")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "k")
    with pytest.raises(SystemExit) as ended:
        _run(monkeypatch, ["files", "convert", "x.md"], _app(), app=_app())
    out = capsys.readouterr()
    assert ended.value.code == 1
    assert out.out == "Error: No such file: x.md (theme paper)\n"
    assert out.err == ""


def test_the_same_exception_object_is_re_raised(root, monkeypatch):
    marker = RuntimeError("the original")

    def entry():
        raise marker

    with pytest.raises(RuntimeError) as raised:
        _run(monkeypatch, [], entry)
    assert raised.value is marker


def test_returned_exit_code_leaves_and_is_reported(root, gw, monkeypatch):
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit) as ended:
        _run(monkeypatch, ["wait"], lambda: 1, command_names=["start", "wait"])
    assert ended.value.code == 1
    report = gw.requests[0]["body"]["reports"][0]
    assert report["surface"] == "cc-test wait" and report["message"] == "cc-test wait failed (exit code 1)"


# --- Kept and counted, never dropped -------------------------------------------------------------


def test_unreachable_gateway_keeps_the_report(root, monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:0")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "k")
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "x.md"], _app(), app=_app())
    assert tool_errors.default_outbox().count == 1
    log = (root / "logs" / "tool-error-reports.log").read_text(encoding="utf-8")
    assert "1 report(s) kept" in log


def test_kept_reports_go_with_the_next_failure_and_leave_when_accepted(root, gw, monkeypatch):
    monkeypatch.setenv("CC_GATEWAY_URL", "http://127.0.0.1:0")
    monkeypatch.setenv("CC_GATEWAY_SESSION_KEY", "k")
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "a.md"], _app(), app=_app())
    _session(monkeypatch, gw.url)
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "b.md"], _app(), app=_app())
    assert len(gw.requests[0]["body"]["reports"]) == 2
    assert tool_errors.default_outbox().count == 0


def test_refused_send_keeps_the_report(root, monkeypatch):
    server = _Gateway(status=503)
    try:
        _session(monkeypatch, server.url)
        with pytest.raises(SystemExit):
            _run(monkeypatch, ["files", "convert", "x.md"], _app(), app=_app())
        assert len(server.requests) == 1
        assert tool_errors.default_outbox().count == 1
    finally:
        server.close()


def test_rate_limit_pauses_and_keeps(root, monkeypatch):
    server = _Gateway(status=429)
    try:
        _session(monkeypatch, server.url)
        for name in ("a.md", "b.md"):
            with pytest.raises(SystemExit):
                _run(monkeypatch, ["files", "convert", name], _app(), app=_app())
        assert len(server.requests) == 1, "the second failure must not send while paused"
        assert tool_errors.default_outbox().count == 2
    finally:
        server.close()


def test_full_outbox_counts_what_it_could_not_keep(root, gw, monkeypatch):
    box = tool_errors.default_outbox()
    template = {"component": "tool", "source": "cc-test", "kind": "exit", "message": "m", "repeat_count": 1}
    for _ in range(tool_errors.MAX_KEPT + 3):
        box.keep(dict(template))
    assert box.count == tool_errors.MAX_KEPT
    assert len(box.not_kept_markers()) == 3
    _session(monkeypatch, gw.url)
    sent = tool_errors.flush(box, tool_errors.resolve_credential())
    batch = gw.requests[0]["body"]["reports"]
    assert len(batch) == tool_errors.MAX_REPORTS_PER_BATCH
    assert batch[-1]["kind"] == "outbox-full" and batch[-1]["repeat_count"] == 3
    assert sent == tool_errors.MAX_REPORTS_PER_BATCH
    assert box.not_kept_markers() == []


# --- The credential, in the owner's order --------------------------------------------------------


def test_outside_a_session_the_machine_credential_is_used(root, gw, monkeypatch):
    config = root / "config" / "config.json"
    config.parent.mkdir(parents=True)
    config.write_text(json.dumps({"gateway": {"url": gw.url, "token": "machine-token"}}), encoding="utf-8")
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "x.md"], _app(), app=_app())
    assert gw.requests[0]["auth"] == "Bearer machine-token"
    assert gw.requests[0]["path"] == tool_errors.DIRECTOR_ERRORS_PATH


def test_inside_a_director_folder_the_machine_root_is_used(tmp_path, monkeypatch):
    home = tmp_path / "instances" / "default"
    home.mkdir(parents=True)
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(home))
    assert tool_errors.machine_root() == tmp_path


def test_before_sign_in_the_install_report_route_is_used(root, gw, monkeypatch):
    monkeypatch.setenv(tool_errors.HOSTED_GATEWAY_ENV, gw.url)
    with pytest.raises(SystemExit):
        _run(monkeypatch, ["files", "convert", "secret-plan.md"], _app(), app=_app())
    request = gw.requests[0]
    assert request["path"] == tool_errors.INSTALL_REPORTS_PATH
    assert request["auth"] is None
    body = request["body"]
    assert body["component"] == "tool" and body["step"] == tool_errors.STEP_BEFORE_SIGN_IN
    assert body["install_id"] == (root / "install-id").read_text(encoding="utf-8")
    assert "secret-plan" not in json.dumps(body)
    assert tool_errors.default_outbox().count == 0


def test_before_sign_in_sends_at_most_three_an_hour(root, gw, monkeypatch):
    monkeypatch.setenv(tool_errors.HOSTED_GATEWAY_ENV, gw.url)
    for i in range(5):
        with pytest.raises(SystemExit):
            _run(monkeypatch, ["files", "convert", f"f{i}.md"], _app(), app=_app())
    assert len(gw.requests) == tool_errors.MAX_BEFORE_SIGN_IN_PER_HOUR
    assert tool_errors.default_outbox().count == 2


def test_gateway_error_carries_the_correlation_header():
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(404)
            self.send_header("Content-Type", "application/json")
            self.send_header("X-Correlation-Id", "c0ffee")
            self.end_headers()
            self.wfile.write(b'{"error": "no such thing"}')

        def log_message(self, *args):
            return

    server = HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with pytest.raises(gateway.GatewayError) as raised:
            gateway.get_json("x", bearer="k", base_url=f"http://127.0.0.1:{server.server_address[1]}")
        assert raised.value.correlation_id == "c0ffee"
        assert raised.value.status == 404
    finally:
        server.shutdown()
        server.server_close()

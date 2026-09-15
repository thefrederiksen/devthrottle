"""The leak test from issue #2889, in process: a known secret is used through every command, then its
output, the audit log and the tool log are searched for it - by a search that first proves it finds a
planted copy. Crashes are covered by test_crash.py (a fresh process); the real browser, the
browser-harness output, snapshots, screenshots, the Director log and the session transcript are covered
by tests/live_leak_check.py, which needs a running Director."""

import json
import sys

import pytest
from typer.testing import CliRunner

from conftest import add_entry, new_secret
from leak_search import BrokenInstrumentError, SecretSearch
from src import browser_login, cli, paths
from src.redact import variants_for
from test_login import FakeTab, page

PY = sys.executable
RUN_MARKER = "leaktest-" + new_secret()


def test_Search_FindsAPlantedSecret_InEveryForm(tmp_path):
    secret = new_secret()
    search = SecretSearch([(secret, "leak-user")])
    clean = tmp_path / "clean.log"
    clean.write_text(f"{RUN_MARKER} nothing to see\n", encoding="utf-8")

    for form in variants_for(secret, "leak-user"):
        leaky = tmp_path / "leaky.log"
        # Planted as plain text: writing it as JSON would escape the backslashes in forms like Python's
        # repr of UTF-32 bytes, so the file would no longer hold the form the search looks for.
        leaky.write_text(f"{RUN_MARKER} before {form} after\n", encoding="utf-8")
        assert search.search_file(leaky, RUN_MARKER, tmp_path / "work"), form
    assert search.search_file(clean, RUN_MARKER, tmp_path / "work") == []


def test_Search_MissingEmptyOrUnrelatedFile_IsABrokenInstrument(tmp_path):
    search = SecretSearch([(new_secret(), "")])
    empty = tmp_path / "empty.log"
    empty.write_bytes(b"")
    unrelated = tmp_path / "unrelated.log"
    unrelated.write_text("another run entirely", encoding="utf-8")

    for path in (tmp_path / "missing.log", empty, unrelated):
        with pytest.raises(BrokenInstrumentError):
            search.search_file(path, RUN_MARKER, tmp_path / "work")


def test_EveryCommand_LeaksTheSecretNowhere(store, tmp_path, monkeypatch):
    secret = add_entry(store, name="leak", username="leak-user", domains=("https://127.0.0.1",))
    kept = add_entry(store, name="kept", agents=False)
    monkeypatch.setenv("CC_SESSION_ID", RUN_MARKER)
    monkeypatch.setattr(browser_login, "POLL_SECONDS", 0.01)
    runner = CliRunner()
    transcript = []

    def invoke(args, **kwargs):
        result = runner.invoke(cli.app, args, **kwargs)
        transcript.append(f"$ cc-secrets {' '.join(args)}\n{result.output}{result.stderr}\nexit {result.exit_code}\n")
        return result

    echo_plain = "import sys; s=sys.stdin.readline().strip(); print(s); sys.stderr.write(s)"
    echo_encoded = ("import sys,base64,urllib.parse,json; s=sys.stdin.readline().strip();"
                    "print(base64.b64encode(s.encode()).decode()); print(urllib.parse.quote(s)); print(json.dumps(s));"
                    "sys.stdout.flush(); sys.stdout.buffer.write(s.encode('utf-16-le'))")
    echo_env = "import os; print(os.environ['CC_SECRET'])"
    echo_askpass = ("import os,subprocess,sys; h=os.environ['SUDO_ASKPASS'];"
                    "print(subprocess.run(['cmd','/c',h] if sys.platform=='win32' else [h],capture_output=True,text=True).stdout)")

    invoke(["list"])
    invoke(["list", "--json"])
    invoke(["run", "leak", "--", PY, "-c", echo_plain])
    invoke(["run", "leak", "--json", "--", PY, "-c", echo_encoded])
    invoke(["run", "leak", "--via", "env", "--", PY, "-c", echo_env])
    invoke(["run", "leak", "--via", "askpass", "--", PY, "-c", echo_askpass])
    invoke(["run", "leak", "--", PY, "-c", "import sys; raise SystemExit('boom ' + sys.stdin.readline().strip())"])
    invoke(["run", "kept", "--", PY, "-c", echo_plain])

    login_target = [{"url": "https://127.0.0.1/login", "webSocketDebuggerUrl": "ws://login"}]
    monkeypatch.setattr(cli, "_browser_port", lambda name: 9310)
    monkeypatch.setattr(browser_login, "list_page_targets", lambda port: login_target)
    monkeypatch.setattr(browser_login, "CdpConnection", lambda url: type("C", (), {"close": lambda self: None})())
    tabs = iter([
        FakeTab([page(password=True, username=True), page(url="https://127.0.0.1/welcome")]),  # logs in
        FakeTab([page(url="https://evil.test/login", password=True, username=True)]),          # refused: tab
        FakeTab([page(password=True, username=True, target="http://127.0.0.1:9/steal")]),     # refused: form
        FakeTab([page(password=True, username=True)]),                                         # rejected
        FakeTab([page(password=True, username=True)], fail_fill="the page echoed {value}"),    # hostile echo
    ])
    monkeypatch.setattr(browser_login, "_Tab", lambda conn, entry: next(tabs))
    for _ in range(5):
        invoke(["login", "leak", "--browser", "agent-browser", "--timeout", "0.3"])
    invoke(["log"])
    invoke(["log", "--json"])

    output_file = tmp_path / "command-output.txt"
    output_file.write_text("\n".join(transcript), encoding="utf-8")
    log_files = sorted((paths.secrets_home() / "logs").glob("cc-secrets-*.log"))
    assert log_files, "the tool log was never written"
    search = SecretSearch([(secret, "leak-user"), (kept, "leak-user")])

    searched, hits = search.search_files(
        [(output_file, "exited 0"), (paths.audit_path(), RUN_MARKER)] + [(f, "[login] done") for f in log_files],
        tmp_path / "work")

    text = output_file.read_text(encoding="utf-8")
    assert searched == 2 + len(log_files)
    assert hits == []
    assert text.count("[REDACTED]") >= 6
    assert "logged in" in text
    assert "evil.test" in text and "sends to" in text

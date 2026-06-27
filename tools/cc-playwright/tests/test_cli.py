"""Regression tests for cc-playwright bug fixes.

These tests never launch a real browser or touch the network. Browser/CDP and
filesystem boundaries are mocked. Each test maps to a fixed shipped-tool review
finding.
"""

import json
import sys
import types
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent / "src"))

import cli  # noqa: E402


# --------------------------------------------------------------------------
# Issue 1: browser discovery (env override, --browser-path, which, paths)
# --------------------------------------------------------------------------

def test_find_browser_uses_browser_path_override(tmp_path):
    exe = tmp_path / "my-brave.exe"
    exe.write_text("x", encoding="utf-8")
    assert cli._find_browser(str(exe)) == str(exe)


def test_find_browser_bad_override_raises(tmp_path):
    missing = tmp_path / "nope.exe"
    with pytest.raises(SystemExit) as ex:
        cli._find_browser(str(missing))
    assert "--browser-path" in str(ex.value)


def test_find_browser_uses_env_var(tmp_path, monkeypatch):
    exe = tmp_path / "env-chrome"
    exe.write_text("x", encoding="utf-8")
    monkeypatch.setenv(cli.BROWSER_ENV_VAR, str(exe))
    assert cli._find_browser(None) == str(exe)


def test_find_browser_bad_env_var_raises(tmp_path, monkeypatch):
    monkeypatch.setenv(cli.BROWSER_ENV_VAR, str(tmp_path / "ghost"))
    with pytest.raises(SystemExit) as ex:
        cli._find_browser(None)
    assert cli.BROWSER_ENV_VAR in str(ex.value)


def test_find_browser_falls_back_to_which(monkeypatch):
    monkeypatch.delenv(cli.BROWSER_ENV_VAR, raising=False)
    monkeypatch.setattr(cli.shutil, "which",
                        lambda name: "/usr/bin/brave" if name == "brave" else None)
    assert cli._find_browser(None) == "/usr/bin/brave"


def test_find_browser_not_found_names_overrides(monkeypatch):
    monkeypatch.delenv(cli.BROWSER_ENV_VAR, raising=False)
    monkeypatch.setattr(cli.shutil, "which", lambda name: None)
    monkeypatch.setattr(cli, "_candidate_browser_paths", lambda: [])
    with pytest.raises(SystemExit) as ex:
        cli._find_browser(None)
    msg = str(ex.value)
    assert "--browser-path" in msg
    assert cli.BROWSER_ENV_VAR in msg


# --------------------------------------------------------------------------
# Issue 2: snapshot --full produces a meaningfully different output
# --------------------------------------------------------------------------

class _FakePage:
    """Minimal stand-in: returns different payloads for the two snapshot JS
    blocks so we can prove --full adds data."""

    url = "https://example.com/"

    def title(self):
        return "Example"

    def evaluate(self, js):
        if "headings" in js:
            return {
                "headings": [{"tag": "H1", "text": "Hello"}],
                "landmarks": [{"tag": "NAV", "role": "", "aria": ""}],
                "meta_description": "a description",
                "text": "lots of visible body text",
                "text_length": 25,
            }
        return [{"tag": "BUTTON", "text": "Go"}]


def _patch_connect(monkeypatch, page):
    monkeypatch.setattr(cli, "_connect",
                        lambda conn: (types.SimpleNamespace(stop=lambda: None),
                                      None, None, page))


def test_snapshot_full_differs_from_default(monkeypatch, capsys):
    page = _FakePage()
    _patch_connect(monkeypatch, page)

    cli.cmd_snapshot(types.SimpleNamespace(connection="default", full=False))
    plain = json.loads(capsys.readouterr().out)

    cli.cmd_snapshot(types.SimpleNamespace(connection="default", full=True))
    full = json.loads(capsys.readouterr().out)

    assert plain["full"] is False
    assert "headings" not in plain
    assert full["full"] is True
    assert full["headings"] == [{"tag": "H1", "text": "Hello"}]
    assert full["text"] == "lots of visible body text"
    # The fuller output must contain keys the default does not.
    assert set(full) - set(plain)


# --------------------------------------------------------------------------
# Issue 3: stop tries graceful close first, forces only if needed
# --------------------------------------------------------------------------

def test_terminate_process_graceful(monkeypatch):
    calls = []
    monkeypatch.setattr(cli.subprocess, "run",
                        lambda *a, **k: calls.append(a[0]))
    # Alive at first check, then exits during the grace wait.
    states = iter([True, False])
    monkeypatch.setattr(cli, "_is_running", lambda pid: next(states, False))
    monkeypatch.setattr(cli.time, "sleep", lambda s: None)

    method = cli._terminate_process(123, grace_seconds=1.0)
    assert method == "graceful"
    # No forceful taskkill /F was issued.
    assert all("/F" not in c for c in calls)


def test_terminate_process_forces_when_stuck(monkeypatch):
    calls = []
    monkeypatch.setattr(cli.subprocess, "run",
                        lambda *a, **k: calls.append(list(a[0])))
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)  # never exits
    monkeypatch.setattr(cli.time, "sleep", lambda s: None)
    monkeypatch.setattr(cli, "_wait_for_exit", lambda pid, t: False)

    method = cli._terminate_process(999, grace_seconds=0.1)
    assert method == "forced"
    flat = [tok for c in calls for tok in c]
    assert "/F" in flat  # forced kill happened
    assert "/T" not in flat  # but not a tree-kill


def test_cmd_stop_reports_method(monkeypatch, capsys):
    monkeypatch.setattr(cli, "_load_state",
                        lambda conn: {"pid": 42, "port": 9333, "profile_dir": "C:/p"})
    monkeypatch.setattr(cli, "_state_browser_alive", lambda state: True)
    monkeypatch.setattr(cli, "_clear_state", lambda conn: None)
    monkeypatch.setattr(cli, "_terminate_process", lambda pid: "graceful")

    cli.cmd_stop(types.SimpleNamespace(connection="work"))
    out = json.loads(capsys.readouterr().out)
    assert out["status"] == "stopped"
    assert out["method"] == "graceful"


# --------------------------------------------------------------------------
# Issue 4: Windows profile-lock pre-check detects a busy user-data-dir
# --------------------------------------------------------------------------

def test_dir_locked_detects_running_browser(tmp_path, monkeypatch):
    profile = tmp_path / "linkedin"
    profile.mkdir()
    cmdline = f'brave.exe --user-data-dir={profile} --remote-debugging-port=9333'
    monkeypatch.setattr(cli, "_browser_command_lines", lambda: [cmdline])
    assert cli._dir_locked_by_running_browser(profile) is True


def test_dir_locked_ignores_other_dirs(tmp_path, monkeypatch):
    profile = tmp_path / "linkedin"
    profile.mkdir()
    other = f'brave.exe --user-data-dir={tmp_path / "other"} --foo'
    monkeypatch.setattr(cli, "_browser_command_lines", lambda: [other])
    assert cli._dir_locked_by_running_browser(profile) is False


def test_dir_locked_enumeration_failure_not_fatal(tmp_path, monkeypatch):
    profile = tmp_path / "p"
    profile.mkdir()

    def boom():
        raise OSError("cannot enumerate")

    monkeypatch.setattr(cli, "_browser_command_lines", boom)
    # Probe failure must not raise; it just cannot detect a lock.
    assert cli._dir_locked_by_running_browser(profile) is False


# --------------------------------------------------------------------------
# Issue 5: corrupt state file is moved aside with a warning, not silently empty
# --------------------------------------------------------------------------

def test_load_state_corrupt_moves_aside_and_warns(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(cli, "CONNECTIONS_STATE_DIR", tmp_path)
    monkeypatch.setattr(cli, "_migrate_legacy_state_if_needed", lambda: None)
    bad = tmp_path / "work.json"
    bad.write_text("{ this is not json", encoding="utf-8")

    result = cli._load_state("work")

    assert result == {}
    assert not bad.exists()
    assert (tmp_path / "work.json.corrupt").exists()
    err = capsys.readouterr().err
    assert "WARNING" in err
    assert "corrupt" in err
    assert err.isascii()


# --------------------------------------------------------------------------
# Issue 6: screenshot defaults to the configured screenshots location
# --------------------------------------------------------------------------

def test_configured_screenshots_dir_reads_config(tmp_path, monkeypatch):
    config = tmp_path / "config" / "config.json"
    config.parent.mkdir(parents=True)
    config.write_text(
        json.dumps({"screenshots": {"source_directory": "D:/Shots"}}),
        encoding="utf-8",
    )
    monkeypatch.setattr(cli, "_cc_director_config_path", lambda: config)
    assert cli._configured_screenshots_dir() == Path("D:/Shots")


def test_configured_screenshots_dir_missing_config(tmp_path, monkeypatch):
    monkeypatch.setattr(cli, "_cc_director_config_path",
                        lambda: tmp_path / "nope.json")
    assert cli._configured_screenshots_dir() is None


def test_screenshot_defaults_to_configured_dir(tmp_path, monkeypatch, capsys):
    shots = tmp_path / "Pictures" / "Screenshots"
    captured = {}

    class _ShotPage:
        url = "https://x/"

        def screenshot(self, path, full_page):
            captured["path"] = path
            Path(path).parent.mkdir(parents=True, exist_ok=True)
            Path(path).write_bytes(b"png")

    _patch_connect(monkeypatch, _ShotPage())
    monkeypatch.setattr(cli, "_configured_screenshots_dir", lambda: shots)

    cli.cmd_screenshot(types.SimpleNamespace(
        connection="default", output=None, full_page=False))
    out = json.loads(capsys.readouterr().out)
    assert Path(out["screenshot"]).parent == shots


def test_screenshot_explicit_output_wins(tmp_path, monkeypatch, capsys):
    target = tmp_path / "explicit.png"

    class _ShotPage:
        url = "https://x/"

        def screenshot(self, path, full_page):
            Path(path).write_bytes(b"png")

    _patch_connect(monkeypatch, _ShotPage())
    monkeypatch.setattr(cli, "_configured_screenshots_dir",
                        lambda: tmp_path / "ignored")

    cli.cmd_screenshot(types.SimpleNamespace(
        connection="default", output=str(target), full_page=False))
    out = json.loads(capsys.readouterr().out)
    assert Path(out["screenshot"]) == target


# --------------------------------------------------------------------------
# Issue 8: action command errors route through _err()'s JSON shape
# --------------------------------------------------------------------------

def test_main_routes_exception_through_err(monkeypatch, capsys):
    def boom(args):
        raise RuntimeError("simulated playwright timeout")

    # Build a parser run that dispatches to a failing command.
    monkeypatch.setattr(sys, "argv", ["cc-playwright", "info"])
    monkeypatch.setattr(cli, "cmd_info", boom)

    with pytest.raises(SystemExit) as ex:
        cli.main()
    assert ex.value.code == 1
    err = capsys.readouterr().err
    payload = json.loads(err)
    assert "error" in payload
    assert "RuntimeError" in payload["error"]
    assert err.isascii()


# --------------------------------------------------------------------------
# Review2 M5: a plain-text SystemExit is reshaped into the {"error"} JSON
# contract, while an _err()-issued exit is passed through (not double-wrapped).
# --------------------------------------------------------------------------

def test_main_reshapes_plaintext_systemexit_to_json(monkeypatch, capsys):
    # A former bare-text failure (e.g. _find_browser / _connect / _locate).
    def plain_text_exit(args):
        raise SystemExit("Need --selector, --text, or --role")

    monkeypatch.setattr(sys, "argv", ["cc-playwright", "click"])
    monkeypatch.setattr(cli, "cmd_click", plain_text_exit)

    with pytest.raises(SystemExit) as ex:
        cli.main()
    assert ex.value.code == 1
    err = capsys.readouterr().err
    payload = json.loads(err)
    assert payload["error"] == "Need --selector, --text, or --role"
    assert err.isascii()


def test_main_err_path_not_double_wrapped(monkeypatch, capsys):
    # _err() prints JSON and exits with an integer code; main() must let that
    # through unchanged and not emit a second JSON line.
    def use_err(args):
        cli._err("structured failure")

    monkeypatch.setattr(sys, "argv", ["cc-playwright", "info"])
    monkeypatch.setattr(cli, "cmd_info", use_err)

    with pytest.raises(SystemExit) as ex:
        cli.main()
    assert ex.value.code == 1
    err = capsys.readouterr().err.strip()
    # Exactly one JSON object on stderr, not wrapped twice.
    assert err.count("\n") == 0
    payload = json.loads(err)
    assert payload["error"] == "structured failure"


# --------------------------------------------------------------------------
# Review2 M6: PID-reuse safety - a recorded PID is trusted only after an
# identity check (debug port or profile_dir), never on bare existence.
# --------------------------------------------------------------------------

def test_state_browser_alive_false_when_pid_dead(monkeypatch):
    monkeypatch.setattr(cli, "_is_running", lambda pid: False)
    assert cli._state_browser_alive({"pid": 1, "port": 9333}) is False


def test_state_browser_alive_true_when_debug_port_responds(monkeypatch):
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)
    monkeypatch.setattr(cli, "_debug_port_responds", lambda port: True)
    state = {"pid": 1234, "port": 9333, "profile_dir": "C:/p"}
    assert cli._state_browser_alive(state) is True


def test_state_browser_alive_true_when_profile_dir_locked(monkeypatch):
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)
    monkeypatch.setattr(cli, "_debug_port_responds", lambda port: False)
    monkeypatch.setattr(cli, "_dir_locked_by_running_browser", lambda d: True)
    state = {"pid": 1234, "port": 9333, "profile_dir": "C:/p"}
    assert cli._state_browser_alive(state) is True


def test_state_browser_alive_false_on_pid_reuse(monkeypatch):
    # PID alive, but neither identity signal confirms our browser -> the OS
    # reused the PID for an unrelated process.
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)
    monkeypatch.setattr(cli, "_debug_port_responds", lambda port: False)
    monkeypatch.setattr(cli, "_dir_locked_by_running_browser", lambda d: False)
    state = {"pid": 1234, "port": 9333, "profile_dir": "C:/p"}
    assert cli._state_browser_alive(state) is False


def test_cmd_stop_pid_reuse_does_not_kill(monkeypatch, capsys):
    monkeypatch.setattr(
        cli, "_load_state",
        lambda conn: {"pid": 42, "port": 9333, "profile_dir": "C:/p"},
    )
    monkeypatch.setattr(cli, "_state_browser_alive", lambda state: False)
    cleared = {}
    monkeypatch.setattr(cli, "_clear_state",
                        lambda conn: cleared.setdefault("conn", conn))

    def must_not_terminate(pid):
        raise AssertionError("must not terminate an unidentified PID")

    monkeypatch.setattr(cli, "_terminate_process", must_not_terminate)

    cli.cmd_stop(types.SimpleNamespace(connection="work"))
    out = json.loads(capsys.readouterr().out)
    assert out["status"] == "not_running"
    assert cleared["conn"] == "work"


def test_cmd_start_pid_reuse_does_not_report_already_running(monkeypatch, capsys):
    # PID recorded and alive, but identity check fails -> start must NOT short
    # circuit on already_running. We stop it just past that branch to avoid
    # launching a real browser.
    monkeypatch.setattr(
        cli, "_load_state",
        lambda conn: {"pid": 42, "port": 9333, "profile_dir": "C:/p"},
    )
    monkeypatch.setattr(cli, "_state_browser_alive", lambda state: False)

    sentinel = RuntimeError("reached launch path")

    def stop_here(connection, override):
        raise sentinel

    monkeypatch.setattr(cli, "_resolve_profile_dir", stop_here)

    with pytest.raises(RuntimeError) as ex:
        cli.cmd_start(types.SimpleNamespace(
            connection="work", profile=None, port=None, url=None,
            browser_path=None))
    assert ex.value is sentinel


# --------------------------------------------------------------------------
# Review2 L6/L7: messaging and the Windows lock pre-check no longer hardcode
# "Brave" and the CIM filter now includes chromium.exe.
# --------------------------------------------------------------------------

def test_browser_command_lines_filter_includes_chromium(monkeypatch):
    captured = {}

    class _Result:
        stdout = ""

    def fake_run(cmd, **kwargs):
        captured["cmd"] = cmd
        return _Result()

    monkeypatch.setattr(cli.sys, "platform", "win32")
    monkeypatch.setattr(cli.subprocess, "run", fake_run)

    cli._browser_command_lines()
    joined = " ".join(captured["cmd"])
    assert "chromium.exe" in joined
    # The previously-listed images are still covered.
    for name in ("brave.exe", "chrome.exe", "msedge.exe"):
        assert name in joined


def test_connect_error_message_does_not_say_brave(monkeypatch):
    # State says running so we reach the connect attempt, which fails.
    monkeypatch.setattr(cli, "_load_state",
                        lambda conn: {"pid": 1, "port": 9999})
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)

    class _Chromium:
        def connect_over_cdp(self, url):
            raise RuntimeError("no cdp here")

    class _PW:
        chromium = _Chromium()

        def stop(self):
            pass

    sync_mod = types.ModuleType("playwright.sync_api")
    sync_mod.sync_playwright = lambda: types.SimpleNamespace(start=lambda: _PW())
    pkg = types.ModuleType("playwright")
    monkeypatch.setitem(sys.modules, "playwright", pkg)
    monkeypatch.setitem(sys.modules, "playwright.sync_api", sync_mod)

    with pytest.raises(SystemExit) as ex:
        cli._connect("work")
    msg = str(ex.value)
    assert "Brave" not in msg
    assert "9999" in msg

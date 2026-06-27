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
    monkeypatch.setattr(cli, "_load_state", lambda conn: {"pid": 42})
    monkeypatch.setattr(cli, "_is_running", lambda pid: True)
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

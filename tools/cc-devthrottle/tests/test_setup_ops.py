"""Tests for cc-devthrottle setup diagnostics and delegation."""

import json
import os
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import setup_ops  # noqa: E402


def _script_target(root: Path, script: str) -> Path:
    if os.name == "nt":
        return root / "pyenv" / "Scripts" / f"{script}.exe"
    return root / "pyenv" / "bin" / script


def _user_bin() -> Path:
    return Path.home() / ".local" / "bin"


def _write_healthy_script(root: Path, script: str) -> None:
    target = _script_target(root, script)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text("fake", encoding="utf-8")
    bin_dir = root / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        (bin_dir / f"{script}.cmd").write_text("@echo off\r\n", encoding="utf-8")
        (bin_dir / script).write_text("#!/bin/sh\n", encoding="utf-8")
    else:
        user_bin = _user_bin()
        user_bin.mkdir(parents=True, exist_ok=True)
        shim = user_bin / script
        shim.write_text("#!/bin/sh\n", encoding="utf-8")
        shim.chmod(0o755)


@pytest.fixture
def isolated_install(monkeypatch, tmp_path):
    root = tmp_path / "cc-director"
    monkeypatch.setenv("CC_DIRECTOR_ROOT", str(root))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setenv("USERPROFILE", str(tmp_path / "profile"))
    monkeypatch.setenv("HOME", str(tmp_path / "profile"))
    # On Windows the shim that makes a tool resolvable is written into the install bin
    # directory; on macOS and Linux it is a link in the user's own ~/.local/bin. Both
    # directories have to be on PATH or `shutil.which` cannot see the healthy install
    # this fixture is standing up, and the doctor reports a problem that does not exist.
    # PATH held only the install bin directory, so the suite was green on Windows and
    # red everywhere else.
    search_path = [str(root / "bin")]
    if os.name != "nt":
        search_path.append(str(Path(tmp_path / "profile") / ".local" / "bin"))
    monkeypatch.setenv("PATH", os.pathsep.join(search_path))
    if os.name == "nt":
        monkeypatch.setenv("PATHEXT", ".COM;.EXE;.BAT;.CMD")
    return root


def test_doctor_reports_missing_cc_devthrottle(isolated_install):
    data = setup_ops.doctor_data()

    assert data["ok"] is False
    assert data["needsRepair"] is True
    assert "cc-devthrottle" in data["expectedScripts"]
    assert "cc-devthrottle" in {name for name in data["expectedScripts"]}
    assert any("cc-devthrottle" in problem for problem in data["problems"])
    assert data["repairCommand"] == "cc-devthrottle setup repair"


def test_doctor_uses_recorded_python_tool_scripts(isolated_install):
    state_dir = isolated_install / "config" / "setup"
    state_dir.mkdir(parents=True)
    (state_dir / "python-tools-scripts.json").write_text(
        json.dumps(["cc-devthrottle", "cc-html"]),
        encoding="utf-8",
    )
    (state_dir / "installed.json").write_text(
        json.dumps({"python-tools": "1.2.3"}),
        encoding="utf-8",
    )
    _write_healthy_script(isolated_install, "cc-devthrottle")
    _write_healthy_script(isolated_install, "cc-html")

    data = setup_ops.doctor_data()

    assert data["installedBundleVersion"] == "1.2.3"
    assert data["expectedScripts"] == ["cc-devthrottle", "cc-html"]
    assert data["problems"] == []
    assert data["ok"] is True


def test_setup_cli_args_delegate_repair_to_install():
    assert setup_ops._setup_cli_args("repair", "gateway", dry_run=True, json_output=True) == [
        "install",
        "--role",
        "gateway",
        "--dry-run",
        "--json",
    ]


def test_select_setup_cli_asset_prefers_rebranded_asset():
    assets = {
        "cc-director-setup-cli-win-x64.exe": "https://example/old.exe",
        "devthrottle-setup-cli-win-x64.exe": "https://example/new.exe",
    }

    assert setup_ops._select_setup_cli_asset(assets) == (
        "devthrottle-setup-cli-win-x64.exe",
        "https://example/new.exe",
    )


def test_no_legacy_hard_coded_tool_lists_remain():
    assert not hasattr(setup_ops, "PYTHON_TOOLS")
    assert not hasattr(setup_ops, "NODE_TOOLS")
    assert not hasattr(setup_ops, "DOTNET_TOOLS")


def test_run_autostart_rejects_unknown_verb():
    # The one home per OS lives behind the setup CLI (issue #2022); this passthrough only accepts the
    # three real verbs and fails loud on anything else rather than shelling a nonsense command.
    import typer

    with pytest.raises(typer.Exit) as exc:
        setup_ops.run_autostart("bogus")
    assert exc.value.exit_code == 2


def test_run_autostart_shells_to_setup_cli_autostart(monkeypatch):
    # Proves the passthrough shape: `devthrottle-setup-cli autostart <verb> [--json]`, where the real
    # per-OS mechanism lives. No OS-specific logic is reimplemented in Python.
    monkeypatch.setattr(setup_ops, "_locate_setup_cli", lambda: "/path/devthrottle-setup-cli")

    captured = {}

    class _Result:
        returncode = 0

    def _fake_run(args, check=False):
        captured["args"] = args
        return _Result()

    monkeypatch.setattr(setup_ops.subprocess, "run", _fake_run)

    setup_ops.run_autostart("on", json_output=True)
    assert captured["args"] == ["/path/devthrottle-setup-cli", "autostart", "on", "--json"]

    setup_ops.run_autostart("status", json_output=False)
    assert captured["args"] == ["/path/devthrottle-setup-cli", "autostart", "status"]


def test_legacy_alias_names_cover_all_retired_fleet_commands():
    # The retired per-tool fleet commands consolidated into cc-devthrottle (issue #823), plus
    # cc-playwright, cut from the shipped toolbelt (issue #1002). Kept in sync with the installer
    # engine's PythonToolsInstaller.LegacyAliasShimNames so the same names the installer purges are
    # the ones doctor reports. cc-fleet-selftest must be present (it was missing).
    assert setup_ops.LEGACY_ALIAS_NAMES == [
        "cc-send",
        "cc-ask",
        "cc-spawn",
        "cc-sessions",
        "cc-whoami",
        "cc-settings",
        "cc-cron",
        "cc-fleet-selftest",
        "cc-playwright",
    ]


def test_doctor_reports_legacy_aliases(isolated_install):
    data = setup_ops.doctor_data()

    reported = {entry["name"] for entry in data["legacyAliases"]}
    assert reported == set(setup_ops.LEGACY_ALIAS_NAMES)
    # On a clean isolated install none of the retired aliases resolve on PATH.
    assert all(entry["resolvedPath"] is None for entry in data["legacyAliases"])

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
    # Only Windows writes shims into the install bin directory. On macOS and Linux the installer
    # never creates that directory (PythonToolsInstaller.WriteUnixShims links into ~/.local/bin), so
    # creating it here would model an install that does not exist and hide a doctor that demands it.
    if os.name == "nt":
        bin_dir = root / "bin"
        bin_dir.mkdir(parents=True, exist_ok=True)
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
    # PATH is what the installer leaves behind, per platform: on Windows it adds the install bin
    # directory, where it writes the shims; on macOS and Linux the shims are links in
    # ~/.local/bin and that is the ONLY directory it puts on PATH
    # (InstallFinalizer.EnsureMacUserBinOnPath). Do not add the install bin directory here on
    # macOS or Linux - that would hide a doctor that still demands it.
    if os.name == "nt":
        search_path = str(root / "bin")
    else:
        search_path = str(tmp_path / "profile" / ".local" / "bin")
    monkeypatch.setenv("PATH", search_path)
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


_ALL_SETUP_CLI_ASSETS = {
    "cc-director-setup-cli-win-x64.exe": "https://example/old.exe",
    "devthrottle-setup-cli-win-x64.exe": "https://example/new.exe",
    "devthrottle-setup-cli-mac-arm64": "https://example/mac",
    "devthrottle-setup-cli-linux-x64": "https://example/linux",
}


def test_select_setup_cli_asset_prefers_rebranded_asset_on_windows():
    assert setup_ops._select_setup_cli_asset(_ALL_SETUP_CLI_ASSETS, "windows", "x64") == (
        "devthrottle-setup-cli-win-x64.exe",
        "https://example/new.exe",
    )


def test_select_setup_cli_asset_picks_mac_asset_on_macos():
    assert setup_ops._select_setup_cli_asset(_ALL_SETUP_CLI_ASSETS, "macos", "arm64") == (
        "devthrottle-setup-cli-mac-arm64",
        "https://example/mac",
    )


def test_select_setup_cli_asset_picks_linux_asset_on_linux():
    assert setup_ops._select_setup_cli_asset(_ALL_SETUP_CLI_ASSETS, "linux", "x64") == (
        "devthrottle-setup-cli-linux-x64",
        "https://example/linux",
    )


def test_select_setup_cli_asset_never_offers_windows_exe_to_macos_or_linux():
    windows_only = {"devthrottle-setup-cli-win-x64.exe": "https://example/new.exe"}

    assert setup_ops._select_setup_cli_asset(windows_only, "macos", "arm64") == (None, None)
    assert setup_ops._select_setup_cli_asset(windows_only, "linux", "x64") == (None, None)


@pytest.mark.parametrize("system, machine", [("macos", "x64"), ("linux", "arm64"), ("freebsd", "x64")])
def test_select_setup_cli_asset_unpublished_platform_raises(system, machine):
    with pytest.raises(setup_ops.UnsupportedSetupPlatformError, match=f"{system} {machine}"):
        setup_ops._select_setup_cli_asset(_ALL_SETUP_CLI_ASSETS, system, machine)


@pytest.mark.parametrize(
    "sys_platform, os_name, machine, expected",
    [
        ("darwin", "posix", "arm64", ("macos", "arm64")),
        ("linux", "posix", "x86_64", ("linux", "x64")),
        ("linux", "posix", "aarch64", ("linux", "arm64")),
        ("win32", "nt", "AMD64", ("windows", "x64")),
    ],
)
def test_current_platform_normalises_names(monkeypatch, sys_platform, os_name, machine, expected):
    monkeypatch.setattr(setup_ops.sys, "platform", sys_platform)
    monkeypatch.setattr(setup_ops, "_is_windows", lambda: os_name == "nt")
    monkeypatch.setattr(setup_ops.platform, "machine", lambda: machine)

    assert setup_ops._current_platform() == expected


def test_run_setup_cli_unpublished_platform_fails_before_any_download(monkeypatch):
    import typer

    monkeypatch.setattr(setup_ops, "_locate_setup_cli", lambda: None)
    monkeypatch.setattr(setup_ops, "_current_platform", lambda: ("linux", "arm64"))

    def _no_network():
        raise AssertionError("must not look up a release for an unpublished platform")

    def _no_run(*args, **kwargs):
        raise AssertionError("must not run anything")

    monkeypatch.setattr(setup_ops, "_latest_release", _no_network)
    monkeypatch.setattr(setup_ops.subprocess, "run", _no_run)

    with pytest.raises(typer.Exit) as exc:
        setup_ops.run_setup_cli("install", "workstation")
    assert exc.value.exit_code == 1


def test_download_setup_cli_fetches_this_platforms_asset(monkeypatch, tmp_path):
    monkeypatch.setattr(setup_ops, "_current_platform", lambda: ("linux", "x64"))
    monkeypatch.setattr(setup_ops.tempfile, "gettempdir", lambda: str(tmp_path))
    monkeypatch.setattr(
        setup_ops,
        "_latest_release",
        lambda: {
            "assets": [
                {"name": name, "browser_download_url": url} for name, url in _ALL_SETUP_CLI_ASSETS.items()
            ]
        },
    )
    fetched = []

    def _fake_download(url, dest, show_progress=True):
        fetched.append(url)
        Path(dest).write_text("#!/bin/sh\n", encoding="utf-8")
        return True

    monkeypatch.setattr(setup_ops, "_download_file", _fake_download)

    path = setup_ops._download_setup_cli()

    assert fetched == ["https://example/linux"]
    assert Path(path).name == "devthrottle-setup-cli-linux-x64"
    if os.name != "nt":
        assert os.access(path, os.X_OK)


def test_doctor_requires_the_platforms_shim_directory_on_path(isolated_install, monkeypatch, tmp_path):
    # A healthy install, with PATH exactly as the installer leaves it, is healthy. On macOS and
    # Linux that PATH holds ~/.local/bin and NOT the install bin directory; the old rule demanded
    # the install bin directory and called every healthy Mac install broken.
    _write_healthy_script(isolated_install, "cc-devthrottle")

    data = setup_ops.doctor_data()

    assert data["problems"] == []
    assert data["shimDirOnPath"] is True
    if os.name != "nt":
        assert data["shimDir"] == str(tmp_path / "profile" / ".local" / "bin")
        assert data["binDirOnPath"] is False

    # And taking the shim directory off PATH is reported.
    monkeypatch.setenv("PATH", str(tmp_path / "elsewhere"))
    data = setup_ops.doctor_data()
    assert data["needsRepair"] is True
    assert any("shim directory is not on PATH" in problem for problem in data["problems"])


def test_doctor_healthy_install_needs_only_the_shim_directory(isolated_install):
    # A healthy macOS or Linux install has no install bin directory at all. The old rule called
    # that "install bin directory is missing" and asked for a repair.
    _write_healthy_script(isolated_install, "cc-devthrottle")
    if os.name != "nt":
        assert not (isolated_install / "bin").exists()

    data = setup_ops.doctor_data()

    assert data["problems"] == []
    assert data["needsRepair"] is False


def test_doctor_reports_a_missing_shim_directory(isolated_install):
    data = setup_ops.doctor_data()

    shim_dir = data["shimDir"]
    assert not Path(shim_dir).exists()
    assert f"tool shim directory is missing: {shim_dir}" in data["problems"]
    assert data["needsRepair"] is True


def test_latest_release_url_defaults_to_the_product_release_repository(monkeypatch):
    monkeypatch.delenv("DEVTHROTTLE_GITHUB_OWNER", raising=False)
    monkeypatch.delenv("DEVTHROTTLE_GITHUB_REPO", raising=False)

    # Must match tools/cc-director-setup-engine/GitHubRepositoryDefaults.cs.
    assert setup_ops._latest_release_url() == (
        "https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest"
    )


def test_latest_release_url_honours_environment_overrides(monkeypatch):
    monkeypatch.setenv("DEVTHROTTLE_GITHUB_OWNER", "someone")
    monkeypatch.setenv("DEVTHROTTLE_GITHUB_REPO", "fork")
    assert setup_ops._latest_release_url() == "https://api.github.com/repos/someone/fork/releases/latest"

    monkeypatch.setenv("DEVTHROTTLE_GITHUB_OWNER", "  ")
    monkeypatch.setenv("DEVTHROTTLE_GITHUB_REPO", "")
    assert setup_ops._latest_release_url() == (
        "https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest"
    )


def _raise_http_error(status):
    import urllib.error

    def _urlopen(request, timeout=None):
        raise urllib.error.HTTPError(request.full_url, status, "Not Found", {}, None)

    return _urlopen


def test_latest_release_http_error_raises_with_status_and_url(monkeypatch):
    monkeypatch.delenv("DEVTHROTTLE_GITHUB_OWNER", raising=False)
    monkeypatch.delenv("DEVTHROTTLE_GITHUB_REPO", raising=False)
    monkeypatch.setattr(setup_ops.urllib.request, "urlopen", _raise_http_error(404))

    with pytest.raises(setup_ops.ReleaseLookupError) as exc:
        setup_ops._latest_release()

    assert "HTTP 404" in str(exc.value)
    assert setup_ops._latest_release_url() in str(exc.value)


def test_run_setup_cli_release_lookup_failure_reaches_the_user(monkeypatch, capsys, plain):
    import typer

    monkeypatch.setattr(setup_ops, "_locate_setup_cli", lambda: None)
    monkeypatch.setattr(setup_ops, "_current_platform", lambda: ("linux", "x64"))
    monkeypatch.setattr(setup_ops.urllib.request, "urlopen", _raise_http_error(403))
    with pytest.raises(typer.Exit) as exc:
        setup_ops.run_setup_cli("install", "workstation")

    assert exc.value.exit_code == 1
    captured = capsys.readouterr()
    # An error belongs on standard error (docs/axi-standard.md, principle 6); standard output stays
    # clean so a `--json` caller never has to parse around it.
    assert captured.out == ""
    output = plain(captured.err)
    assert "HTTP 403" in output
    assert "/releases/latest" in output
    assert "help[1]:\n  cc-devthrottle setup doctor" in output


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

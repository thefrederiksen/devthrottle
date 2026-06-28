"""Setup/install operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import subprocess
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

console = Console()

GITHUB_API_BASE = "https://api.github.com"
REPO_OWNER = "thefrederiksen"
REPO_NAME = "devthrottle"

SETUP_CLI_ASSET_NAMES = [
    "devthrottle-setup-cli-win-x64.exe",
    "cc-director-setup-cli-win-x64.exe",
]

SETUP_CLI_COMMAND_NAMES = [
    "devthrottle-setup-cli",
    "devthrottle-setup-cli.exe",
    "cc-director-setup-cli",
    "cc-director-setup-cli.exe",
]

# Retired per-tool fleet commands that were consolidated into the single cc-devthrottle
# command (issue #823). Their executables no longer ship, so any leftover bin shim pointing at
# them fails with exit 127. The installer engine purges these (PythonToolsInstaller
# .LegacyAliasShimNames - keep the two lists in sync); doctor reports their resolved path so a
# machine that still carries one is visible.
LEGACY_ALIAS_NAMES = [
    "cc-send",
    "cc-ask",
    "cc-spawn",
    "cc-sessions",
    "cc-whoami",
    "cc-settings",
    "cc-cron",
    "cc-fleet-selftest",
]


def _latest_release() -> Optional[dict]:
    url = f"{GITHUB_API_BASE}/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest"
    try:
        request = urllib.request.Request(
            url,
            headers={
                "Accept": "application/vnd.github.v3+json",
                "User-Agent": "cc-devthrottle",
            },
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise


def _release_assets(release: dict) -> dict:
    assets = {}
    for asset in release.get("assets", []):
        name = asset.get("name", "")
        url = asset.get("browser_download_url", "")
        if name and url:
            assets[name] = url
    return assets


def _download_file(url: str, dest_path: str, show_progress: bool = True) -> bool:
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "cc-devthrottle"})
        with urllib.request.urlopen(request, timeout=300) as response:
            total_size = int(response.headers.get("Content-Length", 0))
            downloaded = 0
            block_size = 8192

            with open(dest_path, "wb") as file:
                while True:
                    chunk = response.read(block_size)
                    if not chunk:
                        break
                    file.write(chunk)
                    downloaded += len(chunk)
                    if show_progress and total_size > 0:
                        percent = (downloaded / total_size) * 100
                        mb_downloaded = downloaded / (1024 * 1024)
                        mb_total = total_size / (1024 * 1024)
                        print(
                            f"\r  Progress: {percent:.1f}% ({mb_downloaded:.1f}/{mb_total:.1f} MB)",
                            end="",
                        )
            if show_progress:
                print()
        return True
    except (urllib.error.URLError, OSError) as exc:
        console.print(f"[red]Download failed:[/red] {exc}")
        return False


class DevThrottleInstaller:
    """Local setup diagnostics and installer delegation for DevThrottle."""

    def __init__(self) -> None:
        self.install_root = _install_root()
        self.install_dir = self.install_root / "bin"
        self.pyenv_dir = self.install_root / "pyenv"
        self.pyenv_scripts_dir = self.pyenv_dir / ("Scripts" if _is_windows() else "bin")
        self.setup_state_dir = self.install_root / "config" / "setup"
        self.skill_dir = Path(os.environ.get("USERPROFILE", "")) / ".claude" / "skills" / "dev-throttle"
        self.alpha_mode = self._read_alpha_mode()

    def _read_alpha_mode(self) -> bool:
        config_path = self.install_root / "config" / "config.json"
        if not config_path.exists():
            return False
        try:
            data = json.loads(config_path.read_text(encoding="utf-8"))
            return bool(data.get("alpha_mode", False))
        except (OSError, ValueError, TypeError):
            return False


def _is_windows() -> bool:
    return os.name == "nt"


def _install_root() -> Path:
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return Path(override)
    if _is_windows():
        return Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "cc-director"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share")) / "cc-director"


def _path_entries() -> list[str]:
    return [p for p in os.environ.get("PATH", "").split(os.pathsep) if p]


def _path_contains(path: Path) -> bool:
    target = str(path).rstrip("\\/")
    if _is_windows():
        target = target.lower()
        return any(entry.rstrip("\\/").lower() == target for entry in _path_entries())
    return any(entry.rstrip("/") == target for entry in _path_entries())


def _read_json_file(path: Path, fallback):
    try:
        if path.exists():
            return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError, TypeError):
        return fallback
    return fallback


def _expected_scripts(installer: DevThrottleInstaller) -> list[str]:
    sidecar = installer.setup_state_dir / "python-tools-scripts.json"
    value = _read_json_file(sidecar, [])
    scripts = [str(v) for v in value if isinstance(v, str) and v.strip()]
    if "cc-devthrottle" not in scripts:
        scripts.insert(0, "cc-devthrottle")
    return sorted(set(scripts))


def _installed_bundle_version(installer: DevThrottleInstaller) -> Optional[str]:
    manifest = _read_json_file(installer.setup_state_dir / "installed.json", {})
    if isinstance(manifest, dict):
        value = manifest.get("python-tools")
        return str(value) if value else None
    return None


def _venv_script_path(installer: DevThrottleInstaller, script: str) -> Path:
    suffix = ".exe" if _is_windows() else ""
    return installer.pyenv_scripts_dir / f"{script}{suffix}"


def _shim_paths(installer: DevThrottleInstaller, script: str) -> dict[str, str]:
    if _is_windows():
        return {
            "cmd": str(installer.install_dir / f"{script}.cmd"),
            "bare": str(installer.install_dir / script),
        }
    return {"link": str(Path.home() / ".local" / "bin" / script)}


def _command_status(installer: DevThrottleInstaller, script: str) -> dict:
    shim_paths = _shim_paths(installer, script)
    venv_script = _venv_script_path(installer, script)
    return {
        "name": script,
        "resolvedPath": shutil.which(script),
        "venvScript": str(venv_script),
        "venvScriptExists": venv_script.exists(),
        "shims": {
            name: {"path": path, "exists": Path(path).exists()} for name, path in shim_paths.items()
        },
    }


def _legacy_alias_status() -> list[dict]:
    return [{"name": name, "resolvedPath": shutil.which(name)} for name in LEGACY_ALIAS_NAMES]


def doctor_data() -> dict:
    installer = DevThrottleInstaller()
    expected = _expected_scripts(installer)
    commands = [_command_status(installer, script) for script in expected]
    missing = [
        command["name"]
        for command in commands
        if not command["venvScriptExists"] or not any(s["exists"] for s in command["shims"].values())
    ]
    cc_resolved = shutil.which("cc-devthrottle")
    problems = []
    if not installer.install_dir.exists():
        problems.append("install bin directory is missing")
    if not _path_contains(installer.install_dir):
        problems.append("install bin directory is not on PATH")
    if cc_resolved is None:
        problems.append("cc-devthrottle is not resolvable on PATH")
    if missing:
        problems.append(f"missing or incomplete tool shims: {', '.join(missing)}")

    return {
        "ok": not problems,
        "needsRepair": bool(problems),
        "installRoot": str(installer.install_root),
        "binDir": str(installer.install_dir),
        "binDirExists": installer.install_dir.exists(),
        "binDirOnPath": _path_contains(installer.install_dir),
        "pyenvDir": str(installer.pyenv_dir),
        "pyenvScriptsDir": str(installer.pyenv_scripts_dir),
        "setupStateDir": str(installer.setup_state_dir),
        "installedBundleVersion": _installed_bundle_version(installer),
        "expectedScripts": expected,
        "commands": commands,
        "ccDevThrottlePath": cc_resolved,
        "legacyAliases": _legacy_alias_status(),
        "skillDir": str(installer.skill_dir),
        "skillInstalled": (installer.skill_dir / "SKILL.md").exists(),
        "alphaMode": installer.alpha_mode,
        "problems": problems,
        "repairCommand": "cc-devthrottle setup repair",
        "repairDelegatesTo": "devthrottle-setup-cli install --role workstation",
    }


def _select_setup_cli_asset(assets: dict) -> tuple[Optional[str], Optional[str]]:
    for name in SETUP_CLI_ASSET_NAMES:
        if name in assets:
            return name, assets[name]
    return None, None


def _locate_setup_cli() -> Optional[str]:
    for command in SETUP_CLI_COMMAND_NAMES:
        found = shutil.which(command)
        if found:
            return found

    installer = DevThrottleInstaller()
    candidates = []
    for command in SETUP_CLI_COMMAND_NAMES:
        candidates.append(installer.install_dir / command)
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    return None


def _download_setup_cli() -> Optional[str]:
    release = _latest_release()
    if not release:
        return None
    assets = _release_assets(release)
    asset_name, url = _select_setup_cli_asset(assets)
    if not asset_name or not url:
        return None

    cache_dir = Path(tempfile.gettempdir()) / "cc-devthrottle-setup-cli"
    cache_dir.mkdir(parents=True, exist_ok=True)
    dest = cache_dir / asset_name
    if _download_file(url, str(dest), show_progress=True):
        return str(dest)
    return None


def _setup_cli_args(command: str, role: str, dry_run: bool, json_output: bool) -> list[str]:
    setup_command = "update" if command == "update" else "install"
    args = [setup_command, "--role", role]
    if dry_run:
        args.append("--dry-run")
    if json_output:
        args.append("--json")
    return args


def run_setup_cli(command: str, role: str, dry_run: bool = False, json_output: bool = False) -> None:
    if role not in {"workstation", "gateway"}:
        console.print("[red]ERROR:[/red] --role must be workstation or gateway")
        raise typer.Exit(2)

    setup_cli = _locate_setup_cli()
    if not setup_cli:
        console.print("Setup CLI not found locally; downloading the latest release setup CLI...")
        setup_cli = _download_setup_cli()
    if not setup_cli:
        console.print("[red]ERROR:[/red] Could not find or download devthrottle-setup-cli.")
        console.print("Download and run devthrottle-setup-win-x64.exe from the latest GitHub release.")
        raise typer.Exit(1)

    args = [setup_cli, *_setup_cli_args(command, role, dry_run, json_output)]
    console.print(f"Delegating to setup engine: {' '.join(args)}")
    completed = subprocess.run(args, check=False)
    if completed.returncode != 0:
        raise typer.Exit(completed.returncode)


def status(json_output: bool) -> None:
    data = doctor_data()
    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"Install root:     {data['installRoot']}")
    console.print(f"Bin dir:          {data['binDir']}")
    console.print(f"Bin dir exists:   {'yes' if data['binDirExists'] else 'no'}")
    console.print(f"Bin dir on PATH:  {'yes' if data['binDirOnPath'] else 'no'}")
    console.print(f"cc-devthrottle:   {data['ccDevThrottlePath'] or 'not found'}")
    console.print(f"Tools bundle:     {data['installedBundleVersion'] or 'not recorded'}")
    console.print(f"Skill dir:        {data['skillDir']}")
    console.print(f"Skill installed:  {'yes' if data['skillInstalled'] else 'no'}")
    console.print(f"Alpha mode:       {'on' if data['alphaMode'] else 'off'}")
    if data["problems"]:
        console.print("[red]Problems:[/red]")
        for problem in data["problems"]:
            console.print(f"  - {problem}")
        console.print(f"Repair: {data['repairCommand']}")
    else:
        console.print("[green]Setup looks healthy.[/green]")


def doctor(json_output: bool) -> None:
    status(json_output)


def install(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    try:
        run_setup_cli("install", role, dry_run, json_output)
    except KeyboardInterrupt:
        console.print("\nInstallation cancelled by user.")
        raise typer.Exit(1)
    except (OSError, RuntimeError, urllib.error.URLError, subprocess.SubprocessError) as exc:
        console.print(f"\n[red]ERROR:[/red] {exc}")
        raise typer.Exit(1)


def update(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    run_setup_cli("update", role, dry_run, json_output)


def repair(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    run_setup_cli("repair", role, dry_run, json_output)

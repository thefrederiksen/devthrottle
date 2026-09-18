"""Setup/install operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import platform
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

from . import axi_cli

console = Console()

_SETUP_ROLES = ("workstation", "gateway")
_AUTOSTART_VERBS = ("on", "off", "status")

GITHUB_API_BASE = "https://api.github.com"
# The product's release location, and the same default as
# tools/cc-director-setup-engine/GitHubRepositoryDefaults.cs - keep the two in sync. It must be the
# REAL repository: a wrong default answers 404 and no setup tool can ever be downloaded. The
# environment variables override it for a fork that hosts its releases elsewhere.
REPO_OWNER_ENVIRONMENT_VARIABLE = "DEVTHROTTLE_GITHUB_OWNER"
REPO_NAME_ENVIRONMENT_VARIABLE = "DEVTHROTTLE_GITHUB_REPO"
DEFAULT_REPO_OWNER = "thefrederiksen"
DEFAULT_REPO_NAME = "devthrottle"

# The setup command line tool each release publishes, keyed by (operating system, processor), in
# preference order. These are the names .github/workflows/release.yml uploads - keep the two in
# sync. Windows 11 on Arm runs the x64 build through its built-in emulation, so it takes the same
# asset. A platform missing from this table has no setup tool to download, and the caller must say
# so rather than fetch another platform's executable.
SETUP_CLI_ASSETS_BY_PLATFORM = {
    ("windows", "x64"): ["devthrottle-setup-cli-win-x64.exe", "cc-director-setup-cli-win-x64.exe"],
    ("windows", "arm64"): ["devthrottle-setup-cli-win-x64.exe", "cc-director-setup-cli-win-x64.exe"],
    ("macos", "arm64"): ["devthrottle-setup-cli-mac-arm64"],
    ("linux", "x64"): ["devthrottle-setup-cli-linux-x64"],
}

_MACHINE_ALIASES = {
    "amd64": "x64",
    "x86_64": "x64",
    "x64": "x64",
    "arm64": "arm64",
    "aarch64": "arm64",
}

SETUP_CLI_COMMAND_NAMES = [
    "devthrottle-setup-cli",
    "devthrottle-setup-cli.exe",
    "cc-director-setup-cli",
    "cc-director-setup-cli.exe",
]

# Command names that no longer ship, and whose leftover bin shim points at a venv executable that
# is gone: the retired per-tool fleet commands consolidated into the single cc-devthrottle command
# (issue #823), plus cc-playwright, cut from the shipped toolbelt (issue #1002). A leftover fleet
# alias fails with exit 127; a leftover shim for a tool dropped from the manifest instead tells a
# healthy install that "cc-* tools are not fully installed", which is worse. The installer engine
# purges these (PythonToolsInstaller.LegacyAliasShimNames - keep the two lists in sync); doctor
# reports their resolved path so a machine that still carries one is visible.
LEGACY_ALIAS_NAMES = [
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


class ReleaseLookupError(Exception):
    """The latest release could not be read. Carries the address and the reason so the user sees
    exactly which request failed - never a silent 'nothing found'."""


def _resolve_setting(variable: str, default: str) -> str:
    value = os.environ.get(variable, "").strip()
    return value or default


def _release_repository() -> tuple[str, str]:
    return (
        _resolve_setting(REPO_OWNER_ENVIRONMENT_VARIABLE, DEFAULT_REPO_OWNER),
        _resolve_setting(REPO_NAME_ENVIRONMENT_VARIABLE, DEFAULT_REPO_NAME),
    )


def _latest_release_url() -> str:
    owner, name = _release_repository()
    return f"{GITHUB_API_BASE}/repos/{owner}/{name}/releases/latest"


def _latest_release() -> dict:
    url = _latest_release_url()
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github.v3+json",
            "User-Agent": "cc-devthrottle",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        raise ReleaseLookupError(f"GET {url} failed with HTTP {exc.code} {exc.reason}") from exc
    except urllib.error.URLError as exc:
        raise ReleaseLookupError(f"GET {url} failed: {exc.reason}") from exc


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
                            file=sys.stderr,
                        )
            if show_progress:
                print(file=sys.stderr)
        return True
    except (urllib.error.URLError, OSError) as exc:
        print(axi_cli.ascii_text(f"Download failed: {exc}"), file=sys.stderr)
        return False


class DevThrottleInstaller:
    """Local setup diagnostics and installer delegation for DevThrottle."""

    def __init__(self) -> None:
        self.install_root = _install_root()
        self.install_dir = self.install_root / "bin"
        self.pyenv_dir = self.install_root / "pyenv"
        self.pyenv_scripts_dir = self.pyenv_dir / ("Scripts" if _is_windows() else "bin")
        self.setup_state_dir = self.install_root / "config" / "setup"
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


def _machine_root(root: Path) -> Path:
    """The machine root that ``root`` belongs to.

    A Director's own folder is always ``<machine root>/instances/<name>``, and every Director hands
    that path to every session it starts as the CC_DIRECTOR_ROOT setting. So this command, run from
    inside a session, used to take that Director's folder for the whole machine - and reported the
    install status of, and repaired into, a folder that holds no install. This climbs out of a
    Director's folder and answers with the machine root above it.

    It climbs repeatedly, because the same leak produced nested folders
    (``instances/default/instances/default`` exists on the computer that prompted this work) and one
    climb out of that lands on another Director's folder. Climbing on cannot reach past a real
    machine root: a real machine root is never a child of a folder named ``instances``.

    Any other path - a throwaway root a test rig pins with CC_DIRECTOR_ROOT, say - comes back exactly
    as it went in, because a rig keeping its own tools is what makes a rig safe to run at all.
    """
    current = root
    while current.parent.name.lower() == "instances":
        machine = current.parent.parent
        # A folder named "instances" at the top of the tree is not a shape any Director writes.
        # Inventing a parent that is not there would be worse than answering with what we were given.
        if machine == current.parent:
            return current
        current = machine
    return current


def _install_root() -> Path:
    override = os.environ.get("CC_DIRECTOR_ROOT")
    if override:
        return _machine_root(Path(override))
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


def _shim_dir(installer: DevThrottleInstaller) -> Path:
    """The directory whose presence on PATH makes the tools resolvable. On Windows the installer
    writes the shims into the install bin directory and adds that to PATH; on macOS and Linux the
    shims are links in ~/.local/bin and that is the only directory the installer puts on PATH
    (InstallFinalizer.EnsureMacUserBinOnPath). Requiring the install bin directory there reported
    every healthy macOS install as broken."""
    if _is_windows():
        return installer.install_dir
    return Path.home() / ".local" / "bin"


def doctor_data() -> dict:
    installer = DevThrottleInstaller()
    shim_dir = _shim_dir(installer)
    expected = _expected_scripts(installer)
    commands = [_command_status(installer, script) for script in expected]
    missing = [
        command["name"]
        for command in commands
        if not command["venvScriptExists"] or not any(s["exists"] for s in command["shims"].values())
    ]
    cc_resolved = shutil.which("cc-devthrottle")
    problems = []
    # Only the directory that holds the shims must exist. On macOS and Linux that is ~/.local/bin
    # and the install bin directory is never created (PythonToolsInstaller.WriteUnixShims), so
    # requiring it there reported every healthy install as needing repair.
    if not shim_dir.exists():
        problems.append(f"tool shim directory is missing: {shim_dir}")
    if not _path_contains(shim_dir):
        problems.append(f"tool shim directory is not on PATH: {shim_dir}")
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
        "shimDir": str(shim_dir),
        "shimDirOnPath": _path_contains(shim_dir),
        "pyenvDir": str(installer.pyenv_dir),
        "pyenvScriptsDir": str(installer.pyenv_scripts_dir),
        "setupStateDir": str(installer.setup_state_dir),
        "installedBundleVersion": _installed_bundle_version(installer),
        "expectedScripts": expected,
        "commands": commands,
        "ccDevThrottlePath": cc_resolved,
        "legacyAliases": _legacy_alias_status(),
        # No skill status here: the installer places no skill files (issue 995). Skills are held on
        # the Gateway, so "is the dev-throttle skill on this disk" is no longer a question about
        # whether setup worked - reporting it would read as a missing piece of a good install.
        "alphaMode": installer.alpha_mode,
        "problems": problems,
        "repairCommand": "cc-devthrottle setup repair",
        "repairDelegatesTo": "devthrottle-setup-cli install --role workstation",
    }


def _current_platform() -> tuple[str, str]:
    if _is_windows():
        system = "windows"
    elif sys.platform == "darwin":
        system = "macos"
    elif sys.platform.startswith("linux"):
        system = "linux"
    else:
        system = sys.platform
    machine = platform.machine().lower()
    return system, _MACHINE_ALIASES.get(machine, machine)


class UnsupportedSetupPlatformError(RuntimeError):
    """No release publishes a setup tool for this operating system and processor."""


def _setup_cli_asset_names(system: str, machine: str) -> list[str]:
    names = SETUP_CLI_ASSETS_BY_PLATFORM.get((system, machine))
    if not names:
        supported = ", ".join(f"{s} {m}" for s, m in SETUP_CLI_ASSETS_BY_PLATFORM)
        raise UnsupportedSetupPlatformError(
            f"No DevThrottle setup tool is published for {system} {machine} "
            f"(published for: {supported}). Install devthrottle-setup-cli by hand and put it on "
            "PATH, then run this command again."
        )
    return names


def _select_setup_cli_asset(assets: dict, system: str, machine: str) -> tuple[Optional[str], Optional[str]]:
    for name in _setup_cli_asset_names(system, machine):
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
    system, machine = _current_platform()
    # Checked before any network call: a platform with no published setup tool fails here, loudly,
    # and never reaches a download of another platform's executable.
    _setup_cli_asset_names(system, machine)
    release = _latest_release()
    assets = _release_assets(release)
    asset_name, url = _select_setup_cli_asset(assets, system, machine)
    if not asset_name or not url:
        print(f"The latest release has no setup tool for {system} {machine}.", file=sys.stderr)
        return None

    cache_dir = Path(tempfile.gettempdir()) / "cc-devthrottle-setup-cli"
    cache_dir.mkdir(parents=True, exist_ok=True)
    dest = cache_dir / asset_name
    if _download_file(url, str(dest), show_progress=True):
        if not _is_windows():
            # A release asset carries no executable bit; the setup tool is run straight from here.
            dest.chmod(0o755)
        return str(dest)
    return None


def _releases_page() -> str:
    return f"https://github.com/{'/'.join(_release_repository())}/releases/latest"


def _download_setup_cli_or_exit() -> str:
    print("Setup CLI not found locally; downloading the latest release setup CLI...", file=sys.stderr)
    try:
        setup_cli = _download_setup_cli()
    except UnsupportedSetupPlatformError as exc:
        axi_cli.fail(str(exc), ["cc-devthrottle setup doctor"])
    except ReleaseLookupError as exc:
        axi_cli.fail(
            f"Could not read the latest release: {exc}. Check the network and run this again.",
            ["cc-devthrottle setup doctor"],
        )
    if not setup_cli:
        axi_cli.fail(
            "Could not find or download devthrottle-setup-cli. Download the setup tool for this "
            f"machine from {_releases_page()} and put it on PATH, then run this again.",
            ["cc-devthrottle setup doctor"],
        )
    return setup_cli


def _setup_cli_args(command: str, role: str, dry_run: bool, json_output: bool) -> list[str]:
    setup_command = "update" if command == "update" else "install"
    args = [setup_command, "--role", role]
    if dry_run:
        args.append("--dry-run")
    if json_output:
        args.append("--json")
    return args


def run_setup_cli(command: str, role: str, dry_run: bool = False, json_output: bool = False) -> None:
    # Progress and "what is happening" notes go to standard error, so `--json` output stays parseable.
    if role not in _SETUP_ROLES:
        axi_cli.usage_error(f"--role must be one of {', '.join(_SETUP_ROLES)}, not '{role}'.")

    setup_cli = _locate_setup_cli()
    if not setup_cli:
        setup_cli = _download_setup_cli_or_exit()

    args = [setup_cli, *_setup_cli_args(command, role, dry_run, json_output)]
    print(axi_cli.ascii_text(f"Delegating to setup engine: {' '.join(args)}"), file=sys.stderr)
    completed = subprocess.run(args, check=False)
    if completed.returncode != 0:
        axi_cli.fail(
            f"the setup engine exited with code {completed.returncode} during '{command}'; "
            "its own output above says why.",
            ["cc-devthrottle setup doctor", f"cc-devthrottle setup repair --role {role}"],
        )
    if json_output:
        return
    if dry_run:
        axi_cli.print_next([f"cc-devthrottle setup {command} --role {role}", "cc-devthrottle setup status"])
    else:
        axi_cli.print_next(["cc-devthrottle setup status", "cc-devthrottle autostart status"])


def run_autostart(verb: str, json_output: bool = False) -> None:
    """Start-at-login control (issue #2022). Thin passthrough to `devthrottle-setup-cli autostart <verb>`,
    where the one per-OS mechanism lives (Windows Run key / macOS launch agent / Linux systemd --user). The
    CLI + config-file is the universal home a headless Linux server needs, so this is where autostart lives
    now that it left the web Settings page."""
    verb = (verb or "status").lower()
    if verb not in _AUTOSTART_VERBS:
        axi_cli.usage_error(f"autostart verb must be one of {', '.join(_AUTOSTART_VERBS)}, not '{verb}'.")

    setup_cli = _locate_setup_cli()
    if not setup_cli:
        setup_cli = _download_setup_cli_or_exit()

    args = [setup_cli, "autostart", verb]
    if json_output:
        args.append("--json")
    try:
        completed = subprocess.run(args, check=False)
    except OSError as exc:
        axi_cli.fail(f"could not run the setup engine at {setup_cli}: {exc}", ["cc-devthrottle setup doctor"])
    if completed.returncode != 0:
        axi_cli.fail(
            f"the setup engine exited with code {completed.returncode} for 'autostart {verb}'; "
            "its own output above says why.",
            ["cc-devthrottle autostart status", "cc-devthrottle setup doctor"],
        )
    if json_output or verb == "status":
        return
    other = "off" if verb == "on" else "on"
    axi_cli.print_next(["cc-devthrottle autostart status", f"cc-devthrottle autostart {other}"])


def status(json_output: bool) -> None:
    data = doctor_data()
    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"Install root:     {data['installRoot']}")
    console.print(f"Bin dir:          {data['binDir']}")
    console.print(f"Bin dir exists:   {'yes' if data['binDirExists'] else 'no'}")
    console.print(f"Bin dir on PATH:  {'yes' if data['binDirOnPath'] else 'no'}")
    console.print(f"Shim dir:         {data['shimDir']}")
    console.print(f"Shim dir on PATH: {'yes' if data['shimDirOnPath'] else 'no'}")
    console.print(f"cc-devthrottle:   {data['ccDevThrottlePath'] or 'not found'}")
    console.print(f"Tools bundle:     {data['installedBundleVersion'] or 'not recorded'}")
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


def _run_setup_command(command: str, role: str, dry_run: bool, json_output: bool) -> None:
    """install, update and repair share one failure policy: every way the delegation can break ends
    in one sentence on standard error with the next step, never a traceback."""
    try:
        run_setup_cli(command, role, dry_run, json_output)
    except typer.Exit:
        # Already reported. typer.Exit is a RuntimeError, so without this it would be caught below
        # and reported a second time as "failed: 2", with the usage exit code lost.
        raise
    except KeyboardInterrupt:
        axi_cli.fail(f"setup {command} was cancelled.", [f"cc-devthrottle setup {command} --role {role}"])
    except (OSError, RuntimeError, urllib.error.URLError, subprocess.SubprocessError) as exc:
        axi_cli.fail(f"setup {command} failed: {exc}", ["cc-devthrottle setup doctor"])


def install(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    _run_setup_command("install", role, dry_run, json_output)


def update(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    _run_setup_command("update", role, dry_run, json_output)


def repair(role: str = "workstation", dry_run: bool = False, json_output: bool = False) -> None:
    _run_setup_command("repair", role, dry_run, json_output)

"""Setup/install operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import shutil
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from typing import Optional, Tuple

import typer
from rich.console import Console

console = Console()

GITHUB_API_BASE = "https://api.github.com"
GITHUB_RAW_BASE = "https://raw.githubusercontent.com"
REPO_OWNER = "thefrederiksen"
REPO_NAME = "devthrottle"

PYTHON_TOOLS = [
    "cc-crawl4ai",
    "cc-devthrottle",
    "cc-docgen",
    "cc-excel",
    "cc-facebook",
    "cc-gmail",
    "cc-hardware",
    "cc-html",
    "cc-image",
    "cc-outlook",
    "cc-pdf",
    "cc-playwright",
    "cc-photos",
    "cc-posthog",
    "cc-powerpoint",
    "cc-reddit",
    "cc-settings",
    "cc-transcribe",
    "cc-twitter",
    "cc-vault",
    "cc-video",
    "cc-voice",
    "cc-whisper",
    "cc-word",
    "cc-youtube",
    "cc-youtube-info",
]

NODE_TOOLS = [
    "cc-browser",
    "cc-fox-browser",
    "cc-brandingrecommendations",
    "cc-websiteaudit",
]

DOTNET_TOOLS = [
    "cc-click",
    "cc-computer",
    "cc-trisight",
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


def _download_raw_file(path: str, dest_path: str, branch: str = "main") -> bool:
    url = f"{GITHUB_RAW_BASE}/{REPO_OWNER}/{REPO_NAME}/{branch}/{path}"
    return _download_file(url, dest_path, show_progress=False)


class DevThrottleInstaller:
    """Installer for DevThrottle tools."""

    def __init__(self) -> None:
        self.install_dir = Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director" / "bin"
        self.skill_dir = Path(os.environ.get("USERPROFILE", "")) / ".claude" / "skills" / "cc-director"
        self.alpha_mode = self._read_alpha_mode()

    def _read_alpha_mode(self) -> bool:
        config_path = (
            Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director" / "config" / "config.json"
        )
        if not config_path.exists():
            return False
        try:
            data = json.loads(config_path.read_text(encoding="utf-8"))
            return bool(data.get("alpha_mode", False))
        except (OSError, ValueError, TypeError):
            return False

    def install(self) -> bool:
        console.print("=" * 60)
        console.print("  DevThrottle Setup")
        console.print("  https://github.com/thefrederiksen/devthrottle")
        console.print("=" * 60)
        console.print()

        if self.alpha_mode:
            console.print("Alpha mode: ON -- installing app + tools")
        else:
            console.print("Alpha mode: OFF -- installing app only (no tools)")

        console.print("[1/5] Creating install directory...")
        console.print(f"      {self.install_dir}")
        self.install_dir.mkdir(parents=True, exist_ok=True)

        console.print("[2/5] Checking for latest release...")
        release = _latest_release()

        if release:
            version = release.get("tag_name", "unknown")
            console.print(f"      Found release: {version}")
            assets = _release_assets(release)

            if self.alpha_mode:
                console.print("[3/5] Downloading tools...")
                downloaded, skipped = self._download_tools(assets)
                console.print(f"      Downloaded: {downloaded}, Skipped (not yet released): {skipped}")
            else:
                console.print("[3/5] Skipping tools (alpha mode off)")

            if "cc-director.exe" in assets:
                dest_path = self.install_dir / "cc-director.exe"
                console.print("      Downloading cc-director.exe...")
                _download_file(assets["cc-director.exe"], str(dest_path))
        else:
            console.print("      No releases found. Skipping downloads.")
            console.print("      Tools will be available after the first release.")

        if self.alpha_mode:
            console.print("[4/5] Configuring PATH...")
            if self._add_to_path():
                console.print(f"      Added {self.install_dir} to user PATH")
            else:
                console.print("      Already in PATH")

            console.print("[5/5] Installing Claude Code skill...")
            self.skill_dir.mkdir(parents=True, exist_ok=True)
            skill_path = self.skill_dir / "SKILL.md"

            if _download_raw_file(".claude/skills/cc-director/SKILL.md", str(skill_path)):
                console.print(f"      Installed: {skill_path}")
            else:
                console.print("      WARNING: Could not download SKILL.md")
                console.print("      Claude Code integration may not work until manually installed.")
        else:
            console.print("[4/5] Skipping PATH setup (alpha mode off)")
            console.print("[5/5] Skipping skill install (alpha mode off)")

        return True

    def _download_tools(self, assets: dict) -> Tuple[int, int]:
        downloaded = 0
        skipped = 0

        for tool in PYTHON_TOOLS:
            asset_name = f"{tool}.exe"
            if asset_name not in assets:
                skipped += 1
                continue

            dest_path = self.install_dir / asset_name
            console.print(f"      Downloading {asset_name}...")
            if _download_file(assets[asset_name], str(dest_path)):
                downloaded += 1
            else:
                console.print(f"      WARNING: Failed to download {tool}")

        for tool in NODE_TOOLS:
            asset_name = f"{tool}.zip"
            if asset_name not in assets:
                skipped += 1
                continue
            downloaded += self._install_zipped_tool(tool, assets[asset_name], "node")

        for tool in DOTNET_TOOLS:
            asset_name = f"{tool}.zip"
            if asset_name not in assets:
                skipped += 1
                continue
            downloaded += self._install_zipped_tool(tool, assets[asset_name], "dotnet")

        return downloaded, skipped

    def _install_zipped_tool(self, tool: str, url: str, tool_type: str) -> int:
        zip_path = self.install_dir / f"{tool}.zip"
        dest_dir = self.install_dir / f"_{tool}"

        console.print(f"      Downloading {tool}.zip...")
        if not _download_file(url, str(zip_path)):
            console.print(f"      WARNING: Failed to download {tool}")
            return 0

        try:
            if dest_dir.exists():
                shutil.rmtree(dest_dir)

            with zipfile.ZipFile(str(zip_path), "r") as zip_file:
                zip_file.extractall(str(dest_dir))

            self._create_launchers(tool, tool_type)
            zip_path.unlink()
            return 1
        except (zipfile.BadZipFile, OSError) as exc:
            console.print(f"      WARNING: Failed to extract {tool}: {exc}")
            if zip_path.exists():
                zip_path.unlink()
            return 0

    def _create_launchers(self, tool: str, tool_type: str) -> None:
        if tool_type == "node":
            cmd_content = f'@node "%~dp0_{tool}\\src\\cli.mjs" %*\n'
            bash_content = f'#!/bin/sh\nnode "$(dirname "$0")/_{tool}/src/cli.mjs" "$@"\n'
        elif tool_type == "dotnet":
            if tool == "cc-computer":
                cmd_content = f'@"%~dp0_{tool}\\{tool}.exe" --cli %*\n'
                bash_content = f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" --cli "$@"\n'
                gui_cmd = self.install_dir / f"{tool}-gui.cmd"
                gui_bash = self.install_dir / f"{tool}-gui"
                gui_cmd.write_text(f'@"%~dp0_{tool}\\{tool}.exe" %*\n', encoding="utf-8")
                gui_bash.write_text(
                    f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" "$@"\n',
                    encoding="utf-8",
                )
            else:
                cmd_content = f'@"%~dp0_{tool}\\{tool}.exe" %*\n'
                bash_content = f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" "$@"\n'
        else:
            return

        (self.install_dir / f"{tool}.cmd").write_text(cmd_content, encoding="utf-8")
        (self.install_dir / tool).write_text(bash_content, encoding="utf-8")

    def _add_to_path(self) -> bool:
        install_str = str(self.install_dir)
        try:
            import winreg

            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Environment",
                0,
                winreg.KEY_READ | winreg.KEY_WRITE,
            )

            try:
                current_path, _ = winreg.QueryValueEx(key, "Path")
            except OSError:
                current_path = ""

            path_entries = [p.strip() for p in current_path.split(";") if p.strip()]
            path_lower = [p.lower() for p in path_entries]

            if install_str.lower() in path_lower:
                winreg.CloseKey(key)
                return False

            new_path = current_path.rstrip(";") + ";" + install_str if current_path else install_str
            winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
            winreg.CloseKey(key)

            try:
                import ctypes

                hwnd_broadcast = 0xFFFF
                wm_settingchange = 0x1A
                smto_abortifhung = 0x0002
                result = ctypes.c_long()
                ctypes.windll.user32.SendMessageTimeoutW(
                    hwnd_broadcast,
                    wm_settingchange,
                    0,
                    "Environment",
                    smto_abortifhung,
                    5000,
                    ctypes.byref(result),
                )
            except OSError:
                pass

            return True

        except OSError as exc:
            console.print(f"      WARNING: Could not modify PATH: {exc}")
            console.print(f"      Please manually add {install_str} to your PATH")
            return False


def status(json_output: bool) -> None:
    installer = DevThrottleInstaller()
    data = {
        "installDir": str(installer.install_dir),
        "installDirExists": installer.install_dir.exists(),
        "skillDir": str(installer.skill_dir),
        "skillInstalled": (installer.skill_dir / "SKILL.md").exists(),
        "alphaMode": installer.alpha_mode,
    }
    if json_output:
        console.print(json.dumps(data, indent=2))
        return

    console.print(f"Install dir:      {data['installDir']}")
    console.print(f"Install dir exists: {'yes' if data['installDirExists'] else 'no'}")
    console.print(f"Skill dir:        {data['skillDir']}")
    console.print(f"Skill installed:  {'yes' if data['skillInstalled'] else 'no'}")
    console.print(f"Alpha mode:       {'on' if data['alphaMode'] else 'off'}")


def install() -> None:
    try:
        success = DevThrottleInstaller().install()
    except KeyboardInterrupt:
        console.print("\nInstallation cancelled by user.")
        raise typer.Exit(1)
    except (OSError, RuntimeError, urllib.error.URLError) as exc:
        console.print(f"\n[red]ERROR:[/red] {exc}")
        raise typer.Exit(1)

    if success:
        console.print()
        console.print("=" * 60)
        console.print("  Installation complete.")
        console.print("  Restart your terminal to use DevThrottle tools.")
        console.print("=" * 60)
        return

    console.print("\nInstallation failed. See errors above.")
    raise typer.Exit(1)

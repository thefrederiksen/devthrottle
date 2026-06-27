"""
Core installer logic for cc-director tools.
"""

import os
import shutil
import zipfile
import winreg
from pathlib import Path
from typing import Tuple

from github_api import (
    get_latest_release,
    get_release_assets,
    download_file,
    download_raw_file
)


# Python tools - each builds to {name}.exe (except cc-setup -> cc-director-setup.exe)
PYTHON_TOOLS = [
    "cc-crawl4ai",
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

# Node.js tools - each is a zip containing _cc-{name}/ directory
NODE_TOOLS = [
    "cc-browser",
    "cc-fox-browser",
    "cc-brandingrecommendations",
    "cc-websiteaudit",
]

# .NET tools - each is a zip containing _cc-{name}/ directory
DOTNET_TOOLS = [
    "cc-click",
    "cc-computer",
    "cc-trisight",
]


class CCDirectorInstaller:
    """Installer for cc-director tools suite."""

    def __init__(self):
        self.install_dir = Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director" / "bin"
        self.skill_dir = Path(os.environ.get("USERPROFILE", "")) / ".claude" / "skills" / "cc-director"
        self.alpha_mode = self._read_alpha_mode()

    def _read_alpha_mode(self) -> bool:
        """Read alpha_mode from config.json."""
        config_path = Path(os.environ.get("LOCALAPPDATA", "")) / "cc-director" / "config" / "config.json"
        if not config_path.exists():
            return False
        try:
            import json
            with open(config_path, "r") as f:
                config = json.load(f)
            return config.get("alpha_mode", False)
        except Exception:
            return False

    def install(self) -> bool:
        """
        Run the full installation.

        Returns:
            True if successful, False otherwise
        """
        if self.alpha_mode:
            print("Alpha mode: ON -- installing app + tools")
        else:
            print("Alpha mode: OFF -- installing app only (no tools)")

        # Step 1: Create install directory
        print(f"[1/5] Creating install directory...")
        print(f"      {self.install_dir}")
        self.install_dir.mkdir(parents=True, exist_ok=True)

        # Step 2: Get latest release info
        print(f"[2/5] Checking for latest release...")
        release = get_latest_release()

        if release:
            version = release.get("tag_name", "unknown")
            print(f"      Found release: {version}")
            assets = get_release_assets(release)

            if self.alpha_mode:
                # Step 3: Download tools (alpha only)
                print(f"[3/5] Downloading tools...")
                downloaded, skipped = self._download_tools(assets)
                print(f"      Downloaded: {downloaded}, Skipped (not yet released): {skipped}")
            else:
                print(f"[3/5] Skipping tools (alpha mode off)")

            # Download main app exe regardless of alpha mode
            if "cc-director.exe" in assets:
                dest_path = self.install_dir / "cc-director.exe"
                print(f"      Downloading cc-director.exe...")
                download_file(assets["cc-director.exe"], str(dest_path))
        else:
            print("      No releases found. Skipping downloads.")
            print("      (Tools will be available after first release)")

        if self.alpha_mode:
            # Step 4: Add to PATH (alpha only -- tools need PATH)
            print(f"[4/5] Configuring PATH...")
            if self._add_to_path():
                print(f"      Added {self.install_dir} to user PATH")
            else:
                print(f"      Already in PATH")

            # Step 5: Install SKILL.md (alpha only)
            print(f"[5/5] Installing Claude Code skill...")
            self.skill_dir.mkdir(parents=True, exist_ok=True)
            skill_path = self.skill_dir / "SKILL.md"

            if download_raw_file("skills/cc-director/SKILL.md", str(skill_path)):
                print(f"      Installed: {skill_path}")
            else:
                print(f"      WARNING: Could not download SKILL.md")
                print(f"      Claude Code integration may not work until manually installed.")
        else:
            print(f"[4/5] Skipping PATH setup (alpha mode off)")
            print(f"[5/5] Skipping skill install (alpha mode off)")

        return True

    def _download_tools(self, assets: dict) -> Tuple[int, int]:
        """
        Download tool executables and archives from release assets.

        Args:
            assets: Dict mapping asset names to download URLs

        Returns:
            Tuple of (downloaded_count, skipped_count)
        """
        downloaded = 0
        skipped = 0

        # Python tools: download as {name}.exe
        for tool in PYTHON_TOOLS:
            asset_name = f"{tool}.exe"
            if asset_name not in assets:
                skipped += 1
                continue

            dest_path = self.install_dir / asset_name
            print(f"      Downloading {asset_name}...")

            if download_file(assets[asset_name], str(dest_path)):
                downloaded += 1
            else:
                print(f"      WARNING: Failed to download {tool}")

        # Node.js tools: download as {name}.zip, extract to _{name}/, create launchers
        for tool in NODE_TOOLS:
            asset_name = f"{tool}.zip"
            if asset_name not in assets:
                skipped += 1
                continue

            downloaded += self._install_zipped_tool(tool, assets[asset_name], "node")

        # .NET tools: download as {name}.zip, extract to _{name}/, create launchers
        for tool in DOTNET_TOOLS:
            asset_name = f"{tool}.zip"
            if asset_name not in assets:
                skipped += 1
                continue

            downloaded += self._install_zipped_tool(tool, assets[asset_name], "dotnet")

        return downloaded, skipped

    def _install_zipped_tool(self, tool: str, url: str, tool_type: str) -> int:
        """
        Download and extract a zipped tool, then create launcher scripts.

        Args:
            tool: Tool name (e.g. "cc-browser")
            url: Download URL for the zip
            tool_type: "node" or "dotnet"

        Returns:
            1 if successful, 0 if failed
        """
        zip_path = self.install_dir / f"{tool}.zip"
        dest_dir = self.install_dir / f"_{tool}"

        print(f"      Downloading {tool}.zip...")
        if not download_file(url, str(zip_path)):
            print(f"      WARNING: Failed to download {tool}")
            return 0

        # Extract zip
        try:
            # Remove old directory
            if dest_dir.exists():
                shutil.rmtree(dest_dir)

            with zipfile.ZipFile(str(zip_path), 'r') as zf:
                zf.extractall(str(dest_dir))

            # Create launcher scripts
            self._create_launchers(tool, tool_type)

            # Clean up zip
            zip_path.unlink()
            return 1

        except (zipfile.BadZipFile, OSError) as e:
            print(f"      WARNING: Failed to extract {tool}: {e}")
            if zip_path.exists():
                zip_path.unlink()
            return 0

    def _create_launchers(self, tool: str, tool_type: str):
        """Create .cmd and Git Bash launcher scripts for a tool."""
        if tool_type == "node":
            # Node.js tool: launch via node
            cmd_content = f'@node "%~dp0_{tool}\\src\\cli.mjs" %*\n'
            bash_content = f'#!/bin/sh\nnode "$(dirname "$0")/_{tool}/src/cli.mjs" "$@"\n'
        elif tool_type == "dotnet":
            # .NET tool: launch the exe directly
            if tool == "cc-computer":
                # cc-computer has CLI mode (default) and GUI mode
                cmd_content = f'@"%~dp0_{tool}\\{tool}.exe" --cli %*\n'
                bash_content = f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" --cli "$@"\n'
                # Also create GUI launcher
                gui_cmd = self.install_dir / f"{tool}-gui.cmd"
                gui_bash = self.install_dir / f"{tool}-gui"
                gui_cmd.write_text(f'@"%~dp0_{tool}\\{tool}.exe" %*\n')
                gui_bash.write_text(f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" "$@"\n')
            else:
                cmd_content = f'@"%~dp0_{tool}\\{tool}.exe" %*\n'
                bash_content = f'#!/bin/sh\n"$(dirname "$0")/_{tool}/{tool}.exe" "$@"\n'
        else:
            return

        cmd_path = self.install_dir / f"{tool}.cmd"
        bash_path = self.install_dir / tool

        cmd_path.write_text(cmd_content)
        bash_path.write_text(bash_content)

    def _add_to_path(self) -> bool:
        """
        Add install directory to user PATH environment variable.

        Returns:
            True if PATH was modified, False if already present
        """
        install_str = str(self.install_dir)

        try:
            # Open user environment variables
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Environment",
                0,
                winreg.KEY_READ | winreg.KEY_WRITE
            )

            try:
                # Get current PATH
                current_path, _ = winreg.QueryValueEx(key, "Path")
            except WindowsError:
                current_path = ""

            # Check if already in PATH (case-insensitive)
            path_entries = [p.strip() for p in current_path.split(";") if p.strip()]
            path_lower = [p.lower() for p in path_entries]

            if install_str.lower() in path_lower:
                winreg.CloseKey(key)
                return False

            # Add to PATH
            new_path = current_path.rstrip(";") + ";" + install_str if current_path else install_str
            winreg.SetValueEx(key, "Path", 0, winreg.REG_EXPAND_SZ, new_path)
            winreg.CloseKey(key)

            # Notify Windows of environment change
            try:
                import ctypes
                HWND_BROADCAST = 0xFFFF
                WM_SETTINGCHANGE = 0x1A
                SMTO_ABORTIFHUNG = 0x0002
                result = ctypes.c_long()
                ctypes.windll.user32.SendMessageTimeoutW(
                    HWND_BROADCAST,
                    WM_SETTINGCHANGE,
                    0,
                    "Environment",
                    SMTO_ABORTIFHUNG,
                    5000,
                    ctypes.byref(result)
                )
            except OSError:
                pass  # Non-critical if broadcast fails

            return True

        except OSError as e:
            print(f"      WARNING: Could not modify PATH: {e}")
            print(f"      Please manually add {install_str} to your PATH")
            return False

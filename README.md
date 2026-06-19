<p align="center">
  <img src="images/devthrottle-logo.png" alt="DevThrottle" width="420">
</p>

# DevThrottle

Mission control for [Claude Code](https://docs.anthropic.com/en/docs/claude-code). Run, watch, and switch between many Claude Code sessions at once -- each working on its own repository -- from one desktop app, with an embedded terminal, a live git view, voice control, and 35+ bundled `cc-*` command-line tools.

![DevThrottle](images/cc-director-main.png)

> **Platform:** Windows 10/11 and macOS (Apple Silicon). Windows is the most complete experience.

## Install

**[Download the latest release](https://github.com/thefrederiksen/devthrottle/releases/latest)** -- always the newest version, all platforms.

### Windows

[![Download DevThrottle Setup for Windows](https://img.shields.io/badge/Download-Setup%20for%20Windows-2EA44F?style=for-the-badge)](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-win-x64.exe)

1. Download **[devthrottle-setup-win-x64.exe](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-win-x64.exe)** and run it. If SmartScreen appears, click **More info -> Run anyway** (the exe is not code-signed yet).
2. The wizard checks prerequisites (and can auto-install the **.NET 10 runtime**), then installs the Director app, the `cc-*` CLI tools, and the Claude Code skills -- all to user-writable locations, **no admin needed**.

### macOS (Apple Silicon)

[![Download DevThrottle Setup for macOS](https://img.shields.io/badge/Download-Setup%20for%20macOS-2EA44F?style=for-the-badge)](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-mac-arm64.zip)

1. Download and unzip **[devthrottle-setup-mac-arm64.zip](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-mac-arm64.zip)**, then right-click **DevThrottle Setup.app -> Open** (not code-signed, so a plain double-click is blocked by Gatekeeper).
2. The wizard installs the Director to `~/Applications` and the `cc-*` tools into `~/.local/bin`. **No sudo needed.**

### Prerequisite: Claude Code

DevThrottle drives [Claude Code](https://docs.anthropic.com/en/docs/claude-code), so you need a **paid Claude plan** (Pro, Max, Team, or Enterprise) and Claude Code installed. Use Anthropic's official **native installer** -- not `npm`, which is the usual cause of "`claude` command not found":

- **Windows (PowerShell):** `irm https://claude.ai/install.ps1 | iex`
- **macOS / Linux:** `curl -fsSL https://claude.ai/install.sh | bash`

Then run `claude` once to sign in. Full prerequisites and troubleshooting are in the **[installation guide](docs/public/getting-started/02-installation.md)**.

<details>
<summary><b>Other ways to install (setup wizard screenshots, one Claude Code prompt, direct download)</b></summary>

**What the setup wizard looks like.** **Workstation** installs the app plus all `cc-*` tools (per-user, no admin); **Gateway** adds the Gateway tray app and the Cockpit web UI on top.

![DevThrottle Setup - Welcome](images/setup-1-welcome.png)
![DevThrottle Setup - Prerequisites](images/setup-2-prerequisites.png)
![DevThrottle Setup - Install](images/setup-3-update.png)

**Install through Claude Code (no wizard).** Paste a single prompt into Claude Code in any folder; it detects your OS, finds the latest release, verifies the download against the release manifest (SHA-256), and installs DevThrottle to a user-writable location -- no admin/sudo. ([Why a prompt instead of an installer?](docs/PHILOSOPHY.md))

```text
Install the latest release of DevThrottle on THIS machine. You are doing the install yourself -
no installer wizard, no admin/sudo. Detect the OS and follow the matching section. STOP with a
clear message if any step fails; do not silently work around it or build from source.

REPO: github.com/thefrederiksen/devthrottle
Find the latest release - prefer `gh release view --repo thefrederiksen/devthrottle --json tagName,assets`,
else the public API https://api.github.com/repos/thefrederiksen/devthrottle/releases/latest. It must
include `release-manifest.json` plus this OS's asset below. ALWAYS verify the downloaded asset's
SHA-256 against the manifest's entry for that asset before installing; mismatch = STOP.

== WINDOWS ==
ASSET:  cc-director-win-x64.exe        (requires the .NET 10 runtime on the machine)
TARGET: %LOCALAPPDATA%\cc-director\app\cc-director.exe   (user-writable -> auto-update needs no admin)
1. Download cc-director-win-x64.exe + release-manifest.json to %TEMP%\ccd-install.
2. Verify: Get-FileHash -Algorithm SHA256 == manifest sha256 for cc-director-win-x64.exe, else STOP.
3. Create %LOCALAPPDATA%\cc-director\app. If cc-director.exe is there AND running, ask the user to
   close it (do not kill it).
4. Copy the verified exe to %LOCALAPPDATA%\cc-director\app\cc-director.exe.
5. Start Menu shortcut: %APPDATA%\Microsoft\Windows\Start Menu\Programs\DevThrottle.lnk -> the exe
   (working dir = its folder). OPTIONAL autostart on login: also drop a shortcut in
   %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup.

== macOS (Apple Silicon) ==
ASSET:  cc-director-mac-arm64.zip      (contains "CC Director.app"; self-contained)
TARGET: ~/Applications/CC Director.app  (user-writable, NOT /Applications -> auto-update needs no sudo)
1. Download cc-director-mac-arm64.zip + release-manifest.json to /tmp/ccd-install.
2. Verify: `shasum -a 256` of the zip == manifest sha256 for cc-director-mac-arm64.zip, else STOP.
3. Unzip it. `mkdir -p ~/Applications`. If "~/Applications/CC Director.app" is running, ask the user
   to quit it (do not kill it).
4. Replace: `rm -rf "~/Applications/CC Director.app"` then move the unzipped "CC Director.app" into
   ~/Applications.
5. Clear Gatekeeper quarantine: `xattr -dr com.apple.quarantine "~/Applications/CC Director.app"`.
   It's now in Launchpad/Spotlight. OPTIONAL: add to the Dock; OPTIONAL autostart via Login Items.

== BOTH ==
6. Launch it once and confirm the running version matches the release tag.
7. Report: release tag installed, install path, the SHA you verified, and the shortcut/Dock entry.

DO NOT: use Program Files or /Applications, require admin/sudo, build from source, or skip SHA verification.
```

The one-prompt install covers the Director app; the `cc-*` tools and skills come with the setup wizard above. Full reference: [docs/install/install-prompt.md](docs/install/install-prompt.md).

**Direct download.** Grab the app binary straight from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest) (`cc-director-win-x64.exe` or `cc-director-mac-arm64.zip`). This installs the Director only -- the `cc-*` tools come with the wizard.

</details>

## Getting Started

Launch DevThrottle, point it at a repository, and create a session. Each session is a real Claude Code instance running in an embedded console -- anything Claude Code can do, it does here. From the one window you switch between sessions in the sidebar, watch each one's live status, review its git changes, and answer it by voice when you step away from the keyboard.

New here? Walk through the **[Quick Start](docs/public/getting-started/03-quick-start.md)**.

Voice mode and the media tools (`cc-voice`, `cc-whisper`, `cc-transcribe`, `cc-image`) call OpenAI, so they need an OpenAI API key. They are **optional** -- add the key only when you want those features.

## Documentation

The full documentation lives in [`docs/public/`](docs/public/):

| Guide | What's in it |
|-------|--------------|
| [Introduction](docs/public/getting-started/01-introduction.md) | What DevThrottle is and how the pieces fit |
| [Installation](docs/public/getting-started/02-installation.md) | Full install, prerequisites, troubleshooting |
| [Quick Start](docs/public/getting-started/03-quick-start.md) | Your first session and first conversions |
| [Features](docs/public/features/01-overview.md) | Every screen, with screenshots |
| [Tools](docs/public/tools/01-overview.md) | The 35+ bundled `cc-*` command-line tools |
| [Control API](docs/public/api/01-control-api.md) | Drive a running Director over its REST interface |

## Build from source

```bash
dotnet build cc-director.sln
dotnet run --project src/CcDirector.Avalonia/CcDirector.Avalonia.csproj
dotnet test cc-director.sln
```

Requires the **.NET 10 SDK**. On Windows, set the default terminal to the **Windows Console Host** (not Windows Terminal); a warning dialog guides you if needed. App data lives under `%LOCALAPPDATA%\cc-director\` (override with `CC_DIRECTOR_ROOT`).

## Why DevThrottle

I was running five or more Claude Code sessions at once and nothing fit -- terminals lacked file browsing and GitHub integration, and editors got in the way. So I built my own session manager, and I use it every day as my primary development environment. ([The philosophy behind the install model.](docs/PHILOSOPHY.md))

## Stay Updated

- **Watch this repo** (the "Watch" button up top) to hear about new releases.
- **Join the [Discussions](https://github.com/thefrederiksen/devthrottle/discussions)** to ask questions, share your setup, or request features.
- **Follow along** at [sorenfrederiksen.com](https://sorenfrederiksen.com).

## License

MIT

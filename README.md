<p align="center">
  <img src="images/devthrottle-logo.png" alt="DevThrottle" width="420">
</p>

# DevThrottle

A desktop application for managing multiple [Claude Code](https://docs.anthropic.com/en/docs/claude-code) sessions simultaneously. Run, monitor, and switch between independent Claude Code instances -- each working on its own repository -- from a single unified interface.

> **Platform:** Runs on **Windows 10/11** and **macOS (Apple Silicon)**. Both have shipping builds; Windows is currently the most complete experience.

## Install

**[Download the latest release](https://github.com/thefrederiksen/devthrottle/releases/latest)** -- this link always points to the newest version, all platforms, all assets.

### Windows

[![Download DevThrottle Setup for Windows](https://img.shields.io/badge/Download-Setup%20for%20Windows-2EA44F?style=for-the-badge)](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-win-x64.exe)

1. Download **[devthrottle-setup-win-x64.exe](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-win-x64.exe)**.
2. Double-click it. The wizard is self-contained -- it runs even with no .NET installed. If SmartScreen appears, click **More info** -> **Run anyway** (the exe is not code-signed yet).
3. Follow the wizard. It checks prerequisites (and can auto-install the **.NET 10 runtime** for you via winget), then installs the Director app, 35+ `cc-*` CLI tools, and 14 Claude Code skills. Everything goes to user-writable locations -- **no admin needed**.

### macOS (Apple Silicon)

[![Download DevThrottle Setup for macOS](https://img.shields.io/badge/Download-Setup%20for%20macOS-2EA44F?style=for-the-badge)](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-mac-arm64.zip)

1. Download **[devthrottle-setup-mac-arm64.zip](https://github.com/thefrederiksen/devthrottle/releases/latest/download/devthrottle-setup-mac-arm64.zip)** and unzip it.
2. Right-click **DevThrottle Setup.app** -> **Open** (the app is not code-signed, so a plain double-click is blocked by Gatekeeper).
3. Follow the wizard. It installs the Director to `~/Applications`, every `cc-*` tool into one shared Python environment, and links them into `~/.local/bin` (added to your shell `PATH`). **No sudo needed.**

### Prerequisite: Claude Code

DevThrottle manages [Claude Code](https://docs.anthropic.com/en/docs/claude-code) sessions, so you need a **paid Claude plan** (Pro, Max, Team, or Enterprise -- the free Claude.ai plan does **not** include Claude Code) and Claude Code installed. Use Anthropic's official **native installer** (no Node.js; **do not use `npm`** -- it's the usual cause of "`claude` command not found" and PATH problems), then run `claude` once to sign in:

- **Windows (PowerShell):** `irm https://claude.ai/install.ps1 | iex`
- **macOS / Linux:** `curl -fsSL https://claude.ai/install.sh | bash`

More options and troubleshooting: [Anthropic's setup guide](https://code.claude.com/docs/en/setup).

<details>
<summary><b>What the Windows wizard looks like</b></summary>

**Choose your profile** -- **Standard** for core document tools, email, media, and vault, or **Developer** for the full suite including browser automation, LinkedIn, Reddit, social media, and code generation.

![Setup - Choose profile](images/setup-1-welcome.png)

**Prerequisites check** -- the installer verifies the .NET 10 runtime (required; can be auto-installed), Claude Code, Python 3.11+, and Node.js 20+ are available; Brave Browser is optional.

![Setup - Prerequisites](images/setup-2-prerequisites.png)

**Install tools and skills** -- 15+ CLI tools and 14 Claude Code skills, all placed on your PATH.

![Setup - Install](images/setup-3-update.png)

</details>

<details>
<summary><b>Alternative install: paste one prompt into Claude Code (no wizard)</b></summary>

DevThrottle can also install itself **through Claude Code**: paste a single prompt that downloads and installs DevThrottle for you. Works on **Windows** and **macOS (Apple Silicon)**, no admin needed. ([Why a prompt instead of an installer?](docs/PHILOSOPHY.md))

Open Claude Code in any folder and paste the prompt below. It detects your OS, finds the latest release, verifies the download against the release manifest (SHA-256), and installs DevThrottle to a **user-writable** location -- so it needs no admin/sudo and the built-in auto-updater can later replace it in place.

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
6. Launch it once and confirm the running version matches the release tag (check the newest log under
   %LOCALAPPDATA%\cc-director\logs\director\ on Windows, or the app's log dir on macOS).
7. Report: release tag installed, install path, the SHA you verified, and the shortcut/Dock entry.
   Note the runtime prerequisites if not set up: on Windows the .NET 10 runtime must be installed
   (the setup wizard can install it automatically; or get it from
   https://dotnet.microsoft.com/download/dotnet/10.0), a Claude subscription (for Claude Code), and an
   OpenAI API key (audio/transcription/TTS) in the cc-director config dir
   (%LOCALAPPDATA%\cc-director\config\credentials.env on Windows; the equivalent config dir on macOS).

DO NOT: use Program Files or /Applications, require admin/sudo, build from source, or skip SHA verification.
```

Full reference, including why the prompt installs where it does: [docs/install/install-prompt.md](docs/install/install-prompt.md).

Note: the one-prompt install covers the Director app itself; the `cc-*` CLI tools and skills come with the setup wizard above.

</details>

<details>
<summary><b>Alternative: direct download (skip the prompt and the wizard)</b></summary>

Grab the app directly from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest):

| Platform | Download | Notes |
|----------|----------|-------|
| Windows x64 | [cc-director-win-x64.exe](https://github.com/thefrederiksen/devthrottle/releases/latest/download/cc-director-win-x64.exe) | Requires the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the setup wizard can install it for you) |
| macOS (Apple Silicon) | [cc-director-mac-arm64.zip](https://github.com/thefrederiksen/devthrottle/releases/latest/download/cc-director-mac-arm64.zip) | Unzip to `CC Director.app`, move to `~/Applications` (user-writable, so auto-update needs no sudo). First launch: right-click -> Open, or `xattr -dr com.apple.quarantine "~/Applications/CC Director.app"` |

This installs the Director app only. The `cc-*` CLI tools and skills come with the **setup wizard** (see [Install](#install) above).

</details>

## Getting Started

### Start your first session

Launch DevThrottle, point it at a repository, and create a session. That session is a real Claude Code instance running in an embedded console -- anything Claude Code can do, it does here.

![DevThrottle](images/cc-director-main.png)

### Optional: voice and image features

Voice mode and the media tools (`cc-voice`, `cc-whisper`, `cc-transcribe`, `cc-image`) call OpenAI, so they need an OpenAI API key. They are **not** required to run DevThrottle -- add the key only when you want those features (see the [installation guide](docs/public/getting-started/02-installation.md)).

## Why DevThrottle

I built DevThrottle because I was running 5+ Claude Code sessions at once and nothing fit. Terminal programs were missing features I needed -- file browsing, GitHub integration, easy screenshot handling. VS Code had too many things I didn't want getting in the way. So I built my own Claude Code session manager. I use it every day as my primary development environment. It ships with 35+ purpose-built CLI tools and 14 Claude Code skills that handle everything from document generation to browser automation to email management.

![DevThrottle - Multiple sessions with workflow recording](images/cc-director-workflow.png)

## Features

### Multi-Session Management
- Run multiple Claude Code sessions side-by-side, each in its own embedded console
- Switch between sessions instantly from the sidebar
- Drag-and-drop to reorder sessions
- Name and color-code sessions for easy identification
- Right-click context menu: Rename, Open in Explorer, Open in VS Code, Close

### Embedded Console
- Claude Code runs in a native Windows console window overlaid directly onto the Avalonia application
- Full interactive terminal — no emulation, no limitations
- Send prompts from a dedicated input bar at the bottom (Ctrl+Enter to submit)

### Real-Time Activity Tracking
- Monitors each session's state in real-time: **Idle**, **Working**, **Waiting for Input**, **Waiting for Permission**, **Exited**
- Color-coded status indicators on each session in the sidebar
- Powered by Claude Code's hook system — every tool call, prompt, and notification is captured

### Session Persistence
- Sessions survive app restarts — DevThrottle reconnects to running Claude processes on launch
- "Reconnect" button scans for orphaned `claude.exe` processes and reclaims them
- Recent sessions are remembered with their custom names and colors

### Git Integration
- **Source Control tab** shows staged and unstaged changes for the active session's repository
- File tree with status indicators (Modified, Added, Deleted, Renamed, etc.)
- Current branch display with ahead/behind sync status
- Click a file to open it in VS Code

### Repository Management
- **Repositories tab** for registering, cloning, and initializing Git repositories
- Clone from URL or browse your GitHub repos
- Quick-launch a new session from any registered repository

### Hook Integration
- Automatically installs hooks into Claude Code's `~/.claude/settings.json`
- Captures 14 hook event types: session start/end, tool use, notifications, subagent activity, task completion, and more
- Named pipe IPC (`CC_ClaudeDirector`) for fast, async event delivery
- Optional pipe message log panel (toggle from sidebar) for debugging and observability

### Logging & Diagnostics
- File logging to `%LOCALAPPDATA%\cc-director\logs\director\`
- "Open Logs" button in the sidebar for quick access

## Bundled CLI Tools

DevThrottle ships with 35+ command-line tools that are installed on your PATH and available from any terminal or Claude Code session. Every tool follows the `cc-*` naming convention.

| Category | Tools | Description |
|----------|-------|-------------|
| **Documents** | `cc-pdf`, `cc-html`, `cc-word`, `cc-excel`, `cc-powerpoint` | Convert Markdown to PDF, HTML, Word, Excel, and PowerPoint with 7 built-in themes |
| **Email** | `cc-gmail`, `cc-outlook` | Read, search, and manage Gmail and Outlook (calendar, attachments, labels) |
| **Browser** | `cc-browser` | Persistent browser automation with named workspaces and connection management |
| **Social** | `cc-reddit`, `cc-spotify` | Reddit automation with human-like delays, Spotify playback control |
| **Web** | `cc-crawl4ai`, `cc-websiteaudit`, `cc-brandingrecommendations` | AI-ready web crawling, SEO/security audits, branding action plans |
| **Desktop** | `cc-click`, `cc-trisight`, `cc-computer` | Windows UI automation, 3-tier element detection (UIA + OCR + pixel), AI desktop agent |
| **Media** | `cc-image`, `cc-voice`, `cc-whisper`, `cc-video`, `cc-transcribe`, `cc-photos` | Image generation/OCR, text-to-speech, transcription, video processing, photo organization |
| **Data** | `cc-vault`, `cc-youtube-info`, `cc-personresearch`, `cc-docgen` | Personal vault (contacts/tasks/goals), YouTube transcripts, person research, C4 diagrams |
| **System** | `cc-hardware`, `cc-comm-queue`, `cc-director-setup` | Hardware info, communication approval queue, installer/updater |

All tools work standalone from the command line and are also designed to be called by Claude Code during sessions.

## Architecture

The main app is an Avalonia desktop application (`src/CcDirector.Avalonia`) backed by a cross-platform core (`src/CcDirector.Core`), a Gateway HTTP API, and an embedded terminal stack. The full solution lives in `cc-director.sln`.

**How it works:**

1. DevThrottle spawns Claude Code with a pseudo-terminal (ConPTY on Windows, PTY on Mac/Linux)
2. A relay script is installed as a Claude Code hook — it forwards hook events (JSON) over IPC
3. An IPC server inside DevThrottle receives events, routes them to the correct session, and updates the activity state
4. The UI reflects state changes in real-time via data binding

```
                          Windows                              Mac/Linux
                          -------                              ---------
Claude Code ──hook──▶ PowerShell relay               Python relay script
                            │                                   │
                      Named pipe                         Unix domain socket
                      (CC_ClaudeDirector)              (~/.cc_director/director.sock)
                            │                                   │
                            └──────────────┬────────────────────┘
                                           ▼
                                     DevThrottle
                                           │
                               ┌───────────┴───────────┐
                           EventRouter          Session UI
                         (maps session_id)    (activity colors,
                                                status badges)
```

## Requirements

- **Windows 10/11** or **macOS (Apple Silicon)**
- A paid Claude plan and the [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) installed and on PATH
- .NET 10 SDK (only needed if building from source; the pre-built downloads are self-contained)
- **Windows only:** Windows Console Host as the default terminal (not Windows Terminal — a warning dialog will guide you if needed)

## Building

```bash
dotnet build src/CcDirector.Avalonia/CcDirector.Avalonia.csproj
```

## Running

```bash
dotnet run --project src/CcDirector.Avalonia/CcDirector.Avalonia.csproj
```

Or open `cc-director.sln` in Visual Studio and run the `CcDirector.Avalonia` project.

## Running Tests

```bash
dotnet test cc-director.sln
```

## Configuration

The Avalonia app loads `appsettings.json` from the same directory as the executable. The setup wizard writes a working default; the most useful settings are:

- **Agent.ClaudePath** — path to the `claude` executable (default: `"claude"`)
- **Agent.DefaultClaudeArgs** — CLI arguments passed to each session (default: `"--dangerously-skip-permissions"`)

Session state, logs, vault data, and tool config live under `%LOCALAPPDATA%\cc-director\` (override with the `CC_DIRECTOR_ROOT` environment variable).

## Platform Support

DevThrottle ships builds for **Windows 10/11** and **macOS (Apple Silicon)**. The core backend (`CcDirector.Core`) and the Avalonia UI are a single cross-platform codebase; only the platform-specific plumbing differs:

| Component | Windows | macOS |
|-----------|---------|-------|
| Terminal backend | ConPTY | Unix PTY (openpty) |
| IPC for hooks | Named pipes | Unix domain sockets |
| Hook relay | PowerShell | Python |
| UI | Avalonia | Avalonia (same codebase) |

**Current macOS limitations:**

- **Apple Silicon only** — there is no Intel (x64) macOS build yet.
- **The macOS apps are not code-signed**, so Gatekeeper quarantines them on first launch (the installer/updater strips the quarantine flag from what it places).
- **The embedded native console** (`SessionBackendType.Embedded`) is Windows-only; macOS uses the cross-platform terminal.
- **The Gateway role is Windows-only** — macOS installs are Workstation-only (the Director app + the `cc-*` CLI tools). There is no Gateway service or Cockpit on macOS.

Linux builds from source but is not yet packaged as a release.

## Stay Updated

This project is actively developed. To stay in the loop:

- **Watch this repo** (click "Watch" at the top) to get notified of new releases
- **Join the [Discussions](https://github.com/thefrederiksen/devthrottle/discussions)** to ask questions, share your setup, or request features
- **Follow along** at [sorenfrederiksen.com](https://sorenfrederiksen.com)

## License

MIT

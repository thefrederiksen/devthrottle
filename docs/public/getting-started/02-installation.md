# Installation

DevThrottle runs on Windows and macOS (Apple Silicon) and requires a few prerequisites. This guide walks you through getting everything set up.

## Prerequisites

The DevThrottle **Setup** app checks for these on its Prerequisites screen. Four are required; Brave is optional. Each tool below has a setup section with the exact install command and how to confirm it is on your `PATH`.

| Tool | Required? | Minimum |
|------|-----------|---------|
| [.NET 10 Runtime](#net-10-runtime) | Required | 10.0 |
| [Claude Code](#claude-code) | Required | latest |
| [Python](#python) | Required | 3.11+ |
| [Node.js](#nodejs) | Required | 20+ |
| [Brave Browser](#brave-browser-optional) | Optional | latest |
| [Tailscale](#tailscale-optional--remote-access) | Optional | latest |

> **Just installed one of these and Setup still says "Not found"?** See [If a tool is not detected after installing it](#if-a-tool-is-not-detected-after-installing-it).

### .NET 10 Runtime

The Director, Gateway, and Cockpit are .NET 10 apps. They are shipped framework-dependent (small downloads), so the **ASP.NET Core Runtime 10** must be present on the machine.

- **Windows:** the Setup app detects it and offers **Install automatically** (runs `winget install Microsoft.DotNet.AspNetCore.10`). Or install it yourself from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).
- **macOS:** the macOS Director app is self-contained and does **not** require a separate .NET install.

Confirm: `dotnet --list-runtimes` includes a `Microsoft.AspNetCore.App 10.x` line.

### Claude Code

The Anthropic CLI. Use the official **native installer** -- **do not use `npm`**, which is the usual cause of "`claude` command not found" and PATH problems.

- **Windows (PowerShell):** `irm https://claude.ai/install.ps1 | iex`
- **macOS / Linux:** `curl -fsSL https://claude.ai/install.sh | bash`

Then run `claude` once to sign in (requires a paid Claude plan -- Pro, Max, Team, or Enterprise).

Confirm: open a **new** terminal and run `claude --version`. More options: [Anthropic's setup guide](https://code.claude.com/docs/en/setup).

### Python

Python 3.11 or higher (used by several cc-* tools and MCP servers).

- **Windows:** download from [python.org/downloads](https://www.python.org/downloads/) and **check "Add python.exe to PATH"** in the installer, or run `winget install Python.Python.3.12`.
- **macOS:** `brew install python@3.12` (or download from python.org).

Confirm: `python --version` (on macOS, `python3 --version`) prints `Python 3.11+`.

### Node.js

Node.js 20 or higher (MCP servers and browser tools).

- **Windows:** download the LTS installer from [nodejs.org](https://nodejs.org/), or run `winget install OpenJS.NodeJS.LTS`.
- **macOS:** `brew install node` (or download from nodejs.org).

Confirm: `node --version` prints `v20+`.

### Brave Browser (optional)

Brave is the browser engine for `cc-browser` and related tools (Chrome stable blocks the extensions they rely on). **It is optional** -- if you have Claude Code, Python, and Node.js, Setup lets you install without it. You can add Brave later and the browser tools will pick it up.

- Download from [brave.com/download](https://brave.com/download/).

### Tailscale (optional -- remote access)

Tailscale is what lets a Gateway or the Cockpit on **another machine** (or your phone) reach the Directors on this one. **It is optional for local-only use** -- a Director without Tailscale works normally on its own machine; it just will not appear on a remote Gateway.

- **Windows:** `winget install tailscale.Tailscale`, then log into your tailnet from the tray icon.
- The Setup app checks three things and tells you exactly which one is missing: the CLI is installed, the daemon is running and logged in, and the machine has a MagicDNS name.
- One-time per tailnet (not per machine): **MagicDNS** and **HTTPS certificates** must be enabled in the [Tailscale admin console](https://login.tailscale.com/admin/dns) under DNS.

See [Multi-Machine Setup](#multi-machine-setup-remote-access) for how this fits together.

### If a tool is not detected after installing it

Programs read your `PATH` **once at launch**. If you install a prerequisite (or fix your `PATH`) **while the Setup app is already open**:

1. Click **Re-check** on the Prerequisites screen. Recent Setup builds re-read your live `PATH` from the registry, so a just-installed tool should now show **Found**.
2. If it still shows **Not found**, close and reopen the Setup app -- it will pick up the new `PATH` on the next launch.
3. Still missing? Open a **brand-new terminal** and run the tool's confirm command above (e.g. `claude --version`). If that also fails, the tool is not actually on your `PATH` yet -- re-run its installer and make sure any "Add to PATH" option is selected.

### Optional (for specific tools)

| Requirement | Needed for |
|-------------|------------|
| FFmpeg | cc-transcribe, cc-video |
| Graphviz | cc-docgen (C4 diagrams) |
| Playwright browsers | cc-browser, cc-reddit, cc-crawl4ai |
| OpenAI API key | cc-image, cc-voice, cc-whisper, cc-computer, cc-transcribe, cc-photos |
| Google OAuth credentials | cc-gmail |
| Azure App Registration | cc-outlook |

## Install CC Tools

The fastest way to get the CLI tools is with the installer:

```bash
cc-devthrottle setup install
```

This downloads all tools from GitHub releases, places them in `%LOCALAPPDATA%\cc-director\bin\`, and adds them to your PATH. No admin privileges required.

### macOS

On macOS, use the **DevThrottle Setup** app instead: download `devthrottle-setup-mac-arm64.zip` from the [latest release](https://github.com/thefrederiksen/devthrottle/releases/latest), unzip it, and right-click -> Open (it is ad-hoc-signed, so Gatekeeper asks once). The wizard installs the Director to `~/Applications`, installs every `cc-*` tool into one shared Python environment under `~/Library/Application Support/cc-director`, and symlinks the tools into `~/.local/bin` (added to your shell `PATH`). Apple Silicon only; Workstation-only (no Gateway on macOS).

### Verify installation

After installation, open a new terminal and verify:

```bash
cc-markdown --version
cc-excel --version
cc-hardware
```

## Install the Desktop Engine

Clone the repository and build:

```bash
git clone https://github.com/thefrederiksen/devthrottle.git
cd cc-director
dotnet build src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

Run the application:

```bash
dotnet run --project src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

## Configure Claude Code Skills

DevThrottle includes Claude Code skills that extend what Claude can do. After cloning, the skills in `.claude/skills/` are automatically available when you run Claude Code from the repository directory.

Key skills:
- `/commit` -- create commits following project standards
- `/review-code` -- security and PII review before commits
- `/update-docs` -- keep documentation in sync with code changes

## Setting Up Email Tools

### Outlook (cc-outlook)

1. Create an Azure App Registration with Mail.Read and Mail.Send permissions
2. Configure the tool:

```bash
cc-outlook accounts add your@email.com --client-id YOUR_CLIENT_ID
cc-outlook auth
```

3. Follow the device code flow to authenticate

### Gmail (cc-gmail)

1. Create OAuth credentials in Google Cloud Console
2. Configure the tool:

```bash
cc-gmail accounts add personal --default
cc-gmail auth
```

## Setting Up Browser Automation

Install Playwright browsers (needed for cc-browser, cc-reddit):

```bash
npx playwright install chromium
```

## Environment Variables

Set the OpenAI API key for AI-powered tools:

```bash
set OPENAI_API_KEY=your-key-here
```

Or add it permanently through Windows System Properties > Environment Variables.

## Multi-Machine Setup (Remote Access)

One Gateway machine runs the fleet view (the Cockpit); every other machine just runs Directors that show up there. Adding a new machine to the fleet is three steps:

1. **Install Tailscale** and log into the same tailnet (`winget install tailscale.Tailscale`, then sign in from the tray icon).
2. **Install DevThrottle** (Workstation role) with the Setup app or `cc-director-setup-cli install`.
3. **Set the Gateway URL** in the Director's Settings (or `gateway.url` in config.json), pointing at the Gateway machine, e.g. `https://your-gateway.your-tailnet.ts.net`.

That is all. The Director registers itself with the Gateway, opens its own Tailscale Serve front door for remote access, and verifies its advertised address actually answers before registering -- there are no manual `tailscale serve` commands and no firewall rules to add.

### How it works (so the troubleshooting below makes sense)

A Director listens on `localhost` only; the single remote path to it is a Tailscale Serve HTTPS mapping on its **own** machine, which each Director now provisions and self-heals for itself. The Director also refuses to register an address that does not demonstrably answer, so a misconfigured machine produces one precise error in its own log instead of a silently dead entry in the fleet.

### Troubleshooting

| Symptom | Meaning | Fix |
|---------|---------|-----|
| Cockpit: "endpoint never answered since registration -- check Tailscale Serve / the Director log on MACHINE" | The Director's machine never opened its HTTPS front door. | On that machine, check the Director log for the exact reason (see rows below); usually Tailscale is missing, logged out, or HTTPS certs are not enabled for the tailnet. |
| Director log: "tailscale CLI not found" | Tailscale is not installed on the Director's machine. | `winget install tailscale.Tailscale`, log in, restart the Director (or wait -- it retries automatically). |
| Director log: "tailscale serve --https=PORT failed: ..." | The serve command itself failed; the CLI output is included verbatim. | Most common: HTTPS certificates are not enabled for the tailnet -- enable them in the admin console under DNS -> HTTPS Certificates. |
| Director log: "NOT registering ... healthz probe timed out" | The mapping exists (or just got created) but the address does not answer yet. | First-ever serve on a machine can take seconds to get its TLS certificate; the Director retries with backoff and registers when it answers. If it never clears, check `tailscale serve status` on that machine. |
| Cockpit: "unreachable (timeout; cooling down)" | The Director WAS reachable before and went dark (machine asleep, Tailscale down, process gone). | Wake the machine / check Tailscale connectivity; the Gateway re-probes automatically. |
| Setup app: Tailscale row shows a failing check | Detection-only preflight: CLI missing, daemon stopped/logged out, or no MagicDNS name. | The row text contains the exact command to run; local-only use is unaffected. |

## Next Steps

- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools

# Installation

CC Director runs on Windows and macOS (Apple Silicon) and requires a few prerequisites. This guide walks you through getting everything set up.

## Prerequisites

The CC Director **Setup** app checks for these on its Prerequisites screen. Three are required; Brave is optional. Each tool below has a setup section with the exact install command and how to confirm it is on your `PATH`.

| Tool | Required? | Minimum |
|------|-----------|---------|
| [Claude Code](#claude-code) | Required | latest |
| [Python](#python) | Required | 3.11+ |
| [Node.js](#nodejs) | Required | 20+ |
| [Brave Browser](#brave-browser-optional) | Optional | latest |

> **Just installed one of these and Setup still says "Not found"?** See [If a tool is not detected after installing it](#if-a-tool-is-not-detected-after-installing-it).

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
cc-setup
```

This downloads all tools from GitHub releases, places them in `%LOCALAPPDATA%\cc-director\bin\`, and adds them to your PATH. No admin privileges required.

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
git clone https://github.com/cc-director/cc-director.git
cd cc-director
dotnet build src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

Run the application:

```bash
dotnet run --project src/CcDirector.Wpf/CcDirector.Wpf.csproj
```

## Configure Claude Code Skills

CC Director includes Claude Code skills that extend what Claude can do. After cloning, the skills in `.claude/skills/` are automatically available when you run Claude Code from the repository directory.

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

## Next Steps

- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools

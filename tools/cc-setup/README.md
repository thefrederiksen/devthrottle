# cc-setup

Windows installer for the cc-director tools suite. Downloads tools from GitHub releases, configures PATH, and installs Claude Code skill integration.

## Usage

```bash
cc-director-setup
```

No arguments required. Runs a 5-step automated installation.

## Installation Steps

1. **Create install directory** - `%LOCALAPPDATA%\cc-director`
2. **Check latest release** - Fetches from GitHub API
3. **Download tools** - Downloads executables from release assets
4. **Add to PATH** - Modifies Windows user PATH via registry
5. **Install Claude Code skill** - Downloads SKILL.md to `~/.claude/skills/cc-director/`

## What It Does NOT Do

- Does not install Python, Node.js, or .NET runtimes
- Does not configure API keys or OAuth credentials
- Does not install FFmpeg, Playwright, or Graphviz
- Does not require admin privileges (installs to user directory)

## Output

```
============================================================
  cc-director Setup
  https://github.com/thefrederiksen/devthrottle
============================================================

[1/5] Creating install directory...
[2/5] Checking for latest release...
[3/5] Downloading tools...
[4/5] Configuring PATH...
[5/5] Installing Claude Code skill...

============================================================
  Installation complete!
  Restart your terminal to use cc-director tools.
============================================================
```

## Error Handling

- Continues on individual tool download failures (with warning)
- Skips PATH modification if already configured
- Warns if Claude Code skill download fails but continues
- Safe to run multiple times (idempotent)

## Dependencies

Runtime: Python stdlib only (urllib, winreg, pathlib, ctypes)

## Install Directory

```
%LOCALAPPDATA%\cc-director\
  bin\
    cc-markdown.exe
    cc-transcribe.exe
    cc-image.exe
    ...
```

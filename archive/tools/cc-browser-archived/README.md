# cc-browser

Fast browser automation daemon for Claude Code. Extracted from OpenClaw's browser module with unwanted dependencies removed.

## Architecture

```
Claude Code (Bash tool)
     |
     v
cc-browser CLI (Node.js)
     | HTTP (localhost:9280)
     v
cc-browser Daemon (keeps browser connection warm)
     | CDP (localhost:9222)
     v
Chrome/Edge
```

## Why This Exists

- **No MCP overhead** - Direct CLI, no MCP protocol latency
- **Persistent connection** - Daemon keeps Playwright/CDP connection warm
- **Fast commands** - ~20-50ms per command vs 500-800ms for cold start
- **Uses existing logins** - Isolated workspace but real Chrome
- **No dependencies on pi-agent-core** - Standalone module

## Quick Start

```bash
# Terminal 1: Start the daemon (keeps running)
cd cc-browser
node src/daemon.mjs

# Terminal 2: Send commands
node src/cli.mjs start                           # Launch Chrome
node src/cli.mjs navigate --url "https://example.com"
node src/cli.mjs snapshot --interactive          # Get element refs
node src/cli.mjs click --ref e1                  # Click element
node src/cli.mjs type --ref e2 --text "hello"    # Type into element
node src/cli.mjs screenshot                      # Take screenshot
node src/cli.mjs stop                            # Close browser
```

## Commands

### Daemon
| Command | Description |
|---------|-------------|
| `daemon` | Start the daemon (foreground) |
| `status` | Check daemon and browser status |

### Browser Lifecycle
| Command | Description |
|---------|-------------|
| `browsers` | List available browsers |
| `profiles [--browser edge]` | List Chrome/Edge built-in profiles with emails |
| `workspaces` | List configured cc-browser workspaces |
| `favorites --workspace work` | Get favorites from workspace.json |
| `start [--headless]` | Launch with isolated workspace |
| `start --browser edge` | Launch Edge instead of Chrome |
| `start --workspace acmeflow` | Launch using workspace alias |
| `start --browser chrome --workspace personal` | Launch Chrome with named workspace |
| `start --profileDir "Profile 1"` | Use existing Chrome profile (requires Chrome closed) |
| `stop` | Close browser |

### Navigation
| Command | Description |
|---------|-------------|
| `navigate --url <url>` | Go to URL |
| `reload` | Reload page |
| `back` | Go back |
| `forward` | Go forward |

### Page Inspection
| Command | Description |
|---------|-------------|
| `snapshot [--interactive]` | Get page structure with element refs |
| `info` | Get URL, title, viewport |
| `text [--selector <css>]` | Get page text |
| `html [--selector <css>]` | Get page HTML |

### Interactions
| Command | Description |
|---------|-------------|
| `click --ref <e1>` | Click element |
| `type --ref <e1> --text "hi"` | Type into element |
| `press --key Enter` | Press keyboard key |
| `hover --ref <e1>` | Hover over element |
| `select --ref <e1> --value "opt"` | Select dropdown |
| `scroll [--direction down]` | Scroll viewport |
| `scroll --ref <e1>` | Scroll element into view |

### Screenshots
| Command | Description |
|---------|-------------|
| `screenshot [--fullPage]` | Take screenshot (base64) |
| `screenshot-labels` | Screenshot with element labels |

### Tabs
| Command | Description |
|---------|-------------|
| `tabs` | List all tabs |
| `tabs-open [--url <url>]` | Open new tab |
| `tabs-close --tab <id>` | Close tab |
| `tabs-focus --tab <id>` | Focus tab |

### Advanced
| Command | Description |
|---------|-------------|
| `wait --text "loaded"` | Wait for text |
| `wait --time 1000` | Wait for time (ms) |
| `evaluate --js "document.title"` | Run JavaScript |
| `fill --fields '[...]'` | Fill multiple form fields |
| `upload --ref <e1> --path <file>` | Upload file |

## Element Refs

The `snapshot` command returns element refs (e1, e2, etc.) that you can use in subsequent commands:

```bash
# Get the page structure
node src/cli.mjs snapshot --interactive
# Output: - button "Sign In" [ref=e1]
#         - textbox "Email" [ref=e2]

# Use the refs
node src/cli.mjs type --ref e2 --text "user@example.com"
node src/cli.mjs click --ref e1
```

## Options

| Option | Description |
|--------|-------------|
| `--port <port>` | Daemon port (default: 9280) |
| `--cdpPort <port>` | Chrome CDP port (default: 9222) |
| `--workspace <name>` | Named workspace for isolated sessions |
| `--tab <targetId>` | Target specific tab |
| `--timeout <ms>` | Action timeout |

## Files

```
cc-browser/
  src/
    daemon.mjs        # HTTP server
    cli.mjs           # CLI client
    main.mjs          # Combined CLI+daemon for .exe
    session.mjs       # Playwright connection & page state
    interactions.mjs  # Click, type, hover, drag, etc.
    snapshot.mjs      # Page snapshots & ref generation
    chrome.mjs        # Chrome detection & launch
  package.json
  cc-browser.cmd      # Windows launcher
```

## Workspace Configuration

Workspaces provide isolated browser sessions with persistent logins. Each workspace has its own data directory and port configuration.

### Workspace Location

```
%LOCALAPPDATA%\cc-browser\{browser}-{workspace}\workspace.json
```

### Configured Workspaces

| Workspace | Browser | Daemon Port | CDP Port | Purpose |
|-----------|---------|-------------|----------|---------|
| edge-work | Edge | 9280 | 9222 | Work accounts |
| chrome-work | Chrome | 9281 | 9223 | Work accounts |
| chrome-personal | Chrome | 9282 | 9224 | Personal accounts |

### Workspace JSON Structure

```json
{
  "name": "Edge Work",
  "browser": "edge",
  "workspace": "work",
  "cdpPort": 9222,
  "daemonPort": 9280,
  "purpose": "Work accounts",
  "aliases": ["acmeflow", "work"],
  "favorites": ["https://example.com"],
  "accounts": []
}
```

### Multi-Workspace Usage

Run multiple browser workspaces simultaneously, each on its own ports:

```bash
# Start daemons (each in separate terminal)
cc-browser daemon --workspace acmeflow                     # Port 9280
cc-browser daemon --browser chrome --workspace work       # Port 9281
cc-browser daemon --browser chrome --workspace personal   # Port 9282

# Commands auto-detect daemon port from workspace.json
cc-browser start --workspace acmeflow
cc-browser start --browser chrome --workspace work
cc-browser start --browser chrome --workspace personal
```

### How It Works

1. **Daemon reads workspace config** - Uses `daemonPort` and `cdpPort` from workspace.json
2. **CLI auto-discovers daemon port** - Reads workspace.json to find which port the daemon is on
3. **No explicit --port flag needed** - Just specify `--workspace`

### Creating a New Workspace

1. Create the workspace directory:
   ```bash
   mkdir %LOCALAPPDATA%\cc-browser\chrome-test
   ```

2. Create workspace.json with unique ports:
   ```json
   {
     "name": "Chrome Test",
     "browser": "chrome",
     "workspace": "test",
     "cdpPort": 9225,
     "daemonPort": 9283,
     "purpose": "Testing",
     "aliases": ["test"],
     "favorites": [],
     "accounts": []
   }
   ```

3. Start the daemon:
   ```bash
   cc-browser daemon --browser chrome --workspace test
   ```

## Ports

| Port | Purpose |
|------|---------|
| 9280 | Daemon HTTP API (edge-work) |
| 9281 | Daemon HTTP API (chrome-work) |
| 9282 | Daemon HTTP API (chrome-personal) |
| 9222 | Chrome CDP (edge-work) |
| 9223 | Chrome CDP (chrome-work) |
| 9224 | Chrome CDP (chrome-personal) |

## Build and Deploy

cc-browser is a Node.js tool. It is NOT compiled to a standalone exe -- it is deployed as source files + node_modules with a `.cmd` launcher script.

### Build Process

**ALWAYS build before deploying. Never copy source files directly to the install location.**

```bash
# From the cc-browser directory:
cd tools/cc-browser

# Step 1: Build (creates dist/ with source, node_modules, and launcher)
powershell -ExecutionPolicy Bypass -File build.ps1

# Step 2: Verify dist/ contents
ls dist/
# Expected: cc-browser.cmd, package.json, README.md, src/, node_modules/
```

### Deploy (via master build script)

The master build script handles building AND deploying all tools:

```bash
# From repo root -- builds ALL tools including cc-browser:
cd <repo-root>
scripts\build-tools.bat
```

This runs `build.ps1` then copies `dist/` contents to `%LOCALAPPDATA%\cc-director\bin\_cc-browser\`.

### Build-only (cc-browser individually)

To build cc-browser without deploying:

```bash
cd tools/cc-browser
powershell -ExecutionPolicy Bypass -File build.ps1
```

Then manually copy from `dist/` to the install location if needed.

### What Gets Deployed

```
%LOCALAPPDATA%\cc-director\bin\
  cc-browser.cmd                    # Windows launcher: @node "%~dp0_cc-browser\src\cli.mjs" %*
  cc-browser                        # Git Bash launcher (used by Claude Code)
  _cc-browser\                      # Underscore prefix avoids file/dir name collision
    package.json
    README.md
    src\
      cli.mjs
      daemon.mjs
      main.mjs
      session.mjs
      sessions.mjs
      interactions.mjs
      snapshot.mjs
      chrome.mjs
      captcha.mjs
      human-mode.mjs
      vision.mjs
    node_modules\
      playwright-core\              # Only runtime dependency
```

The subdirectory uses an underscore prefix (`_cc-browser`) because Windows cannot have
a file and directory with the same name. The `.cmd` launcher serves CMD/PowerShell
(which resolves `.cmd` via PATHEXT), while the extensionless launcher serves Git Bash
(which Claude Code uses as its shell).

### deploy.mjs (DEV SHORTCUT -- DO NOT USE FOR RELEASES)

The `deploy.mjs` script copies source files directly to the install location, bypassing the build step. This exists only for rapid iteration during development. **Do not use it for releases** -- it skips `npm install` and does not create the `dist/` staging area.

### After Deploying

Restart the daemon to pick up changes:

```bash
cc-browser stop
cc-browser daemon --workspace <your-workspace>
```

## Comparison to MCP

| Aspect | Playwright MCP | cc-browser |
|--------|---------------|------------|
| Latency | 500-2000ms | 20-50ms |
| Connection | Cold start each call | Persistent |
| Browser | Launches new Chromium | Uses existing Chrome |
| Protocol | MCP + stdio | HTTP (fast) |
| Dependencies | @playwright/mcp | playwright-core only |

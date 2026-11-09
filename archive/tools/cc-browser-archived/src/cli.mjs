#!/usr/bin/env node
// CC Browser - CLI Client
// Fast browser automation for Claude Code
// Usage: node cli.mjs <command> [options]

import { spawn } from 'child_process';
import { dirname, join, resolve, extname } from 'path';
import { fileURLToPath } from 'url';
import { readFileSync, existsSync, readdirSync, writeFileSync, mkdirSync } from 'fs';
import { saveRecording, findRecording, listRecordings } from './recordings.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;
const DEFAULT_CDP_PORT = 9222;

// Lockfile path for daemon port auto-detection
function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(process.env.HOME || '', 'AppData', 'Local');
  return join(localAppData, 'cc-browser', 'daemon.lock');
}

// Read lockfile to get running daemon port
function readLockfile() {
  const lockPath = getLockfilePath();
  if (!existsSync(lockPath)) {
    return null;
  }
  try {
    const content = readFileSync(lockPath, 'utf8');
    return JSON.parse(content);
  } catch (err) {
    console.error(`[DEBUG] Failed to read lockfile: ${err.message}`);
    return null;
  }
}

// Get workspace directory path
function getWorkspaceDir(browser, workspace) {
  const localAppData = process.env.LOCALAPPDATA || join(process.env.HOME || '', 'AppData', 'Local');
  return join(localAppData, 'cc-browser', `${browser}-${workspace}`);
}

// Read workspace.json configuration
function readWorkspaceConfig(browser, workspace) {
  const workspaceDir = getWorkspaceDir(browser, workspace);
  const configPath = join(workspaceDir, 'workspace.json');

  if (!existsSync(configPath)) {
    return null;
  }

  const content = readFileSync(configPath, 'utf8');
  return JSON.parse(content);
}

// Resolve alias to browser+workspace
function resolveAlias(alias) {
  const localAppData = process.env.LOCALAPPDATA || join(process.env.HOME || '', 'AppData', 'Local');
  const ccBrowserDir = join(localAppData, 'cc-browser');

  if (!existsSync(ccBrowserDir)) return null;

  const dirs = readdirSync(ccBrowserDir);
  for (const dir of dirs) {
    const configPath = join(ccBrowserDir, dir, 'workspace.json');
    if (existsSync(configPath)) {
      const config = JSON.parse(readFileSync(configPath, 'utf8'));
      if (config.aliases && config.aliases.includes(alias)) {
        const [browser, ...workspaceParts] = dir.split('-');
        const workspace = workspaceParts.join('-');
        return { browser, workspace, config };
      }
    }
  }
  return null;
}

// Get daemon port from workspace.json, lockfile, or default
function getDaemonPort(args) {
  // If port is explicitly provided, use it
  if (args.port) {
    console.error(`[DEBUG] Using explicit port: ${args.port}`);
    return args.port;
  }

  // If browser and workspace are specified, try to read from workspace.json
  if (args.browser && args.workspace) {
    console.error(`[DEBUG] Looking for ${args.browser}-${args.workspace} config`);
    const config = readWorkspaceConfig(args.browser, args.workspace);
    if (config && config.daemonPort) {
      console.error(`[DEBUG] Found daemonPort: ${config.daemonPort}`);
      return config.daemonPort;
    }
    console.error(`[DEBUG] No config or daemonPort found`);
  } else if (args.workspace && !args.browser) {
    // Workspace specified without browser - try to resolve as alias
    console.error(`[DEBUG] Resolving alias: ${args.workspace}`);
    const resolved = resolveAlias(args.workspace);
    if (resolved) {
      console.error(`[DEBUG] Alias resolved: ${resolved.browser}-${resolved.workspace}`);
      // Update args with resolved browser/workspace for downstream use
      args.browser = resolved.browser;
      args.workspace = resolved.workspace;
      if (resolved.config.daemonPort) {
        console.error(`[DEBUG] Found daemonPort: ${resolved.config.daemonPort}`);
        return resolved.config.daemonPort;
      }
    }
    console.error(`[DEBUG] Alias not found: ${args.workspace}`);
  } else {
    // No browser/workspace specified - check lockfile for running daemon
    console.error(`[DEBUG] No browser/workspace specified, checking lockfile...`);
    const lockData = readLockfile();
    if (lockData && lockData.port) {
      console.error(`[DEBUG] Found running daemon via lockfile: port=${lockData.port}, browser=${lockData.browser}, workspace=${lockData.workspace}`);
      return lockData.port;
    }
    console.error(`[DEBUG] No lockfile found`);
  }

  console.error(`[DEBUG] Using default port: ${DEFAULT_DAEMON_PORT}`);
  return DEFAULT_DAEMON_PORT;
}

// ---------------------------------------------------------------------------
// Argument Parser
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const args = { _: [] };
  const tokens = argv.slice(2);

  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token.startsWith('--')) {
      const key = token.slice(2);
      const next = tokens[i + 1];
      if (next && !next.startsWith('--')) {
        // Try to parse as JSON for complex values (arrays, objects)
        // If not valid JSON, use as plain string - this is intentional
        try {
          args[key] = JSON.parse(next);
        } catch (_parseError) {
          args[key] = next;
        }
        i++;
      } else {
        args[key] = true;
      }
    } else {
      args._.push(token);
    }
  }

  return args;
}

// ---------------------------------------------------------------------------
// HTTP Client
// ---------------------------------------------------------------------------

async function request(method, path, body, port = DEFAULT_DAEMON_PORT, timeoutMs = 60000) {
  const url = `http://127.0.0.1:${port}${path}`;
  console.error(`[DEBUG] Request: ${method} ${url}`);

  try {
    const res = await fetch(url, {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(timeoutMs),
    });

    const data = await res.json();
    return data;
  } catch (err) {
    if (err.code === 'ECONNREFUSED' || err.cause?.code === 'ECONNREFUSED') {
      return {
        success: false,
        error: `Daemon not running on port ${port}. Start it with: cc-browser daemon`,
      };
    }
    throw err;
  }
}

// ---------------------------------------------------------------------------
// Output Helpers
// ---------------------------------------------------------------------------

function output(data) {
  console.log(JSON.stringify(data, null, 2));
}

function outputError(message) {
  console.error(JSON.stringify({ success: false, error: message }, null, 2));
  process.exit(1);
}

// ---------------------------------------------------------------------------
// Per-Command Help
// ---------------------------------------------------------------------------

const commandHelp = {
  daemon: `Usage: cc-browser daemon [options]

  Start the background daemon that manages browser connections.

  Options:
    --port <port>       Daemon HTTP port (default: from workspace.json or 9280)
    --browser <name>    Browser: chrome, edge
    --workspace <name>  Named workspace for isolated sessions

  Examples:
    cc-browser daemon
    cc-browser daemon --workspace acmeflow
    cc-browser daemon --browser chrome --workspace work`,

  status: `Usage: cc-browser status [options]

  Check daemon and browser connection status.

  Options:
    --port <port>       Daemon port
    --workspace <name>  Target workspace`,

  browsers: `Usage: cc-browser browsers

  List available browsers detected on this system.`,

  profiles: `Usage: cc-browser profiles [options]

  List system Chrome/Edge built-in profiles (with emails).

  Options:
    --browser <name>    Browser to list profiles for (default: chrome)`,

  workspaces: `Usage: cc-browser workspaces

  List all configured cc-browser workspaces with their ports and aliases.`,

  favorites: `Usage: cc-browser favorites --workspace <name> [options]

  Get favorites/bookmarks from workspace.json configuration.

  Options:
    --workspace <name>  Workspace name (required)
    --browser <name>    Browser (default: edge)`,

  start: `Usage: cc-browser start [options]

  Launch a browser instance and connect to it.

  Options:
    --browser <name>        Browser: chrome, edge
    --workspace <name>      Named workspace (persists logins, isolated sessions)
    --incognito             Launch in incognito mode (temp profile, no saved data)
    --profileDir <dir>      Use existing system Chrome profile (e.g., "Default", "Profile 1")
    --cdpPort <port>        Chrome CDP port (default: from workspace.json or 9222)
    --headless              Start in headless mode
    --no-indicator          Hide the automation info bar
    --mode <mode>           Initial mode: fast, human, stealth

  Note: --incognito cannot be used with --workspace.

  Examples:
    cc-browser start --workspace acmeflow
    cc-browser start --browser chrome --workspace personal
    cc-browser start --incognito
    cc-browser start --profileDir "Default"
    cc-browser start --browser edge --workspace work --mode human`,

  stop: `Usage: cc-browser stop [options]

  Stop the browser and disconnect.

  Options:
    --browser <name>    Browser filter
    --workspace <name>  Workspace filter`,

  navigate: `Usage: cc-browser navigate --url <url> [options]

  Navigate to a URL.

  Options:
    --url <url>          URL to navigate to (required)
    --tab <targetId>     Target specific tab
    --waitUntil <event>  Wait until: load, domcontentloaded, networkidle
    --timeout <ms>       Navigation timeout (default: 30000)

  Examples:
    cc-browser navigate --url "https://example.com"
    cc-browser navigate --url "https://app.com" --waitUntil networkidle`,

  reload: `Usage: cc-browser reload [options]

  Reload the current page.

  Options:
    --tab <targetId>     Target specific tab
    --waitUntil <event>  Wait until: load, domcontentloaded, networkidle
    --timeout <ms>       Reload timeout`,

  back: `Usage: cc-browser back [options]

  Go back in browser history.

  Options:
    --tab <targetId>    Target specific tab`,

  forward: `Usage: cc-browser forward [options]

  Go forward in browser history.

  Options:
    --tab <targetId>    Target specific tab`,

  snapshot: `Usage: cc-browser snapshot [options]

  Get page structure with element refs (e1, e2, etc.) for interactions.

  Options:
    --interactive       Only show interactive elements (buttons, links, inputs)
    --compact           Compact output (fewer structural elements)
    --maxDepth <n>      Maximum tree depth
    --maxChars <n>      Maximum output characters
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser snapshot
    cc-browser snapshot --interactive
    cc-browser snapshot --compact --maxDepth 5`,

  info: `Usage: cc-browser info [options]

  Get page URL, title, and viewport information.

  Options:
    --tab <targetId>    Target specific tab`,

  click: `Usage: cc-browser click <--ref | --text | --selector> [options]

  Click an element on the page.

  Target (exactly one required):
    --ref <ref>          Element reference from snapshot (e.g., e1, e5)
    --text <string>      Click element containing this text
    --selector <css>     Click element matching CSS selector

  Options:
    --exact              With --text, require exact text match (not substring)
    --double             Double-click instead of single click
    --button <btn>       Mouse button: left, right, middle (default: left)
    --modifiers <json>   Keyboard modifiers: ["Control", "Shift", "Alt"]
    --timeout <ms>       Wait timeout (default: 8000, range: 500-60000)
    --tab <targetId>     Target specific tab

  Examples:
    cc-browser click --ref e5
    cc-browser click --text "CaseAttributes.sql"
    cc-browser click --text "Submit" --exact
    cc-browser click --selector "[data-testid='save-btn']"
    cc-browser click --ref e3 --double --modifiers '["Control"]'`,

  type: `Usage: cc-browser type <--ref | --textContent | --selector> --text <text> [options]

  Type text into an input element.

  Target (exactly one required):
    --ref <ref>             Element reference from snapshot
    --textContent <string>  Target element containing this text
    --selector <css>        Target element matching CSS selector

  Options:
    --text <text>        Text to type (required)
    --exact              With --textContent, require exact text match
    --submit             Press Enter after typing
    --slowly             Type character-by-character with delays
    --timeout <ms>       Wait timeout (default: 8000)
    --tab <targetId>     Target specific tab

  Examples:
    cc-browser type --ref e4 --text "hello world"
    cc-browser type --ref e4 --text "search query" --submit
    cc-browser type --selector "#search" --text "query"`,

  press: `Usage: cc-browser press --key <key> [options]

  Press a keyboard key.

  Options:
    --key <key>         Key to press (required). Examples: Enter, Tab, Escape,
                        ArrowDown, Control+a, Shift+Enter
    --delay <ms>        Hold duration
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser press --key Enter
    cc-browser press --key "Control+a"
    cc-browser press --key Escape`,

  hover: `Usage: cc-browser hover <--ref | --text | --selector> [options]

  Hover over an element.

  Target (exactly one required):
    --ref <ref>          Element reference from snapshot
    --text <string>      Hover element containing this text
    --selector <css>     Hover element matching CSS selector

  Options:
    --exact              With --text, require exact text match
    --timeout <ms>       Wait timeout (default: 8000)
    --tab <targetId>     Target specific tab

  Examples:
    cc-browser hover --ref e3
    cc-browser hover --text "Menu Item"`,

  drag: `Usage: cc-browser drag --from <ref> --to <ref> [options]

  Drag one element to another.

  Options:
    --from <ref>        Start element ref (required)
    --to <ref>          End element ref (required)
    --timeout <ms>      Wait timeout
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser drag --from e1 --to e5`,

  select: `Usage: cc-browser select --ref <ref> --value <value> [options]

  Select an option from a dropdown.

  Options:
    --ref <ref>         Element reference (required)
    --value <value>     Option value to select
    --values <json>     Multiple values: ["opt1", "opt2"]
    --timeout <ms>      Wait timeout
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser select --ref e3 --value "option1"`,

  scroll: `Usage: cc-browser scroll [options]

  Scroll the viewport or scroll an element into view.

  Options:
    --direction <dir>   Scroll direction: up, down, left, right (default: down)
    --amount <px>       Scroll amount in pixels (default: 500)
    --ref <ref>         Scroll this element into view instead
    --timeout <ms>      Wait timeout (for ref scrolling)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser scroll
    cc-browser scroll --direction up --amount 300
    cc-browser scroll --ref e10`,

  wait: `Usage: cc-browser wait [options]

  Wait for a condition before continuing.

  Options:
    --time <ms>         Wait for fixed duration
    --text <string>     Wait for text to appear on page
    --textGone <string> Wait for text to disappear
    --selector <css>    Wait for CSS selector to be visible
    --url <pattern>     Wait for URL to match
    --loadState <state> Wait for load state: load, domcontentloaded, networkidle
    --fn <js>           Wait for JS function to return truthy
    --timeout <ms>      Maximum wait time (default: 20000)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser wait --text "Loading complete"
    cc-browser wait --time 2000
    cc-browser wait --selector ".modal" --timeout 10000`,

  evaluate: `Usage: cc-browser evaluate --js <code> [options]

  Execute JavaScript in the browser page context.

  Options:
    --js <code>         JavaScript code to execute (required)
    --fn <code>         Alias for --js
    --code <code>       Alias for --js
    --ref <ref>         Execute in context of element (receives 'el' parameter)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser evaluate --js "document.title"
    cc-browser evaluate --js "document.querySelectorAll('a').length"
    cc-browser evaluate --ref e1 --js "el => el.textContent"
    cc-browser evaluate --ref e1 --js "el => el.getBoundingClientRect()"`,

  fill: `Usage: cc-browser fill --fields <json> [options]

  Fill multiple form fields at once.

  Options:
    --fields <json>     Array of field objects (required):
                        [{"ref":"e1","type":"text","value":"hello"},
                         {"ref":"e2","type":"checkbox","value":true}]
    --timeout <ms>      Wait timeout
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser fill --fields '[{"ref":"e1","type":"text","value":"John"}]'`,

  screenshot: `Usage: cc-browser screenshot [options]

  Take a screenshot of the page or a specific element.

  Options:
    --save <path>       Save screenshot to file (PNG/JPEG)
    --ref <ref>         Screenshot specific element by ref
    --element <css>     Screenshot specific element by CSS selector
    --fullPage          Capture entire page (not just viewport)
    --type <format>     Image format: png, jpeg (default: png)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser screenshot
    cc-browser screenshot --save ./page.png
    cc-browser screenshot --ref e5 --save ./element.png
    cc-browser screenshot --fullPage --save ./full.png --type jpeg`,

  'screenshot-labels': `Usage: cc-browser screenshot-labels [options]

  Take a screenshot with numbered element labels overlaid.

  Options:
    --maxLabels <n>     Maximum number of labels
    --type <format>     Image format: png, jpeg (default: png)
    --tab <targetId>    Target specific tab`,

  upload: `Usage: cc-browser upload --ref <ref> --path <file> [options]

  Upload a file to a file input element.

  Options:
    --ref <ref>         File input element ref
    --element <css>     File input element CSS selector
    --path <file>       File path to upload
    --paths <json>      Multiple file paths: ["file1.txt", "file2.txt"]
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser upload --ref e3 --path "./photo.jpg"`,

  resize: `Usage: cc-browser resize --width <px> --height <px> [options]

  Resize the browser viewport.

  Options:
    --width <px>        Viewport width (required, min: 320)
    --height <px>       Viewport height (required, min: 240)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser resize --width 1920 --height 1080
    cc-browser resize --width 375 --height 812`,

  tabs: `Usage: cc-browser tabs

  List all open browser tabs with their targetIds, URLs, and titles.`,

  'tabs-open': `Usage: cc-browser tabs-open [--url <url>]

  Open a new browser tab.

  Options:
    --url <url>         URL to open in the new tab (default: about:blank)`,

  'tabs-close': `Usage: cc-browser tabs-close --tab <targetId>

  Close a browser tab.

  Options:
    --tab <targetId>    Target tab ID to close (required)`,

  'tabs-focus': `Usage: cc-browser tabs-focus --tab <targetId>

  Focus/activate a browser tab.

  Options:
    --tab <targetId>    Target tab ID to focus (required)`,

  text: `Usage: cc-browser text [options]

  Get the text content of the page or a specific element.

  Options:
    --selector <css>    CSS selector to limit scope
    --tab <targetId>    Target specific tab`,

  html: `Usage: cc-browser html [options]

  Get the HTML of the page or a specific element.

  Options:
    --selector <css>    CSS selector to limit scope
    --outer             Include the outer element HTML
    --tab <targetId>    Target specific tab`,

  mode: `Usage: cc-browser mode [fast|human|stealth]

  Get or set the interaction mode.

  Modes:
    fast      Instant actions, no delays (default)
    human     Human-like delays, mouse curves, typing variation
    stealth   Human mode + anti-detection (WebDriver masking, fingerprint spoofing)

  Examples:
    cc-browser mode              # Show current mode
    cc-browser mode human        # Switch to human mode
    cc-browser mode fast         # Switch to fast mode`,

  captcha: `Usage: cc-browser captcha <detect|solve> [options]

  Detect or solve CAPTCHAs on the current page.

  Subcommands:
    detect              Check if a CAPTCHA is present (DOM-based)
    solve               Attempt to solve the detected CAPTCHA

  Options:
    --vision            Also use LLM vision for detection (requires claude CLI)
    --attempts <n>      Maximum solve attempts (default: 3)
    --tab <targetId>    Target specific tab

  Examples:
    cc-browser captcha detect
    cc-browser captcha detect --vision
    cc-browser captcha solve
    cc-browser captcha solve --attempts 5`,

  session: `Usage: cc-browser session <subcommand> [options]

  Manage named tab sessions for agent cleanup.

  Subcommands:
    create              Create a new session
    list                List all active sessions
    close               Close all tabs in a session
    heartbeat           Touch session to reset TTL timer
    prune               Manually clean up expired sessions

  Options:
    --name <name>       Session name (required for create)
    --session <id>      Session ID (required for close, heartbeat)
    --ttl <ms>          Time-to-live in ms (default: 1800000 = 30 min, 0 = never)
    --metadata <json>   Optional metadata JSON

  Examples:
    cc-browser session create --name "research" --ttl 1800000
    cc-browser session list
    cc-browser session heartbeat --session sess_a7b3c9d2
    cc-browser session close --session sess_a7b3c9d2
    cc-browser session prune`,

  'tabs-close-all': `Usage: cc-browser tabs-close-all

  Close all open tabs. A blank tab is created first (Chrome requires >= 1 tab).
  Tabs are also removed from any sessions they belong to.`,

  record: `Usage: cc-browser record <start|stop|status> [options]

  Record browser interactions for later replay.
  Recordings are saved to the vault automatically.

  Subcommands:
    start               Start recording
    stop                Stop recording and save to vault
    status              Check if recording is active

  Options (stop):
    --name <name>       Name for the recording (default: "recording")
    --output <file>     Save to explicit file path instead of vault

  Examples:
    cc-browser record start
    cc-browser record stop --name "acmeflow login flow"
    cc-browser record stop --output my-flow.json`,

  recordings: `Usage: cc-browser recordings

  List all saved recordings in the vault.`,

  replay: `Usage: cc-browser replay --name <name> [options]

  Replay a previously recorded browser session.
  Finds the most recent recording matching the name from the vault.

  Options:
    --name <name>       Name of recording to replay (searches vault)
    --file <path>       Path to recording JSON file (alternative to --name)
    --mode <mode>       Replay speed: fast or human (default: current mode)
    --timeout <ms>      Per-step timeout in ms (default: 8000)

  Examples:
    cc-browser replay --name "acmeflow login flow"
    cc-browser replay --file my-flow.json --mode fast`,
};

function printCommandHelp(command) {
  const help = commandHelp[command];
  if (help) {
    console.log(help);
    return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

const commands = {
  // Help
  help: (args) => {
    // Per-command help: cc-browser help <command>
    const target = args._[1];
    if (target && printCommandHelp(target)) {
      return;
    }
    if (target) {
      console.log(`No help available for "${target}". Run 'cc-browser help' for all commands.`);
      return;
    }
    console.log(`
cc-browser - Fast browser automation for Claude Code

USAGE:
  cc-browser <command> [options]

DAEMON:
  cc-browser daemon              Start the daemon (keeps running)
  cc-browser status              Check daemon and browser status

BROWSER LIFECYCLE:
  cc-browser browsers                       List available browsers
  cc-browser profiles [--browser chrome]    List Chrome/Edge built-in profiles (with emails)
  cc-browser workspaces                     List configured cc-browser workspaces
  cc-browser favorites --workspace work     Get favorites from workspace.json
  cc-browser start                          Start with default isolated workspace
  cc-browser start --browser edge           Start Edge (default workspace)
  cc-browser start --workspace acmeflow      Start using workspace alias
  cc-browser start --browser chrome --workspace personal  Start Chrome with workspace
  cc-browser start --profileDir "Profile 1" Use existing system Chrome profile
  cc-browser start --incognito              Start in incognito mode (no saved data)
  cc-browser start --no-indicator           Start without automation info bar
  cc-browser stop                           Stop browser

NAVIGATION:
  cc-browser navigate --url <url>              Go to URL
  cc-browser reload                            Reload page
  cc-browser back                              Go back
  cc-browser forward                           Go forward

PAGE INSPECTION:
  cc-browser snapshot [--interactive]          Get page structure with element refs
  cc-browser info                              Get page URL, title, viewport
  cc-browser text [--selector <css>]           Get page text content
  cc-browser html [--selector <css>]           Get page HTML

INTERACTIONS:
  cc-browser click --ref <e1>                  Click element by ref
  cc-browser click --text "File.sql"           Click element by text content
  cc-browser click --selector ".btn"           Click element by CSS selector
  cc-browser type --ref <e1> --text "hello"    Type into element
  cc-browser press --key Enter                 Press keyboard key
  cc-browser hover --ref <e1>                  Hover over element
  cc-browser hover --text "Menu"               Hover by text content
  cc-browser select --ref <e1> --value "opt"   Select dropdown option
  cc-browser drag --from <e1> --to <e5>        Drag element to another
  cc-browser scroll [--direction down]         Scroll viewport
  cc-browser scroll --ref <e1>                 Scroll element into view
  cc-browser resize --width 1280 --height 720  Resize browser viewport

SCREENSHOTS:
  cc-browser screenshot [--fullPage]           Take screenshot (base64)
  cc-browser screenshot --save ./page.png      Save screenshot to file
  cc-browser screenshot-labels                 Screenshot with element labels

TABS:
  cc-browser tabs                              List all tabs
  cc-browser tabs-open [--url <url>]           Open new tab
  cc-browser tabs-close --tab <targetId>       Close tab
  cc-browser tabs-focus --tab <targetId>       Focus tab

MODE:
  cc-browser mode                              Show current mode
  cc-browser mode human                        Set human mode (delays + mouse curves)
  cc-browser mode fast                         Set fast mode (instant, no delays)
  cc-browser mode stealth                      Set stealth mode (human + anti-detect)
  cc-browser start --workspace x --mode human  Start with specific mode

RECORD & REPLAY:
  cc-browser record start                          Start recording interactions
  cc-browser record status                         Check if recording is active
  cc-browser record stop --output login-flow.json  Stop and save recording
  cc-browser recordings                            List saved recordings
  cc-browser replay --file login-flow.json         Replay a recording
  cc-browser replay --file flow.json --mode fast   Replay at full speed

CAPTCHA:
  cc-browser captcha detect                    Detect CAPTCHA (DOM-based)
  cc-browser captcha detect --vision           Detect with LLM vision fallback
  cc-browser captcha solve                     Auto-solve detected CAPTCHA
  cc-browser captcha solve --attempts 5        Solve with max attempts

SESSIONS:
  cc-browser session create --name <name>      Create named session (returns session ID)
  cc-browser session create --name x --ttl 60000  Create with 60s TTL
  cc-browser session list                      List all active sessions
  cc-browser session heartbeat --session <id>  Keep session alive (reset TTL timer)
  cc-browser session close --session <id>      Close all tabs in session
  cc-browser session prune                     Manually clean expired sessions
  cc-browser tabs-open --url <url> --session <id>  Open tab tracked by session
  cc-browser tabs-close-all                    Close all tabs (keeps one blank)

JAVASCRIPT:
  cc-browser evaluate --js "document.title"          Run JavaScript in page
  cc-browser evaluate --js "el => el.textContent" --ref e1   Run JS on element

ADVANCED:
  cc-browser wait --text "loaded"              Wait for text to appear
  cc-browser wait --time 1000                  Wait for time (ms)
  cc-browser fill --fields '[...]'             Fill multiple form fields
  cc-browser upload --ref <e1> --path <file>   Upload file

HELP:
  cc-browser help                              Show this help
  cc-browser help <command>                    Show help for a specific command
  cc-browser <command> --help                  Same as above

OPTIONS:
  --port <port>       Daemon port (default: 9280)
  --cdpPort <port>    Chrome CDP port (default: 9222)
  --browser <name>    Browser to use: chrome, edge
  --workspace <name>  Named workspace for isolated sessions (persists logins)
  --no-indicator      Hide the automation info bar (shown by default)
  --tab <targetId>    Target specific tab
  --timeout <ms>      Action timeout

EXAMPLES:
  # Start daemon (run in background terminal)
  cc-browser daemon

  # List available browsers and Chrome profiles
  cc-browser browsers
  cc-browser profiles                      # List system Chrome profiles
  cc-browser profiles --browser edge       # List system Edge profiles
  cc-browser workspaces                    # List cc-browser workspaces

  # Named workspaces (recommended - persistent logins, isolated sessions)
  cc-browser start --workspace acmeflow              # Use alias
  cc-browser start --browser edge --workspace work  # Edge for work
  cc-browser start --browser chrome --workspace personal  # Chrome personal

  # Simple start (default workspace)
  cc-browser start --browser edge
  cc-browser start --browser chrome

  # Use existing system Chrome/Edge profile (requires all browser windows closed)
  cc-browser start --profileDir "Default"
  cc-browser start --profileDir "Profile 1"
  cc-browser start --browser edge --profileDir "Default"

  # Basic workflow
  cc-browser start --workspace acmeflow
  cc-browser navigate --url "https://example.com"
  cc-browser snapshot --interactive
  cc-browser click --ref e3
  cc-browser type --ref e4 --text "hello"
  cc-browser screenshot
  cc-browser stop

  # Workspace data stored at: %LOCALAPPDATA%\\cc-browser\\<browser>-<workspace>\\

MULTI-WORKSPACE (SIMULTANEOUS BROWSERS):
  Each workspace has its own ports defined in workspace.json:
  - edge-work: daemon=9280, cdp=9222
  - chrome-work: daemon=9281, cdp=9223
  - chrome-personal: daemon=9282, cdp=9224

  # Start daemons for each workspace (in separate terminals):
  cc-browser daemon --workspace acmeflow                     # Port 9280
  cc-browser daemon --browser chrome --workspace work       # Port 9281
  cc-browser daemon --browser chrome --workspace personal   # Port 9282

  # Commands auto-detect daemon port from workspace.json:
  cc-browser start --workspace acmeflow
  cc-browser start --browser chrome --workspace work
  cc-browser start --browser chrome --workspace personal
`);
  },

  // Start daemon (foreground)
  daemon: async (args) => {
    const port = getDaemonPort(args);
    const daemonPath = join(__dirname, 'daemon.mjs');

    const daemonArgs = ['--port', String(port)];
    if (args.browser) daemonArgs.push('--browser', args.browser);
    if (args.workspace) daemonArgs.push('--workspace', args.workspace);

    console.log(`Starting cc-browser daemon on port ${port}...`);
    const child = spawn(process.execPath, [daemonPath, ...daemonArgs], {
      stdio: 'inherit',
    });

    child.on('exit', (code) => {
      process.exit(code || 0);
    });
  },

  // Status
  status: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('GET', '/', null, port);
    output(result);
  },

  // List available browsers
  browsers: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('GET', '/browsers', null, port);
    output(result);
  },

  // List Chrome/Edge profiles
  profiles: async (args) => {
    const port = getDaemonPort(args);
    const browser = args.browser || 'chrome';
    const result = await request('GET', `/profiles?browser=${browser}`, null, port);
    output(result);
  },

  // List configured cc-browser workspaces
  workspaces: async (args) => {
    const localAppData = process.env.LOCALAPPDATA || join(process.env.HOME || '', 'AppData', 'Local');
    const ccBrowserDir = join(localAppData, 'cc-browser');

    if (!existsSync(ccBrowserDir)) {
      output({ success: true, workspaces: [] });
      return;
    }

    const workspaces = [];
    const dirs = readdirSync(ccBrowserDir);
    for (const dir of dirs) {
      const configPath = join(ccBrowserDir, dir, 'workspace.json');
      if (existsSync(configPath)) {
        try {
          const config = JSON.parse(readFileSync(configPath, 'utf8'));
          workspaces.push({
            directory: dir,
            name: config.name || dir,
            browser: config.browser || 'unknown',
            workspace: config.workspace || dir,
            daemonPort: config.daemonPort || null,
            cdpPort: config.cdpPort || null,
            aliases: config.aliases || [],
            purpose: config.purpose || '',
          });
        } catch {
          // Skip invalid configs
        }
      }
    }
    output({ success: true, workspaces });
  },

  // Get favorites from workspace.json
  favorites: async (args) => {
    let browser = args.browser || 'edge';
    let workspace = args.workspace;

    if (!workspace) {
      output({ success: false, error: 'Workspace name required. Use --workspace <name>' });
      return;
    }

    // Resolve alias if no explicit browser was provided
    if (!args.browser) {
      const resolved = resolveAlias(workspace);
      if (resolved) {
        browser = resolved.browser;
        workspace = resolved.workspace;
      }
    }

    const workspaceDir = getWorkspaceDir(browser, workspace);
    const workspaceJsonPath = join(workspaceDir, 'workspace.json');

    if (!existsSync(workspaceJsonPath)) {
      output({ success: false, error: `Workspace not found: ${workspaceJsonPath}` });
      return;
    }

    try {
      const data = JSON.parse(readFileSync(workspaceJsonPath, 'utf8'));
      output({
        success: true,
        browser,
        workspace,
        favorites: data.favorites || [],
        name: data.name,
        purpose: data.purpose,
      });
    } catch (err) {
      output({ success: false, error: `Failed to read workspace: ${err.message}` });
    }
  },

  // Start browser
  start: async (args) => {
    if (args.incognito && args.workspace) {
      outputError('Cannot use --incognito with --workspace');
    }
    const port = getDaemonPort(args);
    const result = await request('POST', '/start', {
      headless: args.headless,
      port: args.cdpPort,
      exe: args.exe,
      browser: args.browser,
      workspace: args.workspace,
      profileDir: args.profileDir,
      useSystemProfile: args.profileDir ? true : args.systemProfile,
      noIndicator: args['no-indicator'] || false,
      mode: args.mode,
      incognito: args.incognito || false,
    }, port);
    output(result);
  },

  // Stop browser
  stop: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/stop', {
      browser: args.browser,
      workspace: args.workspace,
    }, port);
    output(result);
  },

  // Navigate
  navigate: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/navigate', {
      url: args.url,
      tab: args.tab,
      waitUntil: args.waitUntil,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Reload
  reload: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/reload', {
      tab: args.tab,
      waitUntil: args.waitUntil,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Back
  back: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/back', {
      tab: args.tab,
    }, port);
    output(result);
  },

  // Forward
  forward: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/forward', {
      tab: args.tab,
    }, port);
    output(result);
  },

  // Snapshot
  snapshot: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/snapshot', {
      tab: args.tab,
      interactive: args.interactive,
      compact: args.compact,
      maxDepth: args.maxDepth,
      maxChars: args.maxChars,
    }, port);
    output(result);
  },

  // Info
  info: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/info', {
      tab: args.tab,
    }, port);
    output(result);
  },

  // Click
  click: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/click', {
      ref: args.ref,
      text: args.text,
      selector: args.selector,
      exact: args.exact,
      tab: args.tab,
      doubleClick: args.double || args.doubleClick,
      button: args.button,
      modifiers: args.modifiers,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Type
  type: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/type', {
      ref: args.ref,
      textContent: args.textContent,
      selector: args.selector,
      exact: args.exact,
      text: args.text,
      tab: args.tab,
      submit: args.submit,
      slowly: args.slowly,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Press
  press: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/press', {
      key: args.key,
      tab: args.tab,
      delay: args.delay,
    }, port);
    output(result);
  },

  // Hover
  hover: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/hover', {
      ref: args.ref,
      text: args.text,
      selector: args.selector,
      exact: args.exact,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Drag
  drag: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/drag', {
      startRef: args.from || args.startRef,
      endRef: args.to || args.endRef,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Select
  select: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/select', {
      ref: args.ref,
      values: args.values || args.value,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Scroll
  scroll: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/scroll', {
      direction: args.direction,
      amount: args.amount,
      ref: args.ref,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Wait
  wait: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/wait', {
      time: args.time,
      text: args.text,
      textGone: args.textGone,
      selector: args.selector,
      url: args.url,
      loadState: args.loadState,
      fn: args.fn,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Evaluate
  evaluate: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/evaluate', {
      fn: args.js || args.fn || args.code,
      ref: args.ref,
      tab: args.tab,
    }, port);
    output(result);
  },

  // Fill form
  fill: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/fill', {
      fields: args.fields,
      tab: args.tab,
      timeout: args.timeout,
    }, port);
    output(result);
  },

  // Screenshot
  screenshot: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/screenshot', {
      ref: args.ref,
      element: args.element,
      fullPage: args.fullPage,
      type: args.type,
      tab: args.tab,
    }, port);

    if (args.save && result.screenshot) {
      let savePath = String(args.save);
      // Auto-append extension if missing
      const ext = extname(savePath).toLowerCase();
      const imgType = result.type || args.type || 'png';
      if (!ext) {
        savePath += '.' + imgType;
      }
      const absPath = resolve(savePath);
      const dir = dirname(absPath);
      if (!existsSync(dir)) {
        mkdirSync(dir, { recursive: true });
      }
      const buf = Buffer.from(result.screenshot, 'base64');
      writeFileSync(absPath, buf);
      output({ success: true, saved: absPath, size: buf.length, type: imgType });
    } else {
      output(result);
    }
  },

  // Screenshot with labels
  'screenshot-labels': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/screenshot-labels', {
      maxLabels: args.maxLabels,
      type: args.type,
      tab: args.tab,
    }, port);
    output(result);
  },

  // Upload
  upload: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/upload', {
      ref: args.ref,
      element: args.element,
      paths: args.paths || args.path,
      tab: args.tab,
    }, port);
    output(result);
  },

  // Resize
  resize: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/resize', {
      width: args.width,
      height: args.height,
      tab: args.tab,
    }, port);
    output(result);
  },

  // List tabs
  tabs: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs', {}, port);
    output(result);
  },

  // Open tab
  'tabs-open': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/open', {
      url: args.url,
      session: args.session,
    }, port);
    output(result);
  },

  // Close tab
  'tabs-close': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/close', {
      tab: args.tab,
    }, port);
    output(result);
  },

  // Focus tab
  'tabs-focus': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/focus', {
      tab: args.tab,
    }, port);
    output(result);
  },

  // Text content
  text: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/text', {
      selector: args.selector,
      tab: args.tab,
    }, port);
    output(result);
  },

  // HTML
  html: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/html', {
      selector: args.selector,
      outer: args.outer,
      tab: args.tab,
    }, port);
    output(result);
  },

  // Mode - get or set
  mode: async (args) => {
    const port = getDaemonPort(args);
    const newMode = args._[1]; // e.g. "cc-browser mode human" -> args._[1] = "human"
    if (newMode) {
      const result = await request('POST', '/mode', { mode: newMode }, port);
      output(result);
    } else {
      const result = await request('GET', '/mode', null, port);
      output(result);
    }
  },

  // CAPTCHA detect
  'captcha': async (args) => {
    const port = getDaemonPort(args);
    const subcommand = args._[1]; // detect or solve
    if (subcommand === 'detect') {
      const result = await request('POST', '/captcha/detect', {
        tab: args.tab,
        vision: args.vision || false,
      }, port);
      output(result);
    } else if (subcommand === 'solve') {
      const result = await request('POST', '/captcha/solve', {
        tab: args.tab,
        attempts: args.attempts,
      }, port);
      output(result);
    } else {
      outputError('Usage: cc-browser captcha <detect|solve> [--attempts N]');
    }
  },

  // Session management
  'session': async (args) => {
    const port = getDaemonPort(args);
    const subcommand = args._[1]; // create, list, close, heartbeat, prune

    if (subcommand === 'create') {
      if (!args.name) {
        outputError('Usage: cc-browser session create --name <name> [--ttl <ms>]');
        return;
      }
      const result = await request('POST', '/sessions/create', {
        name: args.name,
        ttl: args.ttl,
        metadata: args.metadata,
      }, port);
      output(result);
    } else if (subcommand === 'list') {
      const result = await request('GET', '/sessions', null, port);
      output(result);
    } else if (subcommand === 'close') {
      if (!args.session) {
        outputError('Usage: cc-browser session close --session <id>');
        return;
      }
      const result = await request('POST', '/sessions/close', {
        session: args.session,
      }, port);
      output(result);
    } else if (subcommand === 'heartbeat') {
      if (!args.session) {
        outputError('Usage: cc-browser session heartbeat --session <id>');
        return;
      }
      const result = await request('POST', '/sessions/heartbeat', {
        session: args.session,
      }, port);
      output(result);
    } else if (subcommand === 'prune') {
      const result = await request('POST', '/sessions/prune', {}, port);
      output(result);
    } else {
      outputError('Usage: cc-browser session <create|list|close|heartbeat|prune>');
    }
  },

  // Record interactions
  record: async (args) => {
    const port = getDaemonPort(args);
    const subcommand = args._[1];

    if (subcommand === 'start') {
      const result = await request('POST', '/record/start', {
        tab: args.tab,
      }, port);
      output(result);
    } else if (subcommand === 'stop') {
      const result = await request('POST', '/record/stop', {
        tab: args.tab,
      }, port);

      if (result.success && result.steps) {
        const recording = {
          name: args.name || '',
          recordedAt: result.recordedAt,
          steps: result.steps,
        };

        if (args.output) {
          // Legacy: save to explicit path
          const outPath = resolve(args.output);
          writeFileSync(outPath, JSON.stringify(recording, null, 2));
          console.error(`Recording saved to: ${outPath} (${recording.steps.length} steps)`);
        } else {
          // Save to vault recordings directory
          const name = args.name || 'recording';
          const savedPath = saveRecording(name, recording);
          console.error(`Recording saved: ${savedPath} (${recording.steps.length} steps)`);
        }
      }
      output(result);
    } else if (subcommand === 'status') {
      const result = await request('GET', '/record/status', null, port);
      output(result);
    } else {
      outputError('Usage: cc-browser record <start|stop|status>');
    }
  },

  // Replay recording
  replay: async (args) => {
    const port = getDaemonPort(args);

    if (!args.file && !args.name) {
      outputError('Usage: cc-browser replay --name <name> [--mode fast|human] [--timeout <ms>]\n       cc-browser replay --file <path> [--mode fast|human] [--timeout <ms>]');
      return;
    }

    let recording;

    if (args.name) {
      // Look up recording by name in vault
      const found = findRecording(args.name);
      if (!found) {
        outputError(`No recording found matching: ${args.name}`);
        return;
      }
      console.error(`Replaying: ${found.path}`);
      recording = found.recording;
    } else {
      const filePath = resolve(args.file);
      if (!existsSync(filePath)) {
        outputError(`Recording file not found: ${filePath}`);
        return;
      }
      try {
        recording = JSON.parse(readFileSync(filePath, 'utf8'));
      } catch (err) {
        outputError(`Failed to parse recording file: ${err.message}`);
        return;
      }
    }

    if (!recording.steps || !Array.isArray(recording.steps)) {
      outputError('Invalid recording file: missing "steps" array');
      return;
    }

    const result = await request('POST', '/replay', {
      recording,
      tab: args.tab,
      mode: args.mode,
      timeout: args.timeout,
    }, port, 300000);
    output(result);
  },

  // List saved recordings
  recordings: async (args) => {
    const items = listRecordings();
    if (items.length === 0) {
      console.error('No recordings found.');
      output({ success: true, recordings: [] });
      return;
    }
    for (const item of items) {
      console.error(`  ${item.name || '(unnamed)'} -- ${item.date} -- ${item.steps} steps`);
      console.error(`    ${item.path}`);
    }
    output({ success: true, recordings: items });
  },

  // Close all tabs
  'tabs-close-all': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/close-all', {}, port);
    output(result);
  },
};

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

async function main() {
  const args = parseArgs(process.argv);
  const command = args._[0] || 'help';

  // Per-command help: cc-browser <command> --help
  if (args.help && command !== 'help') {
    if (!printCommandHelp(command)) {
      console.log(`No help available for "${command}". Run 'cc-browser help' for all commands.`);
    }
    return;
  }

  const handler = commands[command];
  if (!handler) {
    outputError(`Unknown command: ${command}. Run 'cc-browser help' for usage.`);
  }

  try {
    await handler(args);
  } catch (err) {
    outputError(err.message);
  }
}

main();

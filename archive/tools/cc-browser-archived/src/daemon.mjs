#!/usr/bin/env node
// CC Browser - HTTP Daemon Server
// Fast browser automation for Claude Code
// Usage: node daemon.mjs [--port 9280]

import { createServer } from 'http';
import { parse as parseUrl } from 'url';
import { existsSync, readFileSync, readdirSync, writeFileSync, unlinkSync, mkdirSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

import { ensureChromeAvailable, checkChromeRunning, stopChrome, launchChrome, listAvailableBrowsers, listChromeProfiles } from './chrome.mjs';

// ---------------------------------------------------------------------------
// Workspace Configuration Reader
// ---------------------------------------------------------------------------

function getWorkspaceDir(browser, workspace) {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-browser', `${browser}-${workspace}`);
}

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
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
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
import { connectBrowser, disconnectBrowser, getCachedBrowser, getPageState, setWorkspaceIndicator, getCurrentMode, setCurrentMode, setBrowserDirect } from './session.mjs';
import {
  listPagesViaPlaywright,
  createPageViaPlaywright,
  closePageByTargetIdViaPlaywright,
  focusPageByTargetIdViaPlaywright,
} from './session.mjs';
import {
  clickViaPlaywright,
  hoverViaPlaywright,
  dragViaPlaywright,
  selectOptionViaPlaywright,
  pressKeyViaPlaywright,
  typeViaPlaywright,
  fillFormViaPlaywright,
  evaluateViaPlaywright,
  scrollViaPlaywright,
  scrollIntoViewViaPlaywright,
  waitForViaPlaywright,
  takeScreenshotViaPlaywright,
  setInputFilesViaPlaywright,
  resizeViaPlaywright,
  navigateViaPlaywright,
  reloadViaPlaywright,
  goBackViaPlaywright,
  goForwardViaPlaywright,
} from './interactions.mjs';
import {
  snapshotViaPlaywright,
  getPageInfoViaPlaywright,
  getTextContentViaPlaywright,
  getHtmlViaPlaywright,
  screenshotWithLabelsViaPlaywright,
} from './snapshot.mjs';
import { detectCaptcha, detectCaptchaDOM, solveCaptcha } from './captcha.mjs';
import { getPageForTargetId } from './session.mjs';
import { startRecording, stopRecording, getRecordingStatus, receiveBeaconEvents } from './recorder.mjs';
import { replayRecording } from './replay.mjs';
import {
  createSession, getSession, listSessions, deleteSession,
  addTabToSession, removeTabFromSessions, findSessionForTab,
  touchSession, pruneExpiredSessions, reconcileTabs,
  persistSessions, loadSessions,
  startCleanupTimer, stopCleanupTimer, sessionCount,
} from './sessions.mjs';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;
const DEFAULT_CDP_PORT = 9222;

// Lockfile path for daemon port auto-detection
function getLockfilePath() {
  const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  return join(localAppData, 'cc-browser', 'daemon.lock');
}

function writeLockfile(port, browser, workspace) {
  const lockPath = getLockfilePath();
  const lockDir = join(lockPath, '..');
  if (!existsSync(lockDir)) {
    mkdirSync(lockDir, { recursive: true });
  }
  const data = {
    port,
    browser: browser || null,
    workspace: workspace || null,
    pid: process.pid,
    startedAt: new Date().toISOString(),
  };
  writeFileSync(lockPath, JSON.stringify(data, null, 2));
  console.log(`[cc-browser] Lockfile written: ${lockPath}`);
}

function removeLockfile() {
  const lockPath = getLockfilePath();
  if (existsSync(lockPath)) {
    unlinkSync(lockPath);
    console.log(`[cc-browser] Lockfile removed: ${lockPath}`);
  }
}

// Track the actual port this daemon is listening on
let actualDaemonPort = DEFAULT_DAEMON_PORT;

// Track the default workspace for this daemon (set via CLI args)
let defaultDaemonBrowser = null;
let defaultDaemonWorkspace = null;
let defaultDaemonCdpPort = null;

// Track the active browser session
let activeCdpPort = null;
let activeBrowserKind = null;
let activeWorkspace = null;
let activeIncognito = false;

function getActiveCdpPort() {
  // Active session takes priority (null means pipe mode)
  if (activeCdpPort) return activeCdpPort;
  // Pipe mode: no port needed
  if (activeBrowserKind && activeCdpPort === null) return null;
  // Then use daemon's default workspace CDP port
  if (defaultDaemonCdpPort) return defaultDaemonCdpPort;
  // Finally fall back to default
  return DEFAULT_CDP_PORT;
}

function getActiveCdpUrl() {
  const port = getActiveCdpPort();
  if (port === null) return 'pipe';
  return `http://127.0.0.1:${port}`;
}

function setActiveSession(cdpPort, browserKind, workspace, incognito = false) {
  activeCdpPort = cdpPort;
  activeBrowserKind = browserKind;
  activeWorkspace = workspace;
  activeIncognito = incognito;
  const label = incognito ? 'incognito' : `${browserKind}-${workspace}`;
  console.log(`[cc-browser] Active session: ${label} on port ${cdpPort}`);
}

function clearActiveSession() {
  console.log(`[cc-browser] Session cleared (was: ${activeBrowserKind}-${activeWorkspace} on port ${activeCdpPort})`);
  activeCdpPort = null;
  activeBrowserKind = null;
  activeWorkspace = null;
  activeIncognito = false;
}

function validateSession(body) {
  // If no active session, return error
  // activeCdpPort is null in pipe mode, so check activeBrowserKind as session indicator
  if (!activeCdpPort && !activeBrowserKind) {
    return { valid: false, error: 'No browser session active. Run "start" first.' };
  }

  // If browser/workspace specified, validate they match
  if (body.browser && body.browser !== activeBrowserKind) {
    return {
      valid: false,
      error: `Browser mismatch: requested "${body.browser}" but active session is "${activeBrowserKind}". Stop current session first.`,
    };
  }
  if (body.workspace && body.workspace !== activeWorkspace) {
    return {
      valid: false,
      error: `Workspace mismatch: requested "${body.workspace}" but active session is "${activeWorkspace}". Stop current session first.`,
    };
  }

  return { valid: true };
}

// ---------------------------------------------------------------------------
// JSON Response Helpers
// ---------------------------------------------------------------------------

function jsonResponse(res, statusCode, data) {
  res.writeHead(statusCode, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(data));
}

function jsonError(res, statusCode, message) {
  jsonResponse(res, statusCode, { success: false, error: message });
}

function jsonSuccess(res, data = {}) {
  jsonResponse(res, 200, { success: true, ...data });
}

// ---------------------------------------------------------------------------
// Request Body Parser
// ---------------------------------------------------------------------------

async function parseBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
      if (body.length > 1024 * 1024) {
        reject(new Error('Request body too large'));
      }
    });
    req.on('end', () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch (err) {
        const preview = body.length > 100 ? body.slice(0, 100) + '...' : body;
        reject(new Error(`Invalid JSON: ${err.message} (body: ${preview})`));
      }
    });
    req.on('error', reject);
  });
}

// ---------------------------------------------------------------------------
// Route Handlers
// ---------------------------------------------------------------------------

const routes = {
  // List available browsers
  'GET /browsers': async (req, res, params) => {
    const browsers = listAvailableBrowsers();
    jsonSuccess(res, { browsers });
  },

  // List Chrome profiles
  'GET /profiles': async (req, res, params, body, query) => {
    const browserKind = query?.browser || 'chrome';
    const profiles = listChromeProfiles(browserKind);
    jsonSuccess(res, { browser: browserKind, profiles });
  },

  // Status
  'GET /': async (req, res, params) => {
    const cdpPort = getActiveCdpPort();
    const status = await checkChromeRunning(cdpPort);
    const cached = getCachedBrowser();

    jsonSuccess(res, {
      daemon: 'running',
      daemonPort: params.daemonPort,
      browser: status.running ? 'connected' : 'not running',
      cdpUrl: status.pipeMode ? 'pipe' : status.cdpUrl,
      cdpPort: status.pipeMode ? null : cdpPort,
      pipeMode: !!status.pipeMode,
      browserKind: activeBrowserKind,
      workspace: activeIncognito ? null : activeWorkspace,
      incognito: activeIncognito,
      mode: getCurrentMode(),
      tabs: status.tabs || [],
      activeTab: status.activeTab,
      playwrightConnected: !!cached,
      sessions: sessionCount(),
    });
  },

  // Start browser
  'POST /start': async (req, res, params, body) => {
    const isIncognito = !!body.incognito;

    // Incognito and workspace are mutually exclusive
    if (isIncognito && body.workspace) {
      return jsonError(res, 400, 'Cannot use --incognito with --workspace');
    }

    const workspaceName = isIncognito ? null : (body.workspace || defaultDaemonWorkspace || 'default');
    const browserKind = body.browser || defaultDaemonBrowser || 'chrome';

    // Read workspace config once for cdpPort and indicator settings
    const workspaceConfig = isIncognito ? null : readWorkspaceConfig(browserKind, workspaceName);

    // CDP port priority: explicit body.port > workspace.json > default
    let cdpPort = body.port;
    if (!cdpPort && workspaceConfig?.cdpPort) {
      cdpPort = workspaceConfig.cdpPort;
    }
    if (!cdpPort) {
      cdpPort = DEFAULT_CDP_PORT;
    }

    // Indicator bar: shows "cc-browser // workspace" bar at top of pages
    // Controlled by: --no-indicator flag > workspace.json > default (true)
    // Stealth mode also disables the bar automatically
    // Incognito mode disables indicator (no workspace to label)
    const requestedMode = body.mode || (workspaceConfig && workspaceConfig.mode);
    let indicator = true;
    if (isIncognito) {
      indicator = false;
    }
    if (requestedMode === 'stealth') {
      indicator = false;
    }
    if (workspaceConfig?.indicator === false) {
      indicator = false;
    }
    if (body.noIndicator) {
      indicator = false;
    }

    const result = await ensureChromeAvailable({
      port: cdpPort,
      headless: body.headless,
      executablePath: body.exe,
      browser: body.browser,
      workspaceName: isIncognito ? 'incognito' : workspaceName,
      useSystemProfile: body.systemProfile || body.useSystemProfile,
      profileDir: body.profileDir,
      incognito: isIncognito,
    });

    // Set mode before connecting (stealth scripts depend on mode)
    if (requestedMode) {
      setCurrentMode(requestedMode);
    }

    // Connect Playwright - pipe mode (browser returned directly) or legacy port mode
    if (result.browser) {
      // Pipe mode: Playwright launched the browser, no CDP port needed
      await setBrowserDirect(result.browser);
      // Track with null cdpPort to indicate pipe mode
      setActiveSession(null, result.browserKind, workspaceName, isIncognito);
    } else if (result.cdpUrl) {
      // Legacy: port-based connection (e.g. already-running browser)
      await connectBrowser(result.cdpUrl);
      setActiveSession(cdpPort, result.browserKind, workspaceName, isIncognito);
    }

    // Show workspace indicator bar on all pages (JS-injected, no --enable-automation)
    if (indicator) {
      await setWorkspaceIndicator(workspaceName);
    }

    jsonSuccess(res, {
      started: result.started,
      cdpUrl: result.browser ? 'pipe' : result.cdpUrl,
      cdpPort: result.browser ? null : cdpPort,
      pipeMode: !!result.browser,
      browserKind: result.browserKind,
      workspace: isIncognito ? null : workspaceName,
      incognito: isIncognito,
      mode: getCurrentMode(),
      tabs: result.tabs,
      activeTab: result.activeTab,
    });
  },

  // Stop browser
  'POST /stop': async (req, res, params) => {
    const cdpPort = activeCdpPort || params.cdpPort || DEFAULT_CDP_PORT;
    await disconnectBrowser();
    const result = await stopChrome(cdpPort);
    clearActiveSession();
    jsonSuccess(res, result);
  },

  // Navigate
  'POST /navigate': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) {
      return jsonError(res, 400, validation.error);
    }
    const cdpUrl = getActiveCdpUrl();
    const result = await navigateViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      url: body.url,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Reload
  'POST /reload': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await reloadViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Go back
  'POST /back': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await goBackViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Go forward
  'POST /forward': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await goForwardViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  // Snapshot
  'POST /snapshot': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await snapshotViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      interactive: body.interactive,
      compact: body.compact,
      maxDepth: body.maxDepth,
      maxChars: body.maxChars,
    });
    jsonSuccess(res, result);
  },

  // Page info
  'POST /info': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await getPageInfoViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
    });
    jsonSuccess(res, result);
  },

  // Click
  'POST /click': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await clickViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      ref: body.ref,
      text: body.text,
      selector: body.selector,
      exact: body.exact,
      doubleClick: body.doubleClick || body.double,
      button: body.button,
      modifiers: body.modifiers,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { clicked: body.ref || body.text || body.selector });
  },

  // Type
  'POST /type': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await typeViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      ref: body.ref,
      textContent: body.textContent,
      selector: body.selector,
      exact: body.exact,
      text: body.text,
      submit: body.submit,
      slowly: body.slowly,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { typed: body.text, ref: body.ref || body.textContent || body.selector });
  },

  // Press key
  'POST /press': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await pressKeyViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      key: body.key,
      delayMs: body.delay,
    });
    jsonSuccess(res, { pressed: body.key });
  },

  // Hover
  'POST /hover': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await hoverViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      ref: body.ref,
      text: body.text,
      selector: body.selector,
      exact: body.exact,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { hovered: body.ref || body.text || body.selector });
  },

  // Drag
  'POST /drag': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await dragViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      startRef: body.startRef || body.from,
      endRef: body.endRef || body.to,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { dragged: `${body.startRef || body.from} -> ${body.endRef || body.to}` });
  },

  // Select
  'POST /select': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await selectOptionViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      ref: body.ref,
      values: Array.isArray(body.values) ? body.values : [body.value || body.values],
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { selected: body.values || body.value, ref: body.ref });
  },

  // Fill form
  'POST /fill': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await fillFormViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      fields: body.fields,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { filled: body.fields?.length || 0 });
  },

  // Scroll
  'POST /scroll': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await scrollViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      direction: body.direction,
      amount: body.amount,
      ref: body.ref,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { scrolled: body.ref || body.direction || 'down' });
  },

  // Wait
  'POST /wait': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await waitForViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      timeMs: body.time,
      text: body.text,
      textGone: body.textGone,
      selector: body.selector,
      url: body.url,
      loadState: body.loadState,
      fn: body.fn,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { waited: true });
  },

  // Evaluate JavaScript
  'POST /evaluate': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await evaluateViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      fn: body.fn || body.js || body.code,
      ref: body.ref,
    });
    jsonSuccess(res, { result });
  },

  // Screenshot
  'POST /screenshot': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const { buffer } = await takeScreenshotViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      ref: body.ref,
      element: body.element,
      fullPage: body.fullPage,
      type: body.type || 'png',
    });

    // Return as base64
    jsonSuccess(res, {
      screenshot: buffer.toString('base64'),
      type: body.type || 'png',
    });
  },

  // Screenshot with labels
  'POST /screenshot-labels': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const { buffer, labels, skipped } = await screenshotWithLabelsViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      maxLabels: body.maxLabels,
      type: body.type || 'png',
    });

    jsonSuccess(res, {
      screenshot: buffer.toString('base64'),
      type: body.type || 'png',
      labels,
      skipped,
    });
  },

  // Upload file
  'POST /upload': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await setInputFilesViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      inputRef: body.ref,
      element: body.element,
      paths: Array.isArray(body.paths) ? body.paths : [body.path || body.paths],
    });
    jsonSuccess(res, { uploaded: body.paths || body.path });
  },

  // Resize viewport
  'POST /resize': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await resizeViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      width: body.width,
      height: body.height,
    });
    jsonSuccess(res, result);
  },

  // List tabs
  'POST /tabs': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const tabs = await listPagesViaPlaywright({ cdpUrl });
    // Annotate tabs with session info
    for (const tab of tabs) {
      const sess = findSessionForTab(tab.targetId);
      if (sess) {
        tab.session = { id: sess.id, name: sess.name };
      }
    }
    jsonSuccess(res, { tabs });
  },

  // Open new tab
  'POST /tabs/open': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);

    // Validate session ID if provided
    if (body.session) {
      const sess = getSession(body.session);
      if (!sess) return jsonError(res, 404, `Session not found: ${body.session}`);
    }

    const cdpUrl = getActiveCdpUrl();
    const tab = await createPageViaPlaywright({ cdpUrl, url: body.url });

    // Track tab in session (also acts as implicit heartbeat)
    if (body.session) {
      addTabToSession(body.session, tab.targetId);
      tab.session = body.session;
    }

    jsonSuccess(res, { tab });
  },

  // Close tab
  'POST /tabs/close': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const tabId = body.tab || body.targetId;
    await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
    removeTabFromSessions(tabId);
    jsonSuccess(res, { closed: tabId });
  },

  // Focus tab
  'POST /tabs/focus': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    await focusPageByTargetIdViaPlaywright({ cdpUrl, targetId: body.tab || body.targetId });
    jsonSuccess(res, { focused: body.tab || body.targetId });
  },

  // Get text content
  'POST /text': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const text = await getTextContentViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      selector: body.selector,
    });
    jsonSuccess(res, { text });
  },

  // Detect CAPTCHA
  'POST /captcha/detect': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const page = await getPageForTargetId({ cdpUrl, targetId: body.tab || body.targetId });
    const result = await detectCaptcha(page, { useVision: body.vision || false });
    jsonSuccess(res, result);
  },

  // Solve CAPTCHA
  'POST /captcha/solve': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const page = await getPageForTargetId({ cdpUrl, targetId: body.tab || body.targetId });
    const result = await solveCaptcha(page, { maxAttempts: body.attempts || 3 });
    jsonSuccess(res, result);
  },

  // Get current mode
  'GET /mode': async (req, res) => {
    jsonSuccess(res, { mode: getCurrentMode() });
  },

  // Set mode
  'POST /mode': async (req, res, params, body) => {
    try {
      setCurrentMode(body.mode);
      jsonSuccess(res, { mode: getCurrentMode() });
    } catch (err) {
      jsonError(res, 400, err.message);
    }
  },

  // Get HTML
  'POST /html': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const html = await getHtmlViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      selector: body.selector,
      outer: body.outer,
    });
    jsonSuccess(res, { html });
  },

  // -----------------------------------------------------------------------
  // Sessions
  // -----------------------------------------------------------------------

  // Create session
  'POST /sessions/create': async (req, res, params, body) => {
    if (!body.name) return jsonError(res, 400, 'name is required');
    const session = createSession({
      name: body.name,
      ttlMs: body.ttl,
      metadata: body.metadata,
    });
    jsonSuccess(res, { session });
  },

  // List sessions
  'GET /sessions': async (req, res) => {
    const all = listSessions();
    const result = all.map(s => ({
      id: s.id,
      name: s.name,
      createdAt: s.createdAt,
      lastActivity: s.lastActivity,
      ttlMs: s.ttlMs,
      tabCount: s.tabIds.length,
      tabIds: s.tabIds,
      metadata: s.metadata,
    }));
    jsonSuccess(res, { sessions: result });
  },

  // Heartbeat -- touch session to keep alive
  'POST /sessions/heartbeat': async (req, res, params, body) => {
    if (!body.session) return jsonError(res, 400, 'session is required');
    const ok = touchSession(body.session);
    if (!ok) return jsonError(res, 404, `Session not found: ${body.session}`);
    jsonSuccess(res, { session: body.session, touched: true });
  },

  // Close session -- close all tabs, delete session
  'POST /sessions/close': async (req, res, params, body) => {
    if (!body.session) return jsonError(res, 400, 'session is required');
    const sess = getSession(body.session);
    if (!sess) return jsonError(res, 404, `Session not found: ${body.session}`);

    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();

    // Close all tabs in this session
    const closed = [];
    const errors = [];
    for (const tabId of [...sess.tabIds]) {
      try {
        await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
        closed.push(tabId);
      } catch {
        errors.push(tabId);
      }
    }

    deleteSession(body.session);
    jsonSuccess(res, {
      session: body.session,
      closed: closed.length,
      errors: errors.length,
    });
  },

  // Prune expired sessions
  'POST /sessions/prune': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();

    const pruned = pruneExpiredSessions();
    let totalClosed = 0;

    for (const p of pruned) {
      for (const tabId of p.tabIds) {
        try {
          await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
          totalClosed++;
        } catch {
          // Tab may already be gone
        }
      }
    }

    jsonSuccess(res, {
      prunedSessions: pruned.length,
      closedTabs: totalClosed,
    });
  },

  // Close all tabs (keeps one blank tab -- Chrome requirement)
  'POST /tabs/close-all': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();

    const tabs = await listPagesViaPlaywright({ cdpUrl });
    if (tabs.length === 0) {
      return jsonSuccess(res, { closed: 0 });
    }

    // Create a blank tab first (Chrome needs at least 1 tab)
    await createPageViaPlaywright({ cdpUrl, url: 'about:blank' });

    // Close all original tabs
    let closed = 0;
    for (const tab of tabs) {
      try {
        await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tab.targetId });
        removeTabFromSessions(tab.targetId);
        closed++;
      } catch {
        // Tab may already be closed
      }
    }

    jsonSuccess(res, { closed });
  },

  // -----------------------------------------------------------------------
  // Record & Replay
  // -----------------------------------------------------------------------

  // Start recording
  'POST /record/start': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await startRecording({ cdpUrl, targetId: body.tab || body.targetId, daemonPort: actualDaemonPort });
    jsonSuccess(res, result);
  },

  // Stop recording
  'POST /record/stop': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    const cdpUrl = getActiveCdpUrl();
    const result = await stopRecording({ cdpUrl, targetId: body.tab || body.targetId });
    jsonSuccess(res, result);
  },

  // Recording status
  'GET /record/status': async (req, res) => {
    const status = getRecordingStatus();
    jsonSuccess(res, status);
  },

  // Beacon endpoint: receives events from browser's beforeunload handler
  // via navigator.sendBeacon(). Captures events that would be lost when
  // full-page navigation destroys the page context.
  'POST /record/beacon': async (req, res, params, body) => {
    receiveBeaconEvents(body);
    res.writeHead(204);
    res.end();
  },

  // Replay recording
  'POST /replay': async (req, res, params, body) => {
    const validation = validateSession(body);
    if (!validation.valid) return jsonError(res, 400, validation.error);
    if (!body.recording) return jsonError(res, 400, 'recording is required');
    const cdpUrl = getActiveCdpUrl();
    const result = await replayRecording({
      cdpUrl,
      targetId: body.tab || body.targetId,
      recording: body.recording,
      mode: body.mode,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },
};

// ---------------------------------------------------------------------------
// HTTP Server
// ---------------------------------------------------------------------------

async function handleRequest(req, res) {
  const { pathname, query } = parseUrl(req.url, true);
  const method = req.method.toUpperCase();
  const routeKey = `${method} ${pathname}`;

  // Parse query params - use active session port as default
  const params = {
    cdpPort: query.cdpPort ? parseInt(query.cdpPort, 10) : getActiveCdpPort(),
    daemonPort: query.daemonPort ? parseInt(query.daemonPort, 10) : actualDaemonPort,
  };

  // Find route handler
  const handler = routes[routeKey];
  if (!handler) {
    return jsonError(res, 404, `Unknown route: ${routeKey}`);
  }

  try {
    const body = method === 'GET' ? {} : await parseBody(req);
    await handler(req, res, params, body, query);
  } catch (err) {
    console.error(`[ERROR] ${routeKey}:`, err.message);
    jsonError(res, 500, err.message);
  }
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

const args = process.argv.slice(2);
let daemonPort = DEFAULT_DAEMON_PORT;
let defaultBrowser = null;
let defaultWorkspace = null;

for (let i = 0; i < args.length; i++) {
  if (args[i] === '--port' && args[i + 1]) {
    daemonPort = parseInt(args[i + 1], 10);
  } else if (args[i] === '--browser' && args[i + 1]) {
    defaultBrowser = args[i + 1];
    i++;
  } else if (args[i] === '--workspace' && args[i + 1]) {
    defaultWorkspace = args[i + 1];
    i++;
  }
}

// If workspace is specified without browser, try to resolve as alias
if (defaultWorkspace && !defaultBrowser) {
  const resolved = resolveAlias(defaultWorkspace);
  if (resolved) {
    console.log(`[cc-browser] Alias "${defaultWorkspace}" resolved to ${resolved.browser}-${resolved.workspace}`);
    defaultBrowser = resolved.browser;
    defaultWorkspace = resolved.workspace;
  }
}

// If browser and workspace are set, try to read ports from workspace.json
if (defaultBrowser && defaultWorkspace) {
  const config = readWorkspaceConfig(defaultBrowser, defaultWorkspace);
  if (config) {
    // Only use config daemonPort if not explicitly set via --port
    if (!args.includes('--port') && config.daemonPort) {
      daemonPort = config.daemonPort;
    }
    // Store default CDP port for this daemon
    if (config.cdpPort) {
      defaultDaemonCdpPort = config.cdpPort;
    }
  }
  // Store daemon's default browser/workspace
  defaultDaemonBrowser = defaultBrowser;
  defaultDaemonWorkspace = defaultWorkspace;
}

const server = createServer(handleRequest);

// Store actual port for status responses
actualDaemonPort = daemonPort;

// Load sessions and start cleanup timer
const sessionDir = (defaultBrowser && defaultWorkspace)
  ? getWorkspaceDir(defaultBrowser, defaultWorkspace)
  : null;

if (sessionDir) {
  loadSessions(sessionDir);
  console.log(`[cc-browser] Sessions loaded (${sessionCount()} active)`);
}

startCleanupTimer(async (tabIds) => {
  if (!activeBrowserKind) return;
  const cdpUrl = getActiveCdpUrl();
  for (const tabId of tabIds) {
    try {
      await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
    } catch {
      // Tab may already be closed
    }
  }
});

server.listen(daemonPort, '127.0.0.1', () => {
  console.log(`[cc-browser] Daemon listening on http://127.0.0.1:${daemonPort}`);
  if (defaultBrowser && defaultWorkspace) {
    const config = readWorkspaceConfig(defaultBrowser, defaultWorkspace);
    const cdpPort = config?.cdpPort || DEFAULT_CDP_PORT;
    console.log(`[cc-browser] Workspace: ${defaultBrowser}-${defaultWorkspace} (CDP: ${cdpPort})`);
  } else {
    console.log(`[cc-browser] CDP port: ${DEFAULT_CDP_PORT}`);
  }
  // Write lockfile for auto-detection
  writeLockfile(daemonPort, defaultBrowser, defaultWorkspace);
  console.log('[cc-browser] Ready for commands');
});

// Graceful shutdown
process.on('SIGINT', async () => {
  console.log('\n[cc-browser] Shutting down...');
  stopCleanupTimer();
  if (sessionDir) persistSessions(sessionDir);
  removeLockfile();
  await disconnectBrowser();
  server.close();
  process.exit(0);
});

process.on('SIGTERM', async () => {
  console.log('\n[cc-browser] Shutting down...');
  stopCleanupTimer();
  if (sessionDir) persistSessions(sessionDir);
  removeLockfile();
  await disconnectBrowser();
  server.close();
  process.exit(0);
});

#!/usr/bin/env node
// CC Browser - Combined CLI + Daemon Entry Point
// For single executable deployment

import { createServer } from 'http';
import { parse as parseUrl } from 'url';
import { existsSync, readFileSync, readdirSync, writeFileSync, mkdirSync } from 'fs';
import { saveRecording, findRecording, listRecordings } from './recordings.mjs';
import { join, resolve, dirname, extname } from 'path';
import { homedir } from 'os';

import { ensureChromeAvailable, checkChromeRunning, stopChrome, launchChrome, listAvailableBrowsers, listChromeProfiles } from './chrome.mjs';
import { connectBrowser, disconnectBrowser, getCachedBrowser, getPageState } from './session.mjs';
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
import {
  createSession, getSession, listSessions, deleteSession,
  addTabToSession, removeTabFromSessions, findSessionForTab,
  touchSession, pruneExpiredSessions, reconcileTabs,
  persistSessions, loadSessions,
  startCleanupTimer, stopCleanupTimer, sessionCount,
} from './sessions.mjs';
import { startRecording, stopRecording, getRecordingStatus, receiveBeaconEvents } from './recorder.mjs';
import { replayRecording } from './replay.mjs';

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const DEFAULT_DAEMON_PORT = 9280;
const DEFAULT_CDP_PORT = 9222;

// ---------------------------------------------------------------------------
// Workspace Configuration
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

function getDaemonPort(args) {
  if (args.port) return args.port;

  if (args.browser && args.workspace) {
    const config = readWorkspaceConfig(args.browser, args.workspace);
    if (config && config.daemonPort) return config.daemonPort;
  } else if (args.workspace && !args.browser) {
    const resolved = resolveAlias(args.workspace);
    if (resolved) {
      args.browser = resolved.browser;
      args.workspace = resolved.workspace;
      if (resolved.config.daemonPort) return resolved.config.daemonPort;
    }
  }

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
        try {
          args[key] = JSON.parse(next);
        } catch {
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
// HTTP Client (for CLI commands)
// ---------------------------------------------------------------------------

async function request(method, path, body, port = DEFAULT_DAEMON_PORT, timeoutMs = 60000) {
  const url = `http://127.0.0.1:${port}${path}`;

  try {
    const res = await fetch(url, {
      method,
      headers: { 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(timeoutMs),
    });

    return await res.json();
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

function output(data) {
  console.log(JSON.stringify(data, null, 2));
}

function outputError(message) {
  console.error(JSON.stringify({ success: false, error: message }, null, 2));
  process.exit(1);
}

// ---------------------------------------------------------------------------
// Daemon Server State
// ---------------------------------------------------------------------------

let actualDaemonPort = DEFAULT_DAEMON_PORT;
let defaultDaemonBrowser = null;
let defaultDaemonWorkspace = null;
let defaultDaemonCdpPort = null;
let activeCdpPort = null;
let activeBrowserKind = null;
let activeWorkspace = null;
let activeIncognito = false;

function getActiveCdpPort() {
  if (activeCdpPort) return activeCdpPort;
  if (defaultDaemonCdpPort) return defaultDaemonCdpPort;
  return DEFAULT_CDP_PORT;
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
  if (!activeCdpPort) {
    return { valid: false, error: 'No browser session active. Run "start" first.' };
  }
  if (body.browser && body.browser !== activeBrowserKind) {
    return { valid: false, error: `Browser mismatch: requested "${body.browser}" but active is "${activeBrowserKind}"` };
  }
  if (body.workspace && body.workspace !== activeWorkspace) {
    return { valid: false, error: `Workspace mismatch: requested "${body.workspace}" but active is "${activeWorkspace}"` };
  }
  return { valid: true };
}

// ---------------------------------------------------------------------------
// Daemon HTTP Handlers
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

async function parseBody(req) {
  return new Promise((resolve, reject) => {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
      if (body.length > 1024 * 1024) reject(new Error('Request body too large'));
    });
    req.on('end', () => {
      try {
        resolve(body ? JSON.parse(body) : {});
      } catch (err) {
        reject(new Error(`Invalid JSON: ${err.message}`));
      }
    });
    req.on('error', reject);
  });
}

const routes = {
  'GET /browsers': async (req, res) => {
    const browsers = listAvailableBrowsers();
    jsonSuccess(res, { browsers });
  },

  'GET /profiles': async (req, res, params, body, query) => {
    const browserKind = query?.browser || 'chrome';
    const profiles = listChromeProfiles(browserKind);
    jsonSuccess(res, { browser: browserKind, profiles });
  },

  'GET /': async (req, res, params) => {
    const cdpPort = getActiveCdpPort();
    const status = await checkChromeRunning(cdpPort);
    const cached = getCachedBrowser();

    jsonSuccess(res, {
      daemon: 'running',
      daemonPort: params.daemonPort,
      browser: status.running ? 'connected' : 'not running',
      browserRunning: status.running,
      cdpUrl: status.cdpUrl,
      cdpPort,
      browserKind: activeBrowserKind,
      workspace: activeIncognito ? null : activeWorkspace,
      incognito: activeIncognito,
      tabs: status.tabs || [],
      activeTab: status.activeTab,
      playwrightConnected: !!cached,
      sessions: sessionCount(),
    });
  },

  'POST /start': async (req, res, params, body) => {
    const isIncognito = !!body.incognito;

    // Incognito and workspace are mutually exclusive
    if (isIncognito && body.workspace) {
      return jsonError(res, 400, 'Cannot use --incognito with --workspace');
    }

    const workspaceName = isIncognito ? null : (body.workspace || defaultDaemonWorkspace || 'default');
    const browserKind = body.browser || defaultDaemonBrowser || 'chrome';

    let cdpPort = body.port;
    if (!cdpPort && !isIncognito) {
      const workspaceConfig = readWorkspaceConfig(browserKind, workspaceName);
      if (workspaceConfig && workspaceConfig.cdpPort) cdpPort = workspaceConfig.cdpPort;
    }
    if (!cdpPort) cdpPort = DEFAULT_CDP_PORT;

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

    await connectBrowser(result.cdpUrl);
    setActiveSession(cdpPort, result.browserKind, workspaceName, isIncognito);

    jsonSuccess(res, {
      started: result.started,
      cdpUrl: result.cdpUrl,
      cdpPort,
      browserKind: result.browserKind,
      workspace: isIncognito ? null : workspaceName,
      incognito: isIncognito,
      tabs: result.tabs,
      activeTab: result.activeTab,
    });
  },

  'POST /stop': async (req, res, params) => {
    const cdpPort = activeCdpPort || params.cdpPort || DEFAULT_CDP_PORT;
    await disconnectBrowser();
    const result = await stopChrome(cdpPort);
    clearActiveSession();
    jsonSuccess(res, result);
  },

  'POST /navigate': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await navigateViaPlaywright({
      cdpUrl,
      targetId: body.tab || body.targetId,
      url: body.url,
      waitUntil: body.waitUntil,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, result);
  },

  'POST /reload': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await reloadViaPlaywright({ cdpUrl, targetId: body.tab, waitUntil: body.waitUntil, timeoutMs: body.timeout });
    jsonSuccess(res, result);
  },

  'POST /back': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await goBackViaPlaywright({ cdpUrl, targetId: body.tab, waitUntil: body.waitUntil, timeoutMs: body.timeout });
    jsonSuccess(res, result);
  },

  'POST /forward': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await goForwardViaPlaywright({ cdpUrl, targetId: body.tab, waitUntil: body.waitUntil, timeoutMs: body.timeout });
    jsonSuccess(res, result);
  },

  'POST /snapshot': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await snapshotViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      interactive: body.interactive,
      compact: body.compact,
      maxDepth: body.maxDepth,
      maxChars: body.maxChars,
    });
    jsonSuccess(res, result);
  },

  'POST /info': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await getPageInfoViaPlaywright({ cdpUrl, targetId: body.tab });
    jsonSuccess(res, result);
  },

  'POST /click': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await clickViaPlaywright({
      cdpUrl,
      targetId: body.tab,
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

  'POST /type': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await typeViaPlaywright({
      cdpUrl,
      targetId: body.tab,
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

  'POST /press': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await pressKeyViaPlaywright({ cdpUrl, targetId: body.tab, key: body.key, delayMs: body.delay });
    jsonSuccess(res, { pressed: body.key });
  },

  'POST /hover': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await hoverViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      ref: body.ref,
      text: body.text,
      selector: body.selector,
      exact: body.exact,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { hovered: body.ref || body.text || body.selector });
  },

  'POST /drag': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await dragViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      startRef: body.startRef || body.from,
      endRef: body.endRef || body.to,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { dragged: `${body.startRef || body.from} -> ${body.endRef || body.to}` });
  },

  'POST /select': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await selectOptionViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      ref: body.ref,
      values: Array.isArray(body.values) ? body.values : [body.value || body.values],
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { selected: body.values || body.value, ref: body.ref });
  },

  'POST /fill': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await fillFormViaPlaywright({ cdpUrl, targetId: body.tab, fields: body.fields, timeoutMs: body.timeout });
    jsonSuccess(res, { filled: body.fields?.length || 0 });
  },

  'POST /scroll': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await scrollViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      direction: body.direction,
      amount: body.amount,
      ref: body.ref,
      timeoutMs: body.timeout,
    });
    jsonSuccess(res, { scrolled: body.ref || body.direction || 'down' });
  },

  'POST /wait': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await waitForViaPlaywright({
      cdpUrl,
      targetId: body.tab,
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

  'POST /evaluate': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await evaluateViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      fn: body.fn || body.js || body.code,
      ref: body.ref,
    });
    jsonSuccess(res, { result });
  },

  'POST /screenshot': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const { buffer } = await takeScreenshotViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      ref: body.ref,
      element: body.element,
      fullPage: body.fullPage,
      type: body.type || 'png',
    });
    jsonSuccess(res, { screenshot: buffer.toString('base64'), type: body.type || 'png' });
  },

  'POST /screenshot-labels': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const { buffer, labels, skipped } = await screenshotWithLabelsViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      maxLabels: body.maxLabels,
      type: body.type || 'png',
    });
    jsonSuccess(res, { screenshot: buffer.toString('base64'), type: body.type || 'png', labels, skipped });
  },

  'POST /upload': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await setInputFilesViaPlaywright({
      cdpUrl,
      targetId: body.tab,
      inputRef: body.ref,
      element: body.element,
      paths: Array.isArray(body.paths) ? body.paths : [body.path || body.paths],
    });
    jsonSuccess(res, { uploaded: body.paths || body.path });
  },

  'POST /resize': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await resizeViaPlaywright({ cdpUrl, targetId: body.tab, width: body.width, height: body.height });
    jsonSuccess(res, result);
  },

  'POST /tabs': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const tabs = await listPagesViaPlaywright({ cdpUrl });
    for (const tab of tabs) {
      const sess = findSessionForTab(tab.targetId);
      if (sess) {
        tab.session = { id: sess.id, name: sess.name };
      }
    }
    jsonSuccess(res, { tabs });
  },

  'POST /tabs/open': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);

    if (body.session) {
      const sess = getSession(body.session);
      if (!sess) return jsonError(res, 404, `Session not found: ${body.session}`);
    }

    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const tab = await createPageViaPlaywright({ cdpUrl, url: body.url });

    if (body.session) {
      addTabToSession(body.session, tab.targetId);
      tab.session = body.session;
    }

    jsonSuccess(res, { tab });
  },

  'POST /tabs/close': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const tabId = body.tab || body.targetId;
    await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
    removeTabFromSessions(tabId);
    jsonSuccess(res, { closed: tabId });
  },

  'POST /tabs/focus': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    await focusPageByTargetIdViaPlaywright({ cdpUrl, targetId: body.tab || body.targetId });
    jsonSuccess(res, { focused: body.tab || body.targetId });
  },

  'POST /text': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const text = await getTextContentViaPlaywright({ cdpUrl, targetId: body.tab, selector: body.selector });
    jsonSuccess(res, { text });
  },

  'POST /html': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const html = await getHtmlViaPlaywright({ cdpUrl, targetId: body.tab, selector: body.selector, outer: body.outer });
    jsonSuccess(res, { html });
  },

  // --- Sessions ---

  'POST /sessions/create': async (req, res, params, body) => {
    if (!body.name) return jsonError(res, 400, 'name is required');
    const session = createSession({
      name: body.name,
      ttlMs: body.ttl,
      metadata: body.metadata,
    });
    jsonSuccess(res, { session });
  },

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

  'POST /sessions/heartbeat': async (req, res, params, body) => {
    if (!body.session) return jsonError(res, 400, 'session is required');
    const ok = touchSession(body.session);
    if (!ok) return jsonError(res, 404, `Session not found: ${body.session}`);
    jsonSuccess(res, { session: body.session, touched: true });
  },

  'POST /sessions/close': async (req, res, params, body) => {
    if (!body.session) return jsonError(res, 400, 'session is required');
    const sess = getSession(body.session);
    if (!sess) return jsonError(res, 404, `Session not found: ${body.session}`);

    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;

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

  'POST /sessions/prune': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;

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

  'POST /tabs/close-all': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;

    const tabs = await listPagesViaPlaywright({ cdpUrl });
    if (tabs.length === 0) {
      return jsonSuccess(res, { closed: 0 });
    }

    await createPageViaPlaywright({ cdpUrl, url: 'about:blank' });

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

  'POST /record/start': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await startRecording({ cdpUrl, targetId: body.tab || body.targetId, daemonPort: actualDaemonPort });
    jsonSuccess(res, result);
  },

  'POST /record/stop': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
    const result = await stopRecording({ cdpUrl, targetId: body.tab || body.targetId });
    jsonSuccess(res, result);
  },

  'GET /record/status': async (req, res) => {
    const status = getRecordingStatus();
    jsonSuccess(res, status);
  },

  'POST /record/beacon': async (req, res, params, body) => {
    receiveBeaconEvents(body);
    res.writeHead(204);
    res.end();
  },

  'POST /replay': async (req, res, params, body) => {
    const v = validateSession(body);
    if (!v.valid) return jsonError(res, 400, v.error);
    if (!body.recording) return jsonError(res, 400, 'recording is required');
    const cdpUrl = `http://127.0.0.1:${getActiveCdpPort()}`;
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

async function handleRequest(req, res) {
  const { pathname, query } = parseUrl(req.url, true);
  const method = req.method.toUpperCase();
  const routeKey = `${method} ${pathname}`;

  const params = {
    cdpPort: query.cdpPort ? parseInt(query.cdpPort, 10) : getActiveCdpPort(),
    daemonPort: query.daemonPort ? parseInt(query.daemonPort, 10) : actualDaemonPort,
  };

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

function startDaemon(args) {
  let daemonPort = args.port || DEFAULT_DAEMON_PORT;

  // If workspace is specified without browser, try to resolve as alias
  if (args.workspace && !args.browser) {
    const resolved = resolveAlias(args.workspace);
    if (resolved) {
      console.log(`[cc-browser] Alias "${args.workspace}" resolved to ${resolved.browser}-${resolved.workspace}`);
      args.browser = resolved.browser;
      args.workspace = resolved.workspace;
    }
  }

  // If browser and workspace are set, try to read ports from workspace.json
  if (args.browser && args.workspace) {
    const config = readWorkspaceConfig(args.browser, args.workspace);
    if (config) {
      if (!args.port && config.daemonPort) daemonPort = config.daemonPort;
      if (config.cdpPort) defaultDaemonCdpPort = config.cdpPort;
    }
    defaultDaemonBrowser = args.browser;
    defaultDaemonWorkspace = args.workspace;
  }

  actualDaemonPort = daemonPort;

  const sessionDir = (args.browser && args.workspace)
    ? getWorkspaceDir(args.browser, args.workspace)
    : null;

  if (sessionDir) {
    loadSessions(sessionDir);
    console.log(`[cc-browser] Sessions loaded (${sessionCount()} active)`);
  }

  startCleanupTimer(async (tabIds) => {
    if (!activeCdpPort) return;
    const cdpUrl = `http://127.0.0.1:${activeCdpPort}`;
    for (const tabId of tabIds) {
      try {
        await closePageByTargetIdViaPlaywright({ cdpUrl, targetId: tabId });
      } catch {
        // Tab may already be closed
      }
    }
  });

  const server = createServer(handleRequest);

  server.listen(daemonPort, '127.0.0.1', () => {
    console.log(`[cc-browser] Daemon listening on http://127.0.0.1:${daemonPort}`);
    if (args.browser && args.workspace) {
      const config = readWorkspaceConfig(args.browser, args.workspace);
      const cdpPort = config?.cdpPort || DEFAULT_CDP_PORT;
      console.log(`[cc-browser] Workspace: ${args.browser}-${args.workspace} (CDP: ${cdpPort})`);
    }
    console.log('[cc-browser] Ready for commands');
  });

  process.on('SIGINT', async () => {
    console.log('\n[cc-browser] Shutting down...');
    stopCleanupTimer();
    if (sessionDir) persistSessions(sessionDir);
    await disconnectBrowser();
    server.close();
    process.exit(0);
  });

  process.on('SIGTERM', async () => {
    console.log('\n[cc-browser] Shutting down...');
    stopCleanupTimer();
    if (sessionDir) persistSessions(sessionDir);
    await disconnectBrowser();
    server.close();
    process.exit(0);
  });
}

// ---------------------------------------------------------------------------
// CLI Commands
// ---------------------------------------------------------------------------

const commands = {
  help: () => {
    console.log(`
cc-browser - Fast browser automation for Claude Code

USAGE:
  cc-browser <command> [options]

DAEMON:
  cc-browser daemon              Start the daemon (keeps running)
  cc-browser status              Check daemon and browser status

BROWSER LIFECYCLE:
  cc-browser start --workspace acmeflow
  cc-browser start --browser chrome --workspace personal
  cc-browser start --incognito             Start in incognito mode (no saved data)
  cc-browser workspaces                    List configured workspaces
  cc-browser profiles [--browser chrome]   List Chrome built-in profiles
  cc-browser stop

NAVIGATION:
  cc-browser navigate --url <url>
  cc-browser snapshot [--interactive]
  cc-browser click --ref <e1>
  cc-browser type --ref <e1> --text "hello"

RECORD & REPLAY:
  cc-browser record start                          Start recording interactions
  cc-browser record stop --output login-flow.json  Stop and save recording
  cc-browser replay --file login-flow.json         Replay a recording

OPTIONS:
  --port <port>       Daemon port (default: from workspace.json)
  --browser <name>    Browser: chrome, edge
  --workspace <name>  Named workspace

EXAMPLES:
  cc-browser daemon --workspace acmeflow
  cc-browser start --workspace acmeflow
  cc-browser navigate --url "https://example.com"
`);
  },

  daemon: (args) => {
    startDaemon(args);
  },

  status: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('GET', '/', null, port);
    output(result);
  },

  browsers: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('GET', '/browsers', null, port);
    output(result);
  },

  profiles: async (args) => {
    const port = getDaemonPort(args);
    const browser = args.browser || 'chrome';
    const result = await request('GET', `/profiles?browser=${browser}`, null, port);
    output(result);
  },

  // List configured cc-browser workspaces
  workspaces: async (args) => {
    const localAppData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
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

  favorites: async (args) => {
    const browser = args.browser || 'edge';
    const workspace = args.workspace;

    if (!workspace) {
      output({ success: false, error: 'Workspace name required. Use --workspace <name>' });
      return;
    }

    const workspaceDir = getWorkspaceDir(browser, workspace);
    const workspaceJsonPath = join(workspaceDir, 'workspace.json');

    if (!existsSync(workspaceJsonPath)) {
      output({ success: false, error: `Workspace not found: ${workspaceJsonPath}` });
      return;
    }

    const data = JSON.parse(readFileSync(workspaceJsonPath, 'utf8'));
    output({
      success: true,
      browser,
      workspace,
      favorites: data.favorites || [],
      name: data.name,
      purpose: data.purpose,
    });
  },

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
      incognito: args.incognito || false,
    }, port);
    output(result);
  },

  stop: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/stop', {}, port);
    output(result);
  },

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

  reload: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/reload', { tab: args.tab, waitUntil: args.waitUntil, timeout: args.timeout }, port);
    output(result);
  },

  back: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/back', { tab: args.tab }, port);
    output(result);
  },

  forward: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/forward', { tab: args.tab }, port);
    output(result);
  },

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

  info: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/info', { tab: args.tab }, port);
    output(result);
  },

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

  press: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/press', { key: args.key, tab: args.tab, delay: args.delay }, port);
    output(result);
  },

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

  evaluate: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/evaluate', {
      fn: args.js || args.fn || args.code,
      ref: args.ref,
      tab: args.tab,
    }, port);
    output(result);
  },

  fill: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/fill', { fields: args.fields, tab: args.tab, timeout: args.timeout }, port);
    output(result);
  },

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

  'screenshot-labels': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/screenshot-labels', {
      maxLabels: args.maxLabels,
      type: args.type,
      tab: args.tab,
    }, port);
    output(result);
  },

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

  resize: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/resize', { width: args.width, height: args.height, tab: args.tab }, port);
    output(result);
  },

  tabs: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs', {}, port);
    output(result);
  },

  'tabs-open': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/open', { url: args.url, session: args.session }, port);
    output(result);
  },

  'tabs-close': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/close', { tab: args.tab }, port);
    output(result);
  },

  'tabs-focus': async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/tabs/focus', { tab: args.tab }, port);
    output(result);
  },

  text: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/text', { selector: args.selector, tab: args.tab }, port);
    output(result);
  },

  html: async (args) => {
    const port = getDaemonPort(args);
    const result = await request('POST', '/html', { selector: args.selector, outer: args.outer, tab: args.tab }, port);
    output(result);
  },

  session: async (args) => {
    const port = getDaemonPort(args);
    const subcommand = args._[1];

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
      const result = await request('POST', '/sessions/close', { session: args.session }, port);
      output(result);
    } else if (subcommand === 'heartbeat') {
      if (!args.session) {
        outputError('Usage: cc-browser session heartbeat --session <id>');
        return;
      }
      const result = await request('POST', '/sessions/heartbeat', { session: args.session }, port);
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
      const result = await request('POST', '/record/start', { tab: args.tab }, port);
      output(result);
    } else if (subcommand === 'stop') {
      const result = await request('POST', '/record/stop', { tab: args.tab }, port);
      if (result.success && result.steps) {
        const recording = {
          name: args.name || '',
          recordedAt: result.recordedAt,
          steps: result.steps,
        };
        if (args.output) {
          const outPath = resolve(args.output);
          writeFileSync(outPath, JSON.stringify(recording, null, 2));
          console.error(`Recording saved to: ${outPath} (${recording.steps.length} steps)`);
        } else {
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

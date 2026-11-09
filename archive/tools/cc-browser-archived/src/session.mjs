// CC Browser - Session Management
// Adapted from: OpenClaw src/browser/pw-session.ts
// Manages Playwright browser connection, page state, and element refs

import { chromium } from 'playwright-core';

// ---------------------------------------------------------------------------
// Types (JSDoc for IDE support)
// ---------------------------------------------------------------------------

/**
 * @typedef {Object} BrowserConsoleMessage
 * @property {string} type
 * @property {string} text
 * @property {string} timestamp
 * @property {{url?: string, lineNumber?: number, columnNumber?: number}} [location]
 */

/**
 * @typedef {Object} BrowserPageError
 * @property {string} message
 * @property {string} [name]
 * @property {string} [stack]
 * @property {string} timestamp
 */

/**
 * @typedef {Object} BrowserNetworkRequest
 * @property {string} id
 * @property {string} timestamp
 * @property {string} method
 * @property {string} url
 * @property {string} [resourceType]
 * @property {number} [status]
 * @property {boolean} [ok]
 * @property {string} [failureText]
 */

/**
 * @typedef {Object} RoleRef
 * @property {string} role
 * @property {string} [name]
 * @property {number} [nth]
 */

/**
 * @typedef {Object} PageState
 * @property {BrowserConsoleMessage[]} console
 * @property {BrowserPageError[]} errors
 * @property {BrowserNetworkRequest[]} requests
 * @property {WeakMap<object, string>} requestIds
 * @property {number} nextRequestId
 * @property {Record<string, RoleRef>} [roleRefs]
 * @property {'role'|'aria'} [roleRefsMode]
 * @property {string} [roleRefsFrameSelector]
 */

// ---------------------------------------------------------------------------
// Mode State
// ---------------------------------------------------------------------------

let currentMode = 'human'; // 'fast' | 'human' | 'stealth'

export function getCurrentMode() {
  return currentMode;
}

export function setCurrentMode(mode) {
  if (!['fast', 'human', 'stealth'].includes(mode)) {
    throw new Error(`Invalid mode: ${mode}. Must be fast, human, or stealth`);
  }
  currentMode = mode;
}

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** @type {WeakMap<object, PageState>} */
const pageStates = new WeakMap();

/** @type {WeakSet<object>} */
const observedPages = new WeakSet();

/** @type {Map<string, {refs: Record<string, RoleRef>, frameSelector?: string, mode?: string}>} */
const roleRefsByTarget = new Map();
const MAX_ROLE_REFS_CACHE = 50;

const MAX_CONSOLE_MESSAGES = 500;
const MAX_PAGE_ERRORS = 200;
const MAX_NETWORK_REQUESTS = 500;

/** @type {{browser: object, cdpUrl: string} | null} */
let cached = null;

/** @type {Promise<{browser: object, cdpUrl: string}> | null} */
let connecting = null;

/** @type {string | null} The targetId of the most recently focused tab */
let activeTargetId = null;

/** @type {WeakSet<object>} Track contexts with webdriver masking applied */
const maskedContexts = new WeakSet();

/** @type {WeakSet<object>} Track contexts with workspace indicator applied */
const indicatedContexts = new WeakSet();

/** @type {string|null} Active workspace name for indicator bar */
let indicatorWorkspace = null;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normalizeCdpUrl(raw) {
  return raw.replace(/\/$/, '');
}

function roleRefsKey(cdpUrl, targetId) {
  return `${normalizeCdpUrl(cdpUrl)}::${targetId}`;
}

// ---------------------------------------------------------------------------
// Role Refs Management
// ---------------------------------------------------------------------------

export function rememberRoleRefsForTarget(opts) {
  const { cdpUrl, targetId, refs, frameSelector, mode } = opts;
  const tid = String(targetId || '').trim();
  if (!tid) return;

  roleRefsByTarget.set(roleRefsKey(cdpUrl, tid), {
    refs,
    ...(frameSelector ? { frameSelector } : {}),
    ...(mode ? { mode } : {}),
  });

  // LRU-like cleanup
  while (roleRefsByTarget.size > MAX_ROLE_REFS_CACHE) {
    const first = roleRefsByTarget.keys().next();
    if (first.done) break;
    roleRefsByTarget.delete(first.value);
  }
}

export function storeRoleRefsForTarget(opts) {
  const { page, cdpUrl, targetId, refs, frameSelector, mode } = opts;
  const state = ensurePageState(page);
  state.roleRefs = refs;
  state.roleRefsFrameSelector = frameSelector;
  state.roleRefsMode = mode;

  if (targetId?.trim()) {
    rememberRoleRefsForTarget({ cdpUrl, targetId, refs, frameSelector, mode });
  }
}

export function restoreRoleRefsForTarget(opts) {
  const { cdpUrl, targetId, page } = opts;
  const tid = String(targetId || '').trim();
  if (!tid) return;

  const cached = roleRefsByTarget.get(roleRefsKey(cdpUrl, tid));
  if (!cached) return;

  const state = ensurePageState(page);
  if (state.roleRefs) return; // Already have refs

  state.roleRefs = cached.refs;
  state.roleRefsFrameSelector = cached.frameSelector;
  state.roleRefsMode = cached.mode;
}

// ---------------------------------------------------------------------------
// Page State Management
// ---------------------------------------------------------------------------

export function ensurePageState(page) {
  const existing = pageStates.get(page);
  if (existing) return existing;

  /** @type {PageState} */
  const state = {
    console: [],
    errors: [],
    requests: [],
    requestIds: new WeakMap(),
    nextRequestId: 0,
  };
  pageStates.set(page, state);

  if (!observedPages.has(page)) {
    observedPages.add(page);

    page.on('console', (msg) => {
      state.console.push({
        type: msg.type(),
        text: msg.text(),
        timestamp: new Date().toISOString(),
        location: msg.location(),
      });
      if (state.console.length > MAX_CONSOLE_MESSAGES) {
        state.console.shift();
      }
    });

    page.on('pageerror', (err) => {
      state.errors.push({
        message: err?.message ? String(err.message) : String(err),
        name: err?.name ? String(err.name) : undefined,
        stack: err?.stack ? String(err.stack) : undefined,
        timestamp: new Date().toISOString(),
      });
      if (state.errors.length > MAX_PAGE_ERRORS) {
        state.errors.shift();
      }
    });

    page.on('request', (req) => {
      state.nextRequestId += 1;
      const id = `r${state.nextRequestId}`;
      state.requestIds.set(req, id);
      state.requests.push({
        id,
        timestamp: new Date().toISOString(),
        method: req.method(),
        url: req.url(),
        resourceType: req.resourceType(),
      });
      if (state.requests.length > MAX_NETWORK_REQUESTS) {
        state.requests.shift();
      }
    });

    page.on('response', (resp) => {
      const req = resp.request();
      const id = state.requestIds.get(req);
      if (!id) return;
      const rec = state.requests.findLast(r => r.id === id);
      if (rec) {
        rec.status = resp.status();
        rec.ok = resp.ok();
      }
    });

    page.on('requestfailed', (req) => {
      const id = state.requestIds.get(req);
      if (!id) return;
      const rec = state.requests.findLast(r => r.id === id);
      if (rec) {
        rec.failureText = req.failure()?.errorText;
        rec.ok = false;
      }
    });

    page.on('close', () => {
      pageStates.delete(page);
      observedPages.delete(page);
    });
  }

  return state;
}

export function getPageState(page) {
  return pageStates.get(page);
}

// ---------------------------------------------------------------------------
// Webdriver Masking
// ---------------------------------------------------------------------------

// --enable-automation sets navigator.webdriver = true, which anti-bot systems
// check. We mask it back to undefined using addInitScript (runs before any
// page scripts) so websites cannot detect the automation info bar.

const WEBDRIVER_MASK_SCRIPT = `
  // Override on instance
  Object.defineProperty(navigator, 'webdriver', {
    get: () => undefined,
    configurable: true,
  });
  // Override on prototype (catches Navigator.prototype.webdriver checks)
  Object.defineProperty(Navigator.prototype, 'webdriver', {
    get: () => undefined,
    configurable: true,
  });
  // Remove the property entirely if possible, then redefine
  try { delete navigator.webdriver; } catch(e) {}
  try { delete Navigator.prototype.webdriver; } catch(e) {}
  Object.defineProperty(Navigator.prototype, 'webdriver', {
    get: () => undefined,
    configurable: true,
  });
`;

// ---------------------------------------------------------------------------
// Stealth Scripts (injected when mode is 'stealth')
// ---------------------------------------------------------------------------

const STEALTH_SCRIPTS = `
  // WebGL vendor/renderer spoofing
  (function() {
    var getParameter = WebGLRenderingContext.prototype.getParameter;
    WebGLRenderingContext.prototype.getParameter = function(param) {
      if (param === 37445) return 'Intel Inc.';
      if (param === 37446) return 'Intel Iris OpenGL Engine';
      return getParameter.call(this, param);
    };
    if (typeof WebGL2RenderingContext !== 'undefined') {
      var getParameter2 = WebGL2RenderingContext.prototype.getParameter;
      WebGL2RenderingContext.prototype.getParameter = function(param) {
        if (param === 37445) return 'Intel Inc.';
        if (param === 37446) return 'Intel Iris OpenGL Engine';
        return getParameter2.call(this, param);
      };
    }
  })();

  // Canvas fingerprint noise
  (function() {
    var originalToDataURL = HTMLCanvasElement.prototype.toDataURL;
    HTMLCanvasElement.prototype.toDataURL = function(type) {
      if (this.width === 0 || this.height === 0) return originalToDataURL.apply(this, arguments);
      var ctx = this.getContext('2d');
      if (ctx) {
        var imageData = ctx.getImageData(0, 0, Math.min(this.width, 2), Math.min(this.height, 2));
        for (var i = 0; i < imageData.data.length; i += 4) {
          imageData.data[i] = imageData.data[i] ^ 1;
        }
        ctx.putImageData(imageData, 0, 0);
      }
      return originalToDataURL.apply(this, arguments);
    };
  })();

  // navigator.plugins spoofing
  (function() {
    Object.defineProperty(navigator, 'plugins', {
      get: function() {
        return [
          { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format', length: 1 },
          { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '', length: 1 },
          { name: 'Native Client', filename: 'internal-nacl-plugin', description: '', length: 2 },
        ];
      },
    });
  })();

  // navigator.languages consistency
  (function() {
    Object.defineProperty(navigator, 'languages', {
      get: function() { return ['en-US', 'en']; },
    });
  })();

  // window.chrome.runtime existence
  (function() {
    if (!window.chrome) window.chrome = {};
    if (!window.chrome.runtime) {
      window.chrome.runtime = {
        connect: function() { return {}; },
        sendMessage: function() {},
      };
    }
  })();
`;

/** @type {WeakSet<object>} Track contexts with stealth scripts applied */
const stealthContexts = new WeakSet();

async function applyStealthScripts(context) {
  if (stealthContexts.has(context)) return;
  stealthContexts.add(context);

  await context.addInitScript(STEALTH_SCRIPTS);

  for (const page of context.pages()) {
    await page.evaluate(STEALTH_SCRIPTS).catch(() => {});
  }
}

async function applyWebdriverMask(context) {
  if (maskedContexts.has(context)) return;
  maskedContexts.add(context);

  // For future navigations in this context
  await context.addInitScript(WEBDRIVER_MASK_SCRIPT);

  // For already-loaded pages
  for (const page of context.pages()) {
    await page.evaluate(WEBDRIVER_MASK_SCRIPT).catch(() => {});
  }
}

// ---------------------------------------------------------------------------
// Workspace Indicator Bar
// ---------------------------------------------------------------------------

// Injects a thin fixed bar at the top of every page showing the workspace name.
// Uses addInitScript so it runs before page scripts on every navigation.

function generateIndicatorScript(workspaceName) {
  const name = JSON.stringify(workspaceName);
  return `(function() {
  if (document.getElementById('cc-bws')) return;
  function inject() {
    if (document.getElementById('cc-bws')) return;
    var b = document.createElement('div');
    b.id = 'cc-bws';
    b.textContent = 'cc-browser // ' + ${name};
    b.style.cssText = 'position:fixed;top:0;left:0;right:0;height:24px;'
      + 'background:linear-gradient(90deg,#1565c0,#1976d2);color:#fff;'
      + 'font:600 11px/24px -apple-system,system-ui,sans-serif;'
      + 'text-align:center;z-index:2147483647;letter-spacing:1px;'
      + 'text-transform:uppercase;box-shadow:0 1px 3px rgba(0,0,0,0.3);'
      + 'pointer-events:none;';
    document.documentElement.style.setProperty('margin-top','24px','important');
    document.documentElement.appendChild(b);
  }
  if (document.body) inject();
  else document.addEventListener('DOMContentLoaded', inject);
})();`;
}

async function applyWorkspaceIndicator(context, workspaceName) {
  if (indicatedContexts.has(context)) return;
  indicatedContexts.add(context);

  const script = generateIndicatorScript(workspaceName);

  // For future navigations
  await context.addInitScript(script);

  // For already-loaded pages
  for (const page of context.pages()) {
    await page.evaluate(script).catch(() => {});
  }
}

/**
 * Enable workspace indicator on all browser contexts.
 * Call after connectBrowser() to show the workspace name bar on every page.
 * @param {string} workspaceName
 */
export async function setWorkspaceIndicator(workspaceName) {
  indicatorWorkspace = workspaceName;
  if (!cached?.browser) return;

  for (const context of cached.browser.contexts()) {
    await applyWorkspaceIndicator(context, workspaceName);
  }
}

// ---------------------------------------------------------------------------
// Browser Connection
// ---------------------------------------------------------------------------

async function fetchWithTimeout(url, timeoutMs = 5000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const res = await fetch(url, { signal: controller.signal });
    return res;
  } finally {
    clearTimeout(timer);
  }
}

async function getChromeWebSocketUrl(cdpUrl, timeoutMs = 5000) {
  try {
    const res = await fetchWithTimeout(`${cdpUrl}/json/version`, timeoutMs);
    if (!res.ok) return null;
    const info = await res.json();
    return info.webSocketDebuggerUrl || null;
  } catch {
    return null;
  }
}

/**
 * Directly set a Playwright browser instance (pipe mode, no CDP URL needed).
 * Used when launchPersistentContext provides the browser directly.
 */
export async function setBrowserDirect(browser) {
  cached = { browser, cdpUrl: 'pipe' };
  connecting = null;

  // Mask navigator.webdriver and observe all pages
  for (const context of browser.contexts()) {
    await applyWebdriverMask(context);
    if (currentMode === 'stealth') {
      await applyStealthScripts(context);
    }
    for (const page of context.pages()) {
      ensurePageState(page);
      if (currentMode === 'stealth') {
        try {
          const session = await page.context().newCDPSession(page);
          await session.send('Page.addScriptToEvaluateOnNewDocument', {
            source: `
              Object.defineProperty(Navigator.prototype, 'webdriver', {
                get: () => undefined,
                configurable: true,
              });
            `,
          });
          await session.send('Runtime.evaluate', {
            expression: `
              Object.defineProperty(Navigator.prototype, 'webdriver', {
                get: () => undefined,
                configurable: true,
              });
            `,
          });
          await session.detach().catch(() => {});
        } catch {
          // Best effort
        }
      }
    }
    context.on('page', (page) => ensurePageState(page));
  }

  browser.on('disconnected', () => {
    if (cached?.browser === browser) {
      cached = null;
    }
  });

  return cached;
}

export async function connectBrowser(cdpUrl) {
  // If we have a pipe-mode browser, verify it is alive and reuse it
  if (cached?.cdpUrl === 'pipe') {
    try {
      // Verify the browser is still connected by checking contexts
      cached.browser.contexts();
      return cached;
    } catch {
      // Pipe browser is dead, clear cache
      cached = null;
    }
  }

  const normalized = normalizeCdpUrl(cdpUrl);

  if (cached?.cdpUrl === normalized) {
    return cached;
  }

  if (connecting) {
    return await connecting;
  }

  const connectWithRetry = async () => {
    let lastErr;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const timeout = 5000 + attempt * 2000;
        const wsUrl = await getChromeWebSocketUrl(normalized, timeout);
        const endpoint = wsUrl || normalized;
        const browser = await chromium.connectOverCDP(endpoint, { timeout });

        const connected = { browser, cdpUrl: normalized };
        cached = connected;

        // Mask navigator.webdriver and observe all pages
        for (const context of browser.contexts()) {
          await applyWebdriverMask(context);
          // Apply stealth scripts if in stealth mode
          if (currentMode === 'stealth') {
            await applyStealthScripts(context);
          }
          for (const page of context.pages()) {
            ensurePageState(page);
            // Use CDP to disable webdriver flag at browser level
            if (currentMode === 'stealth') {
              try {
                const session = await page.context().newCDPSession(page);
                // Inject via CDP before any JS runs (even earlier than addInitScript)
                await session.send('Page.addScriptToEvaluateOnNewDocument', {
                  source: `
                    Object.defineProperty(Navigator.prototype, 'webdriver', {
                      get: () => undefined,
                      configurable: true,
                    });
                  `,
                });
                // Also run it immediately on the current page
                await session.send('Runtime.evaluate', {
                  expression: `
                    Object.defineProperty(Navigator.prototype, 'webdriver', {
                      get: () => undefined,
                      configurable: true,
                    });
                  `,
                });
                await session.detach().catch(() => {});
              } catch {
                // Best effort
              }
            }
          }
          context.on('page', (page) => ensurePageState(page));
        }

        browser.on('disconnected', () => {
          if (cached?.browser === browser) {
            cached = null;
          }
        });

        return connected;
      } catch (err) {
        lastErr = err;
        const delay = 250 + attempt * 250;
        await new Promise((r) => setTimeout(r, delay));
      }
    }
    throw lastErr || new Error('CDP connect failed');
  };

  connecting = connectWithRetry().finally(() => {
    connecting = null;
  });

  return await connecting;
}

export async function disconnectBrowser() {
  const cur = cached;
  cached = null;
  activeTargetId = null;
  if (cur?.browser) {
    await cur.browser.close().catch(() => {});
  }
}

export function getCachedBrowser() {
  return cached;
}

// ---------------------------------------------------------------------------
// Page Resolution
// ---------------------------------------------------------------------------

async function getAllPages(browser) {
  const contexts = browser.contexts();
  return contexts.flatMap((c) => c.pages());
}

async function pageTargetId(page) {
  const session = await page.context().newCDPSession(page);
  try {
    const info = await session.send('Target.getTargetInfo');
    return String(info?.targetInfo?.targetId || '').trim() || null;
  } finally {
    await session.detach().catch(() => {});
  }
}

async function findPageByTargetId(browser, targetId, cdpUrl) {
  const pages = await getAllPages(browser);

  // Try CDP session approach
  for (const page of pages) {
    const tid = await pageTargetId(page).catch(() => null);
    if (tid && tid === targetId) {
      return page;
    }
  }

  // Fallback: URL-based matching using /json/list
  if (cdpUrl) {
    try {
      const baseUrl = cdpUrl.replace(/\/+$/, '').replace(/^ws:/, 'http:').replace(/\/cdp$/, '');
      const res = await fetchWithTimeout(`${baseUrl}/json/list`, 3000);
      if (res.ok) {
        const targets = await res.json();
        const target = targets.find((t) => t.id === targetId);
        if (target) {
          const urlMatch = pages.filter((p) => p.url() === target.url);
          if (urlMatch.length === 1) return urlMatch[0];
          if (urlMatch.length > 1) {
            const sameUrlTargets = targets.filter((t) => t.url === target.url);
            if (sameUrlTargets.length === urlMatch.length) {
              const idx = sameUrlTargets.findIndex((t) => t.id === targetId);
              if (idx >= 0 && idx < urlMatch.length) return urlMatch[idx];
            }
          }
        }
      }
    } catch {
      // Ignore
    }
  }

  return null;
}

export async function getPageForTargetId(opts) {
  const { cdpUrl, targetId } = opts;
  const { browser } = await connectBrowser(cdpUrl);
  const pages = await getAllPages(browser);

  if (!pages.length) {
    throw new Error('No pages available in the connected browser.');
  }

  const resolvedId = targetId || activeTargetId;

  if (!resolvedId) {
    return pages[0];
  }

  const found = await findPageByTargetId(browser, resolvedId, cdpUrl);
  if (!found) {
    // If the active tab was closed/gone, clear it and fall back
    if (!targetId && activeTargetId) {
      activeTargetId = null;
    }
    if (pages.length === 1) return pages[0];
    if (!targetId) return pages[0];
    throw new Error('Tab not found');
  }
  return found;
}

// ---------------------------------------------------------------------------
// Ref Locator
// ---------------------------------------------------------------------------

export function refLocator(page, ref) {
  const normalized = ref.startsWith('@')
    ? ref.slice(1)
    : ref.startsWith('ref=')
      ? ref.slice(4)
      : ref;

  if (/^e\d+$/i.test(normalized)) {
    const state = pageStates.get(page);
    if (state?.roleRefsMode === 'aria') {
      const scope = state.roleRefsFrameSelector
        ? page.frameLocator(state.roleRefsFrameSelector)
        : page;
      return scope.locator(`aria-ref=${normalized}`);
    }

    const info = state?.roleRefs?.[normalized.toLowerCase()];
    if (!info) {
      throw new Error(
        `Unknown ref "${normalized}". Run a new snapshot to get current element refs.`
      );
    }

    const scope = state?.roleRefsFrameSelector
      ? page.frameLocator(state.roleRefsFrameSelector)
      : page;

    const locator = info.name
      ? scope.getByRole(info.role, { name: info.name, exact: true })
      : scope.getByRole(info.role);

    return info.nth !== undefined ? locator.nth(info.nth) : locator;
  }

  // Fallback: aria-ref
  return page.locator(`aria-ref=${normalized}`);
}

// ---------------------------------------------------------------------------
// Tab Operations via Playwright
// ---------------------------------------------------------------------------

export async function listPagesViaPlaywright(opts) {
  const { cdpUrl } = opts;
  const { browser } = await connectBrowser(cdpUrl);
  const pages = await getAllPages(browser);
  const results = [];

  for (const page of pages) {
    const tid = await pageTargetId(page).catch(() => null);
    if (tid) {
      results.push({
        targetId: tid,
        title: await page.title().catch(() => ''),
        url: page.url(),
        type: 'page',
      });
    }
  }
  return results;
}

export async function createPageViaPlaywright(opts) {
  const { cdpUrl, url } = opts;
  const { browser } = await connectBrowser(cdpUrl);
  const context = browser.contexts()[0] || (await browser.newContext());
  await applyWebdriverMask(context);
  if (indicatorWorkspace) {
    await applyWorkspaceIndicator(context, indicatorWorkspace);
  }
  const page = await context.newPage();
  ensurePageState(page);

  const targetUrl = (url || '').trim() || 'about:blank';
  if (targetUrl !== 'about:blank') {
    await page.goto(targetUrl, { timeout: 30000 }).catch(() => {});
  }

  const tid = await pageTargetId(page).catch(() => null);
  if (!tid) {
    throw new Error('Failed to get targetId for new page');
  }

  activeTargetId = tid;

  return {
    targetId: tid,
    title: await page.title().catch(() => ''),
    url: page.url(),
    type: 'page',
  };
}

export async function closePageByTargetIdViaPlaywright(opts) {
  const { cdpUrl, targetId } = opts;
  const { browser } = await connectBrowser(cdpUrl);
  const page = await findPageByTargetId(browser, targetId, cdpUrl);
  if (!page) {
    throw new Error('Tab not found');
  }
  if (activeTargetId === targetId) {
    activeTargetId = null;
  }
  await page.close();
}

export async function focusPageByTargetIdViaPlaywright(opts) {
  const { cdpUrl, targetId } = opts;
  const { browser } = await connectBrowser(cdpUrl);
  const page = await findPageByTargetId(browser, targetId, cdpUrl);
  if (!page) {
    throw new Error('Tab not found');
  }
  await page.bringToFront();
  activeTargetId = targetId;
}

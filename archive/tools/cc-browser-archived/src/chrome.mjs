// CC Browser - Chrome Launch & Management
// Adapted from: OpenClaw src/browser/chrome.ts and chrome.executables.ts
// Handles Chrome detection, launch, and connection

import { execSync } from 'child_process';
import { existsSync, mkdirSync, rmSync, readdirSync, readFileSync } from 'fs';
import { rm } from 'fs/promises';
import { join } from 'path';
import { tmpdir, homedir } from 'os';
import { createServer } from 'net';
import { chromium } from 'playwright-core';

// ---------------------------------------------------------------------------
// Port Availability Check
// ---------------------------------------------------------------------------

export async function isPortAvailable(port) {
  return new Promise((resolve) => {
    const server = createServer();
    server.once('error', () => resolve(false));
    server.once('listening', () => {
      server.close(() => resolve(true));
    });
    server.listen(port, '127.0.0.1');
  });
}

// ---------------------------------------------------------------------------
// Chrome Process Detection
// ---------------------------------------------------------------------------

export function isChromeRunning() {
  try {
    if (process.platform === 'win32') {
      const output = execSync('tasklist /FI "IMAGENAME eq chrome.exe" /FO CSV /NH', {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return output.includes('chrome.exe');
    } else if (process.platform === 'darwin') {
      const output = execSync('pgrep -x "Google Chrome"', {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return output.trim().length > 0;
    } else {
      const output = execSync('pgrep -x chrome', {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return output.trim().length > 0;
    }
  } catch {
    return false;
  }
}

export function isEdgeRunning() {
  try {
    if (process.platform === 'win32') {
      const output = execSync('tasklist /FI "IMAGENAME eq msedge.exe" /FO CSV /NH', {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return output.includes('msedge.exe');
    }
    return false;
  } catch {
    return false;
  }
}

export function isBrowserRunning(browserKind = 'chrome') {
  if (browserKind === 'edge') {
    return isEdgeRunning();
  }
  return isChromeRunning();
}

// ---------------------------------------------------------------------------
// Chrome Executable Detection
// ---------------------------------------------------------------------------

const WINDOWS_CHROME_PATHS = [
  // Chrome
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
  process.env['ProgramFiles(x86)'] && join(process.env['ProgramFiles(x86)'], 'Google', 'Chrome', 'Application', 'chrome.exe'),
  // Edge
  process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env['ProgramFiles'] && join(process.env['ProgramFiles'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
  process.env['ProgramFiles(x86)'] && join(process.env['ProgramFiles(x86)'], 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
].filter(Boolean);

const MACOS_CHROME_PATHS = [
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
];

const LINUX_CHROME_PATHS = [
  '/usr/bin/google-chrome',
  '/usr/bin/google-chrome-stable',
  '/usr/bin/chromium',
  '/usr/bin/chromium-browser',
  '/usr/bin/microsoft-edge',
];

export function findChromeExecutable(preferredBrowser = null) {
  const platform = process.platform;
  let candidates;

  if (platform === 'win32') {
    candidates = WINDOWS_CHROME_PATHS;
  } else if (platform === 'darwin') {
    candidates = MACOS_CHROME_PATHS;
  } else {
    candidates = LINUX_CHROME_PATHS;
  }

  // If a preferred browser is specified, filter and reorder candidates
  if (preferredBrowser) {
    const pref = preferredBrowser.toLowerCase();
    const browserPatterns = {
      'chrome': ['chrome.exe', 'google chrome', 'google-chrome'],
      'edge': ['msedge.exe', 'microsoft edge', 'microsoft-edge'],
    };

    const patterns = browserPatterns[pref];
    if (patterns) {
      // Find matching candidate
      for (const path of candidates) {
        if (path) {
          const lower = path.toLowerCase();
          if (patterns.some(p => lower.includes(p))) {
            if (existsSync(path)) {
              return { path, kind: pref };
            }
          }
        }
      }
      // Preferred browser not found
      throw new Error(`Browser "${preferredBrowser}" not found. Available: chrome, edge`);
    }
  }

  // Default: return first available
  for (const path of candidates) {
    if (path && existsSync(path)) {
      const kind = path.toLowerCase().includes('edge')
        ? 'edge'
        : 'chrome';
      return { path, kind };
    }
  }

  return null;
}

export function listAvailableBrowsers() {
  const platform = process.platform;
  let candidates;

  if (platform === 'win32') {
    candidates = WINDOWS_CHROME_PATHS;
  } else if (platform === 'darwin') {
    candidates = MACOS_CHROME_PATHS;
  } else {
    candidates = LINUX_CHROME_PATHS;
  }

  const available = [];
  for (const path of candidates) {
    if (path && existsSync(path)) {
      const kind = path.toLowerCase().includes('edge')
        ? 'edge'
        : 'chrome';
      available.push({ kind, path });
    }
  }
  return available;
}

// ---------------------------------------------------------------------------
// Chrome Launch
// ---------------------------------------------------------------------------

const DEFAULT_CDP_PORT = 9222;

// ---------------------------------------------------------------------------
// System Chrome Profile Detection
// ---------------------------------------------------------------------------

export function getSystemChromeUserDataDir(browserKind = 'chrome') {
  const platform = process.platform;

  if (platform === 'win32') {
    const localAppData = process.env.LOCALAPPDATA;
    if (!localAppData) return null;

    if (browserKind === 'edge') {
      return join(localAppData, 'Microsoft', 'Edge', 'User Data');
    } else {
      return join(localAppData, 'Google', 'Chrome', 'User Data');
    }
  } else if (platform === 'darwin') {
    const home = homedir();
    if (browserKind === 'edge') {
      return join(home, 'Library', 'Application Support', 'Microsoft Edge');
    } else {
      return join(home, 'Library', 'Application Support', 'Google', 'Chrome');
    }
  } else {
    const home = homedir();
    if (browserKind === 'edge') {
      return join(home, '.config', 'microsoft-edge');
    } else {
      return join(home, '.config', 'google-chrome');
    }
  }
}

export function listChromeProfiles(browserKind = 'chrome') {
  const userDataDir = getSystemChromeUserDataDir(browserKind);
  if (!userDataDir || !existsSync(userDataDir)) {
    return [];
  }

  const profiles = [];
  const entries = readdirSync(userDataDir, { withFileTypes: true });

  for (const entry of entries) {
    if (!entry.isDirectory()) continue;

    // Profile directories are "Default", "Profile 1", "Profile 2", etc.
    if (entry.name === 'Default' || entry.name.startsWith('Profile ')) {
      const profileDir = join(userDataDir, entry.name);
      const prefsPath = join(profileDir, 'Preferences');

      let profileName = entry.name;
      let email = null;

      // Try to read profile name from Preferences
      if (existsSync(prefsPath)) {
        try {
          const prefs = JSON.parse(readFileSync(prefsPath, 'utf-8'));
          if (prefs.profile?.name) {
            profileName = prefs.profile.name;
          }
          if (prefs.account_info?.[0]?.email) {
            email = prefs.account_info[0].email;
          }
        } catch {
          // Ignore parse errors
        }
      }

      profiles.push({
        directory: entry.name,
        name: profileName,
        email: email,
        path: profileDir,
      });
    }
  }

  return profiles;
}

export function getChromeUserDataDir(browserKind = 'chrome', workspaceName = 'default') {
  // Use LocalAppData for persistent storage (survives reboots, keeps logins)
  const appData = process.env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local');
  const base = join(appData, 'cc-browser');
  const dir = join(base, `${browserKind}-${workspaceName}`);
  if (!existsSync(dir)) {
    mkdirSync(dir, { recursive: true });
  }
  return dir;
}

export function resetChromeUserDataDir(browserKind = 'chrome', workspaceName = 'default') {
  const dir = getChromeUserDataDir(browserKind, workspaceName);
  if (existsSync(dir)) {
    rmSync(dir, { recursive: true, force: true });
  }
  mkdirSync(dir, { recursive: true });
  return dir;
}

// ---------------------------------------------------------------------------
// User Data Dir Cleanup (GPU cache corruption + stale lockfiles)
// ---------------------------------------------------------------------------

// Files/dirs that corrupt or lock after force-kill
const CLEANUP_TARGETS = [
  'GrShaderCache',
  'GraphiteDawnCache',
  'lockfile',
  'SingletonLock',
  'SingletonSocket',
  'SingletonCookie',
];

export async function cleanUserDataDir(userDataDir) {
  if (!userDataDir || !existsSync(userDataDir)) return;
  const results = [];
  for (const target of CLEANUP_TARGETS) {
    const targetPath = join(userDataDir, target);
    try {
      await rm(targetPath, { recursive: true, force: true });
      results.push(target);
    } catch {
      // Target did not exist or was already gone -- no-op
    }
  }
  return results;
}

export async function launchChrome(opts = {}) {
  const {
    port = DEFAULT_CDP_PORT,
    headless = false,
    executablePath,
    browser,
    workspaceName = 'default',
    useSystemProfile = false,
    profileDir = null,
    incognito = false,
  } = opts;

  // Find Chrome
  let chromePath = executablePath;
  let browserKind = browser || 'chrome';

  if (!chromePath) {
    const detected = findChromeExecutable(browser);
    if (!detected) {
      throw new Error(
        'Chrome/Edge not found. Install Chrome or specify --exe path.'
      );
    }
    chromePath = detected.path;
    browserKind = detected.kind;
  }

  // User data directory
  let userDataDir;
  let profileDirArg = null;

  if (useSystemProfile || profileDir) {
    // Use system Chrome profile
    userDataDir = getSystemChromeUserDataDir(browserKind);
    if (!userDataDir || !existsSync(userDataDir)) {
      throw new Error(`System ${browserKind} user data directory not found`);
    }
    profileDirArg = profileDir || 'Default';

    // Check if browser is already running - profile will be locked
    if (isBrowserRunning(browserKind)) {
      const browserName = browserKind === 'edge' ? 'Edge' : 'Chrome';
      throw new Error(
        `${browserName} is already running. Close all ${browserName} windows first to use --profileDir.\n` +
        `Alternatively, use 'cc-browser start' without --profileDir for an isolated session.`
      );
    }
  } else if (incognito) {
    // Use a temp directory that Chrome needs but won't persist meaningful data
    userDataDir = join(tmpdir(), `cc-browser-incognito-${Date.now()}`);
    mkdirSync(userDataDir, { recursive: true });
  } else {
    // Use isolated persistent workspace
    userDataDir = getChromeUserDataDir(browserKind, workspaceName);
  }

  // Launch arguments (no --remote-debugging-port: Playwright uses pipe transport)
  const args = [
    '--no-first-run',
    '--no-default-browser-check',
  ];

  // Add profile directory if using system profile
  if (profileDirArg) {
    args.push(`--profile-directory=${profileDirArg}`);
  } else {
    // Only disable sync for isolated profiles
    args.push('--disable-sync');
  }

  if (incognito) {
    args.push('--incognito');
  }

  args.push(
    '--disable-features=TranslateUI',
  );

  // Edge-specific: prevent merging into existing Edge instance
  if (browserKind === 'edge') {
    args.push(
      '--disable-features=msEdgeSingleSignOn,msEdgeWorkspacesIntegration',
      '--no-service-autorun',
      '--disable-background-mode',
    );
  }

  // Determine Playwright channel for Edge
  // Playwright injects many default args designed for test automation that are
  // harmful for our "real browser with automation" use case:
  //   --no-sandbox             -> "stability will suffer" warning bar
  //   --disable-extensions     -> breaks user's installed extensions
  //   --disable-background-networking, --disable-client-side-phishing-detection
  //                            -> detectable by anti-bot systems (real browsers have these)
  //   --disable-component-extensions-with-background-pages -> detectable
  //   --disable-default-apps   -> unnecessary restriction
  //   --disable-popup-blocking -> changes user-facing behavior
  //   --disable-infobars       -> detectable flag (no longer works in modern Chrome)
  //   --enable-automation      -> sets navigator.webdriver=true (#1 bot signal)
  //   --enable-unsafe-swiftshader -> detectable (CPU-based WebGL fallback)
  const launchOpts = {
    executablePath: chromePath,
    headless,
    args,
    ignoreDefaultArgs: [
      '--enable-automation',
      '--no-sandbox',
      '--disable-extensions',
      '--disable-background-networking',
      '--disable-client-side-phishing-detection',
      '--disable-component-extensions-with-background-pages',
      '--disable-default-apps',
      '--disable-popup-blocking',
      '--disable-infobars',
      '--enable-unsafe-swiftshader',
    ],
  };

  if (browserKind === 'edge') {
    launchOpts.channel = 'msedge';
  }

  // Clean corruption-prone files before launch (GPU cache, stale lockfiles)
  await cleanUserDataDir(userDataDir);

  // Launch via Playwright persistent context (uses --remote-debugging-pipe, not port)
  const context = await chromium.launchPersistentContext(userDataDir, launchOpts);

  // Store context and userDataDir for lifecycle management
  pipeLaunchedContext = context;
  pipeLaunchedUserDataDir = userDataDir;

  if (incognito) {
    setIncognitoUserDataDir(userDataDir);
  }

  // Get tab info via CDP sessions on context pages
  const pages = context.pages();
  const tabs = [];
  for (const page of pages) {
    try {
      const session = await context.newCDPSession(page);
      const info = await session.send('Target.getTargetInfo');
      const targetId = info?.targetInfo?.targetId || null;
      await session.detach().catch(() => {});
      if (targetId) {
        tabs.push({
          targetId,
          title: await page.title().catch(() => ''),
          url: page.url(),
        });
      }
    } catch {
      // Skip pages we cannot inspect
    }
  }

  return {
    browser: context.browser(),
    context,
    browserKind,
    userDataDir,
    profileDir: profileDirArg,
    incognito,
    tabs,
    activeTab: tabs[0]?.targetId || null,
  };
}

// ---------------------------------------------------------------------------
// Check if Chrome is already running with CDP
// ---------------------------------------------------------------------------

export async function checkChromeRunning(port = DEFAULT_CDP_PORT) {
  // Check pipe-launched context first (active CDP probe, not just isConnected)
  const ctx = pipeLaunchedContext;
  if (ctx) {
    try {
      const browser = ctx.browser();
      if (!browser || !browser.isConnected()) {
        throw new Error('disconnected');
      }
      // Active CDP probe -- ctx.pages() requires a live pipe; timeout protects
      // against hung connections where isConnected() lies.
      await Promise.race([
        ctx.pages(),
        new Promise((_, reject) =>
          setTimeout(() => reject(new Error('timeout')), 2000)
        ),
      ]);

      // Context is alive - gather tab info
      const pages = ctx.pages();
      const tabs = [];
      for (const page of pages) {
        try {
          const session = await ctx.newCDPSession(page);
          const info = await session.send('Target.getTargetInfo');
          const targetId = info?.targetInfo?.targetId || null;
          await session.detach().catch(() => {});
          if (targetId) {
            tabs.push({
              targetId,
              title: await page.title().catch(() => ''),
              url: page.url(),
            });
          }
        } catch {
          // Skip
        }
      }
      return {
        running: true,
        pipeMode: true,
        browser: ctx.browser(),
        context: ctx,
        tabs,
        activeTab: tabs[0]?.targetId || null,
      };
    } catch {
      // Pipe context is stale/dead -- clear it
      pipeLaunchedContext = null;
      pipeLaunchedUserDataDir = null;
    }
  }

  // Fall back to HTTP port check (legacy / external browsers)
  const cdpUrl = `http://127.0.0.1:${port}`;
  try {
    const res = await fetch(`${cdpUrl}/json/version`, {
      signal: AbortSignal.timeout(1000),
    });
    if (res.ok) {
      const tabsRes = await fetch(`${cdpUrl}/json/list`);
      const tabs = await tabsRes.json();
      const pageTabs = tabs.filter((t) => t.type === 'page');

      return {
        running: true,
        cdpUrl,
        tabs: pageTabs.map((t) => ({
          targetId: t.id,
          title: t.title,
          url: t.url,
        })),
        activeTab: pageTabs[0]?.id || null,
      };
    }
  } catch {
    // Not running
  }
  return { running: false, cdpUrl };
}

// ---------------------------------------------------------------------------
// Ensure Chrome is available (start if needed)
// ---------------------------------------------------------------------------

export async function ensureChromeAvailable(opts = {}) {
  const { port = DEFAULT_CDP_PORT } = opts;

  // Check if already running (pipe context or port)
  const status = await checkChromeRunning(port);
  if (status.running) {
    return {
      started: false,
      ...status,
    };
  }

  // Launch Chrome
  const result = await launchChrome(opts);
  return {
    started: true,
    ...result,
  };
}

// ---------------------------------------------------------------------------
// Stop Chrome
// ---------------------------------------------------------------------------

// Track launched Chrome PID and incognito temp dir
let launchedChromePid = null;
let incognitoUserDataDir = null;

// Track Playwright pipe-launched context (replaces spawn + CDP port)
let pipeLaunchedContext = null;
let pipeLaunchedUserDataDir = null;

export function setLaunchedChromePid(pid) {
  launchedChromePid = pid;
}

export function getLaunchedChromePid() {
  return launchedChromePid;
}

export function setIncognitoUserDataDir(dir) {
  incognitoUserDataDir = dir;
}

export function getIncognitoUserDataDir() {
  return incognitoUserDataDir;
}

export function getPipeLaunchedContext() {
  return pipeLaunchedContext;
}

export function setPipeLaunchedContext(ctx) {
  pipeLaunchedContext = ctx;
}

export async function stopChrome(port = DEFAULT_CDP_PORT) {
  let stopped = false;

  // Try pipe context close first (preferred path)
  if (pipeLaunchedContext) {
    try {
      await pipeLaunchedContext.close();
      stopped = true;
    } catch {
      // Context may already be closed
    }
    pipeLaunchedContext = null;

    // Clean GPU cache and lockfiles after close
    if (pipeLaunchedUserDataDir) {
      await cleanUserDataDir(pipeLaunchedUserDataDir);
      pipeLaunchedUserDataDir = null;
    }
  }

  // If we have the PID, kill the process directly (fallback)
  if (launchedChromePid) {
    try {
      if (process.platform === 'win32') {
        execSync(`taskkill /F /PID ${launchedChromePid} /T`, {
          stdio: 'pipe',
        });
      } else {
        process.kill(launchedChromePid, 'SIGTERM');
      }
      stopped = true;
    } catch {
      // Process might already be dead
    }
    launchedChromePid = null;
  }

  // Legacy: try CDP close via port if neither pipe nor PID worked
  if (!stopped) {
    const cdpUrl = `http://127.0.0.1:${port}`;
    try {
      await fetch(`${cdpUrl}/json/close`, {
        method: 'PUT',
        signal: AbortSignal.timeout(2000),
      });
      stopped = true;
    } catch {
      // CDP close failed
    }

    // Windows: Find and kill process by port if still running
    if (process.platform === 'win32') {
      try {
        const netstatOutput = execSync(`netstat -ano | findstr ":${port}" | findstr "LISTENING"`, {
          encoding: 'utf-8',
          stdio: ['pipe', 'pipe', 'pipe'],
        });
        const match = netstatOutput.match(/LISTENING\s+(\d+)/);
        if (match) {
          const pid = match[1];
          execSync(`taskkill /F /PID ${pid} /T`, { stdio: 'pipe' });
          stopped = true;
        }
      } catch {
        // No process found or already killed
      }
    }

    // Wait a bit and verify port-based shutdown
    await new Promise((r) => setTimeout(r, 500));
    try {
      const res = await fetch(`${cdpUrl}/json/version`, {
        signal: AbortSignal.timeout(1000),
      });
      if (res.ok) {
        stopped = false; // Still running
      }
    } catch {
      stopped = true; // Not responding = stopped
    }
  }

  // Clean up incognito temp directory
  if (stopped && incognitoUserDataDir) {
    try {
      rmSync(incognitoUserDataDir, { recursive: true, force: true });
    } catch {
      // Best effort cleanup
    }
    incognitoUserDataDir = null;
  }

  return { stopped };
}

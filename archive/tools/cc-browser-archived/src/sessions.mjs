// CC Browser - Tab Sessions
// Named groups of tabs for agent cleanup. Pure data module -- no Playwright.

import { randomBytes } from 'crypto';
import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'fs';
import { join } from 'path';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const DEFAULT_TTL_MS = 30 * 60 * 1000; // 30 minutes
const CLEANUP_INTERVAL_MS = 60 * 1000; // 60 seconds

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** @type {Map<string, Session>} */
const sessions = new Map();

/** @type {ReturnType<typeof setInterval> | null} */
let cleanupTimer = null;

// ---------------------------------------------------------------------------
// Session Model
// ---------------------------------------------------------------------------

/**
 * @typedef {Object} Session
 * @property {string} id
 * @property {string} name
 * @property {number} createdAt
 * @property {number} lastActivity
 * @property {number} ttlMs - 0 means never auto-expires
 * @property {string[]} tabIds
 * @property {Record<string, any>} metadata
 */

function generateId() {
  return 'sess_' + randomBytes(4).toString('hex');
}

// ---------------------------------------------------------------------------
// CRUD
// ---------------------------------------------------------------------------

/**
 * Create a new session.
 * @param {Object} opts
 * @param {string} opts.name
 * @param {number} [opts.ttlMs]
 * @param {Record<string, any>} [opts.metadata]
 * @returns {Session}
 */
export function createSession(opts) {
  const { name, ttlMs, metadata } = opts;
  if (!name || !String(name).trim()) {
    throw new Error('Session name is required');
  }

  const now = Date.now();
  const session = {
    id: generateId(),
    name: String(name).trim(),
    createdAt: now,
    lastActivity: now,
    ttlMs: typeof ttlMs === 'number' && ttlMs >= 0 ? ttlMs : DEFAULT_TTL_MS,
    tabIds: [],
    metadata: metadata || {},
  };

  sessions.set(session.id, session);
  return session;
}

/**
 * Get a session by ID.
 * @param {string} id
 * @returns {Session|null}
 */
export function getSession(id) {
  return sessions.get(id) || null;
}

/**
 * List all sessions.
 * @returns {Session[]}
 */
export function listSessions() {
  return Array.from(sessions.values());
}

/**
 * Delete a session (does NOT close its tabs -- caller handles that).
 * @param {string} id
 * @returns {boolean} true if session existed
 */
export function deleteSession(id) {
  return sessions.delete(id);
}

// ---------------------------------------------------------------------------
// Tab Tracking
// ---------------------------------------------------------------------------

/**
 * Add a tab to a session. Updates lastActivity.
 * @param {string} sessionId
 * @param {string} tabId
 * @returns {boolean} true if added
 */
export function addTabToSession(sessionId, tabId) {
  const session = sessions.get(sessionId);
  if (!session) return false;

  if (!session.tabIds.includes(tabId)) {
    session.tabIds.push(tabId);
  }
  session.lastActivity = Date.now();
  return true;
}

/**
 * Remove a tab from whichever session(s) it belongs to.
 * @param {string} tabId
 * @returns {string[]} session IDs that contained this tab
 */
export function removeTabFromSessions(tabId) {
  const affected = [];
  for (const [id, session] of sessions) {
    const idx = session.tabIds.indexOf(tabId);
    if (idx !== -1) {
      session.tabIds.splice(idx, 1);
      affected.push(id);
    }
  }
  return affected;
}

/**
 * Find the session a tab belongs to.
 * @param {string} tabId
 * @returns {Session|null}
 */
export function findSessionForTab(tabId) {
  for (const session of sessions.values()) {
    if (session.tabIds.includes(tabId)) {
      return session;
    }
  }
  return null;
}

// ---------------------------------------------------------------------------
// TTL / Heartbeat
// ---------------------------------------------------------------------------

/**
 * Touch a session to update its lastActivity timestamp.
 * @param {string} sessionId
 * @returns {boolean}
 */
export function touchSession(sessionId) {
  const session = sessions.get(sessionId);
  if (!session) return false;
  session.lastActivity = Date.now();
  return true;
}

/**
 * Find and remove expired sessions. Returns tab IDs that should be closed.
 * @returns {{ sessionId: string, tabIds: string[] }[]}
 */
export function pruneExpiredSessions() {
  const now = Date.now();
  const pruned = [];

  for (const [id, session] of sessions) {
    if (session.ttlMs === 0) continue; // never expires
    const elapsed = now - session.lastActivity;
    if (elapsed >= session.ttlMs) {
      pruned.push({ sessionId: id, tabIds: [...session.tabIds] });
      sessions.delete(id);
    }
  }

  return pruned;
}

/**
 * Remove stale tab IDs from all sessions -- tabs that no longer exist in the browser.
 * @param {string[]} liveTabIds - tab IDs currently open in the browser
 */
export function reconcileTabs(liveTabIds) {
  const liveSet = new Set(liveTabIds);
  for (const session of sessions.values()) {
    session.tabIds = session.tabIds.filter(id => liveSet.has(id));
  }
}

// ---------------------------------------------------------------------------
// Persistence
// ---------------------------------------------------------------------------

const SESSIONS_FILE = 'sessions.json';

/**
 * Save sessions to disk.
 * @param {string} dir - workspace directory
 */
export function persistSessions(dir) {
  if (!dir) return;
  if (!existsSync(dir)) {
    mkdirSync(dir, { recursive: true });
  }
  const data = Array.from(sessions.values());
  writeFileSync(join(dir, SESSIONS_FILE), JSON.stringify(data, null, 2));
}

/**
 * Load sessions from disk (replaces in-memory state).
 * @param {string} dir - workspace directory
 */
export function loadSessions(dir) {
  sessions.clear();
  if (!dir) return;

  const filePath = join(dir, SESSIONS_FILE);
  if (!existsSync(filePath)) return;

  try {
    const raw = readFileSync(filePath, 'utf8');
    const data = JSON.parse(raw);
    if (!Array.isArray(data)) return;

    for (const entry of data) {
      if (entry && entry.id && entry.name) {
        sessions.set(entry.id, {
          id: entry.id,
          name: entry.name,
          createdAt: entry.createdAt || Date.now(),
          lastActivity: entry.lastActivity || Date.now(),
          ttlMs: typeof entry.ttlMs === 'number' ? entry.ttlMs : DEFAULT_TTL_MS,
          tabIds: Array.isArray(entry.tabIds) ? entry.tabIds : [],
          metadata: entry.metadata || {},
        });
      }
    }
  } catch {
    // Corrupted file -- start fresh
  }
}

// ---------------------------------------------------------------------------
// Cleanup Timer
// ---------------------------------------------------------------------------

/**
 * Start the periodic cleanup timer.
 * @param {(tabIds: string[]) => Promise<void>} closeFn - called with tab IDs to close
 */
export function startCleanupTimer(closeFn) {
  stopCleanupTimer();
  cleanupTimer = setInterval(async () => {
    const pruned = pruneExpiredSessions();
    if (pruned.length === 0) return;

    const allTabIds = pruned.flatMap(p => p.tabIds);
    if (allTabIds.length > 0 && closeFn) {
      try {
        await closeFn(allTabIds);
      } catch (err) {
        console.error('[cc-browser] Session cleanup error:', err.message);
      }
    }

    for (const p of pruned) {
      console.log(`[cc-browser] Session "${p.sessionId}" expired, closed ${p.tabIds.length} tab(s)`);
    }
  }, CLEANUP_INTERVAL_MS);

  // Don't prevent process exit
  if (cleanupTimer.unref) {
    cleanupTimer.unref();
  }
}

/**
 * Stop the cleanup timer.
 */
export function stopCleanupTimer() {
  if (cleanupTimer) {
    clearInterval(cleanupTimer);
    cleanupTimer = null;
  }
}

/**
 * Get count of active sessions (for status).
 * @returns {number}
 */
export function sessionCount() {
  return sessions.size;
}

/**
 * Clear all sessions (for testing).
 */
export function clearAllSessions() {
  sessions.clear();
}

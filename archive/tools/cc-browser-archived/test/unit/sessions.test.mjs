// Unit tests for sessions.mjs -- tab session management
import { describe, it, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, rmSync, readFileSync, writeFileSync } from 'fs';
import { join } from 'path';
import { tmpdir } from 'os';

import {
  createSession,
  getSession,
  listSessions,
  deleteSession,
  addTabToSession,
  removeTabFromSessions,
  findSessionForTab,
  touchSession,
  pruneExpiredSessions,
  reconcileTabs,
  persistSessions,
  loadSessions,
  startCleanupTimer,
  stopCleanupTimer,
  sessionCount,
  clearAllSessions,
} from '../../src/sessions.mjs';

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

let tmpDir;

function makeTmpDir() {
  tmpDir = join(tmpdir(), `cc-browser-sessions-test-${Date.now()}`);
  mkdirSync(tmpDir, { recursive: true });
  return tmpDir;
}

function cleanTmpDir() {
  if (tmpDir && existsSync(tmpDir)) {
    rmSync(tmpDir, { recursive: true, force: true });
  }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('sessions - CRUD', () => {
  beforeEach(() => {
    clearAllSessions();
  });

  it('createSession returns a session with correct fields', () => {
    const s = createSession({ name: 'research' });
    assert.ok(s.id.startsWith('sess_'));
    assert.equal(s.name, 'research');
    assert.ok(s.createdAt > 0);
    assert.ok(s.lastActivity > 0);
    assert.equal(s.ttlMs, 30 * 60 * 1000); // default 30 min
    assert.deepEqual(s.tabIds, []);
    assert.deepEqual(s.metadata, {});
  });

  it('createSession with custom TTL and metadata', () => {
    const s = createSession({ name: 'fast', ttlMs: 60000, metadata: { agent: 'test' } });
    assert.equal(s.ttlMs, 60000);
    assert.equal(s.metadata.agent, 'test');
  });

  it('createSession with ttlMs=0 means never expires', () => {
    const s = createSession({ name: 'permanent', ttlMs: 0 });
    assert.equal(s.ttlMs, 0);
  });

  it('createSession throws on empty name', () => {
    assert.throws(() => createSession({ name: '' }), /name is required/);
    assert.throws(() => createSession({ name: '   ' }), /name is required/);
  });

  it('createSession trims name', () => {
    const s = createSession({ name: '  my session  ' });
    assert.equal(s.name, 'my session');
  });

  it('getSession returns session by ID', () => {
    const s = createSession({ name: 'test' });
    const got = getSession(s.id);
    assert.equal(got.id, s.id);
    assert.equal(got.name, 'test');
  });

  it('getSession returns null for unknown ID', () => {
    assert.equal(getSession('sess_nonexistent'), null);
  });

  it('listSessions returns all sessions', () => {
    createSession({ name: 'a' });
    createSession({ name: 'b' });
    createSession({ name: 'c' });
    assert.equal(listSessions().length, 3);
  });

  it('deleteSession removes session', () => {
    const s = createSession({ name: 'temp' });
    assert.equal(deleteSession(s.id), true);
    assert.equal(getSession(s.id), null);
    assert.equal(sessionCount(), 0);
  });

  it('deleteSession returns false for unknown ID', () => {
    assert.equal(deleteSession('sess_nope'), false);
  });

  it('sessionCount tracks active sessions', () => {
    assert.equal(sessionCount(), 0);
    const s1 = createSession({ name: 'a' });
    assert.equal(sessionCount(), 1);
    createSession({ name: 'b' });
    assert.equal(sessionCount(), 2);
    deleteSession(s1.id);
    assert.equal(sessionCount(), 1);
  });

  it('each session gets a unique ID', () => {
    const ids = new Set();
    for (let i = 0; i < 20; i++) {
      ids.add(createSession({ name: `s${i}` }).id);
    }
    assert.equal(ids.size, 20);
  });
});

describe('sessions - tab tracking', () => {
  beforeEach(() => {
    clearAllSessions();
  });

  it('addTabToSession adds tab and updates lastActivity', () => {
    const s = createSession({ name: 'test' });
    const originalActivity = s.lastActivity;

    // Small delay to ensure timestamp differs
    const result = addTabToSession(s.id, 'tab-1');
    assert.equal(result, true);

    const updated = getSession(s.id);
    assert.deepEqual(updated.tabIds, ['tab-1']);
    assert.ok(updated.lastActivity >= originalActivity);
  });

  it('addTabToSession does not duplicate tab IDs', () => {
    const s = createSession({ name: 'test' });
    addTabToSession(s.id, 'tab-1');
    addTabToSession(s.id, 'tab-1');
    assert.deepEqual(getSession(s.id).tabIds, ['tab-1']);
  });

  it('addTabToSession returns false for unknown session', () => {
    assert.equal(addTabToSession('sess_nope', 'tab-1'), false);
  });

  it('removeTabFromSessions removes tab from all sessions', () => {
    const s1 = createSession({ name: 'a' });
    const s2 = createSession({ name: 'b' });
    addTabToSession(s1.id, 'tab-shared');
    addTabToSession(s2.id, 'tab-shared');
    addTabToSession(s1.id, 'tab-only-s1');

    const affected = removeTabFromSessions('tab-shared');
    assert.equal(affected.length, 2);
    assert.ok(affected.includes(s1.id));
    assert.ok(affected.includes(s2.id));

    assert.deepEqual(getSession(s1.id).tabIds, ['tab-only-s1']);
    assert.deepEqual(getSession(s2.id).tabIds, []);
  });

  it('removeTabFromSessions returns empty for unknown tab', () => {
    createSession({ name: 'test' });
    assert.deepEqual(removeTabFromSessions('tab-unknown'), []);
  });

  it('findSessionForTab finds the right session', () => {
    const s1 = createSession({ name: 'a' });
    const s2 = createSession({ name: 'b' });
    addTabToSession(s1.id, 'tab-1');
    addTabToSession(s2.id, 'tab-2');

    assert.equal(findSessionForTab('tab-1').id, s1.id);
    assert.equal(findSessionForTab('tab-2').id, s2.id);
    assert.equal(findSessionForTab('tab-3'), null);
  });
});

describe('sessions - TTL and heartbeat', () => {
  beforeEach(() => {
    clearAllSessions();
  });

  it('touchSession updates lastActivity', () => {
    const s = createSession({ name: 'test' });
    const before = s.lastActivity;
    const ok = touchSession(s.id);
    assert.equal(ok, true);
    assert.ok(getSession(s.id).lastActivity >= before);
  });

  it('touchSession returns false for unknown session', () => {
    assert.equal(touchSession('sess_nope'), false);
  });

  it('pruneExpiredSessions removes expired sessions', () => {
    // Create a session with 1ms TTL so it's immediately expired
    const s = createSession({ name: 'short', ttlMs: 1 });
    addTabToSession(s.id, 'tab-1');
    addTabToSession(s.id, 'tab-2');

    // Force lastActivity into the past
    s.lastActivity = Date.now() - 1000;

    const pruned = pruneExpiredSessions();
    assert.equal(pruned.length, 1);
    assert.equal(pruned[0].sessionId, s.id);
    assert.deepEqual(pruned[0].tabIds, ['tab-1', 'tab-2']);
    assert.equal(getSession(s.id), null);
  });

  it('pruneExpiredSessions skips ttlMs=0 sessions', () => {
    const s = createSession({ name: 'permanent', ttlMs: 0 });
    s.lastActivity = Date.now() - 999999999; // Very old

    const pruned = pruneExpiredSessions();
    assert.equal(pruned.length, 0);
    assert.ok(getSession(s.id) !== null);
  });

  it('pruneExpiredSessions skips sessions within TTL', () => {
    const s = createSession({ name: 'recent', ttlMs: 60 * 60 * 1000 }); // 1 hour
    const pruned = pruneExpiredSessions();
    assert.equal(pruned.length, 0);
    assert.ok(getSession(s.id) !== null);
  });

  it('pruneExpiredSessions handles mixed expired and live', () => {
    const expired = createSession({ name: 'old', ttlMs: 100 });
    const live = createSession({ name: 'new', ttlMs: 60000 });
    addTabToSession(expired.id, 'tab-old');
    addTabToSession(live.id, 'tab-live');

    // Force lastActivity into the past AFTER adding tabs (addTab updates lastActivity)
    expired.lastActivity = Date.now() - 10000;

    const pruned = pruneExpiredSessions();
    assert.equal(pruned.length, 1);
    assert.equal(pruned[0].sessionId, expired.id);
    assert.ok(getSession(live.id) !== null);
  });
});

describe('sessions - reconcileTabs', () => {
  beforeEach(() => {
    clearAllSessions();
  });

  it('removes stale tab IDs not in live set', () => {
    const s = createSession({ name: 'test' });
    addTabToSession(s.id, 'tab-1');
    addTabToSession(s.id, 'tab-2');
    addTabToSession(s.id, 'tab-3');

    // Only tab-1 and tab-3 are live
    reconcileTabs(['tab-1', 'tab-3']);
    assert.deepEqual(getSession(s.id).tabIds, ['tab-1', 'tab-3']);
  });

  it('handles empty live set', () => {
    const s = createSession({ name: 'test' });
    addTabToSession(s.id, 'tab-1');
    reconcileTabs([]);
    assert.deepEqual(getSession(s.id).tabIds, []);
  });
});

describe('sessions - persistence', () => {
  beforeEach(() => {
    clearAllSessions();
    makeTmpDir();
  });

  afterEach(() => {
    cleanTmpDir();
  });

  it('persistSessions writes sessions.json', () => {
    createSession({ name: 'a' });
    createSession({ name: 'b' });
    persistSessions(tmpDir);

    const filePath = join(tmpDir, 'sessions.json');
    assert.ok(existsSync(filePath));

    const data = JSON.parse(readFileSync(filePath, 'utf8'));
    assert.equal(data.length, 2);
  });

  it('loadSessions restores sessions from disk', () => {
    const s1 = createSession({ name: 'alpha' });
    addTabToSession(s1.id, 'tab-x');
    const s2 = createSession({ name: 'beta', ttlMs: 0 });

    persistSessions(tmpDir);

    // Clear and reload
    clearAllSessions();
    assert.equal(sessionCount(), 0);

    loadSessions(tmpDir);
    assert.equal(sessionCount(), 2);

    const restored1 = getSession(s1.id);
    assert.equal(restored1.name, 'alpha');
    assert.deepEqual(restored1.tabIds, ['tab-x']);

    const restored2 = getSession(s2.id);
    assert.equal(restored2.name, 'beta');
    assert.equal(restored2.ttlMs, 0);
  });

  it('loadSessions handles missing file gracefully', () => {
    loadSessions(tmpDir); // No sessions.json exists
    assert.equal(sessionCount(), 0);
  });

  it('loadSessions handles corrupted file gracefully', () => {
    writeFileSync(join(tmpDir, 'sessions.json'), 'not valid json!!!');
    loadSessions(tmpDir);
    assert.equal(sessionCount(), 0);
  });

  it('loadSessions handles null dir gracefully', () => {
    loadSessions(null);
    assert.equal(sessionCount(), 0);
  });

  it('persistSessions handles null dir gracefully', () => {
    // Should not throw
    persistSessions(null);
  });

  it('persistence round-trip preserves all fields', () => {
    const s = createSession({
      name: 'full',
      ttlMs: 99999,
      metadata: { agent: 'test-agent', version: 2 },
    });
    addTabToSession(s.id, 'tab-a');
    addTabToSession(s.id, 'tab-b');

    persistSessions(tmpDir);
    clearAllSessions();
    loadSessions(tmpDir);

    const restored = getSession(s.id);
    assert.equal(restored.name, 'full');
    assert.equal(restored.ttlMs, 99999);
    assert.deepEqual(restored.tabIds, ['tab-a', 'tab-b']);
    assert.equal(restored.metadata.agent, 'test-agent');
    assert.equal(restored.metadata.version, 2);
    assert.ok(restored.createdAt > 0);
    assert.ok(restored.lastActivity > 0);
  });
});

describe('sessions - cleanup timer', () => {
  beforeEach(() => {
    clearAllSessions();
  });

  afterEach(() => {
    stopCleanupTimer();
  });

  it('startCleanupTimer and stopCleanupTimer do not throw', () => {
    let called = false;
    startCleanupTimer(async () => { called = true; });
    stopCleanupTimer();
    // Timer runs every 60s so callback should not have been called
    assert.equal(called, false);
  });

  it('stopCleanupTimer is idempotent', () => {
    stopCleanupTimer();
    stopCleanupTimer();
    // Should not throw
  });
});

describe('sessions - clearAllSessions', () => {
  it('clears everything', () => {
    createSession({ name: 'a' });
    createSession({ name: 'b' });
    assert.equal(sessionCount(), 2);
    clearAllSessions();
    assert.equal(sessionCount(), 0);
    assert.equal(listSessions().length, 0);
  });
});

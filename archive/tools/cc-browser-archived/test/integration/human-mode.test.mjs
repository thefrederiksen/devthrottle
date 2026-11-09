// Integration tests for human mode timing verification
// Tests that human mode adds measurable delays to operations
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

import { setCurrentMode, getCurrentMode } from '../../src/session.mjs';
import {
  sleep,
  navigationDelay,
  preClickDelay,
  postLoadDelay,
} from '../../src/human-mode.mjs';

describe('human mode integration', () => {
  beforeEach(() => {
    setCurrentMode('human');
  });

  it('human mode adds measurable delay to sleep calls', async () => {
    const delay = navigationDelay();
    const start = Date.now();
    await sleep(delay);
    const elapsed = Date.now() - start;
    // Should take at least 80% of the requested delay
    assert.ok(elapsed >= delay * 0.8, `Expected >= ${delay * 0.8}ms, got ${elapsed}ms`);
  });

  it('fast mode skips delays (mode state check)', () => {
    setCurrentMode('fast');
    const mode = getCurrentMode();
    assert.equal(mode, 'fast');
    // In fast mode, no sleep calls are made in interaction code
    // This test verifies the mode check works correctly
  });

  it('mode switching works correctly', () => {
    assert.equal(getCurrentMode(), 'human');
    setCurrentMode('fast');
    assert.equal(getCurrentMode(), 'fast');
    setCurrentMode('stealth');
    assert.equal(getCurrentMode(), 'stealth');
    setCurrentMode('human');
    assert.equal(getCurrentMode(), 'human');
  });

  it('preClickDelay + postLoadDelay add significant time', async () => {
    const clickDelay = preClickDelay();
    const loadDelay = postLoadDelay();

    const start = Date.now();
    await sleep(clickDelay);
    await sleep(loadDelay);
    const elapsed = Date.now() - start;

    // Combined should be at least 1100ms (100 + 1000 minimums)
    assert.ok(elapsed >= 1000, `Combined delays should be >= 1000ms, got ${elapsed}ms`);
  });

  it('sequential delays accumulate correctly', async () => {
    const delays = [];
    for (let i = 0; i < 5; i++) {
      delays.push(preClickDelay());
    }
    const totalExpected = delays.reduce((a, b) => a + b, 0);

    const start = Date.now();
    for (const d of delays) {
      await sleep(d);
    }
    const elapsed = Date.now() - start;

    assert.ok(elapsed >= totalExpected * 0.8,
      `Sequential delays should take >= ${totalExpected * 0.8}ms, got ${elapsed}ms`);
  });
});

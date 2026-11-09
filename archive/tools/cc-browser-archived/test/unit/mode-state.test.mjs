// Unit tests for mode state management
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

import { getCurrentMode, setCurrentMode } from '../../src/session.mjs';

describe('mode state', () => {
  beforeEach(() => {
    // Reset to default
    setCurrentMode('human');
  });

  it('getCurrentMode returns human by default', () => {
    assert.equal(getCurrentMode(), 'human');
  });

  it('setCurrentMode changes to fast', () => {
    setCurrentMode('fast');
    assert.equal(getCurrentMode(), 'fast');
  });

  it('setCurrentMode changes to stealth', () => {
    setCurrentMode('stealth');
    assert.equal(getCurrentMode(), 'stealth');
  });

  it('setCurrentMode changes to human', () => {
    setCurrentMode('fast');
    setCurrentMode('human');
    assert.equal(getCurrentMode(), 'human');
  });

  it('setCurrentMode throws on invalid mode', () => {
    assert.throws(() => setCurrentMode('invalid'), {
      message: /Invalid mode: invalid/,
    });
  });

  it('setCurrentMode throws on empty string', () => {
    assert.throws(() => setCurrentMode(''), {
      message: /Invalid mode/,
    });
  });

  it('mode persists across calls', () => {
    setCurrentMode('fast');
    assert.equal(getCurrentMode(), 'fast');
    assert.equal(getCurrentMode(), 'fast');
    setCurrentMode('stealth');
    assert.equal(getCurrentMode(), 'stealth');
    assert.equal(getCurrentMode(), 'stealth');
  });
});

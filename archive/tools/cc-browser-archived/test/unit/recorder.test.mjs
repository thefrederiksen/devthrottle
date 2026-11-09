// Unit tests for recorder.mjs -- locator building and event normalization
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { buildLocatorsFromInfo, normalizeEvents, INJECTED_SCRIPT } from '../../src/recorder.mjs';

// ---------------------------------------------------------------------------
// buildLocatorsFromInfo
// ---------------------------------------------------------------------------

describe('buildLocatorsFromInfo', () => {
  it('builds role+name locator when both are provided', () => {
    const locators = buildLocatorsFromInfo({
      role: 'button',
      name: 'Sign In',
      text: 'Sign In',
      selector: '#login-btn',
      cssPath: 'body > form > button',
    });

    assert.equal(locators.length, 4);
    assert.deepEqual(locators[0], { strategy: 'role', role: 'button', name: 'Sign In' });
    assert.deepEqual(locators[1], { strategy: 'text', text: 'Sign In' });
    assert.deepEqual(locators[2], { strategy: 'selector', selector: '#login-btn' });
    assert.deepEqual(locators[3], { strategy: 'cssPath', path: 'body > form > button' });
  });

  it('builds role-only locator when name is missing', () => {
    const locators = buildLocatorsFromInfo({
      role: 'textbox',
      text: '',
      selector: 'input.email',
    });

    assert.equal(locators.length, 2);
    assert.deepEqual(locators[0], { strategy: 'role', role: 'textbox' });
    assert.deepEqual(locators[1], { strategy: 'selector', selector: 'input.email' });
  });

  it('skips text locator when text is too short', () => {
    const locators = buildLocatorsFromInfo({
      role: 'link',
      name: 'Home',
      text: 'X',
      selector: 'a.home',
    });

    // text "X" has length 1, should be excluded
    const textLoc = locators.find((l) => l.strategy === 'text');
    assert.equal(textLoc, undefined);
  });

  it('skips text locator when text is too long', () => {
    const locators = buildLocatorsFromInfo({
      role: 'heading',
      name: 'Title',
      text: 'A'.repeat(80),
      selector: 'h1',
    });

    const textLoc = locators.find((l) => l.strategy === 'text');
    assert.equal(textLoc, undefined);
  });

  it('returns empty array when nothing is provided', () => {
    const locators = buildLocatorsFromInfo({});
    assert.equal(locators.length, 0);
  });

  it('includes text locator for valid length text', () => {
    const locators = buildLocatorsFromInfo({
      text: 'Submit Form',
    });

    assert.equal(locators.length, 1);
    assert.deepEqual(locators[0], { strategy: 'text', text: 'Submit Form' });
  });
});

// ---------------------------------------------------------------------------
// normalizeEvents
// ---------------------------------------------------------------------------

describe('normalizeEvents', () => {
  it('deduplicates consecutive navigations to the same URL', () => {
    const raw = [
      { action: 'navigate', url: 'https://example.com' },
      { action: 'navigate', url: 'https://example.com' },
      { action: 'click', locators: [] },
      { action: 'navigate', url: 'https://example.com/page2' },
    ];

    const result = normalizeEvents(raw);
    assert.equal(result.length, 3);
    assert.equal(result[0].url, 'https://example.com');
    assert.equal(result[1].action, 'click');
    assert.equal(result[2].url, 'https://example.com/page2');
  });

  it('keeps different consecutive navigations', () => {
    const raw = [
      { action: 'navigate', url: 'https://a.com' },
      { action: 'navigate', url: 'https://b.com' },
    ];

    const result = normalizeEvents(raw);
    assert.equal(result.length, 2);
  });

  it('handles empty input', () => {
    const result = normalizeEvents([]);
    assert.deepEqual(result, []);
  });

  it('preserves non-navigate events', () => {
    const raw = [
      { action: 'click', locators: [{ strategy: 'role', role: 'button', name: 'OK' }] },
      { action: 'type', locators: [], value: 'hello' },
      { action: 'keypress', key: 'Enter' },
    ];

    const result = normalizeEvents(raw);
    assert.equal(result.length, 3);
  });
});

// ---------------------------------------------------------------------------
// INJECTED_SCRIPT
// ---------------------------------------------------------------------------

describe('INJECTED_SCRIPT', () => {
  it('is a non-empty string', () => {
    assert.equal(typeof INJECTED_SCRIPT, 'string');
    assert.ok(INJECTED_SCRIPT.length > 100);
  });

  it('contains the guard variable', () => {
    assert.ok(INJECTED_SCRIPT.includes('__ccRecorderActive'));
  });

  it('contains the events array', () => {
    assert.ok(INJECTED_SCRIPT.includes('__ccRecorderEvents'));
  });

  it('captures click events', () => {
    assert.ok(INJECTED_SCRIPT.includes("'click'"));
  });

  it('does NOT flush type buffer on click (uses focusout instead)', () => {
    // The click handler should NOT contain flushType -- we rely on focusout
    // to capture the final field value when the user leaves the field.
    const clickSection = INJECTED_SCRIPT.split("'click'")[1].split("}, true);")[0];
    assert.ok(!clickSection.includes('flushType'), 'Click handler should not call flushType');
  });

  it('has a focusout listener for flushing type buffer', () => {
    assert.ok(INJECTED_SCRIPT.includes("'focusout'"), 'Should have focusout event listener');
  });

  it('captures input events for typing', () => {
    assert.ok(INJECTED_SCRIPT.includes("'input'"));
  });

  it('captures keydown for Enter/Escape/Tab', () => {
    assert.ok(INJECTED_SCRIPT.includes("'keydown'"));
    assert.ok(INJECTED_SCRIPT.includes("'Enter'"));
    assert.ok(INJECTED_SCRIPT.includes("'Escape'"));
    assert.ok(INJECTED_SCRIPT.includes("'Tab'"));
  });

  it('captures scroll events', () => {
    assert.ok(INJECTED_SCRIPT.includes("'scroll'"));
  });

  it('includes cssPath helper', () => {
    assert.ok(INJECTED_SCRIPT.includes('cssPath'));
  });

  it('includes buildLocators helper', () => {
    assert.ok(INJECTED_SCRIPT.includes('buildLocators'));
  });
});

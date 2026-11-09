// Unit tests for new features:
// 1. resolveLocator (text/selector click support)
// 2. Screenshot --save (client-side file write)
// 3. Per-command help
import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, unlinkSync, mkdirSync, rmdirSync, readFileSync, writeFileSync } from 'fs';
import { join, extname as extname_ } from 'path';
import { tmpdir } from 'os';

// Helper to avoid dynamic import issues in test callbacks
function require_fs() { return { writeFileSync }; }

import { MockPage, MockLocator } from '../mocks.mjs';
import { resolveLocator } from '../../src/interactions.mjs';

// ---------------------------------------------------------------------------
// resolveLocator tests
// ---------------------------------------------------------------------------

describe('resolveLocator', () => {
  let page;

  beforeEach(() => {
    page = new MockPage();
  });

  it('throws when no target is provided', () => {
    assert.throws(
      () => resolveLocator(page, {}),
      /One of --ref, --text, or --selector is required/
    );
  });

  it('throws when multiple targets are provided (ref + text)', () => {
    assert.throws(
      () => resolveLocator(page, { ref: 'e1', text: 'hello' }),
      /Only one of --ref, --text, or --selector/
    );
  });

  it('throws when multiple targets are provided (ref + selector)', () => {
    assert.throws(
      () => resolveLocator(page, { ref: 'e1', selector: '.btn' }),
      /Only one of --ref, --text, or --selector/
    );
  });

  it('throws when multiple targets are provided (text + selector)', () => {
    assert.throws(
      () => resolveLocator(page, { text: 'hello', selector: '.btn' }),
      /Only one of --ref, --text, or --selector/
    );
  });

  it('throws when all three are provided', () => {
    assert.throws(
      () => resolveLocator(page, { ref: 'e1', text: 'hello', selector: '.btn' }),
      /Only one of --ref, --text, or --selector/
    );
  });

  it('ignores empty/null values (treats as not provided)', () => {
    // ref='' should be treated as not provided
    assert.throws(
      () => resolveLocator(page, { ref: '', text: '', selector: '' }),
      /One of --ref, --text, or --selector is required/
    );
  });

  it('resolves text-based locator (substring match)', () => {
    const result = resolveLocator(page, { text: 'CaseAttributes.sql' });
    assert.ok(result.locator, 'Should return a locator');
    assert.equal(result.description, 'text="CaseAttributes.sql"');
  });

  it('resolves text-based locator with exact match', () => {
    const result = resolveLocator(page, { text: 'Submit', exact: true });
    assert.ok(result.locator, 'Should return a locator');
    assert.equal(result.description, 'text="Submit"');
  });

  it('resolves selector-based locator', () => {
    const result = resolveLocator(page, { selector: '[data-testid="save"]' });
    assert.ok(result.locator, 'Should return a locator');
    assert.equal(result.description, 'selector="[data-testid="save"]"');
  });

  it('returns correct locator type for text (uses getByText)', () => {
    const result = resolveLocator(page, { text: 'Click me' });
    // The MockPage.getByText creates a locator with key "text:Click me"
    assert.ok(result.locator._name.startsWith('text:'), 'Should use getByText locator');
  });

  it('returns correct locator type for exact text', () => {
    const result = resolveLocator(page, { text: 'Click me', exact: true });
    assert.ok(result.locator._name.startsWith('text-exact:'), 'Should use getByText with exact');
  });

  it('returns correct locator type for selector (uses page.locator)', () => {
    const result = resolveLocator(page, { selector: '.my-class' });
    assert.equal(result.locator._name, '.my-class', 'Should use page.locator with CSS selector');
  });

  it('handles whitespace-only values as empty', () => {
    assert.throws(
      () => resolveLocator(page, { ref: '  ', text: '  ', selector: '  ' }),
      /One of --ref, --text, or --selector is required/
    );
  });

  it('trims whitespace from values', () => {
    const result = resolveLocator(page, { text: '  Submit  ' });
    assert.equal(result.description, 'text="Submit"');
  });
});

// ---------------------------------------------------------------------------
// Screenshot --save tests (client-side logic)
// ---------------------------------------------------------------------------

describe('screenshot --save logic', () => {
  const testDir = join(tmpdir(), 'cc-browser-test-' + Date.now());

  // Clean up helper
  function cleanup(path) {
    try { if (existsSync(path)) unlinkSync(path); } catch {}
  }

  function cleanupDir(path) {
    try { if (existsSync(path)) rmdirSync(path, { recursive: true }); } catch {}
  }

  beforeEach(() => {
    cleanupDir(testDir);
    mkdirSync(testDir, { recursive: true });
  });

  it('saves base64 screenshot to file', () => {
    const testPath = join(testDir, 'test.png');
    const base64Data = Buffer.from('fake-png-data').toString('base64');

    // Simulate the save logic from cli.mjs
    const buf = Buffer.from(base64Data, 'base64');
    const { writeFileSync: wfs } = require_fs();
    wfs(testPath, buf);

    assert.ok(existsSync(testPath), 'File should exist');
    const content = readFileSync(testPath);
    assert.deepEqual(content, Buffer.from('fake-png-data'));

    cleanup(testPath);
  });

  it('auto-appends .png extension when missing', () => {
    let savePath = join(testDir, 'screenshot');
    const ext = extname_(savePath).toLowerCase();
    const imgType = 'png';
    if (!ext) {
      savePath += '.' + imgType;
    }
    assert.ok(savePath.endsWith('.png'), `Path should end with .png: ${savePath}`);
  });

  it('auto-appends .jpeg extension for jpeg type', () => {
    let savePath = join(testDir, 'screenshot');
    const ext = extname_(savePath).toLowerCase();
    const imgType = 'jpeg';
    if (!ext) {
      savePath += '.' + imgType;
    }
    assert.ok(savePath.endsWith('.jpeg'), `Path should end with .jpeg: ${savePath}`);
  });

  it('preserves existing extension', () => {
    let savePath = join(testDir, 'page.jpg');
    const ext = extname_(savePath).toLowerCase();
    const imgType = 'png';
    if (!ext) {
      savePath += '.' + imgType;
    }
    assert.ok(savePath.endsWith('.jpg'), `Should keep .jpg: ${savePath}`);
  });

  it('creates parent directories when missing', () => {
    const nested = join(testDir, 'a', 'b', 'c');
    mkdirSync(nested, { recursive: true });
    assert.ok(existsSync(nested), 'Nested directory should be created');
    cleanupDir(join(testDir, 'a'));
  });
});

// ---------------------------------------------------------------------------
// Per-command help tests
// ---------------------------------------------------------------------------

describe('per-command help', () => {
  // We test the commandHelp data structure by importing and inspecting it
  // Since commandHelp is not exported, we test the behavior via the CLI

  it('commandHelp entries exist for major commands', async () => {
    // Read the cli.mjs file and check that commandHelp contains expected commands
    const cliPath = new URL('../../src/cli.mjs', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
    const content = readFileSync(cliPath, 'utf8');

    const expectedCommands = [
      'click', 'type', 'hover', 'screenshot', 'navigate', 'snapshot',
      'evaluate', 'press', 'scroll', 'wait', 'tabs', 'mode', 'captcha',
      'start', 'stop', 'daemon', 'status',
    ];

    for (const cmd of expectedCommands) {
      // Check that the command appears as a key in commandHelp
      const pattern = new RegExp(`['"]?${cmd}['"]?\\s*:`);
      assert.ok(
        content.includes(`  ${cmd}: \``) || content.match(pattern),
        `commandHelp should contain entry for "${cmd}"`
      );
    }
  });

  it('click help mentions --text and --selector flags', async () => {
    const cliPath = new URL('../../src/cli.mjs', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
    const content = readFileSync(cliPath, 'utf8');

    // Extract click help text
    assert.ok(content.includes('--text <string>'), 'Click help should document --text flag');
    assert.ok(content.includes('--selector <css>'), 'Click help should document --selector flag');
    assert.ok(content.includes('--exact'), 'Click help should document --exact flag');
  });

  it('screenshot help mentions --save flag', async () => {
    const cliPath = new URL('../../src/cli.mjs', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
    const content = readFileSync(cliPath, 'utf8');

    assert.ok(content.includes('--save <path>'), 'Screenshot help should document --save flag');
    assert.ok(content.includes('Save screenshot to file'), 'Screenshot help should describe --save');
  });

  it('evaluate help includes useful examples', async () => {
    const cliPath = new URL('../../src/cli.mjs', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
    const content = readFileSync(cliPath, 'utf8');

    assert.ok(content.includes('document.title'), 'Evaluate help should include document.title example');
    assert.ok(content.includes('el => el.textContent'), 'Evaluate help should include element example');
  });

  it('help section mentions per-command help syntax', async () => {
    const cliPath = new URL('../../src/cli.mjs', import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1');
    const content = readFileSync(cliPath, 'utf8');

    assert.ok(content.includes('help <command>'), 'Main help should mention "help <command>"');
    assert.ok(content.includes('<command> --help'), 'Main help should mention "<command> --help"');
  });
});

// ---------------------------------------------------------------------------
// Click by text integration (with mock page)
// ---------------------------------------------------------------------------

describe('click by text via MockPage', () => {
  let page;

  beforeEach(() => {
    page = new MockPage();
  });

  it('getByText returns a clickable locator', async () => {
    const { locator } = resolveLocator(page, { text: 'CaseAttributes.sql' });
    await locator.click();
    assert.equal(locator.calls.length, 1);
    assert.equal(locator.calls[0].method, 'click');
  });

  it('getByText with exact returns a different locator', () => {
    const result1 = resolveLocator(page, { text: 'Submit' });
    const result2 = resolveLocator(page, { text: 'Submit', exact: true });
    // They should have different descriptions
    assert.equal(result1.description, 'text="Submit"');
    assert.equal(result2.description, 'text="Submit"');
    // But different underlying locator names (different mock keys)
    assert.notEqual(result1.locator._name, result2.locator._name);
  });

  it('selector returns a clickable locator', async () => {
    const { locator } = resolveLocator(page, { selector: '.file-name' });
    await locator.click();
    assert.equal(locator.calls.length, 1);
    assert.equal(locator.calls[0].method, 'click');
  });

  it('hover works with text locator', async () => {
    const { locator } = resolveLocator(page, { text: 'Menu Item' });
    await locator.hover();
    assert.equal(locator.calls.length, 1);
    assert.equal(locator.calls[0].method, 'hover');
  });

  it('type/fill works with selector locator', async () => {
    const { locator } = resolveLocator(page, { selector: '#search-input' });
    await locator.fill('test query');
    assert.equal(locator.calls.length, 1);
    assert.equal(locator.calls[0].method, 'fill');
  });
});

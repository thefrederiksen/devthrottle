// CC Browser - Stealth Fingerprint Integration Test
// Launches a REAL browser and verifies anti-detection measures in-page.
// Skip in CI (needs a real display).

import { describe, it, after } from 'node:test';
import assert from 'node:assert/strict';
import { launchChrome, stopChrome } from '../../src/chrome.mjs';

const SKIP = process.env.CI === 'true';

describe('stealth fingerprint checks', { skip: SKIP }, () => {
  let context = null;

  after(async () => {
    if (context) {
      try { await stopChrome(); } catch { /* best effort */ }
    }
  });

  it('passes all anti-detection fingerprint checks', async () => {
    const result = await launchChrome({
      headless: false,
      workspaceName: 'stealth-test-' + Date.now(),
    });
    context = result.context;

    const page = context.pages()[0] || await context.newPage();
    await page.goto('about:blank');

    const checks = await page.evaluate(async () => {
      // navigator.webdriver should be false or undefined (not true)
      const webdriver = navigator.webdriver;

      // plugins should have entries (headless Chrome has 0)
      const pluginCount = navigator.plugins.length;

      // languages should be populated
      const languageCount = navigator.languages?.length || 0;

      // window.chrome should exist in real Chrome
      const hasChrome = !!window.chrome;

      // Check permissions API works (real browser feature)
      let permState = 'unknown';
      try {
        const perm = await navigator.permissions.query({ name: 'notifications' });
        permState = perm.state;
      } catch {
        permState = 'error';
      }

      // WebGL renderer should not be "SwiftShader" (CPU fallback = detectable)
      let webglRenderer = 'none';
      try {
        const canvas = document.createElement('canvas');
        const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
        if (gl) {
          const ext = gl.getExtension('WEBGL_debug_renderer_info');
          if (ext) {
            webglRenderer = gl.getParameter(ext.UNMASKED_RENDERER_WEBGL);
          }
        }
      } catch {
        webglRenderer = 'error';
      }

      // User-Agent should not contain "HeadlessChrome"
      const noHeadlessUA = !navigator.userAgent.includes('HeadlessChrome');

      return {
        webdriver,
        pluginCount,
        languageCount,
        hasChrome,
        permState,
        webglRenderer,
        noHeadlessUA,
      };
    });

    console.log('[stealth-fingerprint] Results:', JSON.stringify(checks, null, 2));

    // navigator.webdriver must NOT be true
    assert.notEqual(checks.webdriver, true,
      'navigator.webdriver must not be true (detected as bot)');

    // Plugins must exist (real browser has PDF plugin etc.)
    assert.ok(checks.pluginCount > 0,
      `navigator.plugins should have entries, got ${checks.pluginCount}`);

    // Languages must be populated
    assert.ok(checks.languageCount > 0,
      `navigator.languages should be populated, got ${checks.languageCount}`);

    // window.chrome must exist
    assert.ok(checks.hasChrome,
      'window.chrome should exist in real Chrome');

    // WebGL should not use SwiftShader (CPU-based = detectable)
    assert.ok(
      !checks.webglRenderer.toLowerCase().includes('swiftshader'),
      `WebGL renderer should not be SwiftShader, got: ${checks.webglRenderer}`
    );

    // User-Agent must not contain HeadlessChrome
    assert.ok(checks.noHeadlessUA,
      'User-Agent must not contain HeadlessChrome');
  });
});

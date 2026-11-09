// CC Browser - Turnstile Detection Integration Test
// Navigates to a Cloudflare Turnstile demo page and verifies the challenge
// widget renders (iframe appears), proving the browser is not blocked.
// Skip in CI (needs a real display + network).

import { describe, it, after } from 'node:test';
import assert from 'node:assert/strict';
import { launchChrome, stopChrome } from '../../src/chrome.mjs';

const SKIP = process.env.CI === 'true';
const TURNSTILE_DEMO = 'https://demo.turnstile.workers.dev/';

describe('Turnstile widget detection', { skip: SKIP }, () => {
  let context = null;

  after(async () => {
    if (context) {
      try { await stopChrome(); } catch { /* best effort */ }
    }
  });

  it('Turnstile challenge iframe renders (not blocked by bot detection)', async () => {
    const result = await launchChrome({
      headless: false,
      workspaceName: 'turnstile-test-' + Date.now(),
    });
    context = result.context;

    const page = context.pages()[0] || await context.newPage();

    console.log('[turnstile-detect] Navigating to Turnstile demo...');
    await page.goto(TURNSTILE_DEMO, { waitUntil: 'domcontentloaded', timeout: 30000 });

    // Wait for the Turnstile iframe to appear (Cloudflare injects it)
    let iframeFound = false;
    try {
      await page.waitForSelector(
        'iframe[src*="challenges.cloudflare.com"]',
        { timeout: 15000 }
      );
      iframeFound = true;
    } catch {
      // Timeout -- iframe never appeared
    }

    // Also check for the turnstile container div as a secondary signal
    let containerFound = false;
    try {
      const container = await page.$('[class*="turnstile"], [id*="turnstile"], .cf-turnstile');
      containerFound = container !== null;
    } catch {
      // Selector failed
    }

    console.log(`[turnstile-detect] iframe found: ${iframeFound}, container found: ${containerFound}`);

    assert.ok(
      iframeFound || containerFound,
      'Turnstile challenge widget should render. If both iframe and container are missing, ' +
      'the browser was likely blocked by bot detection.'
    );
  });
});

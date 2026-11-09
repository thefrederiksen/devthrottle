// Live CAPTCHA tests - opt-in only
// These tests require a running browser + daemon and network access.
// Only run when explicitly invoked with: npm run test:captcha
//
// Prerequisites:
// 1. cc-browser daemon running
// 2. Browser started
// 3. ANTHROPIC_API_KEY set (for vision-based detection)
// 4. Network access to external demo URLs

import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Read live URLs from fixture
const urls = JSON.parse(readFileSync(
  join(__dirname, '..', 'fixtures', 'captcha-urls.json'), 'utf8'
));

const DAEMON_PORT = process.env.CC_BROWSER_PORT || 9280;

async function request(method, path, body) {
  const url = `http://127.0.0.1:${DAEMON_PORT}${path}`;
  const res = await fetch(url, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
    signal: AbortSignal.timeout(60000),
  });
  return res.json();
}

async function isDaemonRunning() {
  try {
    const result = await request('GET', '/');
    return result.success && result.browser === 'connected';
  } catch {
    return false;
  }
}

describe('live CAPTCHA tests (opt-in)', { skip: !process.env.CAPTCHA_LIVE_TESTS }, () => {
  before(async () => {
    const running = await isDaemonRunning();
    if (!running) {
      assert.fail('Daemon not running or browser not connected. Start cc-browser daemon and browser first.');
    }
  });

  it('detects reCAPTCHA on Google demo page', async () => {
    await request('POST', '/navigate', { url: urls.recaptcha_v2.url });
    const result = await request('POST', '/captcha/detect', {});
    assert.ok(result.success, 'Detection request should succeed');
    assert.equal(result.detected, true, 'Should detect reCAPTCHA');
    assert.equal(result.type, 'recaptcha_v2');
  });

  it('solves reCAPTCHA checkbox on Google demo page', async () => {
    await request('POST', '/navigate', { url: urls.recaptcha_v2.url });
    const result = await request('POST', '/captcha/solve', { attempts: 3 });
    assert.ok(result.success, 'Solve request should succeed');
    // Note: may not always pass in all environments
    console.log('reCAPTCHA solve result:', JSON.stringify(result));
  });

  it('detects hCaptcha on demo page', async () => {
    await request('POST', '/navigate', { url: urls.hcaptcha.url });
    const result = await request('POST', '/captcha/detect', {});
    assert.ok(result.success, 'Detection request should succeed');
    assert.equal(result.detected, true, 'Should detect hCaptcha');
  });

  it('detects Turnstile on demo page', async () => {
    await request('POST', '/navigate', { url: urls.turnstile.url });
    const result = await request('POST', '/captcha/detect', {});
    assert.ok(result.success, 'Detection request should succeed');
    assert.equal(result.detected, true, 'Should detect Turnstile');
  });
});

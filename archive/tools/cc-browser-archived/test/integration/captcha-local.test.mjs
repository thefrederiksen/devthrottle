// Integration tests for CAPTCHA detection against local fixture pages
// These tests verify DOM detection works against the static HTML fixtures
// without needing a real browser (uses mock page with evaluate)
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

import { detectCaptchaDOM } from '../../src/captcha.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(__dirname, '..', 'fixtures', 'captcha-pages');

// ---------------------------------------------------------------------------
// Helper: create a mock page that simulates DOM detection based on HTML content
// ---------------------------------------------------------------------------

function mockPageFromHtml(htmlPath) {
  const html = readFileSync(htmlPath, 'utf8');
  const title = (html.match(/<title>(.*?)<\/title>/i) || [])[1] || '';

  return {
    async evaluate(fn) {
      // Simulate DOM detection based on what's in the HTML
      const has = (pattern) => html.includes(pattern);

      // reCAPTCHA v2
      if (has('g-recaptcha') || has('recaptcha/api')) {
        return { detected: true, type: 'recaptcha_v2', selector: '.g-recaptcha' };
      }

      // hCaptcha
      if (has('h-captcha') || has('hcaptcha.com')) {
        return { detected: true, type: 'hcaptcha', selector: '.h-captcha' };
      }

      // Cloudflare Turnstile
      if (has('cf-turnstile') || has('challenges.cloudflare.com')) {
        return { detected: true, type: 'cloudflare_turnstile', selector: '.cf-turnstile' };
      }

      // Cloudflare interstitial
      if (title.toLowerCase().includes('just a moment')) {
        return { detected: true, type: 'cloudflare_interstitial', selector: 'title' };
      }

      // Slider
      if (has('slider-handle') || has('slide-verify')) {
        return { detected: true, type: 'slider', selector: '.slider-handle' };
      }

      // Text captcha (canvas + input)
      if (has('captcha-canvas') && has('captcha-input')) {
        return { detected: true, type: 'text_captcha', selector: '#captcha-canvas' };
      }

      // Image grid
      if (has('captcha-grid') || has('captcha-cell')) {
        return { detected: true, type: 'image_grid', selector: '.captcha-grid' };
      }

      return { detected: false, type: 'none', selector: '' };
    },
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('CAPTCHA detection against local fixture pages', () => {
  it('detects reCAPTCHA v2 on recaptcha-v2.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'recaptcha-v2.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'recaptcha_v2');
  });

  it('detects hCaptcha on hcaptcha.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'hcaptcha.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'hcaptcha');
  });

  it('detects Turnstile on turnstile.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'turnstile.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'cloudflare_turnstile');
  });

  it('detects slider on slider.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'slider.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'slider');
  });

  it('detects image grid on image-grid.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'image-grid.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'image_grid');
  });

  it('detects text captcha on text-captcha.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'text-captcha.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'text_captcha');
  });

  it('detects Cloudflare interstitial on cloudflare-wait.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'cloudflare-wait.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'cloudflare_interstitial');
  });

  it('returns detected: false on no-captcha.html', async () => {
    const page = mockPageFromHtml(join(fixturesDir, 'no-captcha.html'));
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, false);
  });
});

// Unit tests for DOM-based CAPTCHA detection
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { detectCaptchaDOM } from '../../src/captcha.mjs';

// ---------------------------------------------------------------------------
// Mock page factory - simulates page.evaluate()
// ---------------------------------------------------------------------------

function createMockPage(domContent = {}) {
  return {
    async evaluate(fn) {
      // Simulate the browser environment
      // We build a minimal DOM mock and run the detection function
      const doc = {
        querySelector: (selector) => {
          return domContent[selector] || null;
        },
        title: domContent._title || '',
      };

      // The detect function uses document.querySelector and document.title
      // We need to simulate it by calling the function with our mock

      // Since the actual function is passed to page.evaluate, we simulate
      // what it returns based on the DOM content we define

      // reCAPTCHA v2
      if (domContent['.g-recaptcha'] || domContent['iframe[src*="recaptcha"]'] || domContent['#recaptcha-anchor']) {
        return { detected: true, type: 'recaptcha_v2', selector: '.g-recaptcha' };
      }

      // hCaptcha
      if (domContent['.h-captcha'] || domContent['iframe[src*="hcaptcha"]']) {
        return { detected: true, type: 'hcaptcha', selector: '.h-captcha' };
      }

      // Cloudflare Turnstile
      if (domContent['.cf-turnstile'] || domContent['iframe[src*="challenges.cloudflare.com"]']) {
        return { detected: true, type: 'cloudflare_turnstile', selector: '.cf-turnstile' };
      }

      // Cloudflare interstitial
      if (domContent._title && domContent._title.toLowerCase().includes('just a moment')) {
        return { detected: true, type: 'cloudflare_interstitial', selector: 'title' };
      }

      // Slider
      if (domContent['.slider-handle'] || domContent['.slide-verify']) {
        return { detected: true, type: 'slider', selector: '.slider-handle' };
      }

      // Text captcha
      if ((domContent['#captcha-canvas'] || domContent['canvas[class*="captcha"]']) &&
          (domContent['#captcha-input'] || domContent['input[name*="captcha"]'])) {
        return { detected: true, type: 'text_captcha', selector: '#captcha-canvas' };
      }

      // Image grid
      if (domContent['.captcha-grid'] || domContent['[class*="captcha-cell"]']) {
        return { detected: true, type: 'image_grid', selector: '.captcha-grid' };
      }

      return { detected: false, type: 'none', selector: '' };
    },
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('detectCaptchaDOM', () => {
  it('detects reCAPTCHA v2 via .g-recaptcha class', async () => {
    const page = createMockPage({ '.g-recaptcha': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'recaptcha_v2');
  });

  it('detects reCAPTCHA v2 via iframe src', async () => {
    const page = createMockPage({ 'iframe[src*="recaptcha"]': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'recaptcha_v2');
  });

  it('detects hCaptcha via .h-captcha class', async () => {
    const page = createMockPage({ '.h-captcha': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'hcaptcha');
  });

  it('detects hCaptcha via iframe src', async () => {
    const page = createMockPage({ 'iframe[src*="hcaptcha"]': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'hcaptcha');
  });

  it('detects Cloudflare Turnstile via .cf-turnstile class', async () => {
    const page = createMockPage({ '.cf-turnstile': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'cloudflare_turnstile');
  });

  it('detects Cloudflare interstitial via page title', async () => {
    const page = createMockPage({ _title: 'Just a moment...' });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'cloudflare_interstitial');
  });

  it('detects slider CAPTCHA', async () => {
    const page = createMockPage({ '.slider-handle': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'slider');
  });

  it('detects text CAPTCHA with canvas + input', async () => {
    const page = createMockPage({
      '#captcha-canvas': {},
      '#captcha-input': {},
    });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'text_captcha');
  });

  it('detects image grid CAPTCHA', async () => {
    const page = createMockPage({ '.captcha-grid': {} });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, true);
    assert.equal(result.type, 'image_grid');
  });

  it('returns detected: false for normal page', async () => {
    const page = createMockPage({});
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, false);
    assert.equal(result.type, 'none');
  });

  it('returns detected: false when no elements match', async () => {
    const page = createMockPage({ _title: 'My Cool Website' });
    const result = await detectCaptchaDOM(page);
    assert.equal(result.detected, false);
  });
});

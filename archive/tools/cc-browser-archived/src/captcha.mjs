// CC Browser - CAPTCHA Detection and Solving
// Two-tier detection (DOM + LLM vision) with type-specific solvers

import { analyzeScreenshot } from './vision.mjs';
import { sleep, preClickDelay, humanDragPath, randomInt } from './human-mode.mjs';

// ---------------------------------------------------------------------------
// Detection Prompts
// ---------------------------------------------------------------------------

const DETECTION_PROMPT = `Analyze this screenshot for CAPTCHA challenges. Look for:
- reCAPTCHA v2 checkbox ("I'm not a robot")
- reCAPTCHA v2 image grid challenges
- hCaptcha checkbox or challenges
- Cloudflare Turnstile widget
- Cloudflare "Just a moment..." interstitial
- Slider/drag puzzle CAPTCHAs
- Text-based CAPTCHAs (distorted text to type)
- Image grid selection CAPTCHAs

Respond with ONLY valid JSON (no markdown, no explanation):
{
  "detected": true/false,
  "type": "recaptcha_v2|recaptcha_image|hcaptcha|cloudflare_turnstile|cloudflare_interstitial|slider|text_captcha|image_grid|unknown",
  "description": "brief description of what you see"
}`;

// ---------------------------------------------------------------------------
// Tier 1: DOM-based detection (free, no LLM call)
// ---------------------------------------------------------------------------

/**
 * Detect CAPTCHA by checking DOM elements.
 * @param {Object} page - Playwright page object
 * @returns {Promise<{detected: boolean, type: string, selector: string}>}
 */
export async function detectCaptchaDOM(page) {
  return page.evaluate(() => {
    // reCAPTCHA v2
    const recaptcha = document.querySelector('.g-recaptcha') ||
      document.querySelector('iframe[src*="recaptcha"]') ||
      document.querySelector('#recaptcha-anchor');
    if (recaptcha) {
      return { detected: true, type: 'recaptcha_v2', selector: '.g-recaptcha' };
    }

    // hCaptcha
    const hcaptcha = document.querySelector('.h-captcha') ||
      document.querySelector('iframe[src*="hcaptcha"]');
    if (hcaptcha) {
      return { detected: true, type: 'hcaptcha', selector: '.h-captcha' };
    }

    // Cloudflare Turnstile
    const turnstile = document.querySelector('.cf-turnstile') ||
      document.querySelector('iframe[src*="challenges.cloudflare.com"]');
    if (turnstile) {
      return { detected: true, type: 'cloudflare_turnstile', selector: '.cf-turnstile' };
    }

    // Cloudflare interstitial ("Just a moment...")
    if (document.title && document.title.toLowerCase().includes('just a moment')) {
      return { detected: true, type: 'cloudflare_interstitial', selector: 'title' };
    }

    // Slider CAPTCHA (common patterns)
    const slider = document.querySelector('.slider-handle') ||
      document.querySelector('.slide-verify') ||
      document.querySelector('[class*="slider-captcha"]') ||
      document.querySelector('[class*="slide-bar"]');
    if (slider) {
      return { detected: true, type: 'slider', selector: '.slider-handle' };
    }

    // Text CAPTCHA (canvas with input)
    const captchaCanvas = document.querySelector('#captcha-canvas') ||
      document.querySelector('canvas[class*="captcha"]');
    const captchaInput = document.querySelector('#captcha-input') ||
      document.querySelector('input[name*="captcha"]');
    if (captchaCanvas && captchaInput) {
      return { detected: true, type: 'text_captcha', selector: '#captcha-canvas' };
    }

    // Image grid CAPTCHA
    const grid = document.querySelector('.captcha-grid') ||
      document.querySelector('[class*="captcha-cell"]');
    if (grid) {
      return { detected: true, type: 'image_grid', selector: '.captcha-grid' };
    }

    return { detected: false, type: 'none', selector: '' };
  });
}

// ---------------------------------------------------------------------------
// Tier 2: LLM Vision detection
// ---------------------------------------------------------------------------

/**
 * Detect CAPTCHA using LLM vision analysis.
 * @param {Object} page - Playwright page object
 * @returns {Promise<{detected: boolean, type: string, description: string}>}
 */
export async function detectCaptchaVision(page) {
  const screenshot = await page.screenshot({ type: 'png' });
  const base64 = screenshot.toString('base64');
  const response = await analyzeScreenshot(base64, DETECTION_PROMPT);

  try {
    // Strip markdown code fences if present
    const cleaned = response.replace(/```json\s*/g, '').replace(/```\s*/g, '').trim();
    return JSON.parse(cleaned);
  } catch {
    return { detected: false, type: 'unknown', description: 'Failed to parse vision response: ' + response };
  }
}

// ---------------------------------------------------------------------------
// Combined detection: DOM first, vision fallback
// ---------------------------------------------------------------------------

/**
 * Detect CAPTCHA using DOM check first, then vision if requested.
 * Vision requires Claude CLI and is slow, so it is opt-in only.
 * @param {Object} page - Playwright page object
 * @param {Object} [options]
 * @param {boolean} [options.useVision=false] - Also try LLM vision detection
 * @returns {Promise<{detected: boolean, type: string, selector?: string, description?: string}>}
 */
export async function detectCaptcha(page, { useVision = false } = {}) {
  const domResult = await detectCaptchaDOM(page);
  if (domResult.detected) return domResult;

  if (!useVision) return domResult;
  return detectCaptchaVision(page);
}

// ---------------------------------------------------------------------------
// Type-specific solvers
// ---------------------------------------------------------------------------

async function solveRecaptchaCheckbox(page, detection) {
  // Find and click the reCAPTCHA iframe checkbox
  try {
    const frame = page.frameLocator('iframe[src*="recaptcha"]').first();
    const checkbox = frame.locator('#recaptcha-anchor');
    await sleep(preClickDelay());
    await checkbox.click({ timeout: 5000 });
    // Wait for verification
    await sleep(2000);

    // Check if solved (checkbox gets aria-checked=true)
    const checked = await frame.locator('#recaptcha-anchor[aria-checked="true"]').count();
    return { solved: checked > 0, message: checked > 0 ? 'reCAPTCHA checkbox clicked and verified' : 'Clicked but may need image challenge' };
  } catch (err) {
    return { solved: false, message: 'Failed to click reCAPTCHA checkbox: ' + err.message };
  }
}

async function solveImageGrid(page, detection) {
  // Use Sonnet vision to identify which cells to click
  try {
    const screenshot = await page.screenshot({ type: 'png' });
    const base64 = screenshot.toString('base64');
    const response = await analyzeScreenshot(base64,
      'This page has an image grid CAPTCHA. Identify which cells need to be clicked. ' +
      'Look at the instruction text and the grid images. ' +
      'Return ONLY valid JSON: {"cells": [0, 3, 7], "instruction": "what to select"}' +
      ' where cells is an array of 0-indexed cell positions (left to right, top to bottom).',
      { model: 'sonnet' }
    );

    const cleaned = response.replace(/```json\s*/g, '').replace(/```\s*/g, '').trim();
    const parsed = JSON.parse(cleaned);

    // Click each identified cell
    const cells = page.locator('.captcha-cell');
    for (const idx of parsed.cells) {
      await sleep(randomInt(200, 500));
      await cells.nth(idx).click();
    }

    // Click verify button
    await sleep(randomInt(300, 600));
    const verifyBtn = page.locator('#verify-btn');
    if (await verifyBtn.count() > 0) {
      await verifyBtn.click();
    }

    await sleep(1000);

    // Check for success
    const successText = await page.locator('.success').count();
    return { solved: successText > 0, message: successText > 0 ? 'Image grid solved' : 'Attempted image grid but verification unclear' };
  } catch (err) {
    return { solved: false, message: 'Image grid solver failed: ' + err.message };
  }
}

async function solveSlider(page, detection) {
  // Use Sonnet vision to identify slider handle and target positions
  try {
    const screenshot = await page.screenshot({ type: 'png' });
    const base64 = screenshot.toString('base64');
    const response = await analyzeScreenshot(base64,
      'This page has a slider CAPTCHA puzzle. Identify the slider handle position and the target slot position. ' +
      'Return ONLY valid JSON: {"handleX": number, "handleY": number, "targetX": number, "targetY": number}' +
      ' with pixel coordinates from the top-left of the screenshot.',
      { model: 'sonnet' }
    );

    const cleaned = response.replace(/```json\s*/g, '').replace(/```\s*/g, '').trim();
    const coords = JSON.parse(cleaned);

    // Perform human-like drag
    const dragPath = humanDragPath(coords.handleX, coords.handleY, coords.targetX, coords.targetY);

    await page.mouse.move(coords.handleX, coords.handleY);
    await sleep(randomInt(100, 200));
    await page.mouse.down();
    await sleep(randomInt(50, 100));

    for (const point of dragPath) {
      await page.mouse.move(point.x, point.y);
      await sleep(point.delay);
    }

    await page.mouse.up();
    await sleep(1000);

    // Check for success
    const successText = await page.locator('.success').count();
    if (successText > 0) {
      return { solved: true, message: 'Slider CAPTCHA solved' };
    }

    // If failed, take another screenshot to check offset
    const retryScreenshot = await page.screenshot({ type: 'png' });
    const retryBase64 = retryScreenshot.toString('base64');
    const retryResponse = await analyzeScreenshot(retryBase64,
      'Was the slider CAPTCHA solved? Look for success indicators. ' +
      'If not solved, estimate how many pixels the handle needs to move. ' +
      'Return ONLY valid JSON: {"solved": true/false, "offsetPx": number_or_null}',
      { model: 'sonnet' }
    );

    const retryParsed = JSON.parse(retryResponse.replace(/```json\s*/g, '').replace(/```\s*/g, '').trim());
    return { solved: retryParsed.solved, message: retryParsed.solved ? 'Slider solved on verification' : 'Slider not solved - may need adjustment' };
  } catch (err) {
    return { solved: false, message: 'Slider solver failed: ' + err.message };
  }
}

async function solveTextCaptcha(page, detection) {
  // Use Sonnet vision to read distorted text
  try {
    const screenshot = await page.screenshot({ type: 'png' });
    const base64 = screenshot.toString('base64');
    const response = await analyzeScreenshot(base64,
      'This page has a text CAPTCHA with distorted/warped text on a canvas element. ' +
      'Read the text shown in the CAPTCHA image carefully. ' +
      'Return ONLY valid JSON: {"text": "THE_CAPTCHA_TEXT"}',
      { model: 'sonnet' }
    );

    const cleaned = response.replace(/```json\s*/g, '').replace(/```\s*/g, '').trim();
    const parsed = JSON.parse(cleaned);

    // Type the answer into the input field
    const input = page.locator('#captcha-input');
    if (await input.count() === 0) {
      // Try other selectors
      const altInput = page.locator('input[name*="captcha"]').first();
      if (await altInput.count() > 0) {
        await altInput.fill(parsed.text);
      }
    } else {
      await input.fill(parsed.text);
    }

    // Click verify
    await sleep(randomInt(300, 600));
    const verifyBtn = page.locator('#verify-btn');
    if (await verifyBtn.count() > 0) {
      await verifyBtn.click();
    }

    await sleep(1000);

    const successText = await page.locator('.success').count();
    return { solved: successText > 0, message: successText > 0 ? 'Text CAPTCHA solved' : 'Text entered but verification failed' };
  } catch (err) {
    return { solved: false, message: 'Text CAPTCHA solver failed: ' + err.message };
  }
}

async function solveTurnstile(page, detection) {
  // Turnstile often auto-resolves; wait then click checkbox if visible
  try {
    // Wait for potential auto-resolution
    await sleep(3000);

    // Try clicking the Turnstile checkbox iframe
    try {
      const frame = page.frameLocator('iframe[src*="challenges.cloudflare.com"]').first();
      const checkbox = frame.locator('input[type="checkbox"]');
      if (await checkbox.count() > 0) {
        await sleep(preClickDelay());
        await checkbox.click({ timeout: 3000 });
      }
    } catch {
      // May not have a clickable element
    }

    await sleep(2000);

    // Check if turnstile response token exists
    const hasToken = await page.evaluate(() => {
      const resp = document.querySelector('[name="cf-turnstile-response"]');
      return resp && resp.value && resp.value.length > 0;
    });

    return { solved: hasToken, message: hasToken ? 'Turnstile resolved' : 'Turnstile still pending' };
  } catch (err) {
    return { solved: false, message: 'Turnstile solver failed: ' + err.message };
  }
}

async function solveCloudflareWait(page, detection) {
  // Cloudflare interstitial auto-resolves after verification
  try {
    // Wait for title to change from "Just a moment..."
    for (let i = 0; i < 15; i++) {
      await sleep(1000);
      const title = await page.title();
      if (!title.toLowerCase().includes('just a moment')) {
        return { solved: true, message: 'Cloudflare interstitial resolved after ' + (i + 1) + 's' };
      }
    }
    return { solved: false, message: 'Cloudflare interstitial did not resolve within 15s' };
  } catch (err) {
    return { solved: false, message: 'Cloudflare wait failed: ' + err.message };
  }
}

async function solveHcaptcha(page, detection) {
  // Click hCaptcha checkbox
  try {
    const frame = page.frameLocator('iframe[src*="hcaptcha"]').first();
    const checkbox = frame.locator('#checkbox');
    await sleep(preClickDelay());
    await checkbox.click({ timeout: 5000 });
    await sleep(2000);

    // Check if solved
    const hasToken = await page.evaluate(() => {
      const resp = document.querySelector('[name="h-captcha-response"]');
      return resp && resp.value && resp.value.length > 0;
    });

    return { solved: hasToken, message: hasToken ? 'hCaptcha solved' : 'hCaptcha clicked but may need challenge' };
  } catch (err) {
    return { solved: false, message: 'hCaptcha solver failed: ' + err.message };
  }
}

// ---------------------------------------------------------------------------
// Solver map
// ---------------------------------------------------------------------------

const SOLVER_MAP = {
  'recaptcha_v2': solveRecaptchaCheckbox,
  'recaptcha_image': solveImageGrid,
  'hcaptcha': solveHcaptcha,
  'cloudflare_turnstile': solveTurnstile,
  'cloudflare_interstitial': solveCloudflareWait,
  'slider': solveSlider,
  'text_captcha': solveTextCaptcha,
  'image_grid': solveImageGrid,
};

// ---------------------------------------------------------------------------
// Main orchestrator
// ---------------------------------------------------------------------------

/**
 * Detect and solve CAPTCHA on the current page.
 * @param {Object} page - Playwright page object
 * @param {Object} [options]
 * @param {number} [options.maxAttempts=3] - Max solve attempts
 * @returns {Promise<{solved: boolean, message: string, type?: string, attempts?: number}>}
 */
export async function solveCaptcha(page, { maxAttempts = 3 } = {}) {
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    const detection = await detectCaptcha(page);
    if (!detection.detected) {
      return { solved: true, message: 'No CAPTCHA detected', attempts: attempt };
    }

    const solver = SOLVER_MAP[detection.type];
    if (!solver) {
      return { solved: false, message: `No solver for type: ${detection.type}`, type: detection.type, attempts: attempt };
    }

    const result = await solver(page, detection);
    if (result.solved) {
      return { ...result, type: detection.type, attempts: attempt };
    }

    // Wait before retry with increasing backoff
    if (attempt < maxAttempts) {
      await sleep(1000 * attempt);
    }
  }

  return { solved: false, message: `Failed after ${maxAttempts} attempts`, attempts: maxAttempts };
}

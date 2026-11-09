// CC Browser - Browser Interactions
// Adapted from: OpenClaw src/browser/pw-tools-core.interactions.ts
// All browser actions: click, type, hover, drag, select, etc.

import {
  ensurePageState,
  getPageForTargetId,
  refLocator,
  restoreRoleRefsForTarget,
  getCurrentMode,
} from './session.mjs';
import {
  sleep,
  navigationDelay,
  preClickDelay,
  preTypeDelay,
  interKeyDelay,
  preScrollDelay,
  postLoadDelay,
  humanMousePath,
  clickOffset,
  humanDragPath,
  typingDelays,
} from './human-mode.mjs';
import { detectCaptchaDOM, solveCaptcha } from './captcha.mjs';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normalizeTimeoutMs(value, defaultMs = 8000) {
  const ms = typeof value === 'number' && Number.isFinite(value) ? value : defaultMs;
  return Math.max(500, Math.min(60000, Math.floor(ms)));
}

function requireRef(ref) {
  const trimmed = String(ref || '').trim();
  if (!trimmed) {
    throw new Error('ref is required');
  }
  return trimmed;
}

/**
 * Resolve a locator from ref, text, or selector.
 * Returns { locator, description } where description is a human-readable label.
 * Exactly one of ref, text, or selector must be provided.
 */
export function resolveLocator(page, opts) {
  const { ref, text, selector, exact } = opts;
  const hasRef = ref && String(ref).trim();
  const hasText = text && String(text).trim();
  const hasSelector = selector && String(selector).trim();

  const count = (hasRef ? 1 : 0) + (hasText ? 1 : 0) + (hasSelector ? 1 : 0);
  if (count === 0) {
    throw new Error('One of --ref, --text, or --selector is required');
  }
  if (count > 1) {
    throw new Error('Only one of --ref, --text, or --selector can be used at a time');
  }

  if (hasRef) {
    const refStr = String(ref).trim();
    return { locator: refLocator(page, refStr), description: refStr };
  }

  if (hasText) {
    const textStr = String(text).trim();
    const locator = exact
      ? page.getByText(textStr, { exact: true }).first()
      : page.getByText(textStr).first();
    return { locator, description: `text="${textStr}"` };
  }

  const selectorStr = String(selector).trim();
  return { locator: page.locator(selectorStr).first(), description: `selector="${selectorStr}"` };
}

function toAIFriendlyError(err, ref) {
  const msg = err?.message || String(err);

  // Timeout errors
  if (msg.includes('Timeout') || msg.includes('timeout')) {
    return new Error(
      `Element "${ref}" not found or not visible within timeout. ` +
        `Try scrolling the element into view or waiting for it to appear.`
    );
  }

  // Multiple matches
  if (msg.includes('resolved to') && msg.includes('elements')) {
    return new Error(
      `Multiple elements matched "${ref}". Run a new snapshot to get updated refs.`
    );
  }

  // Not attached
  if (msg.includes('not attached') || msg.includes('detached')) {
    return new Error(
      `Element "${ref}" is no longer attached to the DOM. Run a new snapshot.`
    );
  }

  return new Error(`Action failed on "${ref}": ${msg}`);
}

// ---------------------------------------------------------------------------
// Navigation
// ---------------------------------------------------------------------------

export async function navigateViaPlaywright(opts) {
  const { cdpUrl, targetId, url, waitUntil = 'load', timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  // --- HUMAN MODE INJECTION ---
  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(navigationDelay());
  }

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goto(url, { waitUntil, timeout });

  // --- HUMAN MODE POST-LOAD DELAY ---
  if (mode !== 'fast') {
    await sleep(postLoadDelay());
  }

  // --- AUTO CAPTCHA DETECTION (human/stealth modes) ---
  let captchaResult = null;
  if (mode !== 'fast') {
    try {
      const domCheck = await detectCaptchaDOM(page);
      if (domCheck.detected) {
        captchaResult = await solveCaptcha(page);
      }
    } catch {
      // Auto-detection is best-effort, don't fail navigation
    }
  }

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
    ...(captchaResult ? { captcha: captchaResult } : {}),
  };
}

export async function reloadViaPlaywright(opts) {
  const { cdpUrl, targetId, waitUntil = 'load', timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.reload({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function goBackViaPlaywright(opts) {
  const { cdpUrl, targetId, waitUntil = 'commit', timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goBack({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

export async function goForwardViaPlaywright(opts) {
  const { cdpUrl, targetId, waitUntil = 'commit', timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  const timeout = normalizeTimeoutMs(timeoutMs, 30000);
  await page.goForward({ waitUntil, timeout });

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
  };
}

// ---------------------------------------------------------------------------
// Element Interactions
// ---------------------------------------------------------------------------

export async function clickViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, text, selector, exact, doubleClick, button, modifiers, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const { locator, description } = resolveLocator(page, { ref, text, selector, exact });
  const timeout = normalizeTimeoutMs(timeoutMs);

  // --- HUMAN MODE INJECTION ---
  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(preClickDelay());
    try {
      const box = await locator.boundingBox();
      if (box) {
        const centerX = box.x + box.width / 2;
        const centerY = box.y + box.height / 2;
        const offset = clickOffset();
        const targetX = centerX + offset.x;
        const targetY = centerY + offset.y;
        const path = humanMousePath(0, 0, targetX, targetY);
        for (const point of path) {
          await page.mouse.move(point.x, point.y);
        }
      }
    } catch {
      // Bounding box may fail for offscreen elements - proceed with normal click
    }
  }

  try {
    if (doubleClick) {
      await locator.dblclick({ timeout, button, modifiers });
    } else {
      await locator.click({ timeout, button, modifiers });
    }
  } catch (err) {
    throw toAIFriendlyError(err, description);
  }
}

export async function hoverViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, text, selector, exact, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const { locator, description } = resolveLocator(page, { ref, text, selector, exact });
  const timeout = normalizeTimeoutMs(timeoutMs);

  // --- HUMAN MODE INJECTION ---
  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(preClickDelay());
    try {
      const box = await locator.boundingBox();
      if (box) {
        const targetX = box.x + box.width / 2;
        const targetY = box.y + box.height / 2;
        const path = humanMousePath(0, 0, targetX, targetY);
        for (const point of path) {
          await page.mouse.move(point.x, point.y);
        }
      }
    } catch {
      // Proceed with normal hover
    }
  }

  try {
    await locator.hover({ timeout });
  } catch (err) {
    throw toAIFriendlyError(err, description);
  }
}

export async function dragViaPlaywright(opts) {
  const { cdpUrl, targetId, startRef, endRef, startX, startY, endX, endY, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const mode = getCurrentMode();

  // --- HUMAN MODE DRAG (coordinate-based or ref-based) ---
  if (mode !== 'fast') {
    let sx, sy, ex, ey;

    // Determine start coordinates
    if (typeof startX === 'number' && typeof startY === 'number') {
      sx = startX;
      sy = startY;
    } else if (startRef) {
      const startLoc = refLocator(page, requireRef(startRef));
      const startBox = await startLoc.boundingBox();
      if (!startBox) throw new Error('Could not get bounding box for start element');
      sx = startBox.x + startBox.width / 2;
      sy = startBox.y + startBox.height / 2;
    }

    // Determine end coordinates
    if (typeof endX === 'number' && typeof endY === 'number') {
      ex = endX;
      ey = endY;
    } else if (endRef) {
      const endLoc = refLocator(page, requireRef(endRef));
      const endBox = await endLoc.boundingBox();
      if (!endBox) throw new Error('Could not get bounding box for end element');
      ex = endBox.x + endBox.width / 2;
      ey = endBox.y + endBox.height / 2;
    }

    if (sx !== undefined && sy !== undefined && ex !== undefined && ey !== undefined) {
      await sleep(preClickDelay());
      const dragPath = humanDragPath(sx, sy, ex, ey);

      // Move to start position
      await page.mouse.move(sx, sy);
      await sleep(randomInt(50, 150));

      // Mouse down
      await page.mouse.down();
      await sleep(randomInt(30, 80));

      // Follow the drag path with delays
      for (const point of dragPath) {
        await page.mouse.move(point.x, point.y);
        await sleep(point.delay);
      }

      // Mouse up at final position
      await page.mouse.up();
      return;
    }
  }

  // --- FAST MODE / FALLBACK: use Playwright's built-in dragTo ---
  const start = requireRef(startRef);
  const end = requireRef(endRef);
  const timeout = normalizeTimeoutMs(timeoutMs);

  try {
    await refLocator(page, start).dragTo(refLocator(page, end), { timeout });
  } catch (err) {
    throw toAIFriendlyError(err, `${start} -> ${end}`);
  }
}

// Helper for human-mode drag with random int
function randomInt(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

export async function selectOptionViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, values, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const refStr = requireRef(ref);
  if (!values?.length) {
    throw new Error('values are required');
  }

  const locator = refLocator(page, refStr);
  const timeout = normalizeTimeoutMs(timeoutMs);

  try {
    await locator.selectOption(values, { timeout });
  } catch (err) {
    throw toAIFriendlyError(err, refStr);
  }
}

export async function highlightViaPlaywright(opts) {
  const { cdpUrl, targetId, ref } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const refStr = requireRef(ref);
  try {
    await refLocator(page, refStr).highlight();
  } catch (err) {
    throw toAIFriendlyError(err, refStr);
  }
}

// ---------------------------------------------------------------------------
// Text Input
// ---------------------------------------------------------------------------

export async function pressKeyViaPlaywright(opts) {
  const { cdpUrl, targetId, key, delayMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  const keyStr = String(key || '').trim();
  if (!keyStr) {
    throw new Error('key is required');
  }

  // --- HUMAN MODE INJECTION ---
  const mode = getCurrentMode();
  if (mode !== 'fast') {
    await sleep(preClickDelay());
  }

  await page.keyboard.press(keyStr, {
    delay: Math.max(0, Math.floor(delayMs || 0)),
  });
}

export async function typeViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, text, selector, exact, submit, slowly, timeoutMs } = opts;
  const textContent = opts.textContent;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  // For type command, 'text' is the text to type, not a locator.
  // Use 'textContent' for text-based element resolution to avoid conflict.
  const { locator, description } = resolveLocator(page, {
    ref,
    text: textContent,
    selector,
    exact,
  });
  const textStr = String(text ?? '');
  const timeout = normalizeTimeoutMs(timeoutMs);

  const mode = getCurrentMode();

  try {
    if (mode !== 'fast') {
      // Human mode: always type character-by-character with variable delays
      await sleep(preTypeDelay());
      await locator.click({ timeout });
      const delays = typingDelays(textStr);
      for (let i = 0; i < textStr.length; i++) {
        await page.keyboard.type(textStr[i], { delay: 0 });
        if (i < textStr.length - 1) {
          await sleep(delays[i]);
        }
      }
    } else if (slowly) {
      await locator.click({ timeout });
      await locator.type(textStr, { timeout, delay: 75 });
    } else {
      await locator.fill(textStr, { timeout });
    }
    if (submit) {
      if (mode !== 'fast') await sleep(preClickDelay());
      await locator.press('Enter', { timeout });
    }
  } catch (err) {
    throw toAIFriendlyError(err, description);
  }
}

export async function fillFormViaPlaywright(opts) {
  const { cdpUrl, targetId, fields, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const timeout = normalizeTimeoutMs(timeoutMs);
  const mode = getCurrentMode();

  for (const field of fields || []) {
    // --- HUMAN MODE INJECTION: delay between fields ---
    if (mode !== 'fast') {
      await sleep(preTypeDelay());
    }
    const ref = String(field.ref || '').trim();
    const type = String(field.type || '').trim();
    const rawValue = field.value;

    if (!ref || !type) continue;

    const value =
      typeof rawValue === 'string'
        ? rawValue
        : typeof rawValue === 'number' || typeof rawValue === 'boolean'
          ? String(rawValue)
          : '';

    const locator = refLocator(page, ref);

    try {
      if (type === 'checkbox' || type === 'radio') {
        const checked =
          rawValue === true || rawValue === 1 || rawValue === '1' || rawValue === 'true';
        await locator.setChecked(checked, { timeout });
      } else {
        await locator.fill(value, { timeout });
      }
    } catch (err) {
      throw toAIFriendlyError(err, ref);
    }
  }
}

// ---------------------------------------------------------------------------
// JavaScript Evaluation
// ---------------------------------------------------------------------------

export async function evaluateViaPlaywright(opts) {
  const { cdpUrl, targetId, fn, ref } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const fnText = String(fn || '').trim();
  if (!fnText) {
    throw new Error('function is required');
  }

  if (ref) {
    const locator = refLocator(page, ref);
    const elementEvaluator = new Function(
      'el',
      'fnBody',
      `
      "use strict";
      try {
        var candidate = eval("(" + fnBody + ")");
        return typeof candidate === "function" ? candidate(el) : candidate;
      } catch (err) {
        throw new Error("Invalid evaluate function: " + (err && err.message ? err.message : String(err)));
      }
      `
    );
    return await locator.evaluate(elementEvaluator, fnText);
  }

  const browserEvaluator = new Function(
    'fnBody',
    `
    "use strict";
    try {
      var candidate = eval("(" + fnBody + ")");
      return typeof candidate === "function" ? candidate() : candidate;
    } catch (err) {
      throw new Error("Invalid evaluate function: " + (err && err.message ? err.message : String(err)));
    }
    `
  );
  return await page.evaluate(browserEvaluator, fnText);
}

// ---------------------------------------------------------------------------
// Scrolling
// ---------------------------------------------------------------------------

export async function scrollIntoViewViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  const refStr = requireRef(ref);
  const timeout = normalizeTimeoutMs(timeoutMs, 20000);

  try {
    await refLocator(page, refStr).scrollIntoViewIfNeeded({ timeout });
  } catch (err) {
    throw toAIFriendlyError(err, refStr);
  }
}

export async function scrollViaPlaywright(opts) {
  const { cdpUrl, targetId, direction, amount, ref, timeoutMs } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  // If ref provided, scroll to element
  if (ref) {
    const timeout = normalizeTimeoutMs(timeoutMs, 20000);
    try {
      await refLocator(page, ref).scrollIntoViewIfNeeded({ timeout });
    } catch (err) {
      throw toAIFriendlyError(err, ref);
    }
    return;
  }

  // Otherwise, scroll viewport
  const scrollAmount = amount || 500;
  const dir = String(direction || 'down').toLowerCase();

  let deltaX = 0;
  let deltaY = 0;

  switch (dir) {
    case 'up':
      deltaY = -scrollAmount;
      break;
    case 'down':
      deltaY = scrollAmount;
      break;
    case 'left':
      deltaX = -scrollAmount;
      break;
    case 'right':
      deltaX = scrollAmount;
      break;
    default:
      deltaY = scrollAmount;
  }

  const mode = getCurrentMode();
  if (mode !== 'fast') {
    // Human mode: multi-step scroll with variable delta
    await sleep(preScrollDelay());
    const steps = randomInt(3, 6);
    const stepDeltaX = Math.round(deltaX / steps);
    const stepDeltaY = Math.round(deltaY / steps);
    for (let i = 0; i < steps; i++) {
      // Add slight variation to each step
      const varX = stepDeltaX + randomInt(-10, 10);
      const varY = stepDeltaY + randomInt(-10, 10);
      await page.mouse.wheel(varX, varY);
      await sleep(randomInt(30, 100));
    }
  } else {
    await page.mouse.wheel(deltaX, deltaY);
  }
}

// ---------------------------------------------------------------------------
// Wait
// ---------------------------------------------------------------------------

export async function waitForViaPlaywright(opts) {
  const { cdpUrl, targetId, timeMs, text, textGone, selector, url, loadState, fn, timeoutMs } =
    opts;

  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  const timeout = normalizeTimeoutMs(timeoutMs, 20000);

  if (typeof timeMs === 'number' && Number.isFinite(timeMs)) {
    await page.waitForTimeout(Math.max(0, timeMs));
  }

  if (text) {
    await page.getByText(text).first().waitFor({ state: 'visible', timeout });
  }

  if (textGone) {
    await page.getByText(textGone).first().waitFor({ state: 'hidden', timeout });
  }

  if (selector) {
    const sel = String(selector).trim();
    if (sel) {
      await page.locator(sel).first().waitFor({ state: 'visible', timeout });
    }
  }

  if (url) {
    const urlStr = String(url).trim();
    if (urlStr) {
      await page.waitForURL(urlStr, { timeout });
    }
  }

  if (loadState) {
    await page.waitForLoadState(loadState, { timeout });
  }

  if (fn) {
    const fnStr = String(fn).trim();
    if (fnStr) {
      await page.waitForFunction(fnStr, { timeout });
    }
  }
}

// ---------------------------------------------------------------------------
// Screenshots
// ---------------------------------------------------------------------------

export async function takeScreenshotViaPlaywright(opts) {
  const { cdpUrl, targetId, ref, element, fullPage, type = 'png' } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  if (ref) {
    if (fullPage) {
      throw new Error('fullPage is not supported for element screenshots');
    }
    const locator = refLocator(page, ref);
    const buffer = await locator.screenshot({ type });
    return { buffer };
  }

  if (element) {
    if (fullPage) {
      throw new Error('fullPage is not supported for element screenshots');
    }
    const locator = page.locator(element).first();
    const buffer = await locator.screenshot({ type });
    return { buffer };
  }

  const buffer = await page.screenshot({ type, fullPage: Boolean(fullPage) });
  return { buffer };
}

// ---------------------------------------------------------------------------
// File Upload
// ---------------------------------------------------------------------------

export async function setInputFilesViaPlaywright(opts) {
  const { cdpUrl, targetId, inputRef, element, paths } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  if (!paths?.length) {
    throw new Error('paths are required');
  }

  const ref = String(inputRef || '').trim();
  const el = String(element || '').trim();

  if (ref && el) {
    throw new Error('inputRef and element are mutually exclusive');
  }
  if (!ref && !el) {
    throw new Error('inputRef or element is required');
  }

  const locator = ref ? refLocator(page, ref) : page.locator(el).first();

  try {
    await locator.setInputFiles(paths);
  } catch (err) {
    throw toAIFriendlyError(err, ref || el);
  }

  // Dispatch events for sites that don't react to setInputFiles alone
  try {
    const handle = await locator.elementHandle();
    if (handle) {
      await handle.evaluate((el) => {
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      });
    }
  } catch {
    // Best-effort
  }
}

// ---------------------------------------------------------------------------
// Viewport
// ---------------------------------------------------------------------------

export async function resizeViaPlaywright(opts) {
  const { cdpUrl, targetId, width, height } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  if (typeof width !== 'number' || typeof height !== 'number') {
    throw new Error('width and height are required');
  }

  await page.setViewportSize({
    width: Math.max(320, Math.floor(width)),
    height: Math.max(240, Math.floor(height)),
  });

  return { width, height };
}

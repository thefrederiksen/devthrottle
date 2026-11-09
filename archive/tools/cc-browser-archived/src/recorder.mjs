// CC Browser - Recording Engine
// Captures user interactions via CDP script injection and polls for events.
// Each captured action includes multiple locator strategies for replay stability.

import { getPageForTargetId } from './session.mjs';

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

let recording = false;
let steps = [];
let startedAt = null;
let pollTimer = null;
let lastClickDrainTime = 0;

// ---------------------------------------------------------------------------
// Injected Script
// ---------------------------------------------------------------------------

// This script runs inside the browser page. It captures user interactions
// and stores them in window.__ccRecorderEvents for the daemon to drain.
// Events are drained via polling AND via CDP Page.frameRequestedNavigation
// (before navigation destroys the page context).
// It is re-injected on every navigation via Page.addScriptToEvaluateOnNewDocument.

const INJECTED_SCRIPT = `
(function() {
  if (window.__ccRecorderActive) return;
  window.__ccRecorderActive = true;
  window.__ccRecorderEvents = window.__ccRecorderEvents || [];

  // --- Helpers ---

  function cssPath(el) {
    if (!el || el === document.body || el === document.documentElement) return '';
    var parts = [];
    var current = el;
    while (current && current !== document.body && current !== document.documentElement) {
      var tag = current.tagName.toLowerCase();
      var parent = current.parentElement;
      if (parent) {
        var siblings = Array.from(parent.children).filter(function(c) { return c.tagName === current.tagName; });
        if (siblings.length > 1) {
          var idx = siblings.indexOf(current) + 1;
          tag += ':nth-of-type(' + idx + ')';
        }
      }
      parts.unshift(tag);
      current = parent;
    }
    return 'body > ' + parts.join(' > ');
  }

  function buildSelector(el) {
    if (el.id) return '#' + CSS.escape(el.id);
    var tag = el.tagName.toLowerCase();
    var cls = Array.from(el.classList || []).slice(0, 3).map(function(c) { return '.' + CSS.escape(c); }).join('');
    if (cls) return tag + cls;
    return '';
  }

  function ariaRole(el) {
    var explicit = el.getAttribute('role');
    if (explicit) return explicit;
    var tag = el.tagName.toLowerCase();
    var type = (el.getAttribute('type') || '').toLowerCase();
    var roleMap = {
      button: 'button', a: 'link', input: 'textbox', textarea: 'textbox',
      select: 'combobox', img: 'img', h1: 'heading', h2: 'heading',
      h3: 'heading', h4: 'heading', h5: 'heading', h6: 'heading',
      nav: 'navigation', main: 'main', aside: 'complementary',
      footer: 'contentinfo', header: 'banner', form: 'form',
      table: 'table', dialog: 'dialog',
    };
    if (tag === 'input') {
      if (type === 'checkbox') return 'checkbox';
      if (type === 'radio') return 'radio';
      if (type === 'submit' || type === 'button') return 'button';
      if (type === 'range') return 'slider';
      return 'textbox';
    }
    return roleMap[tag] || '';
  }

  function ariaName(el) {
    var label = el.getAttribute('aria-label');
    if (label) return label;
    var labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      var labelEl = document.getElementById(labelledBy);
      if (labelEl) return labelEl.textContent.trim().substring(0, 100);
    }
    var tag = el.tagName.toLowerCase();
    if (tag === 'input' || tag === 'textarea' || tag === 'select') {
      var id = el.id;
      if (id) {
        var labelFor = document.querySelector('label[for="' + CSS.escape(id) + '"]');
        if (labelFor) return labelFor.textContent.trim().substring(0, 100);
      }
      var placeholder = el.getAttribute('placeholder');
      if (placeholder) return placeholder;
    }
    if (tag === 'img') {
      var alt = el.getAttribute('alt');
      if (alt) return alt;
    }
    if (tag === 'button' || tag === 'a') {
      var text = el.textContent.trim().substring(0, 100);
      if (text) return text;
    }
    return '';
  }

  function visibleText(el) {
    var text = el.textContent || '';
    return text.trim().substring(0, 100);
  }

  function buildLocators(el) {
    var locators = [];
    var role = ariaRole(el);
    var name = ariaName(el);
    if (role && name) {
      locators.push({ strategy: 'role', role: role, name: name });
    } else if (role) {
      locators.push({ strategy: 'role', role: role });
    }
    var text = visibleText(el);
    if (text && text.length > 1 && text.length < 80) {
      locators.push({ strategy: 'text', text: text });
    }
    var sel = buildSelector(el);
    if (sel) {
      locators.push({ strategy: 'selector', selector: sel });
    }
    var path = cssPath(el);
    if (path) {
      locators.push({ strategy: 'cssPath', path: path });
    }
    return locators;
  }

  // --- Event Handlers ---

  var typeBuffer = '';
  var typeTarget = null;
  var typeTimer = null;

  function flushType() {
    if (typeBuffer && typeTarget) {
      window.__ccRecorderEvents.push({
        action: 'type',
        locators: buildLocators(typeTarget),
        value: typeBuffer,
        timestamp: Date.now(),
      });
    }
    typeBuffer = '';
    typeTarget = null;
    typeTimer = null;
  }

  document.addEventListener('click', function(e) {
    var el = e.target;
    if (!el || !el.tagName) return;
    window.__ccRecorderEvents.push({
      action: 'click',
      locators: buildLocators(el),
      timestamp: Date.now(),
    });
  }, true);

  // Flush type buffer when user leaves a field (captures final value)
  document.addEventListener('focusout', function(e) {
    if (typeBuffer) flushType();
  }, true);

  document.addEventListener('input', function(e) {
    var el = e.target;
    if (!el || !el.tagName) return;
    var tag = el.tagName.toLowerCase();
    if (tag === 'select') {
      window.__ccRecorderEvents.push({
        action: 'select',
        locators: buildLocators(el),
        value: el.value,
        timestamp: Date.now(),
      });
      return;
    }
    // Debounce text input -- accumulate characters, flush after 500ms idle
    if (tag === 'input' || tag === 'textarea') {
      if (typeTarget !== el) {
        // Switched elements, flush previous
        if (typeBuffer) flushType();
        typeTarget = el;
        typeBuffer = '';
      }
      typeBuffer = el.value;
      if (typeTimer) clearTimeout(typeTimer);
      typeTimer = setTimeout(flushType, 500);
    }
  }, true);

  document.addEventListener('keydown', function(e) {
    var key = e.key;
    if (key === 'Enter' || key === 'Escape' || key === 'Tab') {
      // Flush any pending type first
      if (typeBuffer) flushType();
      window.__ccRecorderEvents.push({
        action: 'keypress',
        key: key,
        locators: e.target && e.target.tagName ? buildLocators(e.target) : [],
        timestamp: Date.now(),
      });
    }
  }, true);

  // Scroll capture (debounced)
  var scrollTimer = null;
  window.addEventListener('scroll', function() {
    if (scrollTimer) clearTimeout(scrollTimer);
    scrollTimer = setTimeout(function() {
      window.__ccRecorderEvents.push({
        action: 'scroll',
        scrollX: window.scrollX,
        scrollY: window.scrollY,
        timestamp: Date.now(),
      });
    }, 300);
  }, true);

  // Flush events via sendBeacon before the page is destroyed.
  // This is critical for capturing clicks that cause full-page navigation.
  window.addEventListener('beforeunload', function() {
    if (window.__ccRecorderEvents && window.__ccRecorderEvents.length > 0 && window.__ccRecorderBeaconPort) {
      var url = 'http://127.0.0.1:' + window.__ccRecorderBeaconPort + '/record/beacon';
      navigator.sendBeacon(url, JSON.stringify(window.__ccRecorderEvents));
    }
  });
})();
`;

// ---------------------------------------------------------------------------
// Recording Control
// ---------------------------------------------------------------------------

/**
 * Start recording interactions on the active page.
 * Injects the capture script and begins polling for events.
 */
export async function startRecording({ cdpUrl, targetId, daemonPort }) {
  if (recording) {
    throw new Error('Recording already in progress. Stop it first.');
  }

  const page = await getPageForTargetId({ cdpUrl, targetId });

  // Reset state
  recording = true;
  steps = [];
  startedAt = new Date().toISOString();

  // Record the initial URL as the first step
  const currentUrl = page.url();
  if (currentUrl && currentUrl !== 'about:blank') {
    steps.push({ action: 'navigate', url: currentUrl });
  }

  // Inject the recording script into the current page
  await page.evaluate(INJECTED_SCRIPT);

  // Set the beacon port so the beforeunload handler can send events
  if (daemonPort) {
    await page.evaluate((port) => { window.__ccRecorderBeaconPort = port; }, daemonPort);
    // Also set it on future navigations
    await page.context().addInitScript(`window.__ccRecorderBeaconPort = ${daemonPort};`);
  }

  // Also inject on future navigations within this page context
  await page.context().addInitScript(INJECTED_SCRIPT);

  // Listen for navigations (fires AFTER navigation completes)
  const navigationHandler = (frame) => {
    if (frame === page.mainFrame()) {
      const url = frame.url();
      // Avoid recording about:blank and duplicate navigations
      if (url && url !== 'about:blank') {
        // Suppress navigations that happen within 2s of a click -- these are
        // SPA routing side-effects, not user-initiated navigations.
        if (Date.now() - lastClickDrainTime < 2000) {
          return;
        }
        const lastStep = steps[steps.length - 1];
        if (!lastStep || lastStep.action !== 'navigate' || lastStep.url !== url) {
          steps.push({ action: 'navigate', url });
        }
      }
    }
  };
  page.on('framenavigated', navigationHandler);

  // Store reference for cleanup
  page.__ccRecorderNavHandler = navigationHandler;

  // Start polling for captured events
  pollTimer = setInterval(async () => {
    if (!recording) return;
    try {
      await drainEvents(page);
    } catch {
      // Page may have navigated or closed -- keep polling
    }
  }, 250);

  console.log('[cc-browser] Recording started');
  return { recording: true, startedAt };
}

/**
 * Drain captured events from the page into our steps array.
 * Called periodically by the poll timer and also just before navigation
 * (via CDP Page.frameRequestedNavigation) to capture events that would
 * otherwise be lost when the page context is destroyed.
 */
async function drainEvents(page) {
  const events = await page.evaluate(() => {
    if (!window.__ccRecorderEvents) return [];
    const evts = window.__ccRecorderEvents.splice(0);
    return evts;
  });

  for (const evt of events) {
    const step = { ...evt };
    delete step.timestamp;
    steps.push(step);
    if (step.action === 'click') {
      lastClickDrainTime = Date.now();
    }
  }
}

/**
 * Stop recording and return the captured steps.
 */
export async function stopRecording({ cdpUrl, targetId }) {
  if (!recording) {
    throw new Error('No recording in progress.');
  }

  // Final drain
  try {
    const page = await getPageForTargetId({ cdpUrl, targetId });
    await drainEvents(page);

    // Clean up navigation listener
    if (page.__ccRecorderNavHandler) {
      page.off('framenavigated', page.__ccRecorderNavHandler);
      delete page.__ccRecorderNavHandler;
    }

    // Clean up injected state
    await page.evaluate(() => {
      delete window.__ccRecorderActive;
      delete window.__ccRecorderEvents;
    }).catch(() => {});
  } catch {
    // Page may be gone -- that's fine, we have whatever we captured
  }

  // Stop polling
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }

  const result = {
    name: '',
    recordedAt: startedAt,
    steps: [...steps],
  };

  // Reset state
  recording = false;
  steps = [];
  startedAt = null;
  lastClickDrainTime = 0;

  console.log(`[cc-browser] Recording stopped (${result.steps.length} steps)`);
  return result;
}

/**
 * Receive events sent via sendBeacon from the browser's beforeunload handler.
 * These are events that would otherwise be lost when full-page navigation
 * destroys the old page context before the poll timer can drain them.
 */
export function receiveBeaconEvents(events) {
  if (!recording || !Array.isArray(events)) return;
  for (const evt of events) {
    const step = { ...evt };
    delete step.timestamp;
    steps.push(step);
    if (step.action === 'click') {
      lastClickDrainTime = Date.now();
    }
  }
}

/**
 * Get current recording status.
 */
export function getRecordingStatus() {
  return {
    recording,
    startedAt,
    stepCount: steps.length,
  };
}

// ---------------------------------------------------------------------------
// Exported for testing
// ---------------------------------------------------------------------------

export { INJECTED_SCRIPT };

/**
 * Build locator strategies for a DOM element description.
 * Exported for unit testing -- mirrors the in-browser buildLocators logic.
 */
export function buildLocatorsFromInfo({ role, name, text, selector, cssPath }) {
  const locators = [];
  if (role && name) {
    locators.push({ strategy: 'role', role, name });
  } else if (role) {
    locators.push({ strategy: 'role', role });
  }
  if (text && text.length > 1 && text.length < 80) {
    locators.push({ strategy: 'text', text });
  }
  if (selector) {
    locators.push({ strategy: 'selector', selector });
  }
  if (cssPath) {
    locators.push({ strategy: 'cssPath', path: cssPath });
  }
  return locators;
}

/**
 * Normalize raw events: dedup consecutive navigations, merge adjacent types.
 * Exported for unit testing.
 */
export function normalizeEvents(rawSteps) {
  const result = [];
  for (const step of rawSteps) {
    const last = result[result.length - 1];
    // Dedup consecutive navigations to the same URL
    if (step.action === 'navigate' && last && last.action === 'navigate' && last.url === step.url) {
      continue;
    }
    result.push(step);
  }
  return result;
}

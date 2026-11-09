// CC Browser - Replay Engine
// Reads a JSON recording and replays each step using Playwright locators.
// Tries multiple locator strategies per step for maximum resilience.

import { getPageForTargetId, getCurrentMode } from './session.mjs';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Get delay between replay steps based on mode.
 */
function stepDelay(mode) {
  if (mode === 'fast') return 100;
  // Human-like delay: 400-900ms
  return 400 + Math.floor(Math.random() * 500);
}

// ---------------------------------------------------------------------------
// Locator Resolution
// ---------------------------------------------------------------------------

/**
 * Resolve a locator from a list of strategies.
 * Tries each strategy in order. Returns { locator, strategy } or throws.
 */
export async function resolveLocator(page, locators, timeoutMs = 5000) {
  if (!locators || locators.length === 0) {
    throw new Error('No locators provided for this step');
  }

  const errors = [];

  for (const loc of locators) {
    try {
      let locator;
      let desc;

      switch (loc.strategy) {
        case 'role': {
          const opts = {};
          if (loc.name) opts.name = loc.name;
          locator = page.getByRole(loc.role, opts).first();
          desc = `role=${loc.role}` + (loc.name ? `[name="${loc.name}"]` : '');
          break;
        }
        case 'text': {
          locator = page.getByText(loc.text).first();
          desc = `text="${loc.text}"`;
          break;
        }
        case 'selector': {
          locator = page.locator(loc.selector).first();
          desc = `selector="${loc.selector}"`;
          break;
        }
        case 'cssPath': {
          locator = page.locator(loc.path).first();
          desc = `cssPath="${loc.path}"`;
          break;
        }
        default:
          continue;
      }

      // Verify the element exists and is visible
      await locator.waitFor({ state: 'visible', timeout: timeoutMs });
      return { locator, strategy: loc.strategy, description: desc };
    } catch (err) {
      errors.push(`${loc.strategy}: ${err.message}`);
    }
  }

  throw new Error(
    `Could not find element with any locator strategy. Tried:\n` +
    errors.map((e) => `  - ${e}`).join('\n')
  );
}

// ---------------------------------------------------------------------------
// Step Execution
// ---------------------------------------------------------------------------

/**
 * Execute a single replay step.
 * Returns { status: 'pass'|'fail', action, detail?, error? }
 */
export async function executeStep(page, step, opts = {}) {
  const { timeoutMs = 8000 } = opts;

  switch (step.action) {
    case 'navigate': {
      await page.goto(step.url, { waitUntil: 'load', timeout: 30000 });
      // Wait for any JS redirects to settle (client-side auth redirects, etc.)
      await page.waitForLoadState('networkidle', { timeout: 5000 }).catch(() => {});
      // Get the actual URL directly from the browser (page.url() can be stale in CDP mode)
      // NOTE: Must use try/catch, not .catch() -- the evaluate can race with navigation
      // and page.url() fallback returns stale pre-redirect URL in CDP mode
      let actualUrl;
      try {
        actualUrl = await page.evaluate(() => window.location.href);
      } catch (err1) {
        // Evaluate can race with navigation in CDP mode -- wait and retry
        await sleep(500);
        try {
          actualUrl = await page.evaluate(() => window.location.href);
        } catch (err2) {
          // Both evaluate attempts failed -- fall back to page.url()
          actualUrl = page.url();
        }
      }
      const expectedPath = new URL(step.url).pathname;
      const actualPath = new URL(actualUrl).pathname;
      if (actualPath !== expectedPath) {
        return {
          status: 'fail',
          action: 'navigate',
          fatal: true,
          error: `Redirected: expected ${expectedPath} but landed on ${actualPath}`,
          detail: step.url,
        };
      }
      return { status: 'pass', action: 'navigate', detail: step.url };
    }

    case 'click': {
      const { locator, description } = await resolveLocator(page, step.locators, timeoutMs);
      await locator.click({ timeout: timeoutMs });
      return { status: 'pass', action: 'click', detail: description };
    }

    case 'type': {
      const { locator, description } = await resolveLocator(page, step.locators, timeoutMs);
      await locator.fill(step.value || '', { timeout: timeoutMs });
      return { status: 'pass', action: 'type', detail: `${description} = "${step.value}"` };
    }

    case 'select': {
      const { locator, description } = await resolveLocator(page, step.locators, timeoutMs);
      await locator.selectOption(step.value, { timeout: timeoutMs });
      return { status: 'pass', action: 'select', detail: `${description} = "${step.value}"` };
    }

    case 'keypress': {
      if (step.locators && step.locators.length > 0) {
        try {
          const { locator } = await resolveLocator(page, step.locators, timeoutMs);
          await locator.press(step.key, { timeout: timeoutMs });
        } catch (_locatorErr) {
          // Element not found -- press on page level instead
          await page.keyboard.press(step.key);
        }
      } else {
        await page.keyboard.press(step.key);
      }
      return { status: 'pass', action: 'keypress', detail: step.key };
    }

    case 'scroll': {
      await page.evaluate(({ x, y }) => {
        window.scrollTo(x, y);
      }, { x: step.scrollX || 0, y: step.scrollY || 0 });
      return { status: 'pass', action: 'scroll', detail: `(${step.scrollX}, ${step.scrollY})` };
    }

    default:
      return { status: 'fail', action: step.action, error: `Unknown action: ${step.action}` };
  }
}

// ---------------------------------------------------------------------------
// Main Replay Function
// ---------------------------------------------------------------------------

/**
 * Replay a recorded session.
 * @param {object} opts
 * @param {string} opts.cdpUrl - CDP connection URL
 * @param {string} [opts.targetId] - Tab target ID
 * @param {object} opts.recording - The parsed recording JSON
 * @param {string} [opts.mode] - 'fast' or 'human' (default: current daemon mode)
 * @param {number} [opts.timeoutMs] - Per-step timeout (default: 8000)
 * @returns {object} { results, summary }
 */
export async function replayRecording({ cdpUrl, targetId, recording, mode, timeoutMs = 8000 }) {
  const page = await getPageForTargetId({ cdpUrl, targetId });
  const replayMode = mode || getCurrentMode() || 'human';

  const results = [];
  let passed = 0;
  let failed = 0;

  for (let i = 0; i < recording.steps.length; i++) {
    const step = recording.steps[i];

    // Wait for page stability before each step
    await page.waitForLoadState('domcontentloaded', { timeout: 5000 }).catch(() => {});

    try {
      const result = await executeStep(page, step, { timeoutMs });
      result.stepIndex = i;
      results.push(result);
      if (result.status === 'fail') {
        failed++;
        console.log(`[cc-browser] Step ${i + 1}/${recording.steps.length}: ${result.action} -> FAIL (${result.error || ''})`);
        // Fatal navigate failure -- no point continuing on wrong page
        if (result.fatal) {
          console.log('[cc-browser] Fatal navigation failure -- stopping replay');
          break;
        }
      } else {
        passed++;
        console.log(`[cc-browser] Step ${i + 1}/${recording.steps.length}: ${result.action} -> PASS (${result.detail || ''})`);
      }
    } catch (err) {
      const result = {
        status: 'fail',
        action: step.action,
        stepIndex: i,
        error: err.message,
      };
      results.push(result);
      failed++;
      console.log(`[cc-browser] Step ${i + 1}/${recording.steps.length}: ${step.action} -> FAIL (${err.message})`);
    }

    // Delay between steps
    if (i < recording.steps.length - 1) {
      await sleep(stepDelay(replayMode));
    }
  }

  const summary = {
    total: recording.steps.length,
    passed,
    failed,
    status: failed === 0 ? 'pass' : 'fail',
  };

  console.log(`[cc-browser] Replay complete: ${passed}/${summary.total} passed`);
  return { results, summary };
}

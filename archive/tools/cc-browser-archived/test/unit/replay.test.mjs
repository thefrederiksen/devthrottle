// Unit tests for replay.mjs -- locator resolution and step execution
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import { resolveLocator, executeStep } from '../../src/replay.mjs';

// ---------------------------------------------------------------------------
// resolveLocator -- unit tests with mock page objects
// ---------------------------------------------------------------------------

// Minimal mock that simulates Playwright's page locator API
function mockPage(opts = {}) {
  const { roleMatch = true, textMatch = true, selectorMatch = true } = opts;

  function makeMockLocator(shouldResolve) {
    return {
      waitFor: async ({ state, timeout } = {}) => {
        if (!shouldResolve) throw new Error('Element not found');
      },
      click: async () => {},
      fill: async () => {},
    };
  }

  return {
    getByRole: (role, options) => ({
      first: () => makeMockLocator(roleMatch),
    }),
    getByText: (text) => ({
      first: () => makeMockLocator(textMatch),
    }),
    locator: (selector) => ({
      first: () => makeMockLocator(selectorMatch),
    }),
  };
}

describe('resolveLocator', () => {
  it('resolves role locator when available', async () => {
    const page = mockPage({ roleMatch: true });
    const locators = [
      { strategy: 'role', role: 'button', name: 'Submit' },
      { strategy: 'text', text: 'Submit' },
    ];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.strategy, 'role');
    assert.ok(result.description.includes('button'));
  });

  it('falls back to text when role fails', async () => {
    const page = mockPage({ roleMatch: false, textMatch: true });
    const locators = [
      { strategy: 'role', role: 'button', name: 'Submit' },
      { strategy: 'text', text: 'Submit' },
    ];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.strategy, 'text');
  });

  it('falls back to selector when role and text fail', async () => {
    const page = mockPage({ roleMatch: false, textMatch: false, selectorMatch: true });
    const locators = [
      { strategy: 'role', role: 'button', name: 'Submit' },
      { strategy: 'text', text: 'Submit' },
      { strategy: 'selector', selector: '#submit-btn' },
    ];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.strategy, 'selector');
  });

  it('falls back to cssPath when all others fail', async () => {
    // cssPath uses page.locator() just like selector
    const page = mockPage({ roleMatch: false, textMatch: false, selectorMatch: true });
    const locators = [
      { strategy: 'role', role: 'button', name: 'Submit' },
      { strategy: 'text', text: 'Submit' },
      { strategy: 'cssPath', path: 'body > div > button' },
    ];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.strategy, 'cssPath');
  });

  it('throws when no locator succeeds', async () => {
    const page = mockPage({ roleMatch: false, textMatch: false, selectorMatch: false });
    const locators = [
      { strategy: 'role', role: 'button', name: 'Submit' },
      { strategy: 'text', text: 'Submit' },
    ];

    await assert.rejects(
      () => resolveLocator(page, locators, 1000),
      (err) => {
        assert.ok(err.message.includes('Could not find element'));
        return true;
      }
    );
  });

  it('throws when locators array is empty', async () => {
    const page = mockPage();

    await assert.rejects(
      () => resolveLocator(page, [], 1000),
      (err) => {
        assert.ok(err.message.includes('No locators provided'));
        return true;
      }
    );
  });

  it('throws when locators is null', async () => {
    const page = mockPage();

    await assert.rejects(
      () => resolveLocator(page, null, 1000),
      (err) => {
        assert.ok(err.message.includes('No locators provided'));
        return true;
      }
    );
  });

  it('skips unknown strategy types', async () => {
    const page = mockPage({ roleMatch: true });
    const locators = [
      { strategy: 'unknown', value: 'foo' },
      { strategy: 'role', role: 'button', name: 'OK' },
    ];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.strategy, 'role');
  });

  it('returns correct description for role locator with name', async () => {
    const page = mockPage({ roleMatch: true });
    const locators = [{ strategy: 'role', role: 'link', name: 'Home' }];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.description, 'role=link[name="Home"]');
  });

  it('returns correct description for role locator without name', async () => {
    const page = mockPage({ roleMatch: true });
    const locators = [{ strategy: 'role', role: 'textbox' }];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.description, 'role=textbox');
  });

  it('returns correct description for text locator', async () => {
    const page = mockPage({ textMatch: true });
    const locators = [{ strategy: 'text', text: 'Click me' }];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.description, 'text="Click me"');
  });

  it('returns correct description for selector locator', async () => {
    const page = mockPage({ selectorMatch: true });
    const locators = [{ strategy: 'selector', selector: '#main-btn' }];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.description, 'selector="#main-btn"');
  });

  it('returns correct description for cssPath locator', async () => {
    const page = mockPage({ selectorMatch: true });
    const locators = [{ strategy: 'cssPath', path: 'body > div > a' }];

    const result = await resolveLocator(page, locators, 1000);
    assert.equal(result.description, 'cssPath="body > div > a"');
  });
});

// ---------------------------------------------------------------------------
// Recording format validation
// ---------------------------------------------------------------------------

describe('recording format', () => {
  it('validates a well-formed recording object', () => {
    const recording = {
      name: 'Login Test',
      recordedAt: '2026-02-27T14:30:00.000Z',
      steps: [
        { action: 'navigate', url: 'https://example.com/login' },
        {
          action: 'click',
          locators: [
            { strategy: 'role', role: 'textbox', name: 'Email' },
            { strategy: 'selector', selector: '#email-input' },
          ],
        },
        {
          action: 'type',
          locators: [{ strategy: 'role', role: 'textbox', name: 'Email' }],
          value: 'test@example.com',
        },
        {
          action: 'click',
          locators: [
            { strategy: 'role', role: 'button', name: 'Sign In' },
            { strategy: 'text', text: 'Sign In' },
          ],
        },
      ],
    };

    assert.ok(Array.isArray(recording.steps));
    assert.equal(recording.steps.length, 4);

    // Navigate step has url
    assert.equal(recording.steps[0].action, 'navigate');
    assert.ok(recording.steps[0].url);

    // Click step has locators array
    assert.equal(recording.steps[1].action, 'click');
    assert.ok(Array.isArray(recording.steps[1].locators));
    assert.ok(recording.steps[1].locators.length > 0);

    // Type step has locators and value
    assert.equal(recording.steps[2].action, 'type');
    assert.equal(recording.steps[2].value, 'test@example.com');

    // Each locator has a strategy field
    for (const step of recording.steps) {
      if (step.locators) {
        for (const loc of step.locators) {
          assert.ok(loc.strategy, `Locator missing strategy in step: ${step.action}`);
        }
      }
    }
  });

  it('supports all action types', () => {
    const actions = ['navigate', 'click', 'type', 'select', 'keypress', 'scroll'];
    for (const action of actions) {
      assert.equal(typeof action, 'string');
    }
  });

  it('supports all locator strategies', () => {
    const strategies = ['role', 'text', 'selector', 'cssPath'];
    for (const s of strategies) {
      assert.equal(typeof s, 'string');
    }
  });
});

// ---------------------------------------------------------------------------
// executeStep -- navigate URL verification
// ---------------------------------------------------------------------------

function mockNavPage(actualUrl) {
  // Simulates a page that navigates but lands on a different URL (redirect)
  return {
    goto: async () => {},
    waitForLoadState: async () => {},
    evaluate: async () => actualUrl,
    url: () => actualUrl,
    getByRole: () => ({ first: () => ({ waitFor: async () => {}, click: async () => {} }) }),
    getByText: () => ({ first: () => ({ waitFor: async () => {}, click: async () => {} }) }),
    locator: () => ({ first: () => ({ waitFor: async () => {}, click: async () => {} }) }),
    keyboard: { press: async () => {} },
  };
}

describe('executeStep - navigate URL verification', () => {
  it('passes when actual URL pathname matches expected', async () => {
    const page = mockNavPage('https://en.wikipedia.org/wiki/Main_Page');
    const step = { action: 'navigate', url: 'https://en.wikipedia.org/wiki/Main_Page' };
    const result = await executeStep(page, step);
    assert.equal(result.status, 'pass');
  });

  it('fails when URL redirects to different pathname', async () => {
    const page = mockNavPage('https://www.example.com/Account/Login?ReturnUrl=%2Fprojects');
    const step = { action: 'navigate', url: 'https://www.example.com/projects' };
    const result = await executeStep(page, step);
    assert.equal(result.status, 'fail');
    assert.equal(result.fatal, true);
    assert.ok(result.error.includes('Redirected'));
    assert.ok(result.error.includes('/projects'));
    assert.ok(result.error.includes('/Account/Login'));
  });

  it('passes when pathnames match even with different query params', async () => {
    const page = mockNavPage('https://www.example.com/search?q=test&page=1');
    const step = { action: 'navigate', url: 'https://www.example.com/search?q=hello' };
    const result = await executeStep(page, step);
    assert.equal(result.status, 'pass');
  });

  it('sets fatal flag on navigate failure for stop-on-fail', async () => {
    const page = mockNavPage('https://www.example.com/login');
    const step = { action: 'navigate', url: 'https://www.example.com/dashboard' };
    const result = await executeStep(page, step);
    assert.equal(result.status, 'fail');
    assert.equal(result.fatal, true);
  });
});

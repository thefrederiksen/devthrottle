// CC Browser - Session Pipe Mode Unit Tests
// Tests setBrowserDirect, connectBrowser pipe mode, and disconnect handling

import { describe, it, beforeEach } from 'node:test';
import assert from 'node:assert/strict';

// ---------------------------------------------------------------------------
// Mock browser/context factories
// ---------------------------------------------------------------------------

function createMockPage() {
  return {
    evaluate: async () => {},
    title: async () => 'Mock Page',
    url: () => 'about:blank',
    on: () => {},
    context: () => createMockContext(),
  };
}

function createMockContext(pages = []) {
  const initScripts = [];
  return {
    pages: () => pages,
    addInitScript: async (script) => { initScripts.push(script); },
    on: () => {},
    newCDPSession: async () => ({
      send: async (method) => {
        if (method === 'Target.getTargetInfo') {
          return { targetInfo: { targetId: 'mock-target-1' } };
        }
        return {};
      },
      detach: async () => {},
    }),
    _initScripts: initScripts,
  };
}

function createMockBrowser(contexts = []) {
  const disconnectHandlers = [];
  return {
    contexts: () => contexts,
    isConnected: () => true,
    close: async () => {},
    on: (event, handler) => {
      if (event === 'disconnected') disconnectHandlers.push(handler);
    },
    _disconnectHandlers: disconnectHandlers,
    _fireDisconnect: () => {
      for (const h of disconnectHandlers) h();
    },
  };
}

// ---------------------------------------------------------------------------
// setBrowserDirect tests
// ---------------------------------------------------------------------------

describe('setBrowserDirect', () => {
  it('sets pipe cache with cdpUrl=pipe and browser reference', async () => {
    const { setBrowserDirect, getCachedBrowser } = await import('../../src/session.mjs');

    const mockCtx = createMockContext();
    const mockBrowser = createMockBrowser([mockCtx]);

    await setBrowserDirect(mockBrowser);

    const cached = getCachedBrowser();
    assert.ok(cached, 'Cache should be set');
    assert.equal(cached.cdpUrl, 'pipe', 'cdpUrl should be "pipe"');
    assert.equal(cached.browser, mockBrowser, 'Browser reference should match');
  });

  it('applies webdriver mask to all contexts', async () => {
    const { setBrowserDirect } = await import('../../src/session.mjs');

    const mockPage = createMockPage();
    const mockCtx = createMockContext([mockPage]);
    const mockBrowser = createMockBrowser([mockCtx]);

    await setBrowserDirect(mockBrowser);

    // addInitScript should have been called with the webdriver mask
    assert.ok(mockCtx._initScripts.length > 0, 'Init scripts should be applied');
    const hasWebdriverMask = mockCtx._initScripts.some(
      (s) => typeof s === 'string' && s.includes('webdriver')
    );
    assert.ok(hasWebdriverMask, 'Webdriver mask should be among init scripts');
  });
});

// ---------------------------------------------------------------------------
// connectBrowser pipe mode tests
// ---------------------------------------------------------------------------

describe('connectBrowser (pipe mode)', () => {
  it('reuses pipe cache and skips CDP URL connection', async () => {
    const { setBrowserDirect, connectBrowser, getCachedBrowser } = await import('../../src/session.mjs');

    const mockCtx = createMockContext();
    const mockBrowser = createMockBrowser([mockCtx]);

    await setBrowserDirect(mockBrowser);

    // connectBrowser with any cdpUrl should reuse the pipe-cached browser
    const result = await connectBrowser('http://127.0.0.1:9999');

    assert.equal(result.cdpUrl, 'pipe', 'Should return pipe cache, not connect via CDP');
    assert.equal(result.browser, mockBrowser, 'Should return same browser instance');
  });

  it('clears pipe cache and falls through when browser throws', async () => {
    const { connectBrowser, getCachedBrowser } = await import('../../src/session.mjs');

    // Manually set a broken pipe cache by importing internal setter
    // We simulate a dead pipe browser that throws on .contexts()
    const deadBrowser = {
      contexts: () => { throw new Error('Browser has been closed'); },
      isConnected: () => false,
      on: () => {},
    };

    // Use setBrowserDirect to set the cache, then break it
    const { setBrowserDirect } = await import('../../src/session.mjs');
    // First set a valid browser
    const mockCtx = createMockContext();
    const mockBrowser = createMockBrowser([mockCtx]);
    await setBrowserDirect(mockBrowser);

    // Now manually corrupt the cache to simulate stale pipe
    const cached = getCachedBrowser();
    if (cached) {
      cached.browser = deadBrowser;
    }

    // connectBrowser should detect the dead pipe and clear cache
    // It will then try CDP which will also fail (nothing on port 19994)
    try {
      await connectBrowser('http://127.0.0.1:19994');
    } catch {
      // Expected: CDP connection also fails
    }

    // The pipe cache should have been cleared
    const afterCache = getCachedBrowser();
    assert.ok(!afterCache || afterCache.cdpUrl !== 'pipe', 'Pipe cache should be cleared on dead browser');
  });
});

// ---------------------------------------------------------------------------
// Disconnected event tests
// ---------------------------------------------------------------------------

describe('browser disconnected event', () => {
  it('clears cache when browser fires disconnected', async () => {
    const { setBrowserDirect, getCachedBrowser } = await import('../../src/session.mjs');

    const mockCtx = createMockContext();
    const mockBrowser = createMockBrowser([mockCtx]);

    await setBrowserDirect(mockBrowser);
    assert.ok(getCachedBrowser(), 'Cache should be set');

    // Fire the disconnected event
    mockBrowser._fireDisconnect();

    assert.equal(getCachedBrowser(), null, 'Cache should be cleared on disconnect');
  });
});

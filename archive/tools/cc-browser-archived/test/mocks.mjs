// CC Browser - Mock Playwright Objects for Unit Testing
// Records method calls with timestamps for verifying delay injection

export class MockLocator {
  constructor(name = 'locator') {
    this._name = name;
    this._calls = [];
    this._boundingBox = { x: 100, y: 100, width: 80, height: 30 };
  }

  _record(method, args = []) {
    this._calls.push({ method, args, timestamp: Date.now() });
  }

  async click(opts = {}) {
    this._record('click', [opts]);
  }

  async dblclick(opts = {}) {
    this._record('dblclick', [opts]);
  }

  async fill(text, opts = {}) {
    this._record('fill', [text, opts]);
  }

  async type(text, opts = {}) {
    this._record('type', [text, opts]);
  }

  async hover(opts = {}) {
    this._record('hover', [opts]);
  }

  async dragTo(target, opts = {}) {
    this._record('dragTo', [target, opts]);
  }

  async boundingBox() {
    this._record('boundingBox');
    return { ...this._boundingBox };
  }

  async scrollIntoViewIfNeeded(opts = {}) {
    this._record('scrollIntoViewIfNeeded', [opts]);
  }

  async selectOption(values, opts = {}) {
    this._record('selectOption', [values, opts]);
  }

  async setChecked(checked, opts = {}) {
    this._record('setChecked', [checked, opts]);
  }

  async press(key, opts = {}) {
    this._record('press', [key, opts]);
  }

  async highlight() {
    this._record('highlight');
  }

  async screenshot(opts = {}) {
    this._record('screenshot', [opts]);
    return Buffer.from('fake-screenshot');
  }

  async setInputFiles(paths) {
    this._record('setInputFiles', [paths]);
  }

  async elementHandle() {
    return { evaluate: async () => {} };
  }

  async waitFor(opts = {}) {
    this._record('waitFor', [opts]);
  }

  first() {
    return this;
  }

  nth(n) {
    return this;
  }

  get calls() {
    return this._calls;
  }
}

export class MockPage {
  constructor() {
    this._calls = [];
    this._url = 'about:blank';
    this._title = 'Mock Page';
    this._locators = new Map();
    this._evaluateResult = undefined;
    this._screenshotBuffer = Buffer.from('fake-page-screenshot');

    this.mouse = {
      _calls: [],
      move: async (x, y, opts) => {
        const entry = { method: 'mouse.move', args: [x, y, opts], timestamp: Date.now() };
        this.mouse._calls.push(entry);
        this._calls.push(entry);
      },
      down: async (opts) => {
        const entry = { method: 'mouse.down', args: [opts], timestamp: Date.now() };
        this.mouse._calls.push(entry);
        this._calls.push(entry);
      },
      up: async (opts) => {
        const entry = { method: 'mouse.up', args: [opts], timestamp: Date.now() };
        this.mouse._calls.push(entry);
        this._calls.push(entry);
      },
      click: async (x, y, opts) => {
        const entry = { method: 'mouse.click', args: [x, y, opts], timestamp: Date.now() };
        this.mouse._calls.push(entry);
        this._calls.push(entry);
      },
      wheel: async (dx, dy) => {
        const entry = { method: 'mouse.wheel', args: [dx, dy], timestamp: Date.now() };
        this.mouse._calls.push(entry);
        this._calls.push(entry);
      },
    };

    this.keyboard = {
      _calls: [],
      press: async (key, opts) => {
        const entry = { method: 'keyboard.press', args: [key, opts], timestamp: Date.now() };
        this.keyboard._calls.push(entry);
        this._calls.push(entry);
      },
      type: async (text, opts) => {
        const entry = { method: 'keyboard.type', args: [text, opts], timestamp: Date.now() };
        this.keyboard._calls.push(entry);
        this._calls.push(entry);
      },
    };
  }

  _record(method, args = []) {
    this._calls.push({ method, args, timestamp: Date.now() });
  }

  async goto(url, opts = {}) {
    this._record('goto', [url, opts]);
    this._url = url;
  }

  async reload(opts = {}) {
    this._record('reload', [opts]);
  }

  async goBack(opts = {}) {
    this._record('goBack', [opts]);
  }

  async goForward(opts = {}) {
    this._record('goForward', [opts]);
  }

  url() {
    return this._url;
  }

  async title() {
    return this._title;
  }

  locator(selector) {
    if (!this._locators.has(selector)) {
      this._locators.set(selector, new MockLocator(selector));
    }
    return this._locators.get(selector);
  }

  getByRole(role, opts = {}) {
    const key = `role:${role}:${opts.name || ''}`;
    if (!this._locators.has(key)) {
      this._locators.set(key, new MockLocator(key));
    }
    return this._locators.get(key);
  }

  getByText(text, opts = {}) {
    const key = opts.exact ? `text-exact:${text}` : `text:${text}`;
    if (!this._locators.has(key)) {
      this._locators.set(key, new MockLocator(key));
    }
    return this._locators.get(key);
  }

  frameLocator(selector) {
    return this;
  }

  async evaluate(fn, ...args) {
    this._record('evaluate', [fn, ...args]);
    return this._evaluateResult;
  }

  async screenshot(opts = {}) {
    this._record('screenshot', [opts]);
    return this._screenshotBuffer;
  }

  async setViewportSize(size) {
    this._record('setViewportSize', [size]);
  }

  async waitForTimeout(ms) {
    this._record('waitForTimeout', [ms]);
  }

  async waitForURL(url, opts = {}) {
    this._record('waitForURL', [url, opts]);
  }

  async waitForLoadState(state, opts = {}) {
    this._record('waitForLoadState', [state, opts]);
  }

  async waitForFunction(fn, opts = {}) {
    this._record('waitForFunction', [fn, opts]);
  }

  async bringToFront() {
    this._record('bringToFront');
  }

  async close() {
    this._record('close');
  }

  on(event, handler) {
    // No-op for mock
  }

  context() {
    return {
      newCDPSession: async () => ({
        send: async () => ({ targetInfo: { targetId: 'mock-target' } }),
        detach: async () => {},
      }),
      pages: () => [this],
      addInitScript: async () => {},
    };
  }

  get calls() {
    return this._calls;
  }
}

/**
 * Get the time in ms between two recorded calls by index.
 * @param {Array} calls - Array of recorded call objects
 * @param {number} i - First call index
 * @param {number} j - Second call index
 * @returns {number} Time difference in ms
 */
export function timeBetween(calls, i, j) {
  if (i < 0 || j < 0 || i >= calls.length || j >= calls.length) {
    throw new Error(`Call index out of range: ${i}, ${j} (length: ${calls.length})`);
  }
  return Math.abs(calls[j].timestamp - calls[i].timestamp);
}

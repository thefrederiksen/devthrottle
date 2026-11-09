// Unit tests for vision.mjs with mocked child_process.execFile
import { describe, it, beforeEach, afterEach, mock } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import childProcess from 'node:child_process';

describe('analyzeScreenshot', () => {
  let analyzeScreenshot;
  let originalExecFile;
  const fakeBase64 = Buffer.from('fake-png-data').toString('base64');

  beforeEach(async () => {
    // Save original and replace with mock
    originalExecFile = childProcess.execFile;
    // Dynamic import to get fresh module
    const mod = await import('../../src/vision.mjs');
    analyzeScreenshot = mod.analyzeScreenshot;
  });

  afterEach(() => {
    // Restore original execFile
    childProcess.execFile = originalExecFile;
  });

  function mockExecFile(impl) {
    childProcess.execFile = impl;
  }

  it('returns text from successful CLI response', async () => {
    mockExecFile((cmd, args, opts, cb) => {
      cb(null, '{"detected": true, "type": "recaptcha_v2"}', '');
    });

    const result = await analyzeScreenshot(fakeBase64, 'test prompt');
    assert.equal(result, '{"detected": true, "type": "recaptcha_v2"}');
  });

  it('invokes claude with correct flags', async () => {
    let capturedArgs;
    mockExecFile((cmd, args, opts, cb) => {
      capturedArgs = { cmd, args, opts };
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'test prompt');

    assert.equal(capturedArgs.cmd, 'claude');

    const args = capturedArgs.args;
    assert.ok(args.includes('-p'), 'should include -p flag');
    assert.ok(args.includes('--tools'), 'should include --tools flag');
    assert.ok(args.includes('Read'), 'should include Read tool');
    assert.ok(args.includes('--dangerously-skip-permissions'), 'should skip permissions');
    assert.ok(args.includes('--max-turns'), 'should include --max-turns');
    assert.ok(args.includes('2'), 'should set max-turns to 2');
    assert.ok(args.includes('--output-format'), 'should include --output-format');
    assert.ok(args.includes('text'), 'should use text output format');
    assert.ok(args.includes('--no-session-persistence'), 'should not persist session');
  });

  it('defaults to haiku model', async () => {
    let capturedArgs;
    mockExecFile((cmd, args, opts, cb) => {
      capturedArgs = args;
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'test prompt');

    const modelIdx = capturedArgs.indexOf('--model');
    assert.ok(modelIdx >= 0, 'should include --model flag');
    assert.equal(capturedArgs[modelIdx + 1], 'haiku');
  });

  it('passes custom model parameter', async () => {
    let capturedArgs;
    mockExecFile((cmd, args, opts, cb) => {
      capturedArgs = args;
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'test prompt', { model: 'sonnet' });

    const modelIdx = capturedArgs.indexOf('--model');
    assert.equal(capturedArgs[modelIdx + 1], 'sonnet');
  });

  it('removes CLAUDECODE from child env', async () => {
    process.env.CLAUDECODE = 'true';
    let capturedOpts;
    mockExecFile((cmd, args, opts, cb) => {
      capturedOpts = opts;
      cb(null, 'ok', '');
    });

    try {
      await analyzeScreenshot(fakeBase64, 'test prompt');
      assert.equal(capturedOpts.env.CLAUDECODE, undefined, 'CLAUDECODE should be removed from child env');
    } finally {
      delete process.env.CLAUDECODE;
    }
  });

  it('throws on CLI error', async () => {
    mockExecFile((cmd, args, opts, cb) => {
      cb(new Error('spawn claude ENOENT'), '', 'command not found');
    });

    await assert.rejects(
      () => analyzeScreenshot(fakeBase64, 'test prompt'),
      (err) => {
        assert.ok(err.message.includes('Claude CLI error'));
        return true;
      }
    );
  });

  it('cleans up temp file after success', async () => {
    let capturedPrompt;
    mockExecFile((cmd, args, opts, cb) => {
      capturedPrompt = args[1]; // -p value
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'test prompt');

    // Extract temp path from the prompt
    const match = capturedPrompt.match(/cc-browser-captcha-\d+\.png/);
    assert.ok(match, 'prompt should reference temp file');
    const tempPath = join(tmpdir(), match[0]);
    assert.ok(!existsSync(tempPath), 'temp file should be deleted after call');
  });

  it('cleans up temp file after error', async () => {
    let capturedPrompt;
    mockExecFile((cmd, args, opts, cb) => {
      capturedPrompt = args[1];
      cb(new Error('fail'), '', '');
    });

    try {
      await analyzeScreenshot(fakeBase64, 'test prompt');
    } catch {
      // expected
    }

    const match = capturedPrompt.match(/cc-browser-captcha-\d+\.png/);
    assert.ok(match, 'prompt should reference temp file');
    const tempPath = join(tmpdir(), match[0]);
    assert.ok(!existsSync(tempPath), 'temp file should be deleted even after error');
  });

  it('includes prompt text in CLI invocation', async () => {
    let capturedPrompt;
    mockExecFile((cmd, args, opts, cb) => {
      capturedPrompt = args[1]; // -p value
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'Look for CAPTCHAs on this page');

    assert.ok(capturedPrompt.includes('Look for CAPTCHAs on this page'), 'should include original prompt');
    assert.ok(capturedPrompt.includes('Read the screenshot image at'), 'should instruct to read image');
  });

  it('sets timeout on child process', async () => {
    let capturedOpts;
    mockExecFile((cmd, args, opts, cb) => {
      capturedOpts = opts;
      cb(null, 'ok', '');
    });

    await analyzeScreenshot(fakeBase64, 'test prompt');
    assert.equal(capturedOpts.timeout, 60000, 'should set 60s timeout');
  });
});

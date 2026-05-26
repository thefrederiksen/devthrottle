/*
 * Node unit test for the browser dictation no-loss guarantee.
 *
 * It loads the REAL shipped dictate-client.js (the file embedded into the
 * Control API and served at /dictate-client.js) with a tiny `window` shim, then
 * exercises the pure capture-first frame router it exposes as
 * window.ccDictate._createCaptureBuffer. No browser, no mic, no socket - so the
 * ordering/no-loss invariant is provable deterministically.
 *
 * This is the browser twin of the C# DictationPipelineTests. Same guarantee:
 * frames captured before the server is ready are buffered and delivered IN
 * ORDER once it is, and nothing is dropped.
 *
 * Run:  node docs/features/dictation/browser/dictate-capture-first.test.mjs
 * Exit: 0 = all pass, 1 = a test failed.
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const clientPath = path.resolve(here, '..', '..', '..', '..',
  'src', 'CcDirector.ControlApi', 'Web', 'dictate-client.js');

const src = fs.readFileSync(clientPath, 'utf8');

// Load the IIFE with `window` as a parameter so its `window.ccDictate = ...`
// assignment lands on our shim. document/navigator are only touched inside
// start(), which we never call.
const windowShim = {};
new Function('window', src)(windowShim);

const createCaptureBuffer = windowShim.ccDictate && windowShim.ccDictate._createCaptureBuffer;

let failures = 0;
function check(name, cond) {
  if (cond) {
    console.log('  PASS - ' + name);
  } else {
    console.log('  FAIL - ' + name);
    failures++;
  }
}

console.log('dictate-client.js capture-first router');

check('exposes _createCaptureBuffer', typeof createCaptureBuffer === 'function');
if (typeof createCaptureBuffer !== 'function') {
  console.log('FATAL: router not exposed; cannot continue');
  process.exit(1);
}

// 1. Frames captured before ready are buffered, not sent.
{
  const cb = createCaptureBuffer();
  const sent = [];
  const send = f => sent.push(f);
  cb.push('a', send);
  cb.push('b', send);
  check('pre-ready frames are buffered, not sent', sent.length === 0 && cb.pendingCount === 2);
  check('buffer is not ready before markReady', cb.ready === false);
}

// 2. markReady flushes buffered frames IN ORDER, then goes live.
{
  const cb = createCaptureBuffer();
  const sent = [];
  const send = f => sent.push(f);
  cb.push('a', send);
  cb.push('b', send);
  cb.markReady(send);
  check('markReady flushed all buffered frames', sent.length === 2);
  check('flush preserved capture order', sent[0] === 'a' && sent[1] === 'b');
  check('buffer reports ready after markReady', cb.ready === true && cb.pendingCount === 0);
}

// 3. After ready, frames stream straight through (no buffering).
{
  const cb = createCaptureBuffer();
  const sent = [];
  const send = f => sent.push(f);
  cb.markReady(send);          // ready with nothing buffered
  cb.push('x', send);
  cb.push('y', send);
  check('post-ready frames sent immediately', sent.length === 2 && sent[0] === 'x' && sent[1] === 'y');
  check('post-ready frames are not buffered', cb.pendingCount === 0);
}

// 4. The real-world sequence: talk during connect, link comes up, keep talking.
//    Final delivered order must equal capture order with zero loss.
{
  const cb = createCaptureBuffer();
  const sent = [];
  const send = f => sent.push(f);
  cb.push('A', send);          // spoken during connect
  cb.push('B', send);
  cb.markReady(send);          // server 'started'
  cb.push('C', send);          // spoken live
  check('end-to-end order is exactly capture order', sent.join('') === 'ABC');
  check('end-to-end: nothing dropped', sent.length === 3);
}

// 5. markReady with an empty buffer sends nothing.
{
  const cb = createCaptureBuffer();
  const sent = [];
  cb.markReady(f => sent.push(f));
  check('empty markReady is a no-op', sent.length === 0 && cb.ready === true);
}

// 6. Large burst captured entirely before ready preserves order on flush.
{
  const cb = createCaptureBuffer();
  const sent = [];
  const send = f => sent.push(f);
  const N = 500;
  for (let i = 0; i < N; i++) cb.push(i, send);
  check('large pre-ready burst fully buffered', sent.length === 0 && cb.pendingCount === N);
  cb.markReady(send);
  let ordered = sent.length === N;
  for (let i = 0; i < N && ordered; i++) ordered = sent[i] === i;
  check('large burst flushed in exact order, no loss', ordered);
}

console.log(failures === 0
  ? 'RESULT: PASS (all router tests green)'
  : ('RESULT: FAIL (' + failures + ' failing)'));
process.exit(failures === 0 ? 0 : 1);

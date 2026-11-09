// Unit tests for human-mode delay engine
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

import {
  sleep,
  randomInt,
  gaussianRandom,
  navigationDelay,
  preClickDelay,
  preTypeDelay,
  interKeyDelay,
  preScrollDelay,
  postLoadDelay,
  idleDelay,
  humanMousePath,
  clickOffset,
  typingDelays,
  humanDragPath,
} from '../../src/human-mode.mjs';

// ---------------------------------------------------------------------------
// Helper: run a function N times and collect results
// ---------------------------------------------------------------------------
function collect(fn, n = 100) {
  const results = [];
  for (let i = 0; i < n; i++) results.push(fn());
  return results;
}

function assertRange(values, min, max, label) {
  for (const v of values) {
    assert.ok(v >= min && v <= max, `${label}: ${v} not in [${min}, ${max}]`);
  }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('randomInt', () => {
  it('returns values in [min, max] inclusive', () => {
    const values = collect(() => randomInt(5, 10));
    assertRange(values, 5, 10, 'randomInt(5,10)');
  });

  it('includes both min and max values', () => {
    const values = collect(() => randomInt(0, 1), 200);
    assert.ok(values.includes(0), 'Should include min value');
    assert.ok(values.includes(1), 'Should include max value');
  });
});

describe('gaussianRandom', () => {
  it('produces values centered around mean', () => {
    const values = collect(() => gaussianRandom(100, 20), 1000);
    const avg = values.reduce((a, b) => a + b, 0) / values.length;
    // With 1000 samples, mean should be within ~5 of target
    assert.ok(Math.abs(avg - 100) < 10, `Average ${avg} should be near 100`);
  });

  it('produces variable values (not all identical)', () => {
    const values = collect(() => gaussianRandom(100, 20), 10);
    const unique = new Set(values);
    assert.ok(unique.size > 1, 'Should produce varied values');
  });
});

describe('delay generators', () => {
  it('navigationDelay returns 800-2500ms', () => {
    assertRange(collect(navigationDelay), 800, 2500, 'navigationDelay');
  });

  it('preClickDelay returns 100-400ms', () => {
    assertRange(collect(preClickDelay), 100, 400, 'preClickDelay');
  });

  it('preTypeDelay returns 200-600ms', () => {
    assertRange(collect(preTypeDelay), 200, 600, 'preTypeDelay');
  });

  it('interKeyDelay returns 30-250ms (clamped gaussian)', () => {
    assertRange(collect(interKeyDelay), 30, 250, 'interKeyDelay');
  });

  it('preScrollDelay returns 500-1500ms', () => {
    assertRange(collect(preScrollDelay), 500, 1500, 'preScrollDelay');
  });

  it('postLoadDelay returns 1000-3000ms', () => {
    assertRange(collect(postLoadDelay), 1000, 3000, 'postLoadDelay');
  });

  it('idleDelay returns 1000-4000ms', () => {
    assertRange(collect(idleDelay), 1000, 4000, 'idleDelay');
  });
});

describe('humanMousePath', () => {
  it('start and end points match inputs', () => {
    const path = humanMousePath(10, 20, 300, 400);
    assert.equal(path[0].x, 10);
    assert.equal(path[0].y, 20);
    assert.equal(path[path.length - 1].x, 300);
    assert.equal(path[path.length - 1].y, 400);
  });

  it('returns multiple points (not just start/end)', () => {
    const path = humanMousePath(0, 0, 500, 500);
    assert.ok(path.length >= 10, `Path should have >=10 points, got ${path.length}`);
  });

  it('path is not a straight line', () => {
    // For a large distance, at least one intermediate point should deviate
    // from the straight line between start and end
    const path = humanMousePath(0, 0, 400, 0);
    // All points on a straight line from (0,0) to (400,0) would have y=0
    const hasDeviation = path.some((p, i) => i > 0 && i < path.length - 1 && p.y !== 0);
    // Note: due to rounding, very small deviations might round to 0.
    // Run this 10 times and at least one should deviate
    let foundDeviation = hasDeviation;
    for (let trial = 0; trial < 10 && !foundDeviation; trial++) {
      const p = humanMousePath(0, 0, 400, 0);
      foundDeviation = p.some((pt, i) => i > 0 && i < p.length - 1 && pt.y !== 0);
    }
    assert.ok(foundDeviation, 'Path should not be a straight line (at least one trial should deviate)');
  });

  it('handles short distances gracefully', () => {
    const path = humanMousePath(100, 100, 102, 101);
    assert.ok(path.length >= 2, 'Should have at least start and end');
  });

  it('handles zero distance', () => {
    const path = humanMousePath(50, 50, 50, 50);
    assert.ok(path.length >= 2, 'Should have at least start and end for zero distance');
  });
});

describe('clickOffset', () => {
  it('returns values within default maxPx bounds', () => {
    for (let i = 0; i < 100; i++) {
      const offset = clickOffset();
      assert.ok(Math.abs(offset.x) <= 3, `x offset ${offset.x} exceeds 3px`);
      assert.ok(Math.abs(offset.y) <= 3, `y offset ${offset.y} exceeds 3px`);
    }
  });

  it('returns values within custom maxPx bounds', () => {
    for (let i = 0; i < 100; i++) {
      const offset = clickOffset(10);
      assert.ok(Math.abs(offset.x) <= 10, `x offset ${offset.x} exceeds 10px`);
      assert.ok(Math.abs(offset.y) <= 10, `y offset ${offset.y} exceeds 10px`);
    }
  });

  it('returns {x, y} object', () => {
    const offset = clickOffset();
    assert.ok(typeof offset.x === 'number');
    assert.ok(typeof offset.y === 'number');
  });
});

describe('typingDelays', () => {
  it('returns array length matching text length', () => {
    const delays = typingDelays('hello');
    assert.equal(delays.length, 5);
  });

  it('returns array of numbers', () => {
    const delays = typingDelays('test');
    for (const d of delays) {
      assert.ok(typeof d === 'number', `Delay should be number, got ${typeof d}`);
    }
  });

  it('values vary (not all identical)', () => {
    const delays = typingDelays('abcdefghijklmnop');
    const unique = new Set(delays);
    assert.ok(unique.size > 1, 'Delays should vary between keystrokes');
  });

  it('values are in interKeyDelay range (30-250)', () => {
    const delays = typingDelays('abcdefghij');
    assertRange(delays, 30, 250, 'typingDelays');
  });

  it('returns empty array for empty string', () => {
    const delays = typingDelays('');
    assert.equal(delays.length, 0);
  });
});

describe('humanDragPath', () => {
  it('includes overshoot and correction', () => {
    const path = humanDragPath(0, 100, 300, 100);
    // Last point should be the target (correction)
    const lastPoint = path[path.length - 1];
    assert.equal(lastPoint.x, 300);
    assert.equal(lastPoint.y, 100);

    // Second-to-last should be the overshoot (past 300)
    const overshoot = path[path.length - 2];
    assert.ok(overshoot.x > 300, `Overshoot x ${overshoot.x} should be past target 300`);
  });

  it('includes per-step delays', () => {
    const path = humanDragPath(0, 0, 200, 0);
    for (const point of path) {
      assert.ok(typeof point.delay === 'number', 'Each point should have a delay');
      assert.ok(point.delay > 0, 'Delay should be positive');
    }
  });
});

describe('sleep', () => {
  it('resolves after approximately the specified duration', async () => {
    const start = Date.now();
    await sleep(50);
    const elapsed = Date.now() - start;
    assert.ok(elapsed >= 40, `Sleep should take at least 40ms, took ${elapsed}ms`);
    assert.ok(elapsed < 200, `Sleep should not take more than 200ms, took ${elapsed}ms`);
  });

  it('handles zero ms', async () => {
    const start = Date.now();
    await sleep(0);
    const elapsed = Date.now() - start;
    assert.ok(elapsed < 100, 'sleep(0) should resolve quickly');
  });
});

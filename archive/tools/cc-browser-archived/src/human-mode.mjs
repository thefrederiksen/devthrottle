// CC Browser - Human Mode Delay Engine
// Pure utility functions for human-like browsing behavior.
// All functions are stateless and return promises or values.

// ---------------------------------------------------------------------------
// Core timing
// ---------------------------------------------------------------------------

/**
 * Promise-based delay.
 * @param {number} ms - Milliseconds to wait
 */
export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, Math.max(0, Math.floor(ms))));
}

/**
 * Uniform random integer in [min, max] inclusive.
 */
export function randomInt(min, max) {
  min = Math.ceil(min);
  max = Math.floor(max);
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

/**
 * Gaussian random using Box-Muller transform.
 * @param {number} mean
 * @param {number} stddev
 * @returns {number}
 */
export function gaussianRandom(mean, stddev) {
  let u = 0;
  let v = 0;
  while (u === 0) u = Math.random();
  while (v === 0) v = Math.random();
  const z = Math.sqrt(-2.0 * Math.log(u)) * Math.cos(2.0 * Math.PI * v);
  return mean + z * stddev;
}

// ---------------------------------------------------------------------------
// Delay generators (return ms values)
// ---------------------------------------------------------------------------

/** Navigation delay: 800-2500ms */
export function navigationDelay() {
  return randomInt(800, 2500);
}

/** Pre-click delay: 100-400ms */
export function preClickDelay() {
  return randomInt(100, 400);
}

/** Pre-type delay: 200-600ms */
export function preTypeDelay() {
  return randomInt(200, 600);
}

/** Inter-key delay: gaussian(100, 40)ms, clamped 30-250ms */
export function interKeyDelay() {
  return Math.max(30, Math.min(250, Math.round(gaussianRandom(100, 40))));
}

/** Pre-scroll delay: 500-1500ms */
export function preScrollDelay() {
  return randomInt(500, 1500);
}

/** Post-load delay: 1000-3000ms */
export function postLoadDelay() {
  return randomInt(1000, 3000);
}

/** Idle delay: 1000-4000ms */
export function idleDelay() {
  return randomInt(1000, 4000);
}

// ---------------------------------------------------------------------------
// Mouse path generation
// ---------------------------------------------------------------------------

/**
 * Generate a human-like mouse path using cubic Bezier curve.
 * Two random control points offset from the straight line by +-30% of distance.
 * Sampled at ~20 points. Produces natural-looking arcs, not straight lines.
 *
 * @param {number} startX
 * @param {number} startY
 * @param {number} endX
 * @param {number} endY
 * @returns {Array<{x: number, y: number}>} Path points
 */
export function humanMousePath(startX, startY, endX, endY) {
  const dx = endX - startX;
  const dy = endY - startY;
  const distance = Math.sqrt(dx * dx + dy * dy);

  // For very short distances, just return start and end
  if (distance < 5) {
    return [{ x: startX, y: startY }, { x: endX, y: endY }];
  }

  const offset = distance * 0.3;

  // Normal vector perpendicular to the line
  const nx = -dy / distance;
  const ny = dx / distance;

  // Control point 1: ~1/3 along the line with random perpendicular offset
  const cp1x = startX + dx * 0.33 + nx * (Math.random() - 0.5) * 2 * offset;
  const cp1y = startY + dy * 0.33 + ny * (Math.random() - 0.5) * 2 * offset;

  // Control point 2: ~2/3 along the line with random perpendicular offset
  const cp2x = startX + dx * 0.67 + nx * (Math.random() - 0.5) * 2 * offset;
  const cp2y = startY + dy * 0.67 + ny * (Math.random() - 0.5) * 2 * offset;

  // Sample the cubic Bezier curve at ~20 points
  const steps = Math.max(10, Math.min(30, Math.round(distance / 15)));
  const points = [];

  for (let i = 0; i <= steps; i++) {
    const t = i / steps;
    const t2 = t * t;
    const t3 = t2 * t;
    const mt = 1 - t;
    const mt2 = mt * mt;
    const mt3 = mt2 * mt;

    const x = mt3 * startX + 3 * mt2 * t * cp1x + 3 * mt * t2 * cp2x + t3 * endX;
    const y = mt3 * startY + 3 * mt2 * t * cp1y + 3 * mt * t2 * cp2y + t3 * endY;

    points.push({ x: Math.round(x), y: Math.round(y) });
  }

  return points;
}

/**
 * Random offset from element center for click position.
 * @param {number} [maxPx=3] - Maximum offset in pixels
 * @returns {{x: number, y: number}} Offset values
 */
export function clickOffset(maxPx = 3) {
  return {
    x: (Math.random() - 0.5) * 2 * maxPx,
    y: (Math.random() - 0.5) * 2 * maxPx,
  };
}

// ---------------------------------------------------------------------------
// Typing simulation
// ---------------------------------------------------------------------------

/**
 * Generate per-character delay array with gaussian variation.
 * @param {string} text
 * @returns {number[]} Array of delays in ms, one per character
 */
export function typingDelays(text) {
  const delays = [];
  for (let i = 0; i < text.length; i++) {
    delays.push(interKeyDelay());
  }
  return delays;
}

// ---------------------------------------------------------------------------
// Human drag path (for sliders)
// ---------------------------------------------------------------------------

/**
 * Generate a human-like drag path with y-wobble, overshoot, and correction.
 * Designed for slider CAPTCHAs.
 *
 * @param {number} startX
 * @param {number} startY
 * @param {number} endX
 * @param {number} endY
 * @returns {Array<{x: number, y: number, delay: number}>} Path points with per-step delay
 */
export function humanDragPath(startX, startY, endX, endY) {
  const basePath = humanMousePath(startX, startY, endX, endY);
  const points = [];

  // Add y-wobble (+-2px) and timing to each step
  for (let i = 0; i < basePath.length; i++) {
    const wobbleY = i === 0 || i === basePath.length - 1 ? 0 : (Math.random() - 0.5) * 4;
    points.push({
      x: basePath[i].x,
      y: Math.round(basePath[i].y + wobbleY),
      delay: randomInt(10, 30),
    });
  }

  // Overshoot: go past the target by 5-15px
  const overshootPx = randomInt(5, 15);
  const dx = endX - startX;
  const direction = dx >= 0 ? 1 : -1;
  points.push({
    x: endX + direction * overshootPx,
    y: endY + (Math.random() - 0.5) * 2,
    delay: randomInt(30, 60),
  });

  // Correction: move back to the target
  points.push({
    x: endX,
    y: endY,
    delay: randomInt(50, 120),
  });

  return points;
}

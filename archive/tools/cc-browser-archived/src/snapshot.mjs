// CC Browser - Page Snapshots
// Adapted from: OpenClaw src/browser/pw-role-snapshot.ts and pw-tools-core.snapshot.ts
// Generates role-based element refs from accessibility tree

import {
  ensurePageState,
  getPageForTargetId,
  storeRoleRefsForTarget,
  restoreRoleRefsForTarget,
  refLocator,
} from './session.mjs';

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const INTERACTIVE_ROLES = new Set([
  'button',
  'link',
  'textbox',
  'checkbox',
  'radio',
  'combobox',
  'listbox',
  'menuitem',
  'menuitemcheckbox',
  'menuitemradio',
  'option',
  'searchbox',
  'slider',
  'spinbutton',
  'switch',
  'tab',
  'treeitem',
]);

const CONTENT_ROLES = new Set([
  'heading',
  'cell',
  'gridcell',
  'columnheader',
  'rowheader',
  'listitem',
  'article',
  'region',
  'main',
  'navigation',
]);

const STRUCTURAL_ROLES = new Set([
  'generic',
  'group',
  'list',
  'table',
  'row',
  'rowgroup',
  'grid',
  'treegrid',
  'menu',
  'menubar',
  'toolbar',
  'tablist',
  'tree',
  'directory',
  'document',
  'application',
  'presentation',
  'none',
]);

// ---------------------------------------------------------------------------
// Role Name Tracking
// ---------------------------------------------------------------------------

function createRoleNameTracker() {
  const counts = new Map();
  const refsByKey = new Map();

  return {
    counts,
    refsByKey,

    getKey(role, name) {
      return `${role}:${name ?? ''}`;
    },

    getNextIndex(role, name) {
      const key = this.getKey(role, name);
      const current = counts.get(key) ?? 0;
      counts.set(key, current + 1);
      return current;
    },

    trackRef(role, name, ref) {
      const key = this.getKey(role, name);
      const list = refsByKey.get(key) ?? [];
      list.push(ref);
      refsByKey.set(key, list);
    },

    getDuplicateKeys() {
      const out = new Set();
      for (const [key, refs] of refsByKey) {
        if (refs.length > 1) {
          out.add(key);
        }
      }
      return out;
    },
  };
}

function removeNthFromNonDuplicates(refs, tracker) {
  const duplicates = tracker.getDuplicateKeys();
  for (const [ref, data] of Object.entries(refs)) {
    const key = tracker.getKey(data.role, data.name);
    if (!duplicates.has(key)) {
      delete refs[ref]?.nth;
    }
  }
}

// ---------------------------------------------------------------------------
// Snapshot Parsing
// ---------------------------------------------------------------------------

function getIndentLevel(line) {
  const match = line.match(/^(\s*)/);
  return match ? Math.floor(match[1].length / 2) : 0;
}

function compactTree(tree) {
  const lines = tree.split('\n');
  const result = [];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.includes('[ref=')) {
      result.push(line);
      continue;
    }
    if (line.includes(':') && !line.trimEnd().endsWith(':')) {
      result.push(line);
      continue;
    }

    const currentIndent = getIndentLevel(line);
    let hasRelevantChildren = false;
    for (let j = i + 1; j < lines.length; j++) {
      const childIndent = getIndentLevel(lines[j]);
      if (childIndent <= currentIndent) break;
      if (lines[j]?.includes('[ref=')) {
        hasRelevantChildren = true;
        break;
      }
    }
    if (hasRelevantChildren) {
      result.push(line);
    }
  }

  return result.join('\n');
}

/**
 * Build role snapshot from Playwright's ariaSnapshot output
 */
export function buildRoleSnapshotFromAriaSnapshot(ariaSnapshot, options = {}) {
  const lines = ariaSnapshot.split('\n');
  const refs = {};
  const tracker = createRoleNameTracker();

  let counter = 0;
  const nextRef = () => {
    counter += 1;
    return `e${counter}`;
  };

  if (options.interactive) {
    const result = [];
    for (const line of lines) {
      const depth = getIndentLevel(line);
      if (options.maxDepth !== undefined && depth > options.maxDepth) {
        continue;
      }

      const match = line.match(/^(\s*-\s*)(\w+)(?:\s+"([^"]*)")?(.*)$/);
      if (!match) continue;
      const [, , roleRaw, name, suffix] = match;
      if (roleRaw.startsWith('/')) continue;

      const role = roleRaw.toLowerCase();
      if (!INTERACTIVE_ROLES.has(role)) continue;

      const ref = nextRef();
      const nth = tracker.getNextIndex(role, name);
      tracker.trackRef(role, name, ref);
      refs[ref] = { role, name, nth };

      let enhanced = `- ${roleRaw}`;
      if (name) enhanced += ` "${name}"`;
      enhanced += ` [ref=${ref}]`;
      if (nth > 0) enhanced += ` [nth=${nth}]`;
      if (suffix.includes('[')) enhanced += suffix;
      result.push(enhanced);
    }

    removeNthFromNonDuplicates(refs, tracker);

    return {
      snapshot: result.join('\n') || '(no interactive elements)',
      refs,
    };
  }

  // Full snapshot
  const result = [];
  for (const line of lines) {
    const depth = getIndentLevel(line);
    if (options.maxDepth !== undefined && depth > options.maxDepth) {
      continue;
    }

    const match = line.match(/^(\s*-\s*)(\w+)(?:\s+"([^"]*)")?(.*)$/);
    if (!match) {
      if (!options.interactive) result.push(line);
      continue;
    }

    const [, prefix, roleRaw, name, suffix] = match;
    if (roleRaw.startsWith('/')) {
      if (!options.interactive) result.push(line);
      continue;
    }

    const role = roleRaw.toLowerCase();
    const isInteractive = INTERACTIVE_ROLES.has(role);
    const isContent = CONTENT_ROLES.has(role);
    const isStructural = STRUCTURAL_ROLES.has(role);

    if (options.compact && isStructural && !name) {
      continue;
    }

    const shouldHaveRef = isInteractive || (isContent && name);
    if (!shouldHaveRef) {
      result.push(line);
      continue;
    }

    const ref = nextRef();
    const nth = tracker.getNextIndex(role, name);
    tracker.trackRef(role, name, ref);
    refs[ref] = { role, name, nth };

    let enhanced = `${prefix}${roleRaw}`;
    if (name) enhanced += ` "${name}"`;
    enhanced += ` [ref=${ref}]`;
    if (nth > 0) enhanced += ` [nth=${nth}]`;
    if (suffix) enhanced += suffix;
    result.push(enhanced);
  }

  removeNthFromNonDuplicates(refs, tracker);

  const tree = result.join('\n') || '(empty)';
  return {
    snapshot: options.compact ? compactTree(tree) : tree,
    refs,
  };
}

// ---------------------------------------------------------------------------
// Snapshot Functions
// ---------------------------------------------------------------------------

export async function snapshotViaPlaywright(opts) {
  const { cdpUrl, targetId, interactive, compact, maxDepth, maxChars = 10000 } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  // Get Playwright's ariaSnapshot
  const ariaSnapshot = await page.locator('body').ariaSnapshot({ timeout: 10000 });

  // Parse into role refs
  const { snapshot, refs } = buildRoleSnapshotFromAriaSnapshot(ariaSnapshot, {
    interactive,
    compact,
    maxDepth,
  });

  // Store refs for later use
  storeRoleRefsForTarget({
    page,
    cdpUrl,
    targetId,
    refs,
    mode: 'role',
  });

  // Truncate if needed
  let finalSnapshot = snapshot;
  if (maxChars && snapshot.length > maxChars) {
    finalSnapshot = snapshot.slice(0, maxChars) + '\n... (truncated)';
  }

  return {
    snapshot: finalSnapshot,
    refs,
    stats: {
      chars: snapshot.length,
      lines: snapshot.split('\n').length,
      refs: Object.keys(refs).length,
      interactive: Object.values(refs).filter((r) => INTERACTIVE_ROLES.has(r.role)).length,
    },
  };
}

/**
 * Get page info: URL, title, viewport
 */
export async function getPageInfoViaPlaywright(opts) {
  const { cdpUrl, targetId } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  const viewport = page.viewportSize();

  return {
    url: page.url(),
    title: await page.title().catch(() => ''),
    viewport: viewport || { width: 0, height: 0 },
  };
}

/**
 * Get page text content
 */
export async function getTextContentViaPlaywright(opts) {
  const { cdpUrl, targetId, selector } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  if (selector) {
    const locator = page.locator(selector).first();
    return await locator.textContent();
  }

  return await page.locator('body').textContent();
}

/**
 * Get page HTML
 */
export async function getHtmlViaPlaywright(opts) {
  const { cdpUrl, targetId, selector, outer } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);

  if (selector) {
    const locator = page.locator(selector).first();
    if (outer) {
      return await locator.evaluate((el) => el.outerHTML);
    }
    return await locator.innerHTML();
  }

  const locator = page.locator('body');
  if (outer) {
    return await page.content();
  }
  return await locator.innerHTML();
}

/**
 * Screenshot with element labels overlaid
 */
export async function screenshotWithLabelsViaPlaywright(opts) {
  const { cdpUrl, targetId, maxLabels = 150, type = 'png' } = opts;
  const page = await getPageForTargetId({ cdpUrl, targetId });
  ensurePageState(page);
  restoreRoleRefsForTarget({ cdpUrl, targetId, page });

  // Get current refs
  const state = page._ccState || {};
  const refs = state.roleRefs || {};

  const viewport = await page.evaluate(() => ({
    scrollX: window.scrollX || 0,
    scrollY: window.scrollY || 0,
    width: window.innerWidth || 0,
    height: window.innerHeight || 0,
  }));

  const refKeys = Object.keys(refs);
  const boxes = [];
  let skipped = 0;

  for (const ref of refKeys) {
    if (boxes.length >= maxLabels) {
      skipped += 1;
      continue;
    }
    try {
      const box = await refLocator(page, ref).boundingBox();
      if (!box) {
        skipped += 1;
        continue;
      }
      // Check if in viewport
      const x0 = box.x;
      const y0 = box.y;
      const x1 = box.x + box.width;
      const y1 = box.y + box.height;
      const vx0 = viewport.scrollX;
      const vy0 = viewport.scrollY;
      const vx1 = viewport.scrollX + viewport.width;
      const vy1 = viewport.scrollY + viewport.height;
      if (x1 < vx0 || x0 > vx1 || y1 < vy0 || y0 > vy1) {
        skipped += 1;
        continue;
      }
      boxes.push({
        ref,
        x: x0 - viewport.scrollX,
        y: y0 - viewport.scrollY,
        w: Math.max(1, box.width),
        h: Math.max(1, box.height),
      });
    } catch {
      skipped += 1;
    }
  }

  try {
    if (boxes.length > 0) {
      await page.evaluate((labels) => {
        // Remove existing labels
        const existing = document.querySelectorAll('[data-cc-labels]');
        existing.forEach((el) => el.remove());

        const root = document.createElement('div');
        root.setAttribute('data-cc-labels', '1');
        root.style.position = 'fixed';
        root.style.left = '0';
        root.style.top = '0';
        root.style.zIndex = '2147483647';
        root.style.pointerEvents = 'none';
        root.style.fontFamily =
          '"SF Mono","SFMono-Regular",Menlo,Monaco,Consolas,"Liberation Mono","Courier New",monospace';

        const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

        for (const label of labels) {
          const box = document.createElement('div');
          box.setAttribute('data-cc-labels', '1');
          box.style.position = 'absolute';
          box.style.left = `${label.x}px`;
          box.style.top = `${label.y}px`;
          box.style.width = `${label.w}px`;
          box.style.height = `${label.h}px`;
          box.style.border = '2px solid #ffb020';
          box.style.boxSizing = 'border-box';

          const tag = document.createElement('div');
          tag.setAttribute('data-cc-labels', '1');
          tag.textContent = label.ref;
          tag.style.position = 'absolute';
          tag.style.left = `${label.x}px`;
          tag.style.top = `${clamp(label.y - 18, 0, 20000)}px`;
          tag.style.background = '#ffb020';
          tag.style.color = '#1a1a1a';
          tag.style.fontSize = '12px';
          tag.style.lineHeight = '14px';
          tag.style.padding = '1px 4px';
          tag.style.borderRadius = '3px';
          tag.style.boxShadow = '0 1px 2px rgba(0,0,0,0.35)';
          tag.style.whiteSpace = 'nowrap';

          root.appendChild(box);
          root.appendChild(tag);
        }

        document.documentElement.appendChild(root);
      }, boxes);
    }

    const buffer = await page.screenshot({ type });
    return { buffer, labels: boxes.length, skipped };
  } finally {
    await page
      .evaluate(() => {
        const existing = document.querySelectorAll('[data-cc-labels]');
        existing.forEach((el) => el.remove());
      })
      .catch(() => {});
  }
}

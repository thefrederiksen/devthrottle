# cc-browser Feature Plan

**Date:** 2026-02-27
**Status:** Draft - Pending Review

---

## Overview

This document outlines four feature enhancements for cc-browser identified during real-world usage. These features address gaps in the current CLI that make certain workflows difficult or impossible.

**Current Architecture Summary:**
- CLI client (`cli.mjs`) sends HTTP requests to a persistent daemon (`daemon.mjs`)
- Daemon manages a Playwright-core connection to Chrome/Edge via CDP
- Element references (e1, e2, etc.) generated from ARIA accessibility tree snapshots
- 27+ commands defined as flat async handler functions in `cli.mjs`
- Custom argument parser (no CLI framework) in `cli.mjs` lines 133-160

---

## Feature 1: Screenshot --save Flag

### Problem

The `screenshot` command only returns base64-encoded image data in JSON. There is no way to save directly to a PNG/JPEG file on disk. This makes it very hard to verify visual state -- users must manually decode base64 or pipe output through external tools.

**Current behavior:**
```bash
cc-browser screenshot
# Returns: { "success": true, "screenshot": "iVBORw0KGgo...", "type": "png" }
```

### Proposed Solution

Add a `--save <filepath>` flag to the screenshot command that writes the decoded image directly to disk.

**New behavior:**
```bash
cc-browser screenshot --save ./page.png
# Returns: { "success": true, "saved": "D:\\path\\to\\page.png", "size": 145230 }

cc-browser screenshot --ref e5 --save ./element.png
# Returns: { "success": true, "saved": "D:\\path\\to\\element.png", "size": 23400 }

cc-browser screenshot --save ./page.jpg --type jpeg
# Returns: { "success": true, "saved": "D:\\path\\to\\page.jpg", "size": 89100 }
```

### Implementation Details

**Files to modify:**

1. **`src/cli.mjs`** - Screenshot command handler (lines 671-682)
   - Add `save` to the args passed to the daemon request
   - After receiving the base64 response, decode and write to disk client-side
   - Return a simplified JSON response with file path and size instead of base64
   - If `--save` not provided, behavior remains unchanged (returns base64 JSON)

2. **`src/cli.mjs`** - Help text (lines 210-344)
   - Add `--save <path>` to the screenshot section documentation

**Implementation approach - Client-side file write:**

The save should happen in the CLI client, not the daemon. This keeps the daemon stateless and avoids path resolution issues across the HTTP boundary.

```javascript
// In cli.mjs screenshot handler (pseudocode)
screenshot: async (args) => {
  const result = await request('POST', '/screenshot', {
    ref: args.ref,
    element: args.element,
    fullPage: args.fullPage,
    type: args.type,
    tab: args.tab,
  }, port);

  if (args.save && result.screenshot) {
    const buf = Buffer.from(result.screenshot, 'base64');
    const absPath = path.resolve(args.save);
    fs.writeFileSync(absPath, buf);
    output({ success: true, saved: absPath, size: buf.length });
  } else {
    output(result);
  }
}
```

**Edge cases to handle:**
- Auto-append file extension if missing (based on `--type` or default png)
- Create parent directories if they don't exist (`fs.mkdirSync` with recursive)
- Clear error if write fails (permissions, invalid path)

**Estimated complexity:** Low. Isolated change to one command handler in `cli.mjs`.

---

## Feature 2: Click by Text Content

### Problem

The `click` command only accepts `--ref`, which requires elements to appear in the ARIA accessibility tree snapshot. Many UI elements -- especially file names in tree views, table cells, list items, and plain text nodes -- appear as text without interactive roles and therefore have no ref assigned. This is blocking for workflows where clickable text elements are the only way to interact.

**Current behavior:**
```bash
cc-browser click --ref e5    # Works - if the element has a ref
cc-browser click --text "CaseAttributes.sql"   # Does not exist
```

The snapshot assigns refs primarily to interactive roles (button, link, textbox, checkbox, combobox, etc.) and content roles (heading, article, region). Plain `<span>` or `<div>` text that happens to be clickable (via JavaScript event handlers or parent click delegation) gets no ref.

### Proposed Solution

Add two new flags to the click command:

1. **`--text <string>`** - Click the first visible element containing exact text
2. **`--selector <css>`** - Click element matching a CSS selector (power-user escape hatch)

**New behavior:**
```bash
cc-browser click --text "CaseAttributes.sql"
# Finds and clicks the first visible element whose text content matches

cc-browser click --text "CaseAttributes.sql" --exact
# Strict: element textContent must exactly equal the string (not contain it)

cc-browser click --selector "[data-file='CaseAttributes.sql']"
# CSS selector based click for advanced cases
```

### Implementation Details

**Files to modify:**

1. **`src/cli.mjs`** - Click command handler (lines 545-557)
   - Add `text`, `exact`, and `selector` to the args passed to daemon
   - Validation: exactly one of `--ref`, `--text`, or `--selector` must be provided

2. **`src/main.mjs`** - Click route handler (lines 397-411)
   - Pass new params through to interactions module

3. **`src/interactions.mjs`** - `click()` function (lines 161-202)
   - Add text-based and selector-based element resolution branches
   - Before the existing ref resolution logic, check for `text` or `selector`
   - Use Playwright's built-in locator strategies:

```javascript
// Text-based click (pseudocode for interactions.mjs)
async function click(page, opts) {
  let locator;

  if (opts.ref) {
    locator = refLocator(page, opts.ref);  // existing behavior
  } else if (opts.text) {
    if (opts.exact) {
      locator = page.getByText(opts.text, { exact: true });
    } else {
      locator = page.getByText(opts.text);
    }
  } else if (opts.selector) {
    locator = page.locator(opts.selector);
  } else {
    throw new Error('One of --ref, --text, or --selector is required');
  }

  // Rest of click logic (human mode delays, double-click, modifiers, etc.)
  // remains the same...
}
```

4. **`src/cli.mjs`** - Help text
   - Document new flags in the INTERACTIONS section

**Playwright locator strategies available:**
- `page.getByText(text)` - matches elements containing the text (default: substring match)
- `page.getByText(text, { exact: true })` - matches elements with exact text content
- `page.locator(css)` - raw CSS selector
- `page.getByRole(role, { name })` - ARIA role match (already used by --ref)

**Additional consideration -- extend to other interaction commands:**

The `--text` and `--selector` flags should also be added to these commands that currently only accept `--ref`:
- `hover` (lines 558-567)
- `type` (lines 568-580)
- `fill` (lines 638-648)
- `info` (lines 505-521)
- `drag` (lines 595-615) - for `--from` and `--to`

This should be a follow-up task after the click implementation is validated, using the same locator resolution pattern extracted into a shared helper function.

**Estimated complexity:** Medium. Changes span 3 files but the core logic leverages existing Playwright locator APIs.

---

## Feature 3: Per-Command Help

### Problem

Running `cc-browser click --help` or `cc-browser help click` does not show usage information for the specific command. The only help available is the full consolidated help text (130+ lines), which requires scrolling to find the relevant section. This makes it hard to discover available flags for specific commands.

**Current behavior:**
```bash
cc-browser help           # Prints all 130+ lines of help
cc-browser click --help   # "Unknown command: click" (--help consumed as flag)
cc-browser help click     # "Unknown command: help" (help doesn't take args)
```

### Proposed Solution

Add per-command help that shows usage, flags, and examples for individual commands.

**New behavior:**
```bash
cc-browser click --help
# Usage: cc-browser click [options]
#
# Click an element on the page.
#
# Options:
#   --ref <ref>         Element reference from snapshot (e.g., e1)
#   --text <string>     Click element containing this text
#   --selector <css>    Click element matching CSS selector
#   --exact             With --text, require exact match
#   --double            Double-click instead of single click
#   --button <btn>      Mouse button: left, right, middle (default: left)
#   --modifiers <json>  Keyboard modifiers: ["Control", "Shift", "Alt"]
#   --timeout <ms>      Wait timeout (default: 8000, range: 500-60000)
#   --tab <id>          Target tab ID
#
# Examples:
#   cc-browser click --ref e5
#   cc-browser click --text "Submit"
#   cc-browser click --ref e3 --double --modifiers '["Control"]'

cc-browser help click     # Same output as above
```

### Implementation Details

**Files to modify:**

1. **`src/cli.mjs`** - Command metadata structure

   Replace the flat `commands` object with a richer structure that includes metadata:

   ```javascript
   const commandDefs = {
     click: {
       handler: async (args) => { /* existing handler */ },
       usage: 'cc-browser click [options]',
       description: 'Click an element on the page.',
       options: [
         { flag: '--ref <ref>', desc: 'Element reference from snapshot (e.g., e1)' },
         { flag: '--text <string>', desc: 'Click element containing this text' },
         // ...
       ],
       examples: [
         'cc-browser click --ref e5',
         'cc-browser click --text "Submit"',
       ],
     },
     // ... other commands
   };
   ```

2. **`src/cli.mjs`** - Argument parser (lines 133-160)
   - Detect `--help` flag in any command's args
   - If `--help` present, print that command's help and exit instead of executing

3. **`src/cli.mjs`** - Help command (lines 208-344)
   - If `help` command receives a positional arg (e.g., `help click`), print that command's help
   - If no arg, print the full help text as before

4. **`src/cli.mjs`** - Help formatter function
   - New function `formatCommandHelp(name, def)` that renders usage, description, options table, and examples in a consistent format

**Approach options:**

**Option A: Metadata object (recommended)**
- Define per-command metadata alongside handlers
- Single source of truth for both execution and documentation
- Keeps full help text as fallback for `cc-browser help` with no args
- More code upfront but self-documenting

**Option B: Static help strings**
- Define per-command help as raw strings
- Less structured but faster to implement
- Risk of help text drifting from actual implementation

Recommend Option A for maintainability.

**Estimated complexity:** Medium-High. Requires restructuring the command registration pattern, but no changes to daemon or browser interaction logic.

---

## Feature 4: JavaScript Execution Command

### Current State -- ALREADY EXISTS

The `evaluate` command already provides full JavaScript execution capability. This was listed as a missing feature, but it exists and works.

**Current commands:**
```bash
# Execute JavaScript in page context
cc-browser evaluate --js "document.title"
cc-browser evaluate --js "document.querySelectorAll('button').length"

# Execute JavaScript in the context of a specific element
cc-browser evaluate --ref e1 --js "el => el.textContent"
cc-browser evaluate --ref e1 --js "el => el.getBoundingClientRect()"
```

**Implementation (interactions.mjs lines 464-506):**
- Accepts `--js`, `--fn`, or `--code` flags (all aliases)
- Wraps code in `Function` constructor with `"use strict"`
- Executes via `page.evaluate()` or `locator.evaluate()` (if --ref provided)
- Returns the result as JSON

### Recommended Improvements

While the command exists, its discoverability is low. Recommended enhancements:

1. **Add to prominent position in help text** - Currently buried in the help output
2. **Add --async flag** - Currently only synchronous evaluation is supported. Adding async support would enable `await fetch()` and other async patterns:
   ```bash
   cc-browser evaluate --js "await fetch('/api/data').then(r => r.json())" --async
   ```
3. **Add --file flag** - Execute JavaScript from a file:
   ```bash
   cc-browser evaluate --file ./scrape-data.js
   ```

---

## Implementation Priority & Order

| Priority | Feature | Complexity | Blocking? |
|----------|---------|-----------|-----------|
| 1 | Click by text (Feature 2) | Medium | YES - blocking current workflow |
| 2 | Screenshot --save (Feature 1) | Low | No - workaround exists (manual decode) |
| 3 | Per-command help (Feature 3) | Medium-High | No - quality of life |
| 4 | Evaluate improvements (Feature 4) | Low | No - command already works |

### Suggested Implementation Order

**Phase 1 - Unblock immediately (Feature 2: Click by text)**
1. Add `--text` and `--selector` to click command in `interactions.mjs`
2. Update CLI handler in `cli.mjs` to pass new args
3. Update daemon route in `main.mjs` to forward new params
4. Test with the CaseAttributes.sql scenario
5. Extract shared locator resolution helper for reuse

**Phase 2 - Screenshot quality of life (Feature 1: --save)**
1. Add save logic to screenshot handler in `cli.mjs`
2. Handle path resolution, directory creation, extension defaults
3. Update help text

**Phase 3 - Developer experience (Feature 3: Per-command help)**
1. Define command metadata structure
2. Migrate all command handlers to new structure
3. Add `--help` detection to argument parser
4. Add `help <command>` argument handling
5. Format and display per-command help

**Phase 4 - Evaluate enhancements (Feature 4)**
1. Add `--async` support to evaluate command
2. Add `--file` support to evaluate command
3. Improve help documentation for evaluate

---

## Files Impact Summary

| File | Feature 1 | Feature 2 | Feature 3 | Feature 4 |
|------|-----------|-----------|-----------|-----------|
| `src/cli.mjs` | Modify | Modify | Major refactor | Modify |
| `src/main.mjs` | -- | Modify | -- | Modify |
| `src/interactions.mjs` | -- | Modify | -- | Modify |
| `src/snapshot.mjs` | -- | -- | -- | -- |

---

## Testing Strategy

Each feature should include:

1. **Manual testing** against a real browser session with representative pages
2. **Edge case testing:**
   - Feature 1: Invalid paths, missing directories, disk full, permission denied
   - Feature 2: Multiple text matches, no matches, hidden elements, iframes
   - Feature 3: Unknown commands, combined flags (`--help --ref e1`)
   - Feature 4: Async errors, infinite loops, large return values
3. **Regression testing:** Existing commands must continue to work unchanged

---

## Open Questions

1. **Feature 2 - Multiple matches:** When `--text` matches multiple elements, should we click the first visible one, or return an error asking the user to be more specific? **Recommendation:** Click the first visible one, but include a `matchCount` in the response so users know if the match was ambiguous.

2. **Feature 2 - Scope to other commands:** Should `--text` and `--selector` be added to `hover`, `type`, `fill`, `info`, and `drag` in the same PR, or follow-up? **Recommendation:** Follow-up PR to keep the initial change focused.

3. **Feature 3 - Help source of truth:** Should per-command help replace the consolidated help entirely, or supplement it? **Recommendation:** Supplement -- keep `cc-browser help` as the full reference, add per-command help as focused quick reference.

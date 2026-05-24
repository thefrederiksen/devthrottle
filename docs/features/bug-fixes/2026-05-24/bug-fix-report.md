# Bug Fix Session - 2026-05-24

Reviewed the latest open GitHub issues, fixed the clear/straightforward ones, and
verified the rest. One genuine code fix was needed and made; the other concrete
candidates turned out to be already resolved in the current code.

---

## FIXED: #106 - cc-powerpoint drops numbered-list body

**Status: Fixed, rebuilt, deployed, and verified.**

### The bug

In `cc-powerpoint from-markdown`, a slide using a numbered list (`1. 2. 3.`)
produced a slide with only the title - the entire body was missing. Bullet lists
(`- `) worked fine.

### Root cause

`tools/cc-powerpoint/src/parser.py` -> `_extract_bullets()` only tracked
`bullet_list_open` / `bullet_list_close` tokens from the markdown-it token stream.
Numbered lists emit `ordered_list_open` / `ordered_list_close`, which were ignored,
so `list_depth` never rose and no body items were collected.

### The fix

```python
if token.type in ("bullet_list_open", "ordered_list_open"):
    list_depth += 1
elif token.type in ("bullet_list_close", "ordered_list_close"):
    list_depth -= 1
```

Added two regression tests (`test_numbered_list`, `test_numbered_sub_bullets`) to
`tests/test_parser.py`. All 19 parser tests pass.

### Before vs after (same input markdown)

Input:
```
# Numbered List Slide

1. First item
2. Second item
3. Third item
```

| Build | Slide body text |
|-------|-----------------|
| Shipped exe (before) | `['Numbered List Slide']` - body empty |
| Fixed + deployed exe  | `['Numbered List Slide', 'First item', 'Second item', 'Third item']` |

### Verification screenshot

Rendered the generated slide with PowerPoint. The body now shows all three items:

![Numbered list slide now renders body](bug106-numbered-list-fixed.png)

The fix was rebuilt via `build.ps1` and deployed to
`%LOCALAPPDATA%\cc-director\bin\cc-powerpoint.exe`; the deployed exe was
re-tested and produces the full body.

---

## VERIFIED ALREADY RESOLVED (no code change needed)

### #116 - cc-outlook calendar events `new_query()` error

The reported error (`ApiComponent.new_query() takes 1 positional argument but 2
were given`) no longer reproduces. The current `get_events()` passes `datetime`
objects to the O365 library, which handles them directly without building a
`new_query`. Ran `cc-outlook calendar events --days 14` against the live account
(both source and the shipped exe): a full event list returned cleanly, no error.
Recommend closing.

### #120 - cc-browser: no JS eval / screenshot fails

The cc-browser v2 rewrite already ships both a JS-eval command
(`evaluate --fn "() => ..."`) and a `screenshot` command. The reporter tried the
non-existent names `eval` / `execute`; the actual command is `evaluate`. The core
"no way to run JavaScript" complaint is resolved. Recommend closing (or narrowing
to just the GPU "image readback" case if it still recurs).

---

## VERIFIED FIXED BY RECENT COMMITS (latest issues)

- **#134** (voice/dictation clips speech at start) - addressed by commit
  `27e2c44` (show Initializing until capture is live).
- **#133** (Dictate meter too small / unresponsive) - addressed by commit
  `1fec455`. Confirmed in `dictate.html`: meter height raised to 92px, `gain`
  2.2, and a `sqrt` response curve on the amplitude. Code matches the issue's
  requested fix direction.

---

## SKIPPED (not straightforward / not clear)

- **#129** - Wingman reliable session-state detection: foundational + explicitly
  "untested"; a research/hardening effort, not a discrete bug.
- **#130** - Voice Mode for in-car session interaction: a design doc / future
  feature ("Not building this now").
- **#131** - Wingman terminal-state misclassification: an auto-created tracking
  issue collecting correction votes, not a single fix.
- **#121** - Terminal background-color leak on initial render: explicitly a
  recurring bug that has "been fixed multiple times and keeps coming back" with
  root cause for the initial-render case "not yet identified." Not straightforward
  and not reliably testable from within a nested-ConPty session.

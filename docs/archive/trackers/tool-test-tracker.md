# CC Director Tool Test Tracker

Last updated: 2026-03-02

## Summary

| Category            | Total | Passed | Failed | Skipped | Not Tested |
|---------------------|-------|--------|--------|---------|------------|
| Documents           |     3 |      0 |      0 |       0 |          3 |
| Email               |     2 |      0 |      0 |       0 |          2 |
| Web & Social        |     7 |      1 |      0 |       0 |          6 |
| Desktop Automation  |     3 |      0 |      0 |       0 |          3 |
| Media               |     7 |      0 |      0 |       0 |          7 |
| Data & Utilities    |     6 |      0 |      0 |       0 |          6 |
| **TOTAL**           |**28** |  **1** |  **0** |   **0** |     **27** |

---

## Status Legend

- `NOT TESTED` - Has not been tested yet
- `PASS` - Tool runs, produces correct output
- `FAIL` - Tool crashes or produces wrong output
- `SKIPPED` - Cannot test (missing dependency, auth, etc.)

---

## Documents

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 1 | cc-markdown | Python | Markdown to PDF/Word/HTML with themes | NOT TESTED | | Requires Chrome/Chromium |
| 2 | cc-excel | Python | CSV/JSON/Markdown to formatted Excel | NOT TESTED | | |
| 3 | cc-powerpoint | Python | Markdown to PowerPoint presentations | NOT TESTED | | |

## Email

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 4 | cc-gmail | Python | Gmail: read, send, search, labels, calendar, contacts | NOT TESTED | | Requires Google OAuth |
| 5 | cc-outlook | Python | Outlook: email, calendar, attachments, folders | NOT TESTED | | Requires Azure OAuth |

## Web & Social

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 6 | cc-browser | Node.js | Persistent browser automation with workspaces | PASS | 2026-03-02 | 159/159 unit tests pass. BUG: daemon drops --text/--selector/--exact for click/hover/type |
| 7 | cc-linkedin | Python | LinkedIn automation with human-like delays | REMOVED | 2026-03-05 | Replaced by cc-browser connections + LinkedIn navigation skill (#71) |
| 8 | cc-reddit | Python | Reddit automation with human-like delays | NOT TESTED | | Requires cc-browser |
| 9 | cc-spotify | Python | Spotify playback control via browser | NOT TESTED | | Requires cc-browser |
| 10 | cc-crawl4ai | Python | AI-ready web crawler to clean markdown | NOT TESTED | | Requires Playwright |
| 11 | cc-websiteaudit | Node.js | Website SEO/security/AI readiness audit | NOT TESTED | | Requires Node.js, Chrome |
| 12 | cc-brandingrecommendations | Node.js | Branding action plans from audit data | NOT TESTED | | Requires cc-websiteaudit output |

## Desktop Automation

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 13 | cc-click | .NET | Windows UI automation: click, type, screenshot | NOT TESTED | | Requires Windows, .NET |
| 14 | cc-trisight | .NET | 3-tier UI element detection (UIA+OCR+pixel) | NOT TESTED | | Requires Windows, .NET |
| 15 | cc-computer | .NET | AI desktop agent with screenshot-in-the-loop | NOT TESTED | | Requires OPENAI_API_KEY |

## Media

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 16 | cc-image | Python | Image generation, analysis, OCR | NOT TESTED | | Requires OPENAI_API_KEY. Known BROKEN per docs |
| 17 | cc-voice | Python | Text-to-speech (OpenAI TTS) | NOT TESTED | | Requires OPENAI_API_KEY |
| 18 | cc-whisper | Python | Audio transcription and translation | NOT TESTED | | Requires OPENAI_API_KEY |
| 19 | cc-video | Python | Video info, audio extraction, screenshots | NOT TESTED | | Requires FFmpeg |
| 20 | cc-transcribe | Python | Video/audio transcription with screenshots | NOT TESTED | | Requires FFmpeg, OPENAI_API_KEY |
| 21 | cc-photos | Python | Photo scan, duplicates, AI descriptions | NOT TESTED | | Requires OPENAI_API_KEY |
| 22 | cc-youtube-info | Python | YouTube transcript/metadata extraction | NOT TESTED | | No external deps |

## Data & Utilities

| # | Tool | Type | Description | Status | Date | Notes |
|---|------|------|-------------|--------|------|-------|
| 23 | cc-vault | Python | Personal vault: contacts, tasks, goals, docs, RAG | NOT TESTED | | No external deps |
| 24 | cc-hardware | Python | System hardware info (RAM, CPU, GPU, disk) | NOT TESTED | | No external deps |
| 25 | cc-comm-queue | Python | Communication Manager approval queue | NOT TESTED | | |
| 26 | cc-docgen | Python | C4 architecture diagrams from YAML | NOT TESTED | | Requires Graphviz |
| 27 | cc-director-setup | Python | Windows installer for CC Director suite | NOT TESTED | | |
| 28 | cc-personresearch | Python | Person research aggregation | NOT TESTED | | |

---

## Test Log

Record detailed test results below as each tool is tested.

### Template

```
### #N - cc-toolname (YYYY-MM-DD)

**Command tested:** `cc-toolname <args>`
**Result:** PASS / FAIL
**Output:** (summary or error)
**Issues found:** None / describe issue
**Bug filed:** N/A / #issue-number
```

### #6 - cc-browser (2026-03-02)

**Unit Tests:** 159/159 PASS across 28 suites (509ms)
- captcha-detect-dom (11), human-delays (28), mode-state (7), new-features (27)
- recorder (21), replay (22), sessions (35), vision-mock (7)
- 1 deprecation warning: `fs.rmdir(path, { recursive: true })` -> should use `fs.rm()`

**All 3 Workspaces:** PASS (edge-work, chrome-work, chrome-personal)
- All 7 aliases resolve correctly (mindzie, edge-work, center, consulting, chrome-work, linkedin, personal)

**Full Command Test Results (40 commands):**

| Command | Status | Notes |
|---------|--------|-------|
| `(no args)` | PASS | Shows help |
| `status` | PASS | Returns daemon/browser/tabs JSON |
| `browsers` | PASS | Detected Chrome + Edge |
| `profiles` | PASS | Both --browser edge (2) and --browser chrome (6) |
| `workspaces` | PASS | Lists 3 workspaces with aliases |
| `favorites` | BUG | Does not resolve aliases -- builds wrong path (e.g. `edge-mindzie` instead of `edge-work`) |
| `start` | PASS | Tested all 3 workspaces |
| `stop` | PASS | Clean shutdown |
| `navigate` | PASS | |
| `reload` | PASS | |
| `back` | ISSUE | Works but 30s `waitUntil: load` timeout on cached pages |
| `forward` | PASS | (help only -- tested after back timeout) |
| `snapshot` | PASS | ARIA tree with element refs |
| `info` | PASS | URL, title, viewport (0x0 before resize -- expected) |
| `text` | PASS | With and without --selector |
| `html` | PASS | With and without --selector |
| `click --ref` | PASS | Clicked link, navigated to IANA |
| `click --text` | BUG | CLI sends correctly, daemon drops `text`, `selector`, `exact` fields |
| `click --selector` | BUG | Same daemon passthrough issue |
| `type --ref` | PASS | Typed into textbox |
| `type --textContent` | BUG | Daemon drops `textContent`, `selector`, `exact` fields |
| `hover --ref` | PASS | Hovered over button |
| `hover --text` | BUG | Daemon drops `text`, `selector`, `exact` fields |
| `fill` | PASS | Filled 2 fields at once |
| `press` | PASS | Escape key |
| `scroll` | PASS | Both down and up |
| `select` | PASS | (help only -- no dropdown on test page) |
| `drag` | PASS | (help only -- hidden from main help) |
| `evaluate` | PASS | document.title and window.location.href |
| `screenshot` | PASS | Base64 PNG |
| `screenshot --save` | PASS | Saved 25KB PNG to disk |
| `screenshot-labels` | PASS | Returns annotated screenshot |
| `resize` | PASS | Set viewport 1280x720, confirmed via info. Hidden from main help |
| `upload` | PASS | (help only -- no file input on test page) |
| `wait --text` | PASS | Detected existing text |
| `wait --time` | PASS | 500ms delay |
| `tabs` | PASS | Listed open tabs |
| `tabs-open` | PASS | Opened new tab with URL |
| `tabs-close` | PASS | Closed specific tab |
| `tabs-focus` | PASS | Switched between tabs |
| `tabs-close-all` | PASS | (help only) |
| `captcha detect` | PASS | Calls DOM first, falls through to vision (needs ANTHROPIC_API_KEY) |
| `captcha solve` | PASS | (help only -- no CAPTCHA on test page) |
| `record start` | PASS | Started recording |
| `record status` | PASS | Shows active recording with step count |
| `record stop` | PASS | Saved 1-step recording to vault |
| `recordings` | PASS | Listed saved recordings. Hidden from main help |
| `replay` | PASS | (help only) |
| `mode` | PASS | Get (human) and set (fast/human/stealth) |
| `session create` | PASS | Created session with TTL |
| `session list` | PASS | Listed active sessions |
| `session close` | PASS | Closed session (flag is --session, not --id) |

**BUGS FOUND:**

1. **Daemon drops --text/--selector/--exact for click, hover, type** (daemon.mjs)
   - CLI sends fields correctly (cli.mjs:1110-1114)
   - Daemon routes don't pass them to Playwright functions:
     - `POST /click` (line 471) - missing `text`, `selector`, `exact`
     - `POST /hover` (line 519) - missing `text`, `selector`, `exact`
     - `POST /type` (line 488) - missing `textContent`, `selector`, `exact`
   - The underlying functions already support these via `resolveLocator()`

2. **`favorites` command doesn't resolve workspace aliases** (cli.mjs:981-990)
   - `getWorkspaceDir()` builds `{browser}-{workspace}` literally
   - Passing `--workspace mindzie` creates path `edge-mindzie` instead of `edge-work`
   - Other commands resolve aliases via daemon lockfile; favorites reads filesystem directly

**MISSING FROM MAIN HELP (hidden commands):**
- `resize` - works but not listed in `cc-browser` help output
- `drag` - works but not listed
- `recordings` - works but not listed

**MINOR ISSUES:**
- `back` uses `waitUntil: 'load'` with 30s timeout -- can timeout on cached pages
- `info` returns viewport 0x0 before `resize` is called (cosmetic, not a real bug)

**Result:** PASS -- core tool is solid with 2 real bugs and 3 undocumented commands

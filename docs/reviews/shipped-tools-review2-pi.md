# Second shipped command-line tools review

Scope: only the shipped Python tools with `ship: true` in `tools/registry.json`: `cc-pdf`, `cc-html`, `cc-word`, `cc-gmail`, `cc-outlook`, `cc-playwright`, `cc-vault`, `cc-comm-queue`, and `cc-devthrottle`.

This was a review only. I did not change tool code, did not run installs, and did not run builds. I only wrote this report.

## Summary

Most round-one fixes are present in the current tree. The remaining issues I found are narrow. The highest risk is that `cc-vault` still has machine-readable output paths that can emit invalid JavaScript Object Notation or non-ASCII user data, which means the earlier output fix was not applied consistently to all shipped tools. I also found one catalog path that still hides root causes, and one agent discovery surface in `cc-devthrottle` that still appears stale.

## Findings

### 1. `cc-vault` still prints JavaScript Object Notation through Rich and can break parsing

Priority: High

Tools affected: `cc-vault`

What is wrong: Several `cc-vault` machine-readable modes still call `console.print(json.dumps(...))` instead of plain `print(json.dumps(...))`. Examples include `tools/cc-vault/src/cli.py:2107`, `2169`, `3406`, `3443`, `3477`, `3534`, `3586`, `3635`, `3642`, and `3668`. This is the same failure mode fixed elsewhere: Rich can wrap long string values at the console width, inserting line breaks inside a value and making the output invalid JavaScript Object Notation.

Why it matters: `cc-vault` stores long names, notes, relationship paths, file paths, and summaries. Those values can easily exceed the default console width. Agents and scripts requesting machine-readable output can receive text that no parser can load.

Suggested fix: Route every `cc-vault` machine-readable path through a shared helper that uses plain `print(json.dumps(..., ensure_ascii=True, default=str))` to standard output. Keep all human progress and warnings on standard error. Add a test that creates a long value, runs each `--json` or `--format json` command, and parses the result.

### 2. Some shipped machine-readable outputs still emit non-ASCII user data

Priority: Medium

Tools affected: `cc-vault`, `cc-gmail`, `cc-outlook`

What is wrong: Some JavaScript Object Notation paths still use `ensure_ascii=False`, and some write UTF-8 bytes directly. Examples: `tools/cc-vault/src/catalog.py:48`, `tools/cc-vault/src/cli.py:1540`, `2107`, and `2169`; `tools/cc-gmail/src/cli.py:1938`; `tools/cc-outlook/src/cli.py:1070`. `cc-vault` also forces standard output and standard error to UTF-8 in `tools/cc-vault/src/cli.py:8-13` specifically to allow non-ASCII names.

Why it matters: The house rule says shipped tool output must be ASCII-only. The source files now pass an ASCII scan, but runtime output can still contain non-ASCII contact names, file names, email display names, and catalog paths.

Suggested fix: For machine-readable output, use `ensure_ascii=True` everywhere. For human output, either escape or transliterate non-ASCII user data consistently. Add a runtime guard test, not just a source-file scan, for representative commands that print stored names and file paths.

### 3. `cc-vault catalog` still hides extraction and embedding failures

Priority: Medium

Tools affected: `cc-vault`

What is wrong: `CatalogScanner._extract_text` catches every exception and returns `None` (`tools/cc-vault/src/catalog.py:302-335`). The caller then records only `No text extracted` (`tools/cc-vault/src/catalog.py:237-249`), losing the real exception, such as a missing converter, unreadable file, corrupt document, or parsing failure. `_embed_summary` also catches every exception and silently ignores it (`tools/cc-vault/src/catalog.py:443-465`).

Why it matters: Catalog users can see a generic failure or a successful summary count while the search embedding failed. That violates the fail-clearly rule and makes repair difficult because the actionable cause is discarded.

Suggested fix: Let extraction exceptions bubble to the command boundary, or return a structured result with the exception type and message. Store the real extraction error on the catalog entry and include it in stream events. For embedding, record a warning status or warning field instead of silently passing.

### 4. `cc-devthrottle actions` still appears partly stale after the round-one fix pass

Priority: Low

Tools affected: `cc-devthrottle`

What is wrong: The hand-written `_ACTIONS` list still does not fully match the real command surface. `session-spawn` advertises only `cc-devthrottle session spawn <repo>` while the actual command also accepts `--agent`, `--prompt`, `--name`, `--type`, `--command`, and `--command-args` (`tools/cc-devthrottle/src/cli.py:83-88` and `325-347`). `message-ask` omits the real `--timeout-ms` option (`tools/cc-devthrottle/src/cli.py:101-109` and `358-365`). `schedule-create` still describes synthetic arguments `cron_or_at` and `seed_or_worklist` instead of the real separate options (`tools/cc-devthrottle/src/cli.py:176-190`).

Why it matters: `actions --json` is the discovery surface for agents. If it is stale, agents can build weak or invalid commands, or miss important options.

Suggested fix: Generate the actions list from the Typer command tree, or add a snapshot test that compares `_ACTIONS` against the real command definitions for command names, positional arguments, and options.

## Items checked and not re-reported

- `cc-outlook` now declares `msal` in `pyproject.toml`.
- `cc-comm-queue` now declares the distribution name as `cc-comm-queue` and includes `pydantic`.
- `cc-pdf` now applies page size and margin, finds more Chromium-family browsers, and uses a temporary browser profile.
- Document converters now have quiet and overwrite controls.
- `cc-gmail` no longer adds `Bcc` to Simple Mail Transfer Protocol message headers.
- `cc-outlook --html` now sets the body type.
- `cc-comm-queue` now uses plain `print(json.dumps(...))` for the paths I checked.

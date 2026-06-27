# Shipped cc-* Tools Review

Executive summary:
- Shipped set from `tools/registry.json`: `cc-pdf`, `cc-html`, `cc-word`, `cc-gmail`, `cc-outlook`, `cc-playwright`, `cc-vault`, `cc-comm-queue`, and `cc-devthrottle`.
- The largest release risk is packaging: the Python bundle resolves dependencies from each shipped tool's `pyproject.toml`, and `cc-outlook` imports `msal` without declaring it there.
- The ASCII-only output rule is not currently met. Rich tables/panels render Unicode box drawing, and several JSON paths deliberately preserve non-ASCII user/API content.
- Machine-readable output is inconsistent: some commands use `--json`, some use `--format json`, some have no machine mode, and several JSON modes go through Rich `console.print`.
- `cc-vault` and `cc-comm-queue` both have configuration/path behaviors that can tell users one thing while writing or reading another location.
- `cc-playwright` is useful but too brittle for a shipped browser tool: it assumes fixed Brave paths and gives uneven structured errors.
- `cc-word` is significantly more lossy than the PDF/HTML converters; common Markdown constructs are dropped when producing DOCX.
- The improvements below are concrete enough to file as GitHub issues and are ordered by product risk.

## High Priority

### 1. Add missing `msal` dependency to the shipped `cc-outlook` bundle

Priority: High

Tools affected: `cc-outlook`, release Python bundle

What is wrong: `tools/cc-outlook/src/auth.py` imports `msal` directly, and `tools/cc-outlook/requirements.txt` lists `msal>=1.20.0`, but `tools/cc-outlook/pyproject.toml` does not. The release bundle builder resolves shipped third-party dependencies from shipped tools' `pyproject.toml` files, not from `requirements.txt`.

Why it matters: a clean installed shared-venv bundle can ship without `msal`, causing `cc-outlook` to fail at import time before users can authenticate or run `--help`.

Suggested fix: add `msal>=1.20.0` to `tools/cc-outlook/pyproject.toml`. Add a release CI smoke test that builds/installs the Python bundle offline and runs `cc-<tool> --version` plus an import smoke for every script in `tools-manifest.json`.

### 2. Enforce ASCII-only output across all shipped tools

Priority: High

Tools affected: all Rich/Typer tools, especially `cc-vault`, `cc-gmail`, `cc-outlook`, `cc-comm-queue`, `cc-devthrottle`, `cc-pdf`, `cc-html`, `cc-word`

What is wrong: many commands use Rich `Table` and `Panel` with default boxes, which render Unicode line characters. Several paths also preserve non-ASCII content, for example `cc-vault` sets UTF-8 console output and uses `ensure_ascii=False`, and Gmail/Outlook recipient JSON writes UTF-8 bytes.

Why it matters: the house rule is ASCII-only output, and terminals/log parsers get a mixed contract today. This also makes copy/paste, CI snapshots, and agent parsing less predictable.

Suggested fix: create shared output helpers for shipped CLIs. Use ASCII table boxes (`box.ASCII` or plain column output), avoid `Panel` in CLI output, use `json.dump(..., ensure_ascii=True)` for machine output, and add a CI check that runs representative `--help`, `--version`, table, and JSON commands and fails if stdout/stderr contain bytes outside ASCII.

### 3. Make `cc-vault init <path>` actually initialize the requested path

Priority: High

Tools affected: `cc-vault`

What is wrong: `cc-vault init` accepts an optional `path` and prints that path, but it calls `ensure_directories()` and `db.init_db()` against paths resolved from `CcStorage.vault()`. The computed `vault_path = Path(path)` is not saved or used to build `DB_PATH`/`DOCUMENTS_PATH`.

Why it matters: users can run `cc-vault init D:\MyVault`, see success for that path, and still have the database/directories created under the default `%LOCALAPPDATA%\cc-director\vault`. That is data-location confusion in a personal-data tool.

Suggested fix: either remove the path argument or fully implement it. The better fix is to persist the selected path into the shared config (`vault.vault_path`) and make `cc-vault` resolve paths from the loaded config dynamically instead of module-level constants captured at import time. Add a test that initializes into a temp path and asserts `vault.db` is created there.

### 4. Fix `cc-comm-queue config set` writing to the wrong config file

Priority: High

Tools affected: `cc-comm-queue`

What is wrong: `config show` and normal queue operations read `cc_shared.config.get_config()`, which resolves to the centralized cc-director config path. `config set` writes `Path.home() / ".cc-director" / "config.json"` directly, a legacy location that the shipped Windows config reader does not use.

Why it matters: users can successfully run `cc-comm-queue config set queue_path ...`, then `config show` and queue operations continue using the old value. This makes setup and repair look broken.

Suggested fix: replace the direct JSON write in `config_set` with `CCDirectorConfig().load()`, update `comm_manager`, and call `save()`. Preserve unknown config keys through the shared config writer. Add a regression test that `config set` changes the same path returned by `cc-devthrottle settings path`.

## Medium Priority

### 5. Standardize machine-readable output and error behavior

Priority: Medium

Tools affected: `cc-devthrottle`, `cc-comm-queue`, `cc-vault`, `cc-gmail`, `cc-outlook`, document converters

What is wrong: JSON support is uneven. Examples: `cc-devthrottle schedule enable/disable/delete` have no `--json`, email recipient export uses `--format json` while account listing uses `--json`, document converters have no machine mode, and many JSON outputs are emitted through Rich `console.print` instead of plain stdout.

Why it matters: these tools are meant for agents and scripts. Inconsistent flags and Rich-rendered JSON force callers into per-command special cases and make failures harder to handle.

Suggested fix: adopt one convention: `--json` for structured output, plain stdout for JSON, stderr for human diagnostics, stable non-zero exit codes for errors. Keep `--format json` as a deprecated alias where it already exists. Add tests that parse JSON output for each shipped tool's primary list/show/status/add commands.

### 6. Broaden and harden `cc-playwright` browser discovery

Priority: Medium

Tools affected: `cc-playwright`

What is wrong: `_find_brave()` only checks two fixed Windows Brave install paths. The tool has no environment override, no `PATH` lookup, no Chrome/Edge fallback, and no macOS Brave/Chrome paths despite macOS being a secondary target. Several state-file errors are also swallowed and treated as empty state.

Why it matters: a browser automation tool that fails unless Brave is installed in exactly those locations will produce avoidable support tickets. Silent state fallback can also start the wrong instance or lose the real reason a connection failed.

Suggested fix: support `CC_PLAYWRIGHT_BROWSER`/`--browser-path`, `shutil.which`, common Windows and macOS Brave/Chrome/Edge paths, and a clear error that includes the override option. When a state JSON file is corrupt, fail with a repair hint or move it aside with an explicit warning instead of silently returning `{}`.

### 7. Preserve normal Markdown semantics in `cc-word from-markdown`

Priority: Medium

Tools affected: `cc-word`

What is wrong: `word_converter.py` converts paragraphs, list items, headings, and table cells mostly with `get_text(strip=True)`. It has no branch for images, links, inline code, bold, italic, strikethrough, checkboxes, footnotes, or nested inline elements.

Why it matters: the HTML/PDF converters keep richer Markdown through HTML/CSS, while the Word converter silently drops meaning and formatting. Users will see broken links, missing images, and flattened emphasis in DOCX output.

Suggested fix: replace the text-only mapping with a recursive HTML-to-DOCX renderer that creates runs for inline tags, adds hyperlinks, imports images, supports task-list checkboxes in ASCII form, and preserves code/strong/emphasis. Add fixtures for links, images, inline code, nested lists, and tables.

### 8. Replace raw SQL contact-list filters in `cc-vault`

Priority: Medium

Tools affected: `cc-vault`

What is wrong: `cc-vault lists add/remove --query` passes a user-supplied SQL `WHERE` clause directly into f-string SQL in `add_list_members_by_query` and `remove_list_members_by_query`.

Why it matters: even though this is local, it is a shipped personal-data tool. A typo or copied expression can cause SQLite errors or unintended bulk changes, and the feature is exposed as a normal CLI option rather than an expert escape hatch.

Suggested fix: replace `--query` with structured filters such as `--company`, `--tag`, `--account`, `--category`, `--relationship`, and `--where` only as a hidden/unsafe expert flag requiring `--yes`. If raw SQL remains, validate it as a single expression, block semicolons/comments, show the matched count first, and require confirmation for removals.

## Low Priority

### 9. Report missing or unreadable images in document converters

Priority: Low

Tools affected: `cc-pdf`, `cc-html`, `cc-word`

What is wrong: image embedding/extraction paths commonly skip missing or unreadable images without warning. `embed_images_as_base64()` catches all exceptions and leaves the original image reference, and remote images are skipped.

Why it matters: users can receive a "Done" message but get an HTML/PDF/DOCX with broken or missing images. That is especially confusing when converting Markdown reports with local screenshots.

Suggested fix: collect asset warnings and print an ASCII warning summary. Add `--strict-assets` to fail when referenced local assets are missing/unreadable, and `--asset-mode embed|link` so users can choose predictable behavior.

### 10. Normalize version strings and prevent version drift

Priority: Low

Tools affected: all shipped tools, especially `cc-devthrottle` and `cc-playwright`

What is wrong: version output formats differ (`cc-devthrottle v...` versus `cc-pdf version ...`), and `cc-playwright` hardcodes its version string in `argparse` instead of reading package metadata.

Why it matters: installers, tools pages, and support scripts should be able to compare versions without per-tool parsing rules. Hardcoded versions drift easily.

Suggested fix: standardize on `cc-tool version X.Y.Z` or JSON-capable `--version --json`, and add tests that compare CLI version output to `pyproject.toml` for every shipped Python tool.

### 11. Add a shipped-tool help and smoke-test matrix

Priority: Low

Tools affected: all shipped tools

What is wrong: each tool has tests in different styles, but there is no single shipped-surface matrix proving every `ship:true` command imports, prints help, prints version, and exposes the expected primary commands from the installed bundle.

Why it matters: registry-driven shipping makes it easy to add/remove tools without noticing a missing dependency, broken script entry point, non-ASCII output, or command-surface regression.

Suggested fix: generate the test matrix from `tools/registry.json` and `tools-manifest.json`. For each shipped script, run `--version`, `--help`, and one safe read-only command where available, then assert exit code, ASCII output, and no traceback. Run it against both source installs and the offline release bundle.

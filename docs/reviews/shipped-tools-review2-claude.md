# Shipped Tools - Second Review Pass (Claude)

A fresh, second review of the command-line tools that SHIP with DevThrottle
(`ship: true` in `tools/registry.json`): cc-pdf, cc-html, cc-word, cc-gmail,
cc-outlook, cc-playwright, cc-vault, cc-comm-queue, cc-devthrottle.

Round one produced 32 items; all were fixed and shipped (v0.9.22 / v0.9.23).
This pass reviews the CURRENT source for anything REMAINING or NEW, and checks
whether any round-one fix is incomplete or introduced a new problem. It does
NOT re-report the 32 fixed items.

Method: read the current source of every shipped tool (split across four
focused review passes), then verify each candidate finding directly against the
code before listing it. Every finding below was confirmed against the named
file and line.

Honest bottom line: round one held up well. The fixes I spot-checked
(Bcc envelope-only, X-GM-MSGID namespace, page-size/margin injection, browser
discovery, ASCII box-drawing, status state machine, WAL/busy_timeout, JSON via
plain print) are all present and correct. The remaining issues are mostly
edge-case correctness and consistency, plus a small number of real
data-integrity bugs reachable in normal use. There is no round-two equivalent
of the round-one severity (no leaked Bcc, no SQL-injected WHERE). The four High
items below are the ones worth fixing before the next release.

---

## High priority

### H1. cc-comm-queue: update methods return `conn.total_changes`, so editing a missing ticket reports success
- Tool: cc-comm-queue
- `src/database.py:704` (`update_status`), `:724` (`update_content`), `:744` (`update_recipient`), `:293` (`update_first_comment_state`)
- What is wrong: all four return `self.conn.total_changes > 0`. `total_changes`
  is cumulative for the lifetime of the connection, not the rows affected by the
  last statement. `update_content`/`update_recipient` also have no existence
  pre-check. `_migrate_schema()` runs a backfill UPDATE on every
  `Database.__init__` (`:211`), so `total_changes` can already be greater than
  zero before the command's own statement runs - the bug is reachable even from
  a single fresh CLI invocation, and is guaranteed in the long-lived
  Communication Manager desktop app (after its first write, every later
  `update_*` returns `True` regardless of whether the ticket exists).
- Why it matters: editing a non-existent ticket reports success and the
  caller/UI believes the edit landed. That is exactly the silent-failure the
  house rule forbids.
- Suggested fix: capture `cursor = self.conn.execute(...)` and return
  `cursor.rowcount > 0` in each method. Add a regression test asserting
  `update_content(<missing ticket>)` returns `False`.

### H2. cc-gmail: non-ASCII (or spaced) attachment filename corrupts Content-Disposition and breaks the attachment
- Tool: cc-gmail
- `src/smtp_client.py:68-71`, `src/gmail_api.py:247-250`
- What is wrong: `part.add_header("Content-Disposition", f"attachment; filename={file_path.name}")`
  builds the entire header value as one pre-formatted string. With a non-ASCII
  filename, the compat32 policy RFC2047-encodes the WHOLE value, so the output
  becomes `Content-Disposition: =?utf-8?q?attachment=3B_filename=...?=` - the
  literal `attachment` disposition type is mangled and the part is no longer
  recognized as an attachment. An ASCII filename containing spaces is emitted
  unquoted (`filename=quarterly report.pdf`), which is also malformed.
- Why it matters: sending any file whose name has accents or spaces produces a
  broken or mis-detected attachment. Attachments are a core function.
- Suggested fix: use the structured form and let the email library do RFC2231
  param encoding: `part.add_header("Content-Disposition", "attachment", filename=file_path.name)`.

### H3. cc-vault: `contacts add --role` is silently dropped (data loss)
- Tool: cc-vault
- `src/cli.py:1495` (`-r/--role` option) -> `:1507` (`role=role`) -> `src/db.py:1090` `add_contact` (`valid_fields` at `:1107-1120`)
- What is wrong: `add_contact` filters keyword arguments against `valid_fields`,
  which contains `'title'` but not `'role'`. The CLI advertises `-r/--role` and
  passes it through `**kwargs`, but the field loop never matches `role`, so the
  value is discarded with no error and no alias to `title`.
- Why it matters: a user who sets a contact's role at creation loses it
  silently - a no-fallback / data-loss violation. (Note `contacts edit` uses a
  separate `--title` option at `:1720`, so the two commands disagree on the
  field name as well.)
- Suggested fix: map `role` to the `title` column in the add path (or accept
  `role` as an alias inside `add_contact`). Add a test that
  `contacts add ... --role X` persists.

### H4. cc-vault: duplicate `_sanitize_fts_query` - the wrong implementation runs, and errors are swallowed to empty results
- Tool: cc-vault
- `src/db.py:4172` and `src/db.py:6030` (two module-level defs of the same name); used by `search_chunks_fts` (`:4197`) and `search_catalog_fts` (`:6052`)
- What is wrong: Python keeps the last definition, so the `:6030` version
  ("quote terms containing operator chars") shadows the `:4172` version
  ("strip all non-word chars"). `search_chunks_fts` sits directly under the
  `:4172` definition but at runtime calls the `:6030` one, so the code does not
  do what it reads as. The two sanitizers diverge on inputs like `SR&ED`, a
  column-filter `term:value`, or a stray `"`. `search_chunks_fts` masks the
  resulting `sqlite3.OperationalError` by returning `[]` (`:4222`) - a silent
  empty result - while `search_catalog_fts` (`:6052`) has no such guard and can
  raise on an unbalanced `"` or a `:`.
- Why it matters: FTS search behaves inconsistently and can silently return
  nothing (or raise) for legitimate queries containing operator characters.
- Suggested fix: delete the dead `:4172` definition (or rename one
  intentionally); have the surviving sanitizer also neutralize `"` and `:`; do
  not swallow the FTS error to `[]` - surface a clear error.

---

## Medium priority

### M1. cc-gmail: non-ASCII recipient display-name blobs the whole To/Cc header on the API send path
- Tool: cc-gmail
- `src/gmail_api.py:255-261` and the reply path `:367`; reply-all rebuilds names in `src/imap_client.py:935-939, 1016-1018`
- What is wrong: `message["to"] = to` flattens a value like
  `"Jose <jose@x.com>, b@c.com"` into a single encoded blob covering the
  addresses too. On the Gmail API path the raw message IS the envelope, so an
  accented display name (very reachable via `reply --all` to a thread with an
  accented participant) corrupts recipient parsing and the send can fail or
  misroute. (The SMTP path still delivers because its envelope is computed
  separately, but the visible header is still garbage.)
- Why it matters: replies/sends involving accented display names break on the
  API path.
- Suggested fix: build address headers with proper per-address phrase encoding -
  parse with `email.utils.getaddresses` and re-emit via the modern
  `EmailMessage`/`policy=default`, which encodes only the display-name phrase
  and leaves the addr-spec intact. (cc-outlook is unaffected; it sends
  recipients as structured Graph JSON.)

### M2. cc-pdf: a hung/slow headless browser raises an uncaught `TimeoutExpired` traceback
- Tool: cc-pdf
- `src/pdf_converter.py:202-207` (`subprocess.run(..., timeout=60)`) and `src/cli.py:254-268` (except chain)
- What is wrong: `subprocess.TimeoutExpired` is a `SubprocessError`, not a
  `RuntimeError`/`OSError`/`ValueError`/`FileNotFoundError`, so it is not caught
  by `from_markdown`'s handlers and dumps a raw Python traceback - violating the
  "tracebacks become clean {error}" rule. A wedged headless Chrome (profile
  lock, GPU init stall) is exactly the field failure this path hits.
- Suggested fix: catch `subprocess.TimeoutExpired` in `convert_to_pdf` (or the
  CLI) and re-raise as a clean error naming the timeout.

### M3. cc-word: multi-line fenced code blocks collapse to a single line
- Tool: cc-word
- `src/word_converter.py:450-454` (`_process_code_block`)
- What is wrong: the whole block text (including `\n`) goes into a single
  python-docx run / `<w:t>`; Word does not render embedded newlines as line
  breaks, so an N-line code block renders as one wrapped line with structure and
  indentation lost. The existing `test_with_code_block` only uses a single-line
  snippet, so this slipped through.
- Why it matters: code-heavy reports are a primary use of these tools; this is a
  real fidelity loss on the most common multi-line content.
- Suggested fix: split on `\n` and emit `run.add_break()` between lines (the
  `<br>` handling in `_render_inline` already does this).

### M4. cc-devthrottle: `setup update` and `setup repair` lack the error guard that `setup install` has
- Tool: cc-devthrottle
- `src/setup_ops.py:369-374` (`update`/`repair`) vs `:358-366` (`install`)
- What is wrong: `install()` wraps `run_setup_cli(...)` in
  `try/except (OSError, RuntimeError, URLError, SubprocessError)` and prints a
  clean `ERROR:` line; `update()` and `repair()` call it bare. A GitHub 5xx /
  rate-limit (re-raised `HTTPError` from `_latest_release()`) or a bad
  downloaded path (`FileNotFoundError` from `subprocess.run`) propagates as a raw
  traceback. `setup update` is the common path.
- Suggested fix: extract the install guard into a helper and call it from all
  three commands.

### M5. cc-playwright: split error contract - some failures print plain text via `SystemExit`, others print JSON `{"error"}`
- Tool: cc-playwright
- `src/cli.py:484` (`_err` emits JSON) vs `SystemExit("...")` at `:194,202,215` (`_find_browser`), `:374,384` (`_connect`), `:731` (`_locate`), `:814` (`cmd_select`), `:975` (`cmd_wait`); `main()` catch-all `:1158` deliberately re-raises `SystemExit` unchanged.
- What is wrong: a consumer cannot rely on one error shape. `cc-playwright info`
  on a missing browser prints a bare line; on a Playwright timeout it prints
  `{"error": ...}`. The existing test only covers the JSON path, masking the
  gap.
- Suggested fix: route these through `_err(...)`, or have `main()` reformat the
  `SystemExit` string into the `{"error"}` shape before exiting.

### M6. cc-playwright: `start`/`stop` trust a recorded PID with no identity check (PID reuse can kill an innocent process)
- Tool: cc-playwright
- `src/cli.py:248` (`_is_running` checks only PID existence) used by `cmd_start` (`:585`) and `cmd_stop`/`_terminate_process` (`:676`)
- What is wrong: if a launched browser dies and the OS reuses its PID, stale
  state makes `start` report `already_running` (refusing to launch) and `stop`
  will `taskkill /PID <reused-pid>` an unrelated process. The recorded state
  already has `port`/`ws_endpoint`/`profile_dir` available for a cheap identity
  check, but none is done.
- Why it matters: low probability but a sharp edge - killing an arbitrary
  process on a long-lived machine.
- Suggested fix: before treating the PID as live, confirm identity - probe
  `http://localhost:<port>/json/version` and/or match the process image
  name/command line against the recorded `profile_dir` (the
  `_dir_locked_by_running_browser` machinery already exists).

### M7. cc-gmail: `requirements.txt` is out of sync with the actual imports / pyproject
- Tool: cc-gmail
- `requirements.txt` vs `src/auth.py` (imports `keyring`, `cc_storage`) and `pyproject.toml` (lists `keyring>=25.0.0`, `cc-storage`)
- What is wrong: `requirements.txt` lists neither `keyring` nor `cc-storage`.
  The PyInstaller build escapes this because `build.ps1` installs from pyproject,
  but any consumer installing from `requirements.txt` gets an environment that
  crashes on first import.
- Suggested fix: regenerate `requirements.txt` from pyproject, or delete it and
  document pyproject as the single dependency source.

### M8. Cross-cutting: `cc_shared.__init__` eagerly imports `config`/`llm`, coupling the doc tools to `cc_storage`
- Tools: cc-pdf, cc-html, cc-word (and any cc_shared consumer)
- `cc_shared/__init__.py:5-6` (eager `from .config` / `from .llm`) vs the deliberately lazy `markdown_parser` at `:15-31`; `config.py:19-26` imports `cc_storage`; `cc-pdf.spec:23-48` bundles `cc_shared.config` but not `cc_storage`
- What is wrong: importing any cc_shared submodule first runs the package
  `__init__`, which eagerly imports `.config` -> `cc_storage`. The three document
  tools never use config or llm, yet inherit that dependency. It only keeps
  working because each tool wraps the import in a multi-level `try/except
  ImportError` that falls back to bare-module imports - the kind of silent
  fallback the house rules discourage - and in a frozen build `import cc_shared`
  would fail outright but for that fallback.
- Why it matters: latent packaging fragility plus a hidden dependency; the lazy
  treatment already applied to `markdown_parser` was not applied to
  `config`/`llm`.
- Suggested fix: make `.config`/`.llm` lazy via the same `__getattr__`
  mechanism, then tighten the fallback chains in the three tools.

---

## Low priority

### L1. cc-html: SVG data-URI images are never extracted (dead `svg+xml` branch)
- cc-html, `src/md_converter.py:55-60`. The regex `data:image/(\w+);base64,(.+)`
  cannot match `svg+xml` (the `+` stops `\w+`), so the `if ext == "svg+xml"`
  branch is unreachable and an inline SVG data URI is left as a giant raw `data:`
  URI in the output. Fix: `data:image/([\w+]+);base64,(.+)`.

### L2. cc-gmail: IMAP `X-GM-RAW` search query is not escaped
- cc-gmail (App-Password path), `src/imap_client.py:247, 314, 521, 863`.
  `f'X-GM-RAW "{query}"'` interpolates the user query; a double-quote terminates
  the IMAP string early, breaking the search (and in principle injecting search
  tokens). Fix: use an IMAP literal or reject/escape embedded quotes.

### L3. cc-gmail: IMAP archive/delete use mailbox-wide `expunge()`
- cc-gmail (App-Password path), `src/imap_client.py:555, 595, 611, 616, 625, 660`.
  After flagging one message `\Deleted`, `conn.expunge()` expunges EVERY
  `\Deleted` message in the selected mailbox. If another message is already
  flagged, it is removed too. Fix: use `UID EXPUNGE` (RFC 4315; Gmail supports
  it) scoped to the target UID.

### L4. cc-outlook: `recipients` uses `--format json` (not the standard `--json`) and emits non-ASCII JSON
- cc-outlook, `src/cli.py:1039, 1064, 1070`. Every other JSON-capable command
  in both email tools uses a boolean `--json`; only `recipients` exposes
  `--format table|json`, shadows the builtin `format`, and writes
  `json.dumps(..., ensure_ascii=False)` while the comparable read path uses
  `ensure_ascii=True` (`:450`). Inconsistent machine interface plus an ASCII-rule
  violation in one path. Fix: replace with a `--json` boolean and
  `ensure_ascii=True`.

### L5. cc-outlook: lacks the Windows UTF-8 stdout wrapper that cc-gmail installs
- cc-outlook vs cc-gmail `src/cli.py:154-158`. cc-gmail wraps
  `sys.stdout/stderr` in a UTF-8 `TextIOWrapper(errors='replace')` on win32;
  cc-outlook has no equivalent, so a non-Rich `print()` of a message
  body/subject outside the legacy console code page can raise
  `UnicodeEncodeError` on a cp1252 console. Fix: mirror cc-gmail's wrapper (or
  move it into the shared layer both import).

### L6. cc-playwright: error/help text hardcodes "Brave" although discovery now selects Chrome/Edge/Chromium
- cc-playwright, `src/cli.py:381, 385, 639, 641`, module docstring `:5`. After
  the round-one discovery work, a Chrome/Edge user gets failure messages naming
  a browser they are not using. Cosmetic. Fix: say "the browser" or interpolate
  the resolved executable name.

### L7. cc-playwright: Windows lock pre-check omits `chromium.exe`
- cc-playwright, `src/cli.py:231-240` (CIM filter `brave.exe`/`chrome.exe`/`msedge.exe`)
  vs `BROWSER_WHICH_NAMES` which includes `chromium` (`:54-64`). If the launched
  browser is Chromium, the "profile busy" advisory pre-check can never see it
  and the user falls through to the slower debug-port timeout. Advisory only.
  Fix: add `chromium.exe` to the filter.

### L8. cc-devthrottle: `schedule-create` action metadata uses synthetic arg names that do not match the real flags
- cc-devthrottle, `src/cli.py:176-188`. The `_ACTIONS` entry lists `args`
  `cron_or_at`, `seed_or_worklist` and a `command` template that omits the real
  `--at`/`--notify-on`/`--notify-webhook` options. An agent that machine-reads
  `actions --json` would build a wrong invocation. (Round one fixed metadata
  drift generally; this one entry still drifts.) Fix: model the real options or
  drop the per-arg list and rely on an accurate `command` template.

### L9. cc-devthrottle: shared `director.py` docstring contradicts the schedule path
- cc-devthrottle / cc_shared, `cc_shared/director.py:8-10` says the Director
  "never needs the Gateway URL or the fleet token," but `schedule_ops.py:46-83`
  resolves `gateway.url`/`gateway.token` and calls `/cron/jobs` directly with a
  Bearer token. Per the brief the schedule->Gateway path is by design, so this
  is a doc/contract inconsistency that will mislead the next maintainer, not a
  runtime bug. Fix: reconcile the docstring.

### L10. cc-comm-queue: ticket-number allocation is not atomic under concurrency
- cc-comm-queue, `src/database.py:301-314` (`_get_next_ticket_number`:
  `SELECT COALESCE(MAX(ticket_number),0)+1`). Two concurrent `add` processes
  (or app + CLI) can read the same MAX; the second INSERT hits the UNIQUE
  constraint and is swallowed by a broad `except`, producing a spurious "Failed
  to add content" (no corruption). The WAL/busy_timeout fix does not make this
  read-then-insert atomic. Fix: use an autoincrement column / `INSERT ...
  RETURNING`, or retry on `IntegrityError`.

### L11. cc-comm-queue: `status` column has no `CHECK` constraint and unknown statuses pass the transition guard
- cc-comm-queue, `src/database.py:43` (schema) and `:127-130`
  (`_validate_status_transition` returns permissively for an unknown current
  status). If a bad status ever lands (via `--force` or a future bug), the
  approval guard silently disables itself for that row. Defense-in-depth for the
  workflow this tool exists to enforce. Fix: add `CHECK(status IN (...))` and
  make the unknown-status branch raise.

### L12. cc-vault: `search` semantic mode ignores `-n` in its per-collection display
- cc-vault, `src/cli.py:383-392`. `-n` is passed to
  `semantic_search(n_results=n)` but the printout hardcodes `items[:5]`, so
  `search "x" -n 20` shows at most 5 per collection - inconsistent with `-n`
  everywhere else and with the hybrid branch. Fix: slice by `n`.

### L13. Cross-cutting nits
- cc-pdf/cc-word still use the collision-prone `path_map.get(img.original_name)`
  image lookup (`cc-pdf/src/md_converter.py:133-137`,
  `cc-word/src/md_converter.py:80-86`) that the round-one fix replaced with
  `relative_paths_in_order` in cc-html only; works today solely because both
  synthesize unique names. Latent regression risk - switch both for parity.
- cc-pdf/cc-word `--force` re-run never clears the sibling `{stem}_images/`
  dir, so repeated forced conversions accumulate `image_001_image_001.png`-style
  duplicates (`cc_shared/image_extractor.py:94-99`).
- cc-gmail and cc-outlook each carry a leftover empty `test/` dir (only
  `.gitkeep` + README) next to the real `tests/` - confusing scaffolding to
  delete.
- cc-outlook `forward_message` (`src/outlook_api.py:749-750`) prepends a plain
  -text note to an HTML body without setting `body_type`, so newlines collapse;
  HTML-escape and wrap the note when the body is HTML.

---

## Round-one fixes verified intact (not re-reported)
- cc-gmail Bcc remains envelope-only (`smtp_client.py:82-92`); reply paths carry
  no Bcc. X-GM-MSGID resolution is consistent across read/mark/delete/archive
  and re-selects the right mailbox per operation. Non-ASCII subjects and bodies
  encode correctly (only the structured To / filename headers in H2/M1
  misbehave). The POSIX-only 0600 token hardening is intentional and documented;
  Windows relies on the `%LOCALAPPDATA%` per-user ACL.
- cc-outlook has `msal` in pyproject + requirements; calendar timezone and
  pagination are correct; `--html` sets the body type.
- cc-comm-queue status state machine has no CLI-reachable bypass (`posted`
  requires `approved` or `--force`; no `approve` command exists in the CLI);
  strict choice validation rejects unknown status/visibility/audience with exit
  code 2; `list` honors `-n` and reports the true total; JSON via plain
  `print(json.dumps(...))`.
- cc-vault WAL + `busy_timeout` are set on every connection (`db.py:72-74`); the
  round-one `--query` parametrization is complete (the only remaining dynamic
  SQL is hardcoded internal table/column identifiers, plus the deliberately
  gated `--where` escape hatch with its `;`/`--`/`/* */` validator). cc-comm
  -queue WAL/busy_timeout likewise on every `_connect` (`database.py:168-170`).
- cc-playwright graceful-then-force `stop` is scoped to the single recorded PID
  with no tree-kill; browser discovery fails with a clear message naming
  `--browser-path` and the env override; it has a solid regression suite mapped
  to the round-one findings.
- cc-devthrottle int/float/bool setting guards are correct (bool before int);
  JSON outputs use plain `print(json.dumps(...))` with `ensure_ascii=True`.
- No Unicode/emoji bytes remain in any reviewed `src/` tree (grep clean); the
  Rich ASCII-truncation shim and ASCII box defaults are present across the
  Rich/Typer tools.

## Test-coverage gaps tied to the findings
None of the new bugs are covered by a regression test:
- H1: no test asserts `update_content`/`update_recipient` return `False` for a missing ticket.
- H3: no test asserts `contacts add --role` persists.
- H4: no test pins which `_sanitize_fts_query` behavior is in effect.
- M3: `test_with_code_block` uses only a single-line snippet, so the multi-line collapse is untested.
- Guard/extension/theme logic is duplicated into all three document `cli.py`
  files but exercised by tests only in cc-html; cc-pdf and cc-word have no
  `test_cli.py`. cc-devthrottle `session_ops` has no `test_session_ops.py`.

---

Source: four focused review passes over the current source of the nine shipped
tools, each finding re-verified against the cited file and line before listing.

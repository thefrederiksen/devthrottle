# Shipped Command-Line Tools Review (DevThrottle)

Review of the command-line tools that ship in the installed product. The authoritative
shipped set comes from `tools/registry.json` (`"ship": true`):

- cc-pdf, cc-html, cc-word (document conversion)
- cc-gmail, cc-outlook (email and calendar)
- cc-playwright (browser automation)
- cc-vault (personal data vault and search)
- cc-comm-queue (Communication Manager approval queue)
- cc-devthrottle (unified fleet, session, settings, schedule, setup)

This is a review only. No tool code was changed, no installs were run, and no file other
than this report was touched. Findings carry `file:line` evidence and a priority of High,
Medium, or Low. Each item is written so it can become a single GitHub issue.

---

## Executive summary

The shipped tools are in reasonable shape and the house ASCII-only rule is almost perfectly
respected: every Python source for all nine tools scanned clean for emoji and unicode. The
only violation found anywhere is one ellipsis character in a documentation file
(cc-playwright `LINKEDIN_POSTING.md`). That is a genuinely good result and should be
protected with a guard test.

The most serious cross-cutting problem is machine output. The `--json` mode that agents and
scripts depend on is corrupted in cc-devthrottle and cc-comm-queue because JSON is printed
through Rich, which line-wraps to eighty columns when output is not a terminal and inserts
newlines into the middle of long values. Any repository path, URL, or content preview over
eighty characters silently becomes invalid JSON. This breaks the primary reason `--json`
exists. Several other tools simply have no `--json` on their core read commands, so the
suite as a whole is hard to drive programmatically.

The second theme is silent no-ops and silent fallbacks, which directly contradict the
project's "fail clearly, no fallback" rule. Advertised flags do nothing: cc-pdf
`--page-size` and `--margin` are accepted and ignored, cc-outlook `--html` is ignored,
cc-comm-queue `config set` writes to a path nothing ever reads, and cc-vault `init <path>`
ignores its path argument. A user runs the command, sees a success message, and gets no
effect.

The third theme is correctness bugs with real consequences: cc-gmail's IMAP App-Password
path can read, mark, or delete the wrong message because it mixes mailbox UID namespaces,
and it leaks BCC recipients over SMTP. cc-vault reports zero completed tasks forever and its
`--status completed` filter matches nothing, both due to a `done` versus `completed` value
mismatch. cc-word silently drops images and all inline formatting when converting Markdown.

The fourth theme is fidelity of documentation and discoverability: stale docs point at the
old `cc-director` repo and product name and at directories that no longer exist; cc-vault
ships a `VAULT_FIX_PLAN.md` written around a ChromaDB design that has since been replaced by
native SQLite vectors; and cc-devthrottle's hand-maintained `actions` metadata (the agent
discovery surface) has drifted out of sync with the real commands, so agents will build
invalid command lines from it.

Lower-priority but worth a sweep: plaintext OAuth token files with no permission hardening,
SQLite databases opened without WAL or a busy timeout while shared with the desktop app,
several stray personal scripts committed inside the shipped cc-gmail tree, and a number of
flag-name and subcommand inconsistencies between sibling tools.

---

## High priority

### H1. `--json` output is corrupted by Rich line-wrapping
- Tools: cc-devthrottle, cc-comm-queue
- What is wrong: JSON is emitted with `console.print(json.dumps(...))`. When stdout is not a
  terminal (the normal case for agents and pipes) Rich wraps to eighty columns and inserts
  newlines into the middle of values. Confirmed empirically: `cc-devthrottle actions --json`
  from the shipped binary fails `json.load` with "Invalid control character at line 31
  column 80", and a long repository path is split across two lines. Evidence:
  `cc-devthrottle/src/session_ops.py:87`; `settings_ops.py:117,127,147,163,175,193`;
  `schedule_ops.py:184,223,247,327,344,385`; `cc-comm-queue/src/cli.py:405,441,591`.
- Why it matters: `--json` exists specifically so agents and scripts can parse output. Long
  paths, URLs, cron seeds, and content previews routinely exceed eighty characters, so the
  output silently becomes unparseable. There is also a secondary risk that Rich interprets
  `[...]` markup inside string values.
- Suggested fix: use plain `print(json.dumps(...))` for all JSON (the correct pattern already
  exists in the same codebase at `setup_ops.py:333` and the actions path `cli.py:283`), or a
  `Console(width=10**6, soft_wrap=True)`, or `console.print_json`.

### H2. cc-gmail IMAP path can read, mark, or delete the wrong message
- Tool: cc-gmail
- What is wrong: `list`/`search` return IMAP UIDs from the INBOX mailbox
  (`src/imap_client.py:132-195`), but `get_message_details` (`:264`), `mark_as_read`
  (`:452`), `mark_as_unread` (`:459`), and `delete_message` (`:508`) select the `ALL` mailbox
  and then `uid fetch/store` that same number. In Gmail IMAP every label is a separate
  mailbox with its own UID space, so an INBOX UID does not address the same message in
  `[Gmail]/All Mail`.
- Why it matters: On the App-Password path, which the README recommends, `cc-gmail read`,
  mark-read, and especially `delete` can silently operate on the wrong message. Destructive
  operation on the wrong target is the worst failure mode for an email tool.
- Suggested fix: operate within the same mailbox the IDs came from, or key on the stable
  `X-GM-MSGID` rather than per-mailbox IMAP UID.

### H3. cc-gmail leaks BCC recipients to all recipients over SMTP
- Tool: cc-gmail
- What is wrong: `smtp_client.py:83` sets `message["Bcc"] = bcc` and `:95` sends
  `message.as_string()`. `smtplib` transmits the message verbatim, so the `Bcc:` header is
  delivered to every To and Cc recipient. (The Gmail API path is fine because Gmail strips
  Bcc.)
- Why it matters: BCC exists to hide recipients from each other. This exposes the entire BCC
  list, a privacy breach.
- Suggested fix: never put Bcc in the MIME headers; keep it only in the SMTP envelope
  recipient list passed to `sendmail`.

### H4. cc-pdf `--page-size` and `--margin` are silently ignored
- Tool: cc-pdf
- What is wrong: `src/pdf_converter.py:90-102` builds the headless Chrome command with no
  page-size or margin flag. `PAGE_SIZES` is defined and validated against (`:11-14,58-60`)
  but never applied; the `margin` parameter (`:44`) is never used. The only effective margin
  is the hardcoded `@page { margin: 1in; }` in `tools/cc_shared/css_themes.py:231-233`.
- Why it matters: Chrome's `--print-to-pdf` defaults to Letter, but the CLI default is
  `--page-size a4` (`cli.py:117-121`), so the documented default silently produces a
  Letter-sized PDF. `--margin` does nothing. These flags are advertised in `--help` and in
  the global CLAUDE.md usage and they lie about their effect.
- Suggested fix: inject a dynamic `@page { size: ...; margin: ...; }` rule from the
  `page_size`/`margin` arguments at convert time (Chrome honors `@page size`), replacing the
  static print CSS.

### H5. cc-pdf only finds Google Chrome or Chromium, not Edge or Brave
- Tool: cc-pdf
- What is wrong: `src/pdf_converter.py:17-37` lists only Chrome/Chromium paths. There is no
  Microsoft Edge (Chromium-based, fully supports headless `--print-to-pdf`), no Brave, no
  `shutil.which()` PATH lookup, and no environment override. Also `:23` builds
  `C:\Users\{USERNAME}\AppData\Local\...` by hand, which becomes `C:\Users\\AppData\...` when
  `USERNAME` is empty.
- Why it matters: Chrome is the single hard dependency of cc-pdf. The house default browser
  is Brave, and many corporate Windows machines ship only Edge. On those machines cc-pdf
  fails with "Chrome not found" even though a capable Chromium browser is installed.
- Suggested fix: add Edge and Brave paths, fall back to `shutil.which` for
  chrome/chromium/msedge/brave, honor an environment override, and use `%LOCALAPPDATA%`
  instead of hand-building the path.

### H6. cc-outlook `--html` flag is a silent no-op (text sent as HTML)
- Tool: cc-outlook
- What is wrong: `cli.py:461` passes `html=html` but `outlook_api.py:146-192` (`send_message`)
  and `:194-227` (`create_draft`) never use it; they only set `message.body = body`. Because
  O365 defaults the body type to HTML, plain text is actually sent as HTML (newlines
  collapse) and `--html` changes nothing, the reverse of intent.
- Why it matters: Sends come out malformed, and the flag that should control format is dead.
  This is exactly the silent fallback the project rules forbid.
- Suggested fix: set `message.body_type = 'HTML' if html else 'text'`.

### H7. cc-comm-queue `config set` writes to a path that is never read
- Tool: cc-comm-queue
- What is wrong: `config_set` writes `~/.cc-director/config.json` (`cli.py:1209,1231-1233`)
  but `get_config()` reads `CcStorage.config_json()` =
  `%LOCALAPPDATA%\cc-director\config\config.json`. The two paths are different and the home
  one does not exist, so the command reports "OK: Set ..." and changes nothing the tool ever
  loads.
- Why it matters: A documented command (README lines 66-74) silently lies; users setting
  queue path or persona via the CLI get no effect.
- Suggested fix: write through the shared config store (`CcStorage.config_json()`), the same
  store `config show` reads, or defer to `cc-devthrottle settings set`.

### H8. cc-word drops all inline formatting and all images on Markdown conversion
- Tool: cc-word
- What is wrong: `src/word_converter.py` builds the document with `get_text(strip=True)`
  everywhere (headings `:196-197`, paragraphs `:206-208`, list items `:234-239`, table cells
  `:339`). There is no `<img>` handler in `_process_element` (`:183-229`), and `<a>`,
  `<strong>`, `<em>`, and inline `<code>` are never inspected. Nested lists lose structure.
- Why it matters: For a Markdown-to-Word tool this is major fidelity loss. Bold, italic,
  links, and inline code collapse to plain text, and Markdown images vanish from the `.docx`
  with no warning. The reverse path via mammoth is fine; only the from-markdown path is
  affected.
- Suggested fix: walk inline children and emit runs with the right bold/italic/hyperlink,
  add an `<img>` handler that inserts the picture, and recurse for nested lists with an
  indent level.

---

## Medium priority

### M1. cc-devthrottle schedule commands bypass the Director and call the Gateway directly
- Tool: cc-devthrottle
- What is wrong: `director.py:1-10` states the tool "only ever calls its own Director ... it
  never needs the Gateway URL or the fleet token." But `schedule_ops.py:44-66,110-127` reads
  `config.gateway.url`/`config.gateway.token` and issues `requests` straight to `/cron/jobs`
  with a Bearer token. Three different backends now live in one tool: session and message go
  to the Director loopback, settings go to a local file, schedule goes to the Gateway. When
  `gateway.token` is empty those calls go to a remote Gateway unauthenticated.
- Why it matters: Inconsistent trust model, the shared design contract is now inaccurate, and
  remote schedule calls may be unauthenticated or fail depending on Gateway configuration.
- Suggested fix: route schedule operations through the Director Control API to match the
  stated architecture, or update the `director.py` contract and make the auth requirement
  explicit.

### M2. cc-devthrottle `actions` metadata has drifted from the real commands
- Tool: cc-devthrottle
- What is wrong: `_ACTIONS` (`cli.py:50-259`) is a hand-maintained mirror of the Typer
  commands and is already wrong. `schedule-create` advertises args `cron_or_at`, `tz`,
  `seed_or_worklist` but the real command uses separate `--at`/`--cron` and
  `--seed`/`--worklist` (`cli.py:466-486`). It omits real options (`spawn --agent/--prompt/
  --name/--type`, `message ask --timeout-ms`) and lists `setup doctor` as distinct from
  `setup status` though `doctor()` just calls `status()` (`setup_ops.py:354-355`).
- Why it matters: `actions --json` is the agent discoverability surface. Agents will
  construct invalid command lines from it.
- Suggested fix: generate the action list from the Typer app, or add a test asserting parity
  between `_ACTIONS` and the real commands; drop or realias the duplicate `doctor`.

### M3. Sparse and inconsistent `--json` across the email and vault tools
- Tools: cc-gmail, cc-outlook, cc-vault, cc-devthrottle
- What is wrong: Core read commands lack machine-readable output. cc-gmail and cc-outlook
  emit JSON only on `accounts list` and `recipients` and use two different conventions
  (`--json` versus `--format json`); `list`, `read`, `search`, `labels`/`folders`, and
  calendar have none. cc-vault's flagship `search` (`cli.py:252`), `ask` (`:220`), and
  `stats` (`:188`) have no `--json` while many sub-apps do. cc-devthrottle's mutating
  commands (`rename`, `spawn`, `send`, `ask`, schedule `enable`/`disable`/`delete`) have no
  `--json`.
- Why it matters: This is an agent-first suite; agents cannot reliably parse the result of
  the most common operations.
- Suggested fix: add a uniform `--json` to all read commands and to the mutating commands
  that return a result, standardizing on `--json`.

### M4. SQLite opened without WAL or busy timeout, shared with the desktop app
- Tool: cc-comm-queue (and applies to cc-vault concurrency generally)
- What is wrong: `database.py:125` connects with `check_same_thread=False` but sets no
  `timeout`, no `PRAGMA journal_mode=WAL`, and no `busy_timeout`. The Communication Manager
  desktop app and this CLI both open `communications.db`. The CLI also never calls
  `qm.close()`.
- Why it matters: Default rollback-journal locking plus the five-second default makes
  concurrent writes raise "database is locked".
- Suggested fix: enable WAL and set `PRAGMA busy_timeout=5000` on connect; close
  connections on exit.

### M5. OAuth and token caches written in plaintext with no permission hardening
- Tools: cc-gmail, cc-outlook
- What is wrong: cc-gmail writes `token.json` (containing the refresh token) via `write_text`
  with no chmod (`auth.py:368`); cc-outlook writes its MSAL cache the same way
  (`auth.py:137,333-334`). Under `%LOCALAPPDATA%` the Windows profile gives baseline
  protection, but the cross-platform fallback directory is world-readable under the default
  umask, and nothing scopes the file to the user.
- Why it matters: A leaked refresh token grants ongoing mailbox access.
- Suggested fix: `os.chmod(path, 0o600)` on POSIX and document the location. (cc-gmail
  already stores App Passwords in the OS credential manager via keyring, which is good; the
  OAuth token file is the gap.)

### M6. `msal` missing from cc-outlook pyproject dependencies
- Tool: cc-outlook
- What is wrong: `src/auth.py:56` imports `msal`, central to all auth, but
  `pyproject.toml` `[project].dependencies` omits it (it is only in `requirements.txt`).
- Why it matters: A package built or installed from pyproject crashes with
  `ModuleNotFoundError` on first auth.
- Suggested fix: add `msal>=1.20.0` to the pyproject dependencies.

### M7. cc-vault reports zero completed tasks and `--status completed` matches nothing
- Tool: cc-vault
- What is wrong: `complete_task()` sets `status = 'done'` (`db.py:2982`) per the CHECK
  constraint (`db.py:359`), but `get_vault_stats()` counts `WHERE status = 'completed'`
  (`db.py:900`) and the `tasks list` help advertises `pending, completed, all` (`cli.py:613`)
  while the filter passes the literal through to `t.status = ?` (`db.py:2934-2936`). No row is
  ever `completed`, so `stats` always shows zero and `tasks list --status completed` returns
  nothing; only the undocumented `--status done` works.
- Why it matters: A core feature reports wrong numbers and a documented filter silently
  returns empty.
- Suggested fix: count and filter on `done` (or map `completed` to `done`) and align the
  help text.

### M8. cc-comm-queue `list` caps at 100 and misreports the total
- Tool: cc-comm-queue
- What is wrong: `cli.py:474` defaults `-n` to 20 but calls `qm.list_content(...)` whose
  default `limit=100` (`queue_manager.py:85`), then slices `items[:limit]`. So `-n 200` can
  never return more than 100 rows, and the "Showing X of N" footer uses the fetched count,
  not the real total. Confirmed: with 120 items, `list -n 200` prints "Showing 100 of 100".
- Why it matters: Silent truncation and a misleading total in an approval queue.
- Suggested fix: pass the CLI limit into `list_content` and compute the true total
  separately (for example via `get_stats`).

### M9. cc-comm-queue `mark-posted` has no status guard and bypasses approval
- Tool: cc-comm-queue
- What is wrong: The tool's purpose is human approval, but `mark-posted` (`cli.py:717`) and
  the underlying `update_status` (`database.py:582`) accept any current status. An agent can
  `add` then immediately `mark-posted` and `log-to-vault`, skipping `pending_review` and
  `approved`. Only `send` enforces `status == approved` (`cli.py:927`).
- Why it matters: The approval gate, the entire reason the queue exists, can be bypassed.
- Suggested fix: enforce valid status transitions centrally, or at minimum require `--force`
  to mark a non-approved item posted.

### M10. Unexpected errors produce raw tracebacks instead of clean messages
- Tools: cc-pdf, cc-html, cc-word, cc-vault, cc-playwright, cc-devthrottle
- What is wrong: Several command bodies catch only narrow exception types or none. cc-word
  `to-markdown` on a corrupt `.docx` makes mammoth raise an uncaught type that dumps a
  traceback (`cc-word/src/cli.py:182-202`). cc-vault `search` with no `OPENAI_API_KEY` lets
  `RuntimeError` from `vectors.embed_text` (`vectors.py:133-134,668`) propagate uncaught to a
  stack trace (`ask` is handled cleanly, `search` is not). cc-playwright action commands
  (`cli.py:502-687`) have no try/except, so a Playwright `TimeoutError` prints a traceback
  instead of the tool's own `{"error": ...}` JSON shape (`_err`, `cli.py:330-332`).
  cc-devthrottle `settings set` (`settings_ops.py:85-87`) does `int(value)`/`float(value)`
  with no guard, and `setup update`/`repair` lack the try/except that `setup install` has
  (`setup_ops.py:358-374`).
- Why it matters: Failing with a raw traceback is the opposite of the enterprise "fail
  explicitly with a clear message" standard, and for cc-playwright it breaks the JSON
  contract callers rely on.
- Suggested fix: add a catch-all at each command entry point that prints `Error: ...` and
  exits 1 (entry points are where try/except is allowed per the coding standard); route
  cc-playwright exceptions through `_err()`.

### M11. cc-outlook calendar timezone handling shifts times
- Tool: cc-outlook
- What is wrong: Naive local datetimes are stamped as UTC in `get_free_busy`
  (`outlook_api.py:1037-1046`, fed naive from `cli.py:1591-1592`) and in the `flag` due date
  (`:742-745`), and `create_event`/`get_events` pass naive datetimes to O365
  (`:565-606`). Times come out shifted by the local offset.
- Why it matters: Wrong meeting and free/busy times. (cc-gmail has a milder version:
  `calendar_api.py:119-120` sends a naive `dateTime` with no `timeZone`, so Google falls
  back to the calendar default, which is ambiguous but usually acceptable.)
- Suggested fix: attach the local timezone or convert to UTC consistently across all
  calendar paths.

### M12. cc-outlook calendar capped at 250 events with no pagination
- Tool: cc-outlook
- What is wrong: `get_events` hard-codes `limit=250` with no `@odata.nextLink` follow
  (`outlook_api.py:568-573`), and `search_events` filters in Python over that capped set
  (`:982`).
- Why it matters: Calendar listing and search silently miss everything beyond the first 250
  events.
- Suggested fix: paginate via `@odata.nextLink`.

### M13. cc-outlook truncates IDs that documented workflows then consume
- Tool: cc-outlook
- What is wrong: `attachments` prints the attachment id `[:30] + '...'` (`cli.py:745`), and
  `calendar events`/`list` truncate event and calendar ids (`cli.py:1085,1126`), yet the
  README tells users to feed those ids into `download-attachment` and `calendar get`
  (README lines 92-93). Message `list` prints full ids, so it is also internally
  inconsistent.
- Why it matters: The only source of the id emits an unusable value, breaking documented
  multi-step workflows.
- Suggested fix: print full ids, or add `--json`/`--ids` that emits untruncated values.

### M14. Stray personal scripts committed inside the shipped cc-gmail tree
- Tool: cc-gmail
- What is wrong: `tools/cc-gmail/enrich_contacts.py`, `extract_sent_contacts.py`,
  `sync_to_vault.py`, and `enrich_state.json` are personal one-off scripts, not part of the
  CLI (the PyInstaller entry is `src/cli.py`, so they are not in the exe). They contain their
  own problems: `enrich_contacts.py:188-194` shells out to `claude -p` (print mode is banned
  project-wide), `sync_to_vault.py:54-55,68-69` and `enrich_contacts.py:422` use
  `except Exception: return False` (silent fallback), and `enrich_state.json` is personal run
  state checked into the repo. No credentials are leaked (the JSON holds only numeric IDs and
  a date).
- Why it matters: They do not belong in a shipped tool's source tree and they model
  practices the project forbids.
- Suggested fix: delete all four from the tool directory (move to an internal scripts area if
  still needed).

### M15. Local sibling packages are unresolvable from pyproject
- Tools: cc-gmail, cc-outlook (and the document tools reference `cc-shared`)
- What is wrong: cc-gmail lists bare `cc-storage` (`pyproject.toml:22`) and cc-outlook lists
  `cc-shared`/`cc-storage`; these are local sibling packages, not on PyPI. A plain
  `pip install` from pyproject cannot resolve them; only `build.ps1` works (it does
  `pip install -e ../cc_shared`). The document tools reference `cc-shared` by name with no
  pin or path.
- Why it matters: pyproject is misleading and a non-build install fails.
- Suggested fix: use path or optional dependencies, or document the build-only flow
  explicitly.

### M16. cc-pdf silently swallows image-embed failures
- Tools: cc-pdf, cc-html
- What is wrong: `html_generator.py:117-119` in both tools does `except Exception: pass` while
  base64-embedding an image. For cc-pdf this is worse than cosmetic: a failed embed leaves a
  relative `src` that the headless Chrome temp-file URL cannot resolve, so the image silently
  disappears from the PDF and the user gets a "Done".
- Why it matters: Violates the no-silent-fallback rule and produces a PDF missing content
  with no signal.
- Suggested fix: catch the specific I/O error and either raise with a clear message naming
  the image or print a visible `WARNING: could not embed <path>: <reason>`.

---

## Low priority

### L1. One unicode character in a shipped doc (house ASCII rule)
- Tool: cc-playwright. `LINKEDIN_POSTING.md:137` contains U+2026 (horizontal ellipsis).
  Replace with `...`. This is the only ASCII-rule violation found across all nine tools;
  consider adding a guard test that scans shipped tool output and docs for non-ASCII.

### L2. cc-pdf launches Chrome without a dedicated user-data-dir
- Tool: cc-pdf. `pdf_converter.py:90-102` adds no `--user-data-dir`. Headless
  `--print-to-pdf` against an in-use default profile can fail unpredictably on Windows. Add
  a throwaway temp profile dir alongside the temp HTML.

### L3. `pygments` is a declared dependency but unused; syntax highlighting is claimed but absent
- Tools: cc-pdf, cc-html, cc-word. `pygments>=2.17.0` is in all three pyproject files but
  never referenced, while `tools/cc_shared/markdown_parser.py:29` advertises "syntax
  highlighting" and `:39-47` builds `MarkdownIt` with no highlight callback. Either wire
  Pygments in (and ship its CSS) or drop the dependency and the docstring claim.

### L4. cc-word generates CSS it then discards; `--css` flag drift
- Tool: cc-word. `cli.py:133-138` generates theme CSS but `convert_to_word`
  (`word_converter.py:130-142`) re-derives the theme by name and never reads the CSS, so the
  whole CSS pipeline is dead work. cc-pdf and cc-html expose `--css` on from-markdown;
  cc-word does not. Drop the unused CSS generation and document that Word styling is
  theme-name-only.

### L5. cc-vault `init <path>` ignores its path argument
- Tool: cc-vault. `init` (`cli.py:160-185`) computes `vault_path = Path(path)` and prints
  "Vault initialized at: <path>" but `ensure_directories()` uses the module config from
  `CcStorage.vault()` (`config.py:55-57`); the argument and `--force` do nothing. Either
  honor the argument or remove it and document that the path is fixed (overridable only via
  `CC_VAULT_PATH`).

### L6. cc-vault documented `config.json` vault override is dead and the documented path is wrong
- Tool: cc-vault. README (`README.md:33-41,171`) and `VAULT_FIX_PLAN.md:19` say the vault is
  at `%LOCALAPPDATA%\cc-myvault` and settable via `config.json` `vault_path`. The real path
  is `%LOCALAPPDATA%\cc-director\vault`, and `get_vault_path()` never reads `config.json`
  (only `CC_VAULT_PATH` overrides). Correct the docs and either wire up the override or drop
  the dead `save_config` write.

### L7. cc-vault `VAULT_FIX_PLAN.md` describes a replaced ChromaDB design
- Tool: cc-vault. The plan is written around ChromaDB, but the implementation now uses native
  SQLite vectors (`vectors.py`, `db.py:679-691`). Most of its phases are already done. Mark
  it resolved or archive it so reviewers do not chase non-existent code.

### L8. cc-vault double Metaphone is actually single Metaphone
- Tool: cc-vault. `fuzzy_search.py:149` computes `p, s = jellyfish.metaphone(word),
  jellyfish.metaphone(word)`, the same call twice, so primary always equals secondary despite
  the docstring claiming Double Metaphone. Sound-alike recall is weaker than advertised; use a
  real double-metaphone or correct the docs.

### L9. cc-vault `semantic_search` swallows per-collection errors to empty results
- Tool: cc-vault. `vectors.py:671-676` catches every exception per collection, logs at DEBUG,
  and returns `[]`, so a corrupt collection looks identical to "no matches". Log at WARNING
  with the exception class.

### L10. cc-playwright Windows profile-lock pre-check is effectively a no-op
- Tool: cc-playwright. `_looks_locked` (`cli.py:195-205`) only detects POSIX singleton lock
  files; on Windows (the primary platform) a busy user-data-dir is not detected, so the
  process waits the full fifteen seconds before the timeout error (`cli.py:401-407`). Probe
  the debug port early or detect a running Brave on that dir and surface the "close
  cc-browser first" hint immediately.

### L11. cc-gmail/cc-outlook reply-all is incomplete
- Tool: cc-gmail. IMAP `create_reply_draft` reply-all appends only original `To`, never `Cc`,
  and never removes the sender's own address (`imap_client.py:891-897`); the API path is
  similar (`gmail_api.py:342-349`). Include Cc and exclude the user's own address.

### L12. Cross-tool surface inconsistencies between cc-gmail and cc-outlook
- Tools: cc-gmail, cc-outlook. Same operations use different names: folder selector is
  `-l/--label` (gmail) versus `-f/--folder` (outlook); gmail has `labels`/`untrash`, outlook
  has `folders`/`unarchive`; gmail has a `contacts` subcommand, outlook does not; gmail `auth`
  has `--method`/`--no-browser`, outlook `auth` does not. Run a deliberate parity pass so
  equivalent operations share names and flags.

### L13. cc-comm-queue smaller issues
- Tool: cc-comm-queue. `list --status <bad>` silently lists everything because
  `status_map.get(...)` returns `None` meaning "all" (`cli.py:490`); error out on an unknown
  status. `error`-status items are invisible in `status` and stats (`queue_manager.py:104-110`,
  `cli.py:559`) because `QueueStats.error` is never populated. `migrate` tries `from migrate`
  before `from .migrate`, the reverse of every other import (`cli.py:1249`), and
  `migrate.get_default_content_path` reads dead paths (`migrate.py:32-45`).

### L14. cc-devthrottle smaller issues
- Tool: cc-devthrottle. `schedule enable/disable` does a read-modify-write PUT of the whole
  job (`schedule_ops.py:133-136`), which is racy and round-trips server-computed fields; use a
  dedicated enable endpoint or send only mutable fields. `settings set` boolean coercion
  treats any value not in `("true","1","yes")` as `False` (`settings_ops.py:82-84`), so a typo
  silently sets `False`. `selftest` is Windows-only (`session_ops.py:294,324` hardcode cmd.exe
  syntax). Exit codes are inconsistent (`run_setup_cli` uses exit 2 for a bad `--role` while
  the rest use 1).

### L15. Document tools lack a quiet or machine-readable mode and per-tool READMEs
- Tools: cc-pdf, cc-html, cc-word. All progress goes to stdout via Rich color markup with no
  `--quiet` or `--json`, which is noise for scripted use, and there are no per-tool README
  files (usage lives only in `--help` and the global CLAUDE.md). Add a consistent `--quiet`
  (and optionally `--json`) and short per-tool READMEs. Exit-code conventions are otherwise
  consistent and correct across the three.

### L16. Miscellaneous correctness and UX nits
- cc-pdf/cc-html/cc-word HTML `<title>` is not escaped (`cc_shared` template,
  `html_generator.py:53,61-65`); a heading with `&`, `<`, or `>` yields a malformed title.
- cc-html to-markdown image map keys on `original_name`
  (`cc_shared/image_extractor.py:91-94`), so two images with the same name collide.
- cc-gmail double-prints most errors (`logger.error` to stderr plus `console.print` to
  stdout, for example `cli.py:907-910`).
- cc-outlook `accounts remove` returns exit 0 on failure (`cli.py:271-274`), `get_profile`
  returns `mailbox.main_resource` (often `'me'`) as the email (`outlook_api.py:38-41`),
  `read --raw` is declared but unimplemented (`cli.py:396`), and `get_free_busy`/
  `forward_event` call `.status_code` on a falsy response, masking the real error
  (`:1053-1054,1112-1113`).
- cc-playwright `screenshot` defaults output to the tool state dir rather than the
  configured screenshots location (`cli.py:613`).

---

## Things checked and found correct

- ASCII house rule: every Python source in all nine shipped tools scanned clean for
  emoji/unicode; cc-gmail/cc-outlook even actively strip non-ASCII from rendered message
  content (`utils.py sanitize_text`). The only violation anywhere is L1.
- cc-devthrottle Director discovery is clean and fails clearly: it uses `CC_DIRECTOR_API`/
  `CC_SESSION_ID` env vars (not port scanning) and raises a user-facing `DirectorError` with
  the URL on connection failure (`director.py:24-30,87-90`).
- cc-comm-queue schema migrations are idempotent (ALTER-ADD-COLUMN swallowing only
  `OperationalError`, plus an idempotent backfill) (`database.py:139-211`).
- cc-vault optional converters fail loudly with a clear `ImportError` when an optional
  dependency is missing (`converters.py:23-31`), which is correct no-fallback behavior; both
  cc-vault and cc-playwright implement `--version` correctly for the installer health check.
- cc-gmail App Passwords are stored in the OS credential manager via keyring, not on disk
  (`auth.py:139-146`); no client secrets are committed for either email tool.
- cc-devthrottle `config.save()` deep-merges so it does not clobber config keys owned by the
  desktop app, and refuses to overwrite corrupt JSON (`config.py:452-474`).
- PyInstaller path handling (`pyi_rth_paths.py`, the `sys._MEIPASS` handling in each
  `main.py`, and the dual relative/absolute import guards) is reasonable across the suite,
  though the import-bootstrap is duplicated in many files and could be centralized.

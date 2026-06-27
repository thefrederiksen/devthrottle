# Shipped Tools Review - Consolidated Candidate Issues

Synthesis of three independent reviews (Claude, Codex, Pi) of the cc-* tools that ship with DevThrottle (registry.json `ship:true`: cc-pdf, cc-html, cc-word, cc-gmail, cc-outlook, cc-playwright, cc-vault, cc-comm-queue, cc-devthrottle).

Confidence key: **[C]** Claude, **[X]** Codex, **[P]** Pi. More markers = stronger consensus.
Status column tracks our one-at-a-time review (pending / issue filed #NNN / declined).

## High priority

| # | Candidate issue | Tool(s) | Flagged by | Status |
|---|---|---|---|---|
| 1 | BCC recipients leaked to all recipients over SMTP (Bcc header transmitted) | cc-gmail | C | pending |
| 2 | IMAP UID namespace mix - read/mark/delete can hit the WRONG message (App-Password path) | cc-gmail | C | pending |
| 3 | `--json` output corrupted: JSON printed through Rich line-wraps at 80 cols, breaks json.load for long paths/URLs | cc-devthrottle, cc-comm-queue | C (+ X,P note broader json inconsistency) | pending |
| 4 | Runtime output not ASCII-only: Rich tables/panels emit Unicode box-drawing | most Rich/Typer tools | X, P | pending |
| 5 | Packaging: `cc-outlook` imports `msal` but pyproject omits it (only in requirements.txt) -> bundle can ship without it | cc-outlook | X, C | pending |
| 6 | Packaging: `cc-comm-queue` dist name (`cc_comm_queue`) vs registry name mismatch can drop its unique dep (pydantic) from the wheelhouse | cc-comm-queue | P | pending |
| 7 | `config set` writes to a path nothing reads (`~/.cc-director/config.json` vs the real config store) | cc-comm-queue | C, X | pending |
| 8 | Approval gate bypass: `mark-posted` (and log-to-vault) accept any status; agent can skip pending_review/approved | cc-comm-queue | C | pending |
| 9 | `from-markdown` drops ALL inline formatting (bold/italic/links/code) and ALL images in DOCX | cc-word | C, X | pending |
| 10 | `--page-size` and `--margin` are accepted but silently ignored (a4 default actually prints Letter) | cc-pdf | C | pending |
| 11 | Only finds Chrome/Chromium - not Edge or Brave (house default browser); fails on Edge-only machines | cc-pdf | C | pending |
| 12 | `--html` flag is a silent no-op; plain text actually sent as HTML | cc-outlook | C | pending |

## Medium priority

| # | Candidate issue | Tool(s) | Flagged by | Status |
|---|---|---|---|---|
| 13 | Standardize the machine-output contract: `--json` on all read commands, plain stdout for JSON, human text to stderr, stable exit codes (consolidates `--json`/`--format json` split) | all | C, X, P | pending |
| 14 | `cc-vault init <path>` ignores its path argument - inits the default location instead | cc-vault | C, X | pending |
| 15 | `cc-vault` tasks: complete sets `done` but stats/filter use `completed` -> stats always 0, `--status completed` matches nothing | cc-vault | C | pending |
| 16 | `cc-comm-queue` silently defaults invalid status/visibility/audience instead of rejecting | cc-comm-queue | P, C | pending |
| 17 | `cc-playwright` browser discovery brittle: two fixed Brave paths, no override/which/Edge/Chrome/macOS | cc-playwright | X, C | pending |
| 18 | `cc-vault lists --query` injects a raw user SQL WHERE clause via f-string | cc-vault | X | pending |
| 19 | Shared SQLite opened without WAL/busy_timeout (CLI + desktop app contend) -> "database is locked" | cc-comm-queue, cc-vault | C | pending |
| 20 | OAuth/token caches written in plaintext with no 0600 perms (POSIX fallback world-readable) | cc-gmail, cc-outlook | C | pending |
| 21 | `cc-devthrottle actions` metadata (agent discovery surface) has drifted from the real commands | cc-devthrottle | C | pending |
| 22 | `cc-comm-queue list` caps at 100 and misreports the total ("Showing 100 of 100" with 120 items) | cc-comm-queue | C | pending |
| 23 | Unexpected errors dump raw tracebacks (cc-playwright breaks its own `{error}` JSON contract) | cc-pdf, cc-html, cc-word, cc-vault, cc-playwright, cc-devthrottle | C | pending |
| 24 | `cc-outlook` calendar: naive datetimes shift times by local offset; 250-event cap; truncated IDs that workflows must consume | cc-outlook | C | pending |
| 25 | `cc-devthrottle schedule` calls the Gateway directly (contradicts the "Director-only" contract; can be unauthenticated) | cc-devthrottle | C | pending |
| 26 | Stray personal scripts committed in shipped cc-gmail tree (shell out to `claude -p`, silent fallbacks) | cc-gmail | C | pending |

## Low priority / cross-cutting

| # | Candidate issue | Tool(s) | Flagged by | Status |
|---|---|---|---|---|
| 27 | Add a shipped-tool smoke/guard matrix from registry.json: offline bundle install, `--version`/`--help`, ASCII-only assert, json-parse, command-surface snapshot | all | C, X, P | pending |
| 28 | Standardize `--count`/`-n` (drop/alias `--limit`) and version string format across tools | all | P, X, C | pending |
| 29 | Document converters: add `--quiet`/`--force`/`--no-clobber`; warn on missing/unreadable images instead of silent skip | cc-pdf, cc-html, cc-word | P, C, X | pending |
| 30 | `cc-playwright`: `snapshot --full` is a no-op; `stop` uses forceful taskkill (profile-lock risk); Windows lock pre-check is a no-op | cc-playwright | P, C | pending |
| 31 | Stale docs: cc-vault README/VAULT_FIX_PLAN reference replaced ChromaDB design and wrong paths; one Unicode ellipsis in cc-playwright LINKEDIN_POSTING.md | cc-vault, cc-playwright | C | pending |
| 32 | Misc nits: unescaped HTML `<title>`, cc-gmail double-printed errors, cc-outlook `accounts remove` exits 0 on failure / `read --raw` unimplemented, pygments declared-but-unused | several | C | pending |

---

Source reports: `shipped-tools-review-claude.md`, `shipped-tools-review-codex.md`, `shipped-tools-review-pi.md` (same directory).

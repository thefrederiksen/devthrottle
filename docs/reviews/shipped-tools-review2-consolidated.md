# Shipped Tools - Second Review Pass - Consolidated

A fresh review of the nine shipped tools AFTER round one's 32 fixes shipped (v0.9.22/v0.9.23). Goal: remaining or new issues, and incomplete round-one fixes. Confidence: **[C]** Claude, **[P]** Pi. (Codex did not complete its pass.)

Both reviewers confirmed the round-one fixes held up (Bcc envelope-only, X-GM-MSGID, page-size, browser discovery, ASCII boxes/ellipsis, WAL, status state machine, parametrized SQL). The items below are NEW or incompletely-fixed.

## High priority

| # | Finding | Tool | By |
|---|---|---|---|
| 1 | `update_status/update_content/update_recipient` return cumulative `conn.total_changes`, not `cursor.rowcount` - editing a MISSING ticket reports success (silent failure; guaranteed once any write/migration ran) | cc-comm-queue | C |
| 2 | Round-one `--json` plain-print fix was NOT applied to cc-vault: ~10 sites still `console.print(json.dumps(...))` (cli.py 2107/2169/3406/3443/3477/3534/3586/3635/3642/3668) - long values still corrupt JSON | cc-vault | P |
| 3 | `contacts add --role` is silently dropped (`role` not in `add_contact` valid_fields) - data loss; `contacts edit` uses `--title`, so they disagree | cc-vault | C |
| 4 | Duplicate `_sanitize_fts_query` (db.py 4172 and 6030) - the wrong one wins; FTS errors swallowed to `[]` for queries with operator chars | cc-vault | C |
| 5 | Non-ASCII/spaced attachment filename corrupts `Content-Disposition` (whole header RFC2047-encoded) - attachment breaks | cc-gmail | C |

## Medium priority

| # | Finding | Tool | By |
|---|---|---|---|
| 6 | Several JSON paths still `ensure_ascii=False` / write UTF-8 user data - runtime output not ASCII even though source scans clean | cc-vault, cc-gmail, cc-outlook | P, C |
| 7 | `catalog._extract_text` / `_embed_summary` catch-all and lose the real error (records only "No text extracted"; embed failure silent) | cc-vault | P |
| 8 | API send path blobs the To/Cc header with a non-ASCII display name (reply --all to an accented participant) - send can fail/misroute | cc-gmail | C |
| 9 | `subprocess.TimeoutExpired` (wedged headless Chrome) is uncaught - raw traceback instead of clean error | cc-pdf | C |
| 10 | Multi-line fenced code block collapses to one line in DOCX (no `add_break` between lines) | cc-word | C |
| 11 | `setup update` / `setup repair` lack the try/except guard `setup install` has - raw traceback on GitHub 5xx/rate-limit *(note: setup_ops.py is in the concurrently-refactored area)* | cc-devthrottle | C |
| 12 | Split error contract: some failures `SystemExit("plain text")`, others `{"error": ...}` JSON - consumers cannot rely on one shape | cc-playwright | C |
| 13 | `start`/`stop` trust a recorded PID with no identity check - PID reuse can refuse to start or kill an innocent process | cc-playwright | C |
| 14 | `requirements.txt` omits `keyring`/`cc-storage` that the code imports (pyproject is correct) | cc-gmail | C |
| 15 | `cc_shared.__init__` eagerly imports `.config`/`.llm` (-> cc_storage), coupling the doc tools that never use them; survives only via silent import fallbacks | cc_shared / cc-pdf,html,word | C |

## Low priority (selected)

| # | Finding | Tool | By |
|---|---|---|---|
| 16 | IMAP archive/delete use mailbox-wide `expunge()` - can remove OTHER already-`\Deleted` messages; use UID EXPUNGE | cc-gmail | C |
| 17 | `X-GM-RAW "{query}"` not escaped - a `"` breaks/injects the IMAP search | cc-gmail | C |
| 18 | SVG data-URI images never extracted (`data:image/(\w+)` cannot match `svg+xml`) | cc-html | C |
| 19 | `recipients` uses `--format json` (not `--json`) and `ensure_ascii=False` - inconsistent + ASCII violation | cc-outlook | C |
| 20 | No Windows UTF-8 stdout wrapper (cc-gmail has one) - `UnicodeEncodeError` risk on cp1252 console | cc-outlook | C |
| 21 | `search` semantic mode hardcodes `items[:5]`, ignoring `-n` | cc-vault | C |
| 22 | Ticket-number allocation (`MAX+1`) not atomic - concurrent add can spuriously fail | cc-comm-queue | C |
| 23 | `status` column has no CHECK constraint; unknown status passes the transition guard | cc-comm-queue | C |
| 24 | `actions` metadata for `schedule-create` still drifts (synthetic arg names) *(held: cli.py is concurrently refactored)* | cc-devthrottle | C, P |
| 25 | `director.py` docstring still says "never needs Gateway URL/token" but schedule calls the Gateway directly | cc_shared | C |
| 26 | cc-pdf/cc-word still use collision-prone `path_map.get(original_name)` (round-one fixed cc-html only); forced re-run accumulates `_images/` dupes | cc-pdf, cc-word | C |
| 27 | Misc: cc-outlook `forward_message` plain-text note into HTML body; leftover empty `test/` dirs in cc-gmail/cc-outlook; cc-playwright hardcodes "Brave" in messages; Windows lock pre-check omits `chromium.exe` | several | C |

---

Source reports: `shipped-tools-review2-claude.md` (detailed, file:line verified), `shipped-tools-review2-pi.md`. Codex pass incomplete.

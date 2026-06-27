# Shipped Tools - Fix Plan

Fix every item in `docs/reviews/shipped-tools-review-consolidated.md` (32 candidates from the Claude/Codex/Pi reviews), add tests, redeploy, and email proof. No GitHub issues - this doc is the plan and tracker.

## Execution rules
- ASCII-only output everywhere; fix Rich Unicode box-drawing (safe_box / box.ASCII).
- No silent fallbacks - fix root causes, fail with clear messages (per CLAUDE.md / docs/CodingStyle.md).
- Add a regression test for each behavioral bug fix; run each tool's pytest.
- Shared working tree: each work package owns DISJOINT directories. No package edits another's files.

## Work packages (parallel, by disjoint directories)

### Package A - Document tools  (owns: tools/cc-pdf, tools/cc-html, tools/cc-word, tools/cc_shared)
- #9 cc-word: real inline rendering (bold/italic/links/inline-code) + images + nested lists.
- #10 cc-pdf: apply --page-size and --margin via a dynamic @page rule.
- #11 cc-pdf: find Edge/Brave + shutil.which + env override; use %LOCALAPPDATA%.
- #29 + L-asset: --quiet/--force/--no-clobber; warn on missing/unreadable images (no silent skip).
- L2 pdf temp user-data-dir; L3 pygments wire-or-drop; L4 cc-word dead CSS; L16 title escaping + cc-html image-name key collision.
- Fold in: ASCII output, add --json where a read command lacks it, consistent --quiet.

### Package B - Email tools  (owns: tools/cc-gmail, tools/cc-outlook)
- #1 cc-gmail: stop leaking Bcc over SMTP (envelope-only).
- #2 cc-gmail: IMAP read/mark/delete operate in the same mailbox / key on X-GM-MSGID.
- #5 cc-outlook: add msal to pyproject dependencies.
- #12 cc-outlook: --html sets body_type correctly.
- #20 token caches chmod 0600 on POSIX.
- #24 cc-outlook calendar: timezone-correct datetimes, paginate beyond 250, emit full IDs.
- #26 remove stray personal scripts from cc-gmail tree.
- L11 reply-all (Cc + exclude self); L12 cheap gmail/outlook flag parity; L16 nits (double-printed errors, accounts remove exit code, read --raw, get_profile email, get_free_busy status_code).

### Package C - Queue + Vault  (owns: tools/cc-comm-queue, tools/cc-vault; must NOT touch cc_shared)
- #6 cc-comm-queue: fix dist-name vs registry mismatch so its unique dep ships.
- #7 cc-comm-queue: config set writes through the shared config store (the one config show reads).
- #8 cc-comm-queue: enforce status transitions (mark-posted/log-to-vault cannot bypass approval; --force to override).
- #16 strict validation (reject unknown status/visibility/audience).
- #22 list honors -n and reports the true total.
- #3(part) cc-comm-queue: print JSON with plain print(json.dumps), not Rich.
- #14 cc-vault init honors its path; #15 done/completed alignment; #18 --query parametrized/structured.
- #19 WAL + busy_timeout on both DBs; L6/L7 vault docs; L8 double-metaphone; L9 semantic error logging; L13 queue nits.

### Package D - Browser  (owns: tools/cc-playwright)
- #17 browser discovery: env override + shutil.which + Edge/Chrome/Brave + macOS paths + clear error.
- #30 snapshot --full implement-or-remove; stop graceful-then-force; real Windows lock pre-check.
- L1 unicode ellipsis in LINKEDIN_POSTING.md; screenshot default -> configured screenshots location.

### Package E - cc-devthrottle (owner: orchestrator; owns: tools/cc-devthrottle/src/session_ops.py, settings_ops.py, schedule_ops.py ONLY)
- #3(part) plain print(json.dumps) in session_ops/settings_ops/schedule_ops.
- #23 settings set int/float/bool guards.
- #25 schedule auth/contract note.
- HOLD (collides with a concurrent session's uncommitted cli.py/setup_ops.py refactor): #21 actions drift, anything in cli.py/setup_ops.py.

### Cross-cutting (orchestrator, after packages land)
- #13 machine-output contract: ensure read commands have --json, JSON to stdout, human text to stderr.
- #27 shipped-tool smoke/guard matrix from registry.json (offline bundle: --version/--help/ASCII/json).
- #28 standardize --count/-n and version-string format.

## Then
1. Integrate, run all tool test suites, commit per package.
2. Cut release (vNext) and let CI build.
3. Install from the release, verify a sample of fixes live, capture proof/screenshots.
4. Write an HTML QA report (before/after) and email it.

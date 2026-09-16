# Worker brief - phase 2, the smallest cc-dev-reports

You are a Worker on the Dev Reports mission. Your Manager is session 2ba644bd. Report to it, once, when done
or genuinely blocked (`cc-devthrottle message send 2ba644bd "<one line>"`). Never contact the owner.

**Your worktree:** `D:\ReposFred\devthrottle-dev-reports-p2-tool`, branch `dev-reports/p2-tool` (cut from
`mission/dev-reports`). Work there only. Commit and push to that branch as you go. No attribution of any kind in
commits. Never merge to main, never open a pull request. Never touch another worktree. Never stop or restart any
Director.

## Read first (in your worktree)

1. `docs/missions/dev-reports/PLAN-phase-2.md` - the routes and shapes (section "Session routes" and "The
   smallest tool"). The Gateway side is being built IN PARALLEL by another Worker; the plan is the contract
   between you. If it is ambiguous, message the Manager - do not invent.
2. `docs/axi-standard.md` - the command-line standard this tool follows.
3. `tools/cc-devthrottle` - copy its packaging (pyproject, `main.py`, typer app, tests layout), its way of
   reading `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY` and `CC_SESSION_ID`, its HTTP timeouts, its `--json`
   handling, and how it is installed/registered as a managed tool (grep the repo for how `cc-devthrottle` and
   `cc-ship` get onto PATH and into the tool registry and installer - `tools/cc-ship` was added days ago and is
   the freshest example; do the same for `cc-dev-reports`).

## What to build: `tools/cc-dev-reports`

- `cc-dev-reports open <file>` - read the file as UTF-8, POST `/sessions/{CC_SESSION_ID}/dev-reports` with
  `{ key: <absolute path, lower-cased on Windows>, html }` using the session key. On 422 print every shape-check
  error, one per line, and exit 1. On 413 print the size error and exit 1. On success print report id, version,
  title, status, and that the owner reads it in the Reports view (phase 3) - plus the owner route
  `/dev-reports/<id>`. A file larger than 10 MB is refused locally before sending, with the same words.
- `cc-dev-reports reply "<text>" [--report <id>]` - POST the reply. Without `--report`, use the session's most
  recently updated report (`GET /sessions/{sid}/dev-reports`); when it has none, error "this session has no dev
  report yet - run cc-dev-reports open <file> first", exit 1.
- `--json` on both keeps one stable shape. Unknown flag -> error, non-zero exit. Missing environment
  (no session key / URL / session id) -> a clear error naming the variable. Every HTTP call has a timeout; nothing
  waits forever. ASCII only in output.

## Proof required

- pytest tests with the HTTP layer faked: success output, every-error output on 422, 413, local size refusal,
  reply picks the newest report, reply with no report, unknown flag fails, missing environment fails, timeout is
  set on every request (assert it), `--json` shape.
- Run the tool's tests and paste the count. If the Gateway Worker's branch has landed routes by the time you
  finish, you do NOT need to run against a live Gateway - the Manager does the end-to-end run.

## Done means

Committed and pushed on `dev-reports/p2-tool`, with `docs/missions/dev-reports/WORKER-phase-2-tool.md`: files,
commands with real sample output, tests and counts, what is not proven. Then ONE line to the Manager.

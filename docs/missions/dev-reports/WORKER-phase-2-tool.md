# Worker report - phase 2, the cc-dev-reports tool

Worker session 40490db1, branch `dev-reports/p2-tool`. Brief: `BRIEF-phase-2-tool-worker.md`.

## Files

| File | What |
|---|---|
| `tools/cc-dev-reports/src/reports_ops.py` | `open_report`, `reply`, and the renderer. One result dictionary per command, same keys whatever happened |
| `tools/cc-dev-reports/src/cli.py` | Typer app: `open <file> [--json]`, `reply "<text>" [--report <id>] [--json]` |
| `tools/cc-dev-reports/main.py`, `pyproject.toml`, `requirements.txt`, `src/__init__.py` | Packaging, copied from `cc-devthrottle` (package `cc_dev_reports` from `src`, script `cc-dev-reports`) |
| `tools/cc-dev-reports/install.py`, `README.md` | Launcher onto PATH from a checkout, copied from `cc-ship` (adds a check that typer is importable) |
| `tools/cc-dev-reports/tests/test_cli.py` | 24 tests |
| `tools/cc_shared/gateway.py` | `GatewayError` now also carries `body`, the parsed JSON of a refused answer (None when absent or not JSON). Needed so the 422's `errors` list reaches the tool; the sentence alone dropped it |
| `tools/cc_shared/tests/test_gateway.py` | 2 tests for `body` |
| `tools/registry.json` | `cc-dev-reports`, category fleet, python, `ship: false` - the same registration `cc-ship` got |
| `docs/cli-reference.md` | A `cc-dev-reports` section |

## Decisions made inside the mandate

- Size limit: 10,485,760 bytes of the UTF-8 file, exactly that many allowed; the local refusal prints the
  Gateway's 413 sentence word for word (Manager ruling, message of 2026-09-16).
- Timeout: 30 seconds per request, one constant `HTTP_TIMEOUT_SECONDS`, passed explicitly on every call.
- `reply` without `--report` takes `reports[0]` of `GET /sessions/{sid}/dev-reports`, because the plan says that
  list is newest update first. The tool does not re-sort (the Gateway owns the ordering).
- `--json` keys, always all present: `ok`, `command`, `report`, `created`, `reply`, `ownerRoute`, `error`,
  `code`, `errors`. On `reply` success `report` is `{ "id": ... }` only.
- Error codes: the Gateway's `code` when its answer has one (`shape_check_failed`, `report_too_large`, ...);
  otherwise `http_<status>`, or `gateway_error` when no answer came. Local: `missing_environment`,
  `file_not_found`, `not_utf8`, `report_too_large`, `no_report`, `bad_answer`.
- Exit codes: 0 success, 1 any refusal, 2 a usage error (unknown flag - Typer's own).
- Not installer-shipped (`ship: false`), like `cc-ship`. Putting it in the installer (`build-all-tools.ps1`,
  `tools-manifest.json`, `ship: true`) is a later call.

## Tests

```
python -m pytest tools/cc-dev-reports                       -> 24 passed
python -m pytest tools/cc-dev-reports tools/cc_shared/tests/test_gateway.py -> 87 passed
python -m pytest tools/test_shipped_tools_contract.py       -> 40 passed
python -m pytest tools/cc-devthrottle                       -> 351 passed, 9 failed
```

The 9 `cc-devthrottle` failures are all in `test_session_list_axi.py` and fail identically on the base commit
828eb0482 without this change (checked in a separate detached worktree) - not caused here.

The HTTP fake sits at `gateway._OPENER.open`, so path, body, bearer key, timeout and error parsing are the real
code. Covered: success output; every error printed one per line on 422; 413; local refusal over the limit with
nothing sent; exactly-at-limit is sent; missing file; reply picks the newest; reply with `--report`; reply with no
report; unknown flag on both commands and the root; each of the three missing variables on both commands; the
timeout reaches the socket on every request (patched to 7.25 so a default cannot pass it); `--json` key set on
success and failure of both commands; quotes stay readable and non-ASCII is escaped; a multi-line Gateway sentence
stays on separate lines.

Revert proofs (each went red, then restored and green):
- Dropped `timeout=` from the reply post -> the timeout test failed.
- `GatewayError.body` not filled -> the 422, 413 and `--json` tests failed (code fell to `http_422`, errors lost).
- Local size check disabled -> the local refusal test failed.

## Sample output (real runs of `python tools/cc-dev-reports/main.py` against a loopback stand-in server)

```
$ cc-dev-reports open <scratch>/good.html
published:
  id: 7d3c1e8a-0000-4000-8000-000000000001
  version: 2
  title: Phase 2 report
  status: waiting-on-you
  created: false
owner:
  reads it in the Reports view (arrives in phase 3)
  route: /dev-reports/7d3c1e8a-0000-4000-8000-000000000001
help[2]:
  cc-dev-reports reply --report 7d3c1e8a-0000-4000-8000-000000000001 "<text>"
  cc-dev-reports open <file>
(exit 0)

$ cc-dev-reports open <scratch>/bad.html
error: The report failed the shape check (2 error(s)). Nothing was published.
code: shape_check_failed
errors[2]:
  The report has no header marker.
  Question "deploy" has two recommended options, keep one.
help[1]:
  cc-dev-reports open <file>
(exit 1)

$ cc-dev-reports open <scratch>/huge.html
error: This report is 10485761 bytes. A dev report can be at most 10485760 bytes (10 megabytes).
code: report_too_large
(exit 1)

$ cc-dev-reports reply Updated the numbers, see version 2.
replied:
  report: 7d3c1e8a-0000-4000-8000-000000000001
  id: r-1
  at: 2026-09-16T12:00:00Z
  route: /dev-reports/7d3c1e8a-0000-4000-8000-000000000001
(exit 0)

$ cc-dev-reports reply x --json
{
 "ok": true,
 "command": "reply",
 "report": {
  "id": "7d3c1e8a-0000-4000-8000-000000000001"
 },
 "created": null,
 "reply": {
  "id": "r-1",
  "text": "x",
  "at": "2026-09-16T12:00:00Z"
 },
 "ownerRoute": "/dev-reports/7d3c1e8a-0000-4000-8000-000000000001",
 "error": null,
 "code": null,
 "errors": []
}
(exit 0)

$ cc-dev-reports open <scratch>/good.html --bogus
Usage: cc-dev-reports open [OPTIONS] FILE
Try 'cc-dev-reports open --help' for help.
+- Error ---------------------------------------------------------------------+
| No such option: --bogus                                                     |
+-----------------------------------------------------------------------------+
(exit 2)

$ cc-dev-reports reply x
error: CC_SESSION_ID is not set, so this command cannot tell which session it is running in. cc-dev-reports only works inside a DevThrottle session.
code: missing_environment
(exit 1)

```

## What is not proven

- Not run against the real Gateway routes: they are being built in parallel. The stand-in server answers the
  shapes in `PLAN-phase-2.md`; if the Gateway's 422 body, error code names or list order differ from the plan, the
  tool will print what it receives but tests would not have caught the mismatch. The Manager's end-to-end run is
  the proof.
- The installer launcher (`install.py`) was not run on this machine (it writes to `~/.local/bin`).
- Not run on macOS or Linux. The key lower-cases only on Windows; the non-Windows branch is untested here.
- The `pip install` packaging path (`project.scripts`) was not exercised; `main.py` from a checkout was.

# cc-dev-reports

A session publishes a dev report - one HTML file - to the owner through the Gateway, and replies
to the owner inside it (Dev Reports mission, phase 2). The Gateway owns every ruling: the shape
check, the title, the status, the size limit, and which report is newest.

```
cc-dev-reports open <file> [--json]                    # publish; again = a new version
cc-dev-reports reply "<text>" [--report <id>] [--json] # default: this session's newest report
```

- The report key is the file's absolute path, lower-cased on Windows, so publishing the same file
  again makes a new version of the same report.
- A shape-check refusal prints every error, one per line, and exits 1. Nothing is published.
- A file over 10485760 bytes is refused before anything is sent, with the Gateway's own sentence.
- `--json` always has the same keys: `ok`, `command`, `report`, `created`, `reply`, `ownerRoute`,
  `error`, `code`, `errors`.
- Needs `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY` and `CC_SESSION_ID` (every DevThrottle session
  has them). A missing one is named. Every request times out after 30 seconds.
- Unknown flags fail. Output is ASCII.

The owner reads a report in the Reports view, which arrives in phase 3; until then the owner route
is `/dev-reports/<id>`.

## Install

Not in the installer yet. From a checkout that follows origin/main:

```
python tools/cc-dev-reports/install.py
```

## Tests

```
python -m pytest tools/cc-dev-reports
```

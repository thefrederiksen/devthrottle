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
  `error`, `code`, `errors`. `ownerRoute` carries the same whole address the human output prints.
- Needs `CC_GATEWAY_URL`, `CC_GATEWAY_SESSION_KEY` and `CC_SESSION_ID` (every DevThrottle session
  has them). A missing one is named. Every request times out after 30 seconds.
- Unknown flags fail. Output is ASCII.

## The address the owner clicks

Both commands print ONE address, whole, and it is the only one anyone needs:

```
<CC_GATEWAY_URL>/r/<report id>
```

The Gateway routes it by the device that opened it - a phone lands on the phone's report screen, anything
else on the Cockpit's Reports tab - and in both cases it lands INSIDE that report, not on a list. Signed
out, the sign-in round trip ends on the same report.

The report id is printed in full. A shortened identifier is not something an agent can act on.

## Install

Not in the installer yet. From a checkout that follows origin/main:

```
python tools/cc-dev-reports/install.py
```

## Tests

```
python -m pytest tools/cc-dev-reports
```

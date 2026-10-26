# cc-dev-reports

A session publishes a dev report - one HTML file - to the owner through the Gateway, and replies
to the owner inside it (Dev Reports mission, phase 2). The Gateway owns every ruling: the shape
check, the title, the status, the size limit, and which report is newest.

```
cc-dev-reports open <file> [--json]                    # publish; again = a new version
cc-dev-reports reply "<text>" [--report <id>] [--json] # default: this session's newest report
cc-dev-reports answers <file> [--json]                 # the owner's stored answers, from any session
cc-dev-reports answers --report <id> [--ack [--item <id>]...] [--json]
```

- The report key is the file's absolute path, lower-cased on Windows, so publishing the same file
  again makes a new version of the same report.
- A shape-check refusal prints every error, one per line, and exits 1. Nothing is published.
- A file over 10485760 bytes is refused before anything is sent, with the Gateway's own sentence.
- `--json` always has the same keys: `ok`, `command`, `report`, `created`, `reply`, `ownerRoute`,
  `count`, `reports`, `marked`, `error`, `code`, `errors`. `ownerRoute` carries the same whole
  address the human output prints.

## Reading the owner's answers in a later run

A factory run publishes its report and closes. The owner answers when he likes, and the Gateway
STORES the answer (status `saved`), because the session it would have been typed into has ended.
The next run - a different session - reads it:

- `answers <file>` sends the same key `open` publishes under and reads every report in the account
  with that key, whichever session published it: the newest, plus each older one still holding an
  answer or note no session has marked acted on. A report is keyed on (session, file), so each run's
  publish is a new report; reading by file is how an answer left on run N's report is not hidden by
  run N+1 publishing again.
- Each question prints the chosen option, the owner's comment and when, or `not answered yet`; each
  note prints with the words it was attached to.
- `--ack` (with `--report`) marks the owner's answers and notes on that report acted on, by this
  session; `--item` marks only the items named. The output lists what this call marked. A later
  answer to the same question is a new item and reads as not acted on.
- A session reads only its own account's reports; in a team's account, only those of the person
  behind the session. Another account's report is `report_not_found`.
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

The DevThrottle installer puts `cc-dev-reports` on your PATH with every other shipped tool.

To run a CHECKOUT instead of the installed copy - developing the tool itself - use
`python tools/cc-dev-reports/main.py ...`, or put a launcher for one checkout on your own
PATH with `install.py`. Keep that launcher out of the folder the installer owns: two
launchers for one name is two sources, and the search path decides which one answers.

## Tests

```
python -m pytest tools/cc-dev-reports
```

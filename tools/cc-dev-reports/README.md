# cc-dev-reports

A session publishes a dev report - one HTML file - to the owner through the Gateway, and replies
to the owner inside it (Dev Reports mission, phase 2). The Gateway owns every ruling: the shape
check, the title, the status, the size limit, and which report is newest.

```
cc-dev-reports open <file> [--json]                    # same session = a new version; later session = a new report
cc-dev-reports reply "<text>" [--report <id>] [--json] # default: this session's newest report
cc-dev-reports answers <file> [--claim] [--json]       # read, or claim before acting
cc-dev-reports answers --report <id> [--claim|--ack] [--item <id>...] [--json]
```

- The report key is the file's absolute path, lower-cased on Windows. Publishing it again from the
  same session makes a new version; publishing from a later session makes a new report. A factory
  seat's key read is scoped to its stable factory and seat identity. Seats can share an absolute path;
  path uniqueness is not a contract.
- A shape-check refusal prints every error, one per line, and exits 1. Nothing is published.
- A file over 10485760 bytes is refused before anything is sent, with the Gateway's own sentence.
- `--json` always has the same keys: `ok`, `command`, `report`, `created`, `reply`, `ownerRoute`,
  `count`, `reports`, `claimed`, `marked`, `error`, `code`, `errors`. `ownerRoute` carries the same whole
  address the human output prints.

## Reading the owner's answers in a later run

A factory run publishes its report and closes. The owner answers when he likes, and the Gateway
STORES the answer (status `saved`), because the session it would have been typed into has ended.
The next run - a different session - reads it:

- `answers <file>` sends the same key `open` publishes under and reads reports for this account and,
  for a factory seat, only reports belonging to that factory and seat. It returns the newest report
  plus every older report still holding an unacted answer or note. It does not cap that list.
- The Gateway stores factory identity on each report when it is published. For an older scheduled
  report without stored identity, it resolves the publishing session through its cron run record.
  This lets different seats safely use the same file path.
- Each question prints the chosen option, the owner's comment and when, or `not answered yet`; each
  note prints with the words it was attached to.
- Factory runs can overlap after the scheduler's start call returns, so read does not reserve answers.
  Run `answers <file> --claim` before acting. The output lists `claimed` item ids owned by this
  session; act only on those ids. If another live session owns an item, its claim is shown and this
  call does not return that id as claimed. A claim can be taken over after its session ends.
- `--claim` with `--report` reserves unacted items on one report. `--item` (repeatable) limits the
  claim to named ids. By file, `--claim` reserves every unacted item on each returned report.
- After the action succeeds, run `answers --report <id> --ack` to mark this session's claimed items
  acted on. `--item` limits the mark to named ids. An unclaimed or other-session item cannot be
  acknowledged by this session. An ack that leaves items unacted because this session does not
  hold their claim (it marked nothing while some remain, or a named `--item` stayed open) exits 1
  with code `not_marked` and lists each one with its holder; an ack with nothing left to mark exits
  0. A later answer to the same question is a new item.
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

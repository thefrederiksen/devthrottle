# CC Director CLI Reference

Auto-generated from `--help` output. Use `<tool> <command> --help` for subcommand details.

---

## cc-browser

Browser automation via Chrome Extension + Native Messaging with persistent connections.

```
USAGE: cc-browser [--connection <name>] <command> [options]

CONNECTION MANAGEMENT:
  connections list                   List all connections
  connections add <name> [--url URL] [--tool TOOL]
                                     Add a new connection
  connections open <name>            Launch Chrome for connection
  connections close <name>           Close Chrome for connection
  connections remove <name>          Delete connection
  connections status                 Show daemon and connection status

BROWSER COMMANDS (require --connection or single active connection):
  navigate --url <url>               Navigate to URL
  snapshot [--interactive] [--compact]
                                     Get page structure with element refs
  info                               Page URL, title, viewport
  text [--selector <css>]            Get text content
  html [--selector <css>]            Get HTML content

INTERACTIONS:
  click --ref <e1>                   Click element by ref
  type --ref <e1> --text "x"         Type into element
  fill --ref <e1> --value "x"        Fill input
  press --key Enter                  Press keyboard key
  hover --ref <e1>                   Hover over element
  scroll [--direction down] [--amount 500]
                                     Scroll viewport
  wait --text "loaded"               Wait for text
  wait --selector ".done"            Wait for selector

SCREENSHOTS:
  screenshot [--type jpeg]           Take screenshot (base64)

TABS:
  tabs                               List all tabs
  tabs/open [--url <url>]            Open new tab
  tabs/close --tab <id>              Close tab

JAVASCRIPT:
  evaluate --fn "() => document.title"

NAVIGATION SKILLS:
  skills list                        List all skills (managed + custom)
  skills show <connection>           Show resolved skill for connection
  skills show <name> --managed       Show a managed skill by name
  skills fork <connection>           Fork managed skill to custom
  skills reset <connection>          Reset to managed skill
  skills learn <connection> "text"   Append learned pattern
  skills learned <connection>        Show learned patterns
  skills clear-learned <connection>  Clear learned patterns

DAEMON:
  daemon                             Start daemon in foreground
  status                             Show daemon status
  install                            Install native messaging host

OPTIONS:
  --connection <name>  Target connection (auto-resolved if single active)
  --port <port>        Daemon port (default: 9280)
  --timeout <ms>       Action timeout
```

---

## cc-comm-queue

CLI for adding content to the Communication Manager approval queue.

```
USAGE: cc-comm-queue [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  add       Add content to pending_review queue
  add-json  Add content from JSON file or stdin
  list      List content items in the queue
  status    Show queue status and counts
  show      Show details of a specific item
  delete    Delete a content item from the queue
  migrate   Migrate existing JSON files to SQLite
  config    Configuration management
```

### cc-comm-queue add

```
USAGE: cc-comm-queue add PLATFORM CONTENT_TYPE CONTENT

ARGUMENTS:
  PLATFORM      linkedin, twitter, reddit, youtube, email, blog
  CONTENT_TYPE  post, comment, reply, message, article, email
  CONTENT       The actual content text

OPTIONS:
  --persona        -p   Persona: acmeflow, center_consulting, personal [default: personal]
  --destination    -d   Where to post (URL)
  --context-url    -c   What we're responding to (URL)
  --context-title       Title of content we're responding to
  --tags           -t   Comma-separated tags
  --notes          -n   Notes for reviewer
  --created-by          Agent/tool name
  --send-timing    -st  immediate, scheduled, asap, hold [default: asap]
  --scheduled-for       ISO datetime for scheduled send
  --send-from      -sf  Account: acmeflow, personal, consulting
  --media          -m   Path to media file (repeatable)
  --json                Output as JSON (for agents)

  EMAIL-SPECIFIC:
  --email-to            Recipient email address
  --email-subject       Email subject line
  --email-attach        Attachment file path (repeatable)

  LINKEDIN-SPECIFIC:
  --linkedin-visibility  public, connections [default: public]

  REDDIT-SPECIFIC:
  --reddit-subreddit     Target subreddit
  --reddit-title         Reddit post title
```

### cc-comm-queue list

```
USAGE: cc-comm-queue list [OPTIONS]

OPTIONS:
  --status  -s  Filter: pending, approved, rejected, posted
            -n  Max results [default: 20]
```

### cc-comm-queue delete

```
USAGE: cc-comm-queue delete CONTENT_ID [OPTIONS]

ARGUMENTS:
  CONTENT_ID    Ticket number or content ID (can be partial)

OPTIONS:
  --force  -f   Skip confirmation prompt
  --json        Output as JSON (for agents)
```

---

## cc-crawl4ai

AI-ready web crawler: crawl pages to clean markdown.

NOTE: not part of the shipped product (not in the "ship" allowlist in tools/registry.json). Available in the repo and buildable for dev via `scripts/build-all-tools.ps1 -Tool cc-crawl4ai`.

```
USAGE: cc-crawl4ai [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  crawl    Crawl a single URL and extract content
  batch    Crawl multiple URLs in parallel
  session  Manage browser sessions
```

---

## cc-gmail

Gmail CLI: read, send, search, and manage emails.

```
USAGE: cc-gmail [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v
  --account  -a TEXT  Gmail account to use

COMMANDS:
  auth            Authenticate with Gmail
  list            List recent emails from a label/folder
  read            Read a specific email
  send            Send an email
  draft           Create a draft email
  reply           Create a draft reply
  drafts          List draft emails
  search          Search emails (Gmail query syntax)
  count           Count emails matching a query
  labels          List all labels/folders
  delete          Delete/trash an email
  untrash         Restore from trash
  archive         Archive email(s)
  archive-before  Archive all inbox before date
  profile         Show authenticated user profile
  stats           Mailbox statistics dashboard
  label-stats     Stats for a specific label
  label-create    Create a new label/folder
  move            Move email to a label
  accounts        Manage Gmail accounts
  calendar        Google Calendar operations
  contacts        Google Contacts operations
```

### cc-gmail list

```
USAGE: cc-gmail list [OPTIONS]

OPTIONS:
  --label   -l TEXT     Label/folder [default: INBOX]
  --count   -n INTEGER  Number of emails [default: 10]
  --unread  -u          Show only unread
  --include-spam        Include spam and trash
```

### cc-gmail send

```
USAGE: cc-gmail send [OPTIONS]

OPTIONS:
  --to       -t TEXT  Recipient email [required]
  --subject  -s TEXT  Email subject [required]
  --body     -b TEXT  Email body
  --file     -f PATH  Read body from file
  --cc          TEXT  CC recipients
  --bcc         TEXT  BCC recipients
  --html              Body is HTML
  --attach      PATH  Attachments
```

### cc-gmail search

```
USAGE: cc-gmail search [OPTIONS] QUERY

ARGUMENTS:
  QUERY  Gmail search query [required]

OPTIONS:
  --count   -n INTEGER  Number of results [default: 10]
  --include-spam        Include spam and trash
```

### cc-gmail read

```
USAGE: cc-gmail read [OPTIONS] MESSAGE_ID

OPTIONS:
  --raw  Show raw message data
```

---

## cc-hardware

Query system hardware information.

```
USAGE: cc-hardware [OPTIONS] COMMAND [ARGS]...

OPTIONS: --json -j, --version -v, --help

COMMANDS:
  ram      RAM information
  cpu      CPU information
  gpu      GPU information (NVIDIA only)
  disk     Disk information
  os       Operating system information
  network  Network interface information
  battery  Battery information
```

---

---

## cc-html

Convert between Markdown and HTML with themes.

```
USAGE: cc-html [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v  Show version
  --themes       List available themes

COMMANDS:
  from-markdown  Convert Markdown to HTML with beautiful themes
  to-markdown    Convert HTML to Markdown, extracting embedded images
```

### cc-html from-markdown

```
USAGE: cc-html from-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input Markdown file [required]

OPTIONS:
  --output   -o PATH  Output HTML file [required]
  --theme    -t TEXT   Theme name [default: paper]
  --css         PATH   Custom CSS file
```

### cc-html to-markdown

```
USAGE: cc-html to-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input HTML file [required]

OPTIONS:
  --output  -o PATH  Output .md file (defaults to input name with .md extension)
```

---

## cc-pdf

Convert between Markdown and PDF with themes.

```
USAGE: cc-pdf [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v  Show version
  --themes       List available themes

COMMANDS:
  from-markdown  Convert Markdown to PDF with beautiful themes
  to-markdown    Convert PDF to Markdown, extracting embedded images
```

### cc-pdf from-markdown

```
USAGE: cc-pdf from-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input Markdown file [required]

OPTIONS:
  --output     -o PATH  Output PDF file [required]
  --theme      -t TEXT   Theme name [default: paper]
  --css           PATH   Custom CSS file
  --page-size     TEXT   Page size: a4, letter [default: a4]
  --margin        TEXT   Page margin [default: 1in]
```

### cc-pdf to-markdown

```
USAGE: cc-pdf to-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input PDF file [required]

OPTIONS:
  --output  -o PATH  Output .md file (defaults to input name with .md extension)
```

---

## cc-word

Convert between Markdown and Word documents with themes.

```
USAGE: cc-word [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v  Show version
  --themes       List available themes

COMMANDS:
  from-markdown  Convert Markdown to Word documents with beautiful themes
  to-markdown    Convert a Word document to Markdown, extracting embedded images
```

### cc-word from-markdown

```
USAGE: cc-word from-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input Markdown file [required]

OPTIONS:
  --output   -o PATH  Output .docx file [required]
  --theme    -t TEXT   Theme name [default: paper]
```

### cc-word to-markdown

```
USAGE: cc-word to-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input Word document (.docx) [required]

OPTIONS:
  --output  -o PATH  Output .md file (defaults to input name with .md extension)
```

---

## cc-excel

Convert between CSV, JSON, Markdown tables, and formatted Excel workbooks.

```
USAGE: cc-excel [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v  Show version
  --themes       List available themes

COMMANDS:
  from-csv       Convert a CSV file to a formatted Excel workbook
  from-json      Convert a JSON file to a formatted Excel workbook
  from-markdown  Convert Markdown pipe tables to a formatted Excel workbook
  from-spec      Generate a multi-sheet Excel workbook from a JSON spec file
  to-markdown    Convert an Excel workbook to Markdown pipe tables
```

### cc-excel from-csv

```
USAGE: cc-excel from-csv [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input CSV file [required]

OPTIONS:
  --output        -o PATH     Output .xlsx file [required]
  --theme         -t TEXT      Theme name [default: paper]
  --delimiter        TEXT      CSV delimiter [default: ,]
  --encoding         TEXT      File encoding [default: utf-8]
  --no-header                  First row is data, not headers
  --sheet-name       TEXT      Worksheet tab name
  --no-autofilter              Disable autofilter
  --no-freeze                  Disable freeze panes
  --chart            TEXT      Chart type: bar, line, pie, column
  --chart-x          TEXT      Category column for chart
  --chart-y          TEXT      Value column(s) for chart (repeatable)
  --summary          TEXT      Summary rows: sum, avg, or all
  --highlight        TEXT      Conditional formatting: best-worst or scale
```

### cc-excel to-markdown

```
USAGE: cc-excel to-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input .xlsx file [required]

OPTIONS:
  --output      -o PATH  Output .md file (defaults to input name with .md extension)
  --sheet-name     TEXT   Convert a specific sheet by name
  --all-sheets            Convert all sheets (default: first sheet only)
```

---

## cc-outlook

Outlook CLI: read, send, search emails and manage calendar.

```
USAGE: cc-outlook [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v
  --account  -a TEXT  Outlook account to use

COMMANDS:
  auth                 Authenticate (Device Code Flow)
  list                 List recent emails
  read                 Read a specific email
  send                 Send an email
  draft                Create a draft
  search               Search emails
  reply                Create a draft reply
  forward              Forward an email
  flag                 Flag message for follow-up
  categorize           Set categories
  attachments          List attachments
  download-attachment  Download attachment
  delete               Delete/trash email
  archive              Archive (move to Archive folder)
  unarchive            Move from Archive to Inbox
  move                 Move email to any folder (path or ID)
  folders              List all mail folders (--ids to show folder IDs)
  profile              Show authenticated user
  accounts             Manage accounts
  calendar             Calendar operations
```

### cc-outlook list

```
USAGE: cc-outlook list [OPTIONS]

OPTIONS:
  --folder  -f TEXT     Folder: inbox, sent, drafts, deleted, junk [default: inbox]
  --count   -n INTEGER  Number of emails [default: 10]
  --unread  -u          Show only unread
```

### cc-outlook send

```
USAGE: cc-outlook send [OPTIONS]

OPTIONS:
  --to          -t TEXT  Recipient(s), comma-separated [required]
  --subject     -s TEXT  Subject [required]
  --body        -b TEXT  Body
  --file        -f PATH  Read body from file
  --cc             TEXT  CC recipients
  --bcc            TEXT  BCC recipients
  --html                 Body is HTML
  --attach      -a PATH  Attachments
  --importance  -i TEXT   low, normal, high [default: normal]
```

### cc-outlook search

```
USAGE: cc-outlook search [OPTIONS] QUERY

OPTIONS:
  --folder  -f TEXT     Folder to search [default: inbox]
  --count   -n INTEGER  Number of results [default: 10]
```

### cc-outlook read

```
USAGE: cc-outlook read [OPTIONS] MESSAGE_ID

OPTIONS:
  --raw  Show raw message data
```

---

## cc-photos

Photo organization: scan, categorize, detect duplicates, AI descriptions.

```
USAGE: cc-photos [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  discover  Discover where photos are located
  scan      Scan drives for photos
  dupes     Find and manage duplicates
  list      List images in database
  search    Search image descriptions
  analyze   Analyze images with AI
  stats     Database statistics
  source    Manage photo sources
  exclude   Manage excluded paths
```

---

## cc-powerpoint

Convert between Markdown and PowerPoint presentations with themes.

```
USAGE: cc-powerpoint [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --version  -v  Show version
  --themes       List available themes

COMMANDS:
  from-markdown  Convert Markdown to PowerPoint presentations with beautiful themes
  to-markdown    Convert a PowerPoint presentation to Markdown, extracting images
```

### cc-powerpoint from-markdown

```
USAGE: cc-powerpoint from-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Markdown file with --- slide separators [required]

OPTIONS:
  --output  -o PATH  Output .pptx file (defaults to input name with .pptx extension)
  --theme   -t TEXT   Theme name [default: paper]
```

### cc-powerpoint to-markdown

```
USAGE: cc-powerpoint to-markdown [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input PowerPoint file (.pptx) [required]

OPTIONS:
  --output  -o PATH  Output .md file (defaults to input name with .md extension)
```

---

## DevThrottle Command (cc-devthrottle)

Unified DevThrottle command surface for session-to-session messaging, session management, Gateway
schedules, settings, and local setup diagnostics. Every command runs inside a DevThrottle session
and talks to the GATEWAY, presenting the session's own key (`CC_GATEWAY_URL` +
`CC_GATEWAY_SESSION_KEY`, stamped into the environment at launch). The Director itself listens on
nothing - the remove-the-network-port mission deleted its HTTP surface.

### cc-devthrottle

Unified DevThrottle command surface for fleet, session, and message management.

Run with no arguments, it shows live state instead of the help (`--help` still shows the help):
this session's full id, name, state and repository (or `session: none - ...` outside a session),
the fleet count by state, how many sessions need the owner, and the next commands. If the Gateway
cannot be reached it exits 1 and says why.

```
session[1]{id,name,state,repo}:
  2e7b6504-4fc2-44bd-9bb3-cebcccba554b,AXI Tools - Worker - no-args live state,working,devthrottle-axi-noargs
count: 35 (needs-you 8, working 13, ready 10, snoozed 4)
needs-you: 8
help[4]:
  cc-devthrottle session list --state needs-you
  cc-devthrottle session list
  cc-devthrottle session spawn <repo> --controlled-by self
  cc-devthrottle --help
```

```
USAGE: cc-devthrottle [OPTIONS] COMMAND [ARGS]...

COMMANDS:
  actions          List agent-discoverable actions.
  session list     List every session in the fleet.
  session whoami   Show this session's own fleet identity.
  session rename   Rename a session, defaulting to the current session.
  session spawn    Open a new session - here, on another computer, or on one named Director.
  session report   Tell the session that owns you what you did, at the end of your turn.
  director list    List every Director this account runs, with the id --director accepts.
  mission list     List the missions on the Gateway, active ones by default.
  message send     Send a message to one session, or broadcast with all.
  message ask      Ask one session a question and print its answer.
  fleet-manager    Show, set, or clear which session is this account's one Fleet Manager.
  skill list       List every skill in the fleet library.
  skill get        Print a skill in full, ready to follow.
  skill pull       Pull a skill into a directory for editing.
  skill push       Push a directory back as the skill's draft.
  skill publish    Publish a draft - live fleet-wide, immediately.
  settings show    Display current settings.
  settings get     Get a specific setting value.
  settings set     Set a configuration value.
  schedule list    List every schedule on the Gateway.
  schedule create  Create a schedule, one-off with --at or recurring with --cron.
  schedule run     Fire a schedule immediately.
  setup status     Show local DevThrottle setup status.
  setup install    Install DevThrottle from the latest GitHub release.
  selftest         Run an end-to-end fleet messaging smoke test.

OPTIONS:
  --version -v
```

```
USAGE: cc-devthrottle session rename TARGET_OR_NAME [NEW_NAME]

ARGUMENTS:
  TARGET_OR_NAME  New name for this session, or a target when NEW_NAME is also provided [required]
  NEW_NAME        New name when an explicit target is provided
```

`cc-devthrottle session rename "New Name"` renames the current session using `CC_SESSION_ID`.
`cc-devthrottle session rename 9b2f "New Name"` renames an explicit target.

### Session List

```
USAGE: cc-devthrottle session list [OPTIONS]

OPTIONS:
  --json     -j  Output raw JSON: every field, a bare array. Filters still apply.
  --state        Only these states, comma separated: needs-you, working, ready, snoozed, crashed.
  --repo         Only this repository: its folder name or full path.
  --machine      Only sessions on this machine.
  --fields       Fields to show, comma separated. Default: id,name,state,repo.
                 Valid: id, name, state, repo, machine, number, model, agent, mission, path.
```

The output follows the command-line output standard (`docs/axi-standard.md`): a `count:` line, then
one comma-separated row per session under a `sessions[N]{fields}:` header, then `help[N]:` with the
next commands to run.

```
count: 2 (needs-you 1, working 1)
sessions[2]{id,name,state,repo}:
  9b2f41c0-7d1e-4a55-9c1a-2f6e0d3b8a71,"AXI Tools - Worker - step 3, session list",needs-you,devthrottle
  e0c3a8d2-5b64-4f1e-8a09-6d2c7f1b4e93,review: session list,working,cc-consult
help[4]:
  cc-devthrottle session list --state needs-you
  cc-devthrottle session list --fields id,name,state,repo,machine,number,model,agent,mission,path
  cc-devthrottle session list --json
  cc-devthrottle session whoami
```

- **Default fields** are `id`, `name`, `state` and `repo`. `--fields` picks others, in the order
  given; an unknown field name exits 2 and lists the valid ones. `--fields` cannot be combined with
  `--json`, which always carries every field (exit 2).
- **Ids and names are always shown in full**, never shortened. A value containing a comma, a quote,
  surrounding spaces or a character outside ASCII is written in double quotes with backslash
  escapes; an empty name is written `""`, and a missing value is written as nothing.
- **State** is one plain word, folded from the Gateway's triage verdict: `crashed` if the session
  crashed, otherwise `needs-you`, `snoozed`, or - for an active session - `working` while the agent
  is working and `ready` when it is not. An unknown triage verdict, or a session with no id, exits 1
  rather than being guessed at.
- **Filters** (`--state`, `--repo`, `--machine`) can be combined, and every one of them applies to
  `--json` as well: the output is the same bare array, narrowed. `--repo` matches the repository
  folder name or the full path, ignoring case and slash direction; `--machine` ignores case. An
  unknown state exits 2 and lists the valid states.
- **An empty answer says so**: `count: 0` for an empty fleet, and `count: 0 of N total` when a
  filter matched nothing. With `--json` it is `[]`.
- **`--json`** without a filter prints exactly what the Gateway returned. Any caution that the list
  may be incomplete goes to standard error, never into the JSON.

The `model` field is the model that session's agent is actually running, read from the agent's own
records at every turn-end - so it follows a mid-session model switch. Where there is no model, it
says which kind of absence it is rather than being blank: `no model yet` for a session with nothing
recorded yet (it is read at each turn-end), `model not reported` for an agent that cannot report one
at all (Gemini, Cursor - it is never coming), and `(unknown)` when the Gateway sent no verdict for
that row.

### Session Whoami

```
USAGE: cc-devthrottle session whoami
```

Shows this session's own id, name, machine, and repository.

### Mission List

```
USAGE: cc-devthrottle mission list [OPTIONS]

OPTIONS:
  --json     -j  Output raw JSON: every field, a bare array. Filters still apply.
  --all      -a  Include missions that have been completed or removed.
  --state        Show only this state: active, complete, removed, or all. Overrides --all.
  --name         Only missions whose name contains this text, ignoring case.
  --fields       Fields to show, comma separated. Default: id,name,state.
                 Valid: id, name, state, why, why-updated, state-changed, run.
```

The same shape as `session list`: a `count:` line, one row per mission, then `help[N]:`.

```
count: 70 of 78 total (active 70)
missions[70]{id,name,state}:
  43b85d84-07bd-410b-a6c6-88a137bcc1c3,AXI - make our command-line tools agent-shaped,active
  ...
38 of these missions have no why set.
help[5]:
  cc-devthrottle mission list --all
  cc-devthrottle mission list --fields id,name,state,why,why-updated,state-changed,run
  cc-devthrottle mission list --json
  cc-devthrottle mission attach <session> <id>
  cc-devthrottle session spawn <repo> --controlled-by self --mission <id>
```

- **Active only by default**, which is itself a filter, so the count says how many missions it left
  out. `--all` shows every state and counts each: `count: 78 (active 70, complete 5, removed 3)`.
- **The why** is long free text, so it is shown only when `--fields` asks for it. When it is not
  shown, a line says how many of the listed missions have no why set.
- **Filters** apply to `--json` as well. `--json` asks the Gateway exactly what it always asked
  (`--state` is passed through), and `--name` narrows that same bare array.
- An unknown state or field exits 2 and lists the valid values. Every row is checked in full before
  anything is filtered or shown, and a broken one exits 1 rather than being listed, filtered out or
  shown blank: a row that is not an object; a mission with no id, an id another row already has, or
  no name or a blank one (the Gateway refuses a blank name); a state other than active, complete or
  removed; a why that is missing or not text (an empty why is "unset", and is flagged); or a
  why-updated, state-changed or run that is missing, blank, or neither text nor null. `--json`
  without `--name` prints the Gateway's answer as it came; `--json --name` checks every row first.
- **An empty answer says so**: `count: 0`, or `count: 0 of N total` with the filter that matched
  nothing named on the next line.

### Schedule List

```
USAGE: cc-devthrottle schedule list [OPTIONS]

OPTIONS:
  --json     -j           Output raw JSON: every field, a bare array. Filters still apply.
  --enabled / --disabled  Only enabled schedules, or only disabled ones.
  --machine               Only schedules that run on this machine.
  --fields                Fields to show, comma separated. Default: id,name,enabled,next-run.
                          Valid: id, name, enabled, next-run, machine, kind, cron, run-at,
                          time-zone, work-list, path, last-fired, last-status, notify, created.
```

```
count: 39 (enabled 23, disabled 16)
schedules[39]{id,name,enabled,next-run}:
  cj_1a10c4,SmartScreen + winget follow-up,no,
  cj_33022a,Monday business finance run,yes,2026-09-21T11:01:00Z
  ...
help[5]:
  cc-devthrottle schedule list --enabled
  cc-devthrottle schedule list --fields id,name,enabled,next-run,machine,kind,cron,run-at,time-zone,work-list,path,last-fired,last-status,notify,created
  cc-devthrottle schedule list --json
  cc-devthrottle schedule get <id>
  cc-devthrottle schedule runs <id>
```

- Every field is the Gateway's own value, unreworded; `next-run` is in UTC and is empty when there
  is no next run. The seed prompt is left to `schedule get`.
- **Filters** (`--enabled` or `--disabled`, `--machine`) combine, and apply to `--json` as the same
  bare array, narrowed. `--machine` ignores case. With no filter, `--json` prints exactly what the
  Gateway returned.
- An answer from the Gateway with no list of jobs exits 1 - it is never reported as "no schedules".
  Every schedule is checked in full before anything is filtered or shown, whichever fields are asked
  for, and a broken one exits 1 rather than being listed, filtered out or shown blank: no id, or an id
  another row already has; an enabled flag that is not true or false; any field missing or of the
  wrong kind; a blank name, time zone, target machine or repo path (the Gateway refuses each); a kind
  other than recurring or oneOff; no cron expression on a recurring schedule or no run-at time on a
  one-off; or a notify policy other than none, always or failure. With no filter, `--json` prints the
  rows as the Gateway sent them.
- **An empty answer says so**: `count: 0`, or `count: 0 of N total` when a filter matched nothing.

### Fleet Manager

```
USAGE: cc-devthrottle fleet-manager show [--json]
       cc-devthrottle fleet-manager set [SESSION] [--json]
       cc-devthrottle fleet-manager clear [--json]

ARGUMENTS:
  SESSION  The session to mark: its number, an id prefix, or its name. Omit to mark this session.
```

An account has exactly one Fleet Manager, and this is the mark that says which session it is. It is
held on the Gateway (`GET` and `PUT /gateway/fleet-manager`). The Wingman judges the turn ends of the
sessions that session directly owns; every other session with a live owner is left alone. The
workflow a session is seated on never makes it the Fleet Manager, and a marked session that another
session owns is not treated as one while that ownership stands.

`set` replaces any earlier mark. `clear` removes it. `show` prints the full id and the name, or
`fleet-manager: none`. `--json` prints `{"sessionId": "<id>"}`, or `{"sessionId": null}` when there is
no mark.

### Message Send

```
USAGE: cc-devthrottle message send TARGET MESSAGE

ARGUMENTS:
  TARGET   Session id, id prefix, or name - or 'all' to broadcast [required]
  MESSAGE  The message text to send [required]
```

The recipient sees a framed message that names the sender and how to reply:

```
[message from feature-work (machine-A), id 4c810000] run the integration tests on your branch  (to reply: cc-devthrottle message send 4c810000 "<your reply>")
```

An ambiguous id prefix or name is refused with the list of candidates. No message is sent.

### Message Ask

```
USAGE: cc-devthrottle message ask [OPTIONS] TARGET QUESTION

ARGUMENTS:
  TARGET    Session id, id prefix, or name - a single session, not 'all' [required]
  QUESTION  The question to ask [required]

OPTIONS:
  --timeout-ms INTEGER  How long to wait for the answer (default 120000)
```

If the target does not answer within the timeout, the command prints a clear timeout message and
exits non-zero. `message ask all` is not supported.

### Session Spawn

```
USAGE: cc-devthrottle session spawn [OPTIONS] REPO

ARGUMENTS:
  REPO  Absolute path to the repository / working directory for the session [required]

OPTIONS:
  --agent TEXT          Agent CLI: ClaudeCode (default), Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli
  --prompt TEXT         First prompt to send once the session is ready
  --name TEXT           Custom display name for the session
  --purpose TEXT        What the session is FOR; used to build the name when --name is omitted
  --machine TEXT        Start it on ANOTHER COMPUTER: the Gateway routes to a Director there
  --director TEXT       Start it on ONE named Director, by Director id or display name
  --command TEXT        For --agent RawCli: the executable to run (e.g. cmd, pwsh)
  --command-args TEXT   For --agent RawCli: arguments for the command
  --controlled-by TEXT  WHO OWNS IT: 'self', a session id, or 'none'. Required from a session
  --standalone          The USER owns it: no controller (same as --controlled-by none)
```

Prints the new session's short id and full GUID; the session then appears in
`cc-devthrottle session list`. A non-existent repository path exits non-zero with a clear error.

**A session-initiated spawn must say who OWNS the new session.** Every session has exactly one
owner and it is either another session or the user; no owner means the user. From inside a session
both are possible, so there is no default between them and the spawn is refused until you say:
`--controlled-by self` (you own it - it stays quiet and reports back to you), `--standalone` (the
user owns it - it goes red and asks him, and you will not hear from it), or `--controlled-by <id>`
(another session owns it). It is what the attention rule reads afterwards, so it decides whether
that session's finished turn ever reaches the user.

This used to default to `self` whenever `CC_SESSION_ID` was set. It no longer does. The default was
the only way work could stop being the user's without anyone choosing it - an environment variable
deciding that a session answers to a machine. A person spawning from the desktop, the Cockpit or
the phone declares nothing: there is no second candidate, so there is nothing to state.

**`--machine` picks a computer; `--director` picks a Director.** They are not the same question. One
computer runs several named Director instances, so `--machine SOREN_NORTH` resolves to whichever
Director on that machine the Gateway lists first - fine when any will do, a coin toss when it will
not. `--director` names exactly one, by its Director id or its display name, and needs no
`--machine`: a Director identifies the computer it runs on.

A named Director that is not registered fails loudly naming it, and a display name that matches two
Directors fails listing both. Neither ever falls back to another Director, and neither auto-launches
one (`--machine` does) - a session opened quietly on the wrong Director is the failure this exists to
prevent, and nothing in the reply would reveal it.

Giving both narrows rather than overrides: `--machine` filters the Directors a name may match, which
is how you disambiguate a display name two machines share. Naming a Director together with a machine
it does not run on is therefore a contradiction and fails - the alternative is honouring half of what
you asked for without saying which half.

```
USAGE: cc-devthrottle director list [OPTIONS]

OPTIONS:
  --json -j        Output raw JSON: every field, a bare array. Filters still apply.
  --state TEXT     Only these states, comma separated: online, wobbly, offline, stopped.
  --machine TEXT   Only Directors on this machine.
  --fields TEXT    Fields to show. Default: id,name,machine,state.
                   Valid: id, name, machine, state, version, pid, user, started, last-seen.
```

```
count: 4 (online 3, offline 1)
directors[4]{id,name,machine,state}:
  136af82d-29d5-43bc-9f4b-6783bb1111da,SORENLAPTOP,SORENLAPTOP,online
  61640aab-061d-4d2d-a91e-2160d16cec00,DevThrottle_2,SOREN_NORTH,online
  6d4523e2-ed03-4ae6-ac1c-71d00a37bad1,DevThrottle_1,SOREN_NORTH,offline
  4fbad29d-6baa-4cdd-bbee-cef6b0b50978,devthrottle-mac-mini,devthrottle-mac-mini,online
help[4]:
  cc-devthrottle director list --state offline
  ...
```

Lists every Director this account is running, on every machine: its id, its name, its machine, and
its state. The state is the Gateway's own verdict (the one the Fleet Map shows); a state this tool does
not know fails with exit code 1 rather than being guessed. `--json` without a filter prints exactly
what the Gateway sent. Ids and names are never shortened; an unnamed Director shows its machine name.

Prefer the **id** when handing a target to another agent - it survives a rename and cannot collide
with a second Director sharing a display name. A Director's own toolbar has a Copy button that puts
its name, machine and id on the clipboard, for pasting to an agent.

### Skill Commands

Read and author the fleet's skills. A skill is a directory in the Agent Skills standard - `SKILL.md`
at its root plus any files it needs - held centrally on the Gateway and placed on each machine where
every agent already looks. Publishing makes a skill live across the whole fleet immediately.

```
USAGE: cc-devthrottle skill COMMAND [ARGS]...

COMMANDS:
  list      List every skill in the library.
  get       Print a skill in full - the body an agent follows.
  show      Show one skill's register entry.
  versions  List a skill's versions.
  pull      Write a skill into a directory for editing.
  push      Send a directory back as the skill's DRAFT.
  publish   Publish the draft - live for every agent on every machine.
  clone     Copy a skill under a new id (how a built-in is customised).
  enable    Switch a skill on for the fleet.
  disable   Switch a skill off - it is removed from disk on the next session launch.
  delete    Delete a skill.
```

```
USAGE: cc-devthrottle skill get ID [OPTIONS]

ARGUMENTS:
  ID  The skill id, for example move-session [required]

OPTIONS:
  --version INTEGER  A specific version instead of the published one
```

There is no offline fallback, deliberately: `skill get` resolves the current published version from
the Gateway every time, and fails plainly if the Gateway cannot be reached. A stale skill that looks
current is worse than a missing one that announces itself.

```
USAGE: cc-devthrottle skill pull ID --dir DIRECTORY [OPTIONS]
USAGE: cc-devthrottle skill push ID --dir DIRECTORY [OPTIONS]

OPTIONS (pull):
  --dir     -d  Directory to write the skill into [required]
  --version     A specific version instead of the published one

OPTIONS (push):
  --dir     -d  Directory holding the skill files [required]
  --note    -n  One line on what changed
  --force       Push even though the copy is stale
```

A pull writes an authoring directory:

| File | Holds |
|---|---|
| `skill.json` | The register metadata - id, name, summary, triggers, the standard's frontmatter fields, and which files are executable |
| `SKILL.md` | The body an agent reads |
| Everything else | The supporting files, each at its own relative path (`references/tracing.md` is a file `tracing.md` inside a `references` directory) |
| `.skill-hash` | Written by pull, sent back on push so a stale copy is refused rather than clobbering a concurrent author |

A push sends text as text and everything else base64-encoded, so an image, an archive or a compiled
program survives the round trip byte for byte. A push updates the DRAFT only - no agent sees it until
`skill publish`.

### Selftest

```
USAGE: cc-devthrottle selftest [OPTIONS]

OPTIONS:
  --timeout-ms INTEGER  How long the ask step waits for the responder (default 25000)
```

Spawns two throwaway sessions, lists them, sends to one, asks the other, tears them down, and prints
PASS/FAIL.

### Settings

```
USAGE: cc-devthrottle settings COMMAND [ARGS]...

COMMANDS:
  show [SECTION]  Display current settings, or one section.
  get KEY         Get a specific setting value.
  set KEY VALUE   Set a configuration value.
  list            List all setting keys with values.
  path            Show the config file location.
```

`--json` is available on every settings subcommand. Dotted keys use the same names as
`config.json`, for example `screenshots.source_directory` or `gateway.url`.

### Schedule

```
USAGE: cc-devthrottle schedule [--gateway URL] COMMAND [ARGS]...

COMMANDS:
  list
  get ID
  runs ID
  create --name NAME --machine MACHINE --repo REPO (--at WHEN | --cron EXPR) --tz TZ (--seed TEXT | --worklist NAME)
  run ID
  enable ID
  disable ID
  delete ID
  endpoint
```

`--json` is available on `list`, `get`, `runs`, `create`, `run`, and `endpoint`.
`--notify-on none|always|failure` and `--notify-webhook URL` are available on `create`.

### Setup

```
USAGE: cc-devthrottle setup COMMAND [ARGS]...

COMMANDS:
  status [--json]
  install
  update
  repair
  doctor [--json]
```

---

## cc-ship

Take a finished change from the author session to merged on origin/main: independent review by another agent family, live verification, risk, pull request and merge. Not in the installer yet; see `tools/cc-ship/README.md` for setup.

| Command | What it does |
|---|---|
| `cc-ship start --intent <file> [--title <text>]` | Open a run for this branch and go as far as possible |
| `cc-ship wait [--seconds N]` | Block (up to 480 seconds) while a spawned session works |
| `cc-ship continue` | Resume after fixing findings or a failed step |
| `cc-ship respond <id> --fix\|--keep\|--drop [--note <text>]` | Record the owner's call on one finding, or on `fix-limit` |
| `cc-ship status [--json]` | The run's one state and the next step |
| `cc-ship abort` | End the run and stop its sessions; the branch is left alone |

Every command takes `--json`. Errors exit non-zero with `error`, `code` and `help`.

## cc-dev-reports

Publish a dev report (one HTML file) to the owner through the Gateway, and reply to the owner in it. Not in the installer yet; see `tools/cc-dev-reports/README.md` for setup.

| Command | What it does |
|---|---|
| `cc-dev-reports open <file>` | Publish the file; publishing it again makes a new version. A shape-check refusal prints every error |
| `cc-dev-reports reply "<text>" [--report <id>]` | Reply to the owner in a report; default is this session's newest report |

Both take `--json`, which always has the keys `ok`, `command`, `report`, `created`, `reply`, `ownerRoute`, `error`, `code`, `errors`. Errors exit 1.

## cc-reddit

Reddit CLI via browser automation.

```
USAGE: cc-reddit [OPTIONS] COMMAND [ARGS]...

OPTIONS:
  --connection -c TEXT    cc-browser connection name
  --workspace  -w TEXT    Deprecated: use --connection
  --format        TEXT    Output: text, json, markdown [default: text]
  --delay         FLOAT   Delay between actions [default: 1.0]
  --verbose    -v         Verbose output

COMMANDS:
  status      Check daemon and Reddit login status
  whoami      Show logged-in Reddit username
  me          View your profile activity (--posts, --comments)
  saved       View saved posts and comments
  karma       Show karma breakdown
  goto        Navigate to a Reddit URL
  feed        View subreddit feed
  post        View a Reddit post
  comment     Add a comment to a post
  reply       Reply to a comment
  upvote      Upvote a post or comment
  downvote    Downvote a post or comment
  join        Join a subreddit
  leave       Leave a subreddit
  snapshot    Page snapshot (debugging)
  screenshot  Take a screenshot
```

## cc-secrets

Use a stored password without the model ever seeing it. The owner adds entries by hand on each
machine; agents use them through `list`, `run` and `login`, and get back only the result. No command
prints a secret, and there is deliberately no `get`.

The store is one plain JSON file per user per machine (`secrets.json`), protected by user-only file
permissions. Every use writes a line to `secrets-audit.log`.

It protects against accidental exposure (transcripts, logs, output, screenshots), not against a
hostile program running as the same user.

```
USAGE: cc-secrets [OPTIONS] COMMAND [ARGS]...

COMMANDS:
  add      OWNER: add or replace an entry (hidden prompt, or secret piped on stdin)
  remove   OWNER: remove an entry
  list     Entries agents may use: names, usernames, allowed addresses. Never secrets (--all, --json)
  run      Run a command with the secret supplied; output comes back with the secret removed
  login    Fill and submit the login form in a Director-owned browser; refuses any other address
  log      Show the audit log (-n, --json)
  version  Print the version
```

`add` and `remove` refuse to run inside a DevThrottle session. There is no option that takes the
secret as an argument. In Git Bash (mintty) typing cannot be hidden, so `add` refuses there: run it from
PowerShell or cmd, or pipe the secret in.

No error is shown as a traceback: an unexpected error is named by its type only.

### cc-secrets add

```
USAGE: cc-secrets add [OPTIONS] NAME

OPTIONS:
  --username TEXT         The user name that goes with the secret
  --domains TEXT          Comma-separated site addresses login may fill: https://example.com,
                          https://*.example.com, http://127.0.0.1:8080. No scheme means https;
                          scheme, host and port must all match
  --notes TEXT            A note for yourself; agents see it in list
  --agents / --no-agents  Whether sessions on this machine may use it
  --uses TEXT             Comma-separated: login, run [default: both]
  --replace               Replace an existing entry without asking
```

With the secret piped on stdin, `--username`, `--domains` and `--agents`/`--no-agents` are required.

### cc-secrets run

```
USAGE: cc-secrets run [OPTIONS] NAME -- COMMAND...

OPTIONS:
  --via TEXT        stdin, env or askpass [default: stdin]
  --env-name TEXT   Variable name for --via env [default: CC_SECRET]
  --timeout FLOAT   Seconds before the command is stopped [default: 600]
  --json            Print JSON
```

Example: `cc-secrets run devlinux -- sudo -S apt-get update`

### cc-secrets login

```
USAGE: cc-secrets login [OPTIONS] NAME

OPTIONS:
  --browser TEXT    The Director-owned browser profile (cc-devthrottle browser list) [required]
  --timeout FLOAT   Seconds to wait for the login to complete [default: 30]
  --json            Print JSON
```

Open the login page in that browser first. The tab's address AND the address the form sends to must be
allowed, and the form must send by POST: a GET form would put the password in the page address and the
browser history, so it is refused before anything is typed. The browser reads the form's method and
address again after the page's own submit handlers have run, so those are re-checked inside the submit
event itself and the submission is cancelled there if the page changed either one. Outcomes: `logged in`, `refused` (nothing typed), `verification` (finish two-step verification
by hand), `failed`. After a password has been typed, whatever the outcome, every password field in the tab
is emptied and the tab's back/forward history is reset, and that is confirmed by reading the tab back -
including that neither the address nor the history holds the password; if one does, the tab is taken off
that page first. If it cannot be confirmed - for example the connection dropped - it is redone over a fresh
connection, and if that fails too the tab is closed.

---

## cc-transcribe

Transcribe video/audio with timestamps and screenshots.

```
USAGE: cc-transcribe [OPTIONS] INPUT_FILE

ARGUMENTS:
  INPUT_FILE  Input video file (.mp4, .mkv, .avi, .mov) [required]

OPTIONS:
  --output       -o PATH   Output directory
  --screenshots             Extract screenshots at content changes [default: on]
  --no-screenshots          Disable screenshots
  --threshold    -t FLOAT   Sensitivity 0-1, lower=more [default: 0.92]
  --interval     -i FLOAT   Min seconds between screenshots [default: 1.0]
  --language     -l TEXT     Force language code (en, es, de)
  --info                     Show video info and exit
  --version      -v          Show version
```

---

## cc-vault

Personal Vault CLI: contacts, tasks, goals, ideas, documents.

```
USAGE: cc-vault [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  init            Initialize a new vault
  stats           Show vault statistics
  ask             Ask via RAG (--model, --no-hybrid)
  search          Semantic/hybrid search (-n, --hybrid)
  backup          Create full zip backup
  repair-vectors  Rebuild vector index
  restore         Restore from backup
  link            Create entity link
  unlink          Remove entity link
  links           Get links for entity
  context         Entity with linked context (for agents)
  tasks           Task management (list, add, done, cancel, show, update, search)
  goals           Goal tracking
  ideas           Idea capture
  contacts        Contact management (list, add, show, memory, update, search)
  docs            Document management (list, add, show, search, reindex)
  config          Configuration
  health          Health data
  posts           Social media posts
  lists           Contact list management
  graph           Graph statistics and traversal
```

### cc-vault ask

```
USAGE: cc-vault ask [OPTIONS] QUESTION

OPTIONS:
  --model  -m TEXT  OpenAI model [default: gpt-4o]
  --no-hybrid       Disable hybrid search
```

### cc-vault search

Semantic search across ALL vault data (contacts, tasks, docs, etc.).

```
USAGE: cc-vault search [OPTIONS] QUERY

OPTIONS:
  -n INTEGER  Number of results [default: 10]
  --hybrid    Use hybrid search
```

**Examples:**
```bash
cc-vault search "kubernetes deployment"
cc-vault search "marketing strategy" --hybrid -n 5
```

**NOTE:** To search within a specific entity type, use the subcommand instead:
```bash
cc-vault contacts search "Ozdal"       # search contacts by name
cc-vault tasks search "deploy"          # search tasks
cc-vault docs search "architecture"     # search documents
cc-vault posts search "linkedin"        # search posts
```

Do NOT use `cc-vault search contacts "Name"` -- that passes two arguments to the
top-level search command and will fail.

---

## cc-video

Video utilities: info, extract audio, screenshots.

```
USAGE: cc-video [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  info         Show video information
  audio        Extract audio from video
  screenshots  Extract screenshots at content changes
  frame        Extract single frame at timestamp
```

---

## cc-voice

Convert text to speech.

```
USAGE: cc-voice [OPTIONS] TEXT

ARGUMENTS:
  TEXT  Text to convert (or path to text file) [required]

OPTIONS:
  --output  -o PATH   Output audio file (.mp3) [required]
  --voice   -v TEXT    alloy, echo, fable, nova, onyx, shimmer [default: onyx]
  --model   -m TEXT    tts-1, tts-1-hd [default: tts-1]
  --speed   -s FLOAT   0.25 to 4.0 [default: 1.0]
  --raw                Don't clean markdown formatting
  --version            Show version
```

---

## cc-whisper

Transcribe audio using OpenAI Whisper.

```
USAGE: cc-whisper [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  transcribe  Transcribe audio
  translate   Translate audio to English
```

---

## cc-youtube-info

Extract transcripts, metadata from YouTube videos.

```
USAGE: cc-youtube-info [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  info        Video metadata (title, channel, duration, stats)
  transcript  Download transcript
  languages   List available transcript languages
  chapters    List video chapters with timestamps
```

---

## Director Control API - removed

The Director no longer exposes any HTTP surface. The remove-the-network-port mission deleted the
listener, the loopback port range 7879-7898, and every route that lived on it (the voice-turn
endpoint documented here had already been superseded by the Gateway voice path). Drive the fleet
with `cc-devthrottle`, which talks to the Gateway; see docs/public/api/01-control-api.md for the
full mapping of what replaced each piece.


## Common Flag Patterns

Most tools use these consistent flags:

| Flag | Short | Meaning |
|------|-------|---------|
| `--count` | `-n` | Number of results (NOT `--limit`) |
| `--version` | `-v` | Show version |
| `--account` | `-a` | Account name (gmail, outlook) |
| `--output` | `-o` | Output file path |
| `--connection` | `-c` | cc-browser connection |
| `--format` | `-f` | Output format (text, json, markdown) |
| `--to` | `-t` | Recipient email |
| `--subject` | `-s` | Email subject |
| `--body` | `-b` | Email body |
| `--unread` | `-u` | Filter unread only |
| `--label` | `-l` | Gmail label/folder |
| `--folder` | `-f` | Outlook folder |

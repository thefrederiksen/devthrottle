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
  actions          List the actions an agent can discover, with their commands.
  session list     List every session in the fleet.
  session whoami   Show this session's own fleet identity.
  session rename   Rename a session, defaulting to the current session.
  session spawn    Open a new session - here, on another computer, or on one named Director.
  session report   Tell the session that owns you what you did, at the end of your turn
                   (sends nothing when a Fleet Manager owns you: the Gateway tells it).
  session raise    Put your hand up to the session driving you when you are blocked.
  session hand-over  Hand a running session to the Fleet Manager, or back to the owner
                   (the owner's change: the Gateway refuses it from any session key).
  director list    List every Director this account runs, with the id --director accepts.
  worktree list    List the fleet's worktrees; --pool lists this machine's cc-worktrees pool.
  worktree get     Take a pooled worktree to work in (runs cc-worktrees).
  worktree return  Give a pooled worktree back (runs cc-worktrees).
  mission list     List the missions on the Gateway, active ones by default.
  fleet digest     Everything the Fleet Manager reads at the start of a conversation.
  fleet ready      File a Ready record (also: fleet finding, fleet decision).
  fleet outcomes   List the account's outcome records (also: fleet show, fleet answer, fleet advise).
  fleet prefer     Keep a standing preference (also: fleet preferences, fleet forget).
  fleet events     List the stops and deaths of sessions the Fleet Manager owns (also: fleet ack).
  message send     Queue a message for your supervisor or a worker ('all' for every worker).
  message reply    Answer a message that asked for a reply; the answer goes to whoever asked.
  message inbox    Read your unread messages in full, which marks them read.
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
  selftest         Windows only: check a message to a throwaway worker is queued.

OPTIONS:
  --version -v
```

`cc-devthrottle actions` prints every action as a list, `actions[N]{id,command,changes-state}`,
with the full command on each row; `--json` is unchanged.

**After a change, and on an error.** In the schedule, workflow, skill, settings, setup, email,
diag, autostart and browser groups, a command that changes something ends its plain output with
`help[N]:` lines naming what to run next. A value appears in those lines only when the command's
own result supplied it, and the same holds for a command quoted inside a sentence; otherwise it is
a placeholder such as `<schedule-id>`. A failure is written to standard error as `Error: ...`,
followed by `help[N]:` lines naming what to run next, and the command exits 1 - including when the
setup engine behind setup and autostart fails, whose own exit code is kept in the error text. A flag
or argument to fix is a usage error, written like every other usage error in the tool (the Usage
line, the valid options, and `help[1]` naming the command's `--help`), and exits 2. `workflow
delete` and `skill delete` without `--yes` refuse with a usage error when there is no terminal to
ask on, instead of prompting. `workflow pull`, `skill pull`, `workflow materialize` and the file
cache behind `skill get` change nothing on disk unless the Gateway's whole answer is complete and is
for the version asked for - every authored field (name, summary, triggers or steps, and the rest),
an explicit files list, a safe name, an encoding and decodable content for every file, the body,
and the content hash. A supporting file may not use a path the skill's own files use (`SKILL.md`,
`skill.json`, `.skill-hash`, at any letter case). The check turns the answer into the exact bytes of
every file (body, `skill.json` or `workflow.json`, each supporting file, the hash) and refuses it when
any text cannot be written as UTF-8, any name holds a character the Gateway itself never stores
(anything but ASCII letters, digits, dot, dash and underscore - so every control character, including
C1 ones such as U+0085, every space and every non-ASCII letter), or any name holds something an
operating system refuses: `< > : " | ? *` or a backslash, a name over 255 bytes, or (on macOS and
Linux) a whole path longer than the machine allows. Once the answer is checked, the new files are
written over the old ones, then the files the new version no longer has are removed, and the content
hash is written last; whatever still fails is reported as an `Error:` line with `help[N]:`, never a
traceback. Not guaranteed: a write that fails part way because of the machine (a full disk, a file
another program holds, a path over the Windows length limit) or a process killed part way can leave a
mix of old and new files; the old hash stays, so the next `skill get` or
`workflow materialize` rewrites its cache, and a push is compared against the old version. Windows
name aliases such as `SKILL.md.` (a trailing dot or space) are not yet refused. A push whose answer has no new content
hash says so and names `pull`. After `browser start`, the next step is `browser attach`, which
works in any shell; the `eval` line in its output is the Bash or zsh form. `--json` output is unchanged, and the raw text of `skill get` and
`workflow instructions` gets nothing added.

```
$ cc-devthrottle schedule disable cj_abc123
Disabled nightly (cj_abc123).
help[2]:
  cc-devthrottle schedule enable cj_abc123
  cc-devthrottle schedule delete cj_abc123
```

```
USAGE: cc-devthrottle session rename TARGET_OR_NAME [NEW_NAME]

ARGUMENTS:
  TARGET_OR_NAME  New name for this session, or a target when NEW_NAME is also provided [required]
  NEW_NAME        New name when an explicit target is provided
```

`cc-devthrottle session rename "New Name"` renames the current session using `CC_SESSION_ID`.
`cc-devthrottle session rename 9b2f "New Name"` renames an explicit target.

### After a change, and when something fails

These rules hold for the `session`, `message`, `mission`, `repo`, `worktree`, `director` and `machine`
groups, `selftest`, and `cc-devthrottle` itself (`docs/axi-standard.md`):

- **A command that changes something ends with `help[N]:`** - the next commands worth running. A
  runtime value is a placeholder such as `<session-id>` unless the command's own result supplied it,
  in which case the real, full id is filled in:

  ```
  $ cc-devthrottle session rename 9b2f "Review - pull request 2960"
  Renamed 9b2f41c0-7d1e-4a55-9c1a-2f6e0d3b8a71 to "Review - pull request 2960".
  help[2]:
    cc-devthrottle session list
    cc-devthrottle message send 9b2f41c0-7d1e-4a55-9c1a-2f6e0d3b8a71 "<message>"
  ```

  `--json` never carries a `help[]` block: it prints the Gateway's answer, unchanged.
- **An error goes to standard error**, never standard output, as one `Error: <what failed>` line.
  A runtime failure then lists what to run next as `help[N]:` lines and exits **1**:

  ```
  $ cc-devthrottle session interrupt 9b2f
  Error: could not interrupt session 9b2f41c0-7d1e-4a55-9c1a-2f6e0d3b8a71: <the Gateway's reason>
  help[3]:
    cc-devthrottle message send 9b2f41c0-7d1e-4a55-9c1a-2f6e0d3b8a71 "<message>"
    cc-devthrottle session list
    cc-devthrottle setup status
  ```

  A usage error - what was typed has to change: a blank value, flags that contradict each other, a
  flag that would otherwise be ignored - says how to fix it on the `Error:` line, adds the command's
  `Usage:` line and `Valid options:`, and exits **2**. Nothing is sent to the Gateway before a usage
  error. Every command in the tool writes both kinds the same way (`src/axi_cli.py`,
  `src/usage_errors.py`).
- **An ambiguous target** is refused with every candidate's full id, so one can be pasted back.
- **Every command has a one-line summary** in `--help`; the detail follows on later lines.

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
help[5]:
  cc-devthrottle session list --state needs-you|working|ready|snoozed|crashed
  cc-devthrottle message send <session-id> "<message>"
  cc-devthrottle session list --fields id,name,state,repo,machine,number,model,agent,mission,path
  cc-devthrottle session list --json
  cc-devthrottle session whoami
```

- **Default fields** are `id`, `name`, `state` and `repo`. `--fields` picks others, in the order
  given; an unknown field name exits 2 and lists the valid ones. `--fields` cannot be combined with
  `--json`, which always carries every field (exit 2); the refusal says to use `--fields` for a few
  fields or `--json` for every field. The same holds for every list command.
- **Help lines** always name all five states in one `--state` line, whatever the count line shows,
  and how to message a listed session (`message send`).
- **Ids and names are always shown in full**, never shortened. A value containing a comma, a quote,
  surrounding spaces or a character outside ASCII is written in double quotes with backslash
  escapes; an empty name is written `""`, and a missing value is written as nothing.
- **State** is one plain word, folded from the Gateway's triage verdict: `crashed` if the session
  crashed, otherwise `needs-you`, `snoozed`, or - for an active session - `working` while the agent
  is working and `ready` when it is not. An unknown triage verdict, or a session with no id, exits 1
  rather than being guessed at.
- **Filters** (`--state`, `--repo`, `--machine`) can be combined, and every one of them applies to
  `--json` as well: the output is the same bare array, narrowed. `--repo` matches the repository
  folder name, ignoring case but not spaces, or the full path - a Windows path ignoring case and
  slash direction, any other path exactly (as `repo list` and `worktree list` do); `--machine`
  ignores case. An
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

### Session Hand-Over

```
USAGE: cc-devthrottle session hand-over SESSION --to fleet-manager|owner [--json]

ARGUMENTS:
  SESSION  The session: its number, an id prefix, a full id, or its exact name [required]

OPTIONS:
  --to     Who owns it afterwards: fleet-manager, or owner (no owning session) [required]
  --json   Print the Gateway's answer unchanged
```

Changes who owns a session that is already running (the Fleet Manager mission, step 8). Handed to the
Fleet Manager, the session stops going red for the owner, and its stops and its death go to the Fleet
Manager as events; handed back, they stop going there and it asks the owner directly again. The owner is
changed where it lives - on the session's Director, through its `set-controller` command - and the
change is recorded in the governance audit trail (event type `handed-over`, with the owner's device as
the actor).

**This is the owner's change.** The Gateway allows it only from the owner's own signed-in phone or
browser - in the Cockpit, the "Hand sessions to the Fleet Manager..." list on the Fleet Manager page and
the session menu's "Hand to the Fleet Manager" and "Hand back to me". It refuses a session key, the Fleet
Manager's own included, so run from a session this command prints the Gateway's refusal and exits 1.

Every refusal is the Gateway's sentence: a session this account is not running now (another account's
session answers the same), the Fleet Manager itself, handing to a Fleet Manager the account has not
marked or that is not running, a session another RUNNING session owns (it is never taken from it), a
session already where it is being sent, a session that has ended, and a session whose Director is too
old to change an owner (update DevThrottle on that computer). A session whose owner has ended asks the
owner directly and may be handed over.

The plain output is the Gateway's sentence, `session: <id>` and `owner: <id>` (or `owner: you`), then
`help[2]:`. The change is reported from the Gateway's answer, never from the request: an answer that does
not name the session, or names an owner that does not match `--to`, exits 1 saying whether it changed is
unknown. An unknown or missing `--to` exits 2 and sends nothing.

Gateway route: `POST /gateway/fleet-manager/hand-over` with `{ "session": "<full id>", "to":
"fleet-manager" | "owner" }`, answering `{ sessionId, to, ownerSessionId, previousOwnerSessionId,
sentence, session }`; a refusal is `{ error }` with 400, 403, 404, 409 or 502 (a 403 from the
owner-only rule also carries `code: "owner_only"`, the same answer the walkthrough routes give). The session list's rows
carry `pin` (the pinned Fleet Manager and its words) and `ownerChange` (the one change of owner offered
on that row), both decided by the Gateway.

### Message Send

```
USAGE: cc-devthrottle message send TARGET MESSAGE

ARGUMENTS:
  TARGET   Session id, id prefix, or name - or 'all' for each of your workers [required]
  MESSAGE  The message text; it may span lines, and it is read, never typed [required]

OPTIONS:
  --reply-wanted     Ask the recipient for a reply. Prints a correlation id; nothing waits.
                     One session only, not 'all'.
  --reply-by INT     Minutes the recipient has to reply, with --reply-wanted: 1 to 1440 (default 60)
  --everyone         Queue a copy for EVERY session in the account (needs --reason and --grant)
  --reason TEXT      Why a fleet-wide broadcast is warranted. Required with --everyone
  --grant TEXT       A human-issued broadcast grant id (issue #1229)
```

A message is QUEUED, never typed. The Gateway writes it to the recipient's inbox and the command
prints `Queued`, never "delivered"; when the recipient is not working and its composer is empty, its
Director types one doorbell line telling it to run `message inbox`, and it reads the full text there. You may
message only the session that started you and the sessions you started, at most 6 messages an hour
and 1 per recipient every 10 minutes. Anything else is refused, and the refusal says why - put what
you would have said in your report instead.

An ambiguous id prefix or name is refused with the list of candidates. No message is sent.

`--everyone`, `--reason` and `--grant` apply only to a fleet-wide broadcast (`message send all
--everyone`). Given anywhere else they exit 2 and nothing is sent, rather than being ignored. A
message the Gateway does not queue exits 1 with `Not queued:` and the Gateway's reason, verbatim, on
standard error. An identical message already waiting unread is not queued again, and that is a
success. `message send all` prints how many copies were queued and names each worker, by full id,
whose copy was not. It exits 0 when a copy was queued or an identical one is already waiting for at
least one worker, and 1 when nothing was queued and nothing was waiting - including when you have no
workers.

With `--reply-wanted`, the command also prints `correlation id: <id> (reply wanted by <time>)`. The
reply arrives in YOUR inbox and you are rung for it; if none arrives by the deadline, a no-reply
notice from the Gateway arrives instead. `--reply-by` without `--reply-wanted`, and `--reply-wanted`
with `all`, are usage errors (exit 2). A deadline outside 1 to 1440 is refused by the Gateway, and its
sentence is printed verbatim. There is no command that waits for an answer: the old blocking ask was
removed on 16 September 2026 (the Message Load mission).

### Message Reply

```
USAGE: cc-devthrottle message reply ID TEXT

ARGUMENTS:
  ID    The correlation id (or message id) of the message you are answering, from 'message inbox'
  TEXT  The answer; it may span lines, and it is read, never typed [required]
```

Queues the answer in the inbox of whoever sent the question and prints
`Reply queued for <asker> (reply <id>, answering message <id>)`. Only the session the question was
sent to may answer it, and only once; a second reply is refused. A reply is not held to the message
limits, it still lands when the asker has since lost its relationship to you, and one sent after the
deadline arrives marked late. An unknown id, or a message that did not ask for a reply, is refused
with the Gateway's sentence on standard error (exit 1). An identical reply already waiting is a
success (exit 0).

### Message Inbox

```
USAGE: cc-devthrottle message inbox [OPTIONS]

OPTIONS:
  --all        Also show the messages you read in the last 24 hours, newest first, at most 200
  --json, -j   Output the Gateway's answer as JSON
```

Prints every unread message in full, and reading marks them read - that is the acknowledgement the
sender is waiting for. Each row is headed `message`, `reply` or `no-reply notice`, using the Gateway's
own labels. A message that asked for a reply shows its deadline, its correlation id and the exact
`message reply` command that answers it; a reply or notice shows the question it is about. Because a read marks messages read before their text reaches you, a read that
failed part way is recovered with `--all`, and only for 24 hours after that read.

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
  --controlled-by TEXT  WHO OWNS IT: 'self' or 'none'. Required from a session
  --standalone          The USER owns it: no controller (same as --controlled-by none)
```

Prints the new session's short id and full GUID, then `help[]` lines that name the new session by
its full id; the session then appears in `cc-devthrottle session list`. A non-existent repository
path exits non-zero with a clear error. Warnings (no `--name`, a mission that could not be inherited)
go to standard error.

**A session-initiated spawn must say who OWNS the new session.** Every session has exactly one
owner and it is either another session or the user; no owner means the user. From inside a session
both are possible, so there is no default between them and the spawn is refused until you say:
`--controlled-by self` (you own it - it stays quiet and reports back to you), `--standalone` (the
user owns it - it goes red and asks him, and you will not hear from it). A session may not name
another session as the owner: the Gateway refuses it, because the owner is one of the only sessions
the new one may message. It is what the attention rule reads afterwards, so it decides whether
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

```
USAGE: cc-devthrottle director restore WORKSPACE --director ID [OPTIONS]

OPTIONS:
  --director TEXT        The Director that brings the seats back - after a restart, the NEW one.
  --seat TEXT            Only this seat (its captured session id). Repeatable.
  --seed TEXT            <captured session id>=<path>: seed that seat from this file. Repeatable.
  --force-seat TEXT      Start this seat although an earlier start of it may have landed. Repeatable.
  --wait-seconds INTEGER How long to wait for every seat's answer [default: 600]. 0 does not wait.
  --json -j              Output raw JSON.
```

Brings a drained fleet back. This is the restore step of a Director restart, and the command a drain
writes into each seat's record. The **Director** starts every seat, on its own credential, under the
owner that seat had when the Gateway captured it: an owner restarted in the same drain is started
first and named by its new id, and one on another Director keeps its id. You name no owner and
cannot - a session may name only itself or the user as the owner of what it starts. A seat whose owner
did not come back is not started, and says why. Each seat that fails is reported on that seat and the
rest carry on; a seat that already came back is never started twice. A seat whose session is still
running is refused, and so is a seat whose owner is not running. Only one Director restores a workspace
at a time; a second is refused with the first one named. A seat whose earlier start was sent but never
recorded is reported as possibly started and is not started again until you check the session list and
name it with `--force-seat`. The command waits for every seat's answer. Exit 0 means every seat came
back; exit 1 means a seat failed or is still pending; exit 3 means `--wait-seconds 0` - accepted, not
waited, so nothing is known to have come back. Added 17 September 2026 (the Message Load mission).

### Fleet Manager

The Fleet Manager's stored news, the owner's standing preferences, the one digest it reads at the
start of every conversation, and the events about the sessions it owns. All of it is kept on the Gateway and belongs to the account, so a
restarted or moved Fleet Manager reads back exactly what the old one filed. A record stays open until
it is answered, and an answer is final.

Output follows the AXI standard (`docs/axi-standard.md`): a count line, rows with full ids and names,
`count: 0` when there is nothing, and `help[]` lines naming the next command. A value that is not
plain text is written as a JSON string. `--json` prints the Gateway's answer, with every filter
applied. A wrong closed value (`--risk`, `--checks`, `--status`, `--kind`) exits 2 and names the valid
values. `--session` takes a session id, id prefix, number, or exact name. `ID` takes a full record id
or the start of one.

```
USAGE: cc-devthrottle fleet digest [--session <id>] [--json]
USAGE: cc-devthrottle fleet ready "<title>" --pr <link> --risk low|medium|high
         --checks passed|failed|none --tested "<how>" --reviewed-by "<who>"
         --change "<one sentence for a user>" [--session <id>]
         [--advice "<one line>" [--pick "<option key>"]] [--json]
USAGE: cc-devthrottle fleet finding "<title>" --answer "<answer>" [--reason "<why>"]
         [--link <url> ...] [--session <id>] [--advice "<one line>" [--pick "<option key>"]] [--json]
USAGE: cc-devthrottle fleet decision "<title>" --question "<q>" --option "<a>" --option "<b>"
         [--recommend "<a>"] [--why "<why>"] [--session <id>]
         [--advice "<one line>" [--pick "<option key>"]] [--json]
USAGE: cc-devthrottle fleet outcomes [--status open|answered|all] [--kind ready|finding|decision]
         [--count/-n 1-200] [--cursor <nextCursor>] [--all] [--json]
USAGE: cc-devthrottle fleet show ID [--json]
USAGE: cc-devthrottle fleet answer ID "<the owner's words, exactly>" [--json]
USAGE: cc-devthrottle fleet advise ID "<one line of advice>" [--pick "<option key>"] [--json]
USAGE: cc-devthrottle fleet prefer "<preference, verbatim>" [--json]
USAGE: cc-devthrottle fleet preferences [--json]
USAGE: cc-devthrottle fleet forget ID [--json]
USAGE: cc-devthrottle fleet events [--all] [--count/-n 1-200] [--cursor <nextCursor>] [--every-page] [--json]
USAGE: cc-devthrottle fleet ack ID [ID ...] [--json]
USAGE: cc-devthrottle fleet ack --all [--json]
```

WHO MAY RUN THEM. Only the account's marked Fleet Manager session (`cc-devthrottle fleet-manager
set`) may use these commands with its session key. Any other session is refused with
`not_fleet_manager` and a sentence saying why. The owner, on their own signed-in phone or browser,
may list, read and answer records, manage preferences and read the digest, but does not file records.
A Director's own key may do none of it.

`fleet digest` defaults to this session (`CC_SESSION_ID`), and the Fleet Manager may read only its
own. It prints whether the session is the Fleet Manager, the marked Fleet Manager and each session
the account marked before it that still controls at least one live session, EVERY open record of the account (never a page - the command fails
rather than print a list that disagrees with the Gateway's count), the sessions owned by the current
Fleet Manager or by any earlier one (each with its state - `needs-you`, `working` or `stopped` - its
owning session, and the Wingman's latest reading), and the standing preferences. A session an
earlier Fleet Manager started is shown with that owner; once that earlier one has ended, the owner can
hand it to the new Fleet Manager (`session hand-over`). The Gateway remembers the 20 sessions the account marked most recently; an earlier one is
forgotten, and its sessions are no longer listed.

`fleet outcomes` lists the open records by default, newest first, one page at a time. When the
filter matches more than the page, the count line says `count: <shown> of <total>`, the next line is
`nextCursor: <cursor>`, and the help names the command for the next page (`--cursor <cursor>`, with
the same `--status`, `--kind` and `--count`). `--all` follows every page and lists every record; with
`--json` it answers one list with `hasMore: false`. Pages follow a cursor, not a position, so a record
answered between pages is neither skipped nor repeated; a record filed after the first page appears
when you list again from the start. A filter that matches nothing says how many records there are in
all. An `ID` given as the start of an id is matched against every record, every page followed.

`fleet answer` is final: when two callers answer the same record at once - the owner on the phone and
the Fleet Manager, or two Gateway instances - exactly one answer is kept, and the other is refused
(409, `already_answered`). The record keeps who answered (`answeredByRole`: `owner` or
`fleet-manager`). A decision's answer need not be one of its options; the record says whether it was.

`--advice` (on `fleet ready`, `fleet finding` and `fleet decision`) and `fleet advise` store the Fleet
Manager's ONE line of advice on a record, shown to the owner in the walkthrough. Only the marked Fleet
Manager may write it; the owner's own device is refused. The Gateway refuses a line break, and more than
300 characters, with a sentence saying why. `--pick` names the Wingman option the Fleet Manager would
choose, by its key; it goes with `--advice` and must be one of the options of the session's current
Wingman reading (the refusal lists them). `fleet advise` replaces the advice and pick on an open record;
leaving `--pick` out clears the pick; an answered record is refused (409, `already_answered`). Filing
without `--advice` sends exactly the body it always sent. `fleet show` prints `advice`, `pick` and, when
set, `ownerNote`. The records' `--json` shape keeps every field it had and adds `advice`,
`fleetManagerPick`, `adviceSetAtUtc`, `ownerNote` and `ownerNoteAtUtc`.

`fleet digest` also prints `answered: <n> in the last 24 hours` and an `answered` table (id, kind, who
answered, the words, title) - how the Fleet Manager learns what the owner decided in the walkthrough,
where nothing is typed to it (JSON: `recentlyAnswered`, `answeredWithinHours`). Its open-records table
carries each record's `advice` and `ownerNote` (a snooze from the walkthrough).

`fleet events` lists the events about sessions a Fleet Manager owns - a `stop` (with the Wingman's
reading of it, or why there is none) or a `died` (exited or crashed). By default only the
unacknowledged ones, oldest first; `--all` includes acknowledged ones, newest first. One page at a
time, exactly as `fleet outcomes` pages: past the page the count line says `count: <shown> of
<total>`, the next line is `nextCursor: <cursor>`, and the help names `--cursor <cursor>` for the next
page; `--every-page` follows every page. A cursor is issued for one of the two lists and is refused
(400) for the other. A stop still waiting for the Wingman's reading is listed with verdict `waiting`
and the Gateway's sentence saying so; it is not delivered and cannot be acknowledged until its reading
is stored - or, after 5 minutes without one, until it is given the reason there is none and delivered
as that. A reading that ends as `cannot-tell` or fails is delivered as that. The Gateway also delivers them to the
Fleet Manager itself: one prompt, starting `[Fleet Manager events]`, at its own turn end or a few
seconds after an event arrives while it is idle - and typed only if the Director finds the Fleet
Manager waiting for a prompt at that moment. It is refused while the owner has typed text into the
Fleet Manager and not sent it: nothing is typed after the owner's words, and the events wait until the
owner sends them. The Director counts the draft from the owner's first typed character until it sees a
submission - an Enter or a sent prompt; rubbing the text out with Backspace, or the Fleet Manager
working, does not clear it. The Director then holds the session's input from that check until the
prompt's Enter, at most 5 seconds: anything the owner types meanwhile is written after it, in order, so
the owner's Enter can never submit the event text. A send that cannot finish in that time is abandoned,
the text it typed is removed from the composer, and it counts as refused. The 5-second bound holds
for every Fleet Manager that is sent events, because they are sent only to a terminal session, whose
text is written a character at a time; a session whose terminal submits a whole turn in one call
(embedded, pipe or studio) cannot take that call back once started, so it is sent no events and each
attempt is refused with that reason. A refused send leaves the events for the Fleet Manager's next idle
moment. When events are held back for the owner's unsent text or for such a terminal, `fleet events`
prints `deliveryNote:` and `fleet digest` prints `eventsDeliveryNote:` with the Gateway's sentence
(JSON: `deliveryNote`, `eventsDeliveryNote`); otherwise neither line is printed. A Director too old to make that check is sent
nothing, and an answer that does not say the check was made does not count as a delivery.
One prompt carries at most 200 events, the oldest owed; it says how many more wait, and those are sent
at the next idle moment. Delivery is at least once: every event carries its id, the same event can be sent again (a Gateway
that stops between typing and saving the delivery), and the Fleet Manager ignores an id it has
already handled. A stop is stored the moment it is seen and delivered once the Wingman's reading (or
the reason there is none) is attached; a death is stored when an owned session exits, crashes or is
removed, when a connected Director that has reported its sessions leaves it out, or when its Director
shut down - never while its Director is only disconnected or silent, however long, and never while
another Director reports it running. `fleet ack` takes full ids
or the start of each (matched against every event, every page followed), or `--all`, which closes
only the events delivered to the calling session; if one id is not an event of this account, or is a
stop still waiting for its reading (409, `reading_pending`), nothing is acknowledged. Only the
account's marked Fleet Manager session may acknowledge. `fleet digest` lists the oldest 200
unacknowledged events too, says `events: <shown> of <total> unacknowledged` and how many wait for
their reading, and when more remain prints `eventsMoreRemain:` with the `fleet events --cursor`
command that reaches them (JSON: `eventsTotal`, `eventsWaitingForReading`, `eventsHasMore`,
`eventsNextCursor`). Pull request and
report events are not built yet (a later part of phase 1).

`session report` from a session a Fleet Manager owns sends nothing and says so: the Gateway tells the
Fleet Manager. Whether the owner is a Fleet Manager is the roster row's `ownedByFleetManager`, which
the Gateway works out.

Gateway routes: `/gateway/fleet-manager/outcomes` (GET, POST), `/outcomes/{id}` (GET),
`/outcomes/{id}/answer` (POST, 409 when already answered), `/outcomes/{id}/advice` (PUT
`{ advice, pick }`, the Fleet Manager's session key only; 400 for a second line, more than 300
characters, or a pick that is not a current option; 409 when answered), `/preferences` (GET, POST),
`/preferences/{id}` (DELETE), `/digest?session=<id>` (GET),
`/events?status=unacknowledged|all&count=&cursor=` (GET, answering `count`, `total`, `hasMore`,
`nextCursor` and `events`), `/events/ack` (POST `{ ids: [...] }` or
`{ all: true }`, 404 naming any unknown id, 409 `reading_pending` naming a stop still waiting for its reading; only the Fleet Manager's own session key may acknowledge, and
`all` closes only the events delivered to that session). `GET /outcomes` takes `status`, `kind`,
`count` and `cursor`, and answers `count` (this page), `total` (every match, counted by the Gateway),
`hasMore`, `nextCursor` (null on the last page) and `outcomes`. A cursor the Gateway did not issue is
refused with 400. A refused caller gets 403 with
`code: not_fleet_manager`.

Where the Fleet Manager runs is set in Settings, on the Fleet Manager tab, not from the command line.
Those routes are the owner's and refuse a session key: `/gateway/fleet-manager/placement` (GET, and PUT
`{ agent, machine }`), `/start`, `/restart` and `/move` (POST; move takes `{ agent, machine }`). A
start runs on the saved computer only, and the launcher starts a Director there when none is running.
A restart or move closes the old Fleet Manager only after its current turn ends.

The Cockpit's Fleet Manager page (`/fleet-manager`, where the Cockpit opens; `/assistant` now redirects
there) reads `/gateway/fleet-manager/page` (GET, the owner's; a session key is refused and reads the
digest instead): the cards drawn from the records, the right panel (waiting on you, under way, answered
today, and the sessions that still ask the owner directly - their count, and the list of them with a
hand-over button each) and the rail's badge count. A card button answers the record and then sends the
same words to the Fleet Manager as a prompt. Hand over is `POST /gateway/fleet-manager/hand-over` (see
Session Hand-Over).

"Take me through them" (`/fleet-manager/walkthrough` in the Cockpit, opened from the Waiting on you
panel) reads `/gateway/fleet-manager/walkthrough?round=<id>,<id>` (GET): one round of the waiting
records, in the page's order, each with the Wingman's reading of its session (or the sentence saying
why there is none), the Fleet Manager's advice, both picks marked, and whether snooze and close are
offered. The client sends the round's ids back so answered items stay in the list as done. Three owner
routes record what happened, each only AFTER the session took it: `/walkthrough/{id}/answered` (POST
`{ verdictId, optionIndexes }`; recorded only when the Wingman's answer route marked that verdict
answered, with the options' own words), `/walkthrough/{id}/snoozed` (POST; writes the record's
`ownerNote` only when the session is snoozed; the record stays open) and `/walkthrough/{id}/close`
(POST; decides again whether the session may be closed - never with uncommitted, unpushed or unmerged
work, and never when the Gateway cannot tell (409 `close_refused` with the sentence) - then runs the one
stop handler and records the answer `Close the session.` only when a session was stopped). All four
are the owner's: a session key and a Director's key are refused.

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
  --timeout-ms INTEGER  Kept for callers that still pass it; nothing waits any more (default 25000)
```

Windows only (it drives a command prompt session). Spawns one throwaway worker it owns, checks it is
listed, queues a message for it and checks the Gateway answered `queued`, flags it for deletion, and
prints PASS/FAIL. It does not prove the message was read: only the worker's own key can read its
inbox. The last check passes when the Director accepts the deletion flag. The session
stays listed until the Director removes it: its reaper sweeps every 30 seconds and removes a flagged
session only once its 30-second grace period has passed and the session is no longer working. A
session that stays working is skipped on every sweep, so no removal time is promised.

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

### Worktree

Two different things share this word.

`cc-devthrottle worktree list` is the FLEET view, served by the Gateway: every machine's worktrees,
with the Gateway's verdict, the size, and which session is in each. `--repo` there is a repository
NAME and `--state` filters by verdict.

Everything else is this machine's POOL of reusable worktrees, and every one of those commands runs
the separate `cc-worktrees` tool: it is handed the arguments exactly as they were typed, its output
is printed exactly as it printed it, and its exit code is this command's exit code. Nothing about
worktrees is decided here. The rule that decides whether a worktree can be reset without throwing
away a commit that never reached the remote lives in `cc-worktrees`, in one place, and is documented
in `tools/cc-worktrees/README.md`.

```
USAGE: cc-devthrottle worktree COMMAND [ARGS]...

COMMANDS:
  list [--repo <name>] [--state <verdict>] [--json]
  list --pool [--repo <path>] [--fields <a,b>] [--json]
  get --repo <path> --holder <text> [--pool-size N] [--json]
  return <path-or-slot> --lease <id> [--repo <path>] [--json]
  lease <path-or-slot> --holder <text> [--reclaim-held] [--repo <path>] [--json]
  destroy <path-or-slot> [--yes] [--allow-held] [--allow-in-use] [--repo <path>] [--json]
```

`worktree list --pool --json` prints exactly what `cc-worktrees list --json` prints for the same
arguments, character for character.

`--state` belongs to the fleet listing and `--fields` to the pool listing. Each is refused with a
usage error (exit 2) on the other, rather than accepted and ignored.

`destroy` is a dry run that says what it would remove unless `--yes` is given, and it is refused
whatever the recorded state says unless the work in the worktree is proven landed at that moment.
This command adds no flag of its own, so those defaults are `cc-worktrees`' own.

Exit codes are `cc-worktrees`' own: 0 success, 1 error, 2 usage error, 3 the worktree is held with
the reason, 4 the pool is full. A caller branches on 3 and 4, so neither is ever folded into a
generic failure.

If `cc-worktrees` is not installed, these commands refuse with a plain error naming it and saying to
run `cc-devthrottle setup update`, and exit 1. They never answer locally instead. Point them at a
`cc-worktrees` that is not the installed one - a build of your own, or the copy in a checkout - with
`CC_WORKTREES_EXECUTABLE`, which takes the path to the executable or to the tool's `main.py`.

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
  import   OWNER: import every KEY=VALUE line of a file (credentials.env) as its own entry
  edit     OWNER: change an entry's username, addresses, uses, agents, notes or variable - not its secret
  remove   OWNER: remove an entry
  list     Entries agents may use: names, usernames, allowed addresses. Never secrets (--all, --json)
  run      Run a command with the secret supplied; output comes back with the secret removed
  get      Print a SETTING's value (never a secret)
  login    Fill and submit the login form in a Director-owned browser; refuses any other address
  log      Show the audit log (-n, --json)
  version  Print the version
```

An entry is a **secret** (the default: hidden from every output, never printed) or a **setting** - a value
that is not secret, such as a host, an email address or an identifier. Settings live in cc-secrets beside
the secrets so every credential has one home; a setting can be read with `get` and is never hidden from
output, because hiding a host name would blank it out of everything that prints it. The kind is stored in
`secrets.json` beside the value: editing a secret's kind to `setting` by hand makes `get` print it.

`add`, `import`, `edit` and `remove` refuse to run inside a DevThrottle session. There is no option that takes the
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
  --setting               Store a setting that is not secret: readable with get, not hidden
  --env-name TEXT         The variable run supplies it in [default: CC_SECRET]
```

With the secret piped on stdin, `--username`, `--domains` and `--agents`/`--no-agents` are required.

### cc-secrets edit

```
USAGE: cc-secrets edit [OPTIONS] NAME

OPTIONS:
  --username TEXT         The user name that goes with it
  --domains TEXT          Comma-separated site addresses login may fill; --domains= clears them
  --uses TEXT             Comma-separated: login, run
  --agents / --no-agents  Whether sessions on this machine may use it
  --notes TEXT            A note for yourself; agents see it in list
  --env-name TEXT         The variable run supplies it in
```

Changes only the details given; the secret (or a setting's value) is never touched, so no prompt. To clear a field
write it with an equals sign and nothing after it (`--username=`, `--notes=`, `--domains=`): Windows PowerShell 5.1
drops an empty `""` argument, so `--username "" --no-agents` would take `--no-agents` as the user name. A value
that starts with `--` is refused for that reason, by `edit` and by `add`. For example,
make an imported password usable by `login`:
`cc-secrets edit mindzie-qa-password-local --username qa@mindzie.com --domains https://localhost:7330 --uses login,run`

### cc-secrets import

```
USAGE: cc-secrets import [OPTIONS] FILE

OPTIONS:
  --agents / --no-agents  Whether sessions on this machine may use the imported entries [required]
  --uses TEXT             Comma-separated: login, run [default: run]
  --settings TEXT         Comma-separated keys to import as SETTINGS (not secret)
  --skip TEXT             Comma-separated keys NOT to import
  --replace               Replace entries that already exist
  --dry-run               Show what would happen, by name only, and change nothing
```

Each `KEY=VALUE` line becomes its own entry, named after the key in lower case with hyphens
(`POSTHOG_PERSONAL_API_KEY` becomes `posthog-personal-api-key`), whose variable name is the key itself -
so `run` supplies it exactly where a program that read the file expects it. Blank lines and `#` comments
are skipped. The whole store is saved once. No value is ever printed: a line that cannot be imported (too
short, for example) is reported by its key, and the rest are imported.

Name the keys that are not secret (hosts, project identifiers, email addresses) with `--settings`, so they
are stored as settings: a secret is hidden from every command's output, and a host name stored as a secret
would vanish from everything that prints it.

Example: `cc-secrets import $env:LOCALAPPDATA\cc-director\config\credentials.env --agents --settings POSTHOG_HOST,POSTHOG_API_HOST --dry-run`

### cc-secrets run

```
USAGE: cc-secrets run [OPTIONS] NAME -- COMMAND...

OPTIONS:
  --with TEXT       Another entry to supply at the same time, in its own variable (repeatable)
  --via TEXT        stdin, env or askpass [default: env when the entry has its own variable name
                    or --with is used, otherwise stdin]
  --env-name TEXT   Variable name for --via env with one entry [default: the entry's own
                    variable name, otherwise CC_SECRET]
  --timeout FLOAT   Seconds before the command is stopped; 0 means no limit [default: 600]
  --json            Print JSON
```

Examples:

- `cc-secrets run devlinux -- sudo -S apt-get update`
- `cc-secrets run posthog-personal-api-key -- python query.py` (the script reads `POSTHOG_PERSONAL_API_KEY`)
- `cc-secrets run godaddy-key --with godaddy-secret -- python dns.py` (both variables are set)

The command's output is captured and returned when it ends, with every stored secret removed; it is not
streamed.

### cc-secrets get

```
USAGE: cc-secrets get [OPTIONS] NAME

OPTIONS:
  --json   Print JSON
```

Prints a setting's value, for example `cc-secrets get posthog-host`. A secret is refused - use it with `run`.
A setting can also be supplied to a command with `run`, beside a secret:
`cc-secrets run posthog-personal-api-key --with posthog-host -- python query.py`.

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

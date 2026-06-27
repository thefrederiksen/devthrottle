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
  --persona        -p   Persona: mindzie, center_consulting, personal [default: personal]
  --destination    -d   Where to post (URL)
  --context-url    -c   What we're responding to (URL)
  --context-title       Title of content we're responding to
  --tags           -t   Comma-separated tags
  --notes          -n   Notes for reviewer
  --created-by          Agent/tool name
  --send-timing    -st  immediate, scheduled, asap, hold [default: asap]
  --scheduled-for       ISO datetime for scheduled send
  --send-from      -sf  Account: mindzie, personal, consulting
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

## cc-settings

Manage cc-director configuration and system settings.

```
USAGE: cc-settings [OPTIONS] COMMAND [ARGS]...

OPTIONS: --version -v, --help

COMMANDS:
  show [SECTION]                       Display current settings (all or one section)
  get KEY                              Get a specific setting value
  set KEY VALUE                        Set a configuration value
  list                                 List all setting keys with values
  path                                 Show the config file location
```

### cc-settings show

```
USAGE: cc-settings show [OPTIONS] [SECTION]

ARGUMENTS:
  SECTION  Section name (e.g. screenshots, vault, llm) [optional]

OPTIONS:
  --json  -j  Output as JSON
```

### cc-settings get

```
USAGE: cc-settings get [OPTIONS] KEY

ARGUMENTS:
  KEY  Dotted setting key (e.g. screenshots.source_directory) [required]

OPTIONS:
  --json  -j  Output as JSON
```

### cc-settings set

```
USAGE: cc-settings set [OPTIONS] KEY VALUE

ARGUMENTS:
  KEY    Dotted setting key [required]
  VALUE  New value [required]

OPTIONS:
  --json  -j  Output as JSON
```

---

## DevThrottle Command (cc-devthrottle)

Unified DevThrottle command surface for session-to-session messaging, session management, Gateway
schedules, and local setup diagnostics. Fleet/session/message commands run inside a DevThrottle
session and talk to that session's own Director through `CC_DIRECTOR_API`. Schedule commands talk
to the Gateway using `gateway.url` and `gateway.token` from config.

### cc-devthrottle

Unified DevThrottle command surface for fleet, session, and message management.

```
USAGE: cc-devthrottle [OPTIONS] COMMAND [ARGS]...

COMMANDS:
  actions          List agent-discoverable actions.
  session list     List every session in the fleet.
  session whoami   Show this session's own fleet identity.
  session rename   Rename a session, defaulting to the current session.
  session spawn    Open a new session on the local Director.
  message send     Send a message to one session, or broadcast with all.
  message ask      Ask one session a question and print its answer.
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
  --json  -j  Output raw JSON
```

Output columns: short id, name, machine, repository, status. Your own session is marked `(you)`.

### Session Whoami

```
USAGE: cc-devthrottle session whoami
```

Shows this session's own id, name, machine, and repository.

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
  --type TEXT           Session type: Developer, Implementation, Discuss, Product, QA, Support
  --command TEXT        For --agent RawCli: the executable to run (e.g. cmd, pwsh)
  --command-args TEXT   For --agent RawCli: arguments for the command
```

Prints the new session's short id and full GUID; the session then appears in
`cc-devthrottle session list`. A non-existent repository path exits non-zero with a clear error.

### Selftest

```
USAGE: cc-devthrottle selftest [OPTIONS]

OPTIONS:
  --timeout-ms INTEGER  How long the ask step waits for the responder (default 25000)
```

Spawns two throwaway sessions, lists them, sends to one, asks the other, tears them down, and prints
PASS/FAIL.

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

## Director Control API

The Director exposes a loopback REST API (default port range 7879-7898). All session
endpoints are under `/sessions/{sessionId}/`.

### POST /sessions/{id}/voice-turn

Server-side walkie-talkie turn (issue #351). One call = one complete turn:
audio or pre-transcribed text goes in, a spoken summary comes out.

**Input:** `multipart/form-data` or JSON

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `audio` | binary | either/or | Raw audio bytes (AAC, WAV, WebM). Director transcribes via Whisper. |
| `text`  | string | either/or | Pre-transcribed text. Bypasses transcription - used by tests and non-audio callers. |

**Response:** `text/event-stream` (Server-Sent Events). One `data:` JSON per stage,
then a final `reply` event.

```
data: {"stage":"transcribing"}
data: {"stage":"transcript","text":"what should we focus on next?"}
data: {"stage":"waiting"}
data: {"stage":"thinking"}
data: {"stage":"summarizing"}
data: {"stage":"reply","summary":"Here is what I found. The main decision is X.","audioBase64":"<mp3 bytes base64>"}
```

On any error the stream ends with `{"stage":"error","message":"<reason>"}`.

The `summary` field is the wingman-produced plain-prose spoken version (2-3 sentences,
no markdown). The `audioBase64` field contains the TTS bytes (MP3) for that summary.
If the wingman or TTS is unavailable, the fallback path fires: a plain-text excerpt is
synthesized instead. The reply event is always emitted - never goes silent.

**Status codes:**
- `200` - SSE stream (even on turn errors, which surface as `{"stage":"error",...}`)
- `400` - Invalid session id format (JSON body)
- `404` - Session not found (JSON body)
- `410` - Session has already exited (JSON body)

**Multiple turns = multiple calls.** No persistent voice session state on the server
between calls.

---

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

# Tools Overview

CC Director includes 33 command-line tools for document conversion, media processing, email, browser automation, social media, desktop automation, and AI workflows. All tools are installed to `%LOCALAPPDATA%\cc-director\bin\` and are available on your PATH.

## Quick Reference

### Documents

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-pdf | Markdown to PDF with themes | Chrome/Chromium |
| cc-html | Markdown to HTML with themes | None |
| cc-word | Markdown to Word (.docx) with themes | None |
| cc-excel | CSV/JSON/Markdown to formatted Excel workbooks | None |
| cc-powerpoint | Markdown to PowerPoint presentations | None |

### Email

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-gmail | Gmail CLI: read, send, search, labels, calendar, contacts | Google OAuth |
| cc-outlook | Outlook CLI: email, calendar, attachments, folders | Azure OAuth |

### Web and Social

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-browser | Browser automation with persistent connections | Chrome Extension |
| cc-fox-browser | Anti-detection browser automation (Camoufox/Firefox) | Node.js, Camoufox |
| cc-reddit | Reddit automation with human-like delays | Playwright, cc-browser |
| cc-twitter | Twitter/X CLI: post, reply, thread, like, retweet, timeline | Twitter API v2 credentials |
| cc-facebook | Facebook Page CLI: post, comment, reply, list via Graph API | Facebook App + Page Access Token |
| cc-youtube | YouTube CLI: upload, comment, reply, list via Data API v3 | Google OAuth (YouTube Data API) |
| cc-crawl4ai | AI-ready web crawler to clean markdown | Playwright browsers; not shipped (dev-only, build from repo) |
| cc-websiteaudit | Website SEO/security/AI readiness audit | Node.js, Chrome (not yet built) |
| cc-brandingrecommendations | Branding action plans from audit data | Node.js |

### Desktop Automation

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-click | Windows UI automation: click, type, screenshot | Windows, .NET |
| cc-trisight | 3-tier UI element detection (UIA + OCR + pixel) | Windows, .NET |
| cc-computer | AI desktop agent with screenshot-in-the-loop | Windows, .NET, OPENAI_API_KEY |

### Media

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-image | Image generation, analysis, OCR | OPENAI_API_KEY |
| cc-voice | Text-to-speech (OpenAI TTS) | OPENAI_API_KEY |
| cc-whisper | Audio transcription and translation | OPENAI_API_KEY |
| cc-video | Video info, audio extraction, screenshots, frames | FFmpeg |
| cc-transcribe | Video/audio transcription with screenshots | FFmpeg, OPENAI_API_KEY |
| cc-photos | Photo scanning, duplicates, AI descriptions | OPENAI_API_KEY |
| cc-youtube-info | YouTube transcript/metadata extraction | None |

### Data and Utilities

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-vault | Personal vault: contacts, tasks, goals, docs, RAG | None |
| cc-hardware | System hardware info (RAM, CPU, GPU, disk) | None |
| cc-comm-queue | Communication Manager approval queue | None |
| cc-settings | CC Director configuration management | None |
| cc-docgen | C4 architecture diagrams from YAML | Graphviz; not shipped (dev-only, build from repo) |
| cc-director-setup | Windows installer for CC Director | None |
| cc-posthog | PostHog analytics: page views, funnels, events, recordings | PostHog account + API key |

---

## Documents

### cc-pdf

Convert Markdown to PDF with built-in themes.

```bash
cc-pdf report.md -o report.pdf
cc-pdf report.md -o report.pdf --theme boardroom
cc-pdf report.md -o report.pdf --page-size a4 --margin 1in
```

**Themes:** boardroom, terminal, paper (default), spark, thesis, obsidian, blueprint

**Options:** `-o` output file, `--theme` theme name, `--css` custom CSS, `--page-size` a4/letter, `--margin` page margin

### cc-html

Convert Markdown to styled HTML with built-in themes.

```bash
cc-html report.md -o report.html
cc-html report.md -o report.html --theme terminal
```

**Themes:** boardroom, terminal, paper (default), spark, thesis, obsidian, blueprint

**Options:** `-o` output file, `--theme` theme name, `--css` custom CSS

### cc-word

Convert Markdown to Word (.docx) documents with built-in themes.

```bash
cc-word report.md -o report.docx
cc-word report.md -o report.docx --theme boardroom
```

**Themes:** boardroom, terminal, paper (default), spark, thesis, obsidian, blueprint

**Options:** `-o` output file, `--theme` theme name

### cc-excel

Convert CSV, JSON, and Markdown tables to formatted Excel workbooks with themes, charts, and formulas.

```bash
cc-excel from-csv sales.csv -o sales.xlsx --theme boardroom
cc-excel from-json data.json -o report.xlsx
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1
cc-excel from-csv sales.csv -o report.xlsx --summary all --highlight scale
cc-excel from-spec workbook.json -o output.xlsx
```

**Subcommands:** `from-csv`, `from-json`, `from-markdown`, `from-spec`

### cc-powerpoint

Convert Markdown to PowerPoint presentations.

```bash
cc-powerpoint slides.md -o deck.pptx --theme boardroom
```

Use `---` to separate slides. First `# Title` becomes the title slide.

---

## Email

### cc-outlook

Outlook CLI with email and calendar support via Microsoft Graph API.

```bash
cc-outlook list --unread
cc-outlook read <message_id>
cc-outlook search "project update"
cc-outlook reply <message_id>
cc-outlook forward <message_id>
cc-outlook calendar events -d 14
cc-outlook calendar today
cc-outlook calendar search "standup"
cc-outlook folders
cc-outlook attachments <message_id>
```

### cc-gmail

Gmail CLI with multi-account support.

```bash
cc-gmail list --unread
cc-gmail read <message_id>
cc-gmail search "from:someone@example.com"
cc-gmail reply <message_id>
cc-gmail labels
cc-gmail stats
cc-gmail calendar
cc-gmail contacts
```

---

## Web and Social

### cc-browser

Browser automation via Chrome Extension + Native Messaging with persistent connections.

```bash
cc-browser daemon
cc-browser connections open myconnection
cc-browser navigate --url "https://example.com"
cc-browser snapshot --interactive
cc-browser click --ref e3
cc-browser type --ref e4 --text "hello"
cc-browser screenshot
cc-browser connections close myconnection
```

### cc-fox-browser

Anti-detection browser automation using Camoufox (custom Firefox). Bypasses Cloudflare Turnstile and other bot detection that blocks Chromium-based automation. Same API as cc-browser.

```bash
cc-fox-browser daemon
cc-fox-browser start --workspace upwork
cc-fox-browser navigate --url "https://www.upwork.com"
cc-fox-browser snapshot --interactive
cc-fox-browser click --ref e3
cc-fox-browser type --ref e4 --text "hello"
cc-fox-browser tabs
cc-fox-browser stop
```

**Port:** 9380 (cc-browser uses 9280)

**Profiles:** Persistent per workspace at `%LOCALAPPDATA%\cc-fox-browser\camoufox-{workspace}\`

**When to use:** Sites with Cloudflare Turnstile or aggressive bot detection that blocks cc-browser.

### cc-reddit

Reddit automation with human-like delays and random jitter.

```bash
cc-reddit status
cc-reddit feed
cc-reddit post <url>
cc-reddit create <subreddit> --title "Title" --body "Body text"
cc-reddit comment <url> --text "Comment text"
cc-reddit reply <url> --text "Reply text"
```

**Important:** Always use cc-reddit for Reddit operations. Never use cc-browser directly with Reddit.

### cc-twitter

Twitter/X CLI using Twitter API v2 with OAuth 1.0a.

```bash
cc-twitter auth                              # Store API credentials
cc-twitter status                            # Show auth status and account info
cc-twitter post "Tweet content"              # Create a tweet
cc-twitter reply "Reply text" --to <url>     # Reply to a tweet
cc-twitter thread "First" "Second" "Third"   # Post a multi-tweet thread
cc-twitter like <tweet_url>                  # Like a tweet
cc-twitter retweet <tweet_url>               # Retweet
cc-twitter timeline --count 20               # Show home timeline
cc-twitter mentions --count 10               # Show mentions
cc-twitter delete <tweet_url>                # Delete own tweet
```

**Setup:** Register an app at developer.x.com, generate API Key, API Secret, Access Token, and Access Token Secret. Run `cc-twitter auth` to store them.

### cc-facebook

Facebook Page management via Graph API v19.0. Supports page posting only (personal profile posting is restricted by Meta).

```bash
cc-facebook auth                             # Store App ID, Secret, Page Token, Page ID
cc-facebook status                           # Show auth status and page info
cc-facebook pages                            # List managed pages
cc-facebook post "Message" --link <url>      # Create a page post
cc-facebook comment <post_url> "Comment"     # Comment on a post
cc-facebook reply <comment_id> "Reply"       # Reply to a comment
cc-facebook list --count 10                  # List recent page posts
cc-facebook delete <post_id>                 # Delete a post
```

**Setup:** Create a Facebook App at developers.facebook.com, obtain a long-lived Page Access Token with `pages_manage_posts` permission. Run `cc-facebook auth` to store credentials.

### cc-youtube

YouTube CLI using YouTube Data API v3 with OAuth 2.0.

```bash
cc-youtube auth                              # Run OAuth flow (requires credentials.json)
cc-youtube status                            # Show auth status and channel info
cc-youtube upload video.mp4 --title "Title" --description "Desc" --privacy public
cc-youtube list --count 10                   # List channel's videos
cc-youtube comments <video_url> --count 20   # List comments on a video
cc-youtube comment <video_url> "Comment"     # Comment on a video
cc-youtube reply <comment_id> "Reply"        # Reply to a comment
cc-youtube delete <video_id>                 # Delete a video
```

**Setup:** Enable YouTube Data API v3 in Google Cloud Console, download `credentials.json` to the config directory. Run `cc-youtube auth` to complete OAuth flow.

### cc-crawl4ai

AI-ready web crawler that converts pages to clean markdown.

**Not shipped:** not part of the installed product (not in the "ship" allowlist in
tools/registry.json). It stays in the repo and is buildable for dev with
`scripts/build-all-tools.ps1 -Tool cc-crawl4ai`.

```bash
cc-crawl4ai crawl "https://example.com" -o page.md
cc-crawl4ai crawl <url> --fit --stealth
cc-crawl4ai batch urls.txt -o ./output/
```

### cc-websiteaudit

Comprehensive website auditing across SEO, security, structured data, and AI readiness.

**Status:** Source exists but not yet built.

```bash
cc-websiteaudit example.com -o report.pdf
cc-websiteaudit example.com --format json -o audit.json
cc-websiteaudit example.com --modules technical-seo,security
```

**Modules:** technical-seo, on-page-seo, security, structured-data, ai-readiness

### cc-brandingrecommendations

Produces prioritized, week-by-week branding action plans from website audit data.

```bash
cc-brandingrecommendations --audit audit.json -o plan.md
cc-brandingrecommendations --audit audit.json --budget high --industry saas
```

---

## Desktop Automation

Three tools work together for AI-powered desktop automation:

```
cc-computer (AI Agent - the "brain")
    +-- uses TrisightCore (3-tier detection library)
    +-- calls cc-click for actions

cc-trisight (Detection CLI - the "eyes")
    +-- UI Automation + OCR + Pixel Analysis

cc-click (Automation CLI - the "hands")
    +-- Click, type, screenshot, read text, window management
```

### cc-computer

AI desktop automation agent with screenshot-in-the-loop verification.

```bash
cc-computer "Open Notepad and type Hello World"
cc-computer    # Interactive REPL mode
```

### cc-trisight

Three-tier UI element detection for Windows.

```bash
trisight detect --window "Notepad" --annotate --output annotated.png
```

### cc-click

Low-level Windows UI automation.

```bash
cc-click click <element>
cc-click type <text>
cc-click screenshot
cc-click read-text <element>
cc-click list-windows
cc-click list-elements <window>
```

---

## Media

### cc-transcribe

Transcribe video/audio with timestamps and extract screenshots at content changes.

```bash
cc-transcribe video.mp4
cc-transcribe video.mp4 -o ./output/ --no-screenshots
```

### cc-image

Image generation, analysis, and OCR using OpenAI.

**Status:** BROKEN - needs rebuild.

```bash
cc-image generate "A sunset over mountains" -o sunset.png
cc-image describe image.png
cc-image ocr screenshot.png
```

### cc-voice

Text-to-speech using OpenAI TTS.

```bash
cc-voice "Hello, world!" -o hello.mp3 --voice nova
```

**Voices:** alloy, echo, fable, nova, onyx (default), shimmer

### cc-whisper

Audio transcription and translation using OpenAI Whisper.

```bash
cc-whisper transcribe audio.mp3 -o transcript.txt
cc-whisper translate foreign-audio.mp3
```

### cc-video

Video utilities powered by FFmpeg.

```bash
cc-video info video.mp4
cc-video audio video.mp4 -o audio.mp3
cc-video screenshots video.mp4
cc-video frame video.mp4 --timestamp 01:30
```

### cc-photos

Photo organization with duplicate detection, screenshot identification, and AI descriptions.

```bash
cc-photos source add "D:\Photos" --category private --label "Family"
cc-photos scan
cc-photos discover
cc-photos dupes --cleanup
cc-photos analyze --limit 50
cc-photos search "beach vacation"
cc-photos exclude
```

### cc-youtube-info

Extract transcripts, metadata, and chapters from YouTube videos.

```bash
cc-youtube-info transcript <url> -o transcript.txt
cc-youtube-info info <url> --json
cc-youtube-info chapters <url>
```

---

## Data and Utilities

### cc-hardware

Query system hardware information.

```bash
cc-hardware          # All hardware summary
cc-hardware gpu      # GPU info
cc-hardware --json   # JSON output
```

### cc-vault

Personal data vault with contacts, documents, tasks, goals, ideas, and RAG-powered search.

```bash
cc-vault search "query"
cc-vault ask "question"
cc-vault contacts list --account personal
cc-vault contacts show <id>
cc-vault contacts search "name"
cc-vault docs import file.pdf
cc-vault lists list
cc-vault backup
cc-vault stats
```

### cc-comm-queue

CLI for adding content to the Communication Manager approval queue.

```bash
cc-comm-queue add linkedin post "Content..." --persona mindzie
cc-comm-queue list --status pending
cc-comm-queue status
```

### cc-docgen

Generate C4 architecture diagrams from YAML manifest files.

**Not shipped:** not part of the installed product (not in the "ship" allowlist in
tools/registry.json). It stays in the repo and is buildable for dev with
`scripts/build-all-tools.ps1 -Tool cc-docgen`. Requires Graphviz.

```bash
cc-docgen generate --manifest ./docs/architecture.yaml
```

### cc-posthog

PostHog analytics CLI for querying page views, funnels, events, and session recordings.

```bash
cc-posthog init                          # Configure API key and project
cc-posthog status                        # Project status and connection health
cc-posthog views --last 7d               # Page view counts by URL
cc-posthog sources --last 30d            # Traffic sources
cc-posthog visitors --last 30d           # Daily unique visitors
cc-posthog pages --last 7d               # Top pages by path
cc-posthog funnel --last 30d             # Conversion funnel analysis
cc-posthog events --last 7d              # Recent events
cc-posthog event-counts --last 7d        # Event counts by name
cc-posthog recordings --last 7d          # Session recordings
cc-posthog recording <id>               # Events in a recording
cc-posthog report --last 30d --json      # Comprehensive report
cc-posthog compare views --projects a,b  # Cross-project comparison
cc-posthog export events --json          # Export raw events
cc-posthog export funnel --csv           # Export funnel data
```

**Global options:** `--project` / `-p`, `--last` / `-l`, `--json` / `-j`, `--csv`, `--count` / `-n`

**Setup:** Requires a PostHog account and Personal API Key. Run `cc-posthog init` to configure.

### cc-director-setup

Windows installer for the entire CC Director tools suite. Downloads from GitHub releases, no admin required.

```bash
cc-director-setup
```

---

## Environment Variables

```bash
# Required for AI-powered tools
set OPENAI_API_KEY=your-key-here
```

---
name: cc-director
description: CC Director - "Mission Control for Claude Code". A desktop app that runs and supervises multiple Claude Code sessions, ships 30+ cc-* CLI tools on PATH, and exposes a REST Control API (default port 7879). Triggers on "/cc-director", "what cc tools", "list tools", "available tools", "control api", "cc-director api", "session manager", "mission control".
---

# CC Director

CC Director is a desktop application positioned as "Mission Control for Claude Code" - one place to run, observe, and orchestrate multiple Claude Code sessions side by side. It also installs a suite of `cc-*` command-line tools onto your PATH.

This skill orients a Claude Code session to what is available after CC Director is installed. It is written for the installed product, not for building from source.

## What CC Director is, in three parts

1. **Desktop app** - Windows (primary), with experimental Mac/Linux support. Runs and supervises multiple Claude Code sessions, one per repo, with real-time activity tracking, terminal buffers, voice input, and a web Manager UI.
2. **Control API** - A REST/JSON API embedded in the desktop app, on port range 7879-7898 (default 7879). Lets external callers list sessions, send prompts, interrupt, fetch terminal buffers, perform programmatic handovers, and post voice commands.
3. **`cc-*` tool suite** - 30 CLI tools installed on PATH when CC Director is set up. Each tool supports `--help` for its full command syntax.

## The cc-* tools (installed on PATH)

All tools are on PATH after install. For exact flags and examples, run any tool with `--help`.

### Documents
`cc-pdf`, `cc-html`, `cc-word`, `cc-excel`, `cc-powerpoint` - convert markdown to PDF / HTML / Word / Excel / PowerPoint with themed templates (boardroom, paper, terminal, blueprint, thesis, spark, obsidian).

### Email
`cc-gmail`, `cc-outlook` - read, send, and search Gmail and Outlook from the CLI.

### Web and social
`cc-browser` (cross-session browser automation with persistent connections), `cc-playwright` (Brave + remote-debug + CDP; the default for form fills, sign-in, OTP, and React forms), `cc-reddit` (human-paced Reddit), `cc-crawl4ai` (clean markdown extraction for RAG), `cc-websiteaudit`, `cc-brandingrecommendations`.

### Desktop automation
`cc-click` (Windows UI: click, type, screenshot, OCR), `cc-trisight` (3-tier UI element detection: UIA + OCR + pixel), `cc-computer` (AI desktop agent with screenshot-in-the-loop).

### Media
`cc-image`, `cc-voice` (text-to-speech), `cc-whisper` (audio transcription / translation), `cc-video`, `cc-transcribe`, `cc-photos`, `cc-youtube-info`.

### Data and utilities
`cc-vault` (contacts, tasks, goals, docs, RAG), `cc-hardware`, `cc-comm-queue` (queue for outbound email / social with approval), `cc-docgen` (C4 architecture diagrams from YAML), `cc-director-setup` (the installer/updater).

A handful of tools are registered but not yet built (`cc-twitter`, `cc-facebook`, `cc-youtube`, `cc-settings`, `cc-posthog`). If a tool isn't on PATH, it likely isn't built yet.

## Control API at a glance

REST/JSON on Kestrel. Default base URL: `http://localhost:7879` (the app picks a stable port in 7879-7898 if 7879 is taken). When accessed remotely, the API is fronted on the user's Tailscale tailnet rather than localhost.

Key endpoints:

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/healthz` | Health, version, director id, session count |
| GET | `/` | Session list (JSON), or HTML Manager UI for browsers |
| GET | `/sessions` | List sessions |
| GET | `/sessions/{sid}` | Session details |
| GET | `/sessions/{sid}/buffer` | Terminal output (by line range or since-timestamp) |
| POST | `/sessions/{sid}/prompt` | Send a prompt to a session |
| POST | `/sessions/{sid}/interrupt` | Send Ctrl+C |
| GET | `/sessions/{sid}/turns` | Turn history |
| POST | `/handover` | Programmatic session handover |
| POST | `/chat` | Manager chat (LLM-powered analysis of sessions) |
| POST | `/voice/command` | Voice command input |
| GET | `/repos` | Registered repositories |
| POST | `/sessions` | Create a new session |

## Sample API calls

```
# Health check
curl http://localhost:7879/healthz

# List sessions as JSON
curl -H "Accept: application/json" http://localhost:7879/sessions

# Send a prompt to a known session
curl -X POST http://localhost:7879/sessions/<sid>/prompt \
  -H "Content-Type: application/json" \
  -d "{\"text\": \"Fix the bug in auth.js\"}"
```

## When this skill is the right thing to consult

- "What cc-* tools do I have for X?" - look in the tool list above; run the tool with `--help` for syntax.
- "How do I call CC Director from a script?" - point at the Control API section.
- "Is the app running / what version?" - `curl http://localhost:7879/healthz`.

## What this skill does NOT do

- It does not replace `<tool> --help`, which has the authoritative flags and examples for each tool.
- It does not document every API endpoint; the table above covers the common ones.

---

**Skill Version:** 3.0 (end-user)
**Last Updated:** 2026-06-03

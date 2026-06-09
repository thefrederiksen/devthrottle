# CC Director - CenCon Documentation Index

**Version:** 0.6.18
**Last Updated:** 2026-06-09
**Schema:** CenCon Method v1.0

---

## Overview

CC Director is a Windows desktop application (Avalonia; the original WPF UI has been removed) for managing multiple Claude Code CLI sessions simultaneously. It provides multi-session management, real-time activity tracking, session persistence, embedded console hosting, git integration, and voice mode. Each Director also runs a loopback HTTP **Control API**, and a per-machine **Gateway** publishes the local Directors over the owner's Tailscale tailnet for remote/mobile access. A background **scheduler** (single-leader across Directors) fires comm-queue runners, and a read-only **Wingman** derives session status from the terminal.

This document serves as the central reference combining product requirements, system architecture, and security profile.

**Recent additions (as of 0.6.18):** the Gateway now hosts the fleet **wingman turn-brief pipeline** (TurnEndWatcher -> single warm-brain GatewayTurnBriefAgent -> append-only GatewayTurnBriefStore), a central **Key Vault** for fleet secrets (plain-JSON, token-gated `/vault/keys`, seeded once from env), Gateway **Settings** endpoints, and session **Restore** (RestoreContextBuilder, #212). The **Cockpit** Blazor dashboard (served behind the Gateway's one-URL front door) renders the structured brief via `BriefPane` and the brief-feedback corpus. Core gained immutable **session types** (Implement/Discuss/BugReport, #211) and **session groups** (#225), and an **About/diagnostics** payload. See `architecture_manifest.yaml` for the authoritative component list.

---

## Architecture Diagrams

### Context Diagram (C4 Level 1)

![Context Diagram](context.png)

*If diagram is missing, run: `cc_docgen generate`*

### Container Diagram (C4 Level 2)

![Container Diagram](container.png)

*If diagram is missing, run: `cc_docgen generate`*

---

## System Components

### WPF UI Layer (removed) - HISTORICAL

The original WPF UI (`CcDirector.Wpf`) was superseded by the Avalonia UI and has been removed from the tree. The source remains available in git history if needed for reference.

### Avalonia UI Layer (CcDirector.Avalonia)

| Component | Purpose |
|-----------|---------|
| MainWindow | Primary window with session sidebar, content tabs, and right panel |
| CleanView | Card-based conversation view with rewind support |
| GitChangesView | Source control panel showing git changes |
| SessionBrowserView | Browse and resume previous Claude Code sessions |
| UsageDashboardView | Usage and cost tracking dashboard |
| File Viewers | Document tab system (Image, Code, Markdown, Text, PDF viewers) |
| CommManagerView | Communication Manager overlay with approval workflow |
| Dialogs | Modal dialogs (LoadWorkspace, Rename, Relink, WorkspaceProgress, Input) |

### Core Services Layer (CcDirector.Core)

| Component | Purpose |
|-----------|---------|
| SessionManager | Session lifecycle management, creation, restoration |
| Session | Central session abstraction with state machine |
| DirectorPipeServer | Named pipe IPC server for hook events |
| EventRouter | Routes pipe messages to appropriate sessions |
| HookInstaller | Manages Claude Code hooks in settings.json |
| GitStatusProvider | Async git status polling |
| GitSyncStatusProvider | Branch ahead/behind status tracking |
| ClaudeSessionReader | Session verification via .jsonl matching |
| ClaudeClient | Helper library for Claude CLI interactions |
| VoiceModeController | Orchestrates voice interaction flow |
| CircularTerminalBuffer | Thread-safe ring buffer for terminal output |
| AlphaMode | Global alpha mode toggle for gating experimental features |
| CcStorage | Single source of truth for all cc-director storage paths |
| FileLog | Thread-safe async file logging |
| SchedulerService / CommQueueScheduler | Leader-elected background scheduler that fires comm-queue runners |
| MutexLeaderElection / LeaderIdentityStore | Single-leader election across Directors (named mutex + identity sidecar) |
| RecordingIngestService | Phone-recording ingest, transcription, and optional vault promotion |
| OpenAiRecordingTranscriber / CcVaultFiler | OpenAI transcription and cc-vault filing for recordings |
| WingmanService | Read-only LLM tasks: terminal-state classify, cleanup, summaries, rule checks |
| TerminalStateDetector / SessionStatusWingman | Terminal-derived activity state and the single writer of StatusColor |
| StateVoteService | Local-first human corrections of the state detector, synced to GitHub via gh |
| VoiceService / VoiceUtteranceService | Voice command pipeline and resumable phone voice upload |
| VoiceTurnLog / ClaudeSummarizer | Voice turn fidelity log and Haiku response summarizer |

### Director Control API (CcDirector.ControlApi)

| Component | Purpose |
|-----------|---------|
| ControlApiHost | Loopback Kestrel host (127.0.0.1, port 7879..7898) with optional auth and Gateway registration |
| ControlEndpoints | Full Director REST surface (40+ routes) plus HTML pages |
| DirectorAuth | Bearer-token / cookie middleware (shared per-machine token) |
| GatewayClient | Director -> Gateway register/heartbeat/unregister |
| DictationEndpoint | WebSocket /dictate streaming STT (OpenAI Realtime) |
| ProactiveExplainService | Background Opus briefing cached on turn-end for mobile/voice |
| ChatService | Manager-chat relay with optional TTS summary |
| Web assets | manager.html, session-view.html, login.html, dictate.html (+ JS) |

### Tailnet Gateway (CcDirector.Gateway)

| Component | Purpose |
|-----------|---------|
| GatewayHost | One-per-machine Kestrel host aggregating local Directors |
| GatewayEndpoints | Directors/Sessions/Recordings/Handovers/Fanout routes |
| RecordingEndpoints | Phone-recorder ingest (chunk upload, transcribe, promote) |
| CommQueueEndpoints | Read-only tailnet view of the comm-queue approval DB |
| ExesEndpoints | List Directors/sessions and build/launch dev slots 1-4 |
| DirectorRegistry / DirectorEndpointClient | Live-Director discovery and Control API proxying |
| TailscaleServeProvisioner / TailscaleIdentity | Publishes HTTPS mappings and resolves Magic DNS via the tailscale CLI |
| GatewayAuth / AuthMiddleware | Per-machine bearer token and bearer/cookie enforcement |

### Gateway Tray App (CcDirector.GatewayApp)

| Component | Purpose |
|-----------|---------|
| GatewayTrayController | Tray icon hosting the Gateway in-process (Open Dashboard/Logs/Restart/Quit) |
| Autostart | Idempotent HKCU Run-key registration for launch at login |
| Program / App / GatewayAppOptions | Single-instance entry, Avalonia shell, CLI options |

### Gateway Contracts (CcDirector.Gateway.Contracts)

Dependency-free DTOs shared by the Gateway, Control API, and clients (Director/Session/Health metadata; prompt/buffer/wingman/handover/fanout requests and responses; recording manifests; chat/TTS; agent-state records).

### Communication Manager - REMOVED (historical)

Standalone WPF queue app (`CcDirector.CommunicationManager`), removed from the tree (available in git history). The queue lives on as the Avalonia `CommManagerView` plus the shared `Communications` services in Core, surfaced remotely by the Gateway CommQueue endpoints.

### Engine (CcDirector.Engine)

| Component | Purpose |
|-----------|---------|
| EngineHost | Main controller for scheduler and dispatcher |
| Scheduler | Background cron job execution loop |
| JobExecutor | Single job execution with result recording |
| CommunicationDispatcher | Polls approved comms, dispatches via cc-outlook/cc-gmail |
| EngineDatabase | SQLite wrapper for jobs and runs tables |
| VaultArchiver | Archives sent communications to vault |
| ProcessJob | Shell command execution with timeout and output capture |

### Vosk STT (CcDirector.VoskStt)

| Component | Purpose |
|-----------|---------|
| VoskSttService | Offline speech-to-text using Vosk library |
| CustomDictionary | Custom vocabulary for improved recognition |

### Native Windows APIs

| API | Purpose |
|-----|---------|
| CreatePseudoConsole | ConPTY creation for terminal hosting |
| ResizePseudoConsole | Terminal resize handling |
| CreateProcessW | Process spawning with ConPTY attachment |
| SetParent / MoveWindow | Console window embedding |
| NamedPipeServerStream | IPC with hook relay scripts |

---

## Data Flows

### Hook Event Flow

```
Claude Code (claude.exe)
        |
        | stdin JSON (hook fires)
        v
hook-relay.ps1 (PowerShell)
        |
        | Named Pipe Write
        v
\\.\pipe\CC_ClaudeDirector
        |
        | NamedPipeServerStream
        v
DirectorPipeServer
        |
        | Deserialize PipeMessage
        v
EventRouter
        |
        | Map session_id -> Session
        v
Session.HandlePipeEvent()
        |
        | State machine transition
        v
UI Update (INotifyPropertyChanged)
```

### User Input Flow

```
User types in embedded console
        |
        v
conhost.exe
        |
        | stdin to process
        v
claude.exe
        |
        | Hook fires (UserPromptSubmit)
        v
[Hook Event Flow above]
```

### Voice Mode Flow

```
User activates voice mode
        |
        v
AudioRecorder.StartAsync()
        |
        | Records audio to WAV
        v
OpenAiSttService.TranscribeAsync()
        |
        | Converts speech to text
        v
Session.SendInputAsync()
        |
        | Sends prompt to Claude
        v
ClaudeResponseExtractor.GetLatestResponse()
        |
        | Extracts response from terminal
        v
ClaudeSummarizer.SummarizeAsync()
        |
        | Condenses for speech
        v
OpenAiTtsService.SynthesizeAsync()
        |
        | Generates audio
        v
AudioPlayer.PlayAsync()
```

### Communication Dispatch Flow

```
User approves item in Communication Manager
        |
        v
SendAllAsync() fetches all approved non-hold items
        |
        v
DispatchItemAsync() routes by platform
        |
        +-- "email" --> DispatchEmailItemAsync()
        |                   |
        |                   v
        |               Parse email_specific JSON
        |                   |
        |                   v
        |               Route: personal -> cc-gmail
        |                      mindzie  -> cc-outlook
        |                   |
        |                   v
        |               RunToolAndMarkPostedAsync()
        |                   |
        |                   v
        |               MarkPosted() -> DB status='posted'
        |
        +-- other --> Logged as "skipped" (not yet supported)
```

### Remote Access Flow (tailnet)

```
Phone (browser / mobile client)
        |
        | HTTPS over tailnet
        v
Tailscale Serve  (TLS terminated, forwarded to loopback)
        |
        v
GatewayHost (Kestrel, per machine)
        |
        | DirectorRegistry resolves target Director
        v
DirectorEndpointClient --> Director Control API (127.0.0.1)
        |
        v
ControlEndpoints route handler
```

---

## Security Profile Summary

**Last Security Review:** See [security_profile.yaml](security_profile.yaml)

### Key Security Controls

- **Process Isolation**: Each Claude session runs in isolated conhost.exe process
- **IPC Boundary**: Named pipes local-only (no network exposure)
- **HTTP Surface**: Control API binds loopback only; remote access exclusively via Tailscale Serve (TLS) to a single-owner tailnet, gated by a per-machine bearer token. No plain-HTTP LAN/internet exposure.
- **Credential Handling**: No secrets in source; environment variables only (OPENAI_API_KEY); gateway token is a generated 32-byte random value stored under %LOCALAPPDATA%
- **Input Validation**: All paths validated before Process.Start; recording/voice IDs GUID-validated; chunks SHA256-checked
- **Logging**: Sensitive data truncated; no secrets logged

### Compliance Alignment

| Control | Standard |
|---------|----------|
| CC6.1 | Logical access controls |
| CC6.6 | System boundary protection |
| CC7.2 | Incident response |

See [security_profile.yaml](security_profile.yaml) for full scan rules and drift thresholds.

---

## Related Documentation

| Document | Purpose |
|----------|---------|
| [DEVELOPMENT_METHOD.md](DEVELOPMENT_METHOD.md) | CenCon Development Method - how cc-director is changed (four agents, flow:* label state machine, Definition of Ready/Done) |
| [architecture_manifest.yaml](architecture_manifest.yaml) | Machine-readable C4 model |
| [security_profile.yaml](security_profile.yaml) | Security scan rules and drift config |
| [CC_DOCGEN_SPEC.md](CC_DOCGEN_SPEC.md) | Diagram generator specification |
| [../CodingStyle.md](../CodingStyle.md) | Coding standards |

---

## CenCon Development Method (how this repo is changed)

CenCon governs not only how cc-director is **documented** but how it is **changed**. The
[DEVELOPMENT_METHOD.md](DEVELOPMENT_METHOD.md) defines a four-agent process whose runtime is the four
running cc-director sessions (Product / Developer / QA / Support). The single hard rule: **no code is
written without a clearly-defined GitHub issue that passed the Definition of Ready.**

State is carried by `flow:*` labels on GitHub issues in `thefrederiksen/cc-director`:

| Label | Stage | Owning agent | Skill |
|-------|-------|--------------|-------|
| `flow:ready-dev` | spec ready to implement | Developer Agent | `.claude/skills/developer-agent` |
| `flow:rejected` | spec too weak; bounced back | Product Agent | `.claude/skills/product-agent` |
| `flow:ready-qa` | implemented + proof linked | QA Agent | `.claude/skills/qa-agent` |
| `flow:qa-failed` | defect; bounced back | Developer Agent | `.claude/skills/developer-agent` |
| `flow:done` | verified with proof; closed | - | - |
| `flow:needs-human` | 3-strike escalation | the human | - |

Proof (screenshot + HTML report) is committed to the PR branch under `docs/cencon/proof/issue-<n>/`
and linked repo-relative from the issue; merging the PR to `main` is always a human step. The Support
Agent owns and keeps these CenCon documents current.

---

## Maintenance

### Updating This Documentation

1. After significant architecture changes, update `architecture_manifest.yaml`
2. Run `cc_docgen generate` to regenerate diagrams
3. Update `last_updated` field in manifest
4. After security-relevant changes, update `security_profile.yaml` and set new `last_verified` date

### Drift Detection

The `/review-code` skill checks:
- `architecture_manifest.yaml` must be more recent than code changes
- `security_profile.yaml` must be verified within 30 days

If either check fails, the code review will FAIL and require documentation updates.

---

*Generated for CenCon Method v1.0*

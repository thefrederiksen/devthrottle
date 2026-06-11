# Director Control API Contract Audit (Facts / Events / Verbs)

**Status:** CURRENT (audit of the code as of 2026-06-11, commit on `main`)
**Issue:** #326 (Phase 1 / 1B of [../TARGET_IMPLEMENTATION_PLAN.md](../TARGET_IMPLEMENTATION_PLAN.md) section 3)
**Taxonomy source:** [../DIRECTOR_DUMB_WRAPPER_TARGET.md](../DIRECTOR_DUMB_WRAPPER_TARGET.md) section 5 - the Director's entire API surface must be describable as **Facts** (machine state served deterministically), **Events** (raw notifications the Director pushes), and **Verbs** (mechanical actions invoked from above). Anything that interprets, decides, summarizes, or recommends below the line is a **VIOLATION** and names its migration phase.

This document is the map the migration navigates by. Plan 1B acceptance: "the audit doc exists with zero unclassified endpoints."

---

## 1. Inventory method (deterministic - QA re-derives the count with this)

Every Director Control API route is registered with `.MapGet(` / `.MapPost(` / `.MapPut(` / `.MapPatch(` / `.MapDelete(` followed by a string-literal path, one registration per source line, inside `src/CcDirector.ControlApi/`. The two WebSocket endpoints (`GET /dictate`, `GET /sessions/{sid}/stream`) are `MapGet` registrations that upgrade in the handler, so the same pattern captures them. There are no `MapMethods`, `MapFallback`, `MapHub`, or path-matching middleware routes in the project (verified by grep; `ControlApiHost.cs` contains only auth/logging middleware and the `XxxEndpoint.Map(...)` registrar calls, which do not match the pattern).

```bash
# bash (Git Bash)
grep -rnE '\.Map(Get|Post|Put|Patch|Delete)\("' src/CcDirector.ControlApi \
  --include='*.cs' --exclude-dir=obj --exclude-dir=bin | wc -l
```

```powershell
# PowerShell
(Get-ChildItem src\CcDirector.ControlApi -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
  Select-String -Pattern '\.Map(Get|Post|Put|Patch|Delete)\("').Count
```

**Both commands return `114`.** The classification table in section 4 has exactly 114 rows (the `#` column runs 1..114).

## 2. Classification rules used

- **Classification is by what handling a single request does.** An endpoint is a **VIOLATION** if a request to it can run interpretation/judgment below the line (an LLM call - claude.exe side-call, OpenAI condense/transcribe/intent-parse - or an equivalent decision). "Can" includes conditional paths (e.g. condense-on-cache-miss).
- **Pure cache reads of LLM-produced artifacts are Facts** (serving stored bytes is deterministic), but their rationale notes the *producer* is a violation and the endpoint relocates with it. The producers themselves are background pipelines without routes; they are named in section 6 so they do not ride along unnamed.
- **UI conveniences served by the Director to a browser** (login page, manager page, session view, xterm/dictation assets) are classified as **Facts** per the flagged assumption in issue #326: they serve state/HTML deterministically. If a separate "UI surface" category is preferred later, the rationale column already identifies every such row.
- **Zero inbound routes are Events - by design, not omission.** In the section-5 taxonomy, Events flow Director -> Gateway (pushes). The Director's event surface is outbound, implemented in `GatewayClient` (section 5), not as mapped routes. The inbound `POST /sessions/{sid}/assessment` is the Gateway pushing its *interpreted* state down - a mechanical store, classified Verb.
- **A Verb may not contain a decision** (taxonomy consequence 2). Verbs below are all mechanical: bytes/flags/files/processes in, deterministic effect out.

## 3. Tally

| Classification | Count |
|---|---|
| Fact | 50 |
| Event | 0 (event surface is outbound push, section 5) |
| Verb | 54 |
| VIOLATION | 10 |
| Unclassified | **0** |
| **Total (= inventory count)** | **114** |

## 4. The classification table

Grouped by source file, in source order. `WS` marks WebSocket upgrade endpoints.

### 4.1 ControlEndpoints.cs (85 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 1 | GET | /healthz | Fact | Director identity/version/liveness snapshot. |
| 2 | GET | /verify/{nonce} | Fact | Two-way handshake callback echoing nonce + directorId (deterministic identity proof, #223/#224). |
| 3 | GET | / | Fact | Serves the embedded manager.html UI page (UI convenience). |
| 4 | GET | /login | Fact | Serves login.html (UI convenience). |
| 5 | POST | /login | Verb | Validates the token and sets the auth cookie - mechanical credential check. |
| 6 | GET | /logout | Verb | Clears the auth cookie (state mutation despite GET shape). |
| 7 | GET | /sessions/{sid}/view | Fact | Serves session-view.html (UI convenience). |
| 8 | GET | /sessions | Fact | Session roster DTOs (mechanical state only). |
| 9 | GET | /sessions/{sid} | Fact | One session DTO. |
| 10 | POST | /sessions/{sid}/wingman/ask | VIOLATION | Spawns a claude --print side-call to answer/explain over the terminal - judgment below the line. **Migrates: Phase 3** (Wingman decide/execute split, target doc 4.1). |
| 11 | POST | /sessions/{sid}/wingman/act | VIOLATION | `DecideSessionActionAsync` LLM decision below the line; the execute leg (WingmanActionExecutor) stays and becomes the Phase-1B `execute-action` verb. **Migrates: Phase 3.** |
| 12 | GET | /sessions/{sid}/wingman/explain | Fact | Reads the proactively-cached explain text; no LLM on request. Producer (ProactiveExplainService, section 6) is the 4.1 violation - endpoint relocates with Phase 3. |
| 13 | POST | /sessions/{sid}/mobile-mode | Verb | Sets the session ViewMode flag (mechanical; its background-explain warm-up rider is a section-6 producer, Phase 3). |
| 14 | POST | /sessions/{sid}/voice-mode | Verb | Sets ViewMode Voice/Text flag (same warm-up rider note as #13). |
| 15 | POST | /sessions/{sid}/hold | Verb | Sets the OnHold flag (FIFO voice queue park/un-park). |
| 16 | POST | /sessions/{sid}/wingman-enabled | Verb | Sets the WingmanEnabled flag (same warm-up rider note as #13). |
| 17 | GET | /sessions/{sid}/github-urls | Fact | Deterministic parse of the repo's git origin into a new-issue URL (machine fact). |
| 18 | GET | /file | Fact | Serves a local file inline (machine fact: file bytes; tailnet-boundary security note in code). |
| 19 | GET | /sessions/{sid}/wingman | Fact | Wingman observability snapshot: recorded events/actions/goal fields - stored state, no interpretation on request. |
| 20 | POST | /sessions/{sid}/wingman/goal | Verb | Stores the goal string (mechanical; kicks a background LLM goal assessment - section-6 producer, Phase 3). |
| 21 | PATCH | /sessions/{sid} | Verb | Renames the session. |
| 22 | GET | /sessions/{sid}/buffer | Fact | Terminal buffer text (raw or ANSI-cleaned - deterministic transform). |
| 23 | GET | /sessions/{sid}/buffer/html | Fact | Buffer rendered to HTML (deterministic transform). |
| 24 | POST | /sessions/{sid}/assessment | Verb | The Gateway pushes its AssessedState DOWN (#186); stored as a display annotation only - mechanical set, judgment was made above the line. |
| 25 | GET | /sessions/{sid}/turns | Fact | Deterministic JSONL transcript parse into widgets (no LLM). |
| 26 | GET | /sessions/{sid}/summary | Fact | Deterministic SummaryBuilder over the JSONL transcript (no LLM). |
| 27 | GET | /sessions/{sid}/brief | VIOLATION | Condenses DID/NEEDS-YOU via OpenAI on cache miss - interpretation below the line. **Migrates: Phase 3** (Gateway brain already owns turn briefs). |
| 28 | GET | /sessions/{sid}/handover-context | Fact | Deterministic handover prompt template over the deterministic summary. |
| 29 | GET | /sessions/{sid}/recap | Fact | Cache read only - never generates ("GET should never trigger an API spend"). Producer is row 30's violation; relocates with Phase 3. |
| 30 | POST | /sessions/{sid}/recap | VIOLATION | claude --print recap generation - summarization below the line. **Migrates: Phase 3.** |
| 31 | POST | /voice/command | VIOLATION | Whisper transcription + intent parsing + command execution - transcribe/clean is interpretation (target doc section 8). **Migrates: Phase 5** (per-session voice; capture stays as raw I/O). |
| 32 | GET | /voice/status | Fact | Key-availability flag. |
| 33 | POST | /voice/utterance | Verb | Registers a resumable utterance upload id (mechanical). |
| 34 | PUT | /voice/utterance/{id}/chunk/{index:int} | Verb | Stores one audio chunk, idempotent by SHA-256 - raw I/O. |
| 35 | POST | /voice/utterance/{id}/complete | VIOLATION | Assembles chunks then Whisper-transcribes + dictionary-cleans - interpretation below the line (target doc section 8). **Migrates: Phase 5.** |
| 36 | POST | /chat | VIOLATION | Manager-chat orchestration: picks the configured session, waits on turn completion, and optionally rewrites the reply into spoken form via an LLM summarizer. **Migrates: Phase 3.** |
| 37 | POST | /sessions/{sid}/rule-violations | VIOLATION | LLM rule check (`WingmanService.CheckRulesAsync` claude side-call). **Migrates: Phase 3.** |
| 38 | GET | /sessions/{sid}/git | Fact | Deterministic git snapshot of the session repo (machine fact). |
| 39 | POST | /sessions/{sid}/git/stage | Verb | git add on listed paths. |
| 40 | POST | /sessions/{sid}/git/unstage | Verb | git unstage on listed paths. |
| 41 | POST | /sessions/{sid}/git/discard | Verb | git discard on listed paths. |
| 42 | POST | /sessions/{sid}/git/commit | Verb | git commit with the caller's message. |
| 43 | POST | /sessions/{sid}/relink | Verb | Re-points the session at a caller-supplied Claude session id. |
| 44 | POST | /sessions/{sid}/recovery-prompt | Fact | Deterministic recovery-prompt text builder (string template over last summary + git snapshot; POST shape but read-only, no LLM). |
| 45 | POST | /tts | Verb | Mechanical text-to-audio via OpenAI TTS - no judgment. Machine-agnostic, so a candidate to lift Gateway-side with Phase 5 voice work, but not a boundary violation. |
| 46 | GET | /tts/status | Fact | TTS availability/voice/model flags. |
| 47 | GET | /sessions/{sid}/turn-summaries | Fact | Cache read of stored turn summaries. Producer (SessionWingman LLM summarizer, section 6) is the 4.1 violation; relocates with Phase 3. |
| 48 | POST | /sessions/{sid}/turn-summaries | VIOLATION | Generates a turn summary on demand via LLM (`GenerateForLatestTurnAsync`). **Migrates: Phase 3.** |
| 49 | POST | /sessions/{sid}/state-vote | Verb | Captures a human state-correction with the terminal tail and files it (locally + tracker) - mechanical capture, the human supplied the judgment. |
| 50 | POST | /handover | Verb | Deterministic context build (template) + send to / create the target session - no LLM, mechanical relay. |
| 51 | POST | /sessions/{sid}/prompt | Verb | send-input: text (optionally + Enter) to the PTY. |
| 52 | GET | /sessions/{sid}/queue | Fact | The session's queued prompts. |
| 53 | POST | /sessions/{sid}/queue | Verb | Enqueue a prompt. |
| 54 | DELETE | /sessions/{sid}/queue/{itemId} | Verb | Remove a queued prompt. |
| 55 | POST | /sessions/{sid}/queue/{itemId}/send | Verb | Deliver a queued prompt to the PTY now. |
| 56 | POST | /sessions/{sid}/interrupt | Verb | Sends Ctrl+C. |
| 57 | POST | /sessions/{sid}/escape | Verb | Sends ESC. |
| 58 | POST | /sessions/{sid}/history-picker | Verb | Sends the history-picker keystroke. |
| 59 | POST | /sessions/{sid}/clear-context | Verb | Sends the /clear sequence. |
| 60 | POST | /sessions/{sid}/resize | Verb | PTY resize. |
| 61 | POST | /sessions/{sid}/upload-image | Verb | Stores the uploaded image locally and pastes its path into the session. |
| 62 | GET | /screenshots | Fact | Recent screenshot inventory (machine fact). |
| 63 | GET | /screenshots/file | Fact | One screenshot's bytes. |
| 64 | DELETE | /screenshots/file | Verb | Deletes a screenshot file. |
| 65 | POST | /fanout-local | Verb | Mechanical multi-session send-input with optional wait-for-idle poll - no content interpretation. |
| 66 | GET | /repos | Fact | Registered repository list. |
| 67 | DELETE | /repos | Verb | Unregisters a repository. |
| 68 | POST | /repos | Verb | Registers a repository. |
| 69 | PATCH | /repos | Verb | Renames a repository entry. |
| 70 | GET | /repos/overview | Fact | Aggregated deterministic git status across registered repos. |
| 71 | GET | /coaching/categories | Fact | Static category list + storage paths. |
| 72 | GET | /claude-sessions | Fact | Resumable transcript inventory for a repo (machine fact). |
| 73 | GET | /handovers | Fact | Archived handover document list. |
| 74 | POST | /handovers | Verb | Writes a handover document to the archive. |
| 75 | DELETE | /handovers | Verb | Deletes a handover document. |
| 76 | GET | /handovers/content | Fact | One handover document's content. |
| 77 | GET | /fs/list | Fact | Directory listing (machine fact). |
| 78 | POST | /sessions | Verb | create-session: spawns the requested agent in a PTY. |
| 79 | POST | /sessions/github | Verb | Creates a GitHub-Actions-driven session from a validated config - mechanical, no generated content. |
| 80 | DELETE | /sessions/{sid} | Verb | kill-session. |
| 81 | GET | /interrupted | Fact | Crash-journal roster of interrupted sessions. |
| 82 | DELETE | /interrupted/{deadDirectorId}/{deadPid:int} | Verb | Dismisses a dead Director's crash journal. |
| 83 | DELETE | /interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId} | Verb | Dismisses one interrupted session entry. |
| 84 | POST | /shutdown | Verb | Graceful Director shutdown. |
| 85 | POST | /sessions/{sid}/execute-action | Verb | Plan-1B verb (#327): executes the caller-supplied structured WingmanAction verbatim via WingmanActionExecutor (the single write chokepoint) - zero decision logic, no LLM; all executor invariants (audit trail, cooldown, suppression, exited guard) are enforcement, not intelligence. The Phase-3 entry point for row 11's execute leg. |

### 4.2 ClaudeTranscriptsEndpoint.cs (1 route)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 86 | GET | /claude-transcripts | Fact | Transcript files (id + mtime) for a repo - machine fact for relink after /clear (#172). |

### 4.3 DictationEndpoint.cs (6 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 87 | GET | /dictate.html | Fact | Embedded dictation UI page (UI convenience). |
| 88 | GET | /dictate-worklet.js | Fact | Embedded audio worklet asset. |
| 89 | GET | /dictate-client.js | Fact | Embedded client script asset. |
| 90 | GET | /dictate/recovered | Fact | Recovered-dictation pickup list (stored state). |
| 91 | POST | /dictate/recovered/{id}/dismiss | Verb | Removes a recovered-dictation entry. |
| 92 | GET (WS) | /dictate | VIOLATION | Audio capture is raw I/O (stays), but the same socket transcribes via the OpenAI Realtime API + dictionary cleanup - target doc section 8 places transcribe/clean above the line. **Migrates: Phase 5** (capture leg stays on the Director). |

### 4.4 DispatchEndpoint.cs (1 route)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 93 | POST | /dispatch | Verb | Dispatches ONE already-APPROVED communication-queue item by id through the Engine's channel tools (#329) - mechanical execution of an approval decision the human already made; anything not in the approved state is refused (409) and nothing sends. The Phase-3 brain decides WHICH item and WHEN; this verb only carries it out. |

### 4.5 SchedulerEndpoint.cs (2 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 94 | GET | /scheduler | Fact | Leader/runner snapshot of the local scheduler. |
| 95 | POST | /scheduler/{name}/run | Verb | Triggers a named runner now - mechanical trigger; the scheduling *policy* behind it is the 4.4 Engine violation (Phase 3), not this endpoint. |

### 4.6 SessionUsageEndpoint.cs (1 route)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 96 | GET | /sessions/{sid}/usage | Fact | Deterministic token-usage computation from the JSONL transcript. |

### 4.7 SettingsEndpoint.cs (6 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 97 | GET | /settings | Fact | Raw config.json contents. |
| 98 | PUT | /settings | Verb | Merge-patches config.json (re-registers gateway when touched). |
| 99 | POST | /settings/detect/gateway | Verb | Deterministic port-scan probe, optional config apply - mechanical detection. |
| 100 | POST | /settings/detect/public-url | Verb | Deterministic tailnet-endpoint detection, optional apply. |
| 101 | POST | /settings/detect/screenshots | Verb | Deterministic screenshots-folder detection, optional apply. |
| 102 | POST | /settings/test/gateway | Verb | Connectivity probe against a caller-supplied gateway URL (no mutation). |

### 4.8 TerminalStreamEndpoint.cs (4 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 103 | GET | /xterm.js | Fact | Embedded terminal asset. |
| 104 | GET | /xterm.css | Fact | Embedded terminal asset. |
| 105 | GET | /xterm-addon-canvas.js | Fact | Embedded terminal asset. |
| 106 | GET (WS) | /sessions/{sid}/stream | Fact | Live terminal buffer streamed to the client (fact-as-stream); inbound frames are the send-input/resize verbs multiplexed on the same socket - all mechanical raw I/O, no interpretation. |

### 4.9 ToolsEndpoint.cs (5 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 107 | GET | /tools | Fact | cc-* tool catalog + unmanaged binaries (machine fact / tool inventory). |
| 108 | GET | /tools/{name} | Fact | One tool descriptor + linked skills. |
| 109 | POST | /tools/{name}/test | Verb | Runs the tool's smoke tests locally - mechanical execution. |
| 110 | POST | /tools/test | Verb | Runs all tool smoke tests (bounded concurrency) - mechanical execution. |
| 111 | POST | /tools/run | Verb | Invokes ONE catalog tool with caller-supplied args, streamed NDJSON output (#328) - mechanical execution behind the catalog allowlist; the caller decides what runs. |

### 4.10 WorkspacesEndpoint.cs (3 routes)

| # | Method | Path | Classification | Rationale |
|---|---|---|---|---|
| 112 | GET | /workspaces | Fact | Stored workspace definitions. |
| 113 | GET | /workspaces/{slug} | Fact | One workspace definition. |
| 114 | GET | /history | Fact | Session history store contents. |

## 5. The Events surface (outbound - why no inbound route is an Event)

Events in the section-5 taxonomy are Director -> Gateway pushes. They are not mapped routes on the Director; they are outbound calls made by `GatewayClient` (`src/CcDirector.ControlApi/GatewayClient.cs`):

| Event push | Wire call | Notes |
|---|---|---|
| Registration / re-registration | POST `{gateway}/directors/register` | Identity + advertised endpoint (1A). |
| Heartbeat + session-state snapshot | POST `{gateway}/directors/{id}/heartbeat` every 15s | Carries every session's mechanical state (#186); the reconciliation channel. |
| Doorbell (activity transition) | POST `{gateway}/directors/{id}/doorbell` on every session activity-state change | The raw event the Gateway interprets; grows into the Phase-3 event hub. |

Plan 1B's "missing events" work (raw activity transitions, session created/exited, prompt-detected as first-class emissions) extends this outbound surface - it does not add inbound routes.

## 6. Below-the-line producers without routes (so violations do not ride unnamed)

These background pipelines run LLM interpretation inside the Director without an HTTP route of their own. They are the production side of the cache-read Facts above (rows 12, 29, 47) and are all part of target doc 4.1, **migrating in Phase 3**:

| Producer | Where | What it does below the line |
|---|---|---|
| ProactiveExplainService | `src/CcDirector.ControlApi/ProactiveExplainService.cs` | Background explain briefings (feeds row 12's cache); triggered by rows 13/14/16. |
| SessionWingman turn summarizer | `src/CcDirector.Core/Wingman/` (via TurnSummaryCache) | Per-turn LLM summaries (feeds row 47's cache). |
| Wingman goal assessment | `src/CcDirector.Core/Wingman/` (AssessGoalNowAsync) | LLM goal-state verdicts; triggered by row 20. |
| Brief condenser | BriefBuilder/BriefCache (used by row 27) | OpenAI condensation of the last turn. |
| Recap generator | RecapGenerator (used by row 30) | claude --print recap. |

The Phase 3 exit criterion makes this list empty: "zero claude.exe spawns below the Gateway (build-time audit)."

## 7. Violation summary (the migration worklist)

| Row | Endpoint | Migrates in |
|---|---|---|
| 10 | POST /sessions/{sid}/wingman/ask | Phase 3 |
| 11 | POST /sessions/{sid}/wingman/act | Phase 3 (execute leg stays as the Phase-1B execute-action verb - shipped as row 85, #327) |
| 27 | GET /sessions/{sid}/brief | Phase 3 |
| 30 | POST /sessions/{sid}/recap | Phase 3 |
| 31 | POST /voice/command | Phase 5 |
| 35 | POST /voice/utterance/{id}/complete | Phase 5 |
| 36 | POST /chat | Phase 3 |
| 37 | POST /sessions/{sid}/rule-violations | Phase 3 |
| 48 | POST /sessions/{sid}/turn-summaries | Phase 3 |
| 92 | GET /dictate (WS) | Phase 5 (capture leg stays) |

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-06-11 | Claude (Developer Agent, issue #326) | Initial audit: 111 routes inventoried and classified (50 Facts, 0 Events, 51 Verbs, 10 Violations, 0 unclassified); deterministic inventory commands; outbound event surface and route-less below-the-line producers documented. |
| 2026-06-11 | Claude (Developer Agent, issue #327) | Added row 85 POST /sessions/{sid}/execute-action (Verb - the Phase-1B mechanical WingmanAction executor entry); inventory 111 -> 112, Verbs 51 -> 52; rows after 84 renumbered +1 (cross-references updated: section 7 dictate row 91 -> 92). |
| 2026-06-11 | Claude (Developer Agent, issue #328) | Added row 110 POST /tools/run (Verb - the Phase-1B catalog-allowlisted tool invocation with streamed NDJSON result); inventory 112 -> 113, Verbs 52 -> 53; WorkspacesEndpoint rows 110-112 renumbered to 111-113 (no section-7 cross-references affected). |
| 2026-06-11 | Claude (Developer Agent, issue #329) | Added row 93 POST /dispatch as new section 4.4 DispatchEndpoint.cs (Verb - the Phase-1B comm-dispatch of an already-approved queue item; unapproved items refused, nothing sends); inventory 113 -> 114, Verbs 53 -> 54; sections 4.4-4.9 renumbered to 4.5-4.10 and rows 93-113 renumbered to 94-114 (no section-7 cross-references affected - all violation rows are below 93). |

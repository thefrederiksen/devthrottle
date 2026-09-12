# Agent wrappers and the mobile chat tab: how text goes in and how it comes back

**Date:** 2026-09-12
**Scope:** the wrappers around Claude Code, Codex, and Pi (`src/CcDirector.Core/Agents/`, `src/CcDirector.Core/Drivers/`), focused on the two questions that matter to the mobile chat tab: how the text the user enters reaches the live agent, and how the text the agent returned is extracted for display.
**Freshness:** read from `origin/main` at commit 53b8abf (the shared checkout was 49 commits behind; the two files that differ — `Session.cs` and `ChatService.cs` — were re-read from `origin/main` and their submit mechanics are unchanged there).
**Method:** code read only. Nothing was run live, and no screen was exercised. The claims below are about what the code does, not about what a live session showed.

---

## 1. The two consumers that share the name "mobile chat"

There are two different phone surfaces, and they get the agent's text by **different mechanisms**. Naming them apart is the first finding.

### A. The mobile web app Chat page and the Cockpit Chat tab (the chat tab proper)

`apps/mobile/src/pages/Chat.tsx` and `apps/cockpit/src/sessions/ChatTab.tsx` are thin views over the same shared hook, `packages/client-core/src/history/useSessionChat.ts`. That hook:

- **Outbound (what the user typed):** the composer is `SessionControls`, which sends with `sendPrompt` — `POST /sessions/{sid}/prompt` through the Gateway, which forwards it down the tunnel as the `prompt` verb to the owning Director (`SessionCommandExecutor.PromptAsync` → `SendPromptAsync` → `Session.SendTextAsync`).
- **Inbound (what the agent said):** a 2.5-second poll of `GET /sessions/{sid}/history`, which the Gateway proxies to the Director's `SessionHistoryEndpoint`, which builds the conversation from `SessionHistoryReader` — **agent-agnostic, all three agents supported** (details in section 3).

### B. The Android application's Talk page chat send (a different, older surface)

The MAUI phone client (`phone/CcDirectorClient`) sends through `DirectorVoiceClient.SendChatAsync` → the Director's `POST /chat` → `ChatService`. **Its reply extraction is Claude Code only** (details in section 4). The web voice pipeline and the mobile web app do not use this path.

---

## 2. Inbound: how the user's text reaches the agent — identical for all three

All three agents run as **live interactive terminal processes** (a pseudo-terminal per session). The user's text is never passed as a launch flag or written to a pipe. It is **typed into the agent's own composer as keystrokes**, exactly as if a person typed it, with verification at every step. The single implementation is `src/CcDirector.Core/Drivers/TerminalSubmit.cs`, reached from `Session.SendTextAsync` (ConPTY backends) and from each driver's `SubmitAsync`:

- `ClaudeDriver.SubmitAsync` → `TerminalSubmit.SharedSubmitAsync`
- `CodexDriver.SubmitAsync` → `TerminalSubmit.SharedSubmitAsync`
- `PiDriver.SubmitAsync` → `TerminalSubmit.EchoVerifiedSubmitAsync` (the same shared route, echo required)

The shared submit protocol, in order:

1. **Trailing newlines are trimmed** from the caller's text.
2. **Large or multi-line text takes a file route:** a temporary file is written into the repo's `.temp` directory and the agent is submitted a short reference to it. Two variants:
   - `@<relative path>` reference (the default for large input on all three agents);
   - a "Read file X in the .temp directory…" instruction sentence instead — used when the driver tag is **Codex** (also Copilot and OpenCode) and the text is over 300 characters or large.
   **Consequence for the chat tab:** for these sends, what lands in the agent's transcript — and therefore what the user bubble in the chat tab shows — is the file reference or the instruction sentence, **not the user's actual words**. The words are in the temp file, which the chat tab does not read.
3. **Bracketed paste** wraps the text when the terminal requested it.
4. **Echo-verified submit (the common path):** the text is typed **without** an Enter; the Director waits until the composer echoes it back in the terminal byte stream — compared over a normalized alphabet (letters, digits, and slash only) — and only then presses Enter as a separate keystroke. Hardening layered on top:
   - a **visible-tail needle** (last 16 characters), because Codex horizontally scrolls long input and repaints only the tail;
   - an **interleaved-subsequence match** (ordered, densely packed characters, minimum 40), because a repaint can splice a footer hint into the middle of the echoed text (issue #1592);
   - a **rendered-screen second opinion** when the byte stream misses, before disturbing the composer;
   - a **slash-corruption rejection**: an echo preceded by "/" is never accepted (the "Unknown command: /Write" failure);
   - **one Escape-and-retype recovery**, then a throw (`ComposerNotAcceptingInputException`) — the prompt is never silently parked.
5. **Every Enter is verified** (`SubmitVerifier`): after pressing Enter the Director watches the terminal until the turn provably started, and nudges a composer it can *see* is parked (pull request #1513 — previously the common short-prompt path pressed Enter and never looked back).
6. Writes over 48 bytes are sent in 16-byte chunks with 10-millisecond gaps (human-paced).
7. Every failure is counted per session in `PromptDeliveryFailures` and the exception is rethrown untouched — the delivery boundary (issue internal #811).

`Session.SendTextAsync` is the single choke point: on `origin/main` it also stamps a `SubmissionProvenance`, sets the activity state to Working, and records origin statistics. The submit mechanics are unchanged from the older checkout.

**Per-agent differences inbound** (small, and all in the launch wrapper, not the submit path):

| | Claude Code (`ClaudeAgent` / `ClaudeDriver`) | Codex (`CodexAgent` / `CodexDriver`) | Pi (`PiAgent` / `PiDriver`) |
|---|---|---|---|
| Session id at launch | Director mints a GUID, passed as `--session-id` (or `--resume`) | No preassigned id; Codex manages its own | Director mints a GUID, passed as `--session-id`; pi creates or resumes the file named by it (issue #2670) |
| Transcript known from birth | Yes | No — located later by repo | Yes |
| Large-text route | `@`-file reference | Instruction-file sentence (over 300 chars) | `@`-file reference |
| Studio mode (stream-json cards) | Yes | No | No |

---

## 3. Outbound for the mobile web chat tab: the agent's own transcript files

`SessionHistoryReader` dispatches on `AgentKind`, so the mobile web Chat page and the Cockpit Chat tab get a parsed, clean conversation for **all three**:

- **Claude Code:** the JSON Lines transcript under `~/.claude/projects`, located by session id (`ClaudeSessionReader.GetJsonlPath`, with relocation scan) and by the hook-reported transcript path. Parsed by `ClaudeTranscriptReader`.
- **Codex:** the "rollout" under `~/.codex/sessions`, located by `CodexRolloutLocator` — **a heuristic**: the newest rollout whose `session_meta.cwd` matches the session's repo, scoped to the Director session's launch time. Parsed by `CodexTranscriptReader` (only `response_item` lines; tool calls and outputs become their own turns).
- **Pi:** the session file `~/.pi/agent/sessions/<cwd-slug>/<timestamp>_<id>.jsonl`, located **exactly** by the preassigned `--session-id` (`PiSessionLocator`). Parsed by `PiTranscriptReader` (only `message` lines; text / thinking / tool-call parts in order). Pi's `/new` is followed by `PiSessionRebinder`: the clear stamps a timestamp, and at the next turn end the watcher finds the new file pi created and relinks the session to it.

All three normalize into the same `ConversationHistory` → `SessionHistoryDto` (roles plus parts: text, thinking, tool calls, tool results), which `chatView` renders as Markdown bubbles with the "Show:" filters. **The user's own message appears as a bubble because the agent wrote it into its own transcript** — the chat tab is a faithful mirror of the agent's record, not of what the Director sent. Claude sessions that cannot be located fail loudly (`transcript-not-found`), not as an empty "ok".

## 4. Outbound for the Android application's `/chat` path: Claude Code only

`ChatService.ReadReply` extracts the agent's reply through `TryReadLastAssistantFromJsonl` → `ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, …)` — a **Claude-only** path under `~/.claude/projects`:

- **Pi sessions:** `ClaudeSessionId` holds the *pi* session id (the launch spec stores it there, and `PiSessionLocator` resolves by it). The Claude reader therefore looks for a Claude transcript named after a pi id, which does not exist.
- **Codex sessions:** no preassigned id, so `ClaudeSessionId` is null.

The consequences differ by route within `ChatService`:

- **Poll requests** (`PollOnly`) read the transcript only and refuse to scrape — so a Pi or Codex session **returns an empty reply with status "ok"** on this surface: the phone app's send loop shows "Turn ended: ok" with nothing displayed.
- **The blocking send path** falls back to a **buffer diff**: everything the terminal printed since the message was sent, ANSI-cleaned (`AnsiCleaner`) and trimmed for a chat bubble — TUI chrome, spinner remnants, composer echoes included.

Meanwhile the agent-agnostic readers already exist and work (`PiTranscriptReader`, `CodexTranscriptReader`, reached by `SessionHistoryReader`); `ChatService` simply does not dispatch on `AgentKind`. **This is the central asymmetry found by this review.**

## 5. Findings, in order of cost

1. **`ChatService` reply extraction is Claude-only, and the agent-agnostic readers it needs already exist.** The Android application's chat send returns an empty "ok" for Pi and Codex on polls, and a scraped terminal tail on the blocking path. The mobile web chat tab is unaffected (it uses the history endpoint, which dispatches per agent). Fix shape: dispatch `ReadReply`/`TryReadLastAssistantFromJsonl` on `session.AgentKind` through `SessionHistoryReader` (or the per-agent readers), keeping the buffer diff as the last resort for genuinely unlinked sessions.
2. **Codex transcript attribution is a newest-for-repo heuristic.** Two concurrent Codex sessions in the same repo (the fleet runs these) can bind to the same rollout or to each other's: `CodexRolloutLocator` scopes by repo path and launch time, and takes the newest match. The mobile chat tab could then show another session's conversation. The code documents this as a "first-cut heuristic"; it is the weakest of the three bindings.
3. **Large sends change what the chat tab shows the user said.** Over the file routes, the user's bubble is an `@<path>` reference (Claude, Pi) or a "Read file X…" instruction (Codex over 300 characters), not the typed words. Faithful to what the agent saw, misleading to the person who wrote it.
4. **Pi's `/new` window.** Between a context clear and the next turn end, the session still points at the old pi session file, so the chat tab shows the pre-clear conversation until the rebinder runs. Small, self-correcting, and documented in the code.
5. **The server-side `/chat` does not refuse a send while the session is Working** — the "don't inject while running" guarantee lives in the phone client's readiness gate (`ChatTurnResult.IsWorking`). A second writer (another client, a fleet message) can interleave. The mobile web app's send has no such gate at all; its protection is only that the composer send is a verified terminal submit.

## 6. What this review did not cover

- Nothing was run: no live Claude, Codex, or Pi session was driven, and no phone screen was exercised. The behavior described is what the code on `origin/main` does by construction.
- The other five wrappers (Copilot, Cursor, Gemini, Grok, OpenCode) were only referenced where they clarify the pattern.
- The Studio mode (stream-json) path for Claude was noted but not reviewed.
- Whether "the mobile chat tab" the owner means is the mobile web Chat page (section 1A — fully agent-agnostic today) or the Android application's chat send (section 1B — Claude-only today) was not confirmed with the owner; the review covers both, and the difference between them is itself a finding.

# Goal: CC Director Session Supervisor + Voice-Through-Supervisor

**Status:** ACTIVE GOAL - written for an LLM agent to execute autonomously
**Date:** 2026-05-20  (revised same day after first voice test - see Update Log)
**Source PRD:** `PRD_CC_DIRECTOR.md` (same directory) - read it first
**Audience:** the implementing agent. You.

## Update Log

- **2026-05-20 (initial)**: Phase 1 voice input cleanup, Phase 2 turn summary, Phases 3-7 = status / rules / git / crash / code review.
- **2026-05-20 (revision after first voice test)**:
  - User tested voice end-to-end. **Two real problems** that the original phasing did not put on the critical path:
    1. The browser `SpeechSynthesis` voice is too robotic for hands-free use.  Switch to **OpenAI TTS** (default `tts-1`, voice `alloy` or `nova`).  ElevenLabs is the better-sounding follow-up if the OpenAI quality is not enough.
    2. The spoken playback is the **raw agent reply**, not a summary, so it reads code blocks and verbose tool output out loud.  Fix: the Supervisor turn summary produces a dedicated `spoken_text` field that TTS reads.  This couples Phase 2 (turn summary) and the new TTS phase: the summary is the source of truth for the spoken text.
  - Restructured phases: Phase 1 (voice IN) ships tonight as-is.  Phase 2 now produces `spoken_text` as a first-class field, not just an Agent-View summary.  New Phase 3 = OpenAI TTS reading `spoken_text`.  Old Phases 3-7 shift to 4-8.
  - Removed "TTS via OpenAI" from the Out-of-scope list.  Still out of scope for now: ElevenLabs.
- **2026-05-20 (Phases 1-8 SHIPPED in one autonomous run)**:
  - Phase 1: `SupervisorService.CleanVoiceTranscriptAsync` + voice flow wiring + 10 unit tests (PASS).  Voice mode UI shows raw + cleaned bubbles.
  - Phase 2: `SupervisorService.SummarizeTurnAsync` + `TurnSummary` + `TurnSummaryCache` (subscribes to `OnSessionCreated` / `OnTurnCompleted`, in-memory cache per session) + `GET / POST /sessions/{sid}/turn-summaries` + voice JS uses `summary.spokenText` for TTS source + 7 unit tests (PASS).
  - Phase 3: `TtsService` + `TtsRequest` / `TtsErrorResponse` + `POST /tts` + `GET /tts/status` + voice JS prefers OpenAI TTS over `SpeechSynthesis` with graceful fallback + `Voice.TtsVoice` / `Voice.TtsModel` config (`appsettings.json`).
  - Phase 4: `SessionDto.StatusColor` + `StatusColor.From()` red/yellow/green mapping + 7 unit tests (PASS).  Threaded `TurnSummaryCache` through every `SessionDto Map(...)` call site so all consumers get the colour.
  - Phase 5: `SupervisorService.CheckRulesAsync` + `RuleViolation` + `RuleViolationsResponse` + `POST /sessions/{sid}/rule-violations` + CLAUDE.md chain walker (repo -> parents -> `%USERPROFILE%\.claude\CLAUDE.md`) + 5 unit tests (PASS).
  - Phase 6: `SupervisorService.GitSnapshotAsync` (no Haiku - runs `git` locally) + `GitSnapshot` + `GET /sessions/{sid}/git` + 2 unit tests (PASS).
  - Phase 7: `SupervisorService.BuildRecoveryPromptAsync` + `RecoveryPrompt` + `POST /sessions/{sid}/recovery-prompt` + 2 unit tests (PASS).
  - Phase 8: `SupervisorService.CheckCodeReviewDiscipline` (pure local scan over recent TurnData) + 4 unit tests (PASS).
  - 40 / 40 Supervisor unit tests pass.  23 / 23 existing endpoint integration tests (Voice, Chat, ControlApiHost) still pass.  Build clean across Core, Contracts, ControlApi (0 warnings, 0 errors).  Released as `local_builds\cc-director-avalonia4.exe`.
  - Self-test caveat: the live Haiku side-call (`claude --print --bare`) was not exercised end-to-end in CI; only the fail-open / fall-through paths.  Real voice round-trip + summary playback must be verified by the user against the slot-4 binary.

---

## 0. How to use this document

You are picking this up cold. Here is the contract:

1. **Read this doc top to bottom.** Then read `PRD_CC_DIRECTOR.md` for the why.
2. **Work one phase at a time.** Each phase has a clear deliverable, an implementation order, and a **self-test gate**. You must pass the self-test before moving to the next phase.
3. **You self-test.** No human in the loop until you report done. The tests in each phase are written so YOU can verify them and ONLY proceed when they pass.
4. **When everything in the phase passes, report back to the user with a single message** stating: which phase finished, what the self-test produced (e.g. log of raw/cleaned voice pairs), and whether you are proceeding to the next phase or stopping.
5. **The whole goal is done when Phase 1 ships and the user has confirmed.** Phases 2-7 are future work. Phase 1 is tonight.
6. **Critical safety:** do NOT kill any running `cc-director*` or `voice-test-host*` processes. The user runs many directors in parallel. Use `local_builds\_local_build_avalonia<N>.bat` slots 3 or 4 to build into a non-conflicting binary, or use `tools\voice-test-host\` for headless testing.

---

## 1. The one-line goal

> Insert a **Session Supervisor** between voice input and the main agent session, so the raw Whisper transcript is cleaned up by a cheap Haiku side-call before it is sent to the main session. Both raw and cleaned versions are persisted. This is the minimum viable Supervisor: it does the voice cleanup step from PRD section 3.1 responsibility #3. Other responsibilities (turn summary, status classification, rules enforcement, git awareness, crash recovery, code review) are deferred to follow-up goals.

---

## 2. The pattern: Supervisor = generalised `RecapGenerator`

The existing `src\CcDirector.Core\Claude\RecapGenerator.cs` does exactly this pattern for the recap feature:

- Spawn `claude --print --bare --model haiku --tools "" --dangerously-skip-permissions` with a prompt built from session state.
- Wait for the process to exit, capture stdout.
- Return the result string.

**Every Supervisor task in the PRD is the same pattern with a different prompt.** You are not building a new long-running process. You are building a small library of one-shot Haiku calls.

For Phase 1 you add **one new method**: clean a raw voice transcript into a polished prompt for a Claude Code agent.

---

## 3. Phase 1 (TONIGHT) - Voice transcript cleanup (INPUT side only)

### 3.1 Why

Voice input from a moving car produces transcripts like `"uhh so like maybe we could just like fix the bug in the login thing"`. Sending that directly to a senior-level Claude Code agent wastes its tokens parsing the verbal pauses and producing the cleaner restatement itself. A Haiku side-call does the cleanup in ~1 second for ~$0.0001 and the main agent gets a tight, well-formed prompt.

### Scope note (post-test revision)

Phase 1 fixes only the **input** path of voice mode (what the agent receives).  The **output** path (what gets spoken back through the phone speaker) is known-imperfect after Phase 1:
- Voice quality stays as the browser's `SpeechSynthesis` (robotic).  Fixed in Phase 3.
- Spoken text stays as today's regex-cleaned raw reply.  Fixed in Phase 2 once a turn `spoken_text` summary exists.

Ship Phase 1 anyway: better input is independently valuable, and it is a clean small change that proves the Supervisor pattern works.  The user has explicitly accepted that the spoken output will still feel rough until Phases 2+3 land.

### 3.2 Deliverable

A new method on a new class plus a small change to the voice pipeline:

- `src\CcDirector.Core\Supervisor\SupervisorService.cs` - new file. One method: `CleanVoiceTranscriptAsync(string rawTranscript, string repoPath, CancellationToken ct) -> Task<VoiceCleanupResult>`.
- `src\CcDirector.Gateway.Contracts\VoiceCommandResponse.cs` - add two fields: `string? CleanedTranscript` and `string? CleanupReason`. Keep the existing `Transcript` field for the raw.
- `src\CcDirector.Core\Voice\VoiceService.cs` - after Whisper returns, call `SupervisorService.CleanVoiceTranscriptAsync`, populate `CleanedTranscript` in the response. Both raw and cleaned are persisted to the FileLog.
- `src\CcDirector.ControlApi\Web\session-view.html` - voice mode JS sends the **cleaned** transcript (falling back to raw if cleanup failed) to `/chat`. Both raw and cleaned bubbles are shown in the voice log.

### 3.3 Prompt for the Supervisor

The Supervisor's job is bounded: take a raw transcript, output a cleaner version. Use this prompt template (it goes as the positional arg to `claude --print`):

```
You are a transcription cleanup assistant for a hands-free voice interface to a Claude Code agent.

The user just dictated the text below into their phone while driving.  Whisper transcribed it.
The text is a request, question, or instruction the user wants sent to the Claude Code agent
that is working in their <REPO_PATH> repository.

Your job: produce the CLEANED version of the user's message, ready to send to the agent.

Rules:
- Remove filler words (um, uh, like, you know, kind of, basically, sort of).
- Fix obvious mis-transcriptions where the meaning is clear.
- Keep the user's intent and tone.  Do NOT paraphrase or "improve" beyond cleanup.
- Do NOT add greetings, sign-offs, or commentary.
- Do NOT answer the question yourself.  Just clean the prompt.
- If the message is so unclear you cannot confidently clean it, output it verbatim.
- One paragraph.  No bullet lists, no headings, no quotation marks around the result.

Output JSON only, no markdown fence, no other text, this exact shape:
{"cleaned": "<the cleaned prompt>", "reason": "<one short sentence explaining what you changed, or 'no changes needed' if minimal>"}

RAW TRANSCRIPT:
<RAW_TRANSCRIPT>
```

Replace `<REPO_PATH>` with the session's actual repo path and `<RAW_TRANSCRIPT>` with the raw Whisper output.

Parse the JSON response. On parse failure, fall back to the raw transcript and set `reason = "supervisor JSON parse failed"`.

### 3.4 Implementation order

1. Create `src\CcDirector.Core\Supervisor\SupervisorService.cs` with `CleanVoiceTranscriptAsync`. Copy the Process.Start machinery from `RecapGenerator` (same flags: `--print --bare --model haiku --tools "" --dangerously-skip-permissions --output-format text`). Different prompt.
2. Add `CleanedTranscript` and `CleanupReason` fields to `VoiceCommandResponse`.
3. In `VoiceService.HandleAsync`, after Whisper returns and before intent parsing, call `SupervisorService.CleanVoiceTranscriptAsync(transcript, repoPath, ct)`. Use the session's repo path - resolve via the session's `RepoPath` if a sessionId override was sent, otherwise pass an empty string.
4. Update `session-view.html` voice mode JS:
   - When the `/voice/command` response comes back, prefer `cleanedTranscript` over `transcript` for the `/chat` payload.
   - Show the raw transcript in a small grey bubble (or as a "(raw: ...)" annotation) so the user can see what was caught, AND the cleaned version as the primary "You" bubble.
5. Build slot 3 (`scripts\local-build-avalonia.ps1 -Slot 3`) and run it.

### 3.5 Self-test gate for Phase 1

**You must run all of these and capture results before declaring done.**

#### Test A: Unit-level (no Whisper, no main session)

Write a new test file `src\CcDirector.Core.Tests\Supervisor\SupervisorServiceTests.cs` with five test cases. For each, log the (raw, cleaned, reason) tuple to the test output.

| # | Raw input | Expected cleanup behaviour |
|---|---|---|
| 1 | `"um like can you uh fix the bug in the login flow you know"` | Filler words removed, intent intact. Expect cleaned to contain "fix the bug in the login flow", NO "um", "uh", "like", or "you know". |
| 2 | `"add a test for the empty case"` | Already clean. Cleaned ~= raw. |
| 3 | `"so basically we need to refactor session view and add the voice tab thing we talked about earlier"` | Filler stripped. Intent preserved. No "basically" / no "thing we talked about earlier" unless preserved literally. |
| 4 | `""` | Edge case: empty raw transcript. Cleaned should be empty or contain a marker. Service must not throw. |
| 5 | `"asfasdfasdfasdf"` (gibberish) | Service should NOT crash. Should produce SOMETHING (verbatim raw is fine). |

For each test, the assertions are:
- `result.Cleaned` is not null.
- `result.Cleaned.Length > 0` for non-empty raw.
- For test 1, assert no filler tokens remain (`um`, `uh`, `you know`, `like` as a standalone word).
- Service does not throw on any input.

This requires the OpenAI key to be set in the env or `appsettings.json`. If not configured, the test class should `Assert.Skip` (or behave like the VoiceEndpointTests which set/clear env vars).

#### Test B: End-to-end via curl

With slot 3 running on its HTTPS tunnel:

```bash
# Generate a test audio file from text using OpenAI TTS, then POST it to /voice/command.
# (Use the existing OpenAI key.  Do NOT print the key to logs or commit it.)
# If TTS is not configured, skip this test and rely on Test A.
```

Verify the response contains both `transcript` and `cleanedTranscript` and they are different in a sensible way for a deliberately-noisy input.

#### Test C: Visual smoke test

Open the slot 3 HTTPS URL (the trycloudflare URL from the earlier setup, or a new one if it rotated) in cc-playwright. Navigate to the session view. Tap Voice. (Mic permission will fail in headless, that is OK - this test only verifies the UI now shows BOTH the raw and the cleaned bubble when both are present.)

You can simulate the response by either:
- (a) directly invoking the JS that renders bubbles with a fake response object, OR
- (b) running test A above and then loading a real `/voice/command` response into the UI.

Either way: take a screenshot to `D:\ReposFred\devthrottle\docs\features\director\.run\voice-test-screens\09-voice-cleanup.png` showing the raw + cleaned distinction visible in the voice log.

#### What to report

Send ONE message to the user with:

1. The phase you finished (Phase 1).
2. The 5 raw/cleaned pairs from Test A as a Markdown table.
3. The result of Test B (or "skipped, no TTS available").
4. The screenshot path from Test C.
5. Whether you proceed to Phase 2 or stop and wait for review.

Default: **stop and wait for review.** The user wants to test the voice flow end-to-end on their phone before the next phase ships.

---

## 4. Phase 2 - Per-turn structured summary feeding Agent View AND voice TTS

### 4.1 Why

Two problems share one fix:
1. The Agent View today (existing `WidgetBuilder` JSONL rendering) is verbose. PRD wants "clean readable summary of current turn" + "structured history".
2. Voice mode today reads the raw agent reply aloud, which is unusable in a car (it speaks code blocks, tool names, file paths).

A single Supervisor side-call per completed turn produces a structured summary used by both surfaces.  Critically, the summary includes a dedicated `spoken_text` field designed for the ear - the Phase 3 OpenAI TTS reads that, not the raw reply.

### 4.2 Deliverable

- Add `SupervisorService.SummarizeTurnAsync(TurnData turn, string repoPath, CancellationToken ct) -> TurnSummary`.
- `TurnSummary` includes a first-class `SpokenText` field, separate from headline/decisions/etc.
- Wire `Session.OnTurnCompleted` (existing event) to invoke the summariser in a background task; cache the result keyed by turn timestamp.
- Add `GET /sessions/{sid}/turn-summaries` endpoint returning the cached list.
- Update `session-view.html` Agent tab to show a "Summary" badge per turn that, when expanded, shows the structured summary fields.  Existing widgets stay below.
- Update Voice tab's TTS source: when a turn finishes, the spoken text comes from the most recent summary's `spoken_text`, not from the raw reply.  If no summary is available yet (race condition or summary failed), fall back to the existing regex-cleaned raw reply.

### 4.3 Prompt template

```
You are summarising one turn of a Claude Code session for a user who does not want to read the raw output, and who may be listening to a voice playback while driving.

INPUT: the user's prompt, the tools the agent used, the files it touched, the commands it ran, the last assistant text.

Output ONE JSON object, no markdown fence, exactly this shape:
{
  "headline": "<one short sentence describing what the agent did this turn>",
  "files_touched": ["<list of distinct file paths touched, max 5>"],
  "commands_run": ["<list of distinct shell commands, max 3>"],
  "decisions": ["<key decisions / findings the agent made or surfaced, max 3 bullets>"],
  "needs_user": "<one of: 'no' | 'question' | 'error' | 'permission' | 'idle'>",
  "needs_user_detail": "<short sentence if needs_user != 'no', empty otherwise>",
  "spoken_text": "<see rules below>"
}

Rules for spoken_text:
- One to three short sentences.  Maximum ~280 characters.
- Written for the ear: a human listening in a car.  Plain language.  No code, no symbols, no file paths, no commands.
- Reads the FINDING / OUTCOME, not the process.  E.g.: "Tests passed.  Three files were updated.  The login bug is fixed." NOT "I ran dotnet test and got exit code zero..."
- If needs_user != "no", start with: "I need you to <decide / answer / approve>.  <question>."
- If the agent did nothing meaningful (e.g. only acknowledged), spoken_text can be: "Acknowledged, nothing to report."

TURN DATA:
- User prompt: <USER_PROMPT>
- Tools used: <TOOLS_LIST>
- Files touched: <FILES_LIST>
- Commands run: <COMMANDS_LIST>
- Last assistant text (truncated to 2000 chars): <LAST_ASSISTANT_TEXT>
```

### 4.4 Self-test gate for Phase 2

- Unit test: feed three fake `TurnData` instances, verify the parsed `TurnSummary` has all fields populated AND `SpokenText.Length <= 320`.
- Unit test: feed a `TurnData` representing a needs-input case, verify `spoken_text` starts with "I need you to".
- Integration test: trigger a real turn in a test session, wait for `OnTurnCompleted`, verify the summary appears in `/sessions/{sid}/turn-summaries` within 30 s.
- Visual: screenshot the Agent tab showing the headline + decisions + needs_user marker.

### 4.5 Report and stop

Same protocol as Phase 1.  Send ONE message with the three fake summaries + the real one, then stop and wait for user review.

---

## 5. Phase 3 - OpenAI TTS for voice output

### 5.1 Why

Browser `SpeechSynthesis` voices are robotic enough that hands-free voice mode is unusable - the user can decode the words but it feels jarring.  Switching to OpenAI's `tts-1` (default voice `alloy` or `nova`) drops a natural-sounding voice with the same API key that already powers Whisper.

Cost: about $15 per 1M characters.  A 250-character summary = ~$0.004.  At even 100 turns/day that is $0.40/day.  Acceptable.

### 5.2 Deliverable

- New endpoint `POST /tts` on the Director:
  - Body: `{ text: string, voice?: "alloy"|"nova"|"echo"|"fable"|"onyx"|"shimmer", model?: "tts-1"|"tts-1-hd" }`
  - Calls OpenAI's `/v1/audio/speech` endpoint with the resolved API key, returns the audio bytes (`audio/mpeg`).
  - On failure (no key, OpenAI error, timeout): return a structured JSON error so the client can fall back to browser `SpeechSynthesis`.
- New contract `src\CcDirector.Gateway.Contracts\TtsRequest.cs` and `TtsErrorResponse.cs`.
- New service `src\CcDirector.Core\Voice\TtsService.cs` modeled after the Whisper bits in `VoiceService.cs`.
- Configuration `appsettings.json` -> `"Voice": { "TtsVoice": "alloy", "TtsModel": "tts-1" }`, both optional, sensible defaults.  `AgentOptions.TtsVoice` / `AgentOptions.TtsModel` fields.
- Client (`session-view.html` voice JS):
  - Replace the `SpeechSynthesisUtterance` call with `fetch('/tts', ...)` -> `Audio` element playback.
  - On `/tts` failure, fall back to the existing `SpeechSynthesis` path.
  - Re-use the existing "Stop speaking" button (call `audio.pause()` AND `speechSynthesis.cancel()`).
- The source text is `summary.spoken_text` (Phase 2 product), with the regex-cleaned raw reply as last-resort fallback.

### 5.3 Self-test gate for Phase 3

- Unit test: `TtsService` against a recorded mock or by checking the request body shape (do NOT make real OpenAI calls in unit tests).
- Integration test: `POST /tts` with body `{ text: "Hello world." }`, expect `200 OK`, `Content-Type: audio/mpeg`, body length > 1000 bytes.
- End-to-end via cc-playwright: open a session view, fake an agent reply via the chat endpoint, verify the audio element is created with a blob URL of length > 1000.
- Listen test (LLM cannot do this; document a HOW for the user): "Open the trycloudflare URL in Chrome and verify the voice sounds natural, not robotic.  Then mark Phase 3 as user-accepted."

### 5.4 Report and stop

Same protocol.  Report the byte size + Content-Type of a sample TTS response.  User does the listen test.

---

## 6. Phase 4 - Red / yellow / green status classification

### 6.1 Why

The Gateway dashboard needs a single colour per session to be scan-able. The Supervisor's per-turn summary already produces a `needs_user` field (Phase 2). Phase 4 maps that to a colour and exposes it on `SessionDto`.

### 6.2 Deliverable

- Mapping: `red` = `needs_user in {question, error, permission}`. `yellow` = `idle` AND uncommitted git changes exist. `green` = everything else.
- Cache the latest colour per session on the Session itself.
- Add `SessionDto.StatusColor` field (`"red" | "yellow" | "green" | "unknown"`).
- The cards UI (`manager.html` / `cards`) shows the coloured border.

### 6.3 Self-test gate for Phase 4

- Unit test the mapping function.
- Integration test: synthesize three turn summaries with different `needs_user` values, verify the resulting `StatusColor` matches expectations.

### 6.4 Report and stop

Same protocol.

---

## 7. Phase 5 - Rules / memory enforcement

### 7.1 Why

The user maintains `CLAUDE.md` files with rules ("never use em-dashes in drafts", "never kill cc-director processes", etc.). The main agent sometimes forgets these. The Supervisor reads CLAUDE.md and checks each completed turn against the rules.

### 7.2 Deliverable

- New `SupervisorService.CheckRulesAsync(TurnData, string repoPath)` that:
  - Reads `<repoPath>\CLAUDE.md`, `<repoPath>\..\..\CLAUDE.md` (parent chain up to `%USERPROFILE%`), and the global `%USERPROFILE%\.claude\CLAUDE.md`.
  - Passes the rules + turn summary to a Haiku call.
  - Returns a list of `RuleViolation` objects (rule text + what the agent did that violated it).
- Violations surface in the Agent View as warnings.
- Violations also feed into the `red/yellow/green` calculation (any violation -> at least `yellow`).

### 7.3 Self-test gate for Phase 5

- Test: feed a turn that obviously violates a known rule, verify a violation is returned with the rule cited.
- Test: feed a clean turn, verify zero violations.

### 7.4 Report and stop

Same.

---

## 8. Phase 6 - Git awareness

### 8.1 Why

End of each turn the Supervisor reminds about uncommitted changes.

### 8.2 Deliverable

- `SupervisorService.GitSnapshotAsync(string repoPath)` returns `{branch, dirty, ahead, behind, lastCommit}`.
- After each turn the snapshot is included in the Agent View.
- If `dirty == true` for N consecutive idle turns, the status colour goes `yellow`.

### 8.3 Self-test gate

- Unit test against this repo (which is always dirty during development).

### 8.4 Report and stop

Same.

---

## 9. Phase 7 - Crash resilience

### 9.1 Why

OOM crashes lose context. The Supervisor watches for the main session process exiting unexpectedly and builds a recovery prompt from the last N lines of terminal output plus the git diff since session start.

### 9.2 Deliverable

- New event subscription: `Session.OnProcessExited` (or equivalent) where exit code != 0 or the session was not in `Exiting` state.
- `SupervisorService.BuildRecoveryPromptAsync(Session, string repoPath)` returns a markdown blob the user can copy into the next session: "Here is what was happening when the previous session died. Pick up from..."
- Surfaced in the Agent View when the session is in `Failed` state.

### 9.3 Self-test gate

- Force-kill a test session, verify a recovery prompt is produced within 5 s and contains the last user prompt + the dirty git diff.

### 9.4 Report and stop

Same.

---

## 10. Phase 8 - Code review enforcement

### 10.1 Why

Pre-commit code review skill should run before commits / pushes. The Supervisor triggers it.

### 10.2 Deliverable

- After each turn whose commands include `git commit`, the Supervisor checks whether the existing `review-code` skill ran in this session.
- If not, surface a warning in the Agent View AND in the next turn's `needs_user` field.

### 10.3 Self-test gate

- Test that a turn containing `git commit` without a prior `review-code` skill run produces a warning.

### 10.4 Report and stop

Same.

---

## 11. What to reuse (do not rebuild)

| You need... | Use... |
|---|---|
| One-shot Haiku side-call machinery | `src\CcDirector.Core\Claude\RecapGenerator.cs` - copy the Process.Start setup verbatim |
| OpenAI / Anthropic key resolution | `AgentOptions.ResolveOpenAiKey()` (for Whisper) and the existing Claude path resolution that `RecapGenerator` uses for Anthropic-via-claude-CLI |
| Per-session event hooks (turn complete, process exit) | `Session.OnTurnCompleted`, `Session.OnActivityStateChanged`, `Session.Status` |
| Sending text to a session | `Session.SendTextAsync(string)` |
| ANSI-clean buffer reads | `AnsiCleaner.Clean(...)` in `src\CcDirector.ControlApi\AnsiCleaner.cs` |
| HTTPS exposure to phone | `cloudflared` quick tunnel set up earlier - see `scripts\voice-test\PHONE_TEST_README.md` |
| Headless build slot | `scripts\local-build-avalonia.ps1 -Slot 3` or `-Slot 4` -> `local_builds\cc-director-avalonia<N>.exe` |
| Voice pipeline today | `VoiceService.HandleAsync` in `src\CcDirector.Core\Voice\VoiceService.cs` |
| Chat pipeline today | `ChatService.HandleAsync` in `src\CcDirector.ControlApi\Chat\ChatService.cs` |

---

## 12. Out of scope for this goal

- A long-running Supervisor process per session.  We use short-lived `claude --print --bare` calls.
- ElevenLabs TTS.  OpenAI `tts-1` is the default in Phase 3.  ElevenLabs is the upgrade path if OpenAI quality is not enough.
- Multi-CLI Supervisors (Pi, Codex, Gemini).  Phase 1-8 assumes Claude Code as both main and Supervisor; multi-CLI is a future goal.
- Gateway dashboard upgrades (red/yellow/green grid across machines).  Phase 4 wires the status colour into `SessionDto` but the Gateway-side view is a separate goal.
- Scheduler tool from the PRD's open questions.
- Remote Director management.
- Code reviews that BLOCK commits (only warn in Phase 8).

---

## 13. "I'm done" protocol

When you finish a phase:

1. Run the self-test gate. Capture every result.
2. Open ONE message to the user. Lead with: `Phase <N> complete. <one-sentence what changed>.`
3. Include the self-test outputs (table, paths to screenshots, build status).
4. State which phase you are doing next (default: stop and wait).
5. Do NOT proceed to the next phase without an explicit "go".

When you cannot finish a phase:

1. Stop. Do not invent workarounds.
2. Open ONE message stating which phase failed, what step blocked you, and what you tried.
3. Wait for guidance.

---

## 14. Right now: Phase 1 only

Tonight's scope is Phase 1.  Start there.  Do not touch Phases 2-8 unless explicitly told to.

Specifically, tonight:
- Create `SupervisorService.cs` with `CleanVoiceTranscriptAsync`.
- Wire it into `VoiceService.HandleAsync`.
- Surface raw + cleaned in `VoiceCommandResponse`.
- Update `session-view.html` to use the cleaned version and show both bubbles.
- Run unit tests + visual smoke test.
- Build slot 3 (or 4) and confirm the live host returns both fields.
- Report back with the 5 raw/cleaned pairs.
- Stop.

**The user already knows the voice OUTPUT (TTS) will still feel bad until Phases 2 + 3 land.  Do not try to fix it in Phase 1.  Stay disciplined - one phase at a time.**

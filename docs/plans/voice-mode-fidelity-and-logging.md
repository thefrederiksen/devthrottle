# Voice mode: fidelity fix + per-turn comparison logging

Status: planned (implementing)
Owner: Soren
GitHub issue: #141 (https://github.com/thefrederiksen/cc-director/issues/141)
Related: docs/problems/voice-dictionary-not-applied-on-mobile.md (word-level mishearing, separate bug)

## Background

In voice mode ("wingman"), Soren saw the agent's full written reply in the raw
Claude session, then heard a spoken version that did NOT line up: it was too
abbreviated and took too big liberties with the meaning. Separately, when this
happens there is no trail to inspect, so we cannot prove where the divergence
came from.

This plan covers two jobs:

1. Make the spoken reply faithful to what the agent actually said.
2. Log each voice turn (audio + user transcript + agent reply + wingman spoken)
   with short retention so a future divergence can be flagged and compared.

## How the pipeline actually works (so the fix lands in the right place)

The phone client (`phone/CcDirectorClient/Voice/`) runs one voice turn in
`VoiceConversation.SpeakTurnAsync`:

1. INBOUND: `DirectorVoiceClient.TranscribeUtteranceAsync` ->
   `POST /voice/utterance` (register) -> `PUT .../chunk/0` -> `POST .../complete`.
   Server side, `VoiceUtteranceService.CompleteAsync` reassembles the audio blob,
   calls `VoiceService.TranscribeAndCleanAsync` (Whisper -> raw transcript, then
   Wingman cleanup -> cleaned transcript), then DELETES the temp dir. The audio is
   not retained anywhere today.
2. OUTBOUND: `DirectorVoiceClient.SendChatAsync` -> `POST /chat` with `Voice=true`.
   `ChatService` sends the transcript to the session, waits for the turn, reads the
   agent's real reply from the session JSONL (`ChatResponse.DisplayText`), and then
   builds the spoken version (`ChatResponse.Summary`) via
   `ClaudeSummarizer.SummarizeAsync`.
3. The initial send uses a 45s timeout; long turns return "timeout" and the client
   FOLLOWS the turn with `POST /chat {PollOnly:true, Voice:true}`. The FINAL "ok"
   reply the user hears therefore usually arrives on a POLL response
   (`BuildPollResponseAsync`), not the original send. Both code paths build a
   `Summary` via the same summarizer.

Key facts that shape the design:

- The crucial comparison pair the user wants (wingman-spoken vs agent-actual) is
  `Summary` vs `DisplayText`, and BOTH are available in the same place server-side
  (`ChatService`). No cross-call plumbing is needed to capture that pair.
- The audio + transcripts live in a different call (`/voice/utterance/complete`).
- Per voice turn the orchestrator is strictly sequential per session: it transcribes,
  waits for a stopping point, sends, then follows to completion before the next turn.
  So for a given session there is exactly one inbound `/complete` immediately
  followed by exactly one outbound "ok" `/chat`. That lets us correlate the two
  halves server-side by sessionId with no phone or contract changes.
- The endpoints construct `ChatService` / `VoiceUtteranceService` per request, so
  the turn log cannot hold per-request state; it must be stateless (disk-backed).

## Root cause of the over-abbreviation (Job 1)

`src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` `SummarizationPrompt`
instructs Haiku to "Rewrite it as two to four short, casual sentences, like
telling a friend what happened or what the answer is. Speak in concepts only."

For a direct factual answer (e.g. "the CEO is X and the CTO is Y"), that prompt:
- forces compression into 2-4 sentences regardless of how much answer there is,
- licenses paraphrase/reframing ("like telling a friend"),
- "concepts only" can drop the very facts that ARE the answer (names, numbers).

That is the liberty-taking. The TTS-hygiene parts (no code/paths/symbols read
aloud; say in words what code does) are legitimate and stay.

There are also two fallbacks that change meaning silently and violate the repo's
no-fallback rule:
- `SummarizeAsync` returns `TruncateForSpeech(response)` (first 300 chars) on any
  summarizer error.
- `RunClaudeSummarizationAsync` returns `TruncateForSpeech` on empty output.

## Job 1 changes

File: `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`

1. Rewrite `SummarizationPrompt` to be faithfulness-first:
   - Preserve the agent's actual answer and all concrete facts: names, numbers,
     yes/no, the decision/result. Do not omit them.
   - Do not add, embellish, reframe, or change the topic. If the agent did not
     answer, say that plainly; do not invent an answer.
   - Length should match the answer; remove the hard "two to four sentences" cap.
     Keep it spoken-friendly and tight, but completeness wins over brevity.
   - Keep TTS hygiene: no code, commands, file paths, function names, or symbols
     read aloud; describe in plain words what code does.
2. Failure behavior: on summarizer failure or empty output, return empty string
   (logged), NOT a truncated original. The client already speaks the genuine reply
   (`DisplayText`) when `Summary` is empty (`ChatTurnResult.SpokenText()`), so the
   user hears the real, complete answer rather than a silently truncated paraphrase.
   This removes both `TruncateForSpeech` fallbacks (the method can be deleted).
3. Keep the existing `< 200` char passthrough: a short reply is already faithful,
   so `CleanupForSpeech(response)` returns it as-is.

Tests (`src/CcDirector.Core.Tests/Voice/...`):
- A short reply is returned verbatim (after speech cleanup).
- On summarizer failure the result is empty (so the caller speaks the real reply),
  never a truncated paraphrase.
- Prompt-shape assertions where practical (no live CLI in unit tests; use the
  injected/mocked summarizer for behavior, keep prompt review manual).

## Job 2 changes: per-turn comparison log

New file: `src/CcDirector.Core/Voice/VoiceTurnLog.cs` (stateless, disk-backed).

Storage root: `%LOCALAPPDATA%\cc-director\voice-turn-logs\`
One directory per turn: `<yyyyMMdd-HHmmssfff>_<sessionShort>_<turnId>\` containing:
- `audio.<ext>`     - the reassembled utterance bytes (webm/m4a/ogg)
- `inbound.json`    - { turnId, sessionId, sessionName, tsUtc, rawTranscript,
                        cleanedTranscript, cleanupReason }
- `outbound.json`   - { tsUtc, agentReply (DisplayText), wingmanSpoken (Summary),
                        summarizerModel, status }

API (static, like `FileLog`):
- `WriteInbound(sessionId, sessionName, audioBytes, fileName, rawTranscript,
   cleanedTranscript, cleanupReason)` -> returns the turn dir; creates the dir,
   writes `audio.*` and `inbound.json`. Also runs the retention sweep (cheap).
- `AttachOutbound(sessionId, agentReply, wingmanSpoken, summarizerModel, status)`
   -> finds the newest turn dir for `sessionId` that has no `outbound.json` yet
   (within a short window, e.g. 10 min) and writes `outbound.json`. If none is
   found (e.g. a conductor poll with no preceding utterance), writes a standalone
   outbound-only turn dir so the wingman/agent pair is still captured.
- Retention: `RetentionDays = 5` (a few days, then purge). The sweep deletes turn
   dirs whose directory timestamp is older than the cutoff. Runs opportunistically
   on each `WriteInbound` (low volume, one scan per turn is fine).

Correlation: stateless, by sessionId + recency. Justified by the per-session
sequential guarantee above. We deliberately do NOT thread an explicit turnId
through the phone client + contracts for v1 (would touch the MAUI app and two DTOs
for no benefit today); if voice ever becomes concurrent within a session we revisit.

Wiring:
- INBOUND: `VoiceUtteranceService.CompleteAsync` already has the assembled blob
  bytes and the transcribe+clean response, and is the last place the audio exists
  before the temp dir is deleted. Capture the assembled bytes into a `byte[]`
  (currently a `MemoryStream`), pass them plus the response fields to
  `VoiceTurnLog.WriteInbound(...)` before `TryDelete(dir)`. Needs the sessionId,
  which the `/complete` endpoint already resolves (req.SessionId -> repoPath); pass
  `req.SessionId` through to `CompleteAsync`.
- OUTBOUND: in `ChatService`, add a single private helper
  `LogOutboundTurn(session, displayText, summary, status)` and call it from both
  the send-completion path (`HandleAsync`) and the poll path
  (`BuildPollResponseAsync`) whenever `voice == true && status == "ok" &&
  summary` is non-empty. It calls `VoiceTurnLog.AttachOutbound(...)`.

Tests (`src/CcDirector.Core.Tests/Voice/VoiceTurnLogTests.cs`):
- WriteInbound creates a dir with audio + inbound.json.
- AttachOutbound attaches to the newest unpaired inbound for the session.
- AttachOutbound with no pending inbound writes a standalone outbound dir.
- Retention purges dirs older than the cutoff and keeps newer ones.
- Use a temp root override so tests do not touch the real LocalAppData path.

## Out of scope (deliberately)

- The inbound meaning-drift suspect (Wingman transcript cleanup / CleanupOrchestrator)
  is NOT changed here. With the log in place we can capture a real example and
  decide based on evidence, per the handover's "measure, don't guess."
- No phone/MAUI changes, no contract changes.
- No retention UI; retention is a fixed short window with a code constant.

## Verification

- `dotnet build` the solution.
- Run the Core unit tests for Voice/Chat/Summarizer.
- Manual end-to-end (Soren, on device) once built: ask a direct factual question by
  voice, confirm the spoken answer now contains the actual facts, and confirm a
  turn dir appears under `voice-turn-logs` with audio + inbound.json + outbound.json
  whose `wingmanSpoken` and `agentReply` can be compared.

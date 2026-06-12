## Defect class

When a user voice turn is received, the voice turn endpoint reads the most recently available assistant text from the session transcript and summarizes it for TTS. If the agent has not yet processed the new user utterance when the endpoint reads the transcript, it captures and speaks the previous turn's output -- not the response to the current message.

The result is a systematic replay of prior agent output. Four consecutive user voice turns in a single session all exhibited this defect (2026-06-11 session e0a4ffbd, turns 22:44-23:16).

## Why it matters

The voice channel becomes unreliable for interactive back-and-forth. The user's words are swallowed -- they receive no confirmation of what they said and no answer to their question.

## Scope

**In:**
- `src/CcDirector.ControlApi/VoiceTurnEndpoint.cs` -- in `ReadLastAssistantText` (or its equivalent after any refactor): snapshot the JSONL file byte offset (`new FileInfo(jsonlPath).Length`) immediately before calling `session.SendTextAsync()`, then after the turn poll completes read only assistant messages that appear **after** that byte offset
- A unit/integration test that asserts the reply stage summary reflects the text sent in the current turn, not prior turns

**Out:**
- Changes to the Gateway async pipeline
- Changes to `ClaudeSummarizer.cs`
- Changes to `TtsService.cs`

## Acceptance Criteria

- [ ] When two consecutive voice turns are submitted to the same session, the second turn's spoken summary reflects the agent's response to the second turn's input -- not the first turn's response
- [ ] `ReadLastAssistantText` (or replacement) uses a post-send byte offset to filter; no assistant content from before the current turn can leak into the summary
- [ ] `dotnet build cc-director.sln` succeeds with 0 errors
- [ ] `dotnet test --filter VoiceTurn` passes (existing tests unaffected, new regression test added)

## Affected Containers

- `CcDirector.ControlApi`

## Proof Target

A passing test `VoiceTurn_TwoConsecutiveTurns_SecondTurnSpeaksCurrentReply` (or equivalent name) shown in the test output, confirming the fix is covered by a regression test.

## Assumptions

- The JSONL file path is accessible inside the endpoint via `session.ClaudeSessionId` and `Core.Claude.ClaudeSessionReader.GetJsonlPath()` -- same path already used by `ReadLastAssistantText`.
- If `VoiceTurnHelpers.cs` exists on the branch and contains the relevant logic, the fix goes there; if the logic is in `VoiceTurnEndpoint.cs` directly, it goes there. The Developer Agent must verify which file is canonical before implementing.

## Evidence (local, personal -- not included here)

Turn ids: 82e62740d3c44a63845796a3a82fb4a4, 67a25463136e4ae3af280fb1d6149f9a, 637edc39c10249fc800a15b43d1f8636, 66b750d50e71474cbcdd89dcb9e02971
Reviewed by voice-review skill on 2026-06-12.

## Defect class

The Haiku voice summarizer (`ClaudeSummarizer` in `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`) incorrectly identifies valid non-Latin Unicode content (Korean, Japanese, Arabic, etc.) as encoding corruption and refuses to produce a spoken summary. The spoken output is a refusal message explaining that the text cannot be read -- the agent's entire reply is dropped.

## Why it matters

A user conducting a voice session in a non-Latin language receives no spoken feedback when the agent replies. The voice channel goes completely silent on every such turn -- a full service failure for non-English sessions.

## Scope

**In:**
- `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`:
  - Remove any encoding-validation logic or pre-processing that flags non-ASCII code points as invalid
  - Update the summarizer prompt to explicitly instruct the model to summarize faithfully in the source language (or in English with a brief note) without treating non-Latin characters as errors
- A unit test that passes a Korean or Japanese agent reply to `ClaudeSummarizer.CleanupForSpeech` (or the equivalent pre-processing method) and asserts the content is not dropped

**Out:**
- Changes to `VoiceTurnEndpoint.cs`
- Changes to `TtsService.cs`
- Changes to the Gateway

## Acceptance Criteria

- [ ] A test string containing Korean characters (e.g. "안녕하세요, 도움이 필요하시면 말씀해 주세요.") passed through `CleanupForSpeech` (or the relevant pre-processing path) is not reduced to an empty or near-empty string
- [ ] The summarizer prompt does not contain instructions to reject, flag, or replace non-ASCII characters
- [ ] `dotnet build cc-director.sln` succeeds with 0 errors
- [ ] `dotnet test --filter ClaudeSummarizer` passes (existing tests unaffected, new test added)

## Affected Containers

- `CcDirector.Core`

## Proof Target

A passing test `ClaudeSummarizer_NonLatinInput_IsNotDropped` (or equivalent) shown in `dotnet test` output, confirming non-Latin content survives pre-processing.

## Assumptions

- The defect is in pre-processing or the prompt text in `ClaudeSummarizer.cs`. If the encoding guard is elsewhere in the pipeline (e.g. in `VoiceTurnEndpoint.cs` before the summarizer call), the Developer Agent must find and fix it there instead.

## Evidence (local, personal -- not included here)

Turn id: ac38af33e4c84947ab5ea50c164e1add
Archived at: %LOCALAPPDATA%\cc-director\voice-review\flagged\ac38af33e4c84947ab5ea50c164e1add\
Reviewed by voice-review skill on 2026-06-12.

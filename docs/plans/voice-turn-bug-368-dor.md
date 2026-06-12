## Defect class

The `CleanupForSpeech` method in `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` strips the content enclosed in backticks from the agent reply before TTS. Session names, boolean values, and technical identifiers formatted as `` `value` `` are silently removed from the spoken output. The resulting sentence is grammatically incomplete and unintelligible.

## Why it matters

The user receives a voice confirmation that an action was taken but cannot tell from the spoken audio what was actually affected. When the confirmed item matters (e.g. "did the right session get disabled?"), the missing identifier leaves the user unable to verify. This undermines the reliability of voice-mode confirmations.

## Scope

**In:**
- `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs` -- in `CleanupForSpeech` (around line 261 per architecture doc): change the backtick regex from one that deletes the enclosed content to one that strips the backtick markers and keeps the inner text (e.g. `` `sessionName` `` -> "sessionName")
- A unit test asserting that a string containing backtick-wrapped identifiers retains those identifiers after `CleanupForSpeech`

**Out:**
- Changes to `VoiceTurnEndpoint.cs`
- Changes to the Gateway
- Phone changes

## Acceptance Criteria

- [ ] `CleanupForSpeech("Enable session `my-session` is done")` returns a string containing "my-session" (the identifier is preserved)
- [ ] `CleanupForSpeech("Set `wingmanEnabled=False`")` returns a string containing "wingmanEnabled=False"
- [ ] Backtick markers themselves are removed (no backtick characters in the output)
- [ ] Existing behavior for code blocks (triple backtick) is unaffected -- code blocks are still stripped
- [ ] `dotnet build cc-director.sln` succeeds with 0 errors
- [ ] `dotnet test --filter ClaudeSummarizer` passes (existing tests unaffected, new tests added)

## Affected Containers

- `CcDirector.Core`

## Proof Target

Passing tests `ClaudeSummarizer_BacktickIdentifier_IsPreserved` and `ClaudeSummarizer_TripleBacktickCodeBlock_IsStripped` (or equivalent names) shown in `dotnet test` output, confirming the fix handles both cases correctly.

## Assumptions

- The fix is in `ClaudeSummarizer.CleanupForSpeech`. If backtick stripping happens elsewhere (e.g. in a summarizer prompt), the Developer Agent must find and fix it there.

## Evidence (local, personal -- not included here)

Turn id: d46c37c3c8f841fe9200753c164e1add
Archived at: %LOCALAPPDATA%\cc-director\voice-review\flagged\d46c37c3c8f841fe9200753c164e1add\
Reviewed by voice-review skill on 2026-06-12.

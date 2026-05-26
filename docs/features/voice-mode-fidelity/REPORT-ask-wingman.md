# Ask-the-Wingman - Build Report

Date: 2026-05-25

## What this fixes

Voice mode forced everything through a summarizer: a dictated turn returned the capped
`spoken_text` (~280 chars, "outcome not process"), "What's happening?" was explicitly
terse, and the free-text ask was both capped at "1-3 short sentences" and fed only a
4000-char terminal tail. So "read me the whole article we just wrote" came back as a
3-sentence gist, and there was no way to address the wingman directly. The user ended up
in the raw tab.

## What was built

A dedicated **Ask-the-Wingman** voice channel, separate from talking to the agent. You
say "Hey wingman, ..." (or tap an Ask-Wingman button) and the wingman answers from the
session - reading content back VERBATIM when asked, never summarizing. Talking to the
agent is unchanged.

### Server

- `WingmanService.AnswerViaSessionAsync` + `BuildWingmanAnswerSessionPrompt`: a
  read-only full-power session (Read/Grep/Glob, MCP off, no writes, no PTY access) on
  the strong model (opus), handed the WHOLE terminal as a snapshot file plus the repo as
  its working directory. It reads as much as it needs and reproduces content word-for-word.
  No length cap on the answer. Reuses the proven `RunWingmanSessionAsync` mechanism from
  the terminal-state classifier; read-only by construction.
- `POST /sessions/{sid}/wingman/ask`: a free-text question now routes to this faithful
  path; `mode=explain` (the terse briefing) is unchanged. The old terse one-shot ask
  (Haiku, 1-3 sentences, 4000-char tail) is retired from the endpoint.
- `CleanVoiceTranscriptAsync`: moved off Haiku to the strong model and now also returns a
  routing `target` ("agent" | "wingman"). It detects a "Hey wingman" wake phrase (LLM
  intent, not regex - tolerant of Whisper mangling), strips the phrase, and defaults to
  "agent" when unsure. Carried on the wire as `VoiceCommandResponse.RouteTarget`.

### Phone (MAUI Android)

- `DirectorVoiceClient.AskWingmanAsync` (POST /wingman/ask); `TranscribeUtteranceAsync`
  now returns `TranscribeResult(Text, Target)`.
- `VoiceConversation.SpeakTurnAsync` branches: target=wingman (or the explicit button)
  -> ask the wingman and speak the answer immediately (no waiting on the agent's turn,
  no /chat); otherwise the existing agent flow.
- `TalkPage`: an "Ask Wingman" button beside "What's happening?", sharing the same
  push-to-talk record/stop mechanics as Talk.

## Verification

- Build: full solution clean (0 errors); MAUI Android client clean (0 errors, only
  pre-existing NU1608 NuGet version warnings).
- Unit tests: 64 WingmanService tests pass, including new coverage of route-target
  parsing (wingman/agent/absent/unknown/case-insensitive) and the verbatim answer prompt.
  Full Core suite green (one unrelated `SessionLogWriter` test is a known parallel
  temp-dir race; passes in isolation).
- LIVE tests against real opus (`WingmanAnswerLiveTests`, opt-in via
  `WINGMAN_LIVE_TESTS=1`):

  | Test | Result | Evidence |
  | --- | --- | --- |
  | Read an article back verbatim | PASS | model=opus, 8.6s, 591 chars (full body); both rare planted sentences returned intact, not reworded or shortened |
  | "Hey wingman, read me the whole article" | PASS | target=wingman; cleaned="read me the whole article we just wrote." (wake phrase stripped) |
  | "Um, can you fix the bug in the login flow please." | PASS | target=agent; cleaned="Can you fix the bug in the login flow please." (filler removed, left for the agent) |

## Wingman Charter + full no-Haiku conversion + audit gate

The Ask-the-Wingman work fixed one corner; the Wingman as a whole was still mostly Haiku.
That is now resolved across the board:

- **Charter:** `docs/wingman/CHARTER.md` is the single source of truth for what the
  Wingman must achieve and its hard invariants (strong-model-only / never Haiku;
  read-only; faithful not summarizing; stateless; fail-closed; genuinely helpful). It
  applies everywhere the Wingman is used and names the placement rule (all Wingman code
  under `src/CcDirector.Core/Wingman/`).
- **Full conversion:** the Wingman now runs on ONE strong model, `WingmanService.Model`
  (`opus`). Every previously-Haiku path is converted: terminal-state classification, the
  per-turn summary, rules enforcement, goal assessment, the explain briefing, the ask,
  and transcript cleanup. The `DefaultModel`/`StrongModel` constants are now aliases of
  the single `Model`; the only quoted cheap-model literal in the Wingman is gone.
- **Audit gate:** `CcDirector.Core.Tests/Wingman/WingmanCharterAuditTests.cs` runs in the
  normal suite and FAILS THE BUILD if any file under `src/CcDirector.Core/Wingman/`
  reintroduces a cheap-model literal, re-points the model off a strong tier, or gives a
  Wingman session a non-read-only tool. It scans the whole directory, so new Wingman
  files are covered automatically as the component grows. Verified: 3/3 pass, scanning 8
  Wingman files and validating 2 real `allowedTools` arguments.

## Remaining

- On-device E2E on the phone (the usual Soren step): say "Hey wingman, read me the whole
  article" and confirm it reads the real text aloud; confirm an ordinary instruction
  still goes to the agent.
- Phase 5 (an automatic spoken "done vs working" cue) was deferred: the wingman channel
  now answers "is it done?" faithfully on request. Revisit if the automatic cue is still
  wanted.

## Notes

- No Haiku anywhere in the Wingman now, enforced by the audit gate (not just promised).
- Cost/latency: every Wingman call is opus, including high-frequency ones (terminal-state
  classification runs whenever a session goes quiet; transcript cleanup runs on every
  dictated utterance). This is the explicit tradeoff for trust over cheapness. If a hot
  path feels slow, the charter allows a second STRONG tier (e.g. sonnet) for those paths -
  never a return to Haiku. Flag it and I will tune.
- Two adjacent summarizers feed the same voice experience but live outside the Wingman
  namespace and still use a cheap model: `Core/Voice/Services/ClaudeSummarizer` (the voice
  spoken turn summary) and `Core/Claude/RecapGenerator` (conductor recap). Decision
  pending: bring them under the charter (and the audit), or leave them out of scope.
- Not committed - awaiting your go-ahead.

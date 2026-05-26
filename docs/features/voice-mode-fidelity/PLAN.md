# Ask-the-Wingman - Implementation Plan

Status: BUILT (2026-05-25). Server path live-tested against real opus (verbatim read-back
+ "Hey wingman" routing both pass). Phone compiles (MAUI Android); on-device E2E is the
remaining step. Phase 5 (automatic done-vs-working spoken cue) deferred - the wingman
channel now answers "is it done?" faithfully on request. See REPORT.md.

## 1. The problem

Voice mode is useless for getting at actual content. Everything you hear is forced
through a summarizer:

- A normal dictated turn goes to `POST /chat` (the agent). The phone reads back the
  capped `spoken_text` summary (the fidelity-first turn summarizer caps at ~280 chars,
  "1 to 3 short sentences, outcome not process"). When the agent writes a whole
  article, you hear a 3-sentence gist of it.
- "What's happening?" hits `/wingman/ask` `mode=explain`, whose prompt is explicitly
  terse ("Be terse. 1 to 3 short sentences, fewer is better.").
- The free-text ask path (`BuildAskPrompt`, WingmanService.cs:1061) is also told
  "Respond in 1-3 short sentences" AND is fed only a 4000-char buffer tail plus lossy
  headline summaries (AppendSessionContext, WingmanService.cs:1196). Even with the
  terseness rule gone, it physically cannot read a whole article: the full text is not
  in its context.

There is no path that reads you actual content, and there is no way to address the
Wingman directly by voice. You end up in the raw tab.

## 2. North Star

A first-class **Ask-the-Wingman** channel, separate from talking to the agent. You
speak a question to the Wingman ("read me the whole article we just wrote", "what did
it decide?", "what files changed?") and it answers faithfully from the session - no
summarizing, no length cap, reading content verbatim when you ask for it. It has as
much read access as it needs to answer (full terminal scrollback + the repo). The
existing "talk to the agent" path is untouched; this is purely additive.

Two ways to reach it:
1. Say "Hey wingman, ..." in the normal talk flow and the utterance routes to the
   Wingman instead of the agent.
2. A dedicated Ask-Wingman button for when you would rather not say the phrase.

## 3. Decisions already locked (from review with Soren)

- **No Haiku anywhere in this feature.** The Wingman ask and any LLM call involved in
  routing to it run on the strong model (opus). Today `AskAboutSessionAsync` uses
  `DefaultModel` = haiku for the free-text path (WingmanService.cs:26, 972); that
  changes.
- **The ask runs as a read-only full-power session, not a one-shot over a truncated
  tail.** It gets Read/Grep/Glob (no writes, no PTY access) and the full terminal
  snapshot + repo, so it reads as much as it needs. This is the exact mechanism
  `ClassifyTerminalStateViaSessionAsync` already uses (WingmanService.cs:131 ->
  `RunWingmanSessionAsync`, allowedTools "Read Grep Glob", read-only by construction).
- **No length cap on the answer.** You interrupt the TTS if it is too long.
- **"Hey wingman" detection is LLM intent, not a regex.** The transcript already gets
  cleaned by an LLM before anything is sent; that call also returns a routing target
  so Whisper mangling "wingman" does not break it. (Aligns with the prefer-LLM-over-
  regex rule.)

## 4. Current State (verified)

- Endpoint exists: `POST /sessions/{sid}/wingman/ask` (ControlEndpoints.cs:148) takes
  `{ Question, Mode }`. `mode=explain` -> terse briefing (opus); a free-text question
  -> `BuildAskPrompt` (haiku, 1-3 sentences). Context built by
  `WingmanContextBuilder.BuildAsync` (ControlEndpoints.cs:162), then
  `WingmanService.AskAboutSessionAsync` (WingmanService.cs:958).
- The read-only full-power session machinery exists and is proven:
  `ClassifyTerminalStateViaSessionAsync` (WingmanService.cs:131) writes the terminal
  to a temp snapshot, runs `claude --print` with Read/Grep/Glob, parses the result,
  deletes the snapshot. It only returns a state verdict today; we need a sibling that
  returns a free-text answer.
- Phone routing: dictated speech only ever goes to `/chat` via
  `DirectorVoiceClient.SendChatAsync` (DirectorVoiceClient.cs:105). The only thing that
  reaches the Wingman is the fixed "What's happening?" button ->
  `ExplainAsync` (DirectorVoiceClient.cs:160 -> `OnWhatsHappeningClicked`,
  TalkPage.xaml.cs:427). There is no "dictate a question to the Wingman" path.
- Transcript cleanup is server-side during `/voice/utterance/complete`:
  `VoiceService.TranscribeAndCleanAsync` -> `WingmanService.CleanVoiceTranscriptAsync`
  (WingmanService.cs:281), returns `{ cleaned, reason }`. The phone receives the
  cleaned transcript from `TranscribeUtteranceAsync` (DirectorVoiceClient.cs:~97).

## 5. Plan

### Phase 1 - Server: a faithful, full-access Wingman answer
- Add `WingmanService.AnswerViaSessionAsync(question, repoPath, fullTerminalText,
  claudeExePath, ct)` modeled on `ClassifyTerminalStateViaSessionAsync`: snapshot the
  full terminal to a temp file, run a read-only full-power session (Read/Grep/Glob, MCP
  off) on the strong model, and return the answer as plain text. Read-only by
  construction (no write tools, no Send path to the PTY), stateless, temp file deleted
  in `finally`.
- Prompt design (`BuildWingmanAnswerSessionPrompt`): "You are the read-only Wingman for
  this session. Answer the user's question faithfully and completely from what you can
  read. If the user asks you to read content (an article, a file, the agent's last
  reply), read it back VERBATIM - do NOT summarize, shorten, or paraphrase. Use Read to
  open the relevant file or scrollback; read as much as you need. No length limit." It
  may Read the repo, so "the article we just wrote" is found whether it is in the
  scrollback or already written to a file.
- No length cap on the returned answer (remove the 4000-char clamp for this path;
  WingmanService.cs:1002 applies only to the legacy ask path).
- Tests: prompt builder snapshot; parse/return passthrough; read-only guarantee
  (allowed tools contain no writer); strong-model selection asserted.

### Phase 2 - Server: route the ask endpoint to the new path
- Extend `WingmanAskRequest` with an explicit `Mode = "answer"` (free-text question,
  faithful full-access) alongside the existing `explain`. Keep `explain` as-is.
- In the endpoint (ControlEndpoints.cs:148), for `mode=answer` build the full terminal
  text (the session already exposes the buffer used for context) and call
  `AnswerViaSessionAsync`. The legacy terse `BuildAskPrompt` path can stay for now or be
  retired; recommend retiring it so there is one ask behavior and no haiku (confirm).
- Tests: endpoint dispatches `answer` to the session path; `explain` unchanged.

### Phase 3 - Server: "Hey wingman" routing folded into cleanup
- Upgrade `CleanVoiceTranscriptAsync` off haiku to the strong model and extend its
  output to `{ cleaned, reason, target }` where `target` is `agent` | `wingman`. The
  prompt decides from natural language whether the user addressed the Wingman ("Hey
  wingman ...", "wingman, ...", "ask the wingman to ...") and, when it did, strips the
  wake phrase from `cleaned` so the remaining question is clean to forward.
- `VoiceUtteranceService.CompleteAsync` returns `target` to the phone alongside the
  cleaned transcript.
- Tests: "Hey wingman, read me the article" -> target=wingman, cleaned strips the
  phrase; an ordinary instruction -> target=agent, cleaned unchanged.

### Phase 4 - Phone: act on the target + add the button
- In the talk flow, after transcription, branch on `target`: `agent` -> existing
  `SendChatAsync` (`/chat`); `wingman` -> new `DirectorVoiceClient.AskWingmanAsync`
  (`POST /wingman/ask` with the question and `mode=answer`), then read the answer aloud
  with the existing TTS path (mirrors `OnWhatsHappeningClicked`, TalkPage.xaml.cs:427).
- Add an explicit "Ask Wingman" button next to "What's happening?" that captures a
  dictated question and always routes to the Wingman answer path. Follow
  docs/VisualStyle.md.
- Status copy: distinguish "Asking Wingman..." from "Sending..." so it is clear which
  channel you hit.
- Tests: client posts `mode=answer` with the question; roster/contract parse of the new
  `target` field on the complete-utterance response.

### Phase 5 - "Is it done?" clarity (smaller, fold in if cheap)
- Out of strict scope for Ask-the-Wingman, but the same review surfaced that you cannot
  tell by ear when a turn finished. The done/working/waiting verdict is already computed
  deterministically (the badge color). If cheap, have the talk flow speak a short,
  consistent completion phrase tied to that verdict ("Claude is done, waiting for you"
  vs "still working") instead of relying on summary prose. Confirm whether to include.

## 6. Sequencing
1 -> 2 deliver the faithful answer over REST (testable without the phone). 3 -> 4 add
the two ways to reach it from voice. 3 depends on nothing in 1/2; 4 depends on 1-3. 5 is
independent and optional.

## 7. Testing Strategy
- Unit tests per phase (Arrange-Act-Assert, Method_Scenario_Result).
- REST end-to-end before touching the phone: `POST /wingman/ask` `mode=answer` with
  "read me the file X we just wrote" returns the file contents verbatim, not a summary.
- Manual end-to-end on the real phone (not a stand-in), per project rule: say
  "Hey wingman, read me the whole article" and confirm it reads the actual text aloud;
  confirm an ordinary instruction still goes to the agent.
- Deliver a short HTML report (cc-html boardroom) with evidence.

## 8. Risks and Notes
- Read-only guarantee is load-bearing: the answer session must never get a write tool
  or any path to the partner PTY. Reuse the existing allowed-tools list verbatim; add a
  test that asserts no writer is present. (Wingman-terminal-readonly invariant.)
- Latency: a full-power session is seconds, not sub-second. That is the price of being
  able to read whole files; acceptable here and the user was told. No fast/slow fallback
  - one behavior (no-fallback rule).
- Cost/latency of moving cleanup to opus: every utterance's cleanup call gets pricier so
  it can make the agent-vs-wingman routing reliably. Accepted per "no haiku for any of
  this."
- Routing precision: if the cleanup LLM is unsure of intent, default `target=agent`
  (the safe, existing behavior) rather than guessing wingman - but surface that it is a
  deliberate default, not a silent fallback.
- Remote access stays HTTPS-only via Tailscale; no new plain-HTTP surface.

## 9. Open Questions for Soren
1. Retire the legacy terse free-text `BuildAskPrompt` path entirely (recommended: yes,
   so there is one ask behavior and zero haiku), or keep it as a separate fast path?
2. Include Phase 5 (spoken "done vs working" cue) in this work, or track it separately?
3. Button placement: a second button next to "What's happening?", or fold both into one
   Wingman affordance?

# Wingman Turn Briefing - the architecture for understanding a turn

**Status:** v3.4 - BUILT; contract frozen against captured examples (incl. multi-select), extended additively with the session headline + turn title (v2.2), the chapter model - headline = chapter title + explicit `newChapter` boundary (v2.3, 2026-06-05), the mission-complete `suggestedAction` (v2.4, 2026-06-06, issue #201), the cold-reader bar (v3, issue #205), the parked-reply invariants (v3.2, issue #208), action-first rail/buttons (v3.3), and the **FIDELITY guard (v3.4, 2026-06-09)**

## v3.4 - the fidelity guard (the trust fix)

The wingman was distrusted because it RE-DERIVED a turn's narrative from the raw screen
scrape and could contradict what the agent actually said (a real brief inverted a bug finding
into "nothing is broken, purely cosmetic" at `confidence: high`). One caught contradiction
destroys trust for every later brief, so the user fell back to reading the terminal ~80% of
the time. v3.4 binds the brief to the agent's verbatim words:

- The prompt's framing changed from "your job is INTERPRETATION" to **the agent's reply is
  GROUND TRUTH** - the wingman COMPRESSES it and adds the decision layer; it never restates
  the reply's conclusions in its own words and never asserts anything the reply contradicts
  (the NON-CONTRADICTION rule).
- `evidence` is now REQUIRED on every `needsYou` and is the agent's verbatim decisive line.
  Mechanical validation (`ParseAndValidate`) REJECTS a `needsYou` that has no verbatim anchor
  in the reply or the screen when there IS source text to quote (degrades to an honest stub) -
  pre-v3.4 it merely dropped the receipt and shipped the unanchored brief. Parked-reply briefs
  are exempt: they already carry the stronger invariant that the statement quotes the user's
  verbatim typed text.
- Consumers render `evidence` FIRST and expanded, as Claude's own words ("Claude said"), above
  the synthesized statement - it is the anchor that proves the statement matches the reply, not
  a collapsed footnote (Cockpit BriefPane, phone NeedsYou card).
**Supersedes:** the rule-based parts of the Brief view (docs/plans/cockpit-brief-view.md), which
become this design's labeled degrade tier.
**Related:** docs/wingman/CHARTER.md (the invariants this design must honor)

---

## 0. Mission

**The human - the meat computer - is the bottleneck.** One person runs many agent sessions;
the agents are fast and the human's attention is the scarcest resource in the whole system.
Every piece of this architecture exists for exactly one purpose: **reduce the meat computer's
cognitive load and multiply it with helpers, so we build faster.**

The wingman's trajectory: today, a turn-interpreter that tells the human what each session
wants in one glance. Over time, a **super-smart helper** - the meat computer's second brain
across the whole fleet. Every design decision in this document gets judged through that lens:
does it take load OFF the human, or does it quietly hand the human another parsing job?

A corollary that is now LAW (see D6): **interpretation is never done with regex or
text-parsing.** Cheap mechanical rules produce confidently-wrong output, and confidently-wrong
output COSTS the human attention instead of saving it. A helper that has to be double-checked
is not a helper. Interpretation is done by the best model we have, with structured output.

---

## 1. The problem

The Cockpit Brief view answers "where was I?" with three blocks: YOU ASKED / CLAUDE DID /
NEEDS YOU. v1 fills those blocks by EXTRACTION - the literal last user message, a cheap-model
condensation, the needs-you sentence located (or the reply's final paragraph), and option
buttons parsed by regex.

Extraction fails, and we have the receipts. **Example 1** (screenshots in `examples/`):

- `example1-brief-as-shipped.png` - the Brief as v1 rendered it
- `example1-terminal-truth.png` - the terminal truth of the same moment

(Both redacted for the public repo: machine name barred out; the terminal shot downscaled so
incidental paths in the body text are illegible - its point is the wall of text, not the words.
The text captures in this folder had the same scrub: email/name/user-paths genericized.)

What the v1 page showed for a real turn:

| Block | v1 showed | The truth |
|---|---|---|
| YOU ASKED | `yes` | The user had approved building 3 layout fixes; "yes" was the approval. The literal last message carries zero information. |
| NEEDS YOU | "Live on 7471 now (hard-refresh). Uncommitted - /commit when you're happy with it." | Not a question. The actual need: *review the new layout, then decide commit-or-change*. That sentence exists nowhere in the reply - it must be synthesized. |
| Quick buttons | "1 Block order...", "2 Honest working state...", "3 Button gate..." | The agent's numbered SUMMARY list, not choices. A gate against exactly this had been added hours earlier; it passed anyway ("7471" contains digits). |

The root cause is structural, not a tuning problem:

1. **Claude Code's replies are written for a conversation, not a decision UI.** The "what do
   you need from me" signal is usually implicit, soft, or scattered. Extraction assumes the
   signal is explicit somewhere in the text. It often is not.
2. **The ask requires cross-turn interpretation.** A turn whose user message is "yes" can only
   be described by knowing what was being approved. No single-turn extraction can produce it.
3. **Rules are whack-a-mole.** Every parser fix (substring validation, final-paragraph
   fallback, digit gates) failed on the next reply shape, each time in a new way. The failures
   above happened on the SAME DAY the rules were written, on the very session that wrote them.

**Conclusion: determining what a turn means - what the user is doing, what the agent did,
what is actually needed - is interpretation, and interpretation is a strong-model job. This is
the Wingman's job.** (Charter: the Wingman runs on the strong model only; never Haiku/cheap
models in an interpretation path.)

## 2. Position (decided)

- **Synthesis for the headline, verbatim for the receipts.** The needs-you statement is
  WRITTEN by the wingman ("review the layout, then decide: commit or change"), and the
  verbatim quote from the reply is preserved underneath as expandable evidence. Trust requires
  receipts; usefulness requires synthesis. Neither replaces the other.
- **Eager at turn end, never lazy at view time.** An opus reading takes ~15-60s. Lazy-on-view
  would mean a spinner where the answer should be. Generated at TURN END, the latency is
  absorbed by a visible BRIEFING (yellow) state - by the time the user flips to the session,
  the brief is ready. This is the architectural unlock that makes "best model on every turn"
  viable at all.
- **The wingman owns all three blocks in one call.** Intent, DID, and NEEDS YOU come from one
  coherent reading of the turn, not three pipelines that can disagree.

## 2b. Prior art: the Director session view (learn from, do NOT build on)

The Director's embedded web UI (`src/CcDirector.ControlApi/Web/session-view.html`, served by
every Director at `/sessions/{sid}/view`; manager list at `/`) already shipped a wingman-driven
version of several of these ideas. It is NOT the foundation - this design replaces it - but
four things in it are proven and get carried forward as concepts:

**Take (concepts and prompt wisdom, not code):**

1. **The distilled-question pattern.** When red, the page shows `needsUserShort` - a
   wingman-WRITTEN <=500-char statement of what is needed - with a "full agent text" verbatim
   expander. That is this design's synthesized-headline + verbatim-receipts pattern, already
   validated in use. The generation prompt lives in `WingmanService.cs` (turn summaries,
   `NeedsUserShort`) and is the starting point for the TurnBrief prompt.
2. **LLM-generated quick replies.** `WingmanAskResult.QuickReplies` proves the wingman can
   propose tap-to-answer options. BUT its implementation post-parses options out of prose
   (`ExtractQuickReplies` - text parsing, the same disease as regex). We take the idea; the
   TurnBrief contract receives options as STRUCTURED model output, never parsed from text.
3. **The UserPromptSubmit hook as the ask source.** The page's "YOU ASKED" comes from the
   prompt-submit hook, not the transcript: live during the turn, and source-agnostic
   (terminal / phone / voice). The turn package adopts it alongside the JSONL delta.
4. **The state-vote feedback widget.** The page lets the user say "this state is wrong, and
   why". The Brief gets the same: a "this brief is wrong" action whose reports are STORED AS
   LABELED EXAMPLES (D7) - the supervised corpus that improves the wingman's prompts against
   reality instead of guesses, and feeds the v2 example work in section 7.

**Do not take:** the page itself, the poll-driven explain pipeline, the per-session
wingman-opt-in coupling, and every instance of parsing model answers out of prose.

## 3. The turn lifecycle

```
                         THE TURN LIFECYCLE WITH THE WINGMAN

  (blue) WORKING ──────────────── turn ends (TerminalStateDetector) ───────┐
                                                                           v
  ┌─────────────────────────  (yellow) BRIEFING  ──────────────────────────┐
  |                                                                        |
  |  [1] Director assembles the TURN PACKAGE (no LLM):                     |
  |      - this turn's transcript delta (user msg, reply, tools used)      |
  |      - the turn's prompt from the UserPromptSubmit HOOK (live,         |
  |        source-agnostic: terminal / phone / voice - prior art #3)       |
  |      - the ROLLING INTENT carried forward from previous briefs         |
  |        (fixes the "yes" problem)                                       |
  |      - previous turn briefs (the session's story so far)               |
  |      - current screen grid tail (catches interactive pickers the       |
  |        transcript cannot see - see section 7 / v2 caveat)              |
  |                                                                        |
  |  [2] WINGMAN - one structured strong-model call (charter: read-only,   |
  |      never writes to the PTY):  in: turn package -> out: TurnBrief     |
  |                                                                        |
  |  [3] TurnBriefStore - persisted per session per turn; survives         |
  |      restarts; IS the session's history of record                      |
  └────────────────────────────────┬──────────────────────────────────────┘
                                   v
        needsYou != null ──> (red) NEEDS YOU - the rail shows the
                                   synthesized railLine, not a state enum
        needsYou == null ──> (green/idle) done
                                   |
                                   v
  [4] Consumers render the STORED TurnBrief - no client-side parsing,
      no regex anywhere:
      - Cockpit Brief page (option buttons ARE the wingman's options)
      - rail rows / triage view (railLine)
      - phone FIFO cards
      - voice mode "what's happening"
```

## 4. The TurnBrief contract

```json
TurnBrief {
  sessionId, turnNumber, generatedAtUtc, model,

  headline:   "<= 6 words, newspaper-tight: the current CHAPTER's title (v2.3;
               introduced as the session headline in v2.2) - WHAT the session is
               working on, never how. Several turns share one chapter; the
               wingman receives the current title in the turn package, usually
               copies it verbatim, and may REFINE the wording when the same work
               drifts (a reworded title is still the same chapter).",

  newChapter: false | true (v2.3) - true ONLY when this turn moved the session
               to a genuinely different piece of work. The EXPLICIT chapter
               boundary: consumers group briefs into chapter cards on this flag,
               never by comparing headline strings. The first stored title
               mechanically starts the first chapter (validation enforces it);
               degrade tiers never set it. Returning to earlier work later IS a
               new chapter (the same title may recur).

  turnTitle:  "<= 8 words, past tense: what THIS turn did (v2.2) - the turn row
               inside a chapter card in the Cockpit's session story panel",

  intent:     "one or two sentences: what the user is trying to get done,
               carried and UPDATED across turns - never the literal message",

  did:        [ "3-6 bullets: what the agent concretely did/decided this turn" ],

  needsYou:   null | {
    statement:  "synthesized, crisp: leads with whether anything is broken or
                 blocking, then names the concrete action(s)",
    answerVia:  "reply" | "keys",
                 // reply: a typed message answers it (plain-text asks)
                 // keys:  an on-screen menu answers it (picker / permission /
                 //        plan approval) - options carry the key sequence and the
                 //        one-tap button sends it through the raw-input path, so
                 //        even interactive menus are answerable WITHOUT the terminal
    selectionMode: "single" | "multiple",
                 // multiple = a "pick any that apply" checklist (captured live):
                 // option buttons TOGGLE, and the separate `submit` send completes
                 // the answer. single = one tap answers outright.
    submit:     "\r" | null,   // the completing send for selectionMode: multiple
    options:    [ { key: "1 Terse", send: "3\r", note?: "standing grant" }, ... ],
                 // REAL choices the wingman decided exist. May be empty.
                 // A 'type something' affordance is allowed; fake choices are not.
                 // `send` is what one tap transmits (reply text or key sequence).
                 // `note` flags scope/risk (e.g. a permission option that grants
                 // standing access - see the permission-prompt example).
                 // STRUCTURED model output (schema-validated), NEVER parsed out of
                 // prose - the old ExtractQuickReplies approach is banned (D6).
    evidence:   "VERBATIM quote(s) from the reply or the screen - the receipts",
    urgency:    "blocking" | "review" | "fyi",
                 // blocking: the agent cannot continue (picker, permission, plan menu)
                 // review:   finished work awaiting a verdict (Example 1)
                 // fyi:      nothing needed, but a real decision is available
                 //           (ambiguous-thoughts example) - the rail does NOT go red
    confidence: "high" | "ambiguous",
                 // ambiguous => the statement says so honestly:
                 // "unclear; likely X or Y" - never invented certainty
    railLine:   "<= 8 words for the rail / FIFO card / voice"
  },
  suggestedAction: null | {        // v2.4 (issue #201): MISSION COMPLETE
    type:   "close_session",       // ENUMERATED vocabulary - free text never survives
                                   // validation; consumers ignore unknown types.
    reason: "<= 12 words: why the session is finished"
  }
}
```

### suggestedAction - mission complete (v2.4)

Many sessions exist for one short-lived purpose ("investigate X, file the bug"). When the
wingman judges the session's goal DELIVERED - the requested issue was filed and its URL
confirmed in the reply, the question answered, the artifact produced - and nothing blocks,
it sets `suggestedAction = { type: "close_session", reason }` and writes the needsYou
accordingly (urgency=fyi, railLine like "done - close session?").

Rules:
- Suggestion ONLY. Nothing ever auto-closes; the Cockpit renders a two-step approval
  button (BriefPane "MISSION COMPLETE" block) wired to the same close path as the rail
  kebab. The user's click IS the approval.
- The guard list is part of the prompt: never suggest while requested work is unfinished,
  changes the user wanted committed are uncommitted, an approval/question is pending, or
  confidence is low. When in doubt: null.
- Validation is mechanical (D5/D6): unknown type or empty reason drops the ACTION, never
  the brief. Reason capped at 100 chars.
- The suggestion is hidden the moment the session works or the wingman reads again -
  a stale suggestion must not outlive new activity.
- This lives at GATEWAY + COCKPIT only (the Director is dumb metal, #187); the desktop
  does not render it, by design.

### Example 1, briefed correctly

The turn from section 1, as the wingman should have read it (this output was produced by the
strong model with the same session access the wingman would have - it is the quality bar):

```json
{
  "intent": "Redesigning the Cockpit Brief page. You approved 3 layout fixes
             (block order, honest working-state, button gate); Claude built them.",
  "did": [
    "Reordered the page: YOU ASKED and NEEDS YOU now adjacent, CLAUDE DID below",
    "Working state now shows only your ask + a live terminal tail - no stale content",
    "Switched the tail to the parsed screen grid (byte stream was spinner noise)",
    "Added a choice-gate on quick-reply buttons",
    "Verified each on a test Director: order flip, live tail, working->decision flip"
  ],
  "needsYou": {
    "statement": "Nothing is broken or blocking. Claude finished all three fixes and
                  verified them live. It wants your verdict: look at the new layout
                  on 7471 (hard-refresh), then decide - ship it or change it.",
    "options": [ { "key": "Looks good - commit it" }, { "key": "Make changes: ..." } ],
    "evidence": "Live on 7471 now (hard-refresh). Uncommitted - /commit when you're
                 happy with it.",
    "confidence": "high",
    "railLine": "Review new Brief layout -> commit or change?"
  }
}
```

And the screen it produces:

```
+------------------------------------------------------------------------------------------+
| (*) cc-director - cockpit     NEEDS YOU: review layout - 4m    [Brief|Terminal]    < 1/4 > |
+------------------------------------------------------------------------------------------+
|  YOU'RE DOING                                                                             |
|  Redesigning the Cockpit Brief page. You approved 3 layout fixes - block order,           |
|  honest working state, button gate - and Claude went off to build them.                   |
|                                                                                           |
|  NEEDS YOU                                                              confidence: high  |
|  +-------------------------------------------------------------------------------------+ |
|  |  Nothing is broken or blocking. Claude finished all three fixes and verified         | |
|  |  them live. It wants your verdict:                                                   | |
|  |     look at the new layout  ->  https://<host>:7471  (hard-refresh)                  | |
|  |     then decide: ship it, or change it                                               | |
|  +-------------------------------------------------------------------------------------+ |
|  [ Looks good - commit it ]   [ Make changes... ]          Claude's words v (verbatim)   |
|  -----------------------------------------------------------------------------------     |
|  CLAUDE DID                                                            [full reply v]    |
|   * Reordered the page: YOU ASKED + NEEDS YOU adjacent, CLAUDE DID below                  |
|   * Working state: only your ask + a live terminal tail - nothing stale                   |
|   * Tail reads the parsed screen grid (the byte stream was spinner noise)                 |
|   * Gated the quick-reply buttons; verified all three live on a test Director             |
+------------------------------------------------------------------------------------------+
| > Reply...                                                              [Send]  [Queue]  |
+------------------------------------------------------------------------------------------+
```

What this fixes versus the shipped screenshot, point by point: the literal "yes" becomes
rolling intent; the status-line non-question becomes a decision statement with the original
line demoted to evidence; the bogus regex buttons become the wingman's two real options (note:
TWO, one of which is a type-something affordance - no fake choices); and the rail line tells
the user what the session wants before they click it.

## 5. Decisions

- **D1 - Eager, every turn, with a watch-cancel.** The brief generates at turn end. If the
  user replies within ~10s of turn end (they were watching the terminal), the in-flight brief
  is cancelled - no point briefing a decision already made.
- **D2 - One wingman call owns intent + did + needsYou.** The v1 gpt-4.1-mini condenser
  becomes the LABELED degrade tier (wingman disabled / no key): the page must say which tier
  produced it. The regex option parser is DELETED, not gated further.
- **D3 - BRIEFING (yellow) is a real session state.** The rail stops showing the raw detector
  enum; while yellow the Brief page shows "wingman is reading the turn" + the live screen
  tail (already built, honest, never stale). Red rows show railLine.
- **D4 - Charter holds.** Wingman reads everything, writes nothing to the PTY; actuation only
  through the existing request-driven chokepoint; strong model only. "Feed clarity feedback
  back to Claude Code" is OUT of scope - it changes nothing durable; the wingman's job is to
  interpret ambiguity honestly (confidence: ambiguous), not to nag the agent into clarity.
- **D5 - TurnBriefStore is durable and shared.** Rail, Brief page, phone FIFO, and voice all
  read the same stored briefs. TurnSummaryCache's role is absorbed over time (do not build
  new consumers on it).
- **D6 - LAW: no regex, no text-parsing, anywhere in an interpretation path.** Interpretation
  is done by the strongest model available, returning STRUCTURED schema-validated output.
  Mechanical rules are permitted only for MECHANICAL jobs with documented proof of
  sufficiency - e.g. substring-verifying that the wingman's `evidence` field really is
  verbatim (that is validation of the model, not interpretation of the agent). The burden of
  proof is on the rule. History: every parsing rule in Brief v1 (final-paragraph fallback,
  option regex, digit gate) produced confidently-wrong UI within hours of shipping, on the
  very session that wrote it.
- **D7 - The brief has a feedback loop.** A "this brief is wrong" action on the Brief page
  (and FIFO card) stores the report - the TurnBrief, the turn package, and the user's note -
  as a LABELED EXAMPLE. That corpus drives prompt iteration and the v2 example work
  (section 7). Modeled on the old session view's state-vote widget; this is how the wingman
  gets smarter against reality instead of guesses.

## 6. Build plan (phases)

1. **State machine + store.** BRIEFING state in the detector flow (Working -> turn end ->
   Briefing -> Red/Green), TurnBriefStore (durable, per session per turn), turn package
   assembly. No LLM yet - a stub brief proves the lifecycle end to end.
2. **The wingman call.** Structured opus call (WingmanService side-call pattern, JSON out,
   schema-validated), watch-cancel, evidence-verbatim validation (substring check stays - as
   validation of the wingman's evidence field, not as the headline source).
3. **Consumption.** Brief page renders TurnBrief (buttons = options), rail rows show railLine,
   yellow state UI. Delete the regex parser. Degrade tiers labeled: wingman -> mini condenser
   -> raw summary -> terminal.
4. **The other consumers.** Phone FIFO cards + voice read TurnBriefStore.

## 7. The example catalog - Claude Code's question shapes (captured 2026-06-04)

Five shapes were generated deliberately in scratch sessions and captured side-by-side: the
screen grid (what the user sees), the transcript widgets (what the JSONL knows at that
moment), Brief v1's output (what rules made of it), and the correct TurnBrief (authored by
the strong model - the quality bar). Full captures in `examples/capture-*.md`.

| Shape | Capture | Transcript sees it? | answerVia | What it taught the contract |
|---|---|---|---|---|
| AskUserQuestion picker | `capture-picker-askuserquestion.md` | NO - completely blind until answered | keys | Screen grid has everything (question, options, descriptions); answerable remotely by number+Enter through the raw-input path -> `options[].send` key sequences. The "go to the terminal" cop-out dies. |
| Permission prompt | `capture-permission-prompt.md` | NO - blind | keys | The brief MUST surface option SCOPE: "2. Yes, and don't ask again for: git *" is a standing grant, not a yes. Risk-awareness is interpretation -> `options[].note`. |
| Plan-mode approval | `capture-planmode-approval.md` | PARTIALLY - pending ExitPlanMode tool + the plan body are in the transcript; the menu options are screen-only | keys | The brief fuses both sources: plan summary from the transcript, options from the grid. v1's fallback grabbed a mid-reply paragraph - Exhibit B for D6. |
| Plain-text numbered choices | `capture-plaintext-numbered.md` | YES - fully | reply | The easy shape; v1 mostly held up. Lesson: proportionality (small turn -> short brief). Grid snapshots tear under active output - JSONL is truth when sighted. |
| Soft decision / "thoughts" | `capture-ambiguous-thoughts.md` | YES | reply | The missing SEVERITY tier: nothing blocking, but a real decision exists -> `urgency: blocking/review/fyi`; fyi does NOT turn the rail red. |
| Multi-select checklist ("pick any that apply") | `capture-multiselect-checklist.md` | NO - blind at 424 widgets (captured from a REAL work session, not staged) | keys | BROKE the v2 contract: toggling several options + a separate Submit cannot be one `send` -> `selectionMode: multiple` + `submit`. ALSO: the session was asking AND still working (spinner under the questionnaire) - blocking-question and active-work coexist; and the grid was torn while parked on the question (read-through-corruption robustness). |

**Contract changes frozen from these captures** (now in section 4): `needsYou.answerVia`
("reply" | "keys"), `options[].send` (what one tap transmits - reply text or key sequence),
`options[].note` (scope/risk flags), `urgency` ("blocking" | "review" | "fyi").

**Pipeline lessons (bake into phase 1/2):**
- The detector's WaitingForInput can fire BEFORE the JSONL flushes the turn (raced twice
  during capture). Brief generation must verify the transcript is settled or treat the
  screen as the source (ReplyPending machinery already detects this).
- BOOT GOTCHA: a prompt delivered seconds after session creation can land in the composer
  with its Enter swallowed - the session then sits WaitingForInput with the prompt
  UNSUBMITTED and the brief must not present it as a completed turn.
- Grid snapshots are not atomic during output; capture after settle.

**Still wanted:** a genuinely AMBIGUOUS turn (even the strong model must say "unclear,
likely X or Y"). It resists being staged on demand; the D7 feedback loop ("this brief is
wrong") is the realistic collector. Until one exists, `confidence: "ambiguous"` rendering is
designed but unvalidated.

## 8. Open questions

- Cost envelope: opus on every turn of every session - measure $/day on the real fleet before
  phase 2 ships; the watch-cancel may matter more than expected.
- Does BRIEFING (yellow) replace or coexist with the detector's existing yellow ("quiet but
  not confirmed")? Decide in phase 1.
- Should the rolling intent be user-editable (pin/correct the goal from the Brief page)?

## Document history

| Date | Author | Change |
|---|---|---|
| 2026-06-04 | claude (cc-director session) | v1: problem, position, lifecycle, TurnBrief contract, Example 1 (before screenshots + corrected brief), build plan, v2 caveat on interactive questionnaires. |
| 2026-06-04 | claude (cc-director session) | v1.1: Mission section (the meat computer is the bottleneck; helpers exist to reduce cognitive load); prior-art section on the Director session view (take: distilled-question pattern, LLM quick replies, UserPromptSubmit hook, feedback widget; not the code); D6 no-regex/no-text-parsing law; D7 brief feedback loop; turn package + contract amendments. |
| 2026-06-04 | claude (cc-director session) | v2: example catalog - five question shapes captured live (picker, permission prompt, plan approval, plain-text numbered, soft decision) with side-by-side grid/transcript/v1-output/correct-TurnBrief in examples/. Contract frozen: answerVia reply-or-keys (interactive menus answerable remotely via send key sequences), options[].send + options[].note, urgency blocking/review/fyi. Pipeline lessons: transcript-flush race, unsubmitted-composer boot gotcha, grid tearing. |
| 2026-06-04 | claude (cc-director session) | v2.1: sixth shape captured from a LIVE real work session - the multi-select checklist ("pick any that apply"). It broke the v2 contract: added selectionMode single/multiple + submit send. New findings: a session can be blocking-on-a-question AND actively working simultaneously; grids tear even while parked on a question; transcript-blind holds at 424 widgets. |
| 2026-06-05 | claude (cc-director session) | v2.2: additive contract extension for the Cockpit session story panel - `headline` (the session's newspaper headline: re-judged every turn, kept verbatim unless the story materially changed, "since turn N" computed by walking stored briefs) and `turnTitle` (<= 8 words, the turn-card header). Old briefs without the fields render via fallback (headline -> intent, turnTitle -> first did bullet). Companion (outside this contract): GET /sessions/{sid}/usage - per-session token totals + per-turn deltas computed mechanically from the transcript JSONL's usage blocks. |
| 2026-06-05 | claude (cc-director session) | v2.3: the CHAPTER model (Soren's design: reduce cognitive load - the story column must scan in seconds). `headline` re-defined as the current chapter's title: WHAT the session is working on, never how; the wingman may refine its wording as the same work drifts. New `newChapter` bool: the EXPLICIT chapter boundary, wingman judgment - never string comparison (several turns share a chapter; the same title may recur as a later chapter; first stored title mechanically starts chapter 1; degrade tiers never set it). Cockpit renders CHAPTERS as collapsed title-only cards (expand for turn rows, expand a row for the full turn description); pre-v2.3 briefs group by title-change fallback. Headline tightened to <= 6 words / 60 chars. |

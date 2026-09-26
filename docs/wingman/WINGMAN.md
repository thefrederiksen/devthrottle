# The Wingman

This is the single source of truth for the Wingman: what it is, the invariants it must
hold, and how it is actually built today (as-shipped). If you change Wingman behavior, read
this first and keep it accurate.

---

## AMENDED 26 SEPTEMBER 2026 - CONTRACT v4, CALL A IS CODE FIRST AND ONE WORD

**Read this before anything below it, including the v3 amendment. Where they disagree, this is what shipped.**
The turn pipeline mission (issue #3399), design v2, approved by the owner on 26 September 2026.

**Call A decides one thing: does this stop need its owner.** Four code steps run first, in order, and the
first to fire decides with no model call (`CallACodeSteps`, in Core):

1. `picker` - a picker or permission prompt is drawn on the screen (`PickerOnScreen`, the one footer rule) -> needs-you
2. `agent-verdict` - the agent's last message carries its own CC-DISMISS block saying needs-human -> needs-you
3. `question` - the agent's latest reply asks the person a real question (not a heading, list item, quoted
   question, or one the same line answers) -> needs-you
4. `way-back` - the agent set itself a way back in its last turn: a ScheduleWakeup, a Monitor, a session spawn,
   or a background run -> carrying-on (never on a failure with no reply)

A stop none of them decides goes to the model (`turn-verdict-v4.txt`): every visible screen row, the cursor row,
the full-screen flag and the agent's latest reply, and nothing else - no conversation, no recent turns, no first
ask, no previous label, no owned-sessions line. It is asked at temperature 0, with reasoning off and output capped
at sixteen tokens, on the same fast model, and must answer exactly `needs-you`, `done` or `carrying-on`.
Anything else, a timeout or an error is a failed record: red, on the unchanged retry schedule. An answer that is
not one of the words is not re-attempted for a listener, because at temperature 0 it would repeat.

| word | colour | stored verdict |
|---|---|---|
| needs-you | red | needed-you |
| done | cyan | finished (kind done) |
| carrying-on | purple, carrying-on clock unchanged | continues-alone |

Every record says which step decided it and why (`DecidedBy`, `DecisionReason`), and so does the debug view.

**Gone from Call A:** the label, what the agent recommends, the menu and the options, and `InventedMenuCheck`,
which had nothing left to correct. A record stored under v3 keeps its fields and still renders.

**Call B writes the label and the narration, and nothing else (phase 4).** `NarrationCall.BuildPrompt` is given
the reply, the recent turns, the screen, the account's narration rules and language, Call A's word, and - when a
code step decided - that step's reason. It is no longer given a menu, options, a recommendation or a "keys or
reply" line. It answers, between the usual markers:

```
LABEL: <at most ten words - what the row shows>
NARRATION:
<the spoken version>
```

`NarrationCall.ParseAnswer` reads the two parts mechanically. Any other shape - no label line, an empty or
eleven-word label, no narration line, no words - is a failed Call B: Call A's colour stands, the record has no
label (the row shows its plain state label) and no words, and it carries `NarrationFailureReason`. Nothing is
stored until both calls are done, so colour, label and words appear together. A person asking again after a
failed Call B saves the label with the words.

**A menu is "open the session to choose".** On a stop Call A's picker step decided (or a v3 record answered with
keys), the code-owned closing sentence tells the narration to say what is being asked and to tell the person to
open the session to choose. No prompt tells anyone to press a button: there is none. The Cockpit's answer buttons
(the verdict panel's options and the Now card's options, with the "It recommends" line and the confirm-before-send
warning) are gone, and so is `agentRecommends` everywhere it was produced or shown. The answer route itself stays,
with its screen check, because the Fleet Manager walkthrough still answers through it.

**The send-time menu cache is no longer fed by a reading.** Call A does not say whether a menu is drawn, so the
voice-reply guard (`WaitingScreenReader.ConfirmedMenuAsync`) asks its own question on a menu-shaped screen and
fails closed, as it always did on a cache miss.

**Not in the steps, on purpose:** the phrase list ("your call", "waiting on you") until it is checked against the
owner's own labels, and "owns sessions still working", which the measurement showed points the other way.

---

## AMENDED 18 SEPTEMBER 2026 - CONTRACT v3, THE FIVE-FIELD READING

**Read this before anything below it. Where the two disagree, this is what shipped.**

The owner's ruling on the Wingman redesign report cut the contract from twelve fields to five,
and made a reading ATOMIC. Everything below describes the v2 contract, and is kept because it
records why each rule existed and what it cost - but seven of the fields it describes are gone.

**The five fields a reading now has.** `state`, `label`, `narration`, `agentRecommends`, and
`menu` with its `options`.

- `state` is what happened, in one word, and it is the old verdict word with `finishedKind`
  folded into it: `needs-you`, `finished-done`, `finished-report`, `carrying-on`,
  `stuck-recoverable`, `stuck-needs-person`, `cannot-tell`. The STORED column keeps the old six
  words, because every record ever written carries one and the labelling corpus is graded on
  them; `TurnVerdictStates` is the one place the two spellings meet.
- `narration` is the body, and it is the SAME TEXT read and heard. `Summary` and `Spoken` on the
  record hold that same text from v3, so a screen never shows a short version and a long version
  of one turn.
- `agentRecommends` is a READING, never a quote, and nothing checks it against the reply.

**Gone: `spoken`, `summary`, `evidence`, `risk`, `confidence`, `answerVia`, `finishedKind`.**
Measured on the live fleet that morning, 40 per cent of readings failed - 48 of 198 timed out at
sixty seconds and 30 were refused by a content rule, most of them the verbatim receipt. Each cut
field was another way for a fast model to fail a shape check on a call every session pays for at
every stop, and four of them reached no screen at all.

- The RECEIPT is gone and nothing replaces it. It could not be satisfied - the reply carries
  Markdown and the check compares word for word - so it refused about one good reading in seven.
- `risk` now lives on `options[].note` and only there, at the point of decision. The Now screen's
  confirm-before-sending was raised by the cut word, so on a reading made since v3 there is no
  confirm; the consequence is in the note beside the button.
- `confidence` is gone, and NOTHING MAY REQUIRE IT OF A READING MADE SINCE v3. The calm arm in
  `SessionOrdering` and the Fleet Manager's event gate both asked for `high`; requiring that of
  every reading would have kept every row red for ever. `cannot-tell` is now the word that says
  "I could not judge this".
  **But a record that CARRIES a confidence word is still read under it**, in both those gates.
  Every session's newest reading on the morning of the deploy was written under the old contract,
  so deleting the check outright would have recoloured the live roster in the instant the Gateway
  swapped - a session sitting red on an "ambiguous" reading would have gone calm without anything
  about it changing. A stored reading never changes colour because the code around it changed.
- `answerVia` is DERIVED: a menu on the record means keys, no menu means a reply.

**The judge is no longer asked for anything a person hears, so it is no longer a spoken path.**
`TurnVerdictPrompt` used to splice the account's own narration instructions into the judge prompt
between two headings and add the language rule for `spoken`. The v3 template has neither heading,
so the splice THREW - an account that had typed its own narration instructions could not be judged
at all. Both were removed rather than re-aimed: the narration call takes the language and the
account's instructions itself, and `TurnVerdictPrompt.BuildVerdictPrompt` moved from
`SpokenPaths.SpokenFieldPaths` to `SpokenPaths.NotSpokenOutput`. The fields the judge still answers
were English before the change and are English after it.

**A cut field is ABSENT from the Fleet Manager's prompt, never written as empty.** The event prompt
emits `risk:`, `summary:` and the receipt markers only when the record carries them, so a v3 reading
shows a `state:` line and no empty ones. An empty pair of receipt markers says a quote was taken and
that it was blank, which the reader has no way to see through. A session another live session owns
gets no narration call, so it has no summary at all and its `label:` is the whole of what the reading
says about it - which is thinner than v2 gave, and is what the five-field contract provides.

**A READING IS NOT FINISHED UNTIL BOTH MODEL CALLS ARE DONE.** Nothing is stored, shown or spoken
until the whole reading exists. The narration call is made inside the judgement, before the
record is stored, so there are exactly three states - being read, ready, could not be read - and
no half-ready reading and no first draft replaced seconds later.

**The debug view**, staff only, shows for each stop what was fed in, the exact prompt and the raw
answer, FOR BOTH CALLS: `GET /sessions/{sid}/wingman-debug`, drawn as the third view on the
Wingman tab beside Now and History. Staff is a list of account addresses in
`DEVTHROTTLE_STAFF_EMAILS` (`StaffAccess`), empty by default, and it widens nothing but this view.

---

## 1. What the Wingman is

The Wingman is the user's second set of eyes on every Claude Code / AI session card in CC
Director. It rides alongside each session and answers, for the user, "what is this session
doing, does it need me, and what did it actually say?" - without making the user read the
raw terminal. It is a cross-cutting component used in many places and growing.

The bar for every Wingman feature: **does it actually help, or does it send the user back
to the raw tab?** A lossy or wrong answer the user can't trust is worse than nothing.

---

## 2. Hard invariants (enforced)

These hold for every Wingman path. The audit gate
(`CcDirector.Core.UnitTests/Wingman/WingmanCharterAuditTests.cs`, in the default local gate) fails the build if any file
under `src/CcDirector.Core/Wingman/` violates the mechanical ones.

1. **The judge that scored best on the labelled corpus - and the score and the reply time
   are recorded here.** Which model a Wingman path runs on is settled by MEASUREMENT against
   a corpus of labelled turns, not by the belief that a bigger model reads a screen better.
   That belief was this invariant's old wording ("strong model only"), and the measurement
   contradicted it: the thinking tier never answered 48 of its 100 calls and its ninety-fifth
   percentile reply was 273.0 seconds, so on a boundary that has to answer while a row is
   yellow it is not a better judge, it is no judge at all. The models in force, which the
   audit gate reads out of this document:

   - Director-side content features (sections 4 and 5): `WingmanService.Model` = `opus`.
     `DefaultModel` and `StrongModel` are back-compat aliases of `Model`.
   - The Gateway turn verdict (section 3b): the judge = `devthrottle/wingman-fast`.

   There is still no cheap tier and no Haiku: the audit gate fails on a quoted cheap-model
   literal anywhere in Wingman source, and separately fails when `WingmanService.Model` is
   any value this document does not name. Turn-**state detection** (section 3) uses no model
   at all.

   **The score, measured on 2026-09-15** (`devthrottle/wingman-fast`, 341 turns of a
   350-label corpus, eight calls in flight, against the product's real package, scored under
   contract version one; the report is `docs/design/wingman-on-every-turn/grading-2026-09-15.md`
   in the internal repository):

   | | `devthrottle/wingman-fast` | `devthrottle/wingman` (the runner-up) |
   |---|---|---|
   | Calls that never answered | 0 of 341 | 48 of 100 |
   | Reply seconds: fiftieth, ninetieth, ninety-fifth percentile | 9.8, 15.4, 20.2 | 144.5, 238.5, 273.0 |
   | Slowest single answer, seconds | 34.1 | 288.4 |
   | Answers accepted by the contract | 222 of 341 (65.1%) | 18 of 100 (18.0%) |
   | Agreement with the label | 70.7% | 100.0%, over 18 accepted answers |
   | False calm (a calm verdict on a stop that needed a person) | 10 of 154 (6.5%) | 0 of 17 (0.0%) |
   | - of which `finished`, cyan, with no clock behind it | 2 of 154 (1.3%) | 0 of 17 (0.0%) |
   | - of which `continues-alone`, purple, which the clock turns red again | 8 of 154 (5.2%) | 0 of 17 (0.0%) |
   | Receipt failures (the answer is refused and the row stays red) | 68 of 341 | 0 |

   **The judge timeout is 30 seconds**, set from that ninety-fifth percentile by a rule fixed
   before the measurement: 20 stands unless the chosen judge's ninety-fifth is over 20, in
   which case it becomes 30, because a timed-out stop stays red and headroom costs yellow time
   rather than safety. The slowest answers are still lost, deliberately.

   **The two judges were measured at the same eight calls in flight, and their columns still
   are not like-for-like on quality**: the runner-up's rates rest on 18 accepted answers and 17
   turns, which measures how rarely it answers rather than how well it judges. Read its
   never-answered row, not its rate columns. And the corpus labels are agreement between two
   reviewers of different model families, not truth; the owner's own correction (section 3b) is
   the only label that outranks them, and every number above inherits that.

   **What the re-score says, and the one row that is NOT MEASURED.** Contract version two
   (slice D) requires a `finished` answer to say which kind of finished it is. The stored
   answers were re-scored under it without calling any model: acceptance falls to 185 of 341
   (54.3%), agreement to 68.1%, false calm to 8 of 154 (5.2%). The `finished` row then reads
   zero - and **the honest reading of it is NOT MEASURED, not zero**. Every `finished` answer
   in that run was asked under contract version one, which never asked for the kind, so version
   two refuses them all: there is no accepted answer of that kind for a mistake to be counted
   among, the numerator cannot rise however the judge behaves, and a rate over no accepted
   answers is not a rate. Printed as 0.0 percent it would read as a perfect score on the row
   that gates the colour switch. **The cyan false-calm rate becomes a number again on the first
   grading run whose `finished` answers were asked under contract version two, and until then
   the number this document stands behind is the 1.3 percent measured under version one.**
2. **The Wingman's LLM calls are read-only.** No Wingman LLM call may write to a session.
   Any full-power Wingman session gets a read-only tool allow-list (`Read Grep Glob`) only,
   and the side-calls are tool-less (`--tools ""`). (Audited: no non-read-only `allowedTools`.)
   Actuation does NOT change this: when the Wingman acts on a terminal it does so via the
   structured-intent path (section 7), where the **Director** writes - the model only ever
   returns a proposed action and is never handed a write tool.
7. **One write chokepoint.** All Wingman actuation goes through `WingmanActionExecutor`; it is
   the only file under `src/CcDirector.Core/Wingman/` allowed to call a Session write method,
   and every actuation it performs is logged. (Audited.)
8. **Actuation is request-driven - the Wingman never acts on its own, with ONE approved
   exception.** Every action normally originates from an explicit `POST /sessions/{sid}/wingman/act`.
   There is no turn-completion hook, timer, or background loop that invokes actuation; the Wingman
   does not wake up and "figure out what to do" after a turn. The single sanctioned self-actuator
   is **transient-error auto-resume** (`TransientErrorAutoResume`, issue #476, section 5c): a
   Director-driven loop that nudges a session stalled on a *transient* Anthropic API error to
   continue. It is the only background actuator allowed because (a) it is gated behind a setting
   that DEFAULTS OFF - opt-in, the human decision on assumption A-3 - so the Director does nothing
   without explicit user consent, (b) it still writes only through `WingmanActionExecutor`
   (invariant 7 intact), and (c) it acts only on a narrow, content-matched transient signature and
   never on a terminal error. (Audited: nothing under `src/CcDirector.Core/Wingman/` calls
   `WingmanActionExecutor.Execute` EXCEPT the allow-listed `TransientErrorAutoResume.cs`.)
3. **Faithful, not summarizing, when content is asked for.** Status outputs (badge, terse
   briefing) may be short, but when the user asks to *read* content ("read me the article")
   the Wingman reproduces it verbatim, complete, no length cap.
4. **Stateless side-calls.** Each Wingman LLM call is a fresh `claude --print`
   (`--no-session-persistence`, MCP off). No hidden conversation memory between calls.
5. **Fail closed - never fabricate.** On any failure or ambiguity, return an explicit
   `unknown`/error result. Never invent a state, file, decision, or content.
6. **All Wingman code lives under `src/CcDirector.Core/Wingman/`** so the audit covers it.

---

## 3. Turn-state detection (the badge): one timer, blue or red

This is how the colored status badge is decided. It is **one mechanical rule with no LLM,
no regex, and no screen parsing** - deliberately the simplest thing that can possibly work.

### The detector: `TerminalStateDetector`
A per-session watcher on the terminal byte stream with exactly two rules:

1. **A byte out of the ConPTY means the agent is producing output -> `Working`.** We set
   Working the instant a byte arrives and re-arm an idle countdown on every byte. We do not
   inspect what the byte is.
2. **Complete silence for `QuietThreshold` (10s) -> `WaitingForInput`.** When the stream has
   produced nothing for the threshold, the session "needs you". That is the entire decision.

The detector treats a long silence as "needs you" regardless of *why* output stopped - the
agent may have finished cleanly, be blocked on a question, or just be thinking slowly. It
does not try to tell those apart. The only derived signal it relies on is "time since the
last byte", which the session's `CircularTerminalBuffer.LastWriteAtUtc` already tracks.

**One exception: Director-induced repaints are not agent output.** When the Director issues a
PTY resize - on attaching/switching to a session, force-refresh, or a layout change
(`TerminalControl.ResizeSession`) - Claude Code repaints its whole screen and emits a burst of
bytes. Those bytes are *our* doing, not the agent working, so without a guard the detector
would flip an idle (red) session to blue the instant you switch to it. Before each resize the
control calls `Session.SuppressActivityFor` (a ~1.5s window); the detector early-returns on any
byte while `now < Session.SuppressActivityUntilUtc`, counting neither Working nor an idle re-arm.
The window is far under the 10s quiet threshold, so a genuine work-start landing inside it is at
most delayed until the next byte after the window. The detector stays content-blind - it never
inspects the bytes, only whether the Director just caused them.

### The colour: `SessionStatusWingman` (the single writer)
`SessionStatusWingman` is the sole writer of `Session.StatusColor`. It is a direct,
mechanical map from `ActivityState` to a colour - there is **no other colour algorithm
anywhere**:

| ActivityState                         | Colour            | Reason       |
|---------------------------------------|-------------------|--------------|
| `Working`, `Starting`                 | blue              | "working"    |
| `WaitingForInput`, `WaitingForPerm`, `Idle` | red         | "needs you"  |
| `Exited`                              | gray (`unknown`)  | "exited"     |

Because the detector only ever emits `Working` and `WaitingForInput`, in practice the badge
is just **blue (working)** or **red (needs you)**.

### Toggle
- `CC_DIRECTOR_TERMINAL_STATE=0` - use the Claude-Code hook path to drive `ActivityState`
  instead of the terminal timer (off by default; see section 7 on why hooks are not used).
  The colour mapping above is unchanged either way.

---

## 3b. The turn verdict (the Gateway): what a stop MEANS

Section 3 decides that a session has STOPPED. This section decides what the stop MEANS. The
two are different jobs on different machines and they are never merged: the detector is a
ten-second timer on the Director with no model in it, and the verdict is one model call on the
Gateway at the turn-end boundary the detector already found.

The settled specification, with worked examples, is
[`docs/architecture/wingman/TURN_VERDICT.md`](../architecture/wingman/TURN_VERDICT.md). This
section is the charter's summary of it and the invariants it must hold.

### Why it exists

The badge says "needs you" whenever a session has been silent for ten seconds. Measured on the
owner's fleet on 2026-09-14: 801 red flips, 248 turns he actually drove, and of 131 labelled
turn ends 57 percent were "finished", 24 percent "needed a person", 15 percent "carrying on by
itself". Three reds in four did not need him. The verdict exists to tell those apart.

### The one question, and the contract

At every stop of a session the account owns, the Gateway asks ONE model ONE structured question
and stores the answer on the session row with the agent's own words as the receipt. One model
call per stop, terminal-failure stops included; the spoken version for the ear is a SECTION of
that same answer, never a second call.

The answer is one JSON object. The field names are the wire names:

```
{
  "verdict":         "needed-you" | "finished" | "continues-alone" | "stuck-recoverable" | "stuck-needs-person" | "cannot-tell",
  "confidence":      "high" | "ambiguous",
  "evidence":        "<the agent's decisive sentence, copied character for character>",
  "label":           "<= 10 words: the ask, or the report",
  "summary":         "1-2 sentences for a reader who has not looked at this session for hours",
  "agentRecommends": null | "<the agent's own recommendation>",
  "answerVia":       "reply" | "keys",
  "menu":            null | { "question": "...", "selectionMode": "single" | "multiple", "submit": "" | carriage return },
  "options":         [ { "key": "...", "send": "...", "recommended": true|false, "note": "<= 18 words" } ],
  "risk":            "none" | "irreversible" | "standing-grant" | "spends-money",
  "spoken":          "<the same content for the ear, about 30 seconds, opening with the session title>",
  "finishedKind":    "done" | "report"        (only when the verdict is "finished")
}
```

Validation is MECHANICAL and never interpretation (`TurnVerdictContract.ParseAndValidate`):
closed word lists, the declared shape of every member proved before any member is read for
meaning, length caps, and a receipt check that looks for `evidence` verbatim in the latest reply
or in the screen rows after whitespace normalisation.

`verdict`, `confidence`, `evidence`, `label`, `summary`, `answerVia`, `risk` and `spoken` must be
present and be strings; `options` must be present and be an array, empty when there is nothing to
offer. **`agentRecommends` and `menu` are the only members that may be null or absent**, and even
there a wrong TYPE rejects the answer rather than reading as nothing. `finishedKind` is required
on a `finished` verdict and refused on every other. An unknown `verdict` word, an unknown `risk`
word, a member that is missing or of the wrong kind, an option whose `send` carries a carriage
return, a receipt that differs by one word - each rejects the WHOLE answer. Nothing missing is
ever synthesised.

The full rule, member by member, is in the specification. The vocabulary lives in one file,
`TurnVerdictVocabulary`, and its twin in the labelling tool of the internal repository is pinned
to it by a test in each repository.

### The order of the checks, and what each one costs

This is the boundary exactly as `TurnVerdictService.JudgeAsync` runs it. The same block, word for
word, is the comment above that code and section 6 of the specification, so the three cannot
describe it differently:

```
THE BOUNDARY, IN THE ORDER THE CODE RUNS IT. A read means the session's screen or its stored
conversation; the pushed roster and the verdict store are consulted as well and are not counted.
1. The checks that read neither: held, live, not brand new, not exited, not working; then the
   judge switch, checked ONCE in the flight, before the settle wait. Then, for a turn end, the
   settle wait. Then, for an automatic request, the held, live, brand-new, exited and working
   checks again - but NOT the switch, so a switch turned off during a flight does not stop that
   flight. A stop refused here costs NO reads.
2. The screen read, and its one full-grid hash.
3. The reuse check: a stored verdict formed on the same hash is reused and the judge is not
   asked. A stop answered here costs ONE read. The one exception is a rate limit's named wait:
   while one is held for the session, this check reads the stored conversation as well, to tell
   whether the reply is still the stop the wait was named for, and a stop answered by that wait
   has read both.
4. The conversation read.
5. The account ceiling. A stop refused here costs TWO reads.
6. The model call.
```

**Two steps left this boundary on 19 September 2026**, with the mission "Wingman error and retry". The
speech re-attempt refusal and the provider deadline both existed for the voice path's own retry ledger - a
ladder of booked re-attempts beside the judgement. That ledger was replaced by one retry schedule written on
the stored reading itself (`TurnVerdictDto.RetriesMade`, `NextRetryAtUtc`) and carried by the idle sweep
(`TurnVerdictService.StartDueRetries`), so nothing reaches this boundary that may not ask the judge, and a
provider's named wait is honoured by booking the retry later rather than by a check here. A failed reading is
asked again ONLY by that booked retry, under the `Retry` trigger, and only once its booked time has passed;
every other automatic trigger still reuses a failed record, so an unchanged screen is never paid for twice
outside the schedule.

**Three earlier versions of this section were wrong.** The first said nothing was read until every
check passed. The second grouped a step that has since been removed (the speech re-attempt refusal) with the checks
after BOTH reads, when it came before the conversation was read and cost one. The third said the judge switch was checked
again after the settle wait; it is checked once, before it, so a switch turned off during a flight
does not stop that flight. The code was right all three times.

**The real boundary is step 5.** A stop refused there has cost two reads and no model call, and that
is deliberate: the expensive, rate-limited, chargeable thing is the model, and step 5 is what stands in
front of it.

Notes on individual steps, which add reasons and do not change the order or the costs above:

- **Held** is resolved across the account's WHOLE fresh roster in one snapshot, never off the
  session's own row. The push store nulls the role at ingest, so a check that read the row would
  answer "not held" for every session on the fleet. A held session gets NO automatic model call of
  any kind - no verdict, no narration, no speech - whoever holds it, the account's Fleet Manager
  included (owner ruling, 2026-09-25, which reversed the 2026-09-16 exemption that judged a Fleet
  Manager's sessions). The Fleet Manager is still told of the stop, with no reading, by the Gateway's
  Fleet Manager events, and reads the session itself. A person pressing Explain can still narrate any
  held session, as before.
- **The judge switch** binds only the two triggers nobody is waiting on: the detector's turn end, and
  a snooze expiry with a stop nothing has judged. A voice session is judged whatever the switch says,
  because somebody is listening to it, and a person's own request is not automatic at all.
- **The held, live, brand-new, exited and working checks run again after the settle wait; the judge
  switch does not.** A session can become held, or start working, while the request waits, and the
  first answer does not license a read made later. The switch is taken from the account settings read
  when the flight starts and is not looked at again, so turning it off stops the NEXT flight, not one
  already waiting or reading.
- **The screen is read before the reuse check because the reuse check needs it**: it compares the hash
  of this screen to the hash a stored verdict was formed on. A screen read placed after step 5 would
  buy nothing and would cost every reusable stop a model call. An unreadable screen hashes to the empty
  string and is never reused outside the idle sweep, because otherwise two different stops on an
  unreachable Director would look like one screen.
- **The account ceiling** is eight judgements in flight. A stop over it is not judged and stays exactly
  as the detector left it, because the alternative is a queue whose answers arrive about screens that
  have moved on.

### Which way it errs

**Toward the owner, at every single gate.** A calm verdict takes a session out of his queue, so
a wrong calm verdict is the one that goes unnoticed - the session sits there and nobody comes.
So:

- **A rejected answer stores a `failed` record with its reason and leaves the row exactly as the
  detector left it, which is red. Silence is never a decision.** A timeout, a rate limit, an
  unusable provider and a malformed answer all land in the same place: red, and a stored record
  saying why.
- A row is calmed only when it is raw red AND the account's colour switch is on AND the state is
  `judged` AND the confidence is `high` AND the word is `finished` or `continues-alone`. Every
  gate that cannot be answered answers "not calm".
- `confidence: ambiguous` is accepted, stored and graded, and never demotes a red row.
- `cannot-tell` is a real answer and not a failure to answer, and it is not calm.
- A terminal-failure package - no reply, a failure on screen - has `finished` and
  `continues-alone` refused outright by validation. A failure with no reply cannot be calm.

The two switches are per account and BOTH DEFAULT OFF: `JudgeEnabled` (are this account's stops
judged at all) and `ColourEnabled` (do the verdicts reach the screen). With judging on and colour
off - the shadow state - verdicts are stored and gradeable and every row stays exactly the colour
the detector made it.

### What the verdict does to a row

| Verdict | Colour | Label | In the "needs you" count? |
|---|---|---|---|
| `finished`, kind `done` | cyan | leads "Done" | No. Listed in the calm band below the reds |
| `finished`, kind `report` | cyan | leads "Report" | No. Same band |
| `continues-alone` | purple | the Wingman's line, or "Carrying on" | No. Same band, and a clock is running |
| `needed-you`, `stuck-needs-person`, `stuck-recoverable`, `cannot-tell` | red, unchanged | the ask, in the Wingman's words | Yes |
| refused, timed out, rate limited, or never judged | red, unchanged | as today | Yes |

Purple carries a clock (`TurnVerdictWatchdog`): the agent's announced next wake-up plus two
minutes when it announced one, otherwise ten minutes. On expiry the row goes red with the label
"Said it would continue and did not". A session whose own owned sessions are still working is
carrying on whatever its reply says, and the clock does not run while any of them works.

A snooze that ends with no new turn end comes back CYAN with "Snooze ended, nothing new" rather
than red - nothing happened, so there is nothing to bring him. If a stop DID happen while the
snooze ran, that stop's verdict rules, and only a needs-you verdict brings the row back red.

### What it may NOT decide

- **It judges what a stop MEANS. It never decides whether a stop HAPPENED** - that is the
  detector's job, in section 3, and the verdict is only ever asked about stops the detector has
  already decided happened. This is why `not-a-turn-end` is in the shared vocabulary and is a
  word the Wingman may never answer with: it is a fact about the detector, not a state of the
  session, and an answer carrying it is rejected like any other unknown word.
- **It never types, never snoozes, never closes, and never touches a working session or a held
  one.** The held check is the FIRST check of all and runs before the screen is read, for every
  automatic trigger and whoever holds the session.
- **It is never a second colour authority.** One verdict, folded once on the Gateway, rendered
  verbatim by every client (the repository's law 7). A client never re-derives a colour, a label
  or a bucket.

### Answering from the panel is THE OWNER TYPING, not the Wingman

A verdict may carry options, and the Cockpit and the phone render them as buttons. Pressing one
is the owner typing - it goes through ONE server-owned activation route,
`POST /sessions/{sid}/turn-verdict/answer`, which binds the account, the session, the verdict
id, the screen version and the allowed bytes; re-reads the live screen; and REFUSES on a
mismatch with "The screen has changed since the Wingman read it, so nothing was sent." A verdict
can be answered once: a second attempt is refused with "That stop has already been answered".
The write itself goes through the existing prompt send under one screen lock, and every
activation - including every refusal - leaves a line in the activity ledger.

**The Wingman itself never presses a button.** Invariant 8 is unchanged by any of this: the one
sanctioned self-actuator is still transient-error auto-resume (section 5c). A verdict's options
are a shorter route for the owner's own hand, not a licence for the Wingman to act.

### When the verdict is wrong

The panel carries "This is wrong". It records a correction against the verdict - one word from
the shared vocabulary and an optional note - through
`POST /sessions/{sid}/turn-verdict/feedback`, and the labelling tool in the internal repository
pulls those corrections into the labelled corpus, where an owner label outranks a two-family
reviewer label for the same turn. That is the loop by which the score in invariant 1 is meant to
be re-measured.

Answering a verdict SUPERSEDES it rather than deleting it: the record stays findable, stamped
with the moment it was superseded, so a correction can still be made about a stop that has
already been answered.

---

## 4. Ask the Wingman (faithful, full-access answers)

A voice/REST channel separate from talking to the agent: the user asks the Wingman a
question and it answers faithfully from the session, reading content verbatim.

- **`WingmanService.AnswerViaSessionAsync`** - a read-only full-power session (`Read Grep
  Glob`, strong model) handed the whole terminal + repo. It reads as much as it needs and
  reproduces content verbatim; **no length cap**. Used for free-text questions.
- **Endpoint** `POST /sessions/{sid}/wingman/ask`: a free-text `Question` -> the faithful
  answer path; `Mode=explain` -> a terse "what's happening" briefing
  (`AskAboutSessionAsync`, explain mode).
- **"Hey wingman" routing**: `CleanVoiceTranscriptAsync` (run on each dictated utterance)
  cleans the transcript AND returns a `Target` (`agent` | `wingman`), detecting the wake
  phrase by LLM intent (not regex) and stripping it. The phone routes on that target;
  there is also an explicit "Ask Wingman" button.

---

## 5. Other Wingman responsibilities (all strong-model side-calls)

These are content features and have nothing to do with the badge colour (section 3 owns
that). They each read this session's own terminal transcript.

- **Per-turn summary** - `SummarizeTurnAsync` (Agent View headline + structured turn). It is
  cached and persisted for the Agent view, voice, and goals. It does **not** vote on the
  badge colour.
- **Explain briefing** - `AskAboutSessionAsync(explain)` (terse "what's happening").
  Returns a single JSON object with `headline` + `what_happened` + `what_claude_wants` +
  `say` + `actions`. The `say` field is the spoken version (no markdown, ~30s of speech)
  used by the phone's voice mode when the user opens a session; we do **not** pre-render
  TTS audio at turn-end. State is NOT decided here -- `WhatClaudeWantsDirective` binds
  the briefing to the badge colour owned by `SessionStatusWingman`, and the legacy
  `Answer` text is synthesised from the show fields so older clients keep working.
- **Rules / memory enforcement** - `CheckRulesAsync` (CLAUDE.md violations).
- **Goal tracking** - `AssessGoalAsync` (on-track / drifting / complete).
- **Git awareness / crash recovery** - `GitSnapshotAsync`, `BuildRecoveryPromptAsync` (no LLM).

---

## 5b. Actuation (structured-intent): the Wingman acts, the Director writes

The Wingman can act on a session's input prompt - type, press named keys, or submit a line -
without ever being handed a write tool. The split is the whole point:

1. **Decide (LLM, tool-less, read-only).** `WingmanService.DecideSessionActionAsync` runs a
   strong-model `claude --print --tools ""` side-call over the session's live screen + cursor
   (`Session.SnapshotScreenRowsWithCursor`), pending question, and state. It returns a
   `WingmanAction` JSON: `none | type | send_keys | submit`. The model proposes; it cannot
   touch the terminal. Fail-closed: anything ambiguous, low-confidence, or unparseable becomes
   `none`. The model is told to choose `none` for any decision the user owns.
2. **Execute (trusted C#).** `WingmanActionExecutor.Execute` is the **only** code under
   `src/CcDirector.Core/Wingman/` allowed to write to a session. It maps the validated action
   to bytes (`KeyChords` for named keys) and writes through the same `Session.SendInput` path a
   human keystroke uses - so Claude Code's UX is unchanged (terminal stays sacred).

Endpoint: `POST /sessions/{sid}/wingman/act` (decide + execute). `?decideOnly=true` returns the
decision without executing it - a dry run for tests and tooling.

Request-driven and always-on: the Wingman never triggers itself - it acts only when a user
request hits the endpoint (invariant 8). There is no per-session enable flag and no
confirm-first gate, so when it IS asked, it just acts. The safeguards that remain are not
permission gates, they are correctness:

- **Audit.** Every performed action is logged to `FileLog` and to `Session.RecentWingmanActions`
  (surfaced in `GET /sessions/{sid}/wingman`), so you can always see what the Wingman typed and why.
- **Self-injection guard.** Before writing, the executor calls `Session.SuppressActivityFor`
  (the same window the resize path uses) so the `TerminalStateDetector` does not mistake the
  echo/repaint of the Wingman's own keystrokes for fresh agent output and loop on it.
- **Idempotency / cooldown.** The executor refuses to act twice on an unchanged screen within a
  short window (`LastActedScreenHash` + `ActionCooldown`), so a repeated request (e.g. a
  double-tap) cannot inject onto a screen the Wingman just acted on.

---

## 5c. Transient-error auto-resume (the one approved self-actuator)

`TransientErrorAutoResume` (issue #476) is the single exception to invariant 8. Claude Code
sessions sometimes stall mid-turn on a **transient** Anthropic server error - the field-seen
`API Error: 500 Internal server error. This is a server-side issue, usually temporary - try
again in a moment.`, or the related `529 Overloaded` / "try again" family. These usually clear
themselves in a minute or two, but the session just sits there until a human nudges it. This loop
recognizes that *transient, retryable* state and auto-continues the session on a cadence until it
recovers - so long-running / unattended sessions self-heal from Anthropic-side blips.

How it stays inside the charter:

- **Content detection, not a model.** `TransientErrorSignatures.IsRetryableTransient` is a pure,
  case-insensitive substring check over the resolved screen grid (`Session.SnapshotScreenRows`).
  It matches the transient signatures AND vetoes on terminal signatures (invalid key, auth,
  quota/billing, malformed). A terminal error is NEVER auto-retried (scope OUT). No regex, no LLM.
- **Opt-in, default OFF.** Gated behind `config.json` `auto_resume.enabled` (`AutoResumeConfig`,
  default `false`). With it off the loop arms nothing and sends zero continues. This is the human
  decision on assumption A-3: the write action is approved only as an explicit opt-in.
- **One write chokepoint.** Every auto-continue is a `WingmanAction { submit "Please continue." }`
  executed through `WingmanActionExecutor` - invariant 7 is preserved, and each attempt is audited.
- **Bounded cadence + give-up.** First continue after `auto_resume.first_retry_seconds` (default
  60s), then every `auto_resume.interval_seconds` (default 300s) while the error persists. Stops
  the instant the error clears (recovery). Gives up after `auto_resume.max_attempts` (default 12)
  OR `auto_resume.max_elapsed_minutes` (default 120), whichever first, and on give-up flags the
  session red "needs you" so the user takes over.
- **Claude Code only.** Non-Claude and GitHub-Actions sessions are never wired (scope OUT).

Every detection, every auto-continue attempt (with attempt count + timestamp), recovery, and
give-up is logged with the `[TransientErrorAutoResume]` prefix and the session id.

---

## 6. The session recorder (corpus for offline learning)

`TerminalSessionRecorder` (observe-only, **OFF by default**) logs every session's resolved
terminal grid - one JSONL frame per change, each with the activity state and the raw rows - to
`%LOCALAPPDATA%/cc-director/session-recordings/`, capped per session. A general-purpose corpus
of what sessions actually looked like.

Switch it on with a visible setting in `config.json`:

```json
"session_recording": { "enabled": true }
```

`CC_DIRECTOR_RECORD_SESSIONS=1` / `=0` overrides that setting in either direction for one run.

It used to be ON for everyone, switchable off only through an environment variable named in a
source comment. That made an internal engineering corpus - every screen every agent has drawn,
secrets included, with no age limit - an invisible default on every install. What we collect
for our own benefit has to be something the user can see they turned on.

When a session is removed, its recording directory is deleted. Before that, removal closed the
writer and left the file, so an install with recording on accumulated the screens of sessions
that no longer existed in a directory nothing ever swept. Startup additionally sweeps any
recording whose session no longer exists, so recordings orphaned before an upgrade - removed
under the old release, when nothing purged - are deleted the first time the new Director runs.

## 6b. State-change log + the Desktop Wingman tab (observability)

Every state transition (blue<->red) is recorded so a session's history is inspectable:

- **In-memory ring** (`Session.RecentStateChanges`, newest first, capped 100): each entry is
  `{ time, from-state, to-state }`, recorded by `Session.SetActivityState` on every real
  transition.
- **Durable log** (`StateChangeLog`): one append-only JSONL per session at
  `%LOCALAPPDATA%/cc-director/state-changes/<sessionId>.jsonl`, each record carrying the
  timestamp, the from/to state, and the colour. Written by `SessionStatusWingman`. On by
  default; `CC_DIRECTOR_STATE_LOG=0` keeps only the in-memory ring.
- **`Session.LastOutputAtUtc`**: the raw "the terminal moved" timestamp, updated on every
  buffer write.

The **Desktop Wingman tab** (`WingmanView`, right-panel `TabControl` beside Screenshots)
renders this live for the selected session: the current colour, a once-a-second
**"Terminal moved: Ns ago" silence clock** with the current activity state, and the
state-change timeline. The silence clock is the key diagnostic - it shows exactly the
silence the 10s rule is counting. It is read-only - it observes the session, never writes
to it (invariant 2).

## 7. What we deleted, and why (do not resurrect)

- **The LLM turn-state judge** (`ClassifyTerminalStateAsync`,
  `ClassifyTerminalStateViaSessionAsync`, `ColorFromVerdict`, `MapVerdictToActivityState`,
  the `BuildTerminalState*Prompt` builders, and the 100-state synthetic / fixtures corpus):
  removed. The judge read the screen with an LLM each time the terminal went quiet and
  mapped a verdict to a colour. It worked but added latency (a model call per turn-end),
  cost, and non-determinism, and it was a second classifier that could disagree with the
  byte gate. Replaced by the dumb 10s timer in section 3.

  **A model judge IS BACK, and it is a different thing (2026-09, the Wingman-on-every-turn
  mission; section 3b).** What was deleted was a judge that decided WHETHER A STOP HAD
  HAPPENED, on the Director, competing with the byte gate for the same answer. What came back
  decides WHAT A STOP MEANS, on the Gateway, and is only ever asked about stops the byte gate
  has already decided happened. The three things that make it a different object:

  1. **It is not a second colour authority.** The byte gate is still the only thing that says
     a session stopped, and the fold is the only thing that says what colour a row is. The
     verdict is ONE input to that one fold, and a client renders the fold's answer verbatim.
     There is nothing for it to disagree with, because it is never asked the same question.
  2. **It cannot flip-flop a badge.** A verdict is stored once against the screen it was formed
     on (one canonical full-grid hash), it is REUSED rather than re-asked while that screen
     stands, and it may only ever move a row from red toward calm - never the other way, and
     never on a working, exited, brand-new or held session.
  3. **It fails to red, loudly, and its failures are stored.** The deleted judge's
     non-determinism mattered because a bad answer changed the badge. A bad answer here is
     REFUSED by mechanical validation and the row stays exactly as the detector left it. The
     refusal is stored with its reason and is a number in the grading report.

  **Do not resurrect the deleted one.** A model that answers "is this session working?" is
  still forbidden, and so is any second writer of the colour. The rule that survives unchanged
  is one producer per answer.
- **The competing colour heuristics in `SessionStatusWingman`**: the byte-burst
  `OutputActivityWatcher` (blue on a burst), the buffer question-marker scan
  (`BufferShowsUserGate` / `PromotePendingQuestion`), and the turn-summary colour voting
  (`ApplyTurnSummary`). Each was a separate path that could set the colour, and together they
  flip-flopped the badge. Removed so the colour has a single source: `ColorFromActivityState`.
- **Regex screen-parsing** (`ClaudeScreenReader`, `FinishDetectorCore`, `FinishDetector`):
  removed earlier; never resurrect pattern-matching footers/glyphs/menus.
- **Hook-based finish detection:** in the default terminal-driven mode the Director
  deliberately does NOT install Claude Code hooks (`App.axaml.cs`), and `ClaudeAgent`
  launches with only `--session-id`. Detection is terminal-only by design.

---

## 8. Known limits / open items

### The detector, and where its open items are settled

- **The timer is deliberately dumb.** A clean, finished turn goes silent and flips to red
  ("needs you") after 10s just like a turn that is genuinely blocked - the detector does not
  distinguish them. This is the accepted trade-off for having one simple, predictable,
  zero-LLM rule. Since 2026-09 the verdict in section 3b is what tells those two apart AFTER
  the fact, for an account that has switched it on; the detector itself is unchanged and is
  still not allowed a second classifier.
- **Two colours from the detector.** The Director's own badge is blue or red, plus grey for an
  exited session. Cyan, purple and the reading yellow come from the Gateway's fold, not from
  the detector, and they appear only where the account's colour switch is on.
- **A genuinely stuck session reads red the same as one waiting for you.** The silence clock
  in the Wingman tab is how you tell a long-running-but-alive turn from a finished one at a
  glance.
- `SessionStatusWingman` writes via `Session.SetStatusColor`, which still carries a
  source-precedence guard left over from the multi-writer era. With a single writer it never
  fires; it is harmless but vestigial and can be removed.

**Four open items about the DETECTOR belong to the trustworthy-state-switching design, not
here** (`docs/design/trustworthy-state-switching/` in the internal repository). They are named
so that nobody re-opens them against this charter:

1. the detector itself, and whether a stop is decided by something better than silence;
2. the ten-second quiet threshold;
3. each agent's own turn-end event, and the cause and confidence fields that would ride with
   it - section 3b reads them when they are present and treats their absence as a timer guess;
4. the snooze's "work ends it" edge - what a snooze should do when the session starts working
   while it is still running.

### Open items of the turn verdict (section 3b)

- **The desktop has the colour and the label, and no verdict panel.** The receipt, the summary
  and the option buttons are on the Cockpit and the phone. The desktop needs a Director wire
  change and a Director release to carry the rest, and that was deferred until the Director has
  one for other reasons.
- **A session whose supervisor dies with no new stop is not re-judged.** It was held, so it was
  never read; when its supervisor goes it surfaces raw red, which is the right direction, and it
  is judged at its next stop. Written into the code as a gap.
- **The cyan false-calm rate is NOT MEASURED under contract version two.** See invariant 1: the
  number the charter stands behind, 1.3 percent, was measured under version one, and the row
  becomes a number again on the first grading run whose `finished` answers were asked under
  version two.
- **About one in five of the fast tier's answers is refused for a receipt that is not verbatim**
  (63 of 341 on the re-score). That is the safe direction and it is the design - an invented
  quotation must never reach a screen - but every refusal is a stop that stayed red and that the
  owner then looked at for nothing. The prompt's quoting instruction is the lever, and it was
  deliberately not touched in the same change as the validation rules.
- **The snooze's arming moment is observed, not stored.** After a Gateway restart, or after a
  session genuinely leaves the account and returns, the moment is unknown and the expiry answers
  by asking - one extra read at the colour the row already had. It never quietens a question.
  The durable fix is to store the arming moment on the snooze itself, which costs a migration on
  both providers.
- **A shadow account does not re-judge on a snooze expiry**, because its rows carry no verdict
  to read. The re-judge should read the store, as the carrying-on clock does, so that shadow
  records reflect what the product would have done.

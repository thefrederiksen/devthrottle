# The turn verdict - what a stop MEANS

**Status:** BUILT and settled, 2026-09-16, by the mission "Wingman On Every Turn". This is the
settled specification. The product charter's summary of it is
[`docs/wingman/WINGMAN.md`](../../wingman/WINGMAN.md) section 3b, and the invariants it must
hold are in section 2 of that document.

**Supersedes:** [`TURN_BRIEFING.md`](TURN_BRIEFING.md) beside this file, which described the
turn-brief pipeline this replaced.

**Related, and deliberately NOT here:** whether a stop HAPPENED, the ten-second quiet rule, each
agent's own turn-end event, and the snooze's "work ends it" edge all belong to the
trustworthy-state-switching design in the internal repository. This document is only about what
a stop that has already been detected MEANS.

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

## 1. The problem, in one measurement

The badge says "needs you" whenever a session has been silent for ten seconds. That rule is
deliberately dumb and it is the right rule for deciding that a session STOPPED. It is a terrible
rule for deciding whether the stop matters.

Measured on the owner's fleet on 2026-09-14: 801 red flips, 248 turns he actually drove, and of
131 labelled turn ends 57 percent were "finished", 24 percent "needed a person", 15 percent
"carrying on by itself". Three reds in four did not need him.

A person who is woken three times for nothing stops trusting the fourth. So the verdict exists
to tell those apart - and, because a wrong calm answer is the one nobody notices, to get it
wrong in the direction of asking too often rather than too rarely.

## 2. The shape

```
The detector (Director, no model)            decides a stop HAPPENED
   |
   v
Turn-end boundary (Gateway)
   |
   +-- the boundary: seven steps, each with its cost in reads    see section 6
   +-- "reading" stamped -> yellow           so a judged stop never shows red first
   +-- one screen read, hashed once
   +-- the package: screen + reply or failure + last four turns + facts
   +-- ONE model call, one structured question, 30 second timeout
   +-- TurnVerdictContract.ParseAndValidate  mechanical: closed words, shape, caps, receipt
   +-- TurnVerdictStore                      accepted or failed, per account, both providers
   |
   v
SessionOrdering (the one fold)               colour, label, triage bucket, calm band
   |
   v
Every client renders it verbatim             desktop: colour and label. Cockpit and phone: the panel
```

Where each piece lives:

| Piece | File |
|---|---|
| The question, and the mechanical validation of the answer | `src/CcDirector.Core/Wingman/TurnVerdictContract.cs` |
| The prompt itself, as an embedded resource | `src/CcDirector.Core/Wingman/Prompts/turn-verdict-v2.txt` |
| The closed word lists, and what each word means | `src/CcDirector.Core/Wingman/TurnVerdictVocabulary.cs` |
| The seat: the checks, the reads, the call, the store, the ledger | `src/CcDirector.Gateway/Wingman/TurnVerdictService.cs` |
| What the judge is asked about this stop | `src/CcDirector.Gateway/Wingman/TurnVerdictPackageBuilder.cs` |
| The two switches and the timings | `src/CcDirector.Gateway/Wingman/TurnVerdictSettings.cs` |
| Colour, label and bucket | `src/CcDirector.Gateway.Contracts/SessionOrdering.cs` |
| What each colour means, in words | `src/CcDirector.Gateway.Contracts/SessionColourLegend.cs` |
| The carrying-on clock | `src/CcDirector.Gateway/Wingman/TurnVerdictWatchdog.cs` |
| The activation route - the owner typing | `src/CcDirector.Gateway/Wingman/TurnVerdictAnswer.cs` |
| "This verdict is wrong" | `src/CcDirector.Gateway/Wingman/TurnVerdictFeedbackService.cs` |

## 3. The contract

One JSON object per stop. These are the wire names.

```
{
  "verdict":         "needed-you" | "finished" | "continues-alone" | "stuck-recoverable" | "stuck-needs-person" | "cannot-tell",
  "confidence":      "high" | "ambiguous",
  "evidence":        "<the agent's decisive sentence, copied character for character>",
  "label":           "<= 10 words: the ask, or the report",
  "summary":         "1-2 sentences for a reader who has not looked at this session for hours",
  "agentRecommends": null | "<the agent's own recommendation, quoted or closely paraphrased>",
  "answerVia":       "reply" | "keys",
  "menu":            null | { "question": "...", "selectionMode": "single" | "multiple", "submit": "" | carriage return },
  "options":         [ { "key": "...", "send": "...", "recommended": true|false, "note": "<= 18 words: consequence and risk" } ],
  "risk":            "none" | "irreversible" | "standing-grant" | "spends-money",
  "spoken":          "<the same content for the ear, about 30 seconds, opening with the session title>",
  "finishedKind":    "done" | "report"        (only when the verdict is "finished")
}
```

### What each verdict word means

The meanings are copied word for word from the labelling tool in the internal repository, so a
corpus label and a live verdict mean the same thing.

| Word | Meaning | Calm? |
|---|---|---|
| `needed-you` | Genuinely waiting on a person and could not go on without one | No |
| `finished` | Nothing needed from a person, and nothing further to do | Yes |
| `continues-alone` | Nothing needed from a person, and the session will carry on by itself | Yes |
| `stuck-recoverable` | Stopped on a transient fault that typing continue would have fixed | No |
| `stuck-needs-person` | Stopped in a way no automatic retry fixes | No |
| `cannot-tell` | The record does not contain enough to say. A real answer, not a failure to answer | No |
| `not-a-turn-end` | The boundary fired while the session was still working. **Belongs to the detector. The Wingman may never answer with it, and an answer carrying it is rejected** | n/a |

`finished` and `continues-alone` are the same answer to "wake the owner?" and different answers
to "should anything have happened next?". That is the whole reason they are two words: a
finished session staying quiet is correct; one that said it would continue and then stayed quiet
is stuck, and the clock in section 7 is what catches it.

### Validation, which is mechanical and never interpretation

Nothing below reads the answer for sense. A judge that could talk its way past its own
validation is not validated.

- **Shape before meaning**, proved in one pass before any member is read for what it means
  (`TurnVerdictContract.ValidateShape`). This is the exact rule, not a paraphrase of it:

  - **Eight members must be PRESENT and must be strings**: `verdict`, `confidence`, `evidence`,
    `label`, `summary`, `answerVia`, `risk`, `spoken`. Absent rejects the whole answer; present
    but of any other JSON kind rejects the whole answer. A missing field is not an empty one.
  - **`options` must be PRESENT and must be an array.** Empty is the answer when there is nothing
    to offer. Absent rejects, and so does null, and so does any other kind - deliberately, because
    absent-means-empty would be a second way to say one thing, and the same list would then be
    synthesised from either.
  - **`agentRecommends` and `menu` are the ONLY members that may be null or absent.** Absent reads
    as null, because the shape's own null-or-a-value wording says there is nothing to carry. A
    wrong TYPE still rejects the whole answer: a string where `menu` belongs is not read as no
    menu, because a picker silently becoming no picker is how a selection the owner cannot make
    gets stored as one he can.
  - **`finishedKind` is conditional**, and is covered by its own rule below rather than by the
    shape pass.

  Nothing missing is ever synthesised - no invented `answerVia`, no empty `summary`, no
  `recommended` coerced from a string. Each of those was once a repair this contract made on the
  judge's behalf and then stored as though the judge had made it.
- **Closed words.** `verdict` must be one of the six; `confidence`, `answerVia`,
  `selectionMode`, `risk` and `finishedKind` each one of their own list. Any other value rejects
  the whole answer. There is no safe default for `risk` in particular: defaulting to `none`
  would be the contract quietly telling the owner that an irreversible action is free.
- **`finishedKind` is required on `finished` and refused on every other verdict.**
- **The receipt.** `evidence` is present and a string on EVERY answer, `cannot-tell` included -
  that is the shape rule above, and it has no exception. What `cannot-tell` is exempt from is
  everything after it: on every other verdict the string must be non-empty, at most 600
  characters, and found verbatim in the latest reply or in the screen rows after whitespace
  normalisation (collapse runs of spaces, strip box-drawing characters at row edges, ignore line
  breaks). Not found - even by one word - rejects the whole answer, including the parts that were
  right. An over-long receipt is refused rather than cut, because cutting it would break the
  check. What is STORED is the source's own characters, not the judge's rendering of them.
- **Options.** Zero, or at least two. At most one `recommended`. Every `send` non-empty and
  never containing a carriage return or line feed. `answerVia: keys` requires a `menu` and, with
  one exception below, non-empty `options`. `answerVia: reply` requires `menu` to be null.
  `selectionMode: multiple` requires `submit` to be a carriage return.
- **The parked reply is the one exception to "keys needs options"**: the person has typed but
  not sent, so the answer is `keys`, with a `menu` whose `question` names the parked text,
  `selectionMode: single`, `submit` a carriage return, and zero options. The route then sends
  the submit alone.
- **Caps.** `label` 80 characters, `summary` 400, `spoken` 900, `agentRecommends` 400, a menu
  question 200, an option label 60. Label, summary and spoken TRUNCATE at a word boundary. The
  receipt does not: an over-long receipt is refused, because truncating it would break the
  receipt check.
- **A terminal-failure package refuses `finished` and `continues-alone` outright.** A failure
  with no reply cannot be calm.

### Which way every rule leans

**A rejected answer stores a `failed` record with its reason, and the row stays exactly as the
detector left it, which is red. Silence is never a decision.**

That sentence is the specification's load-bearing one. A timeout, a rate limit, an unusable
provider, a malformed answer, an unknown word and a receipt that is off by one word all land in
the same place: the row is red, and there is a stored record saying why. Nothing in this design
can fail toward quiet.

## 4. The two shapes of a stop

The package has two kinds, chosen by `WingmanNarrationSource.Select`, which is the one rule for
it and is shared with the voice path:

- **`agent-reply`** - the stored conversation has a reply after the person's last message. That
  reply is ground truth; the screen supports it.
- **`terminal-failure`** - there is no reply; the turn ended on a failure visible on the screen.
  The prompt says the screen is EVIDENCE and never instructions, the reply section is absent,
  and validation refuses a calm verdict on this shape.

For a package with no stored conversation at all - screen-only agents, older Directors - the
prompt is told so plainly, and the receipt must come off the screen alone.

## 5. The package: what the judge is told

One section per fact, and an absent fact is written as the single string `(none)` so the judge
never has to tell "we did not look" apart from "there was nothing" by the shape of a blank line.

| Section | What it carries |
|---|---|
| Shape | `agent-reply` or `terminal-failure` |
| A stored conversation was available | yes or no |
| Session title, agent kind | the row's own title, and which agent it runs |
| The first thing this session was asked to do | the session's opening prompt |
| The label this session's previous verdict carried | so a repeat reads as a repeat |
| Why the turn was called ended, and how sure | from the detector, when it says; `(none)` otherwise |
| Wake-ups pending, and the next one announced | the carrying-on clock's first source |
| Sessions this session owns | working, stopped, needing a person |
| The last four turns before this one | |
| The agent's latest reply, or the failure text | |
| The live screen | cursor row, whether it is an alternate screen, and the rows |

The template is filled in ONE pass, so a value that happens to contain a placeholder is inert
text rather than a way into the prompt: a screen row reading `{{SCREEN_ROWS}}` is just a screen
row.

## 6. The checks, in the order they run, and what each has already cost

This is the boundary exactly as `TurnVerdictService.JudgeAsync` runs it. The same block, word for
word, is the comment above that code and the charter's section 3b, so the three cannot describe it
differently:

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
4. The speech re-attempt refusal: a caller that may not ask the judge stops here, before the
   conversation is read. A stop refused here costs ONE read.
5. The conversation read.
6. The provider deadline, then the account ceiling. A stop refused by either costs TWO reads.
7. The model call.
```

### Where each step is

Line numbers in `src/CcDirector.Gateway/Wingman/TurnVerdictService.cs` at the commit this document
landed on. They are cited so the order can be CHECKED rather than believed; they will rot as the file
changes, and a stale line number is visibly stale where a wrong sentence about a boundary is not.

| Step | What | Line |
|---|---|---|
| 1 | The account settings, read once for the whole flight | 770 |
| 1 | The held, live, brand-new, exited and working checks | 801 |
| 1 | The judge switch - the only place in the flight it can stand a request down | 809 |
| 1 | The settle wait | 812 |
| 1 | The held, live, brand-new, exited and working checks again, for an automatic request - not the switch | 818-819 |
| 2 | The screen read | 825 |
| 3 | The reuse check | 830 |
| 4 | The speech re-attempt refusal | 844 |
| 5 | The conversation read | 851 |
| 6 | The provider deadline | 857 |
| 6 | The account ceiling | 866 |
| 7 | The model call | 898 |
| - | The switch's one later use: whether the inspector's trace is written, after the store | 953 |

### Three earlier versions of this section were wrong

The first said "nothing is read and nothing is paid for until every check passes". Steps 3, 4 and 6
all come after the screen read, so a reader who believed it would have concluded that a stop refused
by the account ceiling had read nothing. It had read the screen and the conversation.

The second fixed that and then filed the speech re-attempt refusal with the checks that come after
BOTH reads, at a cost of two. It comes before the conversation is read and costs one - the table
above shows it at line 827, before the conversation read at line 834.

The third put the order and the costs right, and then said "the same checks again" after a list that
included the judge switch. The switch is not checked again. It is checked once, before the settle
wait, from settings read when the flight starts; after the wait only the held, live, brand-new,
exited and working checks run a second time. So a switch turned off during a flight does not stop
that flight, and a reader who believed the third version would have expected it to.

The code was right all three times. All three errors were in the sentence beside it, and the third
was written from a description of the code rather than from the code.

### Why the boundary is where it is

**The real boundary is step 6.** A stop refused there has cost two reads and no model call. That is
the deliberate shape: the expensive, rate-limited, chargeable thing is the model, and step 6 is what
stands in front of it. The reads are cheap, local to the Gateway and its tunnel, and the screen read
pays for itself through the reuse.

Notes on individual steps, which add reasons and do not change the order or the costs above:

- **Held** is resolved across the account's WHOLE fresh roster in one snapshot, never off the
  session's own row. The push store nulls the role at ingest, so a check that read the row would
  answer "not held" for every session on the fleet and read every worker. Held, live, brand-new,
  exited and working are one function, `SessionStateSkipCause`, over one snapshot.
  **Held has two answers (owner ruling, 2026-09-16).** Held for narration is "a live owning session
  holds this one", and the voice narration and the idle sweep read it. Held for judging is the same
  EXCEPT when that direct owner is the account's Fleet Manager: the turn end and the snooze expiry
  then judge the session and store its verdict under its own id. It is still never narrated
  automatically - a person pressing Explain can still narrate any held session, as before - and the
  fold still parks it for the owner. Carrying the verdict to the Fleet Manager is step 4 of the Fleet
  Manager mission and is not built yet. The Fleet Manager is the ONE session the account has marked
  (tenant setting `fleet_manager_session_id`, `PUT /gateway/fleet-manager`,
  `cc-devthrottle fleet-manager set|clear|show`), and only while no session owns it; the workflow a
  session is seated on never decides it. The rule is `FleetManagerSessions.IsFleetManager`.
- **The judge switch** binds only the two triggers nobody is waiting on: the detector's turn end, and
  a snooze expiry with a stop nothing has judged. A voice session is judged whatever the switch says,
  because its narration IS the verdict's spoken section, and a person's own request is not automatic
  at all.
- **The held, live, brand-new, exited and working checks run again after the settle wait** (600
  milliseconds by default); **the judge switch does not.** A session can become held, or start
  working, while the request waits, and the first answer does not license a read made later. The
  switch is taken from the account settings read when the flight starts and is not looked at again,
  so turning it off stops the NEXT flight, not one already waiting or reading. The one later use of
  the switch in the flight decides only whether the inspector's trace is written after the verdict
  is stored, and it reads that same early copy.
- **The screen is read before the reuse check because the reuse check needs it**: it compares the hash
  of this screen to the hash a stored verdict was formed on, and has no answer without it. A screen
  read placed after step 6 would buy nothing and would cost every reusable stop a model call. An
  unreadable screen hashes to the empty string and is never reused outside the idle sweep, because
  otherwise two different stops on an unreachable Director would look like one screen and the second
  would be played the first one's words.
- **The speech re-attempt refusal** exists because no automatic path may cost two model calls for one
  stop. The reuse check is the only thing a re-attempt is entitled to, which is why it stops
  immediately after it and before anything else is read.
- **The conversation** is read only once the reuse check has found no match, and
  `WingmanNarrationSource.Select` uses it to choose the package kind.
- **The provider deadline** applies to a caller the provider told to wait - the voice path, after a
  rate limit on this stop - which is not asked about again for this stop until the wait has passed.
- **The account ceiling** is eight judgements in flight, and it binds the turn end, the idle sweep and
  the snooze expiry. A stop over it is not judged; it stays exactly as the detector left it, because
  the alternative is a queue whose answers arrive about screens that have moved on.

Only after step 6 is the package built, the prompt rendered and the judge asked.

## 7. What the verdict does to a row

Both switches are per account and BOTH DEFAULT OFF. `JudgeEnabled` decides whether this
account's stops are judged at all; `ColourEnabled` decides whether the verdicts reach the
screen. Judging on and colour off is the shadow state: verdicts are stored and gradeable and
every row stays exactly the colour the detector made it.

A row is calmed only when ALL of these hold. Every gate that cannot be answered answers "not
calm":

- the row is raw red (stopped - never working, exited or crashed), and
- the account's colour switch is on, and
- the verdict state is `judged`, and
- `confidence` is `high`, and
- the word is `finished` or `continues-alone`.

| Verdict | Colour | Label | Counted in "needs you"? |
|---|---|---|---|
| `finished`, kind `done` | cyan | leads "Done" | No. Listed in the calm band below the reds |
| `finished`, kind `report` | cyan | leads "Report" | No. Same band |
| `continues-alone` | purple | the Wingman's line, or "Carrying on" | No. Same band, clock running |
| Any other word | red, unchanged | the ask, in the Wingman's words | Yes |
| Refused, timed out, rate limited, never judged | red, unchanged | as today | Yes |

**Cyan and not green.** Green is the brand-new session's "Ready". A finished row painted green
read as a session that had not started yet, which is the opposite of what is true about it, so
the two never share a colour.

**Purple carries a clock.** The agent's announced next wake-up plus two minutes when it
announced one, otherwise ten minutes. On expiry the row goes red with "Said it would continue
and did not". A session whose own owned sessions are still working is carrying on whatever its
reply says, and the clock does not run while any of them works.

**A reading stamp comes first.** A stop the Wingman will judge never shows red first: `reading`
is stamped at the boundary before any wait or read, so the first colour pushed is the yellow
"Wingman reading". The verdict's colour follows it, or red on any failure.

**A snooze that ends** with no new turn end comes back cyan with "Snooze ended, nothing new" -
nothing happened, so there is nothing to bring him. If a stop did happen while the snooze ran,
that stop's verdict rules, and only a needs-you verdict brings the row back red. A failed
narration outranks the snooze-ended arm: "nothing new" is terminal and a failed voice is
something he can act on.

## 8. Answering is the owner typing

A verdict may carry options; the Cockpit and the phone render them as buttons. Pressing one is
the OWNER typing. It goes through one server-owned route,
`POST /sessions/{sid}/turn-verdict/answer`, which:

- binds the account, the session, the verdict id, the screen version and the allowed bytes -
  the client sends option INDEXES, never bytes of its own;
- re-reads the live screen and refuses a mismatch with "The screen has changed since the Wingman
  read it, so nothing was sent. Look at the session again.";
- refuses a second attempt on the same verdict with "That stop has already been answered, so
  nothing was sent.";
- for `reply`, sends the chosen option's `send` plus one Enter; for `keys`, sends the selected
  options' `send` bytes in order and then the menu's `submit`, under one screen lock;
- writes one activity-ledger line for every activation, including every refusal.

**The Wingman itself never presses a button.** The charter's invariant 8 is untouched: the one
sanctioned self-actuator is still transient-error auto-resume. A verdict's options are a shorter
route for the owner's own hand.

Answering SUPERSEDES the verdict rather than deleting it. The record stays findable, stamped
with the moment it was superseded, so a correction can still be made about a stop that has
already been answered.

## 9. When the verdict is wrong

The panel carries "This is wrong": one word from the shared vocabulary - all SEVEN of them, since
the destination is the labelled corpus and `not-a-turn-end` is a real reading of a real failure -
and an optional note, through `POST /sessions/{sid}/turn-verdict/feedback`. The labelling tool in
the internal repository pulls those corrections into the corpus, where an owner label outranks a
two-family reviewer label for the same turn.

That is the loop by which the charter's score is meant to be re-measured, and it is the only
label that outranks the corpus.

---

# 10. Sanitised twins

Worked examples. **Nothing in them is real**: the sessions, the repositories, the file names and
the agents' words are all invented, in the house style of
[`examples/`](examples/) beside this file. They exist to show the shape of the judgement, not to
be evidence about any fleet.

## Twin 1 - a report. Cyan, "Report", not counted

### The screen the judge was given (last rows, sanitised)

```
> summarise what changed in the pricing table migration

  I read the two migration files and the table they touch.

  The migration adds one nullable column, currency_code, and backfills it to
  'USD' for every existing row. It does not drop or rename anything, and it
  runs in under a second on a table this size.

  Nothing here needs a decision from you - I have not changed any code.

>
```

### The stored conversation had a reply

Yes. Shape: `agent-reply`.

### The answer

```json
{
  "verdict": "finished",
  "finishedKind": "report",
  "confidence": "high",
  "evidence": "Nothing here needs a decision from you - I have not changed any code.",
  "label": "Read the migration; nothing needs deciding",
  "summary": "It read the pricing-table migration and reported what it does: one nullable column added and backfilled, nothing dropped or renamed. It asks nothing and changed nothing.",
  "agentRecommends": null,
  "answerVia": "reply",
  "menu": null,
  "options": [],
  "risk": "none",
  "spoken": "Pricing migration review. It read the migration and reported back. One nullable column is added and backfilled; nothing is dropped or renamed. It changed no code and asks you nothing."
}
```

### What the owner sees

Cyan. Label **"Report - Read the migration; nothing needs deciding"**. In the calm band below the
reds, and NOT in the "needs you" count. The receipt sentence shows above the summary in the
panel, so he can check the judgement against the agent's own words in one glance.

### Why `report` and not `done`

The agent informed him and asked nothing; it did not claim a piece of work was complete. `done`
is for the second case. Both are cyan, both are uncounted, both sit in the same band - only the
word changes, which is the point of the kind.

## Twin 2 - a permission prompt. Red, with buttons

### The screen the judge was given (sanitised)

```
 Bash command
   rm -rf ./build-cache
   Remove the stale build cache

 This command requires approval
 Do you want to proceed?
 > 1. Yes
   2. Yes, and do not ask again for: rm *
   3. No
 Esc to cancel
```

### The answer

```json
{
  "verdict": "needed-you",
  "confidence": "high",
  "evidence": "This command requires approval",
  "label": "Approve deleting the stale build cache?",
  "summary": "It wants to delete the build cache directory and is waiting on your approval. Option two would stop it asking about any future rm command.",
  "agentRecommends": null,
  "answerVia": "keys",
  "menu": { "question": "Do you want to proceed?", "selectionMode": "single", "submit": "" },
  "options": [
    { "key": "Yes", "send": "1", "recommended": true, "note": "Deletes the build cache directory. It is rebuilt on the next build." },
    { "key": "Yes, and stop asking for rm", "send": "2", "recommended": false, "note": "A standing grant: every future rm runs without asking you." },
    { "key": "No", "send": "3", "recommended": false, "note": "The command is skipped and the turn ends." }
  ],
  "risk": "standing-grant",
  "spoken": "Build cache cleanup. It is asking permission to delete the build cache directory. There are three choices, and the second one would stop it asking about any future delete command."
}
```

### What the owner sees

Red, counted, with **"Approve deleting the stale build cache?"** as the label and three buttons.
Tapping "Yes" sends the single byte `1` and nothing else - `submit` is empty, because this picker
acts on the digit. If the screen has moved since the Wingman read it, the tap is refused and
nothing is sent.

### The parts a reader should notice

- `risk` is `standing-grant`, not `none`, because one of the options is a standing grant. The
  risk of the whole stop is the worst risk on offer.
- The recommended option is the narrow one, not the convenient one.
- `evidence` is four words off the screen, copied exactly. It is short on purpose: the receipt is
  there to prove the judgement came from this screen, not to re-tell it.

## Twin 3 - a terminal failure. Red, and calm was refused before it was considered

### The screen the judge was given (sanitised)

```
 API Error: Connection error.
 Request timed out after 600 seconds.

 The session stopped. Nothing was written.
```

### The stored conversation had a reply

No. Shape: `terminal-failure`.

### The answer

```json
{
  "verdict": "stuck-recoverable",
  "confidence": "high",
  "evidence": "Request timed out after 600 seconds.",
  "label": "Stopped on a timeout; retrying would continue it",
  "summary": "The turn ended on a request timeout rather than a reply. Nothing was written. This is the kind of fault that continues if something tells it to.",
  "agentRecommends": null,
  "answerVia": "reply",
  "menu": null,
  "options": [],
  "risk": "none",
  "spoken": "Documentation sweep. It stopped on a request timeout rather than finishing. Nothing was written, and a nudge to continue would most likely pick it back up."
}
```

### What the owner sees

Red, counted, with the label as the ask.

### Why this twin is here

On this shape, `finished` and `continues-alone` are refused by validation before the answer is
read for anything else. A failure with no reply cannot be calm, whatever the judge says about
it. It is the clearest case of the specification refusing an answer rather than trusting one.

## Twin 4 - the answer that was thrown away

This is the twin that matters most, and it is the one a reader skips.

### The screen the judge was given (sanitised)

```
  I have finished the rename. All 41 call sites now use the new name.

>
```

### The answer the judge gave

```json
{
  "verdict": "finished",
  "finishedKind": "done",
  "confidence": "high",
  "evidence": "I have finished the rename and all 41 call sites now use the new name.",
  "label": "Rename complete across 41 call sites",
  "summary": "The rename is done and every call site uses the new name.",
  "agentRecommends": null,
  "answerVia": "reply",
  "menu": null,
  "options": [],
  "risk": "none",
  "spoken": "Rename sweep. The rename is finished and all forty-one call sites use the new name."
}
```

### What happened to it

**Rejected, whole.** The receipt reads `I have finished the rename and all 41 call sites now use
the new name.` The screen says `I have finished the rename. All 41 call sites now use the new
name.` The judge joined two sentences into one and changed a full stop to "and". After whitespace
normalisation that string is still not on the screen, so the answer is thrown away - including
the parts that were right, which is all of them.

A `failed` record is stored with the reason. The row stays red and counted, exactly as the
detector left it.

### Why this is the design and not a bug

The receipt is what lets the owner check a judgement in one glance. A receipt the judge retyped
from memory instead of copying cannot do that job, and a receipt that is nearly right is worse
than none, because it reads as proof. So the rule is exact and there is no partial credit.

The cost is real and it is measured: on the 15 September grading run about one in five of the
fast tier's answers was refused for exactly this, and each refusal is a stop that stayed red and
that the owner then looked at for nothing. The lever is the prompt's quoting instruction, not the
check - see the charter's section 8.

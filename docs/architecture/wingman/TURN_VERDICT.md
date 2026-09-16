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
   +-- tier one: held, live, not brand-new, not exited, not working, the switch
   |       nothing read yet; a stop refused here costs nothing
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

The first version of this section said "nothing is read and nothing is paid for until every check
passes". That was false, and it is worth saying why rather than quietly correcting it: two of the
checks sit AFTER both reads, and a reader who believed the old sentence would have concluded that a
stop refused by the account ceiling had read nothing. It had read the screen and the conversation.

The code was always right. The order below is `TurnVerdictService.JudgeAsync`, and the line numbers
are that file at the commit this document landed on.

### Tier one - decided before anything is read (lines 761-782)

A stop refused here has cost nothing at all.

1. **Held.** A session a live owning session is holding is not the owner's to be read. Resolved
   across the account's WHOLE fresh roster in one snapshot, never off the session's own row - the
   push store nulls the role at ingest, so a check that read the row would answer "not held" for
   every session on the fleet and read every worker.
2. **Live at all**; **not brand new**; **not exited**; **not working**. These four and the held check
   are one function, `SessionStateSkipCause`, over one snapshot.
3. **The judge switch**, which binds the two triggers nobody is waiting on - the detector's turn end,
   and a snooze expiry with a stop nothing has judged. A voice session is judged whatever the switch
   says, because its narration IS the verdict's spoken section, and a person's own request is not
   automatic at all.
4. **The settle wait** (600 milliseconds by default), after which the role and the facts are
   **resolved again** against a fresh snapshot and put through the same tier-one checks. A session
   can become held, or start working, while the request waits, and the first answer does not license
   a read made later.

### Tier two - the reads, which the checks after them need (lines 785-813)

5. **One screen read**, hashed as ONE canonical full-grid hash over every row (line 785).

   **This read is not gated by anything below it, and it cannot be.** The very next thing the seat
   does is ask whether a stored verdict was formed on this same screen (line 790), and that question
   has no answer without the hash. The read is what makes the reuse possible, and the reuse is what
   stops the model being asked again about a screen that has not moved. A read placed after the
   ceiling check would buy nothing and would cost every reusable stop a model call.

   An unreadable screen hashes to the empty string and is never reused outside the idle sweep,
   because otherwise two different stops on an unreachable Director would look like one screen and
   the second would be played the first one's words.

6. **The stored conversation** (line 811), read once the reuse check has found no match, and used by
   `WingmanNarrationSource.Select` to choose the package kind.

### Tier three - the checks that gate THE MODEL CALL (lines 804-831)

**This is the real boundary.** A stop refused here has cost two reads and no model call. That is the
deliberate shape: the expensive, rate-limited, chargeable thing is the model, and these are what
stand in front of it. The reads are cheap, local to the Gateway and its tunnel, and one of them pays
for itself through the reuse.

7. **A speech re-attempt may not ask the judge at all** (line 804) and stops here, after the reuse
   check, which is the only thing it was entitled to. No automatic path costs two model calls for one
   stop.
8. **The provider's own deadline** (line 817). A caller the provider told to wait - the voice path,
   after a rate limit on this stop - is not asked about again for this stop until the wait has passed.
9. **The account's ceiling** (line 826), eight judgements in flight. A stop over the ceiling is not
   judged; it stays exactly as the detector left it. The alternative is a queue whose answers arrive
   about screens that have moved on.

Only then is the package built, the prompt rendered and the judge asked (line 858).

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

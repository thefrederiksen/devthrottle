# Review - Wingman error and retry

Reviewer seat, mission "Wingman error and retry", 20 September 2026. The worktree under review is
`D:\ReposFred\devthrottle-wingman-error-review` at commit b6bef195b, whose merge base with main is 217b79f63.
Every fact below comes from that tree or from a run I made against it, and I say which.

## Verdict

**No material findings.** Within the scope stated below I found no correctness defect, no data loss, no security
hole and no user-visible break in this change. The one unexplained test result is explained, and it is not a
defect in this change.

## The one unexplained test result: answered

`VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message` is a
**pre-existing test isolation problem, not a defect in this change**. My evidence:

1. **I reproduced it on this branch.** Running the class alone (three tests) thirteen times on this machine,
   four runs failed, every time the same test, every time at line 272 of
   `src/CcDirector.Gateway.Tests/VoiceSweepBudgetTests.cs`:
   `Assert.False(_gateway.VoiceService.NothingToNarrateFor(...))`.
2. **I reproduced it on the merge base.** I cut a throwaway worktree at 217b79f63 - the exact commit this branch
   was cut from, without any of this change - built the same suite there and ran the same class fourteen times.
   Two runs failed, the same test, the same assertion, the same failure text. A defect this change introduced
   cannot fail on a tree this change is not in. The worktree was deleted afterwards.
3. **The mechanism, from the code.** The sweep (`GatewayHost.SweepVoiceSessionsAsync`) awaits only the
   screen-read preparation and then dispatches the generation fire-and-forget (`_ = vs.GenerateAsync(...)`),
   so it returns while the generation is still running. The flag the test asserts on is cleared inside that
   background generation, but only after the judge call it makes has finished. The test asserts immediately
   after the sweep returns, so whether the clearing has landed is a pure race between the test thread and a
   background task. The failure rate follows machine load: this machine is busy today (many fleet sessions
   working), and I see roughly one failure in three; the Developer, on a quieter pass, saw one failure in an
   hour. The test file is unchanged by this branch, and the diff shows the clearing code as untouched context -
   this branch moved neither the assertion nor the mutation it waits for.

So the answer to the question the mandate asks is: **pre-existing test isolation problem**. The test asserts
state that a fire-and-forget task mutates, without waiting for that task. The fix (make the test wait, as the
sibling tests in the same class already do with `WaitForVerb`) belongs to the seat that owns the test suite, not
to this change, and I have not made it.

## What I checked, item by item

**Only one retry mechanism.** `ModelRetries`, `MarkAbandonedIfNothingPending` and `NarrationAbandoned` return
zero hits across the whole source tree. The ten, thirty and ninety second ladder, its booked tasks, its
hold-off, its reservation ids and its test seams are deleted from `WingmanVoiceService.cs`. What survives on
the speech leg is a per-verdict place on the ONE schedule (`SpeechRetries`, keyed by verdict id, held in
memory, cleared by a new turn, by voice off and by success), carried by the same idle sweep and read from the
same `WingmanRetrySchedule`. That is one mechanism with two carriers, which is what the mission asked for.

**Goal 6: no card says an attempt is coming when none is booked.** Every "a retry is coming" sentence is
rendered from `TurnVerdictDto.NextRetryAtUtc` - the same field `StartDueRetries` reads and the same field the
sweep books - and `WingmanErrorDisplay.Exhausted` is true exactly when that field is null. I followed every
path the mandate names:
- *A stale stamp*: while a reading is in flight the row is stamped "reading" and carries no error
  (`TurnVerdictRowStamp.Reading`), so a live retry never shows beside an old promise.
- *A withdrawn booking*: a booked retry whose account has the judge switched off and nobody listening is
  withdrawn when it comes due (`WithdrawBookedRetry`), and the card flips to the Gateway's own "nothing more is
  scheduled" sentence, which is then true.
- *An offline computer*: the retry stays booked and due; the card says the retry "is due now", which is true
  (see the observations below for how long it can read that way).
- *A state transition*: a Working edge invalidates the stored record, so no error and no promise survives the
  session going back to work.
- The old false sentences are gone: `gaveUp` no longer says "The Gateway is still trying", and `notNarrated`,
  which said "nothing further is scheduled" from a flag in memory, is deleted in favour of one state whose
  wording is chosen by the booked time itself. The client (`WingmanErrorLine.tsx`, `wingmanError.ts`) does no
  ruling: its only arithmetic is turning the Gateway's absolute time into "in 40 seconds", and a time it cannot
  parse degrades to the bare label rather than a claim.

**The schedule is exactly 1, 1, 1, 5, 5, 5, 30, 30 minutes and then stops.** `WingmanRetrySchedule.Delays`
is exactly that array; `NextRetryAtUtc` returns null once eight retries are spent, and `StampRetrySchedule`
clamps the count at eight so nothing can be booked past the end. I checked the arithmetic against the code,
not the test names, and then ran the driving test (`TheSchedule_IsOneOneOneFiveFiveFiveThirtyThirty_AndThenItStops`),
which asserts at one second before each booked time that nothing runs, at one second after it that exactly one
retry does, and after the eighth that no sweep however late ever asks again. It passes. A rate limit's named
wait is honoured as the later of the two, in both the reading leg and the speech leg, and the boundary documents
(`docs/wingman/WINGMAN.md`, `docs/architecture/wingman/TURN_VERDICT.md`) were brought down to the six-step
boundary the code now has.

**The cost rule survives.** A successful record is reusable by every automatic trigger including `Retry`
(`IsReusable` returns true for a record that did not fail), and `StartDueRetries` only starts records that need
a retry and whose booked time has come, so no automatic path can pay twice for a successful reading of an
unchanged screen. The driving test presses the sweep's own question and the Retry, Sweep and Voice triggers
directly, two hours later, against a successful reading: nothing is re-asked.

**A bad button list drops the whole list and keeps the reading.** Every option-list and menu rule in
`TurnVerdictContract` - exactly one option, two marked recommended, a missing or empty part, an over-long key or
note, a line ending inside a send, a menu with no question, a menu that cannot be performed - now drops the
whole list, keeps the state and the label, records why in `OptionsDroppedReason`, and the answer is narrated
and shows no error. Nothing is repaired and nothing is guessed: the renamed contract test for two recommended
options pins that the list goes rather than a flag being quietly cleared. The rules outside the button list -
nothing answered, not valid JSON, not a JSON object, a shape fault, an unknown state word, a calm state on a
terminal failure, no label - still refuse the whole answer, still show the tag and still go on the schedule.
The reason reaches the debug view (`WingmanDebugFold`, `WingmanDebugView.tsx`).

**The tests drive the real producer.** The schedule tests construct the real `TurnVerdictService` over a fake
environment and drive it through the sweep's own entry point (`StartDueRetries`) and real turn ends, with a
clock the test moves - they do not hand-build the record they assert on. The speech tests drive the real
`WingmanVoiceService` through `PrepareSweepGenerationAsync` and `GenerateAsync`, the sweep's own pair of calls,
with a movable clock. The contract tests call the real `ParseAndValidate`. The web tests mount the real
Cockpit roster page and the real phone home page (with the transport mocked, which is what a mount test is
for). I found no test that asserts on state it constructed itself where the code under test should have
produced it.

## Observations I am NOT reporting as findings

Both were flagged by the Developer's own report, both are true sentences at the moment they are shown, and
neither breaks goal 6. The seat that drives the mission may still want to weigh them:

- A due retry on a session whose computer is offline stays due forever, and the card says "retry N of 8 is due
  now" until the computer returns. The sweep also writes one skip line per pass for each such session, which
  is log volume on a fleet with red sessions parked on offline machines.
- A booking that can never run (the account's judge switched off, nobody listening) is withdrawn only when it
  comes due, so the card can promise "retry N of 8 in X minutes" for up to thirty minutes before it flips to
  "nothing more is scheduled".

## Scope: what I read, what I ran, what I could not reach

**Read.** The mission document, the Developer's report and the proof document. The whole diff against the
merge base (66 files), with the Gateway files read in depth: `WingmanRetrySchedule`, `WingmanErrorFold`,
`TurnVerdictService` (the reuse rule, `StampRetrySchedule`, `StartDueRetries`, `WithdrawBookedRetry`, the
boundary failure path, `IsReusable` before and after), `WingmanVoiceService` (the deleted ladder, the speech
schedule, `AskAgainAsync`, the sweep arm), `TurnVerdictContract`, `VoiceDisplayFold`, `VoiceRowStamp`,
`TurnVerdictRowStamp`, `GatewayEndpoints`, `GatewayHost`, the contracts (`TurnVerdictDto`, `SessionDto`,
`VoiceDisplay`, `WingmanErrorDisplay`), the shared client component and its tests, both app mounts and their
tests, the boundary documents, and the proof rig's source. The old ledger's names were grepped across the
whole tree.

**Ran** (all in this worktree, at b6bef195b):
- The new driving class `AFailedReadingIsAskedAgainOnAScheduleTests`: 15 passed.
- `WingmanVoiceServiceTests`: 60 passed. `TurnVerdictContractTests`: 71 passed.
- The web tests for the shared error block: 11 passed; the Cockpit mount: 3 passed; the phone mount: 2 passed.
- `VoiceSweepBudgetTests`, the class with the unexplained result: thirteen runs, four failed, all the same
  test and assertion - and the same again on the merge base in a throwaway worktree (fourteen runs, two
  failed), which is the evidence for the verdict above.

**Could not reach.** The screenshots: this seat cannot view images, so I read `PROOF.md`'s descriptions and
the rig's source instead, and verified the rig does what the proof says, including its two honestly declared
seated records. The live fleet measurement (section 7 of the mission): nothing has been merged or deployed,
so the before-and-after measurement is not mine to run; it belongs to the quality assurance pass after the
deploy. The Developer's claim that two other hosted failures and the Core abort reproduce on the base commit:
I did not re-run those; the Gate session is running the parked suites on this same merged tree, and its result
covers them. The full default local gate I did not re-run either - I ran the targeted suites above instead,
because the Gateway suite holds a machine-wide lock and another session's run on this same tree was already in
the queue ahead of me.

Nothing found means nothing found within this scope.

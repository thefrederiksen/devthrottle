# Wingman error and retry - the Developer's report

Branch `mission/wingman-error-and-retry`, pushed, no pull request opened, nothing merged, nothing deployed.
Sections 5.1, 5.2 and 5.3 are built in this one branch with the section 7 tests.

## What was built

**5.1 One retry schedule.** A failed reading is asked again after 1, 1, 1, 5, 5, 5, 30 and 30 minutes. The
schedule is written ON the stored reading (`RetriesMade`, `NextRetryAtUtc`), so it survives a restart and the
card and the sweep read the same two fields. The idle sweep carries it (`TurnVerdictService.StartDueRetries`,
called for every account and every session, not only voice sessions) under a new `Retry` trigger. It ends on
the first success, on a new turn, when the session goes back to work, and it is withdrawn when no reading is
owed any more (the judge switch is off and nobody is listening). A rate limit's named wait is honoured: the next
retry is the later of the schedule and the named wait. The in-reading second attempts are untouched.

The retry ledger in `WingmanVoiceService.cs` is deleted - the ladder, its booked tasks, its hold-off, its test
seams. There is one retry mechanism. A speech failure (the words exist, the audio could not be made) goes on
the same schedule, carried by the same sweep, with no timer of its own.

**5.2 A bad button list drops the buttons, not the reading.** Every option-list rule now removes the whole list
(menu and options), keeps the state and the label, records why in `OptionsDroppedReason` on the stored reading,
and shows it in the debug view. It is not a failure and shows no error. An unknown state word or an unreadable
reply is still a failure and is still retried.

**5.3 The Gateway rules, the clients render.** `SessionDto.WingmanError` carries the tag, a short plain reason,
which retry is next, its absolute time, how many remain, and whether the schedule is used up. The tag, the
retry line and the button live once, in `packages/client-core/src/sessions/WingmanErrorLine.tsx`, mounted on the
Cockpit roster card and the phone roster card (and the phone's child rows). It does not look at voice mode. The
client's only arithmetic is turning the Gateway's absolute time into "in 40 seconds". With nothing booked the
Gateway sends its own sentence and no time, so no client can say an attempt is coming that is not booked.
`POST /sessions/{sid}/wingman/ask-again` makes one attempt now and neither resets nor consumes the schedule.

## Simplifications made (the owner asked for simpler)

- The voice state `notNarrated` is gone; it and the failed reading are ONE state, `wingmanError`. `gaveUp` stays
  for the one case it still describes (audio that never arrived with no failure recorded), but it no longer says
  "the Gateway is still trying" and it now offers the button.
- The "explain once" marker is gone: a person asking always makes one attempt.
- Two steps left the judgement boundary (the speech re-attempt refusal and the provider deadline); the two
  boundary documents, the code comment and the audit test were brought down to six steps together.

## The section 7 tests, one sentence each

In `AFailedReadingIsAskedAgainOnAScheduleTests` (drives the real service through the sweep's own entry point
with a clock the test moves):

- `AFailedReadingOnAnUnchangedScreen_IsAskedAgainWhenDue_AndNotBefore` - the root cause: at 59 seconds nothing
  is asked, at 61 seconds the same unchanged screen is asked about again.
- `TheSchedule_IsOneOneOneFiveFiveFiveThirtyThirty_AndThenItStops` - each of the eight steps is booked at
  exactly its time, nothing runs a second early, and after the eighth nothing is booked however long it waits.
- `TheFirstSuccess_EndsTheSchedule_AndASuccessfulReadingIsNeverAskedTwice` - a success clears the schedule, and
  no trigger, the retry included, pays for a successful reading of an unchanged screen again.
- `ANewTurn_EndsTheOldSchedule_AndAFailureOnItStartsAtTheBeginning` - going back to work leaves nothing booked,
  and a failure on the new turn starts from the first minute.
- `APersonAsking_MakesOneAttemptNow_AndLeavesTheScheduleAsItWas` - a press makes one reading at once and the
  count and the booked time are the same before and after.
- `AUsedUpSchedule_StillAnswersAPress_AndStaysUsedUp` - the button still works when nothing is booked, and
  pressing it books nothing.
- `ARateLimitsNamedWait_IsHonoured_TheNextRetryIsTheLaterOfTheTwo` - a four minute wait moves the first retry
  to four minutes, and a ten second wait does not pull it earlier than one.
- `ARefusalThatIsNotAboutTheButtons_StillFails_AndIsStillRetried` - an unknown state word and an unreadable
  reply both fail, are both retried, and the third answer succeeds.
- `ABadButtonList_IsNotAFailure_TheReadingIsKeptAndNarrated_AndNothingIsRetried` - a one-option answer keeps its
  state and label, loses its buttons, records why, shows no error and books nothing.
- `AReadingWhoseWriteUpFailed_ShowsTheSameError_AndIsReadAgainOnTheSameSchedule` - when the judge answers and
  the write-up call does not, it is the same tag and the same schedule.
- `ABookedRetryThatCanNeverRun_IsWithdrawn_SoTheCardStopsPromisingIt` - with the judge switched off and nobody
  listening, the booking is removed and the card says nothing more is scheduled.
- `TheCard_SaysNothingMoreIsScheduled_ExactlyWhenNothingIsBooked` - the used-up sentence appears exactly when no
  time is booked, never otherwise, and a working session shows no error.

In `TurnVerdictContractTests`: fourteen tests, one per option-list rule, renamed from "rejected" to "drops the
buttons and keeps the reading", each asserting the list is gone, the reading is kept and the reason is recorded.

In `WingmanVoiceServiceTests` (six) and `VoiceDisplayFoldTests` (six): a speech failure goes on the same schedule
and is carried by the sweep; an account condition books nothing; the voice card says an attempt is coming only
while one is booked; the error never hides playable audio, a live attempt or an account condition.

Web: `WingmanErrorLine.test.tsx` (eleven: the countdown, "due now", the used-up sentence, one request per press,
the press surviving the row being read and moving groups), `rosterWingmanError.test.tsx` (Cockpit mount, three)
and `HomeWingmanError.test.tsx` (phone mount, two).

## The revert proof

Committed first (`da5d65c76`). With 5.1's one line removed - the `Retry` trigger made to reuse a failed reading
like every other automatic path - a full rebuild and the whole Wingman, voice and verdict filter (not a filter
naming the expected test) went red on SEVEN tests, the root-cause test among them. Restored with
`git checkout`, REBUILT, same filter: 1,267 passed, none failed.

## What was run, and what it said

- `.\scripts\test-local.ps1` - every suite green EXCEPT `CcDirector.Launcher.Tests`, two failures in
  `LauncherDeclaredCapabilitiesTests`. They ask the Windows kernel whether a launcher restart signal exists;
  a real armed launcher runs on this machine, so the answer is yes. The branch does not touch the Launcher.
- `.\scripts\test-local.ps1 -Parked` (Docker running; one hour six minutes, most of it the Gateway suite):
  `Gateway.UnitTests` 6,395 passed, none failed. `Gateway.Tests` 2,674 passed, three failed. `Core.Tests`
  aborted on one test.
  - `VoiceServingLoopIsolationTests.Voice_sweep_reaches_only_the_owning_tenants_director` and
    `HostedDirectorTunnelGovernanceTests.A_tunnel_push_reaches_the_ledger_and_the_morning_report` fail the same
    way on the base commit 74c41676d, built and run in its own throwaway worktree. Not this change.
  - `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message` failed once
    in the hour-long run and passes when its class is run alone. NOT EXPLAINED. It exercises the sweep, which
    this branch changed, so it is the one result here a reviewer should look at hardest.
  - `Core.Tests` aborted: `RepositoryRegistryConcurrencyTests` leaves a reader thread running after its
    temporary folder is deleted and that thread crashes the test host. Run alone its three tests pass and the
    host still crashes afterwards. The branch does not touch it. Because the run aborted, three Core tests did
    not execute; "904 passed" is not a statement about them.
- Web, run from each package folder with `npx vitest run` (and `npx tsc --noEmit`, clean in all three):
  `packages/client-core` 1,464 passed, `apps/cockpit` 460 passed, `apps/mobile` 103 passed.
- After the last Gateway edit (one log line) the Wingman, voice and verdict filter of `Gateway.UnitTests` was
  rebuilt and rerun green (1,267); the full parked run predates that line and the client fix.

Two runs outlasted the ten minute foreground limit and the harness moved them on by itself; both were logged to
a file and read to the end. Nothing was started hidden on purpose.

## The screenshots

`docs/missions/wingman-error-and-retry/proof/` with `PROOF.md`. Twelve pictures, Cockpit and phone: a failure
counting down, a press, a used-up schedule, a booked retry that succeeded and cleared the tag, and a dropped
button list. The judge failed for real (a rejected key, a real refused call); `PROOF.md` says plainly which two
pictures rest on a seated record and why. No Director was built or launched: the rig uses the hosted tests'
stand-in Director, so the slot and scheduled task rule did not come into play.

## Things to decide

- The error on the row rides the same account switch as the Wingman's colours (it is stamped where they are).
  An account with that switch off sees no tag on the card. Accepted here as the simpler reading; say if the tag
  should show regardless.
- A write-up call that fails after the judge answered is treated as a failed reading: same tag, same schedule.
- Every speech failure that is not an account condition goes on the schedule, including ones the old ladder
  did not retry.
- When a retry is due and the session's computer is offline, the retry stays booked and the card says "retry
  N of 8 is due now" until the computer returns. True, but it can read that way for a long time.
- The generated `schema.ts` was not regenerated (it needs a live Gateway on a fixed port); the new fields are
  typed beside their reader, the way the delivery fields already are.

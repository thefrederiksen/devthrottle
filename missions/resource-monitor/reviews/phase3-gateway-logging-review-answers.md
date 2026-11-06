# Answers: review of the five-minute resource summaries (phase 3)

The developer's answer to each finding in `phase3-gateway-logging-review.md`, decided with the Implementation
Lead. The review found nothing that blocks; every "should fix" is fixed, with a test that was run against the
previous code and seen to fail first unless said otherwise.

| Finding | Answer |
|---|---|
| 1. A Director whose clock is more than ten minutes fast loses every window | **Fixed.** A window ending ahead of the Gateway's clock is put in neither acknowledgement list; the Director keeps it and it is stored once the clock catches up. Test: `Store_AWindowFromTheFuture_IsNeitherHeldNorRefused_AndIsTakenOnceTheClockCatchesUp`. |
| 2. A window committed while the roll-up reads is left out of its hour | **Fixed.** Hours are stamped rolled one minute before the run started (`ResourceSummaryRollup.RollGrace`). Test: `Rollup_AWindowCommittedWhileTheRunWasReading_RollsItsHourAgain`. This test uses the new constant, so it could not be run against the old file. |
| 3a. A crossing's start is not checked to lie in its window | **Fixed.** Refused, with the reason. |
| 3b. Sample counts (the roll-up's weights) are unbounded | **Fixed.** A window claims 1 to 120 samples; processor and memory samples cannot exceed the samples taken. |
| 3c. A session's memory peak is not checked against the machine's total | **Fixed.** |
| 3d. A session's samples are not checked against the window's | **Fixed.** |
| 4. The ten-minute disconnect test would stay green on the wrong content | **Fixed.** Every window has its own figure and a session; each stored machine and session row is checked against its own window; the tunnel now drops on the tick a window closes (three windows held: 10:05, 10:10, 10:15); a lost acknowledgement is tested (`ALostAcknowledgement_KeepsTheWindow_AndTheResendIsStoredOnce`). |
| 5. A clock moved back keeps the fragment over the full window | **Fixed.** A copy with more samples replaces the held one; a resend of the same copy changes nothing. |
| 6. A late window can shrink an hour past retention | **Fixed.** A window is refused by its hour, not its start. |
| 7. Refusing a whole window for one impossible figure also drops its crossings and session rows | **Kept as designed.** The Implementation Lead's ruling: an impossible figure is never stored as a figure and never clamped, and the window is stored marked refused with its reason, so it is not a silent gap. Nulling only the offending figure would keep a window whose other figures came from the same faulty measurement. Raised again in the handback so the cross-platform quality assurance watches for a probe that overshoots 100 per cent by a fraction. |
| 8. "No summary in 15 minutes" blames the connection when the clock is slow | **Fixed.** Received recently but stamped long ago reads "its clock is probably behind". |
| 9. Decimal gigabytes | **Fixed.** Binary, as Task Manager, free and Activity Monitor show them. |
| 10. Pieces phase 2 must wire | **Handed to phase 2** in the handback: call `ResourceSummaryRecorder.CloseIfDue` on the sampler's tick, use `Environment.MachineName` (the Hello's name) for the recorder, and note that the outbox is in memory. |
| 11. First roll-up only an hour after start | **Fixed.** The first run is one minute after start. |
| 12. Coding-standard points | **Fixed:** the three reads log, the impossible fallback in the spare figure is gone, "ctor" is gone from the new log lines. |

## The reviewer's addendum (after the refused-window change)

| Finding | Answer |
|---|---|
| 13. A refused window drops its memory peak, so Spare over the hour can advertise room the machine did not have | **Fixed, keeping refusal per window** (the lead's ruling). Whenever any window in the last hour was refused, Spare is "cannot read" with the reason and the processor's hour peak reads "peak not known", so no believable understatement is shown. Test: `Fold_ARefusedWindowEarlierInTheHour_MakesSpareAndThePeakUnknown_NeverRoomier`, seen failing first. |
| Note: factory cost silently omits refused windows | **Fixed.** A span holding a refused window makes every factory's cost "at least". Test: `History_ARefusedWindow_MakesTheFactoryCostAtLeast`, seen failing first. |
| Note: the migration ids were regenerated | **No store holds an old id.** The ids changed only while the change was unmerged; the one statistics file a test Gateway wrote (the local outage proof, in a throwaway folder) is not reused. Every test store is built fresh from the chain. |
| Findings 1 to 4 "stand unchanged" | The addendum reviewed the third commit, before the fix commit; all four are fixed there, as answered above. |

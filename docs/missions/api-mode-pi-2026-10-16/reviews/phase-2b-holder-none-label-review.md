# Phase 2 follow-on review: a wheel holder of none outranks a held disconnect in the agent label (pull request #4174)

Mission: API mode for agent wrappers, proven on Pi (issue #4070), phase 2 follow-on, item 8: the answer to
finding 2 of the phase 2 review. Reviewer seat, a different agent family from the Developer, reading only.
Reviewed at the pull request head `5de478e69`, on the branch `api-mode-holder-none-label`, one commit ahead of
`origin/main` (`f08847c53`); it merges onto main clean. Date: 17 October 2026.

## Verdict

**Hold.** The rule the change states is right for the state finding 2 named, and the new tests pin it. But the
change is red in its own test file: two existing tests in `ApiModeSessionVerdictTests`, the second class in
`ApiModeSessionFoldTests.cs`, fail at the head, and the pull request's proof ran only the first class. They fail
because the new rule reaches a second state the change did not consider: a holder of `none` is also what every
Director reports when Pi's own process exits, and there the held disconnect is not history but the one place the
exit reason lives. The Tech Lead has to choose the words for that state before the two tests are rewritten; the
choice I recommend keeps both the current report and the reason. Finding 1 blocks the merge on its own.

## Scope: what I read, what I ran, what I could not reach

**Read, in full, on the pull request head:** the whole diff against `origin/main` (three files);
`ApiModeSessionFold.cs` in full, to see every reader of the held events and of the wheel holder, not only
`Agent`; `ApiModeSessionFoldTests.cs` in full, both classes; the fixtures the second class builds from
(`ApiModeEventTestData.RefusedByTheDirectorBeforeAcknowledgement`, `DeliveredThenFailedMidTurn`, `Gone`);
`ApiModeVerdictOnTheRosterTests.cs` where it pins the agent label; `ApiModeDisplayDtos.cs` at `AgentLabel`;
`ApiModeRowStamp.cs` as the fold's one caller; finding 2 and the Developer's answer in
`phase-2-phase-review.md`; finding 1 of `phase-2b-wheel-action-review.md`, which settled what `none` offers
as a button; the pull request description.

**Read for the Director's meaning of `none`:** on `origin/main`, `SessionConnectionModeWords.WheelHolderOf` and
its one caller in `ControlEndpoints.cs`; on the open pull request #4094 (branch `apimode/p4-wheel`), its
description and, in `ApiModeSessionBackend.cs`, every write of `_wheelHolder`: the start, `TakeWheelAsync`,
`HandBackWheelAsync`, `HandleTerminalExitAsync`, `GracefulShutdownAsync`, the terminal detach, and
`OnHostExited`.

**Ran, once, as the brief allows:** `dotnet test` on `CcDirector.Gateway.UnitTests` filtered to the two classes in
the changed test file, `ApiModeSessionFoldTests` and `ApiModeSessionVerdictTests`, after confirming no other test
host was running. Result: 35 tests, 33 passed, **2 failed**. The two failures are in finding 1; the brief named the
first class, and I widened the one run to the whole file because the fold is the type under test in both classes
and the gate's parked-test selection runs a file, not a class.

**Could not reach:** a running Gateway, Director or Pi; the Tech Lead's ruling itself, which the Developer's answer
reports and which is recorded nowhere else in the mission folder.

## The brief's five checks

**1. Is "No agent process is running" true to the Director's meaning of `none`?** Yes, in every Director that
exists. On `origin/main` an API-mode row's holder is `none` exactly when the session's status is `Exited` or
`Failed` (`WheelHolderOf`), so Pi's process is gone. On #4094 `_wheelHolder` becomes `None` in five places: when
Pi's own process exits (`OnHostExited`, every exit, a crash included); when the user's terminal exits on its own
while they held the wheel; inside a take, after `SuspendAsync` has waited for Pi to exit and before the terminal
starts; inside a hand back, after Pi could not be started again; and at shutdown. In each, no process holds the
conversation, so the sentence is true.

**Can `none` coexist with a live protocol process?** Not as a settled state. The one window is inside
`HandBackWheelAsync`: the holder stays `user` or `none` while `ResumeAsync` starts Pi, and becomes `protocol`
only after it returns, so Pi can be alive and may already have published `ConnectionReady` while a roster push
in that window would still carry `none`. The fold already answered "No agent process is running" for `none`
with a held `ConnectionReady` before this change (the `ConnectionReady` case is guarded by holder `protocol`),
so the change alters nothing there, and the switch's own push follows the completed switch. Transient and
self-correcting; not a finding.

**2. Kind and tone.** Kind is `stopped` for every `none` row, computed before and apart from `Agent`, so the
label and the kind agree. Tone for `none` is `red` only when the last turn failed (from the receipts) and
`neutral` otherwise; the `red` for a disconnected agent applies to holder `protocol` only. For the finding 2
state (no turn failed) the row is `stopped`, `neutral`, "No agent process is running". Consistent.

**3. The other fields.** `Agent` is the only reader of the held connection events. `WheelAction` on a live
`none` row offers Hand back, enabled, which the Director honours by starting Pi with nothing to stop (#4094);
"No agent process is running" beside "Hand back" is the right pair. `LastTurnEnding` reads the receipts, not the
held event, and is unchanged. The live stream's item for the same `ConnectionEnded` still reads
"Agent disconnected: <reason>" (`ApiModeLiveRuling.DisconnectedWords`); that is the stream's record of what
happened, and it is right that history reads as history there. Consistent, with the one loss in finding 2.

**4. The tests.** `Fold_AHeldDisconnectWhenNoProcessHoldsTheWheel_SaysNoAgentProcessIsRunning` pins the literal
and fails on the old order: with `none` and a latest held `ConnectionEnded`, the old `Agent` returned
"Agent disconnected: the user took the wheel". `Fold_AHeldDisconnectWhileTheProtocolHoldsTheWheel_SaysTheAgentDisconnected`
passes on both orders; it guards against over-correction, which is a fair thing for a test to do. One cosmetic
note: the two tests give the same disconnect exit code 0 and exit code 1 respectively; nothing reads it.

**5. The Developer's answer.** What it says is accurate: the rule, the two tests, and that the `user` and
`protocol` paths are unchanged. What it leaves out is finding 1 and finding 2 below: the rule also changes the
label for every Pi exit, two existing tests pin the old label for exactly that state, and the proof did not run
them. The pull request body's "ApiModeSessionFoldTests: 22 passed" is true of the class and silent about the file.

## Findings

### Finding 1 (high): two existing tests in the changed test file fail at the head, and the proof did not run them

**Where.** `ApiModeSessionFoldTests.cs`, the second class, `ApiModeSessionVerdictTests`:
`Stamp_RefusedBeforeAcknowledgement_NoTurnAndTheAgentIsGone` (asserts
"Agent disconnected: Pi exited (exit code 137)" with holder `none`) and
`Stamp_FailedMidTurn_SaysTheLastTurnFailedWithItsReason` (asserts
"Agent disconnected: Pi exited during the turn (exit code 137)" with holder `none`).

**The state that reaches it.** Both push a recorded fixture through the real ingest, store and buffer and fold
the row with `WheelHolder = none`. The fold is pure; with the new order `Agent` returns
"No agent process is running" before it reads any held event, so both assertions fail. Certain by reading, and
confirmed by the one run: 2 failed of 35.

**The harm.** The merge gate is red for this change: `scripts\select-parked-tests.ps1` picks every test class in
a file that names a type declared in a changed file, so a local gate run selects both classes of this file and
fails. The pull request body reports a green proof because it filtered to the first class by name. A change that
contradicts two tests in its own file without saying so has not been proven; it has been half-run.

Developer's answer: **Accepted, fixed.** The two tests now pass and assert the Tech Lead's words for the Pi-exit
state: "No agent process is running. The last connection ended: Pi exited (exit code 137)" and "No agent process is
running. The last connection ended: Pi exited during the turn (exit code 137)", so the exit reason still appears. The
proof now runs the whole file, both classes: `ApiModeSessionFoldTests` and `ApiModeSessionVerdictTests`, 36 passed,
0 failed.

### Finding 2 (medium): the new rule drops a held exit reason the Gateway has, because `none` is also what every Director reports when Pi's own process exits

**Where.** `ApiModeSessionFold.Agent`, the new early return for `WheelHolders.None`.

**The state that reaches it.** Pi's API-mode process exits on its own, with or without a turn open. The Director
writes `ConnectionEnded` with the reason ("Pi exited (exit code 137)") and reports the holder `none`: on
`origin/main` because the session's status is `Exited` or `Failed`, on #4094 because `OnHostExited` sets
`None`. The Gateway holds the `ConnectionEnded`. This is the state the two failing tests pin, and it is
indistinguishable, to the fold, from the state finding 2 of the phase review named (holder `none`, latest held
event `ConnectionEnded`) except by the reason text, which the fold rightly does not read.

**The harm.** Before the change the row read "Agent disconnected: Pi exited (exit code 137)"; after it,
"No agent process is running". When the crash happened mid-turn the reason survives in `LastTurnEnding`
("The last turn failed: Pi exited during the turn (exit code 137)"). When it did not (the
refused-before-acknowledgement fixture, or Pi dying while idle) the exit and its code are now on no field of the
row, although the Gateway holds them. The mission's goal is a stated reason, never a silent loss; a row that
says only "No agent process is running" over a crash the Gateway knows about is a reason withheld. The rule
"the current report wins over history" is right about the headline and wrong to discard the history: for the
finding 2 state the history was stale, for this state it is the whole explanation.

**What a fix looks like (the Tech Lead decides).** (a) The current report first and the held ending after it,
whenever a `ConnectionEnded` is the latest held connection event: "No agent process is running. The last
connection ended: Pi exited (exit code 137)". For the finding 2 state it reads "No agent process is running. The
last connection ended: the user took the wheel", which is true history under a true headline and no longer
tells the user a terminal is open. With no `ConnectionEnded` held, the plain "No agent process is running" as
now. (b) Keep the change as it is and accept the loss, rewriting the two tests to the new words and recording
in the Developer's answer that a Pi exit outside a turn now shows no reason on the roster row. Option (a) is the
one I would choose: it is one string and a two-line change in `Agent`, it satisfies finding 2 of the phase
review, and both failing tests can be rewritten to words that still carry the exit code. Either way, the two
tests in finding 1 are rewritten to the chosen words, and the roster test
`GetSessions_AnApiModeSessionThatFailedMidTurn_CarriesTheVerdict` (holder `protocol`) is unaffected.

Developer's answer: **Accepted, fixed, with the Reviewer's option (a).** The Tech Lead ruled: the current report
first, the held ending after it. With a holder of `none`, when a `ConnectionEnded` is the latest held connection event
the label is "No agent process is running. The last connection ended: <reason>", the reason verbatim as the old
disconnected label used it; otherwise "No agent process is running". Kind stays `stopped`. New test:
`Fold_AHeldReadyWhenNoProcessHoldsTheWheel_SaysNoAgentProcessIsRunningWithNoEnding`.

### Finding 3 (low): two comments in the fold now describe the old order

**Where.** `ApiModeSessionFold.cs`: the section comment above the `Agent` call, "the agent process: the latest
connection event held here, else what the wheel supports", and the third bullet of the class summary, which says
whether the agent is connected comes from "the latest `ConnectionReady` or `ConnectionEnded` this Gateway holds".

**The harm.** Both now read as if the held event is consulted first for every holder. The next reader of the fold
learns the rule from these comments before the code, and learns the wrong one. One sentence each, saying that a
holder of `user` or `none` answers before any held event is read.

Developer's answer: **Accepted, fixed.** The section comment above the `Agent` call and the third bullet of the class
summary now say that a holder of `user` or `none` answers before any held event is read, and that for `none` a held
`ConnectionEnded` follows as history.

### Finding 4 (low): the record says less than the change does

**Where.** The Developer's answer under finding 2 in `phase-2-phase-review.md`, and the pull request body's proof.

**The harm.** The answer describes the rule for the state finding 2 named and reports the proof as the class
count. It does not say that the rule also changes the label for every Pi exit, nor that two existing tests pin
that label. Whichever option the Tech Lead takes in finding 2, the answer should name the second state and the
ruling on it, so the next seat does not rediscover it as this review did.

Developer's answer: **Accepted, fixed.** The Developer's answer under finding 2 in `phase-2-phase-review.md` now
names both states a holder of `none` covers - the user's terminal exiting, and Pi's own process exiting - the ruling
on each, and the tests that pin them. The pull request body says the same and reports the whole-file count.

## Summary

| Finding | Severity | Blocks the merge |
|---|---|---|
| 1. Two tests in `ApiModeSessionVerdictTests` fail at the head; the proof ran only the first class | high | yes |
| 2. A holder of `none` also means Pi exited; the held exit reason is dropped from the row | medium | yes, as the cause of 1: the Tech Lead's choice of words decides how the tests are rewritten |
| 3. Two comments in the fold describe the old order | low | no |
| 4. The Developer's answer and the pull request proof leave out the Pi-exit state | low | no |

The verdict is **hold** until the two tests pass under a recorded ruling for the Pi-exit state, and the
Developer's answer names that state.

## Re-review

Re-reviewed at the pull request head `ffcc72792`, one commit past the reviewed head (`git diff 932fbed17..HEAD`:
four files, the fold, its test file, this record and the phase review's Developer's answer). `origin/main` is still
`f08847c53`; the branch is zero commits behind it and merges clean. Date: 17 October 2026. A different Reviewer session
from the first review, in the same agent family, reading only.

### Verdict

**Approve.** Each of the four findings is fixed in the code, the tests and the record as the Developer's answers say,
and nothing else that reads the label, the kind, the tone, the wheel action or the live stream changed or needed to.

### What I read and ran

**Read, in full, on the pull request head:** the whole diff since the first review; `ApiModeSessionFold.cs` in full
(every reader of the holder and of the held events: `Agent`, the kind, the tone, `WheelAction`); both classes of
`ApiModeSessionFoldTests.cs` with their fixtures; `ApiModeLiveRuling.DisconnectedWords` and its one caller in the live
stream; `ConnectionEnded` in `SessionEvents.cs` (`Reason` is a required, non-nullable string, so the new suffix can never
read "ended: " with nothing after it); `ApiModeVerdictOnTheRosterTests.cs` wherever it asserts the agent label or pushes a
holder of `none`; the `AgentLabel` summary in `ApiModeDisplayDtos.cs`; the Developer's answer under finding 2 of the phase
review; the pull request body; the commit message (no attribution line of any kind).

**Searched the whole repository** for every reader of "No agent process is running", "Agent disconnected",
"The last connection ended" and `agentLabel`: the only readers are the fold, the live ruling, the Gateway unit tests and
the mission records. No web client (`packages`, `apps`) reads the API-mode verdict yet beyond the generated schema, so
no renderer holds a conditional on the old words. The `AgentLabel` hits in the Cockpit and the Avalonia app are a
different field (the fleet manager placement and the session's agent kind), not this one.

**Ran, once, after confirming no other test host was running:** `dotnet test` on `CcDirector.Gateway.UnitTests` filtered
to `ApiModeSessionFoldTests` and `ApiModeSessionVerdictTests`. Result: **36 passed, 0 failed, 0 skipped**, the same
count the pull request body reports.

**Could not reach:** a running Gateway, Director or Pi. I did not re-run `ApiModeVerdictOnTheRosterTests` or
`ApiModeLiveRulingTests` (one run only, by the brief); I checked them by reading instead, below.

### Finding by finding

**Finding 1 (two tests in the changed file failed; the proof ran one class). Fixed.** Both tests,
`Stamp_RefusedBeforeAcknowledgement_NoTurnAndTheAgentIsGone` and `Stamp_FailedMidTurn_SaysTheLastTurnFailedWithItsReason`,
now assert the ruled words with the exit code still present, and they pass in my run of the whole file. The pull request
body now reports the whole file, both classes, and the count matches.

**Finding 2 (the held exit reason was dropped for a holder of `none`). Fixed, with option (a) as ruled.** `Agent` computes
the latest held connection event before the `none` check and returns "No agent process is running. The last connection
ended: " plus the reason verbatim when that event is a `ConnectionEnded`, else the plain headline. The reason reaches the
label unchanged, as the old disconnected label carried it. Three tests pin the three cases: a held ending with holder
`none` (the terminal state), a held ready with holder `none` (no ending, new test), and the two Pi-exit fixtures through
the real ingest and store. The `user` path still returns before any held event is read; the `protocol` path uses the same
`latest` expression it did before, only moved up, so its words are unchanged, which
`Fold_AHeldDisconnectWhileTheProtocolHoldsTheWheel_SaysTheAgentDisconnected` pins.

**Finding 3 (two comments described the old order). Fixed.** The class summary's third bullet and the section comment
above the `Agent` call both say a holder of `user` or `none` answers before any held event is read, and that for `none` a
held `ConnectionEnded` follows as history. The comment inside `Agent` says why.

**Finding 4 (the record said less than the change). Fixed.** The Developer's answer under finding 2 of the phase review
names both states a holder of `none` covers, the ruling on each, the five tests that pin them, and the whole-file proof.
The pull request body says the same, and the commit message records the ruling as well.

### Nothing new broken

- **Kind.** `stopped` for every `none` row, computed apart from `Agent`; untouched by this commit.
- **Tone.** For `none`, `red` only when the last turn failed, else `neutral`; the `red` for a disconnected agent still
  applies to holder `protocol` only. `Stamp_FailedMidTurn_SaysTheLastTurnFailedWithItsReason` asserts `red` and passes.
- **Wheel action.** `WheelAction` is unchanged in this commit; a live `none` row still offers Hand back, enabled, which
  sits well beside "No agent process is running". The roster test `GetSessions_AnEndedSessionThatNobodyHolds_ShowsNoWheelButton`
  pushes holder `none` with a held ready and asserts only that the button is null; the label it would carry is the plain
  headline either way.
- **The live ruling.** `ApiModeLiveRuling.DisconnectedWords` is unchanged and the fold still calls it for the `protocol`
  path. The live stream's item for a `ConnectionEnded` still reads "Agent disconnected: <reason>", which is right for a
  stream of what happened; `ApiModeLiveRulingTests` asserts exactly that and nothing in it changed.
- **The roster.** `GetSessions_AnApiModeSessionThatFailedMidTurn_CarriesTheVerdict` pushes the default holder, `protocol`,
  so its "Agent disconnected: Pi exited during the turn (exit code 137)" is unaffected, as the first review said.

### Observation (not a finding)

The `AgentLabel` summary in `ApiModeDisplayDtos.cs` lists three example forms ("Agent connected (model)",
"Agent disconnected: ...", "Not known ...") and names neither "The agent runs in your terminal" nor the two
"No agent process is running" forms. That was already so before this pull request, and the summary presents itself as
examples, so it is not a hold. The next seat that touches the contract could complete the list.

### Summary

| Finding | Fixed in code | Fixed in tests | Fixed in the record |
|---|---|---|---|
| 1. Two tests failed; the proof ran one class | yes | yes, 36 of 36 pass in my run | yes, the body reports the whole file |
| 2. The held exit reason was dropped | yes, option (a) | yes, three tests | yes, the ruling is recorded |
| 3. Two comments described the old order | yes | - | - |
| 4. The record left out the Pi-exit state | - | - | yes, both states named |

The verdict is **approve**: the pull request may merge.

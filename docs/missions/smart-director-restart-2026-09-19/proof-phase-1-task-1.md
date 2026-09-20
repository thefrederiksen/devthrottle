# Proof - Smart Director Restart, phase 1, task 1

The contract types, the record marks, the two new session verbs, the new request text.

Branch `smart-restart/p1-contracts`, code commit `52e4b3228`, cut from `origin/main` at `2092941b6`.
`origin/main` has moved two commits since (`fa94b0b07`, `ff6933f55`, both about the morning report);
neither touches a file this task touches, so the branch was not rebased.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

| When | Passed | Failed | Total |
|---|---|---|---|
| Baseline on untouched `origin/main` at `2092941b6` (the Tech Lead's figure; not re-measured by me) | 418 | 0 | 418 |
| After, on `52e4b3228`, built from source in the same command | 475 | 0 | 475 |

475 - 418 = 57, and 57 is the number of new test cases counted by hand below. No existing test was
changed or removed.

The workspace tests (`--filter "FullyQualifiedName~Workspace"`): 182 passed, 0 failed, of which 17 are
new. I did not measure the workspace count before the change; 165 is arithmetic, not a run.

The whole `CcDirector.Gateway.UnitTests` project, run once because this task touches the Gateway's
validation: 6,465 passed, 1 failed, 8 skipped, 6,474 in all, 2 minutes 30 seconds. The one failure is
`GovernanceAuditLogTests.The_trail_is_tenant_scoped`. Run on its own straight afterwards, that class
passes 11 of 11. The file names no workspace, drain or smart shutdown type. I did NOT establish why it
failed in the full run and I did not run the full project on untouched `origin/main`, so I cannot say
whether it fails there too. It is reported, not explained.

Everything ran in the foreground.

## What was built

1. **Contract types** - `src/CcDirector.ControlApi/SmartRestart/ISmartShutdown.cs` and
   `ISmartShutdownRun.cs`, namespace `CcDirector.ControlApi.SmartRestart`. Every interface, record and
   enum of `phase-1-interface.md` sections 2 and 3, with the names, members and order written there.
   Types only: no engine, no `ControlApiHost.CreateSmartShutdown()`. `SmartShutdownRequest` refuses a
   time that is not 5, 10, 15, 30 or 60 minutes with `ArgumentOutOfRangeException`, naming what was sent
   and what may be sent. The refusal is on the VALUE, so a copy made with `with { TimeAllowed = ... }`
   is refused too.
2. **Record marks** - `WorkspaceDtos.cs` and `WorkspaceValidation.cs`:
   - drain state `ended-at-limit` (`WorkspaceDrainStates.EndedAtLimit`, added to `All`);
   - `WorkspaceDocument.ShutdownKind`, nullable, closed list `WorkspaceShutdownKinds`
     (`smart-shutdown`, `ignore-all`). `WorkspaceOrigins` is untouched: such a record is still
     `captured`;
   - `WorkspaceDocument.CancelledAtUtc`, nullable. One field says both that it was cancelled and when.
   Two rules I added beyond the letter of the mandate, in the spirit of that file: an AUTHORED workspace
   may carry neither field (it is not the record of a run), and `cancelledAtUtc` is refused unless
   `shutdownKind` is `smart-shutdown` (an ignore-all shut down cannot be cancelled). Say if you want
   either removed.
3. **Two verbs on `IDrainSessionControl`**:
   - `InterruptAsync(sessionId)` answers a `DrainDelivery`. The real one calls
     `SessionCommandExecutor.InterruptAsync` - the same verb the Gateway's interrupt route reaches - which
     goes through `Session.InterruptAsync` to the agent driver;
   - `EndAsync(sessionId, reason)` answers a new `DrainEnd` (ended, gone, or could not with the reason).
     The real one calls `SessionCommandExecutor.KillAsync`, the Director's existing stop verb, which
     calls `SessionManager.KillSessionAsync`, checks the process really went, and removes the row. No
     second way of killing was written.
   Fakes on `FakeSessionControl` record every CALL (`Interrupted`, `Ended`), whatever came of it, and
   can be told to refuse. The harness `tools/harnesses/drain-index-diff` implements the seam too; there
   both verbs throw, because that harness runs the older drain. The class comment on
   `SessionManagerDrainControl` is rewritten to say what is true now.
4. **Request text** - `DrainMessages.SmartShutdown`, `DrainMessages.HandOverNow`,
   `DrainMessages.RestartIsOff`. `DrainMessages.Drain` has no changed line. `DrainReportBlock.cs` is
   untouched.

## What I found about older and newer readers

- Every extensible object in the document carries a `[JsonExtensionData]` bag called `Unknown`. A build
  from before the marks reads `shutdownKind` and `cancelledAtUtc` into the DOCUMENT's bag and writes them
  back out as ordinary top-level properties, so this build reads them again, typed. Tested.
- The store keeps the CALLER's copy of the document-level bag on an ordinary save
  (`WorkspaceStore.ApplyStoredProvenance`, the "KNOWN LIMIT" comment). So an older GATEWAY that is sent
  the two new document fields stores them and hands them back. They survive an older Gateway.
- The SEAT's bag is different: the store restores it from the stored copy on every save. I added no seat
  field, so nothing of mine depends on that.
- `ended-at-limit` is a value of an existing string field, so an older READER keeps it as text. But an
  older GATEWAY refuses it on save (`drainState must be one of ...`). That is the limit the interface
  document already reports in its section 5: a real Director can only save that state once a Gateway
  carrying it is deployed.
- A record written before the marks existed reads with both fields null and is accepted unchanged. Tested.
- One thing the engine task must know: on a captured record the store PUTS BACK `ClaudeSessionId` from the
  stored copy on every save. "The conversation id is already on the seat" is therefore true only if the
  CAPTURE read it; the Director cannot add it later through a save. Tested from the good side (the id
  survives a writer that leaves it off).

## Each new test, in one sentence

`SmartShutdownContractTests` (18 cases)
- `Allowed_Times_AreFiveTenFifteenThirtyAndSixtyMinutes`: the allowed list is exactly those five, in order.
- `Default_Time_IsTenMinutesAndIsOneOfTheAllowedTimes`: the default is ten minutes and is itself allowed.
- `SmartShutdownRequest_AnAllowedTime_IsAccepted` (5): each allowed time builds a request that reads back.
- `SmartShutdownRequest_ATimeThatIsNotAllowed_ThrowsNamingWhatWasSentAndWhatMayBe` (6): 0, 1, 7, 20, 90
  and minus 10 minutes are refused and the message names both the value and the five allowed ones.
- `SmartShutdownRequest_ATimeBetweenTwoAllowedOnes_Throws`: ten minutes and one second is refused.
- `SmartShutdownRequest_CopiedWithATimeThatIsNotAllowed_Throws`: a `with` copy cannot get round the refusal.
- `SmartShutdownRequest_TwoRequestsSayingTheSameThing_AreEqual`: the refusal did not break record equality.
- `Enums_CarryExactlyTheMembersTheInterfaceDocumentNames_InItsOrder`: the four enums match the document.
- `Interfaces_CarryExactlyTheMembersTheInterfaceDocumentNames`: the two interfaces have those members only.

`DrainSessionControlSmartShutdownVerbsTests` (9 cases) - the REAL seam over a REAL `SessionManager`; only
the agent process is a stand-in backend.
- `InterruptAsync_ALiveSession_SendsTheAgentsInterruptKeyAndAnswersDelivered`: the interrupt key reaches
  the session's backend and the session is still present.
- `InterruptAsync_AnAgentWithNoSafeInterrupt_AnswersCouldNotWithTheDriversOwnReason`: a Pi session answers
  "could not" with the driver's words, not "gone", and nothing is typed into it.
- `InterruptAsync_ASessionThatIsNotHere_AnswersGone` (2): an unknown id and a malformed id both answer gone.
- `EndAsync_ALiveSession_StopsItsProcessAndTakesItOffTheDirectorAtOnce`: the backend is asked to stop and
  the session is absent on the very next question.
- `EndAsync_ASessionMidTurn_IsEndedAnyway`: a session in the working state is ended all the same.
- `EndAsync_ASessionThatIsNotHere_AnswersGoneAndTouchesNothing` (2): gone is kept apart from ended, and
  the live session beside it is untouched.
- `EndAsync_AStopTheBackendRefused_AnswersCouldNotAndLeavesTheSessionOnTheDirector`: a stop that fails
  answers with the reason and the session stays listed.

`DirectorDrainTests.Drain_OnTheOlderPath_NeverInterruptsAndNeverEndsASession` (1 case): the older drain,
run on the rig over a clean, a blocked, a silent, a wedged and a never-reaped session, calls neither new
verb and never writes `ended-at-limit`; the test first asserts the run really met all five cases.

`DrainMessagesSmartShutdownTests` (12 cases)
- `SmartShutdown_Message_SaysHowLongThereIsAndToWriteTheNextActionFirst`: the minutes, the next-action-first
  instruction, the Director, the reason and the path are all in it.
- `SmartShutdown_Message_NeverPromisesTheSessionWillNotBeKilled`: none of the older promise is in it.
- `SmartShutdown_Message_KeepsThePhrasesLearnedOnARealFleet`: START NOTHING NEW, NO SECRETS and the named
  skill are still in it.
- `SmartShutdown_Message_DescribesTheClosingBlockInExactlyTheOlderMessagesWords`: the closing block
  paragraph and the paragraph for a lead are character for character the older message's.
- `SmartShutdown_Message_ToASessionWithNobodyUnderIt_DoesNotMentionSeatsReportingToIt`: as named.
- `Drain_OlderMessage_StillPromisesNothingIsForced`: the older message still carries its promise and none
  of the new wording.
- `HandOverNow_Message_IsTheShortSecondRequest_NextActionFirst`: the phrase, the minutes left, the path and
  the block are in it and it is under half the first message's length.
- `HandOverNow_Message_IsTellableApartFromTheFirstRequest`: it does not carry START NOTHING NEW, which is
  how the rig recognises the first request.
- `HandOverNow_Message_WithUnderAMinuteLeft_SaysOneMinuteNeverZero` (3): it never says zero minutes.
- `RestartIsOff_Message_SaysTheRestartIsOffAndToCarryOn`: as named, with and without a Director name.

`WorkspaceSmartRestartMarksTests` (17 cases) - all through the real `WorkspaceStore.Save` on SQLite.
- `Save_ASeatEndedAtTheLimit_IsStoredWithItsConversationIdStillOnTheSeat`: the state is stored and the
  conversation id is there even when the writer left it off.
- `Save_ADrainStateNobodyKnows_IsStillRefusedAndTheMessageNamesTheNewOne`: the list is still closed.
- `Save_AKnownShutdownKind_IsStoredAndReadBack_AndTheRecordIsStillACapture` (2): both kinds round trip and
  the origin stays `captured`.
- `ShutdownKinds_AreExactlyTheTwoTheWayUpSearchesBy`: the closed list is those two strings.
- `Save_AShutdownKindNobodyKnows_IsRefusedNamingTheKnownOnes` (4): wrong spellings and the empty string
  are refused and nothing is stored.
- `Save_ARecordWithNoShutdownKind_IsStoredAsBefore`: an ordinary drain record is unaffected.
- `Save_ACancelledSmartShutdown_KeepsWhenItWasCancelled`: the time is stored.
- `Save_CancelledOnARecordThatIsNotASmartShutdown_IsRefused` (2): with no kind and with ignore-all.
- `Save_AnAuthoredWorkspaceClaimingAShutdown_IsRefusedNamingBothFields`: as named.
- `RoundTrip_ThroughAReaderFromBeforeTheMarks_LosesNoneOfThem`: newer to older to newer keeps all three.
- `RoundTrip_ARecordWrittenBeforeTheMarksExisted_ReadsWithNoneOfThemAndIsAccepted`: older to newer.
- `RoundTrip_AMarkFromANewerBuildStillSurvivesBesideTheNewOnes`: a field from the future still survives.

## The revert proofs

Done AFTER the code was committed, by deleting or replacing the real line (never a dead branch, because
warnings are errors here), each time with a full build and the WHOLE check, not a narrowed filter. The
tree was restored with `git checkout -- .`, confirmed clean against `52e4b3228`, REBUILT, and the check
run again: 475 passed, 0 failed.

Round one, all four items at once: **30 failed, 445 passed, 475 in all.**

| What was undone | What went red |
|---|---|
| Item 1: `RequireAllowed` no longer refuses | 8: the six refused times, the in-between time, the copy |
| Item 2: `EndedAtLimit` out of `All`; the `shutdownKind` list check removed | 7 in `WorkspaceSmartRestartMarksTests` |
| Item 3: the real seam no longer reaches the interrupt verb or the stop verb | 7 of 9 in `DrainSessionControlSmartShutdownVerbsTests` |
| Item 3: the older drain made to call `EndAsync` on a session it gave up on | `Drain_OnTheOlderPath_NeverInterruptsAndNeverEndsASession`, and three EXISTING tests that hold "keeps running" |
| Item 4: `SmartShutdown` made to return the older message | 4 in `DrainMessagesSmartShutdownTests` |

Round two, the two rules round one did not touch (the cancel rule, the authored rule): **3 failed, 472
passed**: both cases of `Save_CancelledOnARecordThatIsNotASmartShutdown_IsRefused` and
`Save_AnAuthoredWorkspaceClaimingAShutdown_IsRefusedNamingBothFields`.

New tests that stayed GREEN under the mutations, and why that is right: the accepted-time cases, the
equality, enum and interface cases (nothing they watch was undone); the two `EndAsync` gone cases (the
presence question comes before the stop); the closing-block-equality case (returning the older message
makes the paragraphs equal by construction); the older-message case, the two other `HandOverNow` cases and the
`RestartIsOff` case; the accepted-record and round-trip cases that do not use `ended-at-limit`. None of
these was claimed as a revert proof.

## What I could not reach

- **Nothing in the product calls the new types, verbs or messages yet.** The engine is the next task. So
  no test here watches the real CALLER of `SmartShutdown`, `HandOverNow`, `RestartIsOff`, `InterruptAsync`
  or `EndAsync`; the verb tests watch the real path BENEATH the seam, the message tests watch the words.
  The engine's tests have to hold that they are sent.
- **The older reader is a stand-in.** The older `WorkspaceDocument` type no longer exists in this tree, so
  the round trip uses a small test class with the same unknown-field bag. It shows the mechanism the older
  build relies on, not the older build.
- **No real agent process and no real Director.** The interrupt test proves the interrupt key reaches the
  backend, not that an agent mid-turn stops. The mission document lists that as unverified too.
- **`EndAsync` where the process will not die** is covered only through a backend that reports a failed
  shutdown, not through a real stuck process; `SessionCommandExecutorLivenessTests` already covers that
  beneath the seam. The pooled-worktree branch of `EndAsync` (the stop succeeds and the row is kept) has
  no test: building that case needs a cc-worktrees pool.
- **Pi cannot be interrupted** through the existing interrupt path: its driver refuses, by design, and the
  seam hands the refusal back. I did not add a soft stop (Escape) in its place, because that would be a
  second path chosen silently. The engine has to decide what an uninterruptible session gets at two thirds.
- **The operating system shutdown record** (`RecordAndLetEndAsync`) has no `shutdownKind` of its own. The
  mandate named two kinds and the list is closed, so a third needs a Gateway carrying it. Raised here for
  the Tech Lead rather than invented.
- **`WorkspaceSummaryDto` does not carry `shutdownKind`.** The workspace list is built from table columns,
  not from the stored document, so the way up cannot filter the LIST by it without a column or a read of
  each document. That is phase 3's to decide.
- The one full-project failure above, unexplained.

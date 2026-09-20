# Answers to review-phase-1-4.md - Smart Director Restart, phase 1, task 4

Written by the Developer seat that finished task 4. The seat that built it, and the seat that answered
the two findings, both died on the account's monthly spend limit at 06:47 on 20 September 2026; this
seat stands in for them, as law 11 allows. A Reviewer advises; it does not command. Both findings are
ACCEPTED, and both were already answered in code by the seat before me, in commit `9b297e1f6`. I did
not take that commit's message as evidence: I read it against each finding myself, ran the check, and
ran a revert proof on each answer to show the new tests can fail.

Branch `smart-restart/p1-cancel-ignore-restart`, merged with `origin/main` at `217b79f63` (no rebase,
no force push). The merge had one conflict and one thing the compiler found; both are recorded at the
end of this file.

## The check, run by me on the merged result

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Every run in the foreground, built from source in the same command, never `--no-build`. I read the
count, not the colour.

| When | Passed | Failed | Total |
|---|---|---|---|
| Baseline: untouched `origin/main` at `217b79f63`, my own run in its own worktree | 580 | 0 | 580 |
| After: this branch merged with that `origin/main` | 606 | 0 | 606 |
| Revert proof one (the one further pass removed), REBUILT | 605 | 1 | 606 |
| Revert proof two (the launcher step's catch removed), REBUILT | 604 | 2 | 606 |
| Both restored and REBUILT in the same command | 606 | 0 | 606 |

606 - 580 = 26: the 22 cases task 4 built, plus the 4 cases that answer these two findings. The baseline
is 580 and not the 518 the Tech Lead measured on 20 September because phases 2, 3 and 4 merged in
between, and their tests match the same filter.

Also run by me on the merged result:

- `RetiredMessagingWordsTests` in `CcDirector.Core.UnitTests`: 5 passed, 0 failed.
- `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`: 47 passed,
  0 failed. Phase 2's screens are on main now, so this says the merge did not break them.
- `dotnet build src/CcDirector.Avalonia`: 0 warnings, 0 errors.

## Finding 1 - a session that appears during "Shut down and ignore all sessions" is in neither the record nor the end list

**ACCEPTED.** The review's own words allowed either answer - end what is left after the loop, or accept
the gap knowingly. It is ended.

What changed, in `DirectorSmartShutdown.ShutDownIgnoringAllAsync`:

- The end loop became a local method and is run TWICE. The first pass walks the live list as before. The
  second walks the live list again and ends only what was not in the first list. One further pass, never
  a loop: a Director that keeps producing sessions is not something a tenth pass settles, and a loop
  would be a Director that can never close.
- A session that was ended although the record does not name it is NAMED: in the new
  `IgnoreAllResult.Detail`, and in the record's own problems, through a new optional first parameter on
  `DirectorDrain.SaveSessionsEndedAsync`. So such a session leaves a trace in the two places the owner
  and the way up read, which is the harm the review described - a session dying with no trace at all.
- A session that would not end is named the same way, with the reason the stop path gave. It used to go
  only to the log.
- Only a record that WAS written can be missing a session, so the naming is skipped when the record was
  refused; `RecordRefusal` already says nothing names any of them.

The test that proves it, in `SmartShutdownCancelIgnoreRestartTests`:
`ShutDownIgnoringAllAsync_ASessionThatAppearsAfterTheRecordIsWritten_IsEndedToo_AndTheResultSaysItIsInNoRecord`.
Its fake session control starts a session called `late` at the moment the first session is ended - after
the record was written AND after the list to end was taken, which is exactly the window the review
described. It asserts the Director is left empty, three sessions were ended, `late` was ended once, the
record's first save does not name it, and both the result's `Detail` and the record's problems say it
was ended and is in no record. A second test,
`ShutDownIgnoringAllAsync_ASessionThatWillNotEnd_IsNamedInTheResultWithTheReason`, holds the other half
and also holds that a clean run says nothing at all (`Detail` is null).

**Revert proof.** With the two lines of the further pass deleted and REBUILT, the whole check run with no
narrowed filter: 1 failed, 605 passed - the appears-after-the-record test, and only it. Restored with
`git checkout --`, REBUILT, whole check: 606 passed, 0 failed.

## Finding 2 - a Gateway failure at the launcher ask ends a finished restart run as `Failed` instead of `RestartRefused`

**ACCEPTED.** `SmartShutdownRun.AskLauncherAsync` now catches what `DirectorLauncherRestartStep.RunAsync`
throws and finishes `RestartRefused` with the error's own words and the sentence that the record stands
and is offered when the Director is next started - the same sentence the null-client case a few lines
above already gave for the same fact. Nothing is retried and nothing is hidden.

It says WHICH of the two failures happened, because the owner's next action differs. The step is given a
`beforeAsk` callback, which it runs after the capability re-check passes and before the launcher is
asked, so the run knows whether the ask went out:

- the re-check threw: "The launcher was not asked, because the machine could not be checked again
  first", with the error's words;
- the ask threw: "The launcher was asked ... but its answer never came back", with the error's words and
  the plain warning that the launcher may still act on it, so restarting by hand may not be needed.

The test that proves it:
`Start_WithTheRestartPurpose_AGatewayThatDiesAtTheLauncherStep_EndsRestartRefusedWithTheReason_AndTheRecordStands`,
a theory with both cases. It runs the real engine over the real drain to an emptied Director against a
Gateway that throws at the re-check, or answers the re-check and throws at the ask. It asserts the
outcome is `RestartRefused`, that the words carry the error's own message and the right one of the two
sentences, that the words do NOT say "stopped on an error", that the Director is empty, that the saved
record is uncancelled with its seat still decided `restore`, and that the phases end Restarting then
Finished with no run left active.

**Revert proof.** With the catch removed and the step called as before (`beforeAsk` null) and REBUILT, the
whole check with no narrowed filter: 2 failed, 604 passed - both cases of that theory, and only them.
Restored with `git checkout --`, REBUILT, whole check: 606 passed, 0 failed.

## The review's notes, which it did not raise as findings

- **The busy message of `ShutDownIgnoringAllAsync` pointed at a button the older drain does not have.**
  Changed. It now says a smart shutdown offers "Shut down now" and a drain has no such button and has to
  finish first.
- **The stale comment in `Start_AcrossOneRealRun_...`** ("cancel is never offered because it is not
  built"). Changed to the true reason: that test's engine is wired with no restore, and a run that
  cannot bring a session back must not offer a cancel. No test body or assertion was touched.
- **The count label can go down during `Cancelling`.** Left as it is. It agrees with the rows and with
  the phase label, and a brought-back session genuinely is no longer shut down; nothing untrue reaches
  the screen.
- **Two doors racing.** Left as it is, for the reason the review gives: every entry point refuses first
  where it can, and the interleaving ends with one honest refusal and no session harmed.
- **The null-forgiving operator in the new lines.** Left as it is; it matches the file it is in and each
  use is guarded by the check on the line above it.
- **The engine reads the host's current Gateway client at the moment of use.** No change: that is the fix
  the previous review asked for and it is what the code does.

## What the merge with `origin/main` needed, and why

Phases 2, 3 and 4 merged while this branch waited, so this is recorded rather than left for the next
reader to rediscover.

- **One conflict, in `GatewayClient.cs`:** this branch and main each added a new method in the same
  place - `RequestWorkspaceRestoreAsync` (the restore door this branch asks for a cancel) and
  `PassDevReportsAsync` (phase 4's dev report inheritance). Both were kept, whole and unchanged. The
  merge's difference against each parent is exactly the other side's one method, and nothing else.
- **One thing the compiler found:** phase 4 added `PassDevReportsAsync` to `IRestoreGateway`, so this
  branch's fake Gateway in the cancel tests had to answer it. It records the request and answers as a
  Gateway with nothing to move does - none passed. It does NOT throw "not reached": the real restore now
  asks for every seat it brings back and swallows a failure into that seat's outcome sentence, so a
  throwing fake would quietly write a failure into the cancel tests' own rows.

## What these answers do NOT cover

- **Nothing shows `Detail` yet.** No caller outside the engine calls `ShutDownIgnoringAllAsync` on main
  today: phase 2's dialog returns the choice, and nobody acts on it. Whoever wires that button must show
  `Detail` when it is not null, as `phase-1-interface.md` section 2 says. Until then the sentence about a
  session that was ended and is in no record lives only in the record and the log.
- **Still no real Gateway, no real Director, no real launcher.** Both answers were proved on the rig, as
  the whole of phase 1 was. Phase 5 is where a real run happens.
- **The gap is narrowed, not closed.** A session that appears AFTER the second pass is still ended by the
  closing application with no trace. That is knowingly accepted: the alternative is a loop with no end.
- **The parked suites were not run**, and neither was the whole `CcDirector.Gateway.UnitTests` project on
  the merged result. The task 4 proof records the whole-project run on the unmerged code (6,569 passed).

# Review - Smart Director Restart, phase 1, task 4: cancel, ignore all, the operating system record, the restart purpose

Reviewed commit `8d2101caf` in this worktree, a detached checkout of the Developer's branch
`smart-restart/p1-cancel-ignore-restart`; the code commit is `5000c28ed`. No tracked file in this worktree was
changed by me; this file is the only thing I wrote.

## Scope

What I read, in full:

- The whole diff `git diff origin/main...HEAD`: `ControlApiHost.cs`, `GatewayClient.cs`, `DirectorDrain.cs`,
  `DirectorRestore.cs`, `IDrainSessionControl.cs`, `DirectorLauncherRestartStep.cs` (new),
  `DirectorRestartCycle.cs`, `DirectorSmartShutdown.cs`, `GatewaySmartShutdownBringBack.cs` (new),
  `DrainTestRig.cs`, `DirectorRestoreTests.cs`, `SmartShutdownCancelIgnoreRestartTests.cs` (new),
  `tools/harnesses/drain-index-diff/Program.cs`, and the Developer's proof.
- Around the diff, to check the claims: `DirectorDrain.cs` in the areas the diff touches and the whole smart
  path through it (the asking loop, the collecting loop, the two-thirds stage, the limit method
  `EndEverySessionStillPresentAsync` from its first line to its last, `EndSessionsThatAreNotSeatsAndCheckEmptyAsync`,
  `Emit`, `RowFor`, `MarkGone`, `RecordClosed`, `RecordWithoutHandoversAsync`, `SaveSessionsEndedAsync`),
  `DirectorSmartShutdown.cs` in full (the engine, the run, `Choose`, `RunAsync`, `AskLauncherAsync`, `Finish`,
  `Publish`), `DirectorRestore.cs` in full around `ResolveOwner`, `StillRunning`, `RunStepsAsync` and the
  spawn/token sequence, `SessionManagerDrainControl.CancelDeletion` and the real `Session.CancelDeletion`
  behind it (`Session.cs`), `ControlApiHost.cs` (`CreateSmartShutdown`, `DispatchTunnelCommandAsync`,
  `StartRestartCycle`, `StartWorkspaceRestoreAsync`, `RestartDrainStep`, `JudgeRestartEligibility`),
  `GatewayClient.RequestWorkspaceRestoreAsync`, and, because the whole real-Director cancel rests on them,
  the Gateway side: `WorkspaceEndpoints.cs` (the restore door and the marks route, in full),
  `MachineEndpoints.cs` (the launcher relay route, the slot guard, the only-if-empty flag) and
  `SessionKeyGuard` (the class, to check the credential question). Also `DrainMessages.RestartIsOff`,
  `SmartShutdownWords`, `ISmartShutdown.cs` side by side with `phase-1-interface.md` position by position,
  the mission document (sections 5, 7, 8, 10), the Developer's mandate, `review-phase-1-3.md`, and the
  Developer's proof, which I read as testimony and did not trust.

What I ran, all in the foreground in this worktree, none of it piped through `tail` or `head`:

- The mission's check, built from source in the same command:
  `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
  - **540 passed, 0 failed**, matching the prompt that opened me, the Tech Lead's baseline of 518 plus 22
  new cases, and the Developer's proof.
- `dotnet build src/CcDirector.Avalonia` - 0 warnings, 0 errors. The check builds neither the Avalonia
  project nor the harness; the diff touches the project both reference.
- `dotnet build tools/harnesses/drain-index-diff` - 0 warnings, 0 errors.
- A byte count of the whole diff for anything above 127: zero hits.

What I could not reach:

- **The two revert proofs were not re-run.** Both mutate tracked files and my mandate forbids changing any
  tracked file in this worktree. I read them as testimony and checked them against the test code: the
  cancelled-mark mutation reddens exactly the tests that read the saved record's cancelled mark and the
  restore's new owner rule, and the order mutation reddens exactly the test that reads the journal's first
  entry. Coherent, not executed by me.
- **Nothing ran against a real Gateway, a real Director or a real launcher.** In particular the two facts
  the real-Director cancel rests on are readings, not runs - the Developer says the same in his proof: that
  the restore door accepts a Director's own credential, and that the Gateway relays the restore order back
  down to this Director, whose `StartWorkspaceRestoreAsync` then runs the same `DirectorRestore`. I read the
  routes and the tunnel dispatch and found nothing that contradicts either, but the composition
  (`GatewaySmartShutdownBringBack` to the door to the relayed `DirectorRestore`) is never exercised end to
  end by any test; the rig runs `DirectorRestore` directly.
- **The roster may lag.** The existing restore refuses a seat whose captured session is still on the
  Gateway's roster. A session closed seconds before the cancel may still be listed and be refused as "still
  running", and the owner would have to bring it back from the history. Disclosed by the Developer, not
  solved, not tested. The cancel tests close their sessions well before pressing, so no test covers the lag.
- The parked suites and the Avalonia tests were not run; only the mission's check and the two builds.

## What I looked hardest at, and what I found on each

**1. Cancel, after it is honoured: can anything still be ended, interrupted or flagged?** No. The asking
loop, the collecting loop and the two-thirds stage each break on the cancel before doing anything more; the
interrupt stage checks before each seat; and the cancel is carried out by one method,
`CancelAndKeepWorkingAsync`, which in order takes back every flag the reaper has not yet acted on (through
the new `IDrainSessionControl.CancelDeletion`, one verb over the session's existing `CancelDeletion` - the
fake and the real one both return false when the reaper got there first, and the session is then honestly
one of the closed and is brought back), tells every session still open that the restart is off (the words
task 1 had already written), marks the record cancelled and saves it, and only then brings the closed
sessions back. Nothing in it ends, interrupts or flags anything, and the test asserts the journal holds no
end or interrupt after the press. The one race - a flag the reaper takes between the check and the take-back
- lands on the safe side: the session is counted closed and is brought back.

**2. A cancel that races the limit or "Shut down now".** The two buttons are rivals under one small lock
(`Choose`), so both can never be chosen; the drain's `CancelChosen` additionally refuses to see a cancel
once "Shut down now" is chosen. The drain's guard is `limitReached`: the cancel path is entered only when
the collecting loop did NOT break on the deadline, so **there is no window in which sessions are ended AND
the record is marked cancelled** - the two paths are mutually exclusive behind that one flag. There is a
narrow window (smaller than one poll) where a press is honoured by the run against a snapshot that still
offered it but the drain can no longer act on it; the Developer discloses both ends of it and the run
answers with a plain sentence in the result's `Detail` saying the cancel came too late. The mandate's
specific question - `CanCancel` false from the first moment a session can be ended without a handover - is
held: the limit method's FIRST act is `Emit(EndingAtLimit)`, and `CanCancel` is false in the snapshot from
that moment; before the record exists it is false; on a run with no bring-back wired it is never offered at
all; and once chosen it is never offered again. Tested from all of those directions.

**3. The restore: existing, same Director, leads first, real owner - or a copy?** Not a copy. On the rig the
cancel hands its closed seats to the real `DirectorRestore`, leads first, each under its real owner, fed by
the very record the run saved (the fake Gateway reads the sink's last save at the moment the restore first
asks, and the test asserts that first read already carries the cancelled mark). On a real Director the host
wires `GatewaySmartShutdownBringBack`, which asks the Gateway's restore door for a restore onto THIS
Director's own id; that door is the one the owner's command line uses, it grants the restore lease, and the
Gateway relays the order back down to this Director, which runs the same `DirectorRestore` it always runs
(`StartWorkspaceRestoreAsync`). The Developer's reason for not running the restore directly - a restore with
no lease has every mark refused - matches what the marks route in `WorkspaceEndpoints` says. A session that
cannot be brought back is a sentence on its row (which keeps `ShutDown`, not a false `BroughtBack`) and in
the result, and never stops the others - tested with one refused and one brought back. The new owner rule
in `ResolveOwner` (a cancelled record accepts a never-closed running owner) is guarded by the record's own
cancelled mark and changes nothing for any other record; the revert proof shows the main tests depend on
it. **And the mandate's question, what if the save of the cancelled record fails:** the run ends `Failed`
with the error's own words; the open sessions have already been told the restart is off and keep working;
the closed sessions are NOT brought back; nothing more is ended; the record stands on the Gateway
uncancelled, each closed seat keeping its own answer, so a seat that answered "close" may not be offered on
the way up. That is an honest failure of a save the restore genuinely depends on (it reads the record from
the Gateway), reported to the owner with its reason, in the safe direction - no fallback was added to hide
it. Not counted a defect; recorded because the mandate asked.

**4. Ignore all.** The record is written and saved before the first session is ended, and the test proves
the ORDER on the shared journal: the first entry is the save, every end comes after it, and that first save
carries every conversation id, the ignore-all mark and every seat decided "close". With the Gateway
unreachable - it does not answer, or there is no client at all - every session is STILL ended,
`RecordWritten` is false and `RecordRefusal` says why, in both cases, and the comment at the code says in
words that this is the owner's stated choice and not a fallback. The close times are a second save; if it
fails it is logged and does not turn a written record into an unwritten one, which is true, not dressing.

**5. The operating system shutdown record.** `RecordOperatingSystemShutdownAsync` captures and saves and
returns; the test asserts the journal holds one save and nothing else - no send, no interrupt, no end, no
flag - and every session still live. There is no code path from it to any session verb, so it can never end
or interrupt anything. Its mark (a smart shutdown, every seat `ended-at-limit`) is the pair decision 10.3
reads to offer a saved conversation back, which is what 10.5 asks for; the record's own reason says the
operating system was shutting down. A call while a smart shutdown is already under way answers with that
run's own record rather than writing a second one beside it - the right reading, and it says so plainly
when the run has not written its record yet.

**6. The restart purpose.** The launcher is asked only after the drain has answered that nothing is left:
the test pins the ask as the journal's LAST entry, asserts nothing is live at the moment of the ask, and
asserts one capability re-check. The ask goes through `DirectorLauncherRestartStep`, into which the
cycle's own last two steps were moved - I compared the refusal sentences word for word, and the cycle's
report before the ask is preserved through the `beforeAsk` parameter; there is no second way of asking. A
refusal leaves the record standing, uncancelled, the handed-over seat still decided "restore" - tested both
for a launcher that refuses and for a machine that fails the re-check without the launcher being asked.
`Start` with the restart purpose reads the same private answer `CheckAsync` reads and throws
`InvalidOperationException` with the reason before anything is touched - no capture, no send, no run left
under way - tested for "no" and for "could not tell". The close purpose never asks and never re-checks,
tested with a launcher wired and idle.

**7. The process-wide gates.** The drain's gate is released in the `finally` of `RunAsync` on the cancel
path and on any throw out of `CancelAndKeepWorkingAsync`; the record-only methods take the gate and release
it in their own `finally`; a refused start takes nothing (the eligibility check throws before a run exists);
the tests assert both the drain's gate and the engine's active run are clear at the end of the cancel,
ignore-all and operating-system tests. `Changed` handlers are still raised one by one on the engine thread
and never inside the button lock, which is what the previous review's second answer required.

**8. The old path, and existing tests.** Every behavioural change in `DirectorDrain.cs` sits behind a
`smart is not null` guard or is additive; `limitReached` is read only by the cancel branch; the two
cancel-only sets are empty on every other path; the snapshot's `CanCancel` line is smart-only code in a
smart-only method. The `drain-index-diff` harness now THROWS if the older drain so much as takes back a
close, which is the right guard for the one new verb. `DirectorRestartCycle`'s drain steps are untouched,
and its launcher sentences are the same words as before the extraction. The only existing test file touched
is `DirectorRestoreTests`, and the change is a collection attribute and three lines of comment - no test
body, assertion or helper changed; the attribute exists because the cancel tests now run a real restore
against the same process-wide gate. No assertion in any existing test was changed: the run tests from task 3
still assert `CanCancel` false throughout and still pass, because their engine is wired with no bring-back,
exactly as the proof says.

**9. Names and shapes.** I read `ISmartShutdown.cs` and `phase-1-interface.md` side by side: the four
members, the two outcomes, `IgnoreAllResult`'s four fields, the snapshot, the session states including
`KeptRunning` and `BroughtBack`, and the phase `Cancelling` and `Restarting` all match, and the test asserts
every row's `StateLabel` comes from `SmartShutdownWords`. The allowed times are enforced in the request
record's own `init`, so they hold on the ignore-all path too, where the time is carried for the record
only.

**10. Do the tests watch the real run?** Yes. Every engine test runs the real `DirectorSmartShutdown` over
the real `DirectorDrain` over the rig, held at its first step until the handler is attached; the cancel
tests run the real `DirectorRestore` from the record the run itself saved, and the rig's restore gateway
takes its first read at the moment the restore first asks, so a test would go red if the cancelled record
had not been saved first - the revert proof says exactly that happened. The two `BringBackAsync` tests watch
the real `GatewaySmartShutdownBringBack` over a fake Gateway, which is that class's own contract; no test
anywhere builds a snapshot or a result by hand. 22 new cases by my count, matching 540 minus 518.

## Findings

**Finding 1 - a session that appears during "Shut down and ignore all sessions" is in neither the record nor
the end list.** `DirectorSmartShutdown.cs`, lines 245 to 252: the record is written from the capture, and
the end loop then walks `sessions.LiveSessionIds()` once. A session that a working session spawns between
the capture and the end loop - and nothing interrupts the sessions first, so they keep working and keep
spawning while the record is written - is in no record (the capture has already happened) and is not in the
end list (it was not live when the list was taken). What breaks, and for whom: the owner who chose to ignore
all is told every session is ended and the record says what was closed, and then the application closes on
top of a session that was neither recorded nor ended - it dies with no trace at all, which is the exact
harm the record-first design exists to prevent. The same product already treats this case as in scope on
the smart path: task 3 built `EndSessionsThatAreNotSeatsAndCheckEmptyAsync` (`DirectorDrain.cs`, line 701)
for precisely these sessions, ends them and records them in the record's problem sentence, for exactly
this reason - the owner asked for the Director to be emptied and the application is about to close. The
window is seconds and the likelihood is low, but the smart path proves the gap is real rather than
theoretical. Either the ignore-all path ends what is left after its loop (a second `LiveSessionIds` pass,
and a note in the result if anything had to be left), or the gap is accepted knowingly in the mission
record. The Developer decides.

**Finding 2 - a Gateway failure at the launcher ask ends a finished restart run as `Failed` instead of
`RestartRefused`, and the sentence the owner most needs at that moment is lost.** `DirectorSmartShutdown.cs`:
`AskLauncherAsync` (line 585) handles a null gateway and the three verdicts, but a THROW from
`DirectorLauncherRestartStep.RunAsync` - an `HttpRequestException` out of `CheckCapabilityAsync` or the ask
itself when the Gateway dies or restarts in the seconds between the drain's final save and the ask - is not
caught there; it falls to the run's catch-all (line 569) and the run ends `Failed` with "The smart shutdown
stopped on an error: ... The record on the Gateway says how far it got". What breaks, and for whom: the
owner of a Director that is verifiably empty, every session shut down and recorded, is shown a
stopped-on-error state and is NOT told the one thing the contract wrote `RestartRefused` to carry - "the
record stands and is offered on the next start" - at the exact moment he most needs to hear it, with no
sessions running anywhere and a phase 2 screen built to render the contract's outcomes. The same method
already produces `RestartRefused` for the same situation when the client is merely null ("no longer
connected to a Gateway"), so the two failure modes of one step give two different outcomes for one fact.
Catch at the ask and finish `RestartRefused` with the reason and the standing-record sentence, or accept
`Failed` knowingly; either is one small edit in `AskLauncherAsync`.

## Looked at and not counted as defects

- The busy message of `ShutDownIgnoringAllAsync` says "Use \"Shut down now\" on it" whatever is running;
  the older drain has no such button. Wording, in a case that takes two doors to reach.
- During `Cancelling`, the count label can go down (a brought-back row is no longer counted as shut down).
  It agrees with the rows and the phase label; nothing untrue reaches the screen.
- Two doors racing (a second door starting a smart run while an ignore-all is ending its sessions, after
  its record was written and the drain gate released) is owner versus owner; every entry point refuses
  first where it can, and the interleaving I traced ends with one honest refusal and no session harmed.
- The null-forgiving operator appears in the new lines (`smart.BringBack!`, guarded by `CancelChosen`;
  `seat.SessionId!`, matching the whole existing file). Same class as the previous review recorded.
- The engine's bring-back and launcher seams read the host's CURRENT Gateway client at the moment of use,
  which is the fix the previous review's finding 1 asked for, and the wiring comment says so.

## Verdict

Within the stated scope - the whole diff read against `origin/main`, the code around it including the
Gateway routes the real path depends on, the mission's check run by me (540 passed, 0 failed), and two
builds - the four things task 3 left out are built as the mandate asked: the cancel stops closing, tells,
saves the cancelled record and brings the closed sessions back through the one existing restore with no
window in which sessions are both ended and the record cancelled; the ignore-all writes its record before
the first end and still ends the sessions with the Gateway down, saying why; the operating system record
ends nothing; the restart asks the launcher only through the cycle's own re-check-and-ask step and a refusal
leaves the record standing. Two findings, both narrow: a session appearing during an ignore-all is neither
recorded nor ended, and a Gateway failure at the launcher ask is shown as `Failed` instead of the
`RestartRefused` the contract defines for exactly that situation. Everything I could not reach - the
revert proofs, any real Gateway, Director or launcher, the roster lag, the parked suites - is listed in the
scope above.

# Review - Smart Director Restart, phase 1, task 3: the smart shutdown run

Reviewed commit `2d2abd541` on branch `smart-restart/p1-engine`, a detached checkout in this worktree. The
commit under review is the proof commit; the code commit is `344bffd77`. The mandate, the mission
document, the Developer's mandate and the interface contract were read from the sibling checkout
`D:/ReposFred/devthrottle-smart-restart-p1` as this mandate directs; the Developer's proof was read from
THIS worktree, where the commit under review carries it.

## Scope

What I read, in full:

- The whole diff `git diff origin/main...HEAD`: `ControlApiHost.cs`, `DirectorDrain.cs`,
  `IDrainSessionControl.cs`, `DirectorSmartShutdown.cs` (new), `SmartShutdownWords.cs` (new),
  `DrainTestRig.cs`, `SmartShutdownRunTests.cs` (new), `tools/harnesses/drain-index-diff/Program.cs`,
  and the Developer's proof.
- Around the diff, to check the claims: `DirectorDrain.cs` in full (all 2408 lines: the gate, `RunAsync`,
  `RunCoreAsync` through the finish, `Preflight`, `CollectDocuments`, `ApplyBlock`, `FlagEligibleAsync`,
  `ReReadAtCloseTime`, `PollForAbsence`, `WaitForFlaggedAsync`, the checks, `BuildIntegrity`),
  `ISmartShutdown.cs` and `ISmartShutdownRun.cs` (task 1's contract types) side by side with
  `phase-1-interface.md`, `DrainMessages.cs` (the smart shutdown words and the short second message),
  `ControlApiHost.cs` (`CreateDrain`, `CreateSmartShutdown`, `JudgeRestartEligibility`,
  `ReapplyGatewayAsync`, the three other `var client = _gatewayClient` captures), `GatewayClient.cs`
  (`ListWorkspacesAsync`, `StopAsync`, `Dispose`), `DrainTestRig.cs` in full (both fakes),
  `SmartShutdownRunTests.cs` in full, and `docs/CodingStyle.md`.
- A search of the whole changed surface for every remaining sentence that states the never-force rule,
  for the null-forgiving operator in the new lines, and for non-ASCII in the diff (none).

What I ran, all in the foreground in this worktree:

- The mission's check, built from source in the same command:
  `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
  - **517 passed, 0 failed**, matching the prompt that opened me and the Developer's proof.
- `dotnet build src/CcDirector.Avalonia` - succeeds with no warnings and no errors. The check builds
  neither the Avalonia project nor the harness; the diff touches the project both of them reference, so
  this build was run. (`tools/harnesses/drain-index-diff` compiles as part of the check's dependency
  graph and produced no warnings.)

What I could not reach:

- **The two revert proofs were not re-run.** Both mutate tracked files and this mandate forbids changing
  any tracked file in this worktree. They were read as testimony and checked for coherence against the
  test code: the ten tests the two-thirds mutation reddens are exactly the tests that assert the short
  message, the interrupt moment or the two-thirds times, and the one test the save-before-end mutation
  reddens is the one that reads the call journal. The claims match the code line by line, but they were
  not re-executed by me.
- **Nothing ran against a real Director, a real session or a real Gateway.** Tests and builds only, as the
  mandate allows. In particular `IsMidTurn` over a real `SessionManager` has no test (the proof says so
  itself); every test here watches the fake.
- **A configured Gateway that does not answer** was exercised only through the seam (a throwing
  `reachGateway` delegate), never through a real `GatewayClient` against a dead endpoint.
- The parked suites and the full default gate were not run; only the mission's check and the Avalonia
  build.

## What I looked hardest at, and what I found on each

**1. The old path, byte for byte.** I walked every hunk of `DirectorDrain.cs` against the file on
`origin/main`. Every behavioural change is behind a `smart is null` guard or is additive: the capture
description, the mark and the request text all switch on the one field; `interruptAt` and
`interruptStageDone` are computed and never read on the old path; the `TryListLiveSessions` extraction
carries the same sentence and the same failure handling as the inline block it replaced; the reap-timeout
branch, the integrity sentence and the `DirectorDrainResult` extension are inert while no seat carries the
new state, which only the smart path writes. `DrainOptions.SmartShutdown` is null unless a caller sets it,
and the restart cycle (the only existing caller) does not. No existing test file was touched: the only
test-project change is `DrainTestRig.cs`, and it is purely additive (a shared journal, a mid-turn set, a
handover written only on the second message, a capture that can fail) - every fake method keeps its old
body with the new lines appended inside guards. The proof's second row (487 passed on the new engine
before any new test existed) says the same thing from the other end. The new old-path test and task 1's
`Drain_OnTheOlderPath_NeverInterruptsAndNeverEndsASession` hold it from both directions, and the
`drain-index-diff` harness now throws if the older drain so much as asks the mid-turn question. Sound.

**2. The order at the limit.** Both ways in - the clock and "Shut down now" - funnel into one method,
`EndEverySessionStillPresentAsync`, which marks every seat, saves the record, and only then issues the
first `EndAsync`, saving again after; the order test reads one journal written by both fakes and places
the first save naming the seat `ended-at-limit` strictly before the end, and the last save strictly after
it, and asserts the conversation id is on that saved record. When that first save throws: the throw
leaves the method before any session is ended, the drain's process-wide gate is released in its `finally`,
the run ends `Failed` with the error's own words, and every session keeps running - the safe direction,
and the record stands at its last good save saying how far the run got. One narrow case, noted and not
counted a defect: sessions that appeared AFTER the capture are ended in
`EndSessionsThatAreNotSeatsAndCheckEmptyAsync` with no save before them naming them - they are in no
record, so no save could name them; the problem sentence that describes them is stored at the final save,
which is after the ends. A Director dying inside that window leaves a record that never mentioned them,
which is the same as what it would have said had it died a moment earlier.

**3. Sessions ended that had handed over, or ended before the limit.** A seat that handed over and is
still present at the limit IS ended (the mission requires an empty Director) but KEEPS its state
"drained", its handover path and its restore answer; it is NOT recorded `ended-at-limit`. That is a
deliberate, documented and tested deviation from the letter of the Developer's mandate ("its seat is
recorded as ended-at-limit"), and in my reading it is the better reading of the mission: recording it
`ended-at-limit` would, by mission decision 10.3, offer a clean handover back as "ended without a
handover", unticked. I flag it here so the Tech Lead accepts it knowingly, not as a defect. No path ends a
session before the limit: the only two callers of `EndAsync` are the limit method and the
not-in-the-record sweep that runs after it; everything earlier is `MarkForDeletion`, the same ask-the-reaper
verb the older path has always used, on which the reaper still never cuts a turn.

**4. "Shut down now" twice, during the interrupt stage, after the limit.** Twice: an interlocked
once-flag plus the snapshot's `CanShutDownNow`, which goes false the moment the token is cancelled -
tested with a double press in one call. During the interrupt stage: the stage checks the token before each
seat and returns, the poll wait ends at once, the collect loop breaks at its top, and the run goes to the
limit. After the limit: every `EndingAtLimit` and `Finished` snapshot says the button is spent
(`CanShutDownNow` is false from `EndingAtLimit` onwards), and a press is ignored and logged. Pressed
before anything is asked: no request is ever sent and every session is still recorded and ended - tested.
Sound.

**5. A `Changed` handler that throws or blocks.** Throwing: caught per handler, logged, and neither the
run nor the next handler is stopped - tested over thirty-plus snapshots with a second handler still
receiving every one. Blocking: see finding 2 below - it can stall the run, and nothing guards it.

**6. The snapshots.** Complete and immutable by construction: every raise builds fresh rows from the
current facts into a new record; nothing is merged, and the final snapshot is a copy, not a mutation.
Rows come out leads first, each lead followed by its subtree in depth-first order (a stack walk over the
chain, checked in the test against a three-seat hierarchy). `Total`, `Gone` and `CountLabel` are computed
from the same rows in the same breath, so they cannot disagree. Every field name and shape matches
`phase-1-interface.md` - I read the contract document and `ISmartShutdownRun.cs` side by side, position
by position, including the two times, the two buttons and the note. Two transient notes, neither a defect:
in the wait after the ending loop, a session whose end was accepted but whose absence is not yet verified
still shows its earlier state (for instance "Asked to hand over") for at most one poll, while the phase
label says what is happening; and a session interrupted at two thirds whose short message then failed to
land shows "The request did not reach it", not "Interrupted", although the priority comment in `RowFor`
says interrupted wins - the detail line carries the interrupt fact, so nothing untrue reaches the screen.

**7. A session that refuses an interrupt (the Pi case).** Never shown interrupted: the seat enters
`_interrupted` only when the interrupt was delivered AND the short message landed, and a refused
interrupt puts the driver's own words in the row's detail instead. Tested, including that the row is never
`Interrupted` and that the limit still ends it.

**8. `CheckAsync` with the Gateway unreachable.** `Start` does not trust the dialog's answer: the run
re-asks the same question as its own first step, before it builds a drain, and a refusal ends the run
`Refused`. The test asserts nothing was sent, renamed, flagged, interrupted, ended, asked, captured or
saved - I checked each assertion against the rig's journals. Sound.

**9. The process-wide gates.** The drain's gate is released in a `finally` in `RunAsync` - on the return
and on every throw, including the preflight refusal. A refused smart start never takes it (the check
throws before any drain is run); a smart run refused at its own Gateway check never calls the drain. The
smart gate is one run per process and is read through completion, so a finished run never blocks the next
one - tested from both directions. Sound.

**10. Do the tests watch the real run?** Yes. Every test in `SmartShutdownRunTests` runs the real
`DirectorSmartShutdown` over the real `DirectorDrain` over the rig's two fakes, and the run is held at its
very first step until the handler is attached, so no raised snapshot is missed; no test builds a snapshot
or a result by hand, and the all-states test checks EVERY snapshot raised in one run - count, labels,
both times, both buttons - not just the last one. The five allowed times are checked by the rig's clock
at the moment of each interrupt and each end, and against the snapshots' own two moments.

**11. Comments that still state the never-force rule as a fact about the whole class.** The class comment
on `DirectorDrain` now says the rule holds for the older path and names the owner's ruling that replaces
it for the smart shutdown; the seam comment on `SessionManagerDrainControl` was rewritten the same way and
names the test that holds it. I walked every remaining sentence that says "never force" or "nothing was
forced": each is scoped to the older path, a true statement about the reaper (which still never cuts a
turn), or the words the older path's request text still sends and must keep. One sentence, in
`FlagEligibleAsync` ("that is the mechanical shape of never forcing - not a rule, a missing verb"), still
reads as though the class holds no stronger verb; it is a statement about the flagging mechanism and the
class comment now overrides it, but it is the one the mandate's instruction did not reach.

On the coding style guide: the new lines in `DirectorDrain.cs` use the null-forgiving operator on
`seat.SessionId` throughout. The guide forbids the operator; the whole existing file, on `origin/main`,
uses it on exactly that field in exactly that way, and the new code matches the file rather than the
guide. Not counted a finding; recorded so the Tech Lead knows it was looked at.

## Findings

**Finding 1 - the engine keeps the Gateway client it had when it was made, so it can refuse a smart
shutdown the Director could in fact perform.** `ControlApiHost.cs`, line 228: `CreateSmartShutdown`
captures `var client = _gatewayClient` once, and the reachability probe handed to the engine closes over
that captured variable - it is the SAME probe `CheckAsync` answers the dialog with and the run re-asks as
its own first step. But `_gatewayClient` is a mutable field: `ReapplyGatewayAsync`
(`ControlApiHost.cs`, lines 1480 to 1503) stops and DISPOSES the old client and builds a new one whenever
the Gateway settings change, and `GatewayClient.Dispose` disposes the underlying HTTP client
(`GatewayClient.cs`, lines 993 to 999), so `ListWorkspacesAsync` on it throws. What breaks, and for whom:
a screen that keeps the engine across a Gateway settings change - and the interface contract nowhere says
the screen must build a fresh one for each dialog - gets "the Gateway could not be reached (Cannot access
a disposed object...)" for every smart shutdown afterwards, a false refusal, because `CreateDrain()`,
which the same engine calls through its factory, reads the CURRENT field and would have built a working
drain against the new client. The same holds for an engine built while no client existed: it says "not
connected to a Gateway" for ever, even after the host connects. Nothing unsafe happens - a disposed client
never answers, so the mistake is always a refusal and nothing is ever touched - but the owner is told a
working Gateway is unreachable and cannot do a smart shutdown until the screen happens to rebuild the
engine, and the two halves of one engine read the same fact at different vintages. The host already has a
wrapper that reads the field at call time (`ControlApiHost.cs`, line 182); whether the probe goes through
it, or reads the field per call, is the Developer's choice.

**Finding 2 - a `Changed` handler that blocks stalls the whole run, and neither the engine nor the contract
guards against it.** `DirectorSmartShutdown.cs`, lines 367 to 376 (`Publish`) and `DirectorDrain.cs`, the
`Emit` method (line 1720): the drain raises every snapshot by calling the handler synchronously on the
engine thread, inline, between every poll, at the interrupt stage and at the limit. A handler that throws
is contained; a handler that BLOCKS is not. What breaks, and for whom: a phase 2 screen whose handler
dispatches to its user interface thread SYNCHRONOUSLY - which the contract does not forbid, since it says
only "the screen dispatches to the UI thread itself", and which the repository's own style guide has to
warn against ("use Dispatcher.BeginInvoke, not Invoke") precisely because it is the natural thing to write
- stops the drain mid-step: no further snapshot, no two-thirds stage, no limit, and "Shut down now" has no
effect either, because the button cancels a token that only the blocked thread ever reads. The run never
completes, so `DirectorSmartShutdown._active` (the process-wide one-run gate) then refuses every later
smart shutdown on this process until the application is restarted: one blocked handler wedges the
Director's ability to shut down smartly for good. Phase 2's mandate is not written yet; either the
contract gains the sentence that the handler must return at once and dispatch asynchronously, or the
engine stops waiting on handlers. Which side of that line the fix lands on belongs to the Developer and
the Tech Lead; the harm is that nothing on either side says it today.

## Verdict

Within the stated scope - the whole diff read against `origin/main`, the code around it, the mission's
check run by me (517 passed, 0 failed), and an Avalonia build - the run does what the mandate asked: the
old path is untouched and held by tests from both ends, the limit saves before it ends on every path in
including "Shut down now", the two stages land exactly at two thirds and at the limit for all five allowed
times, the snapshots match the contract field for field, and the tests watch the real engine throughout.
Two findings, both narrow and neither touching the safety of a run: a stale Gateway client captured once
(wrong refusals after a settings change), and an unguarded blocking event handler (a stalled run that
also wedges the process-wide gate). Everything I could not reach is listed in the scope above.

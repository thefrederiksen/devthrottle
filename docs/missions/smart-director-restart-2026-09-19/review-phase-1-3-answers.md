# Answers to the review - Smart Director Restart, phase 1, task 3

The review is `review-phase-1-3.md`, beside this file, committed unchanged. The Developer that built
task 3 is gone, so under law 11 of the method these answers come from a fresh Developer opened by the
Tech Lead of phase 1. Every finding and every note is answered below.

## Finding 1 - the engine keeps the Gateway client it had when it was made: ACCEPTED, fixed

The review is right, with one correction to its second case that makes the harm slightly different and
no smaller. `CreateSmartShutdown` captured the host's client once, and the reachability question closed
over that capture, while the drain half of the same engine read the host's current client. After a
settings change the captured client is disposed, so the check refused a smart shutdown the Director
could perform. The correction: an engine made while NO client existed did not say "not connected to a
Gateway" for ever through the same sentence. `CheckAsync` asks `CreateDrain()` first, and that reads the
current client, so once the host connected the engine moved on to the reachability question, and THAT
threw from the captured nothing. The owner was told "the Gateway could not be reached (this Director is
not connected to a Gateway.)" about a Gateway that was connected. Two ages of one fact in one engine,
exactly as the review says.

**The fix.** The probe now goes through the host's own wrapper, `ControlApiHost.ListWorkspacesAsync`,
which reads the client at the moment of each call - the same vintage `CreateDrain()` reads. The engine
holds no client of its own. The summary comment on `CreateSmartShutdown` now says so, and says a screen
may keep the engine across a settings change. `DirectorSmartShutdown` did not change.

**The test.**
`CreateSmartShutdown_AnEngineMadeBeforeTheHostHadAClient_AnswersFromTheClientTheHostHasNow`, in
`SmartShutdownRunTests`. It watches the HOST and hand-builds nothing under test: a real `ControlApiHost`
with no client hands out a real engine; the engine's check says "not connected to a Gateway"; the host
is then driven through its own replacement path, `ReapplyGatewayAsync`, the path a settings change takes;
and the SAME engine is asked again. It must now answer with a sentence only a Gateway client can say
("Gateway is not configured"), and must no longer say "not connected to a Gateway". The test also holds
that `CreateDrain()` went from nothing to something across the same step, so the two halves of the
engine are shown agreeing. The revert proof is in `proof-phase-1-task-3.md`, "After the review".

**One seam was added to the host to make that reachable, and why.** `ReapplyGatewayAsync()` reads this
machine's real `config.json`. In a unit test on this machine that would build a client for the owner's
real Gateway, open a real stream and register the test host there as a Director, which the mandate
forbids and the unit test project forbids (it is the pure half of the Gateway tests). The storage root
can only be redirected by a process-wide environment variable, which is not safe in a suite that runs
in parallel. So the method gained an internal overload that takes the configuration read; the public
method passes `GatewayConfig.Load` and is otherwise unchanged, and the file is still read at the same
point, after the old client is gone. The test passes a configuration with no Gateway address, so the
whole real replacement path runs and nothing is dialled. The coding style guide allows internal members
for tests, and `GatewayClient` already carries one for the same reason.

**What this test cannot reach.** The other case the review names - an engine made while the host held
an OLD client that a settings change then disposed. Through the real replacement path that needs a
client with a Gateway address, and the host builds a stream client alongside it that dials that address
at once; the host has no way to be handed a stub transport. A client with no address refuses before it
touches its transport, so an old one and a new one say the same sentence and a test could not tell them
apart. Both cases are one defect - one captured variable - and the test goes red when the capture comes
back, which is what holds the fix. The disposed-client case itself is not separately observed.

## Finding 2 - a `Changed` handler that blocks stalls the whole run: ACCEPTED, on the contract side

The harm is real and the review states it exactly. I agree with the Tech Lead that the fix belongs in
the contract and not in the engine, and I weighed the other side before agreeing. Moving handlers off
the engine thread would need a queue per run, so that snapshots still arrive in order and the last one
still arrives before `Completion` is seen; a blocked handler would then fill that queue without limit
for up to an hour, or snapshots would have to be dropped, and a dropped snapshot on a screen that
replaces what it shows is a screen that is silently stale. That is a second mechanism, with its own
failure cases, added to guard against a caller's mistake that one sentence prevents. The engine keeps
raising complete snapshots in order on its own thread.

What was missing is the sentence, and it is now in two places in the same words - the documentation
comment on `ISmartShutdownRun.Changed`, and `phase-1-interface.md` section 3 where it says the event is
raised on an engine thread:

> The handler must return at once. It dispatches to the user interface thread ASYNCHRONOUSLY (a post,
> never a synchronous invoke). A handler that blocks stalls the run, the two thirds stage, the limit and
> the "Shut down now" button, and holds the one-run gate so that no later smart shutdown can start.

No engine code changed for this finding, and so there is no new test for it: a sentence has no
behaviour to watch. Phase 2's mandate should quote it, and phase 2's review should look for a
synchronous invoke in the handler; that is where this finding is finally closed.

## The comment named under the review's point 11: ACCEPTED, rewritten

`FlagEligibleAsync` in `DirectorDrain.cs` said never forcing was "not a rule, a missing verb", which
stopped being true of the class when the smart shutdown arrived. It now says what is true: never forcing
holds for the flagging on both paths, because flagging only ever asks the reaper and the reaper never
cuts a turn; the class does hold a stronger verb, `EndAsync`, and the smart shutdown uses it only at
the limit, never in the flagging.

## The Reviewer's notes that were not counted as findings

- **Sessions that appeared after the capture are ended with no save before them naming them (point 2):
  noted, no change.** They are in no record, so no save could name them; a Director dying in that window
  leaves a record that says what it would have said a moment earlier, and the sentence describing them
  is stored at the final save.
- **The transient row after an accepted end, for at most one poll (point 6): noted, no change.** The
  phase label says what is happening during that poll, and the next snapshot corrects the row; a special
  state for ten seconds would be a ninth thing for the screen to draw.
- **An interrupted session whose short message did not land shows "The request did not reach it", not
  "Interrupted" (point 6): noted, no change.** That row is the more useful truth - the session was NOT
  asked again - and the detail line carries the interrupt. The priority comment in `RowFor` describes a
  seat that IS in the interrupted set, and this seat never enters it, so the comment is not wrong.
- **A session that handed over and is still present at the limit keeps "drained" (point 3): noted, no
  change.** The Reviewer reads it as the better reading of the mission and so do I; recording it
  `ended-at-limit` would offer a clean handover back as "ended without a handover".
- **The null-forgiving operator on `seat.SessionId` in the new lines: noted, no change.** The guide
  forbids it, the whole existing file uses it on exactly that field, and the new lines match the file.
  Changing only the new lines would leave one file with two habits; changing the file is not this task.
- **What the Reviewer could not reach** (the two earlier revert proofs not re-run, no real Director,
  `IsMidTurn` on the real seam, a configured Gateway that does not answer, the parked suites): noted, no
  change here. Each is already listed in the proof's own "What I could not reach", and none is made
  worse or better by this task.

## No existing test was edited

One test was added. `DrainTestRig.cs` and every existing test are untouched.

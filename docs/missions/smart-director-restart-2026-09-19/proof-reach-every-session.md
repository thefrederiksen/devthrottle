# Proof - the smart shutdown reaches and accounts for every session

Phase 5 defects, Smart Director Restart (#3167). Issues fixed: **#3207** (the two-thirds step never
reaches a pi session) and **#3235** (one session that never starts a turn ends the whole shutdown).

Written by the Developer seat that built it, 20 September 2026, in worktree
`D:/ReposFred/devthrottle-smart-restart-verb` on branch `smart-restart/handover-verb`, cut from
`origin/main` at `e188d3048` and never behind it while this work was done.

Both defects have one shape: **a session the shutdown could not ask was handled silently, or
fatally.** Six of seven sessions came back "ended without a handover" on the owner's own run.

---

## Half one - the verb is chosen from what the driver declares (#3207)

### What was wrong

At two thirds of the time allowed the shutdown interrupts whatever is still mid-turn and asks it to
hand over now. It sent the hard interrupt to every agent. `PiDriver.InterruptAsync` **throws** by
design - pi's Ctrl+C clears its editor and twice quits it - so the Director's interrupt verb answered
Conflict, the pi session was never told to hand over, it worked to the limit, and it was ended with
no document. Pi is the agent every review seat in this fleet runs.

### What changed

`IDrainSessionControl.InterruptAsync` is **replaced** by `StopTurnAsync`, which reads the session's
own driver and chooses the verb **before anything is sent**:

- the driver declares `Interrupt` -> the Director's existing interrupt verb;
- it declares only `Cancel` -> the Director's existing escape verb;
- it declares neither -> **nothing is sent**, and the answer is a sentence naming the agent.

No fallback: there is no "try the interrupt and if it throws try the escape". A driver with no safe
hard interrupt throws from its own method, and a catch around that throw would be an exception used
as a decision (law 1).

The seam is the only implementer that knows about drivers, so the choice lives there. The drain reads
the answer, puts the verb in its log and on the row, and - for an agent that declares neither - adds a
line to the record's problems naming the seat and the agent. The session is still sent the short
"hand over now" message, and is still ended at the limit like any other session still present.

The old `InterruptAsync` seam verb is gone rather than kept beside the new one: two ways to stop a
turn is how a caller comes to use the wrong one. The Director's own interrupt and escape verbs are
untouched, so there is still exactly one way to interrupt a session and one way to escape one.

Files: `src/CcDirector.ControlApi/Drain/IDrainSessionControl.cs` (new `DrainStopVerb`,
`DrainTurnStop`, `StopTurnAsync`), `src/CcDirector.ControlApi/Drain/DirectorDrain.cs`
(`InterruptAndAskAgainAsync`, new `StopWords`).

### Every driver, read from the code (not only pi)

`AgentDrivers.For` maps every `AgentKind` to one of six driver classes. Read at this branch's base:

| Driver class | Agent kinds it serves | Declares `Cancel` (Escape) | Declares `Interrupt` (Ctrl+C) | Verb the shutdown now sends |
|---|---|---|---|---|
| `ClaudeDriver` | ClaudeCode | yes | yes | interrupt |
| `CodexDriver` | Codex | yes | yes | interrupt |
| `PiDriver` | Pi | yes | **no** | **escape** (this is #3207) |
| `CursorDriver` | Cursor | no | yes | interrupt |
| `CopilotDriver` | Copilot | no | yes | interrupt |
| `GenericDriver` | Gemini, OpenCode, Grok, RawCli, and any kind with no written driver | yes | yes | interrupt |

**No shipped driver declares neither verb today.** That shape is still built and tested, because the
capability enum allows it and a driver written tomorrow may be it - and a session this Director
cannot stop must be named rather than skipped. Only pi's behaviour changes; every other agent gets
exactly the verb it got before.

### What each new test proves, in plain words

In `src/CcDirector.Gateway.UnitTests/Drain/DrainSessionControlSmartShutdownVerbsTests.cs`, on the
REAL seam over a REAL `SessionManager` and real drivers - only the agent process is a stand-in:

- `StopTurnAsync_AnAgentThatDeclaresTheHardInterrupt_SendsCtrlCAndSaysSo` - an agent that declares
  the hard interrupt gets Ctrl+C and no Escape, and the answer says "interrupt".
- `StopTurnAsync_AnAgentThatDeclaresOnlyTheSoftCancel_SendsEscapeAndNeverCtrlC` - a pi session gets
  the Escape byte and **never** Ctrl+C, and the stop lands. This is the whole of #3207.
- `StopTurnAsync_AnAgentThatDeclaresNeither_SendsNothingAndNamesTheAgent` - an agent declaring
  neither has **nothing written to its terminal**, and the refusal names the agent, so a row and the
  record have a sentence to show.
- `StopTurnAsync_EveryShippedDriver_GetsTheVerbItDeclaresAndNoOther` - a theory over **every**
  `AgentKind` (nine today), walking the real driver registry: whatever that driver declares is what
  is sent, and nothing else. A driver added later is covered the day it is added.
- `StopTurnAsync_AVerbTheDirectorRefuses_AnswersCouldNotWithTheHandlersOwnReason` - a verb that was
  chosen correctly and still did not land comes back as "could not", in the handler's words, not as
  an exception.
- `StopTurnAsync_ASessionThatIsNotHere_AnswersGone` - gone and could-not stay two facts.

In `src/CcDirector.Gateway.UnitTests/Drain/SmartShutdownRunTests.cs`, over the real smart shutdown:

- `Start_ASessionMidTurnWhoseAgentDeclaresOnlyTheSoftCancel_IsStoppedWithEscapeAndHandsOver` - the
  pi shape end to end: Escape is the verb sent, the session is then told to hand over now, it writes
  its handover, it is closed the ordinary way, and the limit is never reached.
- `Start_ASessionMidTurnWhoseAgentDeclaresNoStopVerb_IsNamedOnItsRowAndInTheRecord` - nothing is
  sent, the row says its turn could not be stopped and why, the record's problems name the seat and
  the reason, it is still asked to hand over, and it is ended at the limit. The run finishes.
- `Start_ASessionThatCannotBeInterrupted_...` (existing, reworded) - a session whose agent declares
  the interrupt and refuses it anyway is still asked again, is never shown as interrupted, and is
  ended at the limit.

---

## Half two - one session that cannot be asked does not end the run (#3235)

### What was wrong

`SessionManagerDrainControl.SendAsync` caught `ComposerNotAcceptingInputException` ("the text never
reached the composer") and answered "could not be asked". It did **not** catch
`PromptNotSubmittedException` ("the text is in the composer and nothing ever ran"), which is that
exception's sibling, thrown a few lines further along the same submit path. Both are the same fact to
a drain. One session of that shape - a raw command line that echoes the text and then prints less
than the submit verifier's floor - threw out of the whole run: it stopped at the sixth of seven
sessions, the seventh was never asked, every other session was left running, and nothing was handed
over.

### What changed

One more catch, in the same method, answering `DrainDelivery.Refused` with the submit path's own
words. That is where the decision belongs: **the asking of ONE session is what is allowed to fail.**
The run is not wrapped in a catch - a wrapper would hide the next defect of this shape, and would
still leave every other session un-asked.

The row already has a state for it (`NotDelivered`, "The request did not reach it"), the progress
screen already renders the reason verbatim, and the limit already ends such a session and records it
`ended-at-limit`. Nothing else had to change.

File: `src/CcDirector.ControlApi/Drain/IDrainSessionControl.cs`, `SendAsync`.

### What each new test proves, in plain words

New file `src/CcDirector.Gateway.UnitTests/Drain/SmartShutdownSessionThatCannotBeAskedTests.cs`. It
runs the REAL drain over the REAL seam over a REAL `SessionManager` holding three real sessions; only
the agent processes are stand-ins, and only one of them misbehaves - its terminal takes the text and
throws `PromptNotSubmittedException`, which is the product's own exception travelling the product's
own path.

- `ASessionThatNeverStartsATurn_IsRecordedAsSuchAndEveryOtherSessionIsStillAsked` - the wedged seat
  is the **middle** of three, deliberately: a session AFTER it is what proves the run carried on.
  The run finishes and empties the Director; both healthy sessions were asked; the wedged one is
  recorded `ended-at-limit` and named in the record's problems with the submit path's words; nothing
  is left running.
- `TheSeam_WhenASessionNeverStartsATurn_AnswersCouldNotWithTheSubmitPathsOwnWords` - the one line
  the defect turned on, asserted directly: the seam answers "could not be asked, and here is why"
  rather than throwing, and the session is neither gone nor asked.

---

## The counts

The mission's check, section 7, run in this worktree. Read the count, not the colour.

| Run | Before (base `e188d3048`, my own run) | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | **637 passed, 0 failed** | **652 passed, 0 failed** |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | **147 passed, 0 failed** | **147 passed, 0 failed** |

The fifteen added are the eleven cases in the verbs file (six named tests, one of them a theory over
nine agent kinds, less the four cases it replaced), the two new smart shutdown run tests, and the two
in the new file.

Also run, because the local gate says this change touches a PARKED suite:

- **whole `CcDirector.Gateway.UnitTests` suite, unfiltered: 6,871 passed, 0 failed, 8 skipped**
  (the eight are `HostedSchemaRefusesAnUnownedRowTests`, gated on the PostgreSQL rig connection
  `CC_GATEWAY_TEST_PG_STATS_CONNECTION`, which is not set here; they are untouched by this change).
- `.\scripts\test-local.ps1` (the default gate): every suite Completed **except**
  `CcDirector.Launcher.Tests`, 2 failed of 197 - see "What I could not reach".

## The revert proofs

Each is the real pre-fix shape, not a constant-false guard: this build treats warnings as errors, so
a dead branch fails to compile instead of failing a test. Each mutation was applied to the COMMITTED
file, the suite was **built** and run (never `--no-build`, because a checkout restores the source and
not the assembly), and the file was restored in a `finally`.

| Put back | Filter run | Result |
|---|---|---|
| The seam stops reading the driver: `capabilities` always includes `Interrupt` | `DrainSessionControlSmartShutdownVerbsTests` | **3 failed, 17 passed** - the pi test, the declares-neither test, and the every-driver theory at `kind: Pi` |
| The drain stops naming an agent with no stop verb (the silent skip) | `SmartShutdownRunTests` | **1 failed, 32 passed** - `Start_ASessionMidTurnWhoseAgentDeclaresNoStopVerb_IsNamedOnItsRowAndInTheRecord` |
| The seam stops catching `PromptNotSubmittedException` | `SmartShutdownSessionThatCannotBeAsked` | **2 failed, 0 passed** - both tests in the new file |

After the third restore the tree was clean (`git status` empty) and the drain and restart filter was
rebuilt and re-run: **652 passed, 0 failed.**

## What I could not reach

- **No run against a real Director or a real session.** The mandate forbids it; everything here is
  tests. Nothing in this change has been seen on the isolated quality assurance rig, and phase 5 is
  where that happens.
- **The escape timing is not re-measured.** Issue #3207 records 6.8 seconds for a pi session to
  write its handover after an escape, measured on the rig in run 8. This change makes the shutdown
  send that escape; it does not re-measure the number.
- **`CcDirector.Launcher.Tests` has two failures that are not mine.** They are
  `LauncherDeclaredCapabilitiesTests.An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown`
  and `...Describing_the_launcher_asks_the_signal_and_never_raises_it`, both
  `Assert.False() Failure, Expected: False, Actual: True` on `RestartSignalArmed`. They fail inside
  the gate, alone, and with a filter down to that one class - so it is not test ordering. Nothing in
  that project's dependency graph (`CcDirector.Launcher`, `CcDirector.Core`,
  `CcDirector.Setup.Engine`) is touched by this branch: `git diff origin/main` over those paths is
  empty. The reading is that `LauncherDeclaredCapabilities.Describe()` answers from a machine-wide
  signal, and this machine has real launchers running, so the test asserts something that is true of
  a clean machine and false of the owner's. Reported separately; not fixed here.
- **`scripts/test-local.ps1 -Parked` was not run.** The two parked suites it adds beyond what is
  above are `CcDirector.Gateway.Tests` (host-bound, PostgreSQL) and `CcDirector.Core.Tests`; this
  change touches neither the Gateway host nor Core. The suite that does cover it,
  `CcDirector.Gateway.UnitTests`, was run in full and is in the counts above.

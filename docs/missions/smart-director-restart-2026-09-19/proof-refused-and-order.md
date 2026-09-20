# Proof - the last two defects: a restart that succeeded said refused, and "newest first" was not

What this owed: issue #3257, a smart restart that SUCCEEDED reporting `RestartRefused`, and issue
#3258, the restart history labelled "newest first" that was not ordered by when the shutdown was, with
the start-up offer walking the same order.

Written on 20 September 2026 by the Developer seat for the last two defects, branch
`smart-restart/refused-and-order`, worktree `D:/ReposFred/devthrottle-smart-restart-final`, cut from
`origin/main` at `6d1a8651d`.

**No Director and no rig was started, and nothing put a window on anybody's screen.** Every run below
is the headless test host. The one thing read off this machine outside the tests is the rig Director's
own log file from the 20 September run, which is where the mechanism of #3257 was established.

---

## 1. The headline

Both are fixed, both are proved by a test that has been shown to fail without the fix, and the counts
went up by five with nothing else moving.

| | before | after |
|---|---|---|
| `CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 686 passing, 0 failed | **691 passing, 0 failed** |
| `CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 169 passing, 0 failed | **169 passing, 0 failed** |

Both "before" numbers were measured in this worktree at `6d1a8651d` before a line was changed, not
quoted from an earlier report. The window count is 169 and not the 159 in the mandate, because the two
door tests merged in `6d1a8651d` added ten - the mandate says so itself.

The whole of `CcDirector.Gateway.UnitTests`, unfiltered, was also run because it is a PARKED suite that
the default local gate does not touch and this change lives inside it: **6,936 passing, 8 skipped, 0
failed**, 5 minutes 7 seconds.

---

## 2. Half one - issue #3257: the restart that succeeded and said it had been refused

### The mechanism, read rather than guessed

From the rig Director's own log, process 56400,
`cc-director-restart-qa-rig\instances\default\logs\director\director-2026-09-20-56400.log`:

```
17:38:58.183 [GatewayClient] RequestOwnRestartOnlyIfEmptyAsync: POST /machines/SOREN_NORTH/director/restart
17:38:58.401 [LifecycleSignal] cc-director-shutdown-3be6c633-...: signalled
17:38:58.404 [CcDirector] shutdown requested by lifecycle signal
17:38:58.482 [ControlApiHost] StopAsync
17:38:58.519 [GatewayClient] StopAsync: directorId=3be6c633-...
17:38:58.555 [SmartShutdownRun] AskLauncherAsync FAILED: launcherWasAsked=True: TaskCanceledException
17:38:58.556 [SmartShutdownRun] finished: outcome=RestartRefused
```

Four tenths of a second, in order: the ask goes out; the launcher answers by raising THIS Director's
shutdown signal (`DirectorSupervisor.StopAsync` raises
`LifecycleSignalNames.DirectorShutdown(directorId)`); this process begins its own shutdown;
`ControlApiHost.StopAsync` disposes the Gateway client, which disposes the HTTP client, which aborts
the request still in flight; the abort arrives as a cancellation and is folded into `RestartRefused`.

So the accepted answer and the refused answer arrive at `AskLauncherAsync` as the same thing - an
exception with no answer behind it - and the engine had nothing to tell them apart with.

### What changed, and how the accepted case is now POSITIVELY recognised

It is recognised from **two positive facts, and never from the exception**:

1. **the ask was SENT** - already recorded, by the `beforeAsk` hook `DirectorLauncherRestartStep` runs
   after the machine re-check and before the request (`launcherWasAsked`); and
2. **this process was then ASKED TO STOP** - new. `CcDirector.Core/Lifecycle/LifecycleStopRequest.cs`
   records the moment the lifecycle shutdown signal arrives; `App.axaml.cs` records it in that signal's
   own handler, before the shutdown routine runs, because something already under way has to be able to
   read it. `ControlApiHost.CreateSmartShutdown` hands the engine a `hasBeenAskedToStop` seam over it.

The engine reads that seam **before** the ask as well as after the failure, and claims acceptance only
when it was false before and is true after. So:

- a Director already on its way down for its own reasons - the person closed the window mid-restart -
  never reads its own stop as the launcher accepting; and
- nothing sniffs the exception's type, nothing is swallowed, nothing is retried, and an ask that failed
  with no stop behind it is still `RestartRefused` carrying the error's own words and the sentence
  saying the record is offered on the next start.

**The signal, and only the signal, counts as the stop.** A window closed by the person is a stop too
and is deliberately NOT recorded: the launcher stops a Director by raising that named signal, so the
signal is the evidence that something outside this process acted on the ask. Widening it to "anything
that stops us" would turn the person closing the window into a launcher's acceptance.

### What the owner sees now

`RestartAccepted` was already wired all the way through and needed no new word anywhere:

- the progress screen leaves the message standing and offers no "back" button, because the process is
  about to be stopped (`SmartShutdownCoordinator.OnRunFinished`). Before the fix it offered a back
  button that the launcher would kill the owner's click on;
- the command line exits **0** for `RestartAccepted` (`tools/cc-devthrottle/src/smart_restart_ops.py`),
  where it had been exiting non-zero for a good run.

### The tests, in plain words

Both are in `src/CcDirector.Gateway.UnitTests/Drain/SmartShutdownCancelIgnoreRestartTests.cs`.

- **`Start_WithTheRestartPurpose_ALauncherThatAnswersByStoppingThisDirector_EndsRestartAccepted`** - a
  launcher that answers the only way the real one does: it raises this Director's shutdown signal and
  then the ask dies with a cancellation and no answer. The run must end `RestartAccepted`, must say the
  launcher accepted and that it answered by stopping this Director, must NOT say "restart it by hand"
  or "its answer never came back" - and the record must still be there, uncancelled, with its
  handed-over seat still decided "restore".
- **`Start_WithTheRestartPurpose_AnAskThatDiesWithNoStopBehindIt_IsStillRestartRefused`** (two cases) -
  the ask dies and NOTHING asked this process to stop, and the ask dies in a process that was ALREADY
  stopping before the launcher was asked. Both must still end `RestartRefused`, with the error's own
  words, and must not claim the launcher accepted.

### The revert proof

The source was committed first (`e5a597141`), then mutated, then restored in a `finally` so a failure
mid-run could not leave the mutation behind; every run built, none used `--no-build`; and the restored
source was re-run and re-built afterwards.

Disabling the recognition (`if (false && stoppedByTheLauncher)`) and running the whole
`SmartShutdownCancelIgnoreRestartTests` class:

```
Failed  Start_WithTheRestartPurpose_ALauncherThatAnswersByStoppingThisDirector_EndsRestartAccepted
Failed! - Failed: 1, Passed: 28, Skipped: 0, Total: 29
```

**Exactly one test fails, and it is the new one.** The two refusal cases pass while the recognition is
gone, which is what says they are not passing because of it.

---

## 3. Half two - issue #3258: "newest first" was by when the record was last WRITTEN

### The cause

`DirectorWayUp.CandidatesAsync` ordered by `w.UpdatedUtc` then `w.CreatedUtc`. A Gateway summary row
carries no shutdown time at all - that lives on the document - so the order was by when each record was
last written, and any mark on an old record (a restore that failed, a reopen, a clearing) moved it to
the top. In the owner's own output a record shut down on 12 September sat above one shut down on 20
September, under a message saying "newest first" and above rows each showing its own shutdown time.
`FindOfferAsync` walked that same list and offered the first record that qualified, so a touched older
record could be offered ahead of the one just made.

### What changed

One shared rule, `DirectorWayUp.CompareNewestShutdownFirst` - newest shutdown first, ties broken by the
record id so the order is total and one list never comes back in two orders. The history sorts the
documents it read by it. The offer picks by it. The window labelled "newest first" and the one record
the owner is interrupted with can no longer disagree about which record is newest.

`CandidatesAsync` keeps its `UpdatedUtc` order and its comment now says what that order is FOR: it
chooses WHICH records are read, and it is not the order anything is shown in.

### The start-up read is still one document, and that is not luck

The offer must not read all twenty-five documents at start-up - a tested property, and the reason the
cap exists at all. It still reads one on the ordinary start-up, exactly, because the walk stops on a
bound rather than on a guess:

> a record cannot be WRITTEN before the shutdown it records, so a candidate's `UpdatedUtc` is an upper
> bound on its shutdown moment - and the candidates arrive in `UpdatedUtc` order. So once a record that
> may be offered has a shutdown at or after the NEXT candidate's `UpdatedUtc`, nothing left in the list
> can have a newer shutdown, and the rest are not read.

On the ordinary start-up the newest record was written at its own shutdown and untouched since, so the
first document read ends the walk. When an older record HAS been written since, the walk reads on
exactly as far as a record that could still beat it - and no further.

### The tests, in plain words

Both hold two records whose **stored order and shutdown order disagree**, so a surface using either one
is distinguishable from a surface using the other. In both, the older record was written again a moment
ago because a restore of its seat was tried and failed - a real mark, which is what bumps `UpdatedUtc`
in the wild - and it still owes that seat, so it is still a record that may be offered.

- **`DirectorWayUpHistoryTests.The_history_is_ordered_by_the_shutdown_and_not_by_when_the_record_was_last_written`** -
  the Gateway lists the eight-day-old record first because it was written last; the history must show
  the four-hour-old one first, its first entry's shutdown moment must be later than its second's, and
  both records must have been read, because the order is decided from the documents.
- **`DirectorWayUpOfferTests.The_record_offered_is_the_one_with_the_newest_shutdown_not_the_one_written_last`** -
  the same disagreement, with both records offerable so neither wins by being the only candidate. The
  offer must be the one shut down an hour ago, carrying that shutdown moment, and the engine must have
  read on past the first record it could have offered.

### The revert proofs

Two, each mutating one line, each restored in a `finally`, each run building:

```
records.Sort(CompareNewestShutdownFirst) commented out, DirectorWayUpHistoryTests:
  Failed  The_history_is_ordered_by_the_shutdown_and_not_by_when_the_record_was_last_written
  Failed! - Failed: 1, Passed: 27, Skipped: 0, Total: 28

the offer put back on "the first record that qualifies", DirectorWayUpOfferTests:
  Failed  The_record_offered_is_the_one_with_the_newest_shutdown_not_the_one_written_last
  Failed! - Failed: 1, Passed: 33, Skipped: 0, Total: 34
```

Exactly one failure each, on the right test, and nothing else moved - including the existing test that
says the ordinary start-up reads ONE document, which passed throughout.

---

## 4. The wider gate, and the two failures that are NOT this change

`.\scripts\test-local.ps1` (default run) on this branch: every suite `outcome=Completed` except
`CcDirector.Launcher.Tests`, which reported 2 failed of 197:

```
LauncherDeclaredCapabilitiesTests.An_unarmed_launcher_declares_no_restart_signal_and_that_is_a_NO_not_an_unknown
LauncherDeclaredCapabilitiesTests.Describing_the_launcher_asks_the_signal_and_never_raises_it
```

**They fail the same way on `origin/main`, and that was observed rather than argued.** A clean worktree
was cut at `6d1a8651d`, the same class was run in it, and it reported the same 2 failed of 17. The
worktree was then removed. Both tests assert that NO launcher restart signal is armed on the machine
running them, and a launcher is running on this one (`cc-launcher.exe`, process 37316) - they are
host-bound, and this change touches no launcher code.

The gate also printed a COVERAGE GAP naming the three parked suites. `CcDirector.Gateway.UnitTests` is
where this change lives and it was run in full - see section 1. `CcDirector.Core.Tests` and
`CcDirector.Gateway.Tests` were not run; the only Core file this change adds is a new self-contained
class that nothing existing calls, and no Gateway code was touched.

---

## 5. What I could not reach, and what is still open

1. **Neither fix has been seen on a real restart on this machine, and by instruction.** The mandate
   forbade starting a Director or a rig, so no window was opened and no button was pressed. What is
   offered instead is the mechanism read out of the rig's own log, the tests, and the revert proofs.
   The first real proof of #3257 will be the owner's own run: a successful restart must now report
   ACCEPTED, and the command line must exit zero for it.
2. **The read-stop in the offer rests on an invariant that clock skew can bend.** A record's
   `UpdatedUtc` is stamped by the Gateway and its shutdown moment by the Director, so with the two
   clocks apart a record could in principle carry a shutdown moment a few seconds after its own
   `UpdatedUtc` and the walk could stop one candidate early. It can only ever mis-order records whose
   shutdowns are within that skew of each other. Reading every candidate document would remove it and
   cost twenty-five calls at every start-up, which is the cost the cap exists to avoid.
3. **The cap of twenty-five still selects by `UpdatedUtc`.** A record shut down long ago but written
   recently can take one of the twenty-five slots, so a Director with more than twenty-five records may
   not read the twenty-fifth-newest SHUTDOWN. The order of what IS read is now right; which records are
   read is unchanged. Fixing that needs the shutdown moment on the Gateway's summary row, which is a
   wire change needing a Gateway deploy as well as a release, and is not this change.
4. **Section 6.3 of the phase 5b report is untouched.** The refusal sentence still ends "it is offered
   when the Director is next started" on a record with nothing left to act on, which cannot be offered.
   It is a separate defect on a separate sentence and no issue was opened for it here.
5. **Nothing was done about run A's HTTP 409 race** (the crash journal 250 milliseconds stale). It did
   not recur in phase 5b and nothing here touches it.

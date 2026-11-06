# Review: pull request 4610 - a watcher test completes its held wait with no synchronization context

Reviewed 6 November 2026 by a separate review session. Test-only change: one file,
`src/CcDirector.Core.UnitTests/Sessions/IdleTreeInspectionWatcherTests.cs`, head 42ab718e9, base 761c24802.
The worktree under review was not edited; every run below happened in a scratch worktree cut at the head
commit, or in a scratch console project, under the session's temporary folder.

## Scope

1. Is the claimed cause real: xUnit's synchronization context makes `TaskCompletionSource.SetResult` queue
   the watcher's trailing inspection to the thread pool, so the old `await Task.Yield()` raced it?
2. Does the fix remove the race, or only narrow it?
3. Does `ManyParksInsideTheGap_ShareOneTrailingInspection` still prove what its name says: would it go red
   if the watcher stopped sharing one trailing inspection?
4. Do other tests in the class carry the same race?

## Verdict: APPROVE - merge

The cause is real and was proven directly, the fix leaves no second thread in the test at all, the test still
goes red under two watcher mutations, and no other test in the class completes a held wait.

## 1. The cause is real

The watcher's `InspectAsync` awaits the delay with `ConfigureAwait(false)`, so the continuation stored on the
held task is the bare state machine. When `SetResult` runs, .NET inlines that continuation on the completing
thread only when that thread has no non-default `SynchronizationContext` and no non-default scheduler;
otherwise it queues it to the thread pool. xUnit 2 (this project references `xunit 2.*`) installs its
`AsyncTestSyncContext` on the test thread for EVERY test, synchronous ones included, so the old test's
`SetResult` always queued the inspection, and `await Task.Yield()` then posted the test's own continuation
through that same xUnit context. Two independent queues, one on the pool and one on xUnit's workers: a race
by construction, lost whenever the pool thread was slower than the xUnit worker.

Proof, out of tree: a scratch console program with the same shape (a non-default context current at the
await, `ConfigureAwait(false)`, a plain `TaskCompletionSource`), 200 rounds each way:

| context present when SetResult runs | continuation ran inline before SetResult returned |
|---|---|
| yes (non-default context) | 0 of 200 |
| no (context cleared) | 200 of 200 |

Scratch project: `inline-proof\Program.cs`, in a scratch folder outside the repository.

One point in the fix's favour that the pull request body does not say: changing the test from `async Task`
to `void` would NOT have fixed this on its own, because xUnit 2 sets the context for synchronous tests too.
The helper's explicit clearing of the context is what does the work, and the fix does not depend on the
test being synchronous.

The original failure under machine load was NOT reproduced by this review. An attempt (the pre-fix test
body, 40 runs under 48 busy loops on 24 cores) was abandoned after 20 minutes because the load starved the
test host itself; the direct mechanism proof above stands in for it, since with a context present the
continuation was never inline in 200 tries, which is the whole race.

## 2. The fix removes the race rather than narrowing it

After the fix there is no second thread. `CompleteTheHeldWait` clears the context, calls `SetResult`, and
the continuation runs inline: the watcher checks disposal, takes its lock, calls the inspection delegate
(which appends to `_inspected` synchronously and returns a completed task), and returns. Both inspections
are in `_inspected` when the helper returns, with nothing left to wait for. The fixed test passed 20 of 20
single runs and the class passed 15 of 15.

Inlining rests on three facts: the `TaskCompletionSource` is created without
`RunContinuationsAsynchronously`, no non-default context is current, and the default scheduler is in
force. If any of those stopped being true the test would fail on EVERY run, not intermittently - a
deterministic red that names the test, which is the right failure mode for a harness assumption.

One coupling, advisory only: the test now depends on the watcher awaiting its delay with
`ConfigureAwait(false)`. If that were removed, the continuation would be posted to xUnit's context and run
after the asserts, so the test would go red while the product still shared one trailing inspection. The
helper's documentation comment names the `ConfigureAwait(false)` await, so the next reader is told. Not
worth a change: never resuming on the event thread is itself a property the watcher promises.

## 3. The test still proves its name

Two mutations of the watcher in the scratch worktree, each followed by a rebuild and a run of this one test,
then a restore and a green run:

| mutation of `IdleTreeInspectionWatcher` | result |
|---|---|
| the trailing inspection is dropped after its wait instead of run | RED: `Assert.Equal` collections differ, expected two inspections, actual one |
| the collapse guard removed, every ask inside the gap queues its own trailing wait | RED: `Assert.Single(_waits)` found 2 waits |
| restored | GREEN |

The first mutation is the one the fix touches: the after-completion assertion still bites.

## 4. Other tests in the class

None has the race. The only test that completes a held wait is the one fixed. The other tests that reach the
wait complete it with `Task.CompletedTask`, and an await of an already-completed task continues inline no
matter what context is current (`ASecondParkInsideTheGap_IsDeferredToTheEndOfIt`,
`TwoSessionsInOneFolderParkingTogether_CostOneInspectionAndOneTrailingOne`).
`SeveralWorktreesOfOneRepositoryParkingTogether_CostOneInspectionAndOneTrailingOne` holds its wait open
forever and asserts only before completion. The remaining tests never reach a wait.

Outside the class, one other test in the solution has `TrySetResult` followed by `await Task.Yield()`
(`src/CcDirector.Gateway.UnitTests/GatewayStreamRegistryTests.cs`, line 141). It is a different shape (the
signal is raised inside a producer loop the test awaits) and is outside this review's scope; noted only so
nobody assumes it was checked.

## What was run

- `dotnet run` of the scratch inline proof: exit 0, "200 of each, all as expected".
- Scratch worktree at 42ab718e9: class filter 15 passed; the fixed test 20 of 20 passed; two mutations red;
  restore green. The scratch worktree was removed after the review.

# Phase 1 - the order becomes correct

**The phase 1 row, from section 6 of the mission document:** the Gateway catalogue becomes the source
of the last-access time for the repository list, and the one-caller write on the desktop side stops
being what decides the order.

**The goal it serves, goal 1 of section 3:** starting a session from the Cockpit or the phone moves
that repository to the top of the Director's list. Before this change it did not move at all.

---

## 1. What the mission document claimed, and what the code says

Section 5 names the ground. Every statement was checked against the code before anything was built.

| The claim | Verdict | What the code says |
|---|---|---|
| `KnownRepositoryStore` holds, per tenant and per machine, every repository observed in a session with its time | **True** | `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` - `Observe` upserts per tenant, machine and path; `ReadForMachine` reads one machine's catalogue newest first. |
| Its writer is `SessionHistoryRecorder`, which runs for every session the Gateway sees whatever surface started it | **True, with one qualification** | `SessionHistoryRecorder.ObserveCore` calls `ObserveKnownRepository` on every write, and the Gateway wires the catalogue in for real (`GatewayHost.cs`, the recorder is constructed with `_knownRepositories`). The qualification: the write is throttled. First sight always writes, so every session start is observed; an unchanged re-push is not, which does not matter here. |
| `GET /directors/{id}/known-repositories` already serves the Gateway's record | **True** | Served by the Gateway; the phone already reads it. |
| `RepositoryRegistry.MarkUsed` has exactly one caller in the whole product - the desktop New Session dialog's own start path in `MainWindow.axaml.cs` | **True** | A search of the whole repository for `MarkUsed` returned the definition, its own log lines, one product call site (`MainWindow.axaml.cs`), and an unrelated private method of the same name in the dictation fuzzy matcher. Nothing else. |
| The remote create path, `SessionWriteExecutor`, does not touch it | **True** | The `create` verb runs `SessionCommandExecutor.Create`, which never reads `SessionCommandServices.Repositories`. Only `repo-add`, `repo-delete` and `repo-rename` touch the registry at all. |

**Nothing in section 5 was found to be wrong.**

### One thing section 5 does not mention, found while checking it

A session holding a **pooled worktree** runs in a throwaway slot, and its repository path reports that
slot - every reader of a session's repository path means "where the session is"
(`SessionManager.CreateSession`, and the same rule on `SessionDto.RepoPath`). The repository the slot
came out of is on `PooledWorktree.Repo` and on the wire as `SessionDto.PooledWorktree.Repo`.

So before this change, a pooled session recorded a use of the **slot**:

- the Gateway's catalogue gained a directory nobody would ever pick, which the pool deletes when it
  takes the slot back, while the real repository never moved up the list at all;
- and on the Director side the mark would have found no registered repository to mark.

That is the recency signal being wrong, which is this phase's subject, so it is fixed here rather than
left for phase 3 to serve a slot path as a repository. It is fixed with **one rule used by both
catalogues**, so they cannot drift apart later.

---

## 2. What changed

| File | Change |
|---|---|
| `src/CcDirector.Core/Configuration/RepositoryUsage.cs` | **New.** The one rule for which repository a session counts as a use of: the one it was started in, which for a pooled session is the repository the slot came out of, not the slot. |
| `src/CcDirector.Core/Configuration/RepositoryUsageRecorder.cs` | **New.** Subscribes to `SessionManager.OnSessionCreated` and marks that repository used. Every creation route funnels through `SessionManager.RaiseSessionCreated` - the desktop window, the Cockpit's and the phone's create verb, a schedule, an agent spawning a worker - so one subscription covers all of them, and a route added later is covered without anybody remembering to call anything. |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | Constructs the recorder when a registry is supplied, **in the constructor** rather than in `StartAsync`: the host is built before anything can create a session, whereas `StartAsync` runs on a background task and a session created while it was still running would be a use nobody recorded. Lets go of it in `StopAsync`. |
| `src/CcDirector.Avalonia/MainWindow.axaml.cs` | The single `registry?.MarkUsed(...)` call is **deleted**, and a comment says why and says not to put one back. The desktop button now records its use through the same one place as every other surface. |
| `src/CcDirector.Gateway/History/SessionHistoryRecorder.cs` | Observes the repository the session was **started in** rather than the path it runs in, through the same `RepositoryUsage.StartedIn` rule. |

**What was deliberately not done.** Phase 3 is the one that makes the Gateway serve the union already
ordered, and phase 6 is the one that makes the Director's dialog read the Gateway list. No client
changed what it reads here. Phase 1 is the recency signal becoming correct and complete.

**An honest limit on the phase 1 row's first sentence.** The Gateway catalogue is now a *correct and
complete* source of the last-access time - it sees every session start on every surface, and it records
the right repository. It does not yet *decide* the Director's order, because the Director's dialog
still reads its own registry; that is phase 6, and it is explicitly out of scope here. What this phase
removes is the defect underneath: the order on the Director was decided by one button, and it is now
decided by every session start, which is the same set of events the Gateway catalogue records.

---

## 3. The behavioural proof

The tests are the proof, and they are the flow **and** the failure cases, never one success run.

### The Director's list - `src/CcDirector.Core.Tests/RepositoryUsageRecorderTests.cs`

| Test | What it proves |
|---|---|
| `SessionCreated_ByAnyRoute_MarksItsRepositoryUsed` | **The phase 1 row.** Nothing in it is the desktop dialog: it announces a session the way the Cockpit's create verb, the phone, a schedule and an agent spawn all do, and the repository's last-used time is set. |
| `SessionCreated_MakesThatRepositoryTheMostRecentlyUsedOne` | The order, which is what the list is for: the newest session's repository sorts to the top. |
| `SessionCreated_WithoutTheRecorder_MarksNothing` | **The failure case, kept deliberately.** With no recorder subscribed - the state of the product before this phase - a session started by any route other than the desktop dialog left the last-used time untouched. |
| `SessionCreated_InAPooledWorktree_MarksTheRepositoryTheSlotCameFrom` | A pooled session moves the real repository, and does not put its throwaway slot in the catalogue. |
| `Record_RepositoryIsNotRegistered_RecordsNothingAndDoesNotThrow` | **Failure case.** A session in a folder this machine never registered: nothing to mark, and a picker catalogue is never the reason a session start fails. |
| `Record_SessionHasNoRepositoryAtAll_RecordsNothing` | **Failure case.** No repository at all is recorded as nothing, not as a blank entry. |
| `Dispose_StopsRecording` | The subscription is let go of. |
| `RepositoryUsageTests` (5 tests) | The shared rule itself, including the blank and unknown cases. |

### The wiring - `src/CcDirector.Gateway.UnitTests/RepositoryUsageIsWiredIntoTheDirectorTests.cs`

Deliberately separate. The tests above prove the recorder does the right thing when something
constructs it; these prove something does. **Without them the fix could be removed from the Director
entirely and every other test in the phase would still pass** - a proof of a component nobody calls.

| Test | What it proves |
|---|---|
| `A_host_with_a_registry_records_every_session_it_creates` | The Director wires it, and does so in the constructor - this host is never started. |
| `A_stopped_host_leaves_no_handler_behind` | **Failure case.** A host that is stopped and replaced does not go on writing into a registry nobody reads. |
| `A_host_with_no_registry_creates_sessions_without_recording_anything` | **Failure case.** No catalogue is not a failure; the session is still created. |

### The Gateway's catalogue - `src/CcDirector.Gateway.UnitTests/History/KnownRepositoryObservationTests.cs`

| Test | What it proves |
|---|---|
| `A_session_the_desktop_dialog_never_started_is_recorded_as_a_use` | The recency signal the mission builds on actually sees a session the desktop button never touched. |
| `The_newest_session_puts_its_repository_at_the_top` | The catalogue's order is most recently used first. |
| `A_pooled_session_records_the_repository_the_slot_came_from_not_the_slot` | The pooled defect above, fixed and held. |
| `A_session_with_no_repository_records_nothing` | **Failure case.** |
| `A_catalogue_that_is_not_wired_records_nothing_and_the_session_history_is_unharmed` | **Failure case.** The catalogue is an optional dependency of the recorder; with none, the session is still recorded as history. A catalogue hiccup never costs a session's row. |

---

## 4. Watching the proof fail

A proof nobody has watched fail is decoration. Each change was reverted on its own, the predicted
symptom was confirmed, the rest was confirmed still green, and the change was restored.

### Revert A - the Director does not wire the recorder

Commented out the construction in `ControlApiHost`.

```
[FAIL] CcDirector.Gateway.Tests.RepositoryUsageIsWiredIntoTheDirectorTests
       .A_host_with_a_registry_records_every_session_it_creates
   Assert.NotNull() Failure: Value of type 'Nullable<DateTime>' does not have a value
Failed!  - Failed: 1, Passed: 2, Skipped: 0, Total: 3
```

Predicted and confirmed: the recorder's own tests stayed green (12 passed) and the Gateway catalogue
tests stayed green (5 passed), because neither of those goes through the Director. That is exactly why
the wiring test exists.

### Revert B - the Gateway records the path the session runs in

Replaced the shared rule with `session.RepoPath` in `SessionHistoryRecorder`.

```
[FAIL] CcDirector.Gateway.Tests.History.KnownRepositoryObservationTests
       .A_pooled_session_records_the_repository_the_slot_came_from_not_the_slot
   Assert.Equal() Failure: Strings differ
   Expected: "/repos/devthrottle"
   Actual:   "/pool/devthrottle/wt01"
Failed!  - Failed: 1, Passed: 4, Skipped: 0, Total: 5
```

The throwaway slot in the catalogue, named in the failure message.

### Revert C - the Director records the path the session runs in

Passed `null` for the pooled repository in `RepositoryUsageRecorder`.

```
[FAIL] CcDirector.Core.Tests.RepositoryUsageRecorderTests
       .SessionCreated_InAPooledWorktree_MarksTheRepositoryTheSlotCameFrom
   Assert.NotNull() Failure: Value of type 'Nullable<DateTime>' does not have a value
Failed!  - Failed: 1, Passed: 11, Skipped: 0, Total: 12
```

The real repository never moved.

### The phase 1 defect itself

`SessionCreated_WithoutTheRecorder_MarksNothing` is that revert kept permanently as a test: it is the
product as it stood before this phase, and it asserts the symptom the mission opens with - the
repository does not move.

---

## 5. The mission check

Section 7's check, run from the worktree root on macOS, with the .NET suites run directly as section 7
writes them.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces. |
| `npm test --workspaces --if-present` | **Red - and identically red on clean `origin/main`.** See below. |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 7 failed, 6353 passed. **The same 7 fail on clean `origin/main`** (6345 passed there; the 8 extra passes here are this phase's new tests). |
| `dotnet test src/CcDirector.Core.Tests` | **Green.** 0 failed, 4457 passed, 18 skipped. |
| `dotnet test src/CcDirector.Avalonia.Tests` | 7 failed, 543 passed. **The same 7 fail on clean `origin/main`**, name for name. |

**This is not a baseline being quoted to excuse a red run.** The mission forbids that, and rightly. It
is a measurement: a worktree was cut at `origin/main`, built, and run, and the failing sets came back
identical. The failures are owned by two other seats the Delivery Lead has already opened for exactly
this - one on the web suites and one on the .NET suites on macOS - so nothing here is unowned.

### What is red, and which it is - a defect in the test or in the product

Every one of these is **a defect in the test**, not in the product, and every one of them is a test
that asserts Windows or Linux behaviour without saying so.

**The web suites (97 failing tests across client-core, cockpit and mobile).** `localStorage` is
`undefined` inside the jsdom environment on this machine. The runner says why on its own first line:

```
(node:xxxxx) ExperimentalWarning: localStorage is not available because --localstorage-file was not provided.
```

This machine runs Node 26.5.0; continuous integration runs Node 22 (`.github/workflows/ci.yml`). Node
24 and later ship their own `localStorage` global that is absent unless `--localstorage-file` is given,
and it shadows the one the test environment provides. The tests read the ambient global and so cannot
run on a current Node.

**The .NET suites (14 failing tests).** All Windows-only behaviour asserted on macOS:

- `SessionCommandExecutorLivenessTests` (3) start a child process with `cmd.exe`.
- `CronJobStoreTests.LegacyJson_RenameFailsAfterImport_...` and
  `WorkListStorePersistenceTests.Import_RenameAsideFails_...` make a rename fail by holding the file
  open, which is Windows file-locking semantics - POSIX renames an open file happily.
- `RulePrimitivesTests.IsPathInside_follows_a_link_that_stays_inside_the_root` and
  `RuleCandidateFilterTests.A_rule_scoped_to_this_sessions_repository_is_a_candidate` turn on path and
  link resolution that differs on macOS.
- `MicCaptureConstructionQueriesNoDeviceTests`, `SpeakDialogCloseDuringStartupTests` and
  `SpeakDialogReadyCueBlankingTests` (5) are audio-device tests on a machine with no such device.

**None of them is reachable from anything this phase touched**, which the reverts above also show: each
revert moved exactly one named test and nothing else.

### What this proof does not cover

- No screen was drawn and no screenshot is owed: phase 1 changes no user interface. The Director's
  dialog, the Cockpit and the phone all read exactly what they read before.
- The end-to-end path - a session started from the Cockpit against a live Director, moving the
  repository in that Director's own dialog - is not exercised here. It is proven at the two ends the
  code has: the Gateway records the use from the pushed session, and the Director records it from the
  creation event every remote create raises. The screen that reads it is phase 6.
- `npm test` has never run green on this machine, before or after this change, so it says nothing
  about this phase either way. It is owned by another seat.

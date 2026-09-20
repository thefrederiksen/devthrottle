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
| `src/CcDirector.Core/Configuration/RepositoryRegistry.cs` | **Serialized, and written atomically.** Answering the review's finding 1: this phase turns a one-thread writer into a many-thread one, and unsynchronized that could have emptied the user's list. Every entry point now takes one gate, reads hand back a snapshot instead of a live view, the file is written through a temporary file and one move, and an unreadable file is said out loud instead of being silently replaced. Section 6 has the reasoning. |

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
writes them. **Re-run in full after the rebase onto `origin/main` and after the concurrency fix**, so
these are the counts for the branch as it stands, not the counts the first run produced.

| Command | Result |
|---|---|
| `npm run typecheck` | **Green.** All four workspaces. |
| `npm test --workspaces --if-present` | **Green. 2,117 tests, 0 failures** - client-core 1,453, cc-assistant 106, cockpit 457, mobile 101. |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 7 failed, 6,366 passed, 8 skipped. |
| `dotnet test src/CcDirector.Core.Tests` | **Green.** 0 failed, 4,464 passed, 18 skipped. |
| `dotnet test src/CcDirector.Avalonia.Tests` | 7 failed, 543 passed. |

**The 97 web failures are gone, and they were never this branch's.** The first run of this check found
97 failing web tests, on a machine whose Node 26 shadows the test environment's `localStorage`. The fix
(`020f117f5`) landed on `origin/main` afterwards; rebasing onto it turned all four workspaces green with
no change to this branch's own code. That was predicted, and it is what happened.

**The 14 .NET failures remain, and they are owned elsewhere.** They are the same 14, name for name, the
Reviewer reproduced with its own full runs of both suites. Every one of them is a defect in the test
rather than in the product: each asserts Windows behaviour, or the presence of an audio device, without
saying so. A separate branch is fixing them and this phase must not touch them. They are itemised
below, corrected.

**This is not a baseline being quoted to excuse a red run.** The mission forbids that, and rightly - and
the rule is still, strictly, unmet on this machine. What is offered is a measurement, not an excuse:
the sets are named one by one with their causes, the Reviewer re-ran both suites and got the identical
sets, and every failing test class was searched for the symbols this phase touches with zero hits.

### What is red, and which it is - a defect in the test or in the product

Every one of these is **a defect in the test**, not in the product, and every one of them is a test
that asserts Windows behaviour, or the presence of an audio device, without saying so.

**The web suites - FIXED, and green here now.** The first run of this check found 97 failing tests
across client-core, cockpit and mobile: `localStorage` was `undefined` inside the jsdom environment,
because this machine runs Node 26.5.0 and Node 24 and later ship their own `localStorage` global that
is absent unless `--localstorage-file` is given, shadowing the one the test environment provides. The
runner said so on its own first line:

```
(node:xxxxx) ExperimentalWarning: localStorage is not available because --localstorage-file was not provided.
```

That was the other seat's to fix, it fixed it (`020f117f5`), and rebasing this branch onto `origin/main`
picked the fix up. All four workspaces are green, 2,117 tests, zero failures. Nothing in this branch
changed to make that happen - it touches no web file at all.

**The .NET suites (14 failing tests).** All host behaviour asserted without saying so - Windows
semantics, or a device this machine does not have:

- `SessionCommandExecutorLivenessTests` (3) start a child process with `cmd.exe`.
- `CronJobStoreTests.LegacyJson_RenameFailsAfterImport_...` and
  `WorkListStorePersistenceTests.Import_RenameAsideFails_...` make a rename fail by holding the file
  open, which is Windows file-locking semantics - POSIX renames an open file happily.
- `RulePrimitivesTests.IsPathInside_follows_a_link_that_stays_inside_the_root` and
  `RuleCandidateFilterTests.A_rule_scoped_to_this_sessions_repository_is_a_candidate` turn on path and
  link resolution that differs on macOS.
- `MicCaptureConstructionQueriesNoDeviceTests` (2), `SpeakDialogCloseDuringStartupTests` (1) and
  `SpeakDialogReadyCueBlankingTests` (3) are **six** audio-device tests on a machine with no such
  device. The first two fail with `DllNotFoundException: Unable to load shared library 'winmm.dll'`;
  the other four time out waiting for a device that is not there.
- `LegacyWorkspaceImportTests.An_imported_workspace_keeps_the_name_agent_colour_arguments_and_order_of_every_seat`
  is the seventh Avalonia failure. Its expected value carries a baked-in Windows path, so
  `Path.GetFileName` - which only understands the host's own separator - returns the whole string on
  macOS: `Expected: "devthrottle_internal"`, `Actual: "D:\\ReposFred\\devthrottle_internal"`.
  Environmental, and unrelated to anything this phase touched.

**None of them is reachable from anything this phase touched**, which the reverts above also show: each
revert moved exactly one named test and nothing else.

### What this proof does not cover

- No screen was drawn and no screenshot is owed: phase 1 changes no user interface. The Director's
  dialog, the Cockpit and the phone all read exactly what they read before.
- The end-to-end path - a session started from the Cockpit against a live Director, moving the
  repository in that Director's own dialog - is not exercised here. It is proven at the two ends the
  code has: the Gateway records the use from the pushed session, and the Director records it from the
  creation event every remote create raises. The screen that reads it is phase 6.
- `npm test` is green here now, but this branch touches no web file, so it says nothing about this
  phase either way. It says the other seat's Node fix works.

---

## 6. The review's findings, answered

The review is `docs/missions/one-repository-list/reviews/phase-1-review.md`. Law 11: every finding is
answered by the seat that built the work, accepted or declined with the reason, and a finding is never
closed by the seat that raised it. Both are **accepted**.

### Finding 1 - the registry becomes a multi-threaded, high-frequency writer with no locking. ACCEPTED, and fixed in this branch.

**The finding is right, and it matters more here than it would anywhere else.** Before this phase
`MarkUsed` had exactly one caller, on the Avalonia user-interface thread, so its writes were serialized
by accident. This phase makes it fire on whatever thread created the session - a tunnel command thread
serving the Cockpit or the phone, a restore thread, a schedule - on every session start on the machine,
which is the most frequent event in the product. The end of that road is an empty repository list, and
an empty repository list is the exact defect this mission exists to stop. Opening a second door to it
while closing the first would be a poor trade.

The Reviewer is also right that the race's *class* pre-dates the phase: `repo-add`, `repo-delete` and
`repo-rename` already called `Save` from tunnel threads. That is the reason the fix is at the root and
not at the new call site. **Guarding only the recorder would have left `repo-add` racing `repo-rename`
- the same defect with fewer witnesses** - so nothing was guarded at the call site at all.

**What was done, and why each part of it.** All of it in `RepositoryRegistry`:

| The change | Why this and not something else |
|---|---|
| One process-wide lock, taken by every public entry point, held across the whole mutate-and-save | The shared state is a plain `List<RepositoryConfig>` and one file, and the invariant is that the file matches a list that actually existed. A lock that covered only the file would still lose entries in the list; one that covered only the list would still interleave the writes. The critical section is short and bounded - a list scan and one small file - which is what `docs/CodingStyle.md` asks of a `lock`. |
| `Repositories` hands back a snapshot instead of `AsReadOnly()` | A live view is the same defect wearing the reader's hat: `CatalogReadExecutor`, `RepoStatePusher` and the New Session dialog all enumerate it, and a concurrent add throws `"Collection was modified"` at whichever of them is mid-enumeration. `docs/CodingStyle.md`: *snapshot before iterating across threads*. |
| The file is written through a temporary file and a single move | **This is the part the lock cannot do.** One machine runs several Directors off the same root (`CLAUDE.md`, rule 0b), so another *process* can write this file at any moment, and a lock in this process does not reach it. A plain `File.WriteAllText` truncates the destination before it writes, so any reader in that window sees an empty or half-written list - which is precisely how "the list is silently wiped" happens. With a move, a reader sees the old list or the new one. Across processes the outcome is last-writer-wins; what it can never be is a torn file. The temporary name carries a fresh identifier, because a fixed one would itself be the thing two writers race over. This is already the repository's idiom - `CcDirectorConfigService.WriteAtomic` and `SessionHookFiles.WriteAtomic` do the same. |
| `MarkUsed` raises its change notification *after* releasing the lock | That notification runs the user interface's binding handlers. A handler that came back into the registry while the gate was held would be waiting on the thread already inside it. |
| `SeedFrom` takes the gate once for the whole batch | A reader must never see half a seed. The lock is re-entrant, so the `TryAdd` calls inside it take it again harmlessly. |

**What this does NOT cover, said plainly.** The lock protects the list and the file. It does not make
a single `RepositoryConfig` object thread-safe: `MarkUsed` assigns `LastUsed` on an entry that a reader
on another thread may be reading at that instant. That is a torn read of one optional date, it is
exactly as true before this phase as after it, it cannot corrupt the file, and making those objects
immutable is a different change - they carry `INotifyPropertyChanged` for the desktop list, so the
bound object has to be the shared one. It is named here rather than left for someone to discover.

**The tests, and why they are not timing races.** `src/CcDirector.Core.Tests/RepositoryRegistryConcurrencyTests.cs`,
seven tests. The instruction was to prefer one test that reliably proves writes are serialized over one
that hopes to catch an interleaving, and none of these hopes for anything:

| Test | What it proves, and how it is decided rather than raced |
|---|---|
| `A_second_thread_cannot_write_while_one_thread_is_inside_a_write` | **The core proof.** `SeedFrom` walks the enumerable it is given while holding the gate, so an enumerable that blocks parks a writer *inside* a write for as long as the test likes. A second thread then tries to write. With the gate held it cannot finish - not on a fast machine and not on a slow one - and the file on disk, read without going through the registry, does not contain its entry. Release the gate and the write lands in full. There is no window to miss. Both threads signal that they started, so the test cannot pass by neither of them running. |
| `Every_write_from_every_thread_survives_and_the_file_stays_readable` | 8 threads, 200 writes, each followed by a read. The assertion is the final state - every entry present, in memory and in a fresh read of the file - not any particular interleaving. |
| `A_reader_of_the_file_never_sees_a_half_written_list` | The cross-process harm. A reader loops over the file, sharing it the way a careful second process would, while four threads write. Every read must be complete and parseable. |
| `A_list_a_caller_is_holding_is_not_disturbed_by_a_later_write` | The reader defect, with **no threads at all**: hold the list, write, then use what you are holding. A live view throws on the spot, so this is decided, not raced. |
| `Writing_leaves_no_temporary_files_behind` | A guard on the new mechanism: a leftover temporary beside the list would be read back as rubbish by the next scan. |
| `Load_UnreadableFile_ThrowsAndLeavesTheFileAlone` | The silent catch, gone - see below. |
| `Load_EmptyFile_ReadsAsAnEmptyList` | The failure case of that change: an empty file is not an unreadable one, and must not stop the Director starting. |

**The silent catch in `Load` - made loud, because it was cheap.** It was fallback programming of the
worst kind: a parse failure started with an empty list, and the very next save wrote that emptiness
over the only copy the user had. It now throws, names the file, says the file has been left alone, and
logs `Load FAILED` first. Three things make this cheap rather than a sprawl:

- **There is one product caller.** `App.InitializeServices`, and it already runs inside the startup
  guard that writes a crash file and shows the error - so the loud path exists and is reached.
- **It is this repository's own settled rule for this exact kind of file.** `CcDirectorConfigService`
  says it in as many words: *a malformed config.json THROWS rather than being silently reset. We never
  overwrite a file we couldn't parse - that would destroy the user's data to hide a problem.*
- **An empty or whitespace-only file is not a parse failure** and reads as an empty list, mirroring
  `CcDirectorConfigService.ReadRaw`. Without that, a zero-byte file left behind by the old
  non-atomic write - the very thing being fixed - would stop the Director starting.

The consequence is stated rather than buried: **a genuinely unreadable `repositories.json` now stops
the Director at startup with an error naming the file, instead of starting with an empty list.** That
is the intended trade - the list is recoverable, and a silent wipe is not - and with the atomic write
above, our own writes can no longer produce that file.

### Finding 2 - the proof's itemization of the 14 red tests is wrong in two places. ACCEPTED, and corrected.

Both corrections are in section 5, verified against a fresh full run of `CcDirector.Avalonia.Tests` on
this branch rather than taken from the review:

- The audio group is **six** tests, not five: `MicCaptureConstructionQueriesNoDeviceTests` (2),
  `SpeakDialogCloseDuringStartupTests` (1), `SpeakDialogReadyCueBlankingTests` (3).
- The seventh is now named:
  `LegacyWorkspaceImportTests.An_imported_workspace_keeps_the_name_agent_colour_arguments_and_order_of_every_seat`.
  Its failure was read, not assumed: `Expected: "devthrottle_internal"`, `Actual:
  "D:\ReposFred\devthrottle_internal"` - the test bakes in a Windows path, and `Path.GetFileName` only
  understands the host's own separator. Environmental, and unrelated to this change.

The counts were right and the verdict is unchanged. The point is taken as made: the proof is the record
the next seat trusts, and a seat sent looking for five audio tests would have found six and an unnamed
seventh.

### The note to the Delivery Lead, acknowledged

The review notes that `RepositoryUsageIsWiredIntoTheDirectorTests` lives in `CcDirector.Gateway.UnitTests`,
which the default `scripts/test-local.ps1` run does not execute. That is true, it is not a finding, and
nothing is changed for it: the mission check names that suite explicitly, so the phase is gated on it,
and `CLAUDE.md` already tells anyone touching the Gateway to run `-Parked`.

---

## 7. Watching the concurrency proof fail

Same rule as section 4: each part of the fix was reverted on its own, the predicted symptom was
confirmed, and the change was restored. All three reverts were run on this branch after the rebase.

### Revert D - take the lock away

Every `lock (_gate)` replaced by `if (true)`, leaving the class otherwise exactly as it is.

```
[FAIL] RepositoryRegistryConcurrencyTests.A_second_thread_cannot_write_while_one_thread_is_inside_a_write
   a second thread completed a write while another thread was inside one

[FAIL] RepositoryRegistryConcurrencyTests.Every_write_from_every_thread_survives_and_the_file_stays_readable
   Assert.Equal() Failure: Values differ
   Expected: 200
   Actual:   195
Failed!  - Failed: 2, Passed: 5, Skipped: 0, Total: 7
```

**Five of two hundred writes vanished** - the user's entries, lost in a plain `List.Add` that two
threads entered at once. That is the harm, measured, in the shape the user would eventually see it.
The other five tests stayed green, including the torn-file one, because the atomic write is a separate
mechanism answering a separate writer.

### Revert E - put the silent catch back

`catch (JsonException) { /* If the file is corrupt, start fresh */ }`, exactly as it read before.

```
[FAIL] RepositoryRegistryConcurrencyTests.Load_UnreadableFile_ThrowsAndLeavesTheFileAlone
   Assert.Throws() Failure: No exception was thrown
Failed!  - Failed: 1, Passed: 20, Skipped: 0, Total: 21
```

Nothing was said, and the next save would have written the empty list over the user's file.

### Revert F - write the file in place again

The temporary file and the move replaced by `File.WriteAllText(FilePath, json)`.

```
[FAIL] RepositoryRegistryConcurrencyTests.A_reader_of_the_file_never_sees_a_half_written_list
   Assert.Empty() Failure: Collection was not empty
   Collection: ["empty", "empty", "[\n  {\n    \"Name\": \"seed\",\n    \"Path\": \"/"..., ...]
Failed!  - Failed: 1, Passed: 6, Skipped: 0, Total: 7
```

The reader caught the file **empty**, twice, and half-written several times more. An empty read is what
another Director on this machine would have taken for an empty repository list. The six other tests
stayed green: the lock does not reach another process, which is exactly why this mechanism is there.

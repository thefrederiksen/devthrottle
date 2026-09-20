# Phase 1 review - the order becomes correct

**Branch:** `mission/one-repo-list-phase-1` (one commit, `99b17716f`, based on `74485174f`)
**Reviewed against:** `origin/main` at `0b282aafc`
**Mandate:** `docs/missions/one-repository-list/MISSION.md`, phase 1's row: *the Gateway catalogue becomes
the source of the last-access time for the repository list, and the one-caller write on the desktop side
stops being what decides the order.* Conduct: the DevThrottle Method, Reviewer seat, and the repository
`CLAUDE.md` (Critical Rules) and `docs/CodingStyle.md`.

**Verdict: the phase is built as its row describes, its central claim holds, and the proof proves what it
says. Two findings follow - one real defect in the change's blast radius, one inaccuracy in the proof's
own text. Neither, in my judgement, undoes the phase; the seat that built the work answers both.**

---

## 1. Scope - what this review read, ran, and could not reach

**Read in full:** the diff (9 files, +870/-5); `RepositoryUsage.cs`, `RepositoryUsageRecorder.cs`, both new
test files and `KnownRepositoryObservationTests`; the modified regions of `ControlApiHost.cs`,
`MainWindow.axaml.cs`, `SessionHistoryRecorder.cs`; the proof
(`docs/missions/one-repository-list/proofs/phase-1/README.md`).

**Read to check the central claim, not believed from the proof:** `SessionManager.cs` - every roster
insertion and every `RaiseSessionCreated` call site; every production caller of `CreateSession`,
`CreatePipeModeSession`, `CreateGitHubActionsSession`, `CreateEmbeddedSession`, the restore paths,
`SessionCommandExecutor.Create`, `SessionWriteExecutor`'s handover target, `QueueGitExecutor`,
`MachineSessionSpawner`/`SessionVerbClient`, `ControlEndpoints`' `SessionDto` mapper, `App.axaml.cs`'s
construction of `ControlApiHost`, `RepositoryRegistry.cs`, `RepositoryConfig.cs`,
`KnownRepositoryStore.Observe`, `GatewayHost`'s wiring of `SessionHistoryRecorder`, and the onboarding
wizard's session start (it hands off to the main window, which goes through `SessionManager`).

**Ran (this machine, macOS, Node 26.5.0, .NET 10.0.301):**

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Core.Tests` (full) | Green: 0 failed, 4457 passed, 18 skipped |
| `dotnet test src/CcDirector.Gateway.UnitTests` (full) | 7 failed, 6353 passed, 8 skipped - the same seven, name for name, as the proof |
| `dotnet test src/CcDirector.Avalonia.Tests` (full) | 7 failed, 543 passed - same counts as the proof |
| The 20 new tests, filtered | 12 + 8, all pass |
| **Revert A, re-run by me** (disable the recorder construction in `ControlApiHost`, then restore) | `A_host_with_a_registry_records_every_session_it_creates` fails with the exact predicted symptom (`Nullable<DateTime>` has no value); 1 failed, 2 passed, total 3 - identical to the proof's report. Restored, green again, `git status` clean |
| `npm run typecheck` | Green, all four workspaces |
| `npm test --workspaces --if-present` | 23 + 38 + 36 = 97 failing tests, every worker printing Node 26's own `localStorage is not available` warning - the failure mode the proof names, and the branch touches no web code at all |

**Could not reach:** a live end-to-end run (a session started from the Cockpit against a live Director) -
the proof itself says that is not exercised here and lands in phase 6; and I did not build a separate
worktree at `origin/main` to re-measure the baseline. For the 14 red .NET tests I did not need one: their
failure messages name their own causes (below), and I ran the full suites myself. I also did not run
`scripts/test-local.ps1` (Windows script; this is macOS) - the mission check in section 7 is what I ran,
as written.

---

## 2. The central claim, checked rather than believed

**Claim: every creation route funnels through `SessionManager.RaiseSessionCreated`, so one subscription
covers them all. This claim holds.**

I verified it from the code, not the proof:

- `SessionManager` inserts into its roster in exactly six places (lines 1019, 1224, 1270, 1298, 1746,
  1847), and each is immediately followed by `RaiseSessionCreated`. `RaiseSessionCreated` is the only
  place `OnSessionCreated` is invoked.
- The desktop window, the quick-launch cards, and the onboarding wizard's finish all reach
  `SessionManager.CreateSession`. The tunnel create verb reaches `SessionCommandExecutor.Create` →
  `SessionManager.CreateSession`; the handover target in `SessionWriteExecutor` likewise; schedules and
  Gateway-driven spawns (`MachineSessionSpawner` → `SessionVerbClient.CreateSessionAsync` → tunnel → the
  create verb) reach the same place. `QueueGitExecutor` reaches `CreateGitHubActionsSession`, which raises
  on its own. The restore-after-restart paths raise as well.
- `SessionManager.CreateSession` itself can take a pooled worktree slot at birth
  (`AcquirePooledWorktree`), so the desktop path goes through the same `RepositoryUsage.StartedIn` rule
  as every other surface - no desktop-specific hole.

So the recency signal is complete on the Director side, and the "one subscription covers a route added
later" reasoning is sound as well as true for today's routes.

---

## 3. The specific questions this review was sent

**Desktop `MarkUsed` deletion - no regression.** The deleted line was the only product caller of
`MarkUsed`; I searched the whole tree and found no other caller and no reader that depended on that
particular write (the dialog builds its own entries from `RepositoryRegistry.LastUsed`, which the new
recorder still writes, via the same registry instance - `App.RepositoryRegistry` is the one object both
`MainWindow` and `ControlApiHost` receive). For a desktop session born in a pooled slot the new path
marks the same repository the old path did (`PooledWorktree.Repo` is the repo the slot came from, and the
old code marked the dialog's selected path, which was that same repo). The desktop button records its
use through the new path, with the constructor wired before `StartAsync` - and the constructor-vs-`StartAsync`
reasoning is correct: `App.axaml.cs` runs `StartAsync` on a background task, so a session created while
it was still starting would have gone unrecorded. Release is proper: `StopAsync` disposes the recorder
inside a logged try-catch, and `DisposeAsync` delegates to `StopAsync`, so every teardown path releases it.

**The shared rule.** `RepositoryUsage.StartedIn` really is one rule: the Director's recorder and the
Gateway's `SessionHistoryRecorder` both call it, and nothing else computes a repository-of-use. The wire
carries `SessionDto.PooledWorktree.Repo` (`ControlEndpoints.cs`), so the Gateway has the input it needs.

**Started-in rather than runs-in.** Confirmed at both ends; a pooled session's `RepoPath` *is* the slot by
design (the `SessionManager` comment says so), so the change in `SessionHistoryRecorder` is a real
correction, not a cosmetic one.

**The pooled fix riding along - my judgement: it belongs here, and I argue it rather than assume it.**
The mission's out-of-scope list says "any change to what a repository is, or to worktrees". This fix
changes neither: no worktree behaviour changed and no repository definition changed - what changed is
which *path* the recency signal credits. Phase 1's own row says the Gateway catalogue becomes *the source
of the last-access time*; a catalogue that records throwaway slot paths is not a source of anything -
it holds directories the pool later deletes while the real repository never moves. Phase 3 would then
serve those slot paths to every screen. A phase 1 that left this in would hand the mission a "correct"
signal that is wrong for every pooled session on the machine. It is the minimum needed for the phase's
statement to be true, and it is covered by tests at both ends.

**The honest limit - stated accurately.** The proof's claim is precise: the Gateway catalogue is a correct
and complete *record*; it does not yet *decide* the Director's order, because the dialog still reads its
own registry until phase 6. I verified nothing in the change or the proof overclaims: no client reads
anything new, and the change's own comments say the desktop write moved rather than the screen's source.
The limit is exactly where the proof says it is.

**The baseline reasoning for the 14 red .NET tests - holds, and I tested it rather than assumed it.** I
ran the full suites: the counts match the proof exactly (Gateway 7/6353, Avalonia 7/543). Every failure's
own message names an environmental cause: `cmd.exe` does not exist on this host (3), "this test produces a
live-but-unreadable process using Windows process security, and this host is not Windows" (1), POSIX
rename-while-open semantics (2), macOS path/link resolution (2), no audio device (6 on Avalonia)... and
one the proof does not name - see Finding 2. I also grepped every failing test class for the symbols this
change touches (`RepositoryRegistry`, `RepositoryUsage`, `MarkUsed`, `KnownRepository`,
`SessionHistoryRecorder`, `ControlApiHost`): zero hits in all of them. Combined with my revert run moving
exactly one named test and nothing else, I am satisfied none of the 14 is reachable from this change.
The mission's "no baseline is to be quoted" rule is still, strictly, unmet on this machine - that is the
Delivery Lead's deviation to own, and the proof hides nothing about it.

**Reverts.** I re-ran Revert A myself (the one the whole phase rests on) and watched it fail with the
identical symptom and counts the proof reported, then restored and re-ran green. A proof outside its
author's hands has now watched it fail.

**Critical Rule 7 (the client is dumb).** Nothing in this change puts a ruling on a client: the Director's
recorder writes a local registry; the Gateway folds the repository in its own recorder. No `.tsx`/`.xaml`
view re-derives anything new. Compliant.

**Law 1 (no fallback programming).** No fallback was added anywhere. The `StartedIn` blank-pooled case
falls back to `repoPath`, but that is the honest answer for a session that is simply not pooled - and the
wire guarantees a pooled ref is complete or null (`DirectorRestore.cs`), so no silent repair is happening.

---

## 4. Findings

### Finding 1 - the change turns an unsynchronized file-backed registry into a multi-threaded, high-frequency writer (real defect, widenened from a pre-existing race)

**The harm.** `RepositoryRegistry` has no locking anywhere. `MarkUsed` mutates `repo.LastUsed` and calls
`Save()`, which enumerates the plain `List<RepositoryConfig>` and does `File.WriteAllText(FilePath, json)`
on the calling thread. Before this change `MarkUsed` had exactly one caller, on the Avalonia UI thread, so
its writes were effectively serialized. This change makes `MarkUsed` fire on **whatever thread created the
session** - tunnel command threads, restore threads - on **every session creation on the machine**
(`SessionCommandExecutor.DispatchAsync` holds no lock, and the UI thread can create a session at the same
moment a create verb arrives over the tunnel). Two concurrent writes now race:

- `File.WriteAllText` twice to one path: on Windows the second open throws a sharing-violation
  `IOException`; on POSIX the second open *truncates* the first writer's half-written file, and the tail
  of the longer snapshot can survive as garbage. `RepositoryRegistry.Load` then catches the parse failure
  **silently** ("If the file is corrupt, start fresh") - so the user's whole registered repository list is
  wiped without a word.
- `Save`'s enumeration racing a concurrent `TryAdd` (`repo-add` verb) throws
  `InvalidOperationException`("Collection was modified") - a lost recency write (caught and logged in the
  recorder) or a failed `repo-add` command.

**Why it must change.** The race is probabilistic, but this change multiplies its writer from "one button
on one thread" to "every session start on every thread", which is the single most frequent event in the
product. A repository list that can silently reset to empty is the very defect this mission exists to
stop, arriving from a different door. I note honestly: the race's *class* pre-dates this phase
(`repo-add`/`repo-delete`/`repo-rename` verbs already called `Save` from tunnel threads and could race the
UI thread's writes) - this phase widened it, it did not open it. Whether to fix it here (a lock in
`RepositoryRegistry`, or serialized writes) or hand it to the Delivery Lead as its own task is the
building seat's to answer; the harm is real either way.

### Finding 2 - the proof's itemization of the 14 red tests is wrong in two places (documentation accuracy)

The proof's counts are right - my own full runs reproduce them exactly. Its *itemization* is not:

- The Avalonia seven are listed as "MicCaptureConstructionQueriesNoDeviceTests, SpeakDialogCloseDuringStartupTests
  and SpeakDialogReadyCueBlankingTests (5) are audio-device tests" - that group is six tests (2 + 1 + 3),
  not five.
- The seventh Avalonia failure is never named: `LegacyWorkspaceImportTests.An_imported_workspace_keeps_
  the_name_agent_colour_arguments_and_order_of_every_seat`. I ran it: it fails because the test's expected
  value contains a baked-in Windows path (`D:\ReposFred\...`), so it is environmental and unrelated to
  this change - but a seat that goes looking for five audio tests will find six and an unnamed seventh,
  and the proof is the record the next seat trusts.

The seat that wrote the proof should correct its list. It changes nothing about the verdict.

### Not findings, noted for the Delivery Lead

- **The wiring tests live only in a parked suite.** The tests that prove the Director actually constructs
  the recorder (`RepositoryUsageIsWiredIntoTheDirectorTests`) are in `CcDirector.Gateway.UnitTests`, which
  the default `scripts/test-local.ps1` run does **not** execute. The mission check names the suite
  explicitly, so the phase is proven - but after merge, a default-gate run alone would not re-run them.
  CLAUDE.md already tells callers who touch the Gateway to run `-Parked`; nothing to fix here, only a
  thing to know.
- **The web red is exactly as described.** 97 failures, all in the mode the proof names (Node 26's
  `localStorage` warning on every worker), and this branch's diff contains no web file at all. The Node
  fix (`020f117f5`) is already on `origin/main` and arrives with the rebase the Delivery Lead has
  scheduled. I could not run the post-rebase state on this branch, since rebase is the Delivery Lead's to
  do - that is the one claim I take on trust from the fleet's own commits rather than my own run.

---

## 5. Verdict

Phase 1's row is met as written, and the proof is honest about where its authority ends. The central
claim - one subscription, every creation route - is true in the code, not just in the proof, and I have
now watched the phase's key revert fail with my own run. The pooled-worktree fix belongs in this phase.
The one real defect I found (the registry's concurrency) is a widened pre-existing race that the building
seat should answer; the other finding corrects the proof's text. Nothing here says the phase must not
merge once those are answered.

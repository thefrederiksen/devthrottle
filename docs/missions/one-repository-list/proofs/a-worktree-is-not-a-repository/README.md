# A worktree is not a repository

Mission: One repository list, held on the Gateway. **Not one of the six phases.** A follow-on the owner
asked for on 20 September 2026, after he opened the Cockpit's New Session dialog against a Windows
machine and read

> "556 repositories on this machine, most recently used first."

and said: *"here you are showing the work trees. We should only be showing the repos. That's what we're
doing in the other version."*

**The rule he agreed: a worktree is not a repository. Using a worktree of `devthrottle` is using
`devthrottle`.**

The measurements that came before any of this was written are in `MEASUREMENTS.md`; the predictions
written before any guard was attacked are in `predicted-symptoms.md`; what the attacks actually did - two
of them nothing at all - is in `watched-it-fail.md`.

---

## 1. The ground, measured before anything was built

`MEASUREMENTS.md` has it in full. The three numbers that decided the shape of the work:

| The Windows machine the owner opened | |
|---|---|
| catalogue rows served | **559** |
| rows that are a scanned REPOSITORY | 71 |
| rows that are a LIVE WORKTREE, of just four repositories | **110** |
| rows pointing at folders that no longer exist | **378** |
| of its top twenty rows - the part a person reads - worktrees | **13** |

Every row he named by hand is a live worktree and every one of them resolves.

**And the brief was wrong about the size of the defect, which was worth finding before building.** Two
thirds of that list is not worktrees at all; it is dead folders, and that half is **already fixed on
`main` and reaching nobody**: the forgetting rule is `516b1ce9b` (#3223) and it is not an ancestor of
`v2.8.1`, the newest release. It shows in the live data - not one served row on any of the three machines
carries a root-folder listing, which is the fact that authorises forgetting. **The Gateway has the rule
and no Director is sending it the data.** Reported to the Delivery Lead with the measurement before a
line was written; he verified the tag history himself and took the four numbers to the owner:

> 559 today; **449** with this rule; **274** with both; **71** is what is actually on disk.

## 2. What the Delivery Lead ruled

Part one - resolve at session start - was already agreed with the owner. Part two - collapsing the rows
that are already there - was brought to him with the measurement and a recommendation, and **he ruled it
in with four conditions**, every one of which is now in the code and pinned by a test:

1. **The guard is `.git` IS A FILE, never "git can answer"** - and that sentence goes beside the guard in
   the code, not only in a record. Asked from a plain sub-folder, git cheerfully names the repository
   above it.
2. **A submodule is a repository and is left alone** - pinned with a test even though the protection is
   currently free, because "free" is what stops being true when somebody refactors.
3. **Positive resolution only.** A dead parent, a dangling `.git`, a missing folder, a bare repository's
   worktree: the Director says nothing about them rather than saying they are gone.
4. **Name the safety property in the code in the words it is ruled in**, as the forgetting rule does.

He also ruled the REGISTRY door shut the same way, and asked for a **recommendation, not a build**, on
the rows that sit more than one level below a registered root. That recommendation is section 7.

## 3. What was built

### Part one - the rule at session start, so row 560 is never written

| File | Change |
|---|---|
| `src/CcDirector.Core/Git/LinkedWorktree.cs` | **New.** The one rule: which repository a folder is a linked worktree OF, and null for everything else. |
| `src/CcDirector.Core/Sessions/Session.cs` | `PrimaryRepoPath` - the resolved repository, beside `RepoPath` exactly as `PooledWorktree.Repo` is. `RepoPath` still means where the session IS. |
| `src/CcDirector.Core/Sessions/SessionManager.cs` | Stamps it in `RaiseSessionCreated`, the one place every creation route funnels through, before the subscribers run - because two of them are what record a repository use. |
| `src/CcDirector.Core/Configuration/RepositoryUsage.cs` | The shared rule takes a third argument, pooled first, then the resolved repository, then the folder. **No default**, so the compiler is the guard. |
| `src/CcDirector.Core/Configuration/RepositoryUsageRecorder.cs`, `src/CcDirector.Gateway/History/SessionHistoryRecorder.cs` | Both catalogues read it through that one rule. |
| `src/CcDirector.Gateway.Contracts/SessionDto.cs`, `src/CcDirector.ControlApi/ControlEndpoints.cs` | It travels on the session, because only the Director can read it. |
| `src/CcDirector.Core/Configuration/RepositoryRegistry.cs` | `TryAdd` resolves - Browse and the `repo-add` verb register the repository. `SeedFrom` stays exact. |
| `src/CcDirector.Core/Git/RepositoryMonitor.cs` | Its own resolver now calls the shared step, so there is one rule for "which working tree does this git directory belong to". |

**Why it reads the `.git` file rather than asking git.** It is on the session-creation path, where a
person is waiting: a file read is microseconds and starting a process is not (CLAUDE.md rule 1). It also
works on a machine with no git installed, which this product supports, and where asking git would answer
"I cannot tell" for every worktree on the disk and put all of them back in the list. The monitor reaches
the same question from the other side, on a background scan where a process costs nothing and where it
wants git's own resolved spelling; the one step they share is written once.

**The ordinary session pays nothing.** A repository's `.git` is a directory and only a worktree's is a
file, so a session started in a clone is answered by one file-existence test.

### Part two - the collapse, so the 110 rows that are already there go

| File | Change |
|---|---|
| `src/CcDirector.Gateway/History/KnownRepositoryStore.cs` | `WorktreeOfRepository`; `ObserveDiscovered` takes the Director's statements and folds a worktree's row into its repository's, keeping the newer time. |
| `src/CcDirector.Gateway/History/DiscoveredRepositoryObserver.cs` | Reads those statements off the push, and puts them in the unchanged-re-push signature. |

**There is no new field on the wire, and no Director needs upgrading for it.** The Director's scan
already computes every scanned repository's worktrees with git, on the machine that holds the disk, and
they already ride this push - so the statement "that folder is a worktree of this repository" was there
for the taking. One feed, no new push, no new endpoint, no new tunnel verb. **It starts working on a
Gateway deploy alone**, which is the opposite of the forgetting rule's position.

**THE SAFETY PROPERTY, in the words it was ruled in and in the code: the Gateway collapses a row only on
a POSITIVE OBSERVATION FROM THE DIRECTOR that the folder is a worktree of a named parent; SILENCE IS
NEVER PERMISSION.** Three further conditions hold it shut: a real observation only, the named repository
must be in the same push, and the machine is the scope.

**What is lost, plainly.** A collapsed row's own last-access time disappears into the repository's when
the repository's is newer. No undo, no tombstone - the same posture as the forgetting rule. The Delivery
Lead accepted it as the right semantics rather than a regrettable loss: the repository's most recent use
IS the true answer.

## 4. What it does to the real lists

Simulated against the **live catalogue and the live pushes** read on 20 September 2026, applying the
rules exactly as the code applies them:

| Machine | rows today | rows after the collapse |
|---|---|---|
| the Windows machine the owner opened | 559 | **449** (110 collapsed, onto 4 repositories) |
| this Mac | 94 | 89 |
| the Windows laptop | 31 | 30 |

**And the part he will actually see: the top of his list.** Today thirteen of its top twenty rows are
worktrees. After the collapse the first four rows are `devthrottle`, `mindzieWeb`,
`devthrottle_internal` and `private` - **four repositories, no worktrees.**

**What is still wrong at row five, and it is not this work's to fix.** The rows below are worktrees that
have been DELETED - folders that no longer exist. Nothing can resolve them, so nothing here touches them;
they are the forgetting rule's, and that rule reaches nobody until a Director carrying it ships. Said
plainly so nobody reads "449" as "a handful".

## 5. The proof - the flow AND the failure cases

**100 tests, all of which RAN** - measured by name in filtered runs rather than inferred from a suite
total, because 100 passing and 100 skipped produce the same green banner.

### The rule itself - `LinkedWorktreeTests` (29), Core.Tests

Real repositories and real `git worktree add` on a real disk for the flow, because a rule about git's own
record of itself proved against a layout the test invented would prove only that the two agree. A
worktree beside its repository and a worktree in a folder called `worktrees` - the owner's two examples,
and **the first has no such folder anywhere in it**, which is what says the answer does not come from the
path's text. Then the failure cases: a repository proper, a plain folder INSIDE a repository, a worktree
whose repository was deleted, a `.git` file pointing at nothing, a **submodule**, a **bare repository's
worktree**, a relative `gitdir`, a file that says something else entirely, a folder that does not exist,
no path at all. And the two steps on their own over both separators.

### At the Director - `AWorktreeIsNotARepositoryTests` (12), Core.Tests

The stamp on the session, through `RaiseSessionCreated`; a repository proper carrying nothing; a worktree
whose repository is gone; a folder that is not a repository at all (**a real row: one machine's catalogue
holds its own registered root folder, because a session was once started there**); a resolver that throws
leaving the session created; a restore resolved against the disk as it is now. Then what the Director's
own list records: the REPOSITORY moves and the worktree does not, an unresolvable worktree still moves
itself, and **a pooled session still credits the repository the slot came from**. Then the registry door:
a worktree added by hand registers the repository, a clone and a plain folder register as themselves.
And one KNOWN LIMIT stated as a test rather than hidden - a repository registered through a symbolic link
is not matched, because git records the real path and the registry compares lexically.

### The precedence - `RepositoryUsageTests` (10), Core.Tests

Including **a wrong rule nothing removed**: a pooled slot that is also a worktree still answers with the
pool's own repository, so the test fails if anyone ever reorders the three.

### The wire - `AWorktreeIsNotARepositoryWireTests` (3), Gateway.UnitTests

Separate on purpose. Every test above hands itself the answer; **delete one line from the mapper and the
feature stops working in the product while eighty-nine of them still pass**, because the Gateway is
forbidden to work the answer out for itself.

### The Gateway's catalogue - `KnownRepositoryObservationTests` (+4), Gateway.UnitTests

A session in a worktree records the repository; many worktrees of one repository are one row that moves;
a worktree the Director could not resolve records the worktree; and the pooled precedence again at this
end.

### The collapse - `TheCatalogueCollapsesAWorktreeTests` (17), Gateway.UnitTests

The flow (four rows become one, carrying the newest time - including **one that is not under any listed
root**, because what decides this is the statement and never where the folder sits); the repository's own
newer time winning; a never-opened repository becoming used; a never-opened worktree collapsing without
inventing a time; a repository this push is the first to report; the same folder written two ways. Then
every way of saying nothing: no statements at all, a folder the Director did not name, a push that is not
a real observation, **a folder INSIDE the repository**, a repository named as a worktree of itself, blank
statements, another machine's row, another Director on the same machine. And the two destructive rules
meeting on one row.

### At the observer - `DiscoveredRepositoryObserverTests` (+4)

The statements taken off the push; a warm-start push collapsing nothing; a worktree appearing defeating
the unchanged-re-push skip; and the saving not lost when git lists the same worktrees in a different
order.

### End to end - `AWorktreeIsNotARepositoryTunnelProofTests` (6), the PARKED `Gateway.Tests`

**Real repositories and real `git worktree add` on a real disk**, the Director's own `SessionManager`
resolving and `ControlEndpoints.Map` putting the answer on the wire, its own
`ControlApiHost.SnapshotRepositories` building the repository push, a real SignalR tunnel to a started
`GatewayHost`, and reads over real HTTP through the one route a client calls.

| Test | What it proves |
|---|---|
| `SessionsInWorktrees_AreServedAsTheirOneRepository` | **The flow.** Two sessions in two worktrees, one repository served |
| `TheWorktreeRowsAnOldDirectorLeftBehind_AreCollapsedIntoTheirRepository` | **The collapse.** The catalogue filled the way it really was filled, then shortened |
| `ADirectorThatNamesNoWorktrees_CollapsesNothing` | **Failure case.** Every Director in the field |
| `ASessionInAWorktreeWhoseRepositoryIsGone_IsServedAsTheWorktree` | **Failure case.** Nothing resolvable, nothing changed |
| `ASessionInAFolderThatIsNotARepository_IsServedAsThatFolder` | **Failure case.** A real row on a real machine |
| `ARepositoryIsNeverCollapsedIntoItself` | **Failure case.** git reports the primary working tree in its own worktree list |

## 6. The mission check (MISSION.md section 7)

Machine: Sorens Mac mini, macOS 25.5 (Darwin 25.5.0), Apple silicon, `dotnet` at `~/.dotnet/dotnet`. Run
from the worktree root, exactly as section 7 writes the five commands. **The exit code is reported beside
every count, because the banner and the exit code can disagree - an aborted run prints `Passed!` at the
bottom (`proofs/an-aborted-run-reports-passed.md`). Every log was searched for `aborted`, `crashed`,
`test host` and `was canceled`: ZERO hits in all five.**

| Command | Exit | Result |
|---|---|---|
| `npm run typecheck` | **0** | **Green.** All four workspaces |
| `npm test --workspaces --if-present` | **0** | **Green. 2,205 passed, 0 failed** - client-core 1,484, cc-assistant 106, cockpit 506, mobile 109 |
| `dotnet test src/CcDirector.Gateway.UnitTests` | **0** | **Green. 0 failed, 6,959 passed, 8 skipped, 6,967 total** |
| `dotnet test src/CcDirector.Core.Tests` | **0** | **Green. 0 failed, 4,564 passed, 18 skipped, 4,582 total** |
| `dotnet test src/CcDirector.Avalonia.Tests` | **0** | **Green. 0 failed, 800 passed, 0 skipped, 800 total** |

**ZERO FAILURES. No baseline is quoted, on any platform.**

**The counts were checked against the suites' real sizes rather than taken as read.** The brief gives
Gateway ~6,740, Core ~4,500 and Avalonia ~725; every run here is at or above that, so none is short.

**THE WEB SUITES REFUSE TO RUN ON THIS MACHINE'S DEFAULT NODE, and that is the product working.** This
machine's default is Node 26; the workspaces' own guard stops and says so rather than failing dozens of
tests for reasons that have nothing to do with the code. The run above is on **Node v22.23.2**, which is
what `.nvmrc` and continuous integration use. Run on Node 26 the command exits 1 having run no tests at
all - a red that is a refusal, not a failure, and it is named here so nobody reads a green as covering
both.

The skipped counts are stated beside the passed counts on purpose, because a skipped test reads exactly
like a passing one. None belongs to this change: the Gateway's eight are its architecture-gate and
PostgreSQL guards, and Core's eighteen are its Windows-only path tests, a real-transcription end-to-end,
a pyenv inventory and a worktree reaper test needing an enforced file lock.

**The new tests all RAN, measured by name**: **51** in the three Core classes and **49** in the four
Gateway unit classes, both filtered runs exit 0 with **0 skipped**, plus the parked suite's six in section 5.

### The PARKED suite, run explicitly

`CcDirector.Gateway.Tests` is PARKED: it is not in the mission check and `scripts/test-local.ps1` does
not run it by default. This work's end-to-end proof lives in it, so it was run explicitly rather than
left to ride a gate that never looks at it - **and it was run beside every other end-to-end proof this
mission owns**, because a change to the store they all read could break one of theirs as easily as one
of mine.

```
dotnet test src/CcDirector.Gateway.Tests --filter "...TunnelProof...|KnownRepositoryEndpointTests"
exit=0
Passed!  - Failed: 0, Passed: 31, Skipped: 0, Total: 31, Duration: 25 s
```

**31 of 31, and ZERO SKIPPED** - stated as loudly as the passed count, because a skipped test reads
exactly like a passing one in every report we produce. That is this work's six, the catalogue-forgets
seven, phase 2's, phase 3's, the registry work's and the endpoint fold's. No `aborted`, `crashed`,
`test host` or `was canceled` anywhere in the log.

**WHAT I DID NOT DO, said plainly: I did not complete a run of the WHOLE parked suite.** One was started
and abandoned after about thirty minutes having reached roughly a fifth of the way; at that point it had
**29 failures, none of them in anything this work touches** - Wingman verbs riding the tunnel, the voice
sweep, spawn lineage, hosted route refusals, the skills register, and the suite's own exclusive-lock
tests. Those are the families `OUTCOME.md` already hands up as **red on `main` on macOS**, in a parked
suite nothing in the working loop runs. They are not mine, I have not fixed them, and I am not quoting
them as a baseline for anything: what I am claiming is the filtered run above, and the limit of that
claim is that the rest of the suite was not exercised on this branch.


## 7. The rows more than one level below a root - a RECOMMENDATION, not a build

The Delivery Lead asked for the shape I would choose and what it costs, before anyone writes it.

**What they are.** 203 of the Windows machine's 559 rows sit more than one level below a registered root:
under `D:\ReposMindzie\worktrees`, `D:\ReposFred\worktrees`, `D:\ReposFred\devthrottle.worktrees`, and
half a dozen more. The forgetting rule compares a row's DIRECT PARENT with the roots the Director listed,
deliberately - a prefix test would let a broad root claim a folder it never looked at - so **a row two
levels down is never forgotten however long its folder has been gone.** After this work and the
forgetting rule together, they are 203 of the 274 rows that survive.

**My recommendation: do nothing more, and let the two rules already built take the live half.** 16 of
those 203 are live worktrees, and **this work collapses all 16 already**, because a statement about a
folder says nothing about where the folder sits. What is left is dead folders nobody can resolve, and
every rule that would reach them is a rule that reaches further than any Director has positively
described.

**If it must be done, the only shape I would write is this, and it is the narrowest one available:** the
Director already lists each registered root's direct children; let it also list, for each child folder
that is ITSELF a folder of folders rather than a repository, that folder's own children - one level
deeper, and only under a root it could read. The forgetting rule then treats those listed folders as
covered parents exactly as it treats a root. It is the same positive-listing evidence, extended by one
level, with the same "a folder I could not read is omitted entirely" property.

**What it costs, and why I am not recommending it.** The listing grows from the children of a root to the
children of a root plus the children of every folder under it that is not a repository - on the machine
measured, roughly 175 paths becoming roughly 400, on a push that fires all day. More importantly it
**doubles the reach of a destructive rule to recover rows that cost nothing to keep**: a dead row is one
line in a list nobody scrolls to, and the list is ordered by recency, so they sit at the bottom for ever.
The whole defect the owner reported was at the TOP of the list, and the two rules that are built clear
it.

**What would change my recommendation:** the owner saying the count itself bothers him. "556" is a number
on a screen, and if reading it is the problem then the deep rows are worth the reach. That is his call
and not mine.

## 8. What this proof does NOT cover

- **PostgreSQL.** This machine has no Docker, so every database assertion above ran on SQLite and the
  PostgreSQL-backed proofs reported SKIPPED - which in a report reads exactly like a pass. This work adds
  no migration, no `ORDER BY` and no query; the collapse is a scan over rows the store already
  materializes. **That is a reasoned claim about the code, not a measurement.**
- **The live Gateway.** Nothing was written to it. Section 4's figures are a SIMULATION of the new rules
  over real rows and real pushes read on 20 September 2026, run in Python. It is the strongest statement
  available without a deploy and it is not the same as having seen the hosted Gateway serve it.
- **Windows.** Every test here ran on macOS. The rule reads `.git` with the host's own file APIs on the
  machine that owns the path, which is correct on both, and the path arithmetic is proved over Windows
  spellings in `LinkedWorktreeTests`. No Windows machine ran it.
- **A live Director process.** `ControlApiHost.SnapshotRepositories` is driven directly, as the
  catalogue-forgets proof also did. The debounce and the periodic tick that carry a push up the tunnel
  are unchanged and are not exercised.
- **Anything a screen shows.** No client code changed and nothing was seen running on a phone, a Cockpit
  or a desktop dialog.
- **The symbolic-link limit**, which is stated as a test rather than fixed: a repository registered
  through a link is not matched by the resolved answer on the Director's own registry, so its last-used
  time does not move there. It does not bite on any root this fleet uses.
- **Volume.** A machine with hundreds of worktrees sends a statement per worktree on every push that is
  not skipped. They were already on the push; nothing here measures the cost of reading them.

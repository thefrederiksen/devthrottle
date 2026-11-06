# Review of pull request 4647: the shared probe contract proves it read the machine twice

Mission "Resource Monitor", issue #2811. Reviewer seat, run on the Mac mini (10 cores, load average between 11 and 12 for the whole run), from a worktree at the branch head `bbb6ece9e`, with every fact about shipped code read from `origin/main` at `a09edb1e7`.

## Verdict

**Approve with changes.** The hole is real and I reproduced it. The fix closes it for the two tests that read through `ReadWithRates`. There is no blocking finding. There are three should-fix findings: the assertion orders two wall-clock stamps, which the product's own documentation forbids; the third test in the same file waits out its own hard-coded interval and the new hook does not reach it; and the choice of virtual moves detection of a missing declaration from the build to the one run that this very pull request shows nobody was watching.

## The evidence, reproduced

I did not take the author's numbers. On this branch there is no macOS derivation of the contract (it lives on pull request 4641, unmerged), so I copied `MacOsResourceProbeContractLiveTests.cs` from that branch into the worktree for the duration of the runs, and removed it after. Nothing from these runs is committed. The opt-in was `CC_DIRECTOR_LIVE_MACHINE_TESTS=1`, the build was `-p:EnableWindowsTargeting=true`, and `TMPDIR` pointed under `/private/tmp`.

| Case | Contract file | macOS cache | Override | Result |
|---|---|---|---|---|
| A | base (`origin/main`) | 3 seconds | none | **4 passed.** The two `ReadWithRates` tests passed in 1 second each: a single reading of the machine certified green. **The hole is real.** |
| B | this branch | 3 seconds | none | **2 failed** (`ASampleFromThisMachine...` and `AProcessWhoseFiguresAreRefused...`), each naming two byte-identical stamps, `2026-11-06T17:04:01.4022670Z` twice, and the remedy. |
| C | this branch | 3 seconds | `ProbeCacheFor => MacOsResourceProbe.CacheFor` | **4 passed**, the two `ReadWithRates` tests in 3 seconds each: the duration is the wait. |
| D | this branch | real 1 second | none | **4 passed**, 0 skipped, unchanged timings. No regression. |
| E | this branch | 4 seconds | declared correctly | **1 failed**: `PerProcessProcessor_IsAShareOfTheWholeMachine_NotOfOneCore`, with the message "listed this test's own process but gave it no processor figure, while it was holding a core flat out." See finding 2. |

Case A is the one the author asked me to break. It did not break: the base passes on a stale second read exactly as claimed.

## The five questions, answered

### 1. Is the assertion sound? Does any probe re-stamp on a cache hit?

No probe re-stamps. All three cache paths on `origin/main` hand back the stored record itself, unchanged:

- `src/CcDirector.Core/Machine/LinuxResourceProbe.cs:109` - `if (_cached is { } cached && ... < CacheFor) return cached;`
- `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:154-157` - `sample = cached;` under the one lock, and `now` is only consumed on the fresh branch.
- `src/CcDirector.Core/Machine/MachineMemoryProbe.cs:71` - `return _cached;`

So a cache hit has the first sample's `TakenAtUtc` on both platforms, and the assertion catches it. Case B shows it catching it on macOS. On Linux there is a detail worth knowing: the Linux probe does not read memory through `MachineMemoryProbe` at all. It parses `/proc/meminfo` itself with the static `ParseProcMemInfo(lines, now)`, so the "longer of the two caches" reasoning only has teeth on macOS, where `_memory` defaults to `MachineMemoryProbe.Shared` (a singleton whose cache spans every test in the process). That is harmless; it just means the Linux half of the comment is describing a coupling that does not exist there.

### 2. Can the assertion fire on correct code?

**Yes, in one case, and it is the case the product's own documentation names.** `second.TakenAtUtc > first.TakenAtUtc` is an ORDERING of two wall-clock stamps. The parameter documentation on `ResourceSample.TakenAtUtc` (`src/CcDirector.Core/Machine/ResourceSample.cs:26-30`) reads: "NEVER used to age a cache or to measure an interval ... comparing two of these stamps is the exact defect this interface was changed to remove." `IResourceProbe` says why: time synchronisation steps the wall clock. Both probes take `now` from `_time.GetUtcNow()`, the wall clock, while the cache ages on the monotonic timestamp. A step backwards of more than the 1.25-second wait between the two reads gives a legitimately fresh second sample whose stamp is EARLIER than the first. The test then fails on correct code, and the failure message says something false: "returned the SAME sample twice."

I could not reproduce this without stepping the system clock on a shared machine, which I did not do. The exposure is by the product's own written rule, and the fix costs nothing: see finding 1.

Equal stamps at coarse clock granularity cannot happen on a fresh read: the two reads are at least 1.25 seconds apart and `GetUtcNow` resolves far finer than that on every platform .NET 10 runs on. `>` versus `>=` is therefore not the issue; ordering at all is.

### 3. Is virtual the wrong choice?

The default is not a fallback that hides a problem in the sense of rule 3: when it is too short, the assertion fails loudly and names the override. Case B proves that. What virtual changes is WHEN a missing declaration is caught, and that is where I would push back.

- Virtual: caught by a live assertion, only on the platform in question, only when someone sets the opt-in and runs this project. That is exactly the population of runs this pull request shows had a hole for its whole life without anyone seeing it.
- Abstract: caught by the solution build on every machine, including the gate's build and the two-hourly full run on main, before the Windows seat ever runs a live test. The Linux derivation gets one line in this pull request (`=> LinuxResourceProbe.CacheFor`), and pull request 4641 gets one line when it rebases, which it must do anyway: its branch is behind main by the whole Resource Summaries work.

The strongest defence of virtual is one the pull request does not make: `IResourceProbe.Read` promises "a cached sample up to one second old" (`src/CcDirector.Core/Machine/IResourceProbe.cs:53-56`). By the interface's own terms every probe caches for at most `MachineMemoryProbe.CacheFor`, so the default is the interface's promise, not a guess, and a Windows probe caching longer would be breaking the interface before it broke this test. If virtual stays, that is the sentence the doc comment should carry. Either way the author decides; I rank it should-fix because the whole pull request is an argument that run-time-only detection in a deliberately-run suite is not good enough.

### 4. The wait arithmetic

No finding. Load cannot shorten this wait, only lengthen it. `Thread.Sleep` never returns early. Each probe takes its timestamp BEFORE its pass over the machine and compares `GetElapsedTime(...) < CacheFor` strictly, so the elapsed time at the second read is the first pass's duration plus the sleep plus scheduling delay, all of which add. The margin could be one millisecond and the arithmetic would still hold; 250 milliseconds is fine. The one check in this file that the machine CAN fail under load is the per-process lower bound, which says so in its own message, and it passed on this Mac at load average 11 to 12.

### 5. Does it leave a sibling hole?

**Yes, in the same file.** `PerProcessProcessor_IsAShareOfTheWholeMachine_NotOfOneCore` reads, loads a core, sleeps a hard-coded 3 seconds, and reads again. The new hook does not reach it. Case E shows what happens with a 4-second cache that is declared correctly: the second read comes out of the cache, the first sample has no rates, `CpuPercent` is null, and the test fails blaming the probe for giving no processor figure while holding a core. It fails closed by accident, with a message that names the wrong culprit. See finding 2.

Elsewhere in the project:

- `LinuxResourceProbeLiveTests.ReadWithInterval` sleeps on `LinuxResourceProbe.CacheFor` plus 500 milliseconds. That is the probe's own constant, so it is correct by construction and is not the hole. It asserts nothing about having read twice, though; the same one-line presence assertion would make it fail closed if that constant ever stopped being the real cache.
- `MacOsResourceProbeLiveTests.Read_OnceTheCacheHasAged_IsAFreshSample` advances an injected clock. Not a hole.
- `MachineWork.cs` and `MacOsPlatformReaderLiveTests.cs` contain no sleeps.

## Findings

### Should fix

#### 1. The assertion orders two wall-clock stamps; the product's own documentation says never to

`ResourceProbeContractLiveTests.cs`, the new `Assert.True(second.TakenAtUtc > first.TakenAtUtc, ...)`.

The claim the test wants to make is "these are two distinct readings," and that is an INEQUALITY, not an ordering. Change `>` to `!=`. A cache hit returns the stored record with the stored stamp, so the stamps are equal and the assertion fails, exactly as in case B. A fresh read 1.25 seconds later has a different stamp whichever way the wall clock moved, so the assertion cannot fail on correct code. The message then stays true in every case it fires: equal stamps have only one cause.

What exposes it: a wall-clock step backwards of more than 1.25 seconds during the sleep. I did not reproduce it and would not on a shared machine; the rule it breaks is written on the type at `ResourceSample.cs:26-30`.

#### 2. The per-process test in the same file waits out its own hard-coded 3 seconds and the hook does not reach it

`ResourceProbeContractLiveTests.cs:115`, `Thread.Sleep(TimeSpan.FromSeconds(3))`.

The pull request says the change "makes the suite fail closed for every platform," but only two of the four tests read through `ReadWithRates`. The third has the same shape with a literal. Today it fails closed by luck, because a probe's FIRST sample has no rates and the test checks for one; but the failure message blames the probe ("gave it no processor figure"), which would send the Windows seat into the probe looking for a bug that is in the test.

What exposes it: case E. Set the macOS cache to 4 seconds, declare `ProbeCacheFor => MacOsResourceProbe.CacheFor`, run the four tests. Two pass in 4 seconds, the per-process one fails with the misleading message.

Fix: compute the wait once (the longer of `ProbeCacheFor` and `MachineMemoryProbe.CacheFor`, plus the margin), use it in `ReadWithRates`, and make the per-process sleep the longer of 3 seconds and that wait. Then assert distinct readings there the same way, so the message names the cache when the cache is the cause.

#### 3. Virtual defers detection of a missing declaration to the one run nobody was watching

`ResourceProbeContractLiveTests.cs`, `protected virtual TimeSpan ProbeCacheFor`.

Argued in full under question 3 above. Making it abstract is one line here for Linux and one line in pull request 4641 on its rebase, and moves the check from a deliberately-run live assertion to every build. If the author keeps virtual, write into the doc comment that the default IS the `IResourceProbe` promise of at most one second, which is the actual reason it is safe, rather than the Linux definition, which is a coincidence of the same kind the pull request complains about.

### Cosmetic

#### 4. The doc comment goes false the moment pull request 4641 merges

"the macOS probe declares its own one second independently and merely happens to agree today." Pull request 4641 ties `MacOsResourceProbe.CacheFor` to `MachineMemoryProbe.CacheFor`. Whichever merges second, this sentence is wrong on main for a while. Reword it so it does not depend on merge order.

#### 5. The message reports the requested sleep, not the measured wait

`waited {waited}` is the argument to `Thread.Sleep`. On a loaded machine the real wait is longer, and the message's claim "the wait was shorter than that cache" is an inference. Measure it with `Stopwatch.GetTimestamp()` and `Stopwatch.GetElapsedTime(...)` around the sleep so the message states a fact. With finding 1 applied, equal stamps have one cause and the sentence is correct, but it should still print what happened.

#### 6. The "longer of the two caches" comment describes a coupling Linux does not have

The Linux probe parses `/proc/meminfo` itself and never calls `MachineMemoryProbe.Read`. The comment is right for macOS. Say so, or say "a probe may read memory through that one" and leave it, but do not let a Linux reader go looking for the call.

## What this review did not cover

- Linux and Windows were not run. The reasoning for them is from the code on `origin/main`, not from a machine.
- The clock-step case in finding 1 was not reproduced; I was not willing to step the clock on the owner's Mac.
- Pull request 4641 was read only for the derivation file and the `MacOsResourceProbe` change; it was not reviewed.

## Scope

Read on `origin/main` at `a09edb1e7`: `IResourceProbe.cs`, `ResourceSample.cs`, `LinuxResourceProbe.cs` (constructor and `Read`), `MacOsResourceProbe.cs` (constructor, `Read`, `ReadUncached`, `ReadEverything`, `UnreadableSample`), `MachineMemoryProbe.cs` (`CacheFor`, `Read`), every file in `src/CcDirector.Core.MachineLiveTests`, and `LiveMachinePlatformAttribute.cs`. Read on the branch: the full `ResourceProbeContractLiveTests.cs` and the pull request diff. Read on `origin/resource-monitor/macos-contract-derivation`: `MacOsResourceProbeContractLiveTests.cs` and the `MacOsResourceProbe.cs` diff against main. Ran the five cases in the table above on this Mac. Confirmed the worktree is clean after the runs (`git status --short` empty, `MacOsResourceProbe.CacheFor` back at one second, the borrowed derivation file deleted).

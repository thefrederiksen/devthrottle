# Review of pull request 4641 - the macOS probe runs the shared probe contract, on the general platform gate

Reviewer: Resource Monitor - Reviewer - macOS contract derivation (4641), a seat from a different agent family than the author.
Reviewed at commit 986e23bef, the head of `resource-monitor/macos-contract-derivation`, base `8a69695d2` (origin/main at review time).
Reviewed on this Mac in Release, re-running every measurement the author reported rather than trusting it.

## Headline verdict

**No blocking findings. Approve, with five non-blocking findings, two of which are a false sentence and a
stale paragraph that should be corrected before merge because they are cheap and they are wrong.**

The change does what it says: the macOS contract class exists, derives the shared contract, names its platform,
and runs four real tests on this machine. The stand-in attribute is gone, nothing on the branch still refers to
it, and the general gate makes the whole project safe to run on any machine.

## What I measured myself (Release, `-p:EnableWindowsTargeting=true`, `DOTNET_ROOT=/Users/soren/.dotnet`)

| Run | Result |
|---|---|
| Whole project, `CC_DIRECTOR_LIVE_MACHINE_TESTS=1` | 54 passed, 9 skipped, 0 failed, 63 total |
| Whole project, variable absent | 14 passed, 49 skipped, 0 failed, 63 total |
| The four macOS contract tests, opt-in set | 4 passed, 0 skipped |
| Architecture guards in `CcDirector.Core.UnitTests` (filter `Architecture`) | 21 passed, 0 failed |

The nine skips with the opt-in set are exactly the Linux classes: four contract tests and five
`LinuxResourceProbeLiveTests`, each with the reason `Written for Linux; this machine is macOS.`

Without the opt-in, both the Linux and the macOS contract classes skip with
`Reads the real machine (...). Set CC_DIRECTOR_LIVE_MACHINE_TESTS=1 to run it.` - the opt-in reason first, as
the pull request body states.

The arithmetic also closes: 4 macOS contract + 18 platform-reader + 18 probe tests = 40 macOS live tests, 4 + 5 = 9
Linux live tests, 14 always-on gate tests; 40 + 9 + 14 = 63.

`git grep MacOsLiveMachineFact HEAD` returns nothing: no stale reference to the deleted attribute survives.

## The five questions the author asked, answered

### 1. Coverage lost by deleting `MacOsLiveMachineFactTests.cs` - NON-BLOCKING, one case is unpinned for the class shape this change introduces

The deleted file pinned four cases of one pure function. Per class, on main after this change:

| Deleted case | Where it lives now | Pinned? |
|---|---|---|
| On a Mac with the opt-in, it runs | `LiveMachineFactTests.AskedFor_WithNoCoreRequirement_Runs_EvenOnOneCore` (opt-in half) plus `Platform_ContractClassForThisMachine_Runs` (platform half, same `declaredFor == thisMachine` branch, exercised with Linux/Linux) | Yes |
| On a Mac without the opt-in, it skips and says how to run it | `WithoutTheOptIn_TheTestIsSkipped_AndTheReasonSaysWhatItReadsAndHowToRunIt` | Yes |
| Off a Mac, it skips whether or not the opt-in is set - **for a contract class** | `Platform_ContractClassForAnotherPlatform_SkipsAndNamesBoth` (`isContractClass: true`) | Yes |
| Off a Mac, it skips whether or not the opt-in is set - **for an ordinary live class that declares a platform** | Nothing. No test in `LiveMachineFactTests.cs` calls `Decide(declaredFor: X, isContractClass: false, thisMachine: Y)` with X different from Y. | **No** |
| Off a Mac, the reason does not promise the variable would help | Gone, and the behaviour is now the opposite (see question 2) | No, by design |

The unpinned row is the shape of `MacOsPlatformReaderLiveTests` and `MacOsResourceProbeLiveTests`: 36 tests, not
derived from the contract, carrying `[LiveMachinePlatform(MacOs)]`. Today `Decide` only consults
`isContractClass` when `declaredFor` is null, so the branch is shared with the contract case and the live run above
proves it (the five ordinary `LinuxResourceProbeLiveTests` skipped on this Mac with the opt-in set). But the
deleted test's own comment called this "THE BRANCH THAT MATTERS", and it is now proved only by a live run that
nobody runs by default. Concrete failure scenario: a future edit to `LiveMachinePlatformAttribute.Decide`
(`src/CcDirector.Core.MachineLiveTests/LiveMachinePlatformAttribute.cs:56-70`) that reads "only contract classes
are platform-checked" - a plausible simplification, since the error branch already keys on `isContractClass` -
passes every test in `LiveMachineFactTests.cs`, and on the Linux continuous-integration runner with the opt-in
set, 36 macOS tests run and fail. Fix: one test,
`Platform_AnOrdinaryLiveClassForAnotherPlatform_Skips`, calling
`Decide(LiveMachinePlatform.MacOs, isContractClass: false, thisMachine: LiveMachinePlatform.Linux)` and asserting
`Skip` is not null. Three lines, in a file this pull request does not touch but this pull request is what
introduces the class shape it protects.

Also unpinned, and not this change's job to fix: the composition `attribute skip ?? platform skip` in
`LiveMachineTestCase.GetSkipReason` (`LiveMachinePlatformAttribute.cs:109-110`) has no unit test, because it needs
the xunit discovery machinery. The live run is the only proof it composes at all.

### 2. The skip-reason ordering - NON-BLOCKING; noting it is defensible, but a pull request body is not a durable place to note it

Measured: off-platform without the opt-in, the reason is now the opt-in one, for every class. On a Linux machine
the reader is told to set `CC_DIRECTOR_LIVE_MACHINE_TESTS=1`; they do, re-run, and only then read `Written for
macOS; this machine is Linux.` The cost is one wasted run per reader per platform, never a wrong result. Before
this pull request the project was already inconsistent - the Linux classes from 4629 put the opt-in first, the
macOS classes put the platform first - so this change makes it consistent in the less helpful direction rather
than introducing the problem.

It is not a defect to ship. It is a defect to leave recorded only in a pull request body, which nobody reads in
six months. The ordering is a one-line swap in `LiveMachineTestCase.GetSkipReason` (platform decision first,
then `base.GetSkipReason`) plus one test, and its doc comment at `LiveMachinePlatformAttribute.cs:99-102` states
the current order as intended, so the seat that owns the gate chose it. The right call is a follow-up issue
naming the one-line change and the deleted attribute's reasoning, assigned to that seat, so the reasoning that
the deleted file carried in its doc comment is not lost with it.

### 3. The `reads` rewording - an improvement, with one nit

The old strings were written for a skip reason that already named the platform, so under the general gate they
would have read `Reads the real machine (IOKit exists only on macOS). Set ... to run it.` - a sentence about the
platform inside a sentence about the opt-in. The new strings name the thing read, and the platform-reader file
keeps the specific call in every one (`through vm.swapusage`, `from host_processor_info`, `through statfs`). No
information a reader needs was destroyed: the probe file's 18 tests all carried the same generic sentence
before, so nothing per-test was lost there either.

Nit, `MacOsPlatformReaderLiveTests.cs:446` and `:521`: `one process's own figures through proc_pidinfo` is attached
to `ReadProcess_ManyProcessesOnThisMachine_...` and `ReadProcess_AProcessThatDoesNotExist_...`, neither of which
reads one process's own figures. `process figures through proc_pidinfo` fits all three uses.

### 4. Does the contract class need more than `CreateProbe` and `ProbeName`? - NON-BLOCKING today, and the coupling is unpinned

The base class sleeps `MachineMemoryProbe.CacheFor + 250 ms` between its two reads
(`ResourceProbeContractLiveTests.cs:47-52`). The macOS probe caches for `MacOsResourceProbe.CacheFor`
(`src/CcDirector.Core/Machine/MacOsResourceProbe.cs:60`). Both are one second today, so the second read on this
run was a fresh sample and the four passes above are honest.

But they are two independent literals. The Linux probe writes `CacheFor = MachineMemoryProbe.CacheFor`
(`LinuxResourceProbe.cs:52`); the macOS probe writes `TimeSpan.FromSeconds(1)` and only says in a comment that
the reasoning is the same. Nothing asserts they are equal, and I found no unit test that does
(`MacOsResourceProbeSafetyTests.cs:81` uses the macOS constant alone). Concrete failure scenario: someone raises
the macOS cache to two seconds for a good reason; `ReadWithRates` sleeps 1.25 seconds and gets the cached first
sample back; `ASampleFromThisMachine_KeepsEveryRuleTheInterfaceStates` passes, because
`ResourceSampleContract.Violations` deliberately accepts a rate-less first sample (`ResourceSampleContract.cs:57-63`,
and `TheFirstSample_...` depends on that); `AProcessWhoseFiguresAreRefused_IsStillListedAndNamed` passes, because the
first sample already lists and names every process. Two of the four contract tests would then be reading one
sample twice and reporting green. Only `PerProcessProcessor_...` would notice, and only because it sleeps three
seconds on its own.

The derived class cannot fix this with what the base offers, so the answer to the question as asked is: no, two
members are enough for an honest run **today**. The fix that keeps it honest tomorrow is either
`MacOsResourceProbe.CacheFor = MachineMemoryProbe.CacheFor`, as Linux does, or a `CacheFor` hook on the contract
base that each derived class supplies and the sleep uses. The first is a one-line change in a file this pull
request does not touch; either is a follow-up, not a block.

### 5. Doc-comment claims that exceed what the code proves - NON-BLOCKING, but one sentence is false and should be fixed before merge

**False:** `MacOsResourceProbeContractLiveTests.cs:15-18` says the contract tests "were written against the
INTERFACE, by someone else, before this probe existed." Git history says otherwise: the macOS probe was first
committed on 15 October 2026 (`cede38396`, "The Mac resource probe itself, built on the shared reading types")
and the shared contract on 6 November 2026 (`42e1b0d64`, "the probe interface tells the truth about processes,
clocks and rates"). The probe existed for three weeks before the contract. "By someone else" I cannot verify
either way from history (same git author on both). The argument the paragraph makes - a contract written against
the interface can fail for a reason a single-platform suite cannot - stands without the false clause. Drop
"before this probe existed" and, unless it is known to be true, "by someone else".

**Overstated:** the same file (line 23) and the two reworked class comments say that off a Mac these tests would
be "running and failing on the first Mach call". For `MacOsResourceProbeContractLiveTests` and
`MacOsResourceProbeLiveTests` that is not what would happen: `MacOsResourceProbe.ReadUncached`
(`MacOsResourceProbe.cs:181-186`) checks the platform first and returns an unreadable sample, and the
constructor reads the timebase behind a platform check. The tests would fail on an assertion about
`CouldRead`, never on a Mach call. The sentence is true of `MacOsPlatformReaderLiveTests`, which calls the
reader directly. Harmless to the behaviour, but the comment claims a failure mode the code rules out.

**Accurate, checked:** "A contract class that declares no platform is reported as an error by the discoverer"
matches `LiveMachineFactDiscoverer.Discover`; "The probe calls Mach, IOKit, statfs and libproc" matches the
reader; `[LiveMachinePlatform]` is `Inherited = false` and the class is sealed, so the attribute is read where
it is declared.

## One thing the author did not ask about - NON-BLOCKING, stale guidance this pull request makes false

`missions/resource-monitor/qa-plan.md:225-239`, "A caveat on running the live project: the opt-in is not a
platform check", says the attribute does not check the platform, that a cross-platform run "is expected to show
failures that are not defects", and ends "do not run the quality assurance against it until it is fixed." The
hook it asks for landed in 4629 and this pull request is the change that puts the last platform on it. After
merge that paragraph tells the quality assurance seat not to run the thing this pull request made safe to run.
It should be rewritten to say the gate now decides per class and the whole-project command is the procedure.

## Things I checked and found nothing wrong with

- The deleted attribute's own doc comment said to delete it when the general gate landed; 4629 is merged; done.
- `Platform_EveryConcreteContractClassInThisProject_DeclaresItsPlatform` now covers the new class, so a macOS
  contract class that lost its attribute would fail an always-on test, not run and fail on Linux.
- `[Trait("Category", "Resources")]` on the contract class reaches the inherited tests; the two reworked files keep
  it per method as before.
- Each contract test builds its own `new MacOsResourceProbe()` rather than `Shared`, so no test sees another's
  cached sample.
- The pull request body's "no new warnings" claim: the Release build of the live project printed no warnings
  (`TreatWarningsAsErrors` is on in the project file, so a warning would have been a failed build).
- Nothing pushed, nothing merged, the author's worktree and the shared checkout untouched.

## Summary for the author

Merge it. Before you do, fix the false "before this probe existed" sentence and the stale quality assurance plan
paragraph; both are a minute each. Then file two follow-ups against the gate's seat: the one-line
skip-reason ordering with the deleted attribute's reasoning, and the missing `Decide(..., isContractClass: false, ...)`
off-platform test. And one against the macOS probe: alias `CacheFor` to `MachineMemoryProbe.CacheFor` so the
contract's sleep cannot silently drift past the probe's cache.

---

# Review of pull request 4642 - answer the four points the macOS probe review left open

Documentation only: one new file, `missions/resource-monitor/reviews/macos-narrow-probe-open-points-answer.md`.
Read with `gh pr diff 4642`. Every claim checked against origin/main code, the proof file
`missions/resource-monitor/phase-1/macos-narrow-check.txt`, the quality assurance plan, and the earlier review
documents. The author asked two questions: does it claim anything the evidence does not support, and is the last
section correct.

## Headline verdict

**One blocking finding, in point 1: the body says the probe landed inside the bracket every time; the proof file
says it fell outside the bracket in three of four runs.** The deliverable of this pull request is its claims, and
that sentence states the opposite of the measurement it cites. It is a one-sentence fix. **The last section's
reasoning is correct - do not withdraw it.** Three wording precisions on it, none of which change the finding.

## BLOCKING - point 1 misstates the measurement

`macos-narrow-probe-open-points-answer.md`, point 1, second paragraph: "the probe's available-memory figure landed
inside the bracket every time, but below the middle of it in three runs of four - 0.51, 1.36 and 0.73 percentage
points below."

The proof file, `macos-narrow-check.txt:156-162`, has a table whose last column is headed `probe outside`, with
0.51, 0.00, 1.36 and 0.73, and the sentence under it reads "Worst the probe fell outside the operating system's
own range: 1.36 percentage points". Line 169: "the probe fell BELOW the bracket in three of the four runs". The
original review, `macos-narrow-probe-review.md:276-277`, says "below the lower bound". So runs 1, 3 and 4 were
OUTSIDE the bracket, below its lower edge, and only run 2 was inside. The new document's own heading ("leans
consistently BELOW the operating system's range") and its closing section ("memory, bracketed, to within 1.36
points") both agree with the proof; the body paragraph does not.

Concrete failure: the next reader takes the body at its word, concludes the probe has never left the operating
system's own range, and drops the one-and-a-half-point caution the same point asks for. Fix: "landed below the
bracket's lower edge in three runs of four, by 0.51, 1.36 and 0.73 points, and inside it in the fourth."

Same paragraph, smaller: "on a machine whose available memory moves several points per second" is not measured
anywhere. The proof says the machine moved as much as 4.75 points across one bracketed read (line 163), and that
used memory "was falling" (line 172). Say that.

## NON-BLOCKING - precisions on the rest

**Point 2** - supported. The 4.75-point movement is `macos-narrow-check.txt:158,163`; "in both directions" is
supported by the table (run 2 rose, runs 3 and 4 fell). The 0.34 and 5.95 figures and the "means nothing in
either direction" lesson are `qa-plan.md:206-219`, quoted correctly.

**Point 3** - supported, with one imprecision. `FoldStall` (`MacOsResourceProbe.cs:519-549` on origin/main) does
conflate, but per field: when `vm.loadavg` fails and the pressure level reads, `runQueueLength` is passed null,
which `StallReading.cs:39-40` defines as "not offered on this platform, never failed". When BOTH sources fail the
reading is `Unreadable` with a reason (line 531-532), so the whole-reading case is not conflated. The document's
sentence "returns the same null when vm.loadavg could not be read as when the platform publishes no stall figure"
should name `RunQueueLength`, as the original review did (`macos-narrow-probe-review.md:101-103`). The reference
to interface review finding 3 is correct (`4016-interface-review.md:34-39`, rated must fix there), and "referred
to the Tech Lead" matches `macos-narrow-probe-review-answer.md:93`.

**Point 4** - supported. `git grep IOIteratorIsValid origin/main -- src` returns nothing; `ReadDisks` walks
`IOServiceGetMatchingServices` / `IOIteratorNext` (`MacOsPlatformReader.cs:61-67`). The five-second timer is the
documented interval (`IResourceProbe.cs:48`, `MacOsNative.cs:10`).

**"What none of this is"** - supported. 0.25 percentage points over four ten-second windows is
`macos-narrow-check.txt:109-118`.

**The last section (the contract's sleep)** - the reasoning is correct and I reached the same conclusion
independently in the 4641 review above, before reading this document. Verified: `ReadWithRates` sleeps
`MachineMemoryProbe.CacheFor + 250 ms` (`ResourceProbeContractLiveTests.cs:47-52`); `MacOsResourceProbe.CacheFor`
is the literal `TimeSpan.FromSeconds(1)` (`MacOsResourceProbe.cs:60`); nothing asserts the two are equal;
`ResourceSampleContract.Violations` accepts a rate-less first sample, so `ASampleFromThisMachine_...` and
`AProcessWhoseFiguresAreRefused_...` would both pass on a cached first sample. Three precisions:

1. "all happen to be one second" is true of the macOS constant only. `LinuxResourceProbe.CacheFor` is written
   `= MachineMemoryProbe.CacheFor` (`LinuxResourceProbe.cs:52`), so Linux cannot drift from the sleep unless
   someone first breaks that alias. The coincidence is the macOS literal, and the one-line fix on macOS is the
   same alias.
2. "a green cross-platform run that read the machine once" overstates it. Two of the four contract tests fail
   open; `PerProcessProcessor_...` sleeps its own three seconds and `TheFirstSample_...` reads once by design, so
   the run reads the machine at least twice unless the cache exceeded three seconds. Say "two of the four contract
   tests would pass on one sample".
3. "every rate in the sample under test is the first sample's" - the first sample has no rates; its processor and
   disk readings are unreadable with the first-sample reason, and the tests pass because the contract checker
   deliberately accepts that. Say "the sample has no rates, and the checker accepts a rate-less sample because the
   first-sample test needs it to".

The proposed fix - the contract asks the probe for its cache interval - needs a member that `IResourceProbe` does
not have today; a `CacheFor` on the abstract test base, supplied per derived class, does the same job without
touching the interface. Either is the Tech Lead's call. The finding stands; nothing in it should be withdrawn.

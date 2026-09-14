---
name: test-manager
description: Owns this repo's test suites - the two-minute budget, parallelism, what is parked, and diagnosing slowness by measurement. Triggers on "/test-manager", "clean up the tests", "fix the tests", "continue the test work", "tests are slow", "park a suite", "unpark", "test coverage", "flaky test".
---

# Test Manager

## START HERE IF YOU WERE ASKED TO CLEAN UP, FIX OR CONTINUE THE TESTS

**Read `HANDOVER.md` in this directory FIRST.** It is beside this file and it carries the live state:
what is parked, the bad test list in priority order, exactly where the last batch stopped, and the
traps already hit. This file is the rules; that file is the position on the board. Acting on the rules
without the position means redoing work that is already done.

Then work the queue in this order - it is ordered by what actually helps, not by what is easiest:

1. **Fix or delete the named bad tests.** They are listed in `HANDOVER.md` and below. Until they are
   gone the gate goes red at random, which erodes trust in every other result. Do this before adding
   a single test.
2. **Then un-park in batches of at most 100 ACTUAL tests**, each checked against the four admission
   tests below. About 60 files / 496 tests already meet the bar, so there are roughly five easy
   batches waiting.
3. **Then the git-fixture work** that would let the rest of `Core.Tests` un-park wholesale.

Do not raise the 120-second ceiling. Do not un-park anything to improve a coverage number. Do not add
a retry to a failing test.

The owner of this repository's test suites. Three duties, in priority order:

1. **The gate stays fast.** A gate nobody runs protects nothing.
2. **We know what is NOT running.** Parked coverage is acceptable; parked coverage nobody remembers is not.
3. **Nothing is diagnosed by guessing.** Every claim about why a suite is slow comes from a measurement, and this file records the measurements that have already been made so they are not re-made badly.

## Quick Reference

| Action | Command |
|--------|---------|
| Run the gate (this is THE gate) | `.\scripts\test-local.ps1` |
| Run the gate plus parked suites | `.\scripts\test-local.ps1 -Parked` |
| THE RELEASE GATE - on merged main, at the commit being tagged | `.\scripts\test-local.ps1 -Parked -Configuration Release` **plus** the two `tools/cc-director-setup*.Tests` projects - see the release gate section; the script alone is NOT the whole gate |
| Run only the locked Gateway suite | `.\scripts\test-local.ps1 -Gateway` |
| Filter within a run | `.\scripts\test-local.ps1 -Filter "FullyQualifiedName~Snooze"` |

---

## HOW TESTS COME BACK: 100 AT A TIME, EACH ONE CHECKED

**A batch is at most 100 tests. Every test in it is checked individually. No exceptions.**

Bulk moves are how bad tests get laundered into a good suite. A 2,858-test batch was attempted and
rejected for exactly that reason: it was verified in aggregate - "the suite is green" - which proves
nothing about any individual test in it. Three bad tests were found only because they happened to
fail during the aggregate runs; anything that failed one run in fifty would have sailed in and become
somebody else's problem months later.

**Speed beats coverage, and this is not a slogan.** A gate that is fast gets run on every change; a
comprehensive one that is slow gets skipped, and a skipped gate has a true coverage of zero. Adding a
thousand tests that push the gate over the ceiling makes the product LESS safe, not more. If a batch
would blow the budget, it does not go in - however good the tests are.

### The admission test - every test must pass all four

1. **It runs in parallel.** No assembly-wide `DisableTestParallelization`, no machine-wide lock, and
   nothing that fights another test for a shared resource.
2. **It is deterministic.** Ten consecutive green runs of the batch under load. Not "usually green".
3. **It controls what it asserts on.** No real clock, no `Task.Delay`/`Thread.Sleep`, no spawned
   process, no shared path, no process-global (environment variable, connection pool, static).
4. **It is worth its runtime.** A slow test earning little is a candidate for deletion, not admission.

Anything failing any of the four goes on the bad-test list below. It does NOT go in the batch, and it
does NOT get a retry.

### A test that touches a LOCK does not come back at all

**If a test touches a lock - any lock, of any scope - it stays parked. Do not scope it, do not fix it,
do not admit it "carefully". Leave it and move on.**

This is stricter than rule 1 above and it overrides it. Rule 1 asks whether the test currently fights
another test; this asks whether it goes anywhere near a lock at all, and the answer disqualifies it
without further work. Screening a class is then a five-second question, not an investigation.

**Why, since a scoped lock looks harmless.** Making a lock-touching test admissible is not a small
job, and it consumes the day it is done in. On 3 August 2026 three defects of exactly this shape
surfaced within an afternoon while verifying unrelated changes:

- `FileLogWriter` - a process-wide writer left completed, so an unrelated bystander test threw. Four
  tests in `Gateway.UnitTests` had been failing intermittently for weeks, each looking like a separate
  flaky test.
- `ReleaseSourceTests` - an assertion that held only if less than one second of wall clock passed, so
  it measured how loaded the machine was.
- `SharedInstallLock` - one fixed mutex name for the whole machine, so every installing test queued
  behind every other, and a different test failed on each run.

Each was real, each was worth fixing, and together they are why the tests-coming-back work stalled.
The gate is already fast and trustworthy without the parked suites. **Coverage we do not have costs
nothing; a slow or untrustworthy gate costs every change forever.** So the trade is settled in
advance: leave it parked.

Also note what those three have in common - they were all invisible until parallelism increased. A
suite that serialises hides every one of them, which is why a class moving from a serial project into
a parallel one is the risky direction and needs this screen, and why "it passes today" says nothing.

## THERE IS NO SUCH THING AS A FLAKY TEST

**A test works, or it is a bad test. There is no third category, and the word "flaky" is banned from
this repository.**

The word is not a description, it is an excuse. It reclassifies a defect somebody shipped as weather
that merely happens - something to be tolerated and waited out rather than fixed. The moment a suite
is allowed to have "a few flaky ones", every red result becomes negotiable, and a gate whose red is
negotiable has stopped being a gate. It protects nothing, and the people relying on it do not find out
until something reaches production.

A test that passes alone and fails beside others is not unlucky. It is **asserting on something it
does not control** - wall-clock timing, scheduler order, a shared temporary path, a process-global like
an environment variable or a connection pool. That is a defect IN THE TEST. It was written that way.

**When a test fails intermittently there are exactly two allowed outcomes:**

1. **Rewrite it deterministically.** Inject the clock instead of sleeping. Take the shared thing out of
   the test, or out of the product. Give it its own temporary directory. Make it assert on a state
   transition rather than on elapsed milliseconds.
2. **Delete it.** If nobody will make it deterministic, it is not earning its place. A deleted test is
   honest about the coverage you have; a test that fails one run in three is a lie in both directions -
   it cries wolf when the code is fine and it gets ignored on the day the code is not.

"Quarantine it", "retry it", "it is a known one", "it passes in isolation so it is fine" - all of these
are the same decision: keep a broken test and teach everyone to ignore red. None of them are allowed.

**A DETERMINISTIC FAILURE IS A DIFFERENT THING AND MUST NOT BE CALLED FLAKY EITHER.** A test that fails
every run because a file it needs is not where it looks is a *correct test with a broken setup* - fix
the setup. Diagnose before you label: run the failing test alone, and if it passes alone but fails in
company, name the shared thing it is fighting over. That name is the bug report.

## PARALLEL IS NOT OPTIONAL

**No suite in the default gate may carry `[assembly: CollectionBehavior(DisableTestParallelization = true)]`,
and no suite in the default gate may take a machine-wide lock.** These are the two things that turned a
day of work into a day of waiting, and both are one line that nobody sees.

What that one line cost, measured:

- `CcDirector.Core.Tests` carries it. 4,238 tests run one at a time: **11 minutes on a quiet machine,
  33 on a busy one - to burn 25 seconds of CPU.** The justification written above it is sound and was
  sized for CI's 2-to-4 vCPU runner; it was never revisited for a 24-core workstation, where it
  serialises four thousand tests to protect sixteen classes.
- `CcDirector.Gateway.Tests` takes the machine-wide lock, armed by a `[ModuleInitializer]` - so it is
  taken when the ASSEMBLY LOADS, before any test runs, whatever filter was passed. Its cost is not its
  runtime, it is the QUEUE behind every other working tree. Runs of 45 minutes have executed ZERO tests.

**Cap parallelism, never disable it.** A cap keeps timing-sensitive work honest while still using the
machine; disabling it uses one core out of twenty-four. If a test cannot survive running beside others,
that is a bad test - see the law above - not a reason to serialise everything around it.

**When splitting a suite, the serialisation attribute must NOT follow.** It lives in a file of its own
(`TestParallelization.cs`) precisely so it can be left behind. Any file carrying an assembly-level
attribute is excluded from the shared-helper links for the same reason. Check this explicitly after any
split - a new project that silently inherited it would look like a win and be the old trap rebuilt.

**Prove parallelism by measurement, not by intent.** Wall clock must be far below the summed test
durations. If they are close, the tests are running one at a time whatever the configuration says.

## LOCAL IS THE GATE. NOTHING EVER WAITS ON CONTINUOUS INTEGRATION

Run `.\scripts\test-local.ps1`. This machine is far stronger than a runner and gives the answer in
about a minute; the .NET job takes roughly fifty. **Never wait on continuous integration to merge -
no exceptions, releases included.** The gate is the local run plus a review by a different agent
family (Codex by default), and then the change merges.

The exception list this section used to carry - releases, changes to the build, cross-platform work -
is gone deliberately. Every exception was an invitation to pay the fifty minutes again, and the
answer it bought was one the local run had already given.

A release still needs the coverage the default run skips, and gets it LOCALLY, on merged `main` at
the exact commit about to be tagged:

    .\scripts\test-local.ps1 -Parked -Configuration Release
    dotnet test tools/cc-director-setup.Tests/ -c Release
    dotnet test tools/cc-director-setup-engine.Tests/ -c Release

`-Parked` adds the two skipped suites. `-Configuration Release` matches what users download,
because this script defaults to Debug while the continuous integration job it replaced ran Release.
The two installer projects are outside `cc-director.sln` and this script therefore never runs them,
while the release ships `cc-director-setup.exe` - folding them into `-Parked` is the follow-up that
removes the footgun. An earlier run does not count, and neither does a run against a pull-request
head rather than the squashed commit that gets tagged.

That is not a softened version of the old rule - it is a local gate, not a wait on a runner. It
matters because the release workflow runs ZERO tests and a pushed tag cannot be un-pushed, so a
release is the single place where fixing forward is not available.

Continuous integration still runs after the merge, as a backstop. It is never **waited on** - no
merge is ever held open for it - and it is never left red: **a red is fixed forward immediately,
re-run, and confirmed clear.** Reading a result after the fact is chasing, not waiting, and it is
required: the web and Python jobs are the only place those tests run at all, so a change touching
the browser shells or the Python toolbelt must go back and read them. A red left standing turns the
backstop into noise, and a backstop nobody reads is not a backstop - which is exactly how two
architecture guards stayed red on main for a morning without anybody being told.

## Deciding what to run: ALWAYS ask the selector, never guess

`scripts/select-tests.ps1` answers "which suites can this change actually affect" from the files it
touched. **Use it whenever the question comes up** - before merging, when someone asks whether the
parked suites are needed, or when a change feels risky.

    .\scripts\select-tests.ps1 -Explain

It derives the answer from the .csproj REFERENCE GRAPH, not from a hand-written folder list: each
test project's transitive closure is computed, and a suite is selected when the change touched any
project inside it. A hand-maintained map goes stale silently the first time somebody adds a
ProjectReference; this one cannot.

It fails toward running MORE. An unrecognised path, a changed .csproj or .sln, a change to the gate
itself - all select everything. Only paths whose irrelevance is obvious (docs, markdown, images) are
allowed to select nothing.

**The gate calls it automatically and prints a COVERAGE GAP warning** when a change touches code that
a parked suite covers. That warning is the answer to "who remembers to run `-Parked`" - nobody has
to. Treat it as a required action, not a hint: run `-Parked` before merging, or say in the pull
request why not.

### Why the gate WARNS instead of auto-running the parked suites

Measured by replaying the last hundred merges: a parked suite was implicated in **69 to 80 per cent
of changes**, because `CcDirector.Core` and `Gateway.Contracts` are referenced by nearly everything.
Running them automatically would restore a twelve-to-forty-five-minute gate for seven changes in ten
- exactly the problem the budget removed. So the fast gate stands and the reader is told precisely
what it did not cover.

That number is also the honest measure of how much selection saves here: **less than you would hope.**
The fan-out from the shared projects is the real constraint, and the way to actually shrink it is to
make the parked suites fast enough to stop being parked.

### The map is validated against history, not asserted

`scripts/validate-test-selection.ps1` replays the last hundred merges and checks the only property
that can hurt: that the selection was never TOO SMALL. Ground truth is derived from paths, not from
the graph, so the check is not circular - a change editing `Engine.Tests` must select `Engine.Tests`,
a change under `src/` must select something, a change under `apps/` must select the web tests.

Last run: **100 changes, zero violations.** 44 per cent ran everything (fail-safe), 42 per cent
narrowed, 13 per cent needed no tests, 1 per cent web only.

Re-run it after changing the selector or restructuring projects. It is cheap and it is the only thing
standing between a clever map and a silent skip. It is honest about its limits too: a suite that
should run because of a RUNTIME coupling the reference graph cannot see will not be caught by any
path-based harness.

## The law: a two-minute budget, enforced

`$BudgetSeconds = 120` in `scripts/test-local.ps1`. Each suite gets that long. One that has not
finished is **killed** and the run fails naming it.

Exceeding the budget is **not a test failure** and must never be reported as one. It means the suite
no longer belongs in the default run. Park it, or make it fit.

**Do not raise the ceiling to make a red go away.** Every second added is paid by every person and
every agent, on every change, forever. The number is the point. It was a comment before it was
enforced, and as a comment it did not hold: a suite drifted to eleven minutes on a quiet machine and
thirty-three on a busy one, and people started working around the gate instead of running it.

`-Parked` and `-Gateway` deliberately suspend the ceiling - that run is the release gate and is
expected to be slow.

---

## What runs, and what does not

### In the default gate

| Suite | Tests | Notes |
|-------|-------|-------|
| `CcDirector.Core.UnitTests` | 278 | The PARALLEL half of Core, against 11 minutes sequential for the whole. |
| `CcDirector.Avalonia.Tests` | 423 | |
| `CcDirector.Engine.Tests` | 63 | |
| `CcDirector.HostedAgent.Tests` | 88 | The slowest in the default run, about 40 seconds. |
| `CcDirector.Launcher.Tests` | 191 | |
| `CcDirector.Terminal.Avalonia.Tests` | 25 | |
| installer: `setup.Tests`, `setup-engine.Tests` | 566 | Outside the solution; built separately by the script. |

1,634 tests, about three minutes end to end including the build. Measured 2026-09-13.

`CcDirector.Gateway.UnitTests` LEFT this table on 2026-09-13 - see Parked below and issue #2824.

### Parked (opt in with `-Parked`)

| Suite | Cost | Why it cannot fit |
|-------|------|-------------------|
| `CcDirector.Gateway.Tests` | a QUEUE, not a runtime | Serializes machine-wide. Its cost is waiting for every other working tree on the machine. Runs of 45 minutes have executed ZERO tests. |
| `CcDirector.Gateway.UnitTests` | about 180s, against a 120s ceiling | Grew from 2,777 tests to 4,267 and from about 56 seconds to 180, so the gate STOPPED it on every run - and a stopped suite writes no result file, so its 4,259 passing tests were gating nothing at all. Parked 2026-09-13, issue #2824, which also records what would bring it back. |
| `CcDirector.Core.Tests` | 11 min quiet, 33 busy | Runs SEQUENTIALLY, and its slowest classes spend 30-80 seconds each creating real git repositories and worktrees on disk. Its fast, parallel-safe half was split into `CcDirector.Core.UnitTests` (below); what is left is the timing, git and process work plus the named bad tests. |

**THE COST OF PARKING, WHICH MUST BE SAID OUT LOUD WHENEVER IT COMES UP.** Those three suites hold
real coverage - the Gateway's host-bound endpoint, tenancy and boundary tests, most of Core, and since
2026-09-13 the Gateway's 4,259 UNIT tests as well. A regression in any of them can reach main without a
local red until `-Parked` runs. The Gateway.UnitTests parking is the one most likely to catch someone
out, because a green default run used to mean the Gateway was covered and no longer does. That is a deliberate,
temporary trade: a gate that is actually run beats one that is comprehensive and skipped. It is not
a reason to relax about it.

---

## The machine-wide lock, and why filters do not escape it

`CcDirector.Gateway.Tests` takes `GatewayTestSuiteLock`, which serializes runs of that assembly
across every working tree for one user on one machine. The lock itself is well-argued - overlapping
runs corrupt each other's evidence, and a lock a caller must REMEMBER to take is a convention, which
is why acquisition is automatic.

**It is armed by a `[ModuleInitializer]`, so it is taken when the ASSEMBLY LOADS** - before any test
runs, whatever `--filter` was passed. There is no way to opt a single test out from inside the
assembly. Running one pure unit test from that project still queues behind another working tree.

That is why the pure tests were MOVED OUT into `CcDirector.Gateway.UnitTests` rather than filtered.

Past `MaxWait` (45 minutes) a queued run stops with an error naming the holder. That is the designed
behaviour, not a bug: it never takes a lock from a living process. If a holder is genuinely stuck,
that is a decision for a human - measure a CPU delta before calling anything stuck.

---

## Which project does a new test belong in?

**The locked project is the default home. This direction is the safety property.**

Put a test in `CcDirector.Gateway.Tests` (locked) if it does ANY of:

- constructs a `GatewayHost`, binds a port, or drives `FakeTunnelDirector`
- spawns a process
- touches live Postgres
- **mutates a process-global** - `Environment.SetEnvironmentVariable`, a static toggle, the current directory

Otherwise it may go in `CcDirector.Gateway.UnitTests` (unlocked).

A test that needs exclusivity and is written in the unlocked project runs beside another working
tree's suite and produces corrupted-but-clean-looking evidence. A test that does NOT need it and
sits in the locked project merely runs slower. The asymmetry is the whole argument: **when unsure,
locked.**

Note the environment-variable rule specifically - it is not about cross-process safety, it is about
xUnit running classes in parallel WITHIN a process. 34 classes were moved back for exactly this.

---

## Diagnosing a slow suite

Follow this order. It is written down because guessing produced three wrong answers in one day.

### 1. Get per-class timings from the TRX. Do not read the code first.

Every run writes a TRX. Group by class and sort:

```powershell
$f = Get-ChildItem "$env:TEMP\cc-test-local-*\<Suite>.trx" | Sort-Object LastWriteTime -Desc | Select -First 1
[xml]$d = Get-Content $f -Raw
$rows = $d.TestRun.Results.UnitTestResult | Where-Object { $_.duration } | ForEach-Object {
  [pscustomobject]@{ Cls = ($_.testName -replace '\.[^.]+$','') -replace '^.*\.',''
                     Sec = [TimeSpan]::Parse($_.duration).TotalSeconds } }
$rows | Group-Object Cls | ForEach-Object {
  [pscustomobject]@{ Class=$_.Name; N=$_.Count; Total=[math]::Round((($_.Group|Measure-Object Sec -Sum).Sum),1) } } |
  Sort-Object Total -Descending | Select-Object -First 15
```

### 2. Compare a suspect class against a known-pure one

This single comparison is what actually located the biggest cost:

| class | tests | time | per test |
|-------|-------|------|----------|
| `SessionOrderingTests` (pure) | 118 | 53 ms | **0.45 ms** |
| `SnoozeRegistryTests` (database) | 31 | 11 s | **355 ms** |

A ratio like that names the culprit immediately. A flat profile (top class only a few per cent of
the total) means there is no hotspot and you are looking for a per-test fixed cost instead.

### 3. Only then form a hypothesis, and test it before shipping it

Run the change, measure again, and run the suite repeatedly. If the numbers do not move, **revert**.

---

## Known costs, and the false trails

### Confirmed and fixed

**The migration set was rebuilt once per database-backed test.** Constructing a `GatewayDatabase`
over an empty file applies every migration. Over half the Gateway suite is database-backed, so the
same schema was built from scratch hundreds of times a run - 355 ms per test against 0.45 for a pure
one. `GatewayDbTestHarness` now migrates ONCE per process, holds the result as bytes, and writes
those bytes per test. Behaviour-preserving: same constructor, same migrations, same code path, and
EF's own "no pending migrations" branch then skips the work. **2 minutes to under one.**

If you touch that harness, keep the template as BYTES. The first version copied a file and called
`SqliteConnection.ClearAllPools()` to release it - which is process-global and fired while other
tests were running.

### False trails - do not walk these again

**"Low CPU on the test host means the tests are sleeping."** FALSE, and it cost hours. Child
processes do not appear in the parent's CPU counter, and neither does time waiting on a database.
`Core.Tests` showed 25 seconds of CPU across 11 minutes and was declared sleep-bound; its slowest
tests were in fact spawning **git** the whole time. Measure per-test durations, not parent CPU.

**"It is full of sleeps."** FALSE for both suites. `Gateway.UnitTests` contains 19 sleep calls
totalling **one second**; `Core.Tests` has 68 totalling 24. Neither can explain minutes. Count them
before blaming them:

```bash
grep -rn "Task\.Delay\|Thread\.Sleep" --include=*.cs src/<Suite>/ | wc -l
```

**"More threads will fix it."** FALSE for `Gateway.UnitTests`. Measured: 4 threads ~120 s, 12 threads
163 s, 24 threads 149 s - and the higher settings surfaced more of the bad tests described above. Parallelism is capped
in code (`AssemblyParallelism.cs`), not in `xunit.runner.json`: the JSON landed in the output
directory and was **demonstrably ignored** while the same value worked on the command line.

### Open: BROKEN TESTS, currently tolerated - and that is a defect, not a status

Under load, one or two database tests fail per run in `Gateway.UnitTests`, never the same ones,
passing in isolation. **These are bad tests.** They are fighting over something process-global, and
until they are rewritten or deleted they are actively eroding trust in the gate. Do not describe them
with any word that implies bad luck.

What is known. `GatewayDatabase.Dispose` calls `SqliteConnection.ClearAllPools()`, which is
process-global, and roughly **55 test call sites do the same deliberately** - it is the established
idiom for "release the file so I can read it". One database in a running Gateway makes that invisible;
hundreds disposing concurrently under test means every disposal reaches across every other live
database. That is the shared thing they are fighting over, and the honest fix is to stop the tests
depending on a global at all.

**Two fixes were tried and both reverted. Do not repeat them without new evidence:**

1. *Narrowing `Dispose` to `ClearPool` for its own connection string.* Broke six `DeviceKeyAtRest`
   tests deterministically - those tests rely on somebody's global clear to release a file they then
   read. Perpetrators and victims are the same call sites, so politeness in one cannot work.
2. *Opening test databases unpooled.* Five runs gave 1, 2, 2, 0, 0 failures - indistinguishable from
   the baseline, with failures then appearing in non-database tests too. The hypothesis was NOT
   confirmed, so the production seam was reverted rather than shipped on a hunch.

Whoever picks this up: reproduce it deliberately by running the suite while several other suites run,
then fix or delete the tests. Do not add a retry.

### THE BAD TEST LIST - not a status, a queue of defects

These fail intermittently and are therefore BAD TESTS, not a status. They are held OUT of the fast
gate so they cannot erode trust in it, and named here so nobody mistakes silence for health. Each one
needs a rewrite that removes whatever it does not control, or deletion.

| test class | suite | what was observed |
|------------|-------|-------------------|
| `SessionNumberAllocatorTests` | Core.Tests | Fails intermittently under parallel load on a fresh allocator with no shared state of its own. Cause not established - the allocator holds no static state and `AllocateOffline` is deterministic, so the sharing is somewhere not yet found. |
| `ActivityEventProducerTests` | Core.Tests | Reads `DateTime.UtcNow` directly; needs an injected clock. |
| `JwtAccessTokenValidatorEs256Tests` | Core.Tests | Token expiry asserted against real time. |
| one or two database tests per run | Gateway.UnitTests | Fighting over `SqliteConnection.ClearAllPools()`, which is process-global - see above. |

Do not add a retry to any of them. Do not move them into a default-gate suite until they are fixed.

## Bringing a parked suite back

0. **At most 100 tests in the batch, each checked individually against the four admission tests
   above.** A bigger batch is not faster progress, it is unverified progress.
1. Measure it first, using the recipe above. Do not start optimising.
2. Fix the dominant cost. Prove it with before/after numbers on the same machine.
3. Run it at least three times. It must be under 120 seconds AND deterministic.
4. Move it from `$parkedProjects` to `$defaultProjects` in `scripts/test-local.ps1`, and record the
   measurement in the comment beside it - the number is what lets the next person judge a regression.
5. Update the tables in this skill.

`Gateway.UnitTests` has already made this round trip: parked for exceeding the ceiling, then returned
once the per-test migration cost was removed. That is the pattern to follow.

---

## When a new test assembly is created

Two `[ModuleInitializer]` guards in `CcDirector.Gateway.Tests` are **live-data protection**, not
convenience, and a new assembly starts with neither:

- `TestStorageRootRedirect` - points `CcStorage.Root` at a throwaway temp directory. Without it,
  tests write into the owner's real `%LOCALAPPDATA%\cc-director`: live missions, cron jobs, key
  vault. This has already happened once, renaming a live statistics file aside.
- `TestEnvironment` - pins the Director instance-discovery directory away from the real one, and
  disables the turn-brief sweep and Tailscale serve provisioning.

**Link them, do not copy them** (`<Compile Include="..\CcDirector.Gateway.Tests\...">`), so the two
assemblies cannot drift on a guard this consequential.

Also load-bearing and easy to miss: a `ProjectReference` to
`CcDirector.Gateway.Migrations.Postgres`. Nothing calls into it - EF resolves the migration set by
assembly NAME at runtime, so its absence is a `FileNotFoundException`, not an unused reference.

---

## Coverage: what to say when asked

Answer with what is NOT running, not with a number of tests. The honest summary today:

- **Running by default:** 1,634 tests, about three minutes (measured 2026-09-13).
- **Not running by default:** the host-bound Gateway suite (endpoints, tenancy, boundaries), all of
  `Core.Tests`, and since 2026-09-13 all 4,259 of `Gateway.UnitTests` (issue #2824). All three run
  under `-Parked`.
- **Continuous integration** still runs everything after a merge as a backstop; it takes about fifty
  minutes, blocks nobody, and is never waited for. When it reddens it is fixed forward at once - an
  unchased red is how this backstop stops being one.

If a change touches the Gateway's host-bound surface, or anything Core covers, say so and run
`-Parked` before merging. The budget buys speed on ordinary changes; it is not a licence to skip
verification on the ones that need it.

---

## Continuing this work

`HANDOVER.md`, beside this file, carries the current state of play and the queue: the bad test list in
priority order, the mechanics for the next Core batch, the two traps in the split, and the measurement
recipe that worked. Read it before picking any of this up.

**Skill Version:** 1.1
**Last Updated:** 2026-08-03
**Handover:** HANDOVER.md
**Enforced by:** `scripts/test-local.ps1` (`$BudgetSeconds`, `$defaultProjects`, `$parkedProjects`)

# Resource Monitor - the quality assurance plan

Written 6 November 2026 by the Implementation Lead, while phases 1 and 3 were still building.
For the next seat, not for the owner - the owner gets one published report at the end.

**The mission's deliverable is ONE quality assurance report showing the whole thing working.** Not a
merge and not a release. Section 8 of `missions/resource-monitor.html` is the list of what it must
carry. This file is the part that section does not answer: **where each proof is produced, on which
machine, against which build, and what will go wrong.**

It is written now, early, on purpose. The previous run of this mission had no plan for proving it
across three platforms and stopped before anyone found out.

## The problem this file exists to solve

The report needs the meter's figures, warnings firing and clearing, and the diagnosis, **on Windows,
Linux and macOS**. A Director on each machine therefore has to be running the new code. There are
only two ways to get that, and one of them is not available:

- **A Director release.** This is the owner's decision, made separately, and he has said it comes
  *after* the report. So **the quality assurance cannot depend on a release**, or the mission
  deadlocks: the release waits on the report and the report waits on the release.
- **A development-slot build on each machine.** This is the route. It needs no release and no
  decision from anybody.

The owner has separately offered to **restart the Director** when there is a new build worth looking
at. That is for him to *see* the meter in his own daily-driver Director. It is not the route for
producing the evidence, and he must not be asked to restart anything for a measurement we can take
ourselves.

## Where each proof is produced

| Proof (section 8) | Machine | How |
|---|---|---|
| Memory and processor within 5 percentage points of the operating system's own monitor | all three | The load harness records ours beside theirs: `free` on Linux, Task Manager on Windows, Activity Monitor on macOS. Where a figure cannot be scripted the harness prints ours and prompts for theirs, and the report says which were read by eye. |
| Normal, amber, red, the banner, **the warning clearing**, a narrow window, "cannot read" | one machine is enough for the screenshots; the amber and red thresholds are checked on all three | Development-slot Director plus the load harness. "Cannot read" is forced with an injected failing probe, not by breaking a machine. |
| Red within 70 seconds of pinning the processor, and clearing when the load goes | all three | The harness. Both halves: a warning that fires and never clears is a defect, not a pass. |
| Memory amber, then clearing | all three | The harness allocates until Tight, then frees. |
| Detail screen with a session expanded | one machine | Development-slot Director with real sessions on it. |
| Five-minute rows stored, rolled up, deleted | one machine | Phase 3's tests, against a **local** Gateway. Never production, never Europe. |
| A 10-minute disconnect leaving no gap | one machine | The harness blocks the Gateway; phase 3 owns the assertion. |
| Cockpit fleet view | one machine | Local Gateway seeded with three machines. |
| Sampler's own cost under 0.5 per cent of one core over 10 minutes | all three | The harness. Platforms differ in sampling cost, so one measurement does not cover the others. |
| Diagnosis says "CPU" under a processor load, and "memory" when programs are STALLING on memory | all three | The harness plus the diagnose command. **Not by allocating until the level reads Tight** - see "Tight is not a cause" below. |
| Diagnosis names the disk | Linux, two ways (see below) | Recorded fixture **and** a live load. |
| Buffer capping at 24 hours; a child process outliving its session; the clock jumping | anywhere | Unit and integration tests with an **injected clock**. Never a sleep, never the real clock. These are named failure cases, not edge cases. |

## Tight is not a cause: the memory proof must drive a STALL, not a level

**This row used to ask for something that cannot pass, and it would have recorded a failure against
correct code.** Corrected by the Implementation Lead on 6 November 2026, after the seat that wrote the
diagnosis pointed out that the plan contradicted the product.

`ResourceLevel.Tight` and `SlownessCause.Memory` are different ideas:

- **`Tight`** is a PRESSURE LEVEL - memory is nearly full. It drives the bar's colour and the banner.
- **`SlownessCause.Memory`** means PROGRAMS ARE WAITING ON MEMORY - a stall.

A machine can sit at `Tight` with nothing stalling at all, and that is not a defect: memory that is
allocated and in use is memory doing its job. Filling memory until the level reads `Tight` therefore
does **not** make the diagnosis name memory, and a diagnosis that stayed quiet would have been right
while the plan called it a failure. That is the worst kind of plan defect - it pushes someone to
"fix" correct code to satisfy a document.

**So the memory row drives the platform's own stall or pressure signal, not its utilisation:**

- **Linux** - the kernel's memory stall share over the last minute (`some avg60` from pressure stall
  information), which is the figure the diagnoser reads. Allocate hard enough to make the kernel
  report programs waiting on memory, which in practice means reclaim and swapping, not merely a high
  used figure.
- **macOS** - the kernel memory pressure level through `MacOsKernelPressure`, the one shared mapping.
  A warning level is a cause; a high used figure is not.
- **Windows** - hard page faults per second, the proposed limit being 1,000 a second.

**Both rows stay, and they prove different things.** "Memory amber, then clearing" above is still
correct and still valuable: it proves the LEVEL, the bar and the banner, and allocating until `Tight`
and freeing is exactly the right way to drive it. This row proves the DIAGNOSIS. A run that drove
`Tight` and then reported the diagnosis row as passed would be reporting one proof twice.

**And the wording rule still binds the sentence that comes out.** Only Linux may say programs were
stalled waiting on memory for a share of the time; macOS reports a kernel pressure level and Windows a
fault rate, and neither may claim a measured stall percentage.

## The disk verdict has to be proved twice, and why

**devlinux has changed underneath this mission.** On 15 October its root volume spanned two
solid-state drives *and* a spinning `sdc`, which is why everything the machine did waited on `sdc`.
Today the root volume is `sda` and `sdb` only, `sdc` is out of it, and `sdc` reads 0 per cent busy.
The 15 October verdict can no longer be reproduced live on that machine.

So both of these are required and neither replaces the other:

1. **The recorded 15 October readings, as a fixture, must still produce exactly "disk, `sdc`
   spinning hard drive".** This is the regression test and it is not weakened by the machine moving
   on.
2. **A live verdict under a disk load we create**, naming whichever disk is actually busy at the
   time. Do not try to recreate the old accident. Put a real write load on a real disk, show the
   kernel's pressure figure rising, and record the verdict.

A live proof under a load we created is better evidence than a lucky reproduction of an old accident.

## What each platform may claim - carried from section 5c-i

The platforms do not measure the same thing, so the report must not let them claim the same thing.

- **Linux** measures stall directly and may say "programs were stalled waiting on the disk 32 per
  cent of the time".
- **macOS and Windows may not.** macOS publishes no stall figure for any resource; Windows has only
  performance-counter proxies. They say "the disk is answering slowly", from latency.
- **The macOS and Windows latency thresholds are unproved** - one machine, one internal solid-state
  disk, and no spinning disk has ever been measured on a Mac. The report records them as proposals.
- **The processor limits (85 amber, 95 red, for 60 seconds) are also proposals** until the harness
  has run them on all three platforms. Memory's limits are the existing tested ones and are not.

## Building a Director to test with - the traps, per machine

**Never touch a Director you did not launch.** The owner keeps long-lived Directors on the installed
application and on development slots 1 to 4. Killing one is the one unforgivable mistake in this
repository. **Use slot 5 or higher.** Confirm a process's path before stopping anything, and stop
your own by its named shutdown signal rather than force-killing it - a force-kill leaves a phantom
"interrupted" entry in the fleet.

- **Windows.** Build with `scripts\local-build-avalonia.ps1 -Slot 5 -OutputDir "<repo>\scripts\local-build"`.
  The default output directory is a dead path, so always pass that one. **Launch through the
  `cc-director-launch` scheduled task, never from inside an agent session**: a Director started inside
  an agent's pseudo-console gives its grandchild agents a nested console and they exit within seconds.
  The full local gate `.\scripts\test-local.ps1` runs here and nowhere else.
- **macOS.** Development wrappers live in `~/Applications/DevThrottle Dev` as `Director Dev N.app` -
  never in `/Applications`. **Do not rebuild a slot whose binary is running**: the copy kills it.
  A graphical launch fails while the screen is locked. `dotnet` is at `~/.dotnet` and is not on the
  path; a freshly installed host needs re-signing before it will run. `scripts/test-local.ps1`
  cannot run here at all, so the gate is the test projects plus review.
- **Linux.** A plain solution build stops with NETSDK1100 on the two Windows-only projects; pass
  `-p:EnableWindowsTargeting=true`. devlinux has a history of being slow because of its disk, so a
  slow build there is the machine, not the change.

## The live tests live in a project no gate runs, so the quality assurance is what runs them

Phase 1 put the tests that measure a real machine in their own project,
`src/CcDirector.Core.MachineLiveTests`. That was the right call: a contract test measuring a real
machine over a real interval needs a real wait, and the gate's own source guard refuses a sleep or a
clock read inside a gate project - correctly, and its baseline only ever shrinks. The project is in
the solution so a signature change breaks the build, is in none of the gate's project lists, and
every test in it is additionally gated on `CC_DIRECTOR_LIVE_MACHINE_TESTS` so it skips visibly even
when run directly.

**The consequence has to be faced rather than admired: nothing in continuous integration or in the
local gate will ever run these tests.** A test suite nobody runs is indistinguishable from one that
always passes, and it reads like coverage while providing none. So:

- **This plan is what runs them.** Running this project is a required step of the quality assurance,
  on each of the three machines, with the opt-in variable set.
- **The report must show their output per platform** - the real test names and results on Windows, on
  Linux and on macOS - not a sentence saying the project exists or that it passed somewhere.
- **A skip is not a pass.** These tests skip by default, so a run that reports "skipped" is a run
  that did not happen. The report states, for each platform, that they RAN and what they measured.
- The shared contract every probe is judged against (`ResourceSampleContract`, exercised through
  `ResourceProbeContractLiveTests`) is the test that proves the three platforms answer the same
  question the same way - particularly that per-process processor is a share of the whole machine.
  If it runs on only one platform it proves nothing about agreement, which is its entire purpose.

## A named check every platform must pass: the first sample withholds every rate

Found on macOS on 6 November, by a reviewer **running** the probe rather than reading it. The
per-process processor figure was a **lifetime average** for any process with no earlier reading,
where the contract says null. On the first sample no process has an earlier reading, so every one
took that path - 328 of 513 processes, including one running for two days that duly reported its
two-day average of 5.78 per cent, inside the very sample whose processor line read "first sample, no
interval yet".

**This is worse than the made-up zero it had replaced**, and it is worth understanding why, because
the rest of this mission is full of the same opportunity. A zero looks wrong and gets noticed. A
lifetime average is a *plausible figure for a quantity nobody measured*: a process alive for thirty
days that has just started burning a core reads as near nothing, and a session total built from those
numbers is wrong with nothing marking it as a different quantity. **A rate measured over a different
window than the contract specifies is a wrong quantity wearing the right name.**

It also slipped past two people because it arrived as a *fix*. An earlier review had found the zero;
the answer replaced it with the lifetime average and justified it with "which is the only case it is
used in" - a clause that was false, and that nobody checked by running the code. **An answered
finding is not a fixed finding.**

So the report carries this check, for each of the three platforms:

- **Take the first sample after start-up and count the processes reporting a processor share. The
  answer must be zero.** Any other number is this defect. On macOS it was 328 before the fix and 0
  after.
- **The process list must not shrink to achieve that.** Only the unmeasured quantity is withheld, not
  the process: after the fix, 465 processes were still listed and 296 still had readable memory. A
  probe that drops what it cannot measure fails this check in the other direction, and would look
  identical in a test that only counted shares.
- **The second sample must then report real interval shares** - 289 of 458 on macOS - or the figure
  is never produced at all and the check has proved nothing.

The third bullet is the one that keeps the first two honest: a probe that always returned null would
pass both of the first two and be useless.

## The defect class this mission keeps producing: a plausible number that passes every range check

Three separate findings on 6 November were the same defect wearing different clothes, and all three
would have survived a green test run. They are recorded here because the remaining phases have the
same opportunity, and because every one of them was found by **running** the code rather than reading
it.

- **A lifetime average where the contract says null.** The per-process processor figure reported a
  process's whole-life average whenever there was no earlier reading - which on the first sample is
  every process. It arrived as the *fix* for a made-up zero.
- **A clamp that hid the one defect the contract exists to catch.** A busy-share calculation clamped
  its output, so a probe reporting per-core figures - 400 per cent on a four-core machine - was
  tidied into exactly 100 per cent and passed every range check. The clamp converted a loud,
  obvious error into a plausible reading.
- **A check that silently disabled itself.** A readable processor reading with no core count turned
  two contract checks into no-ops, so a probe that merely forgot to report the core count passed
  everything. A fourth check turned out to be a tautology that could never fire at all.

The pattern: **the dangerous value is not the obviously wrong one, it is the believable one.** A zero
or a 400 gets noticed. A tidy 100, a lifetime average, or a check that quietly stops checking does
not. So for every figure in this report:

- **Prefer a form that cannot overshoot** over one that is corrected afterwards. Where a platform
  publishes idle alongside busy, compute the share from the counters' own totals - that form cannot
  exceed 100 and needs no clamp. Never clamp a figure into range; a figure out of range is a defect
  to surface, not a presentation problem to fix.
- **Null is a correct answer**, and a made-up plausible number never is.
- **Every check gets an input it must reject**, proved to reject it. A contract checker that returns
  "no problems" on correct input is indistinguishable from one that always returns that, so a green
  run proves nothing on its own.
- **Falsify, do not just run.** Each of the three above was caught by running the new test against
  the old code and watching it fail, or by running the checker against a deliberately wrong input.
  A test never seen to fail has not been shown to test anything.

## Agreement must be BRACKETED, not compared against a single reading

The five-percentage-point agreement check is the mission's headline claim, and the obvious way to
measure it does not work. Found twice on 6 November, by the same seat, on two different resources.

**Processor:** a first attempt compared one probe window against whatever tick `top` happened to
print, and read **32 percentage points apart**. It was nearly reported as a probe defect. The machine
was swinging between 30 and 78 per cent busy and the two windows were offset; aligned, they agree to
within half a point.

**Memory:** a first attempt compared the probe against one `vm_stat` reading and gave 0.34 points. The
same method on a loaded machine gave **5.95 points - outside the requirement this phase has to meet**.
Neither figure measured the probe. Available memory moves several points in seconds on that machine,
so a reading taken at a different moment is a different quantity. Bracketed between two `vm_stat`
reads the honest answer is 1.36 points, with the machine itself moving up to 4.75 points across a
single read.

**On a machine under load, two readings of one quantity taken at different moments are two different
quantities.** So:

- **Bracket the probe between two readings of the operating system's own tool**, or align the sampling
  windows so their intervals tick together. Quote the system tool as a range, not a point.
- **A single-reading comparison against the five-point threshold means nothing in either direction -
  including when it passes.** The passing 0.34 was exactly as untrustworthy as the failing 5.95, and
  it is the passing one that would have reached the report unchallenged.
- **The report states the method, and how much the machine moved during the measurement.** A figure
  without that is not evidence, because the reader cannot tell whether it measured the probe or the
  weather.

## Running the live project: the gate decides per class, so run the WHOLE project

This paragraph used to say the opposite, and the warning it carried is now out of date. It said
`LiveMachineFactAttribute` gated on `CC_DIRECTOR_LIVE_MACHINE_TESTS` only, that a cross-platform run
was therefore expected to show failures that were not defects, and that the quality assurance should
not be run against this project until a platform hook existed. **That hook exists.** It landed with the
Linux probe, and the macOS classes were moved onto it when the macOS contract derivation landed.

So the procedure is the simple one, and it is the same on every machine:

```
CC_DIRECTOR_LIVE_MACHINE_TESTS=1 dotnet test src/CcDirector.Core.MachineLiveTests -c Release
```

Every class that reads one platform's own interfaces declares which platform it is for, with
`[LiveMachinePlatform(...)]`, and the decision is made **per class when the test is discovered** - which
is what the shared contract needs, because its tests are written once in an abstract base and
inherited, and an attribute on the base cannot know which platform each derived class is for. On a
machine of another kind those tests SKIP, visibly, with the reason stated (`Written for Linux; this
machine is macOS.`). A contract class that declares no platform is reported as an **error** naming the
fix, rather than run - and failed - everywhere.

**Do not filter by class to work around a platform**, and do not read a skip as a gap in the run: the
skips are the gate working, and their reasons are the record of what this machine could not be asked.
What still deserves a reader's attention is a class that neither ran nor skipped, and a run where the
skip count does not match the platforms this machine is not.

## Rules the report itself must obey

- **One self-contained HTML file published with `cc-dev-reports open`.** A file path handed over
  instead of a published report is the defect that tool exists to stop.
- No `<script>` and no inline event handlers - they are blocked when the report is shown, so the
  shape check refuses them. Everything drawn with HTML, CSS and inline SVG; images as `data:` URLs.
- The status word is one of exactly three: `waiting-on-you`, `agent-working`, `done`. Inventing a
  fourth is the commonest way a first publish fails.
- Header, then summary, then a questions section **even when there are no questions**, then detail
  sections, then evidence last.
- **The evidence section says what was not proved.** A report listing only what went right is not a
  report anybody can act on. Name every figure read by eye rather than measured, every threshold
  still a proposal, and every platform where a proof was not run.
- **An empty result is a broken instrument, not a clean run.** No proof in this report may rest on
  the absence of something. Each one states a specific presence: this figure, on this machine, at
  this time, beside the operating system's own.

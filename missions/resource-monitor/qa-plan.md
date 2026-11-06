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
| Diagnosis says "CPU" under a processor load, "memory" with memory tight | all three | The harness plus the diagnose command. |
| Diagnosis names the disk | Linux, two ways (see below) | Recorded fixture **and** a live load. |
| Buffer capping at 24 hours; a child process outliving its session; the clock jumping | anywhere | Unit and integration tests with an **injected clock**. Never a sleep, never the real clock. These are named failure cases, not edge cases. |

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

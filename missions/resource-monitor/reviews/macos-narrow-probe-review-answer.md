# Answer to the review of the narrowed macOS resource probe

Reviewed by a different agent family from the one that wrote the code, as the method requires. Four
findings were raised, counting the group of smaller points as one. **All four are accepted. Three are
fixed; one is fixed in the part that is mine and referred upward for the part that is not.**

The review was worth more than the code it checked. It compiled a C program against this machine's
headers to verify every structure size, offset and constant, cross-compiled the same program for
x86_64, read the exported-symbol stubs of libsystem_kernel per architecture, and ran 3,300 uncached
passes of the probe while watching the process's Mach port count and resident set size from outside.
It also found, by running the probe rather than by reading it, the one defect in this work that
mattered - and it found it in a place I had been told was already answered.

## 1. The per-process processor figure was a lifetime average where the contract says null - ACCEPTED, fixed

This was the real one, and it is the more interesting for having been *inherited as already fixed*.

Review finding 3 of pull request 4035 was the made-up zero: `CpuPercent: cpuPercent ?? 0` wrote a
process with no earlier reading down as idle. Its answer replaced the zero with the process's
processor time over its whole life, arguing that the lifetime average "is close to the interval rate
for a process that has only just started, **which is the only case it is used in**".

That last clause is false, and the reviewer measured it false. On the FIRST sample no process has an
earlier reading, so every one of them takes that path: **328 of 513 processes** on this machine,
including `suggestd`, which had been running for two days and reported **5.78 per cent** - its
two-day average, presented in a field the contract defines as the share of the machine over the last
interval, inside the very sample whose processor line reads "first sample, no interval yet". The same
path is taken whenever a previous reading is missing for any other reason, such as a reused process
id.

It is not a zero, which is why the earlier review and I both let it past. It is worse in one respect:
a plausible figure for a quantity nobody measured. A process alive for thirty days that has just
begun burning a core reads at its thirty-day average, close to nothing, and a session total built
from those shares is wrong the same way with nothing marking it as a different quantity.

The contract settles it without needing a judgement, and I had not read it closely enough:
`ProcessReading.CpuPercent` is null "when it is not known: the platform refused this process, **or it
started during the interval and has no earlier reading to compare against**". The one case the
lifetime average was defended for is the case the contract names as null. So the reviewer's second
option - restrict the average to processes that started inside the interval - is not available
either without changing a shared type, and it is not needed.

Fixed: no earlier reading means null. `ProcessLifetimeMachinePercent` had no sanctioned case left and
is deleted with its two tests, rather than kept as a helper nothing may call. Measured on this
machine after the fix:

```
first sample   465 processes listed, 0 with a processor share, 296 with memory readable
               highest share reported: none - no process claims a share
second sample  458 listed, 289 with a real interval share, 169 refused
```

The list does not shrink and no process is dropped. Only the quantity that was not measured is
withheld, which is the whole rule.

## 2. Two live tests asserted against whichever disk IOKit listed first - ACCEPTED, fixed

Right, and it is the same class of defect as finding 6 of the earlier review, which went red whenever
a disk image was attached. Both tests write a file and then assert a disk's counters moved, but took
the first disk IOKit listed rather than the disk the write went to. On a Mac with an external disk or
a mounted image enumerated earlier, the write lands on the boot disk and the asserted disk's counters
do not move: **the test fails while the code is correct**, which under the owner's test standard
makes the test the defect.

Fixed by naming the disk the machine runs from - the temporary folder is on the system volume, so
that is the disk whose counters must move. The probe-level test selects
`IsInSystemVolume == true`; the reader-level test walks up from the system volume through IOKit,
which also exercises that walk. The reviewer could not reproduce this (one disk on this machine), and
neither can I; it is fixed on the reasoning, which is sound.

## 3. `CFPropertyListCreateData` declared a 32-bit options argument - ACCEPTED, fixed

`CFOptionFlags` is `unsigned long`, eight bytes here, and the reviewer measured it. Latent rather
than live: the one caller passes the constant 0 and the compiler writes the whole register, so
nothing is wrong today. A non-constant argument could carry stale upper bits into a parameter Core
Foundation requires to be zero. Declared 64-bit, with the reasoning recorded so it is not
"simplified" back. `IOOptionBits` and `CFStringEncoding` nearby really are 32 bits and were correct.

## 4. The smaller points - ACCEPTED, fixed except one that is not mine

- **The refused-process test used `CpuPercent is null` as its proxy for "refused", which over-counts.**
  Correct, and finding 1's fix makes it worse rather than better: now every process without an earlier
  reading is also null. Fixed properly by testing `MemoryBytes`, which is a figure for right now and is
  therefore null if and only if the platform refused the process. That makes the test exact instead of
  approximate - and it matters, because the loose version could have passed on a machine where the
  refused processes were being dropped after all.
- **The live timebase test's comment overstated what it proves.** Right: for a host with two seconds of
  processor time, skipping the conversion gives 0.048 seconds, which is inside the test's window. The
  comment now says so and says what does catch it - the pure tests that pin 125/3 and 1 to 1 against
  captured output, and the two-thread agreement test.
- **`ProcessSnapshot.FiguresRefused` was set and never read.** Removed.
- **`FoldStall` uses the same null for "vm.loadavg failed" and "not offered on this platform".**
  Accepted as correct and NOT fixed here: it is finding 3 of the interface review, it is a property of
  the shared `StallReading` rather than of this probe, and inventing a macOS-only way to say it would
  put the three platforms out of step. Referred to the interface owner. It matters more after the
  narrowing than before, because macOS publishes only two stall figures in total.
- **`ReadDisks` does not check `IOIteratorIsValid`.** Accepted, not fixed: a disk attached during the
  walk can be missing from one sample and is present in the next, which is a five-second delay in
  listing a disk that was plugged in mid-sample. Recorded rather than fixed because the cure is a
  retry loop around a registry walk, and a probe on a five-second timer should not retry; the next
  sample is the retry.

## What I did not do, and what is still open

- **Nothing here was re-reviewed.** These are the author's fixes to his own code, and the one with
  real behaviour change is finding 1. Its evidence is the measured before and after above.
- The reviewer did not cover the two commits after the one it reviewed in the same depth, nor the
  monotonic-clock change, which was uncommitted while it ran. That change is covered by its own
  regression test, checked against the defect rather than merely written: with wall-clock cache aging
  reintroduced it failed with "Assert.NotSame() Failure: Values are the same instance", and passed
  once the fix was back.
- **The clock regression test covered the cache and not the interval - now closed.** I raised this
  doubt myself, the reviewer confirmed it in its addendum, and it is fixed rather than recorded.
  `Read_WhenTheWallClockStepsBackMidSample_TheIntervalAndEveryRateSurviveIt` advances the monotonic
  clock by the interval while stepping the wall clock an hour BACKWARDS - the arrangement that cannot
  occur with one clock reading - and then checks the processor reads, the interval really is the
  monotonic one (this process's figure against the runtime's own over the same interval, within a
  factor of two), and the sample is still stamped with the stepped-back wall time. Checked against the
  defect like the other one: with the interval put back to a wall-clock subtraction it fails, and
  passes once the monotonic interval returns. Both halves of the clock rule are now pinned and both
  tests have been run against the behaviour they are named for.
- No Intel Mac, no spinning disk on macOS, no external or network volume, and the PowerShell gate
  cannot run here. The processor and latency limits remain proposals.
- **The memory agreement check had to be rebuilt after the reviewer read it.** The figures it saw
  (worst 0.34 points) came from comparing the probe against ONE `vm_stat` reading. On a loaded machine
  that same method read 5.95 points - outside the five-point requirement - because available memory
  moves several points in a few seconds here, so a single reading taken at a different moment is a
  different quantity. The check now brackets the probe's read between two `vm_stat` reads: worst 1.36
  points outside the operating system's own range, while the machine moved as much as 4.75 points
  across a single read. Same lesson as the processor windows, which I had already hit once.

## Addendum: the re-review, and three more answers (2026-11-06)

The reviewer re-reviewed the three commits that followed, in a detached throwaway worktree so the
moving working tree could not affect the result. Its addendum is appended to the review. Three things
came out of it.

**Finding 1 was still standing when it looked, and is fixed now.** It re-reviewed at 79f5b243e, which
is one commit before the fix; the fix is 4621bbf5a. Measured after it, on this machine: the first
sample lists 465 processes with **nought** reporting a processor share, where before the fix 328 of 513
did.

**A test of mine failed on correct code when the Director runs as root - ACCEPTED, fixed.** The
refused-process test asserted that at least one process comes back refused. As root nothing is
refused, because root may read every process, so the assertion failed on a correct probe. The reviewer
suggested leaving it and documenting it; I have not, because this repository deletes a test that fails
while the code is correct rather than carrying it with a note. Instead both cases now assert something
specific and true: as an ordinary user some processes must be refused, and as root every process must
be readable. Neither branch asserts nothing.

**`FoldCpu` would not notice a core whose counters stood still - ACCEPTED as a limit, written down
rather than fixed.** The reviewer is right that a still core is not skipped: it adds nought to both
sums, so the denominator silently covers only the cores that moved, and a parked core that had really
been idle would make the machine read busier than it was. There is no honest verdict to put in that
arithmetic, because nothing in it can tell "idle and still counting" from "not counting". So the limit
is now written at the line itself, naming the live per-core assertion as the only thing guarding it and
saying that if that test ever goes red this loop is what has to change. The reviewer also confirmed
from its own measurement that macOS does not take a logical processor offline while running, and that
it found no condition where a parked core's idle counter stops advancing.

## Addendum: the verification review of the four fixes (2026-11-06)

I asked the reviewer to check the four fixes rather than merge on my own measurement, because the
whole lesson of finding 1 is that a fix was answered, believed and never re-run. Its verdict: nothing
in the four is wrong, fix one more thing, then merge. It verified finding 1's fix by running the probe
- first sample 463 processes, 0 with a share, 294 with memory, 169 refused, 0 unnamed, nothing dropped,
against `ps -ax` counting 462 - and confirmed the second sample's shares sum sensibly against the
machine figure.

**The root-user fix was made in one place and not the other - ACCEPTED, fixed.** I changed the
probe-level test and left `ReadProcess_ManyProcessesOnThisMachine_AreReadableAndTheRestAreNullNotAFailure`
in the reader tests asserting `refused > 0` unconditionally, so it failed as root exactly as the other
one used to. Both now assert something specific in each case. This is worth recording as a plain miss:
I found the defect, understood it, fixed the instance in front of me and did not sweep for the rest.

**The reviewer's own suggestion was declined and it agreed.** It had suggested leaving the root case
documented rather than fixed; I did not, because a test that fails while the code is correct is deleted
here rather than carried with a note, and it confirmed that was right and that the ordinary-user branch
is not weaker for it.

**The still-core comment is enough - CONFIRMED, not changed.** I had asked whether `FoldCpu` should
instead report the processor unreadable when a core stands still. It should not: that verdict would
fire on an ordinary idle core between ticks, which is a false alarm on a healthy machine. The written
limit plus the live per-core assertion is the right shape.

**The interval test covers what was meant - CONFIRMED.** It fails on the defect for the right reason:
a wall-clock interval goes negative and the sample reports "first sample".

**Two limits of the memory bracket - ACCEPTED, recorded in the proof file.** The reviewer called the
bracket a fair instrument and better than one reading, with two caveats I have written into section 6
rather than leave implied. The probe fell BELOW the range in three of four runs (0.51, 1.36, 0.73),
which is a lean rather than noise and probably means the probe's read lands a moment after vm_stat's.
And landing inside the bracket is not proof, because the bracket only bounds the quantity if it moved
monotonically between the two reads. The exact check reads vm_stat's formula and the probe's figure out
of the same `host_statistics64` snapshot; that belongs to the memory probe, which this one does not
measure twice, so it is recorded as the way to settle it rather than done here.

**One minor improvement taken.** An unreadable processor reading can now carry the core count where the
cores really were counted - the first sample and the no-ticks case - so the Director's first draw can
say how big the machine is even before any percentage exists. It stays null where the counters could
not be read at all, because then the count was never established either. Pinned by a test.

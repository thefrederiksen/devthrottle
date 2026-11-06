# The four points the review of the narrowed macOS probe left open

The review of the narrowed macOS probe raised four points that were not blocking and were therefore
not settled before the probe merged as `c102fa2e4`. Leaving them in a message would have lost them, so
they are answered here, on main, where the next person to read the probe will find them.

**Two of the four are not macOS questions at all**, and that is the main thing this document says. They
belong to the shared memory probe and to the shared stall reading, where a macOS-only answer would put
the three platforms out of step with each other. Naming the right owner is the answer; a local fix
would be the defect.

## 1. The memory bracket leans consistently BELOW the operating system's range - OPEN, and it is the memory probe's

The exact check is not available from this seat. The probe's figure and `vm_stat`'s formula have to be
read from **one** `host_statistics64` snapshot, and that call is made inside `MachineMemoryProbe`
(issue #2818), not in anything the macOS probe owns.

What was measured, four runs, each bracketing the probe's read between two `vm_stat` reads: the probe's
available-memory figure fell **outside the bracket, below its lower edge, in three runs of four** - by
0.51, 1.36 and 0.73 percentage points. Only run 2 landed inside it. The figures are in
`missions/resource-monitor/phase-1/macos-narrow-check.txt`, column "probe outside".

That is a lean, not noise: noise would scatter either side. The likeliest cause is ordinary and
uninteresting - the probe's read lands a moment after the `vm_stat` read it is compared against, on a
machine whose used memory was falling through the measurement - but *likeliest* is not *measured*, and
it will stay unmeasured until the comparison is made from a single snapshot. For scale, the machine
itself moved as much as **4.75 points across a single bracketed read**, which is more than three times
the worst of these leans.

An earlier revision of this paragraph said the probe "landed inside the bracket every time". That was
false, it contradicted this document's own heading and closing section, and it contradicted the proof
file. It is corrected here rather than quietly: a document that answers an evidence question has to be
checked against the evidence it cites, and this one was not.

Until then, nothing in this mission should quote the macOS memory figure to better than about one and a
half percentage points.

## 2. Landing inside the bracket is not proof - ACCEPTED, and it is a limit on the method, not a defect in the probe

The reviewer is right and this is the more important of the two memory points, because it is about what
the evidence can carry rather than about a number.

Bracketing bounds a quantity **only if that quantity moved monotonically between the two reads.** It
did not have to. Available memory on this machine moved as much as 4.75 points across a single
bracketed read. So a figure that lands inside the bracket may have landed there while the true value
was briefly outside it, and a figure that landed outside - which is what three of these four runs did -
does not prove the probe wrong either.

This is why the measurement lesson in `missions/resource-monitor/qa-plan.md` says a single-reading
comparison against the five-point threshold means nothing in either direction: the same probe measured
0.34 points (passed) and 5.95 points (failed) on the same day, and **neither run measured the probe.**
Bracketing is better than that, and it is still not proof. Only the single-snapshot comparison in
point 1 is proof, and it is the memory probe's to make.

## 3. `FoldStall` cannot tell "could not read it" from "this platform does not offer it" - REFERRED as issue #4648, deliberately not fixed here

`FoldStall` returns the same `null` when `vm.loadavg` could not be read as when the platform publishes
no stall figure at all. Those are different facts and a diagnosis needs to tell them apart: the first
is a fault worth reporting, the second is the normal state of every Mac.

This is interface review finding 3 and a property of the shared `StallReading`, which all three probes
return. Fixing it on macOS alone would mean the macOS probe distinguishing two cases that the Linux and
Windows probes still conflate, through a type whose shape says they cannot be distinguished - three
platforms out of step, and a reader of the shared type misled about what it can express. It is referred
to the Tech Lead as a change to `StallReading` itself.

It is worth stating what macOS does and does not publish, because it is the reason this matters here:
**macOS publishes no stall figure for any resource.** There is no equivalent of
`/proc/pressure/io`, and its process states carry no uninterruptible disk wait. So on this platform the
"not offered" case is not an edge case - it is every reading.

## 4. `ReadDisks` does not check `IOIteratorIsValid` - ACCEPTED, and deliberately NOT fixed

A disk attached or removed while the IOKit registry walk is in progress invalidates the iterator, and
the walk can then finish having missed a disk. `IOIteratorIsValid` is how that is detected. The probe
does not call it.

This is accepted as true and left alone on purpose. The cure for an invalidated iterator is to restart
the walk, and **a probe on a five-second timer must not retry: the next sample is the retry.** A retry
loop around a registry walk makes the probe's own cost unbounded at exactly the moment the machine is
busy doing something with its disks, which is the moment the meter is being watched. One sample missing
one disk, five seconds before a sample that has it, costs a reader nothing. A probe that occasionally
takes an unbounded time to answer costs the whole meter.

What this does mean is that **no verdict may be built on a disk's absence from a single sample.** That
is a rule for whoever folds the verdict, and it is recorded here because the probe cannot enforce it.

## What none of this is

Not one of the four is evidence that a figure the probe reports is wrong. The processor agrees with the
machine's own monitor to within 0.25 percentage points over matched ten-second windows, and memory,
bracketed, to within 1.36 points with the caveat in point 2. Points 1 and 2 are about how well the
memory figure is **known**; points 3 and 4 are about what the shared types can **express** and what a
verdict may **claim**.

## And one the review did not raise - found twice, independently, while deriving the shared contract on macOS (issue #4649)

`ResourceProbeContractLiveTests.ReadWithRates` reads the probe twice and sleeps
`MachineMemoryProbe.CacheFor` plus 250 milliseconds in between, so that every rate has an interval. It
does not ask the probe under test how long **that** probe caches a sample for.

It was safe, and safe only by coincidence. `LinuxResourceProbe.CacheFor` was already written as
`MachineMemoryProbe.CacheFor`, so those two could not drift; it was the **macOS** constant that was its
own literal one second and happened to match. The sleep was therefore 1.25 seconds and the second
`Read` was a genuinely fresh sample. Raise a platform's own cache past that sleep and its second `Read`
returns the **first** sample back out of the cache - so the sample under test is the first sample, which
has no rates at all - and the contract tests go on passing.

**Two of the four contract tests would not notice, and that is the part worth keeping in mind.**
`ASampleFromThisMachine_KeepsEveryRuleTheInterfaceStates` passes, because `ResourceSampleContract`
deliberately accepts a rate-less first sample - `TheFirstSample_...` depends on it accepting one.
`AProcessWhoseFiguresAreRefused_IsStillListedAndNamed` passes, because the first sample already lists
and names every process. Only `PerProcessProcessor_...` would catch it, and only because it sleeps three
seconds of its own. So the failure would read as a green cross-platform run - in the one suite whose
whole purpose is to catch a reading that is self-consistent on one platform and means something
different on another. A check that fails open.

The macOS half is **fixed**: `MacOsResourceProbe.CacheFor` is now `MachineMemoryProbe.CacheFor`, as the
Linux probe already wrote it, so the two cannot drift apart here and the reasoning is recorded on the
field. Windows should do the same when its probe lands.

**That is a local answer to a shared problem, and it does not close the finding.** Tying each probe to
the memory probe's constant means no probe can choose its own cache interval without silently breaking a
contract test that will still report green. The real fix belongs in the shared base - have the contract
ask the probe under test for its own cache interval and sleep on that - and it is referred to the Tech
Lead, who owns the base.

Found independently by this seat and by the reviewer of the derivation, which is the only reason it is
written down with any confidence: it is the kind of coupling that reads as fine to whoever wrote it.

## Where the two referred points went

Neither referral is a place for them to be forgotten, so each has an address:

- **The skip-reason ordering** - on the wrong platform, the skip reason names the removable obstacle
  (the opt-in variable) before the permanent one (the platform), which is backwards. **Issue #4644**,
  which carries the reasoning the deleted `MacOsLiveMachineFactAttribute` used to hold in its doc
  comment, so that reasoning did not go with the file.
- **`StallReading`'s single `null`** for "could not read it" and "this platform does not publish it" -
  point 3 above. **Issue #4648.**
- **The contract's sleep** - point 5 above, the check that fails open. **Issue #4649.**

The last two are changes to shared types that all three probes return or run against, which is why
neither is answered on macOS alone: that would put the three platforms out of step, which is the exact
failure the shared contract exists to prevent.

**Each has an issue number rather than a name, and that is deliberate.** An earlier revision of this
section said these two were "with the Tech Lead" - which is the message-shaped home that this
document's own opening paragraph says loses things. A referral nobody can look up is not a referral.

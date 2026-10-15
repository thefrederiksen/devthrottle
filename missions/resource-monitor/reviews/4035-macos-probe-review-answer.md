# Answer to the review of pull request 4035

Every finding is answered below, accepted or declined with the reason. Nine findings were raised.
Six are fixed, one is fixed in the part that was mine to fix, and two are declined for now with the
reason and what would have to change first.

The review was a good one. It found a defect that defeats the purpose of the probe and proved it out
of my own proof file, and it checked the structure layouts by compiling the operating system's own
headers rather than reading my comments - which is how the three defects before it were found too.

## 1. Root-owned processes vanish from the list - ACCEPTED, fixed

This was the worst of them and it was right. The list was built from the per-process call, which an
ordinary user is refused for another user's process, so roughly three hundred of this machine's
seven hundred processes were not in the list at all - absent, not null - out of a set reporting
itself as read without trouble. The reviewer found the proof in my own live reading: the machine's
own listing named the Spotlight indexer as the two busiest processes on the machine and the probe's
top ten had neither. A probe whose job is to name the culprit could not see the culprit.

Fixed by taking the LIST from the kernel's process table, which an ordinary user may read for every
process, and only the FIGURES from the per-process calls:

- `MacOsNative.sysctl` and `KernelProcessTableName` ask for CTL_KERN, KERN_PROC, KERN_PROC_ALL.
- `MacOsPlatformReader.ReadKernelProcessTable` captures the bytes.
- `MacOsResourceParsing.ReadKernelProcessTable` reads them, at offsets measured by compiling the
  operating system's headers, not counted up from the field list.
- The probe lists every process with its name, parent and start time. A process whose figures are
  refused is IN the list with those figures null.

Measured after the fix, on this machine: 665 processes listed where 423 were listed before, 265 of
them with figures the platform refuses. A new live test,
`Read_Processes_IncludesTheOnesThisUserMayNotMeasure`, fails if the refused processes are ever
dropped again.

Two honest costs. The table's name field is sixteen characters, where the per-process call offers
thirty-two, so a refused process gets the shorter name - the longer one is still preferred wherever
it can be read. And the shared type has nowhere to carry a count of how many processes were refused,
so a consumer can only learn it by counting the nulls; that is part of finding 2 of the interface
review and is not something this probe can fix alone.

## 2. The per-process figure is one core's worth, not a share of the machine - ACCEPTED, fixed

Right, and it was a contradiction between the probe and the contract rather than an arithmetic
slip. `ProcessReading.CpuPercent` is documented as a share of the whole machine from nought to a
hundred, and the per-session total sums those shares; the probe filled it with one core's worth. Two
processes each holding a core on this ten core machine summed to 200 while the machine's own reading
for the same moment said 20.

The contract wins, because it is what the per-session total and the diagnosis are built on and
because the Windows and Linux probes have to answer the same question the same way. Added
`MacOsResourceParsing.ProcessMachinePercent`, which divides by the logical core count; the per-core
helper stays as the building block it always was. The live test that cross-checks against the .NET
runtime's own figure now divides the runtime's figure too, or it would have demanded the probe be
wrong by the core count. The live reading no longer says a hundred percent is one core's worth.

## 3. A process with no previous reading is reported at zero percent - ACCEPTED, fixed

Right, and this is exactly the made-up zero the rules forbid. The `?? 0` reported two different
things as nought: a process the platform refused, and a process that started during the interval. The
reviewer measured the consequence - a child spawned after the first sample, spinning, had used
sixty-five milliseconds of processor time and was written down as completely idle inside a set that
said it had read fine. A freshly spawned agent burning a core looked asleep for a whole five second
tick, which is the machine state the owner actually asks about.

The `?? 0` is gone. A refused process is null. A process with no earlier reading is measured over its
own life - `ProcessLifetimeMachinePercent`, processor time since it started over the time since it
started - which is a real measurement rather than a guess, and is close to the interval rate for a
process that has only just started, which is the only case it is used in. An average over a life is
not the same quantity as a rate over an interval, and the code says so where it is used.

## 4. On an Intel Mac `getfsstat` binds the legacy layout - ACCEPTED, fixed

Accepted without reservation: this is the same shape as the timebase defect, latent rather than
live, and the argument I made for fixing that one applies here in reverse. A direct binding by plain
name does not go through the header that renames the call, so on Intel it would get the older,
smaller layout, stride across the records at the wrong width, and read names out of where the old
layout keeps counters. Nothing would throw and every volume figure would be wrong.

Fixed by naming the entry point: `[DllImport(LibC, EntryPoint = "getfsstat64")]`. Both architectures
export that name for the 64-bit-inode layout, so it is correct on each and cannot drift. The comment
records why the plain name is wrong, because the next reader will otherwise simplify it back.

I could not test this on an Intel Mac - there is none in reach - so the fix rests on the same
evidence the finding does: the headers and the exported symbol tables on this machine. Said plainly
rather than implied.

## 5. The network figure sums tunnels and bridges - ACCEPTED, fixed

Right, and it mattered more than it looks, because this fleet's own traffic runs over a tunnel: a
download counted once on the tunnel and once on the hardware underneath it, and the probe reported
twice the bytes. The contract says physical interfaces that are up, excluding loopback and virtual
ones; the probe excluded only `lo0`.

Added `MacOsResourceParsing.IsPhysicalNetworkInterface`. Three of its four tests come from the
operating system - up, running, not loopback, not point to point, and of Ethernet type - all
measured against its headers. The fourth is a list of name prefixes, and it is there because macOS
genuinely does not mark the rest as virtual in any field: measured on this machine, the access
point, Apple Wireless Direct Link, low latency wireless and the internal network processor
interfaces all report Ethernet, up and running, exactly as the real port does. That part is a
judgement and is commented as one rather than dressed up as a platform fact.

Thirteen unit tests pin it, each using the flags and type this machine really reported, and a live
test asserts the loopback, the tunnels, the bridge and the wireless direct link are all out while at
least one interface is still in.

## 6. Two live tests fail with a disk image attached - ACCEPTED, fixed

Right, and this is the kind of defect I should have caught myself: the tests asserted that EVERY
block storage driver reports a medium type, the probe correctly answered Unknown for a disk image
that publishes none, and the test called that a failure. A test that goes red for what somebody has
plugged in is not reporting a defect in the code, and under the owner's ruling that a test failure
must mean a code defect it is the test that is wrong.

Both now require at least one disk of a known kind, which is what the brief asked for, and allow
Unknown on the others. The guarantee that matters is unchanged: a kind is never guessed, and an
unrecognised medium type still fails loudly rather than being folded into one of the two.

## 7. The volume readings carry two things the contract cannot express - PARTLY ACCEPTED

Two separate things here.

The invented mount point is mine and is fixed. When the file system list could not be read the probe
returned one unreadable volume whose mount point was the made-up string `"(all volumes)"`, so a
screen listing volumes would show a volume by that name - a name no machine has. It now returns an
empty list and logs the reason once.

That trade is honest but not free, and the reviewer's underlying point stands: an empty list cannot
be told apart from a machine with nothing mounted, and a volume whose walk to its disk failed cannot
be told apart from one with no disk behind it. Neither can be fixed from inside this probe - the
sample carries volumes as a bare list with no place for a reason, which is finding 1 of the
interface review. DECLINED here, and it belongs with that finding.

## 8. The cache and the interval are kept on a clock that can step backwards - DECLINED for now

The finding is correct. `DateTime.UtcNow` is corrected by time synchronisation, and a step backwards
would keep serving the cached sample until the clock caught up and would make the interval negative,
so every rate would report the first-sample reason.

Declined in this pull request for two reasons, neither of which is that it does not matter. The
clock is an injected seam that the live tests use to control the interval, so moving the cache and
the interval onto a monotonic clock changes how every one of those tests is written - a real change
on top of an already large one. And `MachineMemoryProbe` has the same shape, so fixing it here alone
would leave the two probes keeping time two different ways, which is worse than both being wrong in
the same known manner. It should be one change across both probes, and it is severity low because it
needs the clock to step while the Director is sampling. Recorded here so it is not lost.

## 9. Housekeeping - ACCEPTED, fixed except where noted

- The summary on `proc_listallpids` still claimed the call returns bytes, which is the very thing
  this branch disproved. Rewritten.
- `IORegistryEntryCreateCFProperties` was declared and never called. Removed.
- The missing statistics key worked out by `TryReadDiskCounters` was discarded with `out _` and the
  disk reported with a generic reason. The reason is now carried through and reported.
- The comment about synthetic volumes described a case the code below it did not have. Rewritten to
  say what the code does.
- `ReadKernelPressureLevel` and the ruling that one means normal are still used only by tests, with
  production putting the raw number on the reading. NOT changed: the shared type asks for the raw
  level and the fold that turns it into words belongs on the Gateway, so moving the mapping into the
  probe would put it in the wrong place. The duplication the reviewer predicts is real, and the right
  fix is for the Gateway fold to call this function rather than write its own.
- The eight volume and sixty-four process headroom cut-offs are unchanged. They are the room left
  for things appearing between two calls, and a silent cut needs a log line; the process list no
  longer uses that path at all, because the kernel table is read with its own headroom and is bounded
  by what the kernel wrote.

## The four unanswered findings of the interface review

The reviewer asked whether they change anything here. They do, and this probe is now the measured
evidence for three of them:

1. Volumes cannot report that enumeration or mapping failed - see finding 7 above.
2. An unreadable process is dropped rather than counted - this was finding 1, and it cost the probe
   the two busiest processes on the machine. Working around it needed the shared type's process
   figures to become nullable, which is the change made in this pull request.
3. Nullable figures conflate "not offered" with "this read failed" - still true of the per-process
   disk figures, which use null for three different things.
4. Positional constructors bypass the factories - `ProcessReading` has no factory, which is why the
   `?? 0` of finding 3 could be written at all.

**One shared type changed in this pull request, and it should be reviewed as a shared change rather
than a macOS one.** `ProcessReading.CpuPercent` is now `double?` and `MemoryBytes` is now `ulong?`,
because a process that is still running and whose figures the platform refuses must be listed and
named with its figures unknown, and there was no way to say that. Both changes are
source-compatible for anything constructing the record positionally with non-null values, so the
Windows and Linux probes still compile; each will have to decide what its own platform refuses.

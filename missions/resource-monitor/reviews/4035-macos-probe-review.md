# Review of pull request 4035: the macOS resource probe

## Scope

I reviewed the pull request at commit `0e24f692d` on branch `resource-monitor/macos-probe`, in a
worktree of my own cut from that commit: all five production files, the four test files, the
captured fixtures and their README, and the three proof files under `missions/resource-monitor/phase-1`.
I did not write any of it, and I did not merge, fix or release anything.

I checked the probe against two things rather than against the author's comments: Apple's own
headers on this Apple M4 Mac mini, by compiling a small C program that prints every structure
size, field offset and constant the probe hard-codes, and the operating system itself, by rerunning
every check and adding two temporary tests of my own that were deleted afterwards and are not in
this branch.

The branch was three commits behind `origin/main` at review time; none of those commits touches
`src/CcDirector.Core/Machine`.

## Commands I ran, and what they printed

All on this machine, with the software development kit at version 10.0.301 as `global.json` requires.

| Command | Result |
| --- | --- |
| `dotnet build cc-director.sln -c Release -p:EnableWindowsTargeting=true` | Build succeeded, 0 warnings, 0 errors, 1 minute 3 seconds |
| `dotnet test src/CcDirector.Core.UnitTests -c Release -p:EnableWindowsTargeting=true --filter "Category=Resources"` | Passed, 110 of 110 |
| `dotnet test src/CcDirector.Core.Tests -c Release -p:EnableWindowsTargeting=true --filter "Category=Resources"` | Passed, 36 of 36 |
| `dotnet test src/CcDirector.Core.UnitTests -c Release -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~GateTestsDoNotDependOnTheMachine"` | Passed, 11 of 11 |
| The same 36 live tests again, with a 20 megabyte disk image attached | **Failed, 2 of 36** (finding 6) |

Every test in the four new test files carries the `Resources` category trait: 19 of 19, 17 of 17,
58 of 58 and 4 of 4.

Not run: `scripts/test-local.ps1`. It is PowerShell and does not run on a Mac, which the author also
says. The default gate on Windows is still owed before this lands.

## What I checked and found nothing wrong with

Said plainly so nobody re-checks it.

**Every structure offset, size and constant, against the headers on this machine.** My compiled
check agrees with the probe on all of them: `ifaddrs` (next 0, name 8, address 24, data 48), the
address family byte at offset 1, `AF_LINK` 18, `if_data` (packets in 20, packets out 28, bytes in 40,
bytes out 44, all four bytes wide), `statfs` on this architecture (size 2168, block size 0, blocks 8,
free 16, available 24, type name 72, mounted on 88, mounted from 1112), `xsw_usage` (size 32, total 0,
available 8, used 16, encrypted 28), `loadavg` (size 24, scale at 16), `proc_taskinfo` (size 96,
resident 8, user 16, system 24), `proc_bsdinfo` (size 136, parent 16, long name 64 for 32 bytes, start
seconds 120, start microseconds 128), `rusage_info_v2` (size 160, disk read 144, disk written 152),
the processor info flavour 2 with four states ordered user, system, idle, nice, `MNT_NOWAIT` 2, the
two registry iteration options 1 and 2, the property list format 100 and the string encoding. The
author's three measured corrections are all right.

**The processor time units.** Reproduced independently in C: after a fixed burn, `getrusage` said
0.153 seconds, the task info read as nanoseconds said 0.004, and the task info through the
timebase (125/3 on this machine) said 0.153. The conversion is right and necessary.

**The memory pressure numbering.** The kernel reads 1 on this healthy machine, and 1, 2 and 4 are
exactly the three constants the system's own dispatch header defines for normal, warning and
critical. The mapping is right.

**The property list path.** The document type declaration is ignored and nothing is fetched; a disk
whose characteristics carry no medium type comes out as `Unknown` and is not guessed at (verified
with a disk image, which is exactly such a disk); a statistics dictionary missing a key fails the
disk with a reason rather than reading zero.

**The walk from a volume to its disk.** Breadth first over all parents, with a seen set keyed by
registry entry identifier and a cap of 64 entries, so a converging or looping registry cannot hang it
and a container built on two stores names both. With the disk image attached, its volume was
attributed to the image's own disk and the root volume stayed on the internal disk. Every handle
taken is released in a `finally`, including the ones left in the queue.

**The one second cache under concurrent callers.** The check and the fill are under one lock, so a
burst of callers takes one pass, and the test that proves it is real.

**Whether the live tests would catch the three defects if they came back.** They would. At the
calculated 144 bytes the kernel writes 136, the reader treats a short write as unreadable, every
process is dropped, and the process tests fail. The process list comes back newest first with
launchd and the kernel last (I measured the last three identifiers as 117, 1 and 0), so reading the
count as bytes cuts launchd off and the test asserting process 1 is present fails. The timebase
cross-check allows a factor of two and the defect was out by forty-two.

## Findings, worst first

### 1. Root-owned processes vanish from the list, and the proof file shows the two busiest on the machine missing

**Severity: must fix** (together with finding 2 of the interface review, which this is the measured case of)
**Files:** `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:624-628`; `src/CcDirector.Core/Machine/MacOsPlatformReader.cs:426-442`; `missions/resource-monitor/phase-1/macos-live-reading.txt:160-181`

The probe reads each process's name, parent and start time with the same call it reads its
processor time with, and that call is refused for every process that belongs to another user. On
this machine as this user: 724 process identifiers, 423 readable, and the refusal is "Operation not
permitted" for both the name call and the task call. So 301 processes are not "null figures for
that process", as the comment at line 624 and the proof file at line 179 say: they are absent
entirely, name and all, and `ProcessSetReading.CouldRead` is true with nothing to say so.

The concrete case is in the author's own proof. The machine's own listing at line 168 names
`mds_stores` at 70.3 percent and `mds` at 60.2 percent as the two busiest processes on the
machine. The probe's top ten, directly above it, has neither. The mission exists to name the culprit,
and on a Mac the culprit was Spotlight indexing, which runs as root, and the probe could not see it.

What the platform does allow without privilege: the kernel's process table through
`sysctl` with `KERN_PROC_ALL` lists every process with its name, parent, owner and start time (720
of 720 here, 179 of them root-owned). Processor time and memory for another user's process are
genuinely not offered to an ordinary user, and the honest shape is a process whose name and parent
are known and whose figures are unreadable with that reason, plus a count on the set of how many
were refused. The current shared types cannot carry either, which is why this is also an answer to
the interface review below.

### 2. The per-process processor figure is one core's worth, where the contract says a share of the whole machine

**Severity: must fix** (one side or the other)
**Files:** `src/CcDirector.Core/Machine/MacOsResourceParsing.cs:757-768`; `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:682-693`; `src/CcDirector.Core/Machine/ProcessReadings.cs:20`; `src/CcDirector.Core/Machine/SlownessDiagnosis.cs:95`; `src/CcDirector.Core.UnitTests/Machine/MacOsResourceParsingTests.cs:783`

`ProcessReading.CpuPercent` is documented on the shared type as "a share of the WHOLE machine, 0 to
100", and `SessionUsage.CpuPercent`, which is a sum of them over a session's process tree, says the
same. The probe fills it with one core's worth, 0 to 100 per core, and the unit test at line 783
pins that: four cores fully used is four hundred. The live reading says so too: "a hundred percent
is one core's worth".

Concrete case, on this ten-core machine: a session whose two processes each hold one core. The
probe reports 100 and 100, the session's tree sums to 200, the whole-machine processor reading for
the same interval says 20 percent busy, and the diagnosis is handed a session using ten times the
load of the machine it runs on. Whichever meaning is right (the per-core one is what every Mac
monitor shows, the whole-machine one is what the shared types promise), the probe and the contract
must say the same thing, and the Windows and Linux probes built next will each pick one.

### 3. A process with no previous reading is reported at zero percent, which is the made-up zero the rule forbids

**Severity: must fix**
**File:** `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:693`

`CpuPercent: cpuPercent ?? 0`. The comment says the set as a whole covers this through the
first-sample reason on the other readings, and that is true only on the very first sample. On every
sample after it, a process that started during the interval, or whose identifier was reused, has no
previous reading and is written down as 0 percent, inside a set whose `CouldRead` is true. The same
`??` also hides the case where the processor time went backwards.

Measured with a temporary test, since deleted: a child that does nothing but spin was started
after the first sample and had used 65 milliseconds of processor time by the second. The second
sample reported it at `CpuPercent = 0` with the set readable and no reason. A freshly spawned agent
that is burning a core shows as idle for one whole interval, which is five seconds in the Director,
on exactly the kind of machine the owner is asking about. The shared type has no way to say "no
rate yet" for one process, which is finding 4 of the interface review in practice; until it has,
the honest figure for such a process is its processor time since it started over the time since it
started, which the probe already holds, not zero.

### 4. On an Intel Mac, `getfsstat` binds to the legacy layout and every volume figure is read from the wrong place

**Severity: should fix** (no Intel build ships today, so it is latent; it is the mirror image of the timebase defect and the same kind)
**Files:** `src/CcDirector.Core/Machine/MacOsNative.cs:127`; `src/CcDirector.Core/Machine/MacOsResourceParsing.cs:446-456`; `src/CcDirector.Core/Machine/MacOsPlatformReader.cs:283-304`

The 2168 byte layout the parser reads is Apple's 64-bit-inode `statfs`. On Apple Silicon that is
the only layout, and the plain symbol `getfsstat` writes it. On x86_64 Apple kept the old layout
(90 character names, `long` counters, a different size) under the plain symbol and put the new one
under `getfsstat$INODE64`; C programs get the new one because the header renames the call, and a
`DllImport` of `"getfsstat"` does not go through the header. Evidence on this machine: `sys/cdefs.h`
sets `__DARWIN_ONLY_64_BIT_INO_T` to 0 for x86_64 and 1 otherwise; `sys/mount.h` line 447 declares
`getfsstat` with `__DARWIN_INODE64`, and line 140 is the legacy structure; `dyld_info` lists
`_getfsstat` and `_getfsstat$INODE64` as two different exports in the x86_64 slice of the kernel
library and only `_getfsstat` in the arm64e slice.

Concrete case: on an Intel Mac the second call succeeds, writes records of the legacy size, and the
parser strides across them at 2168 bytes reading text from where the legacy layout keeps counters
and counters from where it keeps text. Nothing throws, and the volumes reported have the wrong names
and sizes. The release workflow publishes only `osx-arm64`, but `scripts/local-build-mac.sh` offers
`osx-x64`, and the author's own argument for the timebase fix applies here in reverse. The fix is to
choose the entry point by process architecture, or to bind `getfsstat64`, which both slices export
at the same address as their 64-bit-inode symbol.

### 5. The network figure sums tunnels, bridges and every other virtual interface, against the contract

**Severity: should fix**
**Files:** `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:510-523`; `src/CcDirector.Core/Machine/NetworkReading.cs:6-9`

`NetworkReading` is defined as "summed over the physical interfaces that are up, excluding loopback
and virtual ones", and says why: virtual layers carry the same bytes again. The probe excludes only
`lo0`. This machine has `utun0` to `utun5`, `bridge0`, `awdl0`, `llw0`, `ap1`, `gif0`, `stf0` and
four `anpi` interfaces, all counted.

Concrete case: a 100 megabyte download over the Tailscale tunnel this fleet uses counts once on
the tunnel interface and once on the Ethernet interface it rides on, and the probe reports 200
megabytes received. The interface flags and type are in the same `getifaddrs` entry and `if_data`
the probe already reads, so the exclusion costs nothing.

### 6. Two live tests fail on any Mac with a disk image or an external disk attached

**Severity: should fix**
**Files:** `src/CcDirector.Core.Tests/Machine/MacOsPlatformReaderLiveTests.cs:28-60`; `src/CcDirector.Core.Tests/Machine/MacOsResourceProbeLiveTests.cs:165-183`

Both tests assert that EVERY block storage driver has a known medium type. A disk image has none:
its device characteristics carry a product name and a revision and no "Medium Type" key, and many
external enclosures are the same. I attached a 20 megabyte disk image and reran the 36 live tests:
2 failed, with "disk disk4 reported no medium type, so it cannot be called spinning or solid-state"
and "Expected: Not Unknown, Actual: Unknown". The probe was right both times (kind `Unknown`, the
image's volume attributed to the image's own disk); the tests were wrong. The requirement was at
least one physical disk with a name and a known kind, and a developer with an installer image open
gets a red gate for it. Detached afterwards.

### 7. The volume readings carry two things the contract cannot express, so the probe invents around it

**Severity: should fix with the interface findings**
**Files:** `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:428`, `:724`, `:466-498`

When `getfsstat` fails the probe returns one `VolumeReading` whose mount point is the invented
string `"(all volumes)"`, because a bare list has nowhere else to put a reason. A consumer that
lists volumes will show a volume called "(all volumes)". And when the walk up from a volume fails
for a transient IOKit reason, the empty result is cached in the volume-to-disk map until the set of
mounted devices changes, and is indistinguishable from "this volume has no disk behind it". Neither
is the probe's fault; both are what finding 1 of the interface review predicted.

### 8. The cache and the interval are kept on a clock that can step backwards

**Severity: low**
**Files:** `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:83`, `:125`, `:163`

`DateTime.UtcNow` is wall time and is corrected by time synchronisation. A step backwards of a few
minutes keeps serving the cached sample until the clock catches up to one second past when it was
taken, and makes `since` negative so every rate reports the first-sample reason. A monotonic clock
for the cache and the interval, with wall time kept only for `TakenAtUtc`, would not. The existing
memory probe has the same shape, so this is a shared precedent to fix once.

### 9. Housekeeping, none of it wrong on the machine but each a trap for the next reader

**Severity: low**

- `src/CcDirector.Core/Machine/MacOsNative.cs:150-155`: the summary on `proc_listallpids` still says
  it "returns the number of BYTES written, not the count". That is the mistake the branch fixed, and
  the reader at `MacOsPlatformReader.cs:385-390` now says the opposite, correctly.
- `src/CcDirector.Core/Machine/MacOsNative.cs:204-206`: `IORegistryEntryCreateCFProperties` is
  declared and never called.
- `src/CcDirector.Core/Machine/MacOsResourceParsing.cs:430-436`: `ReadKernelPressureLevel` and the
  ruling that 1 is normal are used only by tests. Production puts the raw number on the reading, so
  the Gateway fold will have to re-derive the same mapping, in a second place.
- `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:436-438`: the comment about synthetic volumes
  describes a special case that the code below it does not have.
- `src/CcDirector.Core/Machine/MacOsResourceProbe.cs:305` and `:338`: the missing-key reason that
  `TryReadDiskCounters` works out is discarded with `out _`, and the disk is reported with the
  generic "could not be read" instead of the key that was missing.
- `src/CcDirector.Core/Machine/MacOsPlatformReader.cs:296-298` and `:406`: if more than eight volumes
  or sixty-four processes appear between the two calls, the extras are cut off silently. Unlikely,
  and worth one log line when it happens.

## Do the four unanswered findings of the interface review change anything here?

Yes, three of the four are already biting in this probe, and the fourth is what lets finding 3 compile.

1. **Volumes cannot report that enumeration or mapping failed.** The probe invents a mount point
   called "(all volumes)" to carry the reason, and a failed walk is cached as "no disk" (finding 7).
2. **An unreadable process is dropped rather than counted.** This is finding 1, measured: 301 of 724
   processes, the two busiest on the machine among them, gone from a set that says it read fine.
3. **Nullable figures conflate "not offered" with "this read failed".** The probe uses `null` on
   `DiskReadBytesPerSecond` for three different things: the platform refused the usage call for that
   process, there is no previous reading yet, and the figure not being offered. The contract says
   null means the last one only.
4. **Positional constructors bypass the factories.** `ProcessReading` has no factory, so the probe
   constructs it positionally, and that is the construction in which `cpuPercent ?? 0` sits
   (finding 3). A factory that took a nullable rate would have refused it.

So the shared types should be settled before the Windows and Linux probes repeat all four.

## What this review does not cover

- I could not run anything on an Intel Mac; finding 4 rests on the headers and the exported symbol
  tables, both read on this machine, not on a reproduction.
- One machine, one internal solid-state disk plus one disk image. No spinning disk, no external or
  network volume, no second physical store under one container.
- The PowerShell gate did not run here.
- I did not review the shared types themselves beyond what the probe's use of them exposes.

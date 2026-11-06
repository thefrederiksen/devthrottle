# Review of the narrowed macOS resource probe (issue #2811)

Reviewed: commit 234a1aac1 on branch resource-monitor/macos-probe-narrow, in the worktree
devthrottle-macos-narrow, on devthrottle-mac-mini (Apple M4, 10 cores, macOS 26.5.2, arm64), 2026-11-06.

The branch moved while this review ran: it was rebased so the temporary dev base is gone, the reviewed
commit became d70cc812d, and two commits followed (35e3b41f6, 78a876613) with a further uncommitted edit
moving the probe onto TimeProvider. I checked with git diff that the five production files and the four
test files in d70cc812d are byte-for-byte the files I reviewed in 234a1aac1. The later commits and the
in-progress edit were read but not reviewed to the same depth; see the last section.

## What I ran

    dotnet build cc-director.sln -c Release -p:EnableWindowsTargeting=true     0 errors, no warnings in the macOS files
    dotnet test src/CcDirector.Core.UnitTests ... --filter Category=Resources    112 passed, 0 failed, 0 skipped, 25 ms
    dotnet test src/CcDirector.Core.Tests ... --filter Category=Resources        35 passed, 0 failed, 0 skipped, 440 ms

A C program compiled against this machine's headers printing every size, offset and constant the code
hard-codes; a cross-compile of the same program for x86_64; the exported-symbol stubs of
libsystem_kernel read per architecture; and a scratch console program referencing the built
CcDirector.Core library that read the probe directly and ran 3300 uncached passes while the process's
Mach port count and resident set size were watched from outside.

## Findings, worst first

### 1. The per-process processor figure is a lifetime average whenever a process has no earlier reading, and the contract says it must be null

Verified by running. Medium.

Where: src/CcDirector.Core/Machine/MacOsResourceProbe.cs, `ProcessPercent` ("No earlier reading for this
process: its whole life IS its interval") and src/CcDirector.Core/Machine/MacOsResourceParsing.cs,
`ProcessLifetimeMachinePercent`, whose remark says the lifetime average is used only for a process that
has just started, "which is the only case it is used in".

The contract on `ProcessReading.CpuPercent` (src/CcDirector.Core/Machine/ProcessReadings.cs) reads:
"Processor used over the last interval ... Null when it is not known: the platform refused this process,
or it started during the interval and has no earlier reading to compare against." The probe returns the
lifetime average for EVERY process with no earlier reading, not only one that started during the
interval. The worst case is the first sample, where no process has an earlier reading. Measured on this
machine through the built library:

    first sample:  processor unreadable (first sample, no interval yet)
                   513 processes listed, 328 with a non-null CpuPercent, 185 null
                   5.776%  suggestd (process 17663) started 2026-11-04 23:25:02Z

That 5.78 percent for suggestd is its average over two days, shown in a field the contract defines as the
share of the machine over the last interval, in the same sample whose processor line says there is no
interval yet. The same path is taken whenever a process is missing from the previous sample for any
reason, for example after a process id was reused.

Concrete failure: a process that has been alive for thirty days and has just started burning a whole core
reads at its thirty-day average, close to nought, on the Director's first sample and on any sample where
its previous reading was lost. A session's total built from these shares is wrong in the same way, and
nothing in the sample marks the figure as a different quantity. It is not a zero, but it is a plausible
figure for a quantity that was not measured, which is the thing the rules forbid.

The earlier review's finding 3 and its answer describe the intended narrower behaviour ("a process that
started during the interval is measured over its own life"). The code does not restrict it to that case.
Either return null when there is no earlier reading, as the contract says, or restrict the lifetime
average to processes whose start time falls inside the interval and change the contract to say so. Not
mine to choose.

### 2. Two live tests assert against whichever disk IOKit lists first, and write to the temp folder

Reasoned, not reproduced (this machine has one disk). Low.

Where: src/CcDirector.Core.Tests/Machine/MacOsPlatformReaderLiveTests.cs,
`ReadDisks_AfterWritingAFile_TheCountersHaveMovedForward` (reads `disks[0]`), and
src/CcDirector.Core.Tests/Machine/MacOsResourceProbeLiveTests.cs,
`Read_WhileAFileIsBeingWritten_TheTimePerWriteAndQueueDepthAreReal` (`Disks.First(entry => entry.CouldRead)`).

On a Mac with an external disk or a mounted disk image that IOKit enumerates before the internal disk,
the eight or sixteen mebibytes are written to the boot disk and the asserted disk's write counters do not
move. The test fails on correct code, which is the same class of failure the earlier review's finding 6
removed from the kind check. The disk to assert on is the one with `IsInSystemVolume == true`, or the one
`statfs` names for the temp path. Separately, these tests flush real writes to the disk inside a gate
suite; the suite is parked, but the owner's test standard applies to a parked run too.

### 3. `CFPropertyListCreateData` is declared with a 32-bit options argument where the platform type is 64 bits

Reasoned; the type size was measured. Low.

Where: src/CcDirector.Core/Machine/MacOsNative.cs, `CFPropertyListCreateData(..., uint options, ...)`.
`CFOptionFlags` is `unsigned long`, 8 bytes on this platform (measured). It works today because the one
caller passes the constant 0 and the just-in-time compiler writes the whole register; a non-constant
argument could carry stale upper bits into a parameter Core Foundation requires to be zero. Declare it
as a 64-bit unsigned value. `IOOptionBits` and `CFStringEncoding` are genuinely 32 bits and are declared
correctly.

### 4. Smaller points, none of which produces a zero

- `Read_Processes_IncludesTheOnesThisUserMayNotMeasure` uses `CpuPercent is null` as the proxy for
  "refused". With the fixed test clock of 2026-10-15, a process started after that date with no earlier
  reading also reads null (negative age), so the proxy over-counts. The reader-level assertion added in
  35e3b41f6 (some processes really are refused by the per-process call) closes that gap.
- The live timebase test `ReadMachTimebase_OnThisMachine_IsUsableAndConvertsProcessorTimeSensibly` says
  the wrong conversion "puts it outside" its window of 0.001 to 86,400 seconds. For a test host with two
  seconds of processor time, skipping the conversion gives 0.048 seconds, inside the window. The pure
  tests with 125/3 and the two-thread agreement test are what actually catch it; the live test's comment
  overstates what it proves.
- `FoldStall`: when `vm.loadavg` fails but the pressure level reads, `RunQueueLength` is null, which is
  the same null as "not offered on this platform". This is the interface review's own finding 3, still
  open here for the stall figures.
- `ProcessSnapshot.FiguresRefused` is set and never read.
- `ReadDisks` does not check `IOIteratorIsValid`, so a disk attached during the walk could be missing from
  one sample; the next sample has it.

## Checked and found correct - no need to re-check

Sizes, offsets and constants, every one matching the code, from a C program against this machine's
headers: `struct statfs` 2168 with `f_mntfromname` at 1112 (1024 bytes); `proc_taskinfo` 96 with
resident 8, user 16, system 24; `proc_bsdinfo` 136 (not 144) with parent 16, `pbi_name` at 64 for 32
bytes, start seconds at 120 (8 bytes), microseconds at 128; `rusage_info_v2` 160 with disk read at 144
and written at 152; `xsw_usage` 32 with encrypted at 28; `struct loadavg` 24 with three 4-byte figures
at 0, 4, 8 and an 8-byte scale at 16; `kinfo_proc` 648 with start seconds at 0 (8 bytes), microseconds
at 8 (4 bytes), process id at 40, `p_comm` at 243 for 17 bytes, effective user id at 420, parent at 560;
`PROCESSOR_CPU_LOAD_INFO` 2, `CPU_STATE_MAX` 4, states user 0, system 1, idle 2, nice 3; flavours 3, 4
and 2; sysctl name 1, 14, 0; `kIORegistryIterateRecursively` 1, `kIORegistryIterateParents` 2,
`kCFPropertyListXMLFormat_v1_0` 100, `kCFStringEncodingUTF8` 0x08000100, `kIOMainPortDefault` 0, the
plane name "IOService". The Mach types the declarations rely on: `mach_port_t` and `host_t` 4 bytes,
`vm_address_t` and `vm_size_t` 8 bytes, `integer_t` 4 bytes.

The `statfs64` entry point. The software kit's libsystem_kernel stubs export `_statfs64` for the x86_64
and arm64 targets alike, and `_statfs$INODE64` for x86_64 only. A C program calling `statfs` compiled for
x86_64 binds `_statfs$INODE64`; the plain `_statfs` on x86_64 is the legacy layout. So a direct binding
of the plain name would read the legacy layout on an Intel Mac, exactly as the code comment says, and
naming `statfs64` is right on both. `_mach_task_self` and `_mach_host_self` exist as exported functions
on both targets, so binding them by name works even though the C header makes the first a macro.

The Mach timebase. This machine reports 125/3. The conversion multiplies in UInt128 so a long-lived
process cannot overflow, and a zero numerator or denominator makes every process's processor figure
null, never zero. The pure tests pin 125/3, 1/1 and the hundred-day case.

Memory and handle ownership. Every IOKit entry, iterator and parent, every Core Foundation key,
property and data, the host send right and the kernel-allocated processor array are released in a
finally, and `IOServiceGetMatchingServices` is not double-released. The `vm_deallocate` size is the
returned count times four, which is the documented pattern. Measured: across 3300 uncached passes at
about 1.4 milliseconds each, the process's Mach port count stayed between 51 and 55 and its resident
set size did not grow. The 32-bit return of `mach_host_self` read as IntPtr is harmless because every
callee reads only the low 32 bits.

Zero, empty or false standing in for unknown. The first sample's processor and disk figures say "first
sample, no interval yet". `BusyPercent` is null. The three stall percentages, hard page faults and
processes waiting on disk are null. `IsInSystemVolume` is null, not false, when the walk returns no
ancestor. A refused process is listed with null figures. `ProcessMachinePercent` is null when the core
count is zero. Swap at zero is reported as a successful reading, correctly. A counter that steps
backwards gives null or an unreadable reading. A wrapped 32-bit tick counter drops that core from one
interval and does not zero it. The only exception is finding 1.

No subprocess anywhere in the five production files. Try and catch only at the boundary
(`ReadUncached`) and in the constructor's timebase read. Public `Read` does not log entry and exit, and
commit 78a876613 writes down why, matching `MachineMemoryProbe`.

Tests. `MacOsOnlyFact` sets Skip with a reason off macOS; here 0 of 147 skipped. The safety test on a Mac
asserts `Memory.CouldRead` rather than returning early. No test reports a pass on an empty result except
the two deliberate negative cases (a device and a sysctl that do not exist). The parsing tests never
touch the operating system; the fixtures are committed.

Concurrency. The cache check and fill happen under one lock, the whole pass runs under it, `_cached` and
`_previous` are written only there, and a published sample is immutable. A burst of callers takes one
pass.

Scope. Nothing dropped by the 2026-11-06 narrowing is read or reported: no volume list, no free space,
no byte rates, no network. The disk evidence carries exactly the fields section 5c-i allows. I agree with
the answer's triage of the nine earlier findings, with one correction: finding 3's answer describes a
narrower behaviour than the code has (finding 1 above).

## What this review did not cover

- No Intel Mac: the `statfs64` conclusion rests on the exported-symbol stubs and a cross-compile, not a
  run. No spinning disk, no external or network volume, no disk attached or detached mid-sample.
- The PowerShell gate did not run; it cannot on a Mac.
- The two later commits and the uncommitted TimeProvider edit were read but not reviewed in depth. The
  edit measures every interval with a monotonic timestamp and stamps the sample with wall-clock time,
  which is right; the lifetime path in finding 1 still uses wall-clock age, which is also right since
  process start times are wall-clock.
- The shared types from the interface branch, the Gateway fold and every screen.

## Addendum: the three commits that followed, reviewed at 79f5b243e

The Developer asked for the later commits to be checked as hard as the first. They were, at commit
79f5b243e, built and tested in a detached throwaway worktree so the moving working tree could not
affect the result. Result: the solution builds in Release with no errors and no warning in the macOS files (the one warning is the pre-existing clipboard one in TerminalCopyMenuTests); 165 unit tests and 35 live tests pass, 0 skipped. That matches the Developer's claim.

**35e3b41f6, the process lister removed.** Nothing production needs went with it. `ReadKernelProcessTable`
is still there and is the only source of the process list; `proc_listallpids` and `ReadProcessIds` survive
only in comments (git grep over src, tools, docs and missions at HEAD). The two rewritten tests assert what
their names claim: the table test requires more than fifty rows, this test process, launchd, a non-blank
name on every row and at least one row owned by a different user; the per-process test requires more than
ten readable, readable plus refused equal to the row count, and at least one refused. One consequence to
know: on a Mac where the Director runs as root, nothing is refused and that last assertion fails on correct
code. The suite is parked and the assertion documents the ordinary-user case, so I would leave it and say
so in the test.

**78a876613, every core keeps ticking.** The per-core assertion is right to add. On the question of whether
a core can stop ticking: macOS does not take a logical processor offline while running (`hw.activecpu`
equals `hw.logicalcpu` here, 10 and 10, with 4 performance and 6 efficiency cores), and this review found
no condition under which the idle counter of a parked core stops advancing; the measurement is the
evidence and I cannot add to it from one machine. Note `FoldCpu` would not notice a core whose counters
stood still: `after.Total == before.Total` is not skipped, it adds nought to both sums, so the denominator
would silently shrink exactly as the commit message fears. The per-core live assertion is therefore the
only guard, which is a reason to keep it. The logging decision is the right call and matches
`MachineMemoryProbe`; the standard's purpose, that failures reach the log, is met by the once-per-resource
line.

**79f5b243e, the monotonic clock.** Correct as far as it goes: the cache ages and every interval is
measured with `GetTimestamp` and `GetElapsedTime`, the wall clock only stamps the sample, and
`RatePerSecond` goes through `ResourceRate`. The lifetime path in finding 1 still subtracts the process
start time from wall-clock time, which is right because process start times are wall-clock; a step
backwards makes that age negative and the figure null for one sample, which is harmless.

The Developer's own doubt is correct: the regression test
`Read_WhenTimeSynchronisationStepsTheWallClockBackwards_TheCacheIsUnaffected` pins the CACHE only. If
`since` in `ReadEverything` were put back to a wall-clock subtraction, that test still passes, because it
asserts only which object comes back. The interval side is unpinned. It cannot be pinned off macOS, where
every reading is unreadable regardless, so it wants a live test: take a first sample, do real work, advance
the monotonic clock by the interval AND step the wall clock back by an hour (the live `TestClock` moves
both together today and needs a separate step), then assert `Cpu.CouldRead` and that this process's
`CpuPercent` is non-null and inside the two-fold window of the runtime's own figure, as the two-thread
test already does. With the wall-clock interval, that sample's processor reads "first sample" and every
process rate is null, so the test fails on the defect.

The re-measured figures in macos-narrow-check.txt (processor worst 0.63 points, memory worst 0.34) were
read, not re-measured by me.

**Finding 1 stands at 79f5b243e.** The lifetime-average path is unchanged. The working tree was mid-edit
when I last looked (an unused `figuresRefused` variable failing the build under TreatWarningsAsErrors), so
whatever is being changed there is not reviewed.

# Review of pull request 4016: shared resource probe interface

## Scope

I reviewed the pull request at commit `5237e2128`, including every changed production type and both changed test files. I also read the pull request description, sections 3, 5, 5b, 6 and 7 of `missions/resource-monitor.html`, the repository instructions, `docs/CodingStyle.md`, and `docs/CODE_REVIEW_GUIDE.md`. I checked the types against the later platform probes, per-session attribution, slowness diagnosis, cached concurrent consumers, and the rule that an unavailable measurement must never become zero.

I attempted both required commands in the review worktree. Neither command reached compilation or test discovery because this machine has software development kit 8.0.131, while `global.json` requires 10.0.301. Both commands exited with status 145:

- `dotnet build src/CcDirector.Core.UnitTests -c Release -p:EnableWindowsTargeting=true`
- `dotnet test src/CcDirector.Core.UnitTests --filter "Category=Resources" -p:EnableWindowsTargeting=true`

I could not reach a live platform implementation or later consumer because this pull request deliberately contains types only. I did not treat the branch's own check record as independent proof.

## Findings

### 1. The volume contract cannot report that volume enumeration or disk mapping failed

**Severity: must fix**  
**Files:** `src/CcDirector.Core/Machine/ResourceSample.cs:15-25`; `src/CcDirector.Core/Machine/DiskReadings.cs:146-166`

`ResourceSample.Volumes` is a bare list whose empty state is documented as “none could be listed.” A successful machine with no mounted volumes has the same value. There is no `CouldRead` or reason at the collection level, unlike disks and processes. Inside a readable volume, an empty `PhysicalDiskNames` likewise means only “not known”; it cannot say whether mapping is not offered on that platform or whether an attempted mapping failed, and it cannot carry the failure reason.

This breaks two mission requirements. The meter and log cannot say why volume data could not be read, and the slowness diagnosis cannot distinguish “this volume has no mapped physical disk” from “the mapping needed to identify the spinning disk failed.” A later phase would have to break `ResourceSample` and `VolumeReading` to add those states.

### 2. Silently dropping an unreadable process makes a successful process set look complete

**Severity: must fix**  
**File:** `src/CcDirector.Core/Machine/ProcessReadings.cs:5-32`

The contract explicitly says a process that cannot be read is omitted. `ProcessSetReading.CouldRead` can therefore be true while an unknown number of processes and their usage are missing. A process disappearing between enumeration and inspection is normal, but access denial or failure to read a still-running process is also represented by omission.

Per-session totals can consequently report less processor and memory use than the session really consumes, and the diagnosis can name the wrong busiest process or no culprit at all. A consumer cannot tell that these totals are incomplete. The process contract needs a way to preserve or summarize unreadable entries, or to mark the set incomplete with a reason, rather than turning failed reads into absence.

### 3. Nullable disk and stall figures conflate “not offered” with an individual read failure

**Severity: must fix**  
**Files:** `src/CcDirector.Core/Machine/DiskReadings.cs:42-86`; `src/CcDirector.Core/Machine/StallReading.cs:31-79`

These types define `null` as “not offered on this platform” and provide only one `CouldRead` flag for the whole group. That cannot represent a partial failure. For example, if Linux pressure information yields processor and memory figures but the disk file fails, the probe must either mark the whole stall reading unreadable and discard two valid figures, or return a readable stall with a null disk figure that falsely says the platform does not offer it. Disk kind, utilization, latency, queue depth, and rates have the same problem when their operating system sources do not all succeed together; `DiskKind.Unknown` also carries no reason.

The interface promises that one broken reading never hides the others, while the diagnosis depends on knowing whether expected evidence failed or was never available. The types need a per-figure availability state and failure reason, or smaller atomic readings whose availability is independent.

### 4. The reading invariants are optional because every positional constructor remains public

**Severity: must fix**  
**Files:** `src/CcDirector.Core/Machine/CpuReading.cs:16-34`; `src/CcDirector.Core/Machine/DiskReadings.cs:59-86`; `src/CcDirector.Core/Machine/StallReading.cs:45-79`

The factories reject a blank unreadable reason, but the public positional constructors bypass every factory. A platform probe can construct `new CpuReading(false, null, 0, 0, 0)`, or a readable stall with no offered figures, and all record structs also have a default value with `CouldRead` false, zero figures, and no reason. The tests exercise the factories only, so they do not prove the public contract they claim.

This is exactly the shape through which a later probe can turn “could not read” into unexplained zeroes. The type itself needs to make invalid combinations unrepresentable or validate every public construction path; documenting the preferred factory does not enforce the mission rule.

### 5. Caller-owned lists make cached samples mutable across concurrent consumers

**Severity: should fix**  
**Files:** `src/CcDirector.Core/Machine/DiskReadings.cs:123-130`; `src/CcDirector.Core/Machine/ProcessReadings.cs:47-54`; `src/CcDirector.Core/Machine/ResourceVerdicts.cs:49-76`; `src/CcDirector.Core/Machine/ResourceSample.cs:19-28`

Every list-bearing type stores the caller's `IReadOnlyList` reference directly. `IReadOnlyList` does not make the underlying object immutable: a probe can pass a `List`, reuse it for the next sample, and mutate the already cached sample while the interface, Gateway summary, and diagnosis enumerate it. This can produce inconsistent totals or an enumeration exception, and `ResourcePressureVerdict.Worst` can change after construction.

These values are snapshots shared by timer and interface consumers. Copying to an immutable snapshot at construction prevents a later platform implementation from accidentally making the one-second cache thread-unsafe.

### 6. The new sub-millisecond formatter still prints a measured non-zero value as zero

**Severity: should fix**  
**Files:** `src/CcDirector.Core/Machine/DiskReadings.cs:104-114`; `src/CcDirector.Core.UnitTests/Machine/ResourceReadingTests.cs:181-196`

`FormatMilliseconds(0.004)` returns `0.00`, and the new test requires that result. The method comment says the purpose is to avoid showing a made-up zero, but this case does exactly that for a four-microsecond measurement. The log and later detail screen cannot distinguish that reading from a measured zero.

Use enough precision or an explicit less-than representation for positive values below the display resolution. The test should prove that every positive input remains visibly positive rather than pinning the defect.

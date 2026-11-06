# Windows resource fixtures

Raw bytes the Windows resource probe reads from the operating system, captured on a real machine so
`WindowsResourceParsing` is tested from what Windows actually returns.

- Machine: SOREN_NORTH, Windows 11 Enterprise 10.0.26200, 24 logical processors, 4096-byte pages.
- Captured: 2026-10-15 13:26 UTC, by the capture tool in
  `missions/resource-monitor/phase-1/windows-capture/` on the kept branch
  `resource-monitor/windows-probe` (closed pull request #4020), which compiled the probe's own
  `WindowsResourceNative.cs` and `WindowsResourceParsing.cs` so the bytes came from the same calls.
- `capture-notes.txt` is what the tool printed at the moment of capture: the decoded figures each
  test expects. Its disk model and size lines come from two queries the probe no longer makes (the
  shared reading has no field for either); their bytes were not committed. Its free-space and network
  lines, and the network capture, were removed on 2026-11-06 when the mission dropped both readings.
  Volume C: sits on PhysicalDrive1 and volume D: on PhysicalDrive0.

| File | Call |
|---|---|
| `processor-performance-1.bin`, `-2.bin` | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`, one second apart |
| `process-information.bin` (+ `.address.txt`) | `NtQuerySystemInformation(SystemProcessInformation)`, while 48 threads spun on 24 processors |
| `page-file.bin` | `NtQuerySystemInformation(SystemPageFileInformation)` |
| `system-performance-1.bin`, `-2.bin` | `NtQuerySystemInformation(SystemPerformanceInformation)`, one second apart, captured at 13:43 UTC (the last lines of `capture-notes.txt`) |
| `disk<N>-performance-1.bin`, `-2.bin` | `IOCTL_DISK_PERFORMANCE` on `\\.\PhysicalDrive<N>`, one second apart |
| `disk<N>-seek-penalty.bin` | `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty)` |
| `volume-<L>-extents.bin` | `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` on `\.\<L>:` - which physical disks hold a volume, read for the system volume only |

## What was changed before committing, and why

This repository is public. The bytes are real except for these edits, each made by the capture tool:

- **Processes:** trimmed to operating-system processes (System, Registry, smss, csrss, wininit,
  services, lsass, svchost, fontdrvhost, Memory Compression) and the capture tool itself, so none of
  the owner's applications are named. Each kept entry was copied whole - the entry, its threads and
  its name - and its link to the next entry and its name pointer were rebased onto the original
  buffer address in `process-information.address.txt`.
- **Page file:** the page file's name (a path) is cleared. The probe never reads it.

## What these captures cannot prove

The milliseconds per read and per write divide DISK_PERFORMANCE's ReadTime and WriteTime by ten
million, taking them as 100-nanosecond units. That is right for QueryTime, and on this machine the
per-disk times agree with "Avg. Disk sec/Read" and "Avg. Disk sec/Write" - but this machine's
performance counter frequency is exactly 10,000,000, so the captures cannot tell 100-nanosecond units
from performance counter ticks. On a machine with another frequency (arm64 Windows commonly reports
19.2 MHz) the milliseconds would be wrong if the second reading is the true one. The busy share and the
queue depth are ratios and are right either way. Proving it needs a capture from such a machine,
compared against "Avg. Disk sec/Read" there, before a diagnosis quotes those milliseconds as settled.

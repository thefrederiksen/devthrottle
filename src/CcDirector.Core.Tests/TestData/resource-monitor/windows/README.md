# Windows resource fixtures

Raw bytes the Windows resource probe reads from the operating system, captured on a real machine so
`WindowsResourceParsing` is tested from what Windows actually returns.

- Machine: SOREN_NORTH, Windows 11 Enterprise 10.0.26200, 24 logical processors, 4096-byte pages.
- Captured: 2026-10-15 13:26 UTC, by the capture tool in
  `missions/resource-monitor/phase-1/windows-capture/`, which compiles the probe's own
  `WindowsResourceNative.cs` and `WindowsResourceParsing.cs` so the bytes come from the same calls.
- `capture-notes.txt` is what the tool printed at the moment of capture: the decoded figures each
  test expects.

| File | Call |
|---|---|
| `processor-performance-1.bin`, `-2.bin` | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`, one second apart |
| `process-information.bin` (+ `.address.txt`) | `NtQuerySystemInformation(SystemProcessInformation)`, while 48 threads spun on 24 processors |
| `page-file.bin` | `NtQuerySystemInformation(SystemPageFileInformation)` |
| `disk<N>-performance-1.bin`, `-2.bin` | `IOCTL_DISK_PERFORMANCE` on `\\.\PhysicalDrive<N>`, one second apart |
| `disk<N>-descriptor.bin` | `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceProperty)` |
| `disk<N>-seek-penalty.bin` | `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty)` |
| `disk<N>-geometry.bin` | `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX` |
| `volume-<L>-extents.bin` | `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` on `\\.\<L>:` |
| `interface-table.bin` | `GetIfTable2` |

## What was changed before committing, and why

This repository is public. The bytes are real except for these edits, each made by the capture tool:

- **Processes:** trimmed to operating-system processes (System, Registry, smss, csrss, wininit,
  services, lsass, svchost, fontdrvhost, Memory Compression) and the capture tool itself, so none of
  the owner's applications are named. Each kept entry was copied whole - the entry, its threads and
  its name - and its link to the next entry and its name pointer were rebased onto the original
  buffer address in `process-information.address.txt`.
- **Disks:** the serial number in each storage descriptor is overwritten with spaces. The probe never
  reads it.
- **Page file:** the page file's name (a path) is cleared. The probe never reads it.
- **Network:** trimmed to seven rows (the Ethernet adapter, its three filter rows, Wi-Fi, the loopback
  and one virtual switch). In every row the hardware addresses, the interface identifier and the
  network identifier are zeroed. The probe reads none of them.

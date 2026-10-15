# What these files are, and where they came from

Every file in this folder is REAL OUTPUT captured from a Mac, not written by hand. They exist so the
pure parsing functions in `MacOsResourceParsing` are covered on a Windows or Linux build machine as
well as on a Mac - the probe's system calls cannot run there, but the reading of their output is
where the mistakes actually live, and that part needs no Mac at all.

The machine: **devthrottle-mac-mini**, Apple M4, 10 cores, 16 GiB of memory, macOS 26.5.2
(build 25F84), arm64. Captured **2026-10-15** by the probe's own capture harness, which calls the
same `MacOsPlatformReader` the Director calls and writes what came back without touching it.

| File | What it is | Captured with |
|---|---|---|
| `iokit-bsd-name-disk0.plist` | The disk's device name, "disk0" | `IORegistryEntrySearchCFProperty`, searching the media below the driver |
| `iokit-device-characteristics-disk0.plist` | Medium type, product name, serial | the same, searching the device above the driver |
| `iokit-statistics-disk0.plist` | The cumulative read and write counters | the same, on the `IOBlockStorageDriver` entry itself |
| `sysctl-vm-swapusage.base64` | `struct xsw_usage` | `sysctlbyname("vm.swapusage")` |
| `sysctl-vm-loadavg.base64` | `struct loadavg` | `sysctlbyname("vm.loadavg")` |
| `getfsstat.base64` | Ten `struct statfs`, one per mounted volume | `getfsstat` with MNT_NOWAIT |
| `if-data-en1.base64` | `struct if_data` for the network interface carrying traffic | `getifaddrs`, the AF_LINK entry |
| `if-data-lo0.base64` | `struct if_data` for the loopback, which the probe skips | the same |
| `host-processor-info.txt` | Forty tick counters, four for each of ten cores | `host_processor_info` with PROCESSOR_CPU_LOAD_INFO |
| `proc-taskinfo.base64` | `struct proc_taskinfo` for the capture harness itself | `proc_pidinfo` with PROC_PIDTASKINFO |
| `proc-bsdinfo.base64` | `struct proc_bsdinfo` for the capture harness itself | `proc_pidinfo` with PROC_PIDTBSDINFO |
| `rusage-v2.base64` | `struct rusage_info_v2` for the capture harness itself | `proc_pid_rusage` with RUSAGE_INFO_V2 |
| `kern-proc-all.base64` | the first three rows of the kernel's whole process table, `struct kinfo_proc` | `sysctl` with CTL_KERN, KERN_PROC, KERN_PROC_ALL |

A `.base64` file is the raw bytes the kernel wrote, base64 encoded, with `#` comment lines at the top
saying what it is. The bytes are kept rather than the figures read out of them BECAUSE THE FIELD
OFFSETS ARE THE THING MOST LIKELY TO BE WRONG. Two of them were: `struct proc_bsdinfo` is 136 bytes
and not the 144 that counting the fields suggests, and at the wrong offsets the process name came
back shifted by a character and every start time read as 1970. Keeping the figures instead of the
bytes would have made that fixture agree with the bug.

**These are figures from one real machine at one real moment, so the tests assert those exact
values.** Recapturing on another machine means rewriting the expected values with it, which is the
point: the test proves the parser reads what this platform really produced.

## Nothing here is private

The captured structures hold counters, sizes, mount points and the capture harness's own process
name. The device serial number in `iokit-device-characteristics-disk0.plist` is the one piece that
identifies hardware; it is the owner's own development machine and this repository is public, so if
that ever matters the field is not read by the probe and can be blanked without affecting a test.

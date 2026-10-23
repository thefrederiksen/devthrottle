# Deviation from mission section 5b: the disk free gigabyte floors

**Accepted by the phase 1 Tech Lead (session eff068ba) on 2026-10-15.**

## What section 5b says

Disk free, per volume: amber below 15% **or** below 20 GB; red below 5% **or** below 5 GB.

## What was built instead

The same limits, except that **the gigabyte floors apply only to a volume of 20 GB or more**. A
smaller volume is judged by its percentage free alone. The constant is
`ResourcePressure.DiskFreeAbsoluteFloorsFromBytes` in `src/CcDirector.Core/Machine/ResourcePressure.cs`.

## Why

Read literally, the rule makes every small system partition red for ever. A volume smaller than
20 GB can never have 20 GB free, and one smaller than 5 GB can never have 5 GB free, so the floors
say nothing about it except that it is small.

The evidence is the phase 1 live reading on devlinux, 2026-10-15 at 13:56 UTC (full output in
`linux-verdicts-live-reading.txt`):

```
$ df -B1 / /boot/efi
Filesystem                            1B-blocks         Used     Available Use% Mounted on
/dev/mapper/ubuntu--vg-ubuntu--lv 3441928929280 152145297408 3148778184704   5% /
/dev/sdc1                            1124995072      6438912    1118556160   1% /boot/efi
```

Under the rule as worded, the verdict was:

```
DiskFree: Critical - /boot/efi with 1.0 GB free of 1.0 GB (99%)
```

The partition is 99% free. Almost every machine has a boot partition like this one, so the
disk-free warning would be red on all of them, all the time, and would never clear. That breaks the
goal in mission section 3 that bringing a resource back down clears the warning.

After the change, with the same figures:

```
DiskFree: Normal - the fullest volume is / with 2932.5 GB free of 3205.5 GB (91%)
```

A small volume that really is filling up is still caught by its percentage (a 1 GB volume with
10% free is amber, with 4% free is red). From 20 GB up, the gigabyte floors apply exactly as section
5b states. Both are pinned by `VolumeLevel_SmallVolume_IsJudgedByPercentageAlone` and
`VolumeLevel_FoldsByPercentOrGigabytes`.

## Still a proposal

Like every limit in section 5b, this is confirmed or changed by the cross-platform quality
assurance run.

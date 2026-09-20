# Phase 5 part one: decisions

From the Developer, 20 September 2026. Part one of `mandate-phase-5.md` only: the Launcher hosts the
background scan. Part two, rules as data, is not in this branch and nothing here anticipates it.

Branch `reclaim/phase5-launcher-background-scan`, cut from origin/main with phases 1, 2 and 4 on it.
Phase 3, removal, is not merged and none of it is used: there is no removal code anywhere this
branch can reach.

## What was built

In the engine, `src/CcDirector.Reclaim`:

- `Background/BackgroundScanJob` - one scan of one folder: take the guard, record running, walk,
  save the scan, make the recommendations, save those, record completed. Every way it can end is
  recorded.
- `Background/BackgroundScanGuard` - the guard against a second scan.
- `Background/BackgroundScanStatusStore` and `BackgroundScanStatus` - the record on disk, and the
  reading of it that decides what it means and writes the finished sentences.
- `Background/SavedRecommendationStore` - the recommendations as saved for a screen.
- `Indexing/WholeFileWriter` - write a file whole or not at all. `ScanIndexStore.Save` now uses it,
  so the command line tool's `scan` gets the same protection.
- `Rules/RecommendationRun` - the rule selection, fold and build that used to live in the command
  line tool's `Runner.Recommend`, moved into the engine so the tool and the background job are one
  code path. The tool now calls it.
- `DirectoryScanner.Scan(options, stop)` - the walk can be asked to stop.

In the Launcher, `src/CcDirector.Launcher`:

- `BackgroundDiskScan` - decides only WHEN: which folders, whether one is due, and the loop.
- `LauncherCore.RunBackgroundDiskScanAsync`, started beside the update loop in both the tray mode
  and the headless mode.

## Decisions, each with its reason

1. **The meaning of a record is decided in the engine, not the Launcher and not a screen.**
   `BackgroundScanStatusStore.Read` returns the state and the finished sentences. The Launcher asks
   it whether a folder is due; the Director in phase 6 prints its lines. Critical rule 7.

2. **Four states, not three.** The mandate names running, failed and never run. A scan that
   finished is the fourth, and it has to exist for the other three to mean anything. They are
   `never-run`, `running`, `completed`, `failed`.

3. **"Never run" is the absence of a record, and nothing else is.** A record that cannot be read is
   `failed`, because something was written there. This is the one place an absence is the answer,
   and it is safe because every other ending writes a record: a scan that throws, a broken
   measurement, a stop, all write `failed` with a reason.

4. **A record that says running is only believed while the process that wrote it is alive.** The
   record carries the process number AND when that process started, because the operating system
   reuses numbers. Running with a dead holder reads as `failed`, "cut short". Without this, a
   Launcher killed mid-scan would leave "running" on the screen for ever. A process that exists but
   will not say when it started is left as running, because it cannot be shown to be somebody else.

5. **Asked to stop is recorded as failed, with its own sentence**, not as a fifth state. The reader's
   question is "is there a result, is one coming, or did it not happen", and a stopped scan is the
   third. The job's own return value does tell them apart (`Stopped` against `Failed`) for the log.

6. **A later failure keeps pointing at the last whole result.** The record carries
   `lastCompletedUtc` and the two saved paths forward through running and failed, and the sentences
   say "the scan that finished at ... is still saved and is the newest whole one".

7. **The guard is a file held open and shared with nobody**, in the store folder. The operating
   system refuses the second opening in any process and releases it when the holder dies, so a
   crashed scan can never lock the machine out. A marker file written and deleted by hand would.
   It is one guard for the whole store, not one per folder: two disks walked at once is the thing
   being prevented.

8. **The command line tool's `scan` does NOT take the guard.** A person asking for a scan is not a
   second background scan, and refusing them because the Launcher happens to be walking would be a
   new way for the tool to fail. Two writers cannot damage a saved scan any more, because both
   write whole files. This is a judgement; the Reviewer may think otherwise.

9. **Whole-file writing is a temporary name in the same folder and a move.** The temporary name
   ends `.partial`, which the listing of saved scans does not match, so a leftover from an
   interruption is never listed and is overwritten by the next save of the same folder.

10. **The saved recommendations hold the finished sentences and the headline numbers, not the
    working.** The rules read the live machine, so the Director cannot remake them without looking,
    and it must not look. Phase 6 may need more fields; the file has a format version for that.

11. **Built around 1,406 seconds, as the mandate asks, and nothing was optimised.** What that number
    changed: a scan is daily (`RescanAfter` 24 hours) and looked at hourly; a failed scan waits 6
    hours rather than being thrown straight back at the disk; the first look waits 10 minutes after
    the Launcher starts; the walk runs on its own long-running thread and not the thread pool; the
    stop request is checked once a folder so a quitting Launcher is not held for 23 minutes; and
    being interrupted is treated as ordinary, because the Launcher's own update quits it. The
    scanner's walk itself is unchanged apart from that one check.

12. **Every fixed, ready disk is scanned, one after another.** The brief says D: matters as much as
    C:. Removable, network and optical drives are left alone.

13. **Installed mode only (`--managed`), like the update loop.** A Launcher started from a
    repository build or a test rig must never begin walking the whole disk.

14. **The job catches every exception, once, to record it.** `CLAUDE.md` rule 4 keeps try and catch
    at entry points. This is the engine's one exception to that and it is deliberate: a scan that
    threw and recorded nothing would leave "running" under a live process, which is exactly the
    silent failure the mandate forbids. It records, logs the whole exception, and returns `Failed`.
    It hides nothing.

15. **Which rules run on this machine is asked in two places now**: `MachineRules` in the tool and
    `BackgroundDiskScan.ForThisMachine` in the Launcher, each one line. The Launcher cannot
    reference the tool. Part two, rules as data, will own where rules come from and should make
    this one place; I did not invent a third project for one line ahead of it.

## No removal

The job and the Launcher class delete, move and change nothing outside the store folder. Two tests
hold a fixture tree byte for byte across a run. When phase 3 merges, removal code becomes reachable
from the Launcher's references for the first time; see the proof document for what that means.

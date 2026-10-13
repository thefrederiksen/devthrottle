# Factory Control, step 3 - shifts on the screens

Proof for the change that shows the shifts on the factory page and the schedule screen. Built on SOREN_NORTH on
11 October 2026 on top of a local merge of the run-results branch, then rebased onto origin/main on 12 October 2026
after run results merged (pull request 3799, commit 2a6df8062). The branch now carries only the shifts commits, and
the counts below are from the rebased branch.

## What changed

- **One calculation.** `src/CcDirector.Gateway/Running/ScheduleShifts.cs` reads a schedule's runs for the next
  7 days and asks `WorkShifts` which shift each run starts in. It never counts hours itself.
- **Nothing is stored.** Shifts are counted in the account's display time zone, which is the owner's default. There
  is no per-factory or per-schedule setting, and no table or column was added.
- **Every schedule read and write carries its shifts:**
  - which routes: `GET /cron/jobs`, `GET /cron/jobs/{id}`, and the create and update answers;
  - which fields: `shifts`, `shiftText`, `shiftWarning` and `shiftRule`.
  - Every run in `GET /cron/jobs/{id}/runs` carries `shiftText`, the shift it started in.
- **The warning rule.** It is stated in the code comment and on screen, under every warning: *a run belongs to the
  shift it starts in, and a run still going when that shift ends is a problem.*
  - With history: warn when the schedule's typical run would not end before its shift ends. The typical run is
    the middle length of its recent finished runs.
  - With no finished run yet: warn when the run starts in the last 30 minutes of a shift.
  - It is measured in real time, not wall-clock time. A night shift on a daylight-saving day is seven or nine
    hours long.
- **The Seats tab is grouped by shift:** night, morning, evening, then "No run in the next 7 days". The groups
  are folded on the Gateway (`FactoriesScreenFold.Seats`, `Groups`), and the Cockpit renders them as given.
  - **A seat in two shifts appears in each group**, and its row there lists only that shift's schedules. This is
    the plainer of the two options. A group answers "who works this shift", and only the seat's name repeats.
    Shown once with both shifts, the reader would have to work out which schedule belongs to which shift.
  - Every row also says all the shifts the seat works in.
- **The schedule screen:**
  - a Shift column, amber when the Gateway warned;
  - a Shift line and the warning with its rule in the detail panel;
  - a Shift column in the run table;
  - on a save, the shift warning beside the load warning.
- **Where the screens live.** The factory and schedule screens live in `apps/cockpit`. Their types and the one
  shared helper (`savedWarnings`) are in `packages/client-core`.

## Tests

| Where | What it shows |
| --- | --- |
| `Gateway.UnitTests/Running/ScheduleShiftsTests.cs` (12) | which shifts (two shifts, exactly 16:00); the warning when the typical run fits, ends exactly at the shift end, and does not fit; with no history, at 30 and 31 minutes before the end and early in the shift; Toronto spring-forward (8 March 2026: only that day's 01:00 run of 6h 30m is late) and fall-back (1 November 2026: 8h 30m from midnight fits the nine-hour night, the day before it does not) |
| `Gateway.UnitTests/Factory/FactoriesScreenFoldTests.cs` (4 new) | groups in the day's order with a "none" group; a seat in two shifts is in both, each with only its shift's schedules; a shift no seat works in has no group, and the boss leads; shifts follow the account's zone (03:00 UTC is night in UTC, evening in Toronto); a missing schedule has no shift |
| `Gateway.UnitTests/Running/ScheduleShiftsRoutesTests.cs` (4, real HTTP) | create, list and get carry the shift and the warning; an update into the morning drops it; a run carries the shift it started in; with no account zone the routes answer without shifts instead of guessing a zone |
| `apps/cockpit/src/factory/FactoriesScreen.test.tsx` | the shift headings in the Gateway's order, a seat in two groups with only its own schedules in each, and the shift warning in the "Saved, but:" banner |
| `apps/cockpit/src/schedule/ScheduleView.test.tsx` (2 new) | the Shift column, amber only when warned; the warning and its rule in the detail; a run's shift; no warning when none is given |
| `packages/client-core/src/schedule/savedWarnings.test.ts` (2) | both warnings verbatim, load first; null when neither |

## Commands and counts

Run on the rebased branch, 12 October 2026, one suite at a time.

| Command | Result |
| --- | --- |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 10,678 passed, 1 failed, of 10,679. The one is `HeldDeliveryDriverTests.TheTickDrivesANewlyHeldDelivery_WhileTheStartUpPassIsStillWorkingThroughItsOwn`, which this branch does not touch; its class run alone passes 29 of 29 |
| the same, filtered to the shifts, the factory fold and the four test files that gained the new argument | 145 passed |
| `npx vitest run` in `apps/cockpit` | 1,031 passed |
| `npx vitest run` in `packages/client-core` | 1,982 passed. An earlier run of the same tree had 1 failure that was not captured and did not come back in two further runs |
| `tsc --noEmit` in both | clean |
| `.\scripts	est-local.ps1` | 9 suites green, 1 red. The red is one test in `CcDirector.Core.UnitTests`, `GateTestsDoNotDependOnTheMachineTests`. It named two files: this branch's `ScheduleShiftsRoutesTests.cs`, which is fixed here (the engine's clock is a fixed time), and `CronRunResultTests.cs`, which arrived on main with pull request 3799 and is not changed by this branch. Run again after the fix, the test names only the second file |
| `dotnet test src/CcDirector.Gateway.Tests` | not run |

## What the rebase changed

- **The local merge of run results is gone.** No migration, snapshot or model file is in the diff against main.
- **The seat's closing marker and its shift sit side by side.** Main gained the marker that says whether a seat's
  sessions close themselves. The Seats tab's grouped rows keep it, and its Cockpit test reads it from the shift
  groups.
- **`ScheduleShiftsRoutesTests` no longer reads the real clock.** The engine's clock is fixed at 17:00 UTC, and the
  run test also checks that a run fired then is in the evening shift.

## Screenshots, from a local Gateway

`capture.py` is the script that took the screenshots, and it can be run again. It:

- starts a throwaway Gateway on port 7979 with its own data folder (`CC_DIRECTOR_ROOT`) and
  `CC_GATEWAY_NO_TAILSCALE=1`;
- registers a demonstration factory, "Shift Demo", with three seats and four schedules in America/Toronto;
- drives headless Edge with Playwright as a library, because this is a repeatable script, not interactive work.

It never touches the owner's Gateway, factories or schedules.

| File | What it shows |
| --- | --- |
| `seats-grouped-by-shift-desktop.png` | the Seats tab under Night, Morning and Evening. Scout runs at 02:00 and 18:00, so it is under both Night and Evening, each time with only that shift's schedule |
| `schedule-shift-column-desktop.png` | the Shift column. The Writer's 15:45 schedule is amber |
| `schedule-shift-warning-desktop.png` | that schedule's detail: "Starts close to the end of its shift: the run on Sun 11 Oct at 15:45 starts in the last 30 minutes of the morning shift, which ends at 16:00, and this schedule has no finished run to measure yet. 7 of its 7 runs in the next 7 days do this.", then the rule |
| `*-phone.png` | the same at 375 pixels wide. A shift heading reads as a heading, not a card |

Not proven here:

- **The warning with history, on screen.** The rig's schedules have never run, so the screenshot shows the
  no-history case. The case with history is proven by `ScheduleShiftsTests` and the route tests.
- **The run table's Shift column, on screen.** It has no rows in the rig, because no Director is connected. It is
  proven by the route and Cockpit tests.

## Revert proof

With the work committed, the rule "with history, the run ends at fire plus its typical length" was broken on purpose
(`r.Fire + t > r.Shift.EndUtc` became `r.Fire > r.Shift.EndUtc`) and `ScheduleShifts` tests run against a fresh build:
4 failed (the does-not-fit case, the long typical run, and both daylight-saving days). Restored, rebuilt and rerun:
81 of 81 `ScheduleShifts` and `FactoriesScreenFold` tests pass.

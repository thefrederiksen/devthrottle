# Factory Control, step 3 - shifts on the screens

Proof for the change that shows the shifts on the factory page and the schedule screen. Run on SOREN_NORTH,
11 October 2026, on branch `factory-control/shifts`. The branch has `origin/factory-control/runresult` (pull request
3799) merged locally, because the shifts come from its `WorkShifts`. It is rebased onto origin/main once 3799 merges.

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

| Command | Result |
| --- | --- |
| `dotnet test src/CcDirector.Gateway.UnitTests` | 10,493 passed, 4 failed. All four are migration-snapshot tests (`FleetOutcomeStopIdentityMigrationTests`, `FleetManagerLaterStepsMigrationChainTests`, both providers). They fail because 3799's migration is merged locally beside main's newer one. This branch changes no table, migration or model, and the four go away on the rebase |
| `npx vitest run` in `apps/cockpit` | 1,026 passed |
| `npx vitest run` in `packages/client-core` | 1,982 passed |
| `tsc --noEmit` in both | clean |
| `.\scripts\test-local.ps1` | 8 suites green, 2 red. The 2 reds are `BackgroundWorkRegisterTests` (`LinkRequestsCache.cs`) and `ShownErrorSourceScanTests`. Both arrived with origin/main at #3853 and were fixed on main by #3860, and neither touches this change |
| `dotnet test src/CcDirector.Gateway.Tests --filter CronJobEndpointsTests or CronRunEndpointsTests` | did not run: another session's release gate held the Gateway test lock. These two files only gained the new argument; they are run after the rebase |

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

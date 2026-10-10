# Proof - seat markers on the Factories screen (Factory Control, step 2, display half)

Every seat now says whether its sessions close themselves, and a factory that has a seat which does not is flagged on
the Factories list, on its card, and in its page header. The verdict and every word are folded on the Gateway and
rendered verbatim by the Cockpit.

## What the marker says

Folded by `CronRunEndingFold.SeatMarker` from how each of the seat's schedules' runs ended - the record the Gateway
has kept since 9 October:

- `closes itself - its last 6 runs` (green) - every counted run closed its own session.
- `stayed open 3 of its last 7 runs` (amber) - in 3 of them someone else had to end it (you, or another session), or
  it is still open more than an hour after it fired.
- `no runs recorded yet` (grey) - nothing to judge by. Not an accusation, not a pass.
- A seat with several schedules is judged across all of them, and the words add `, across its 2 schedules`.

**The window** is the seat's newest 10 runs across its schedules (the same depth as the Schedule page's record).
Only runs that say something are counted. That leaves out a run still under an hour old, a run that ended before
endings were recorded, a run that ended with its Director, a work-list drain, and a fire that started no session. A
fire that started no session left nothing open, and the factory's status already reports it.

**One ruling.** Each run is judged by `CronRunEndingFold.Judge`, which the Schedule page's per-schedule record
(`Summarize`) now uses too. The two can never judge the same run differently.

**The flag** names each seat that does not close itself by its role word, never a person's name: `Does not close
itself: Scout - stayed open 3 of its last 7 runs`, or `2 seats do not close themselves: ...`. It links to the Seats
tab. A seat with no record does not raise the flag.

## Screenshots

From a throwaway Gateway built from this branch (`rig/shoot.py`). Two factories:

- **Tallyhand (QA)**: the Boss closed itself 6 times out of 6. The Scout was stopped by you in 3 of its last 7 runs.
- **WarmForward (QA)**: the Boss closed itself 5 times out of 5. The Writer has no schedule.

| File | Shows |
|---|---|
| `1-factories-list-table.png` | Tallyhand flagged in amber under its status; WarmForward not flagged |
| `2-factories-list-cards.png` | The same flag on Tallyhand's card |
| `3-factory-page-header.png` | The flag in Tallyhand's page header |
| `4-tallyhand-seats.png` | Boss: "closes itself - its last 6 runs" (green). Scout: "stayed open 3 of its last 7 runs" (amber) |
| `5-warmforward-seats.png` | Boss closes itself. Writer: "no runs recorded yet" (grey) |
| `gateway-*.json` | The Gateway's own answers the screenshots were rendered from |

How the data was made:

- The factories went through the real registry route, and their schedules through the real schedule route, linked to
  their seats.
- The runs were written straight into the rig's `cron_runs` table, because the rig has no Director to start
  sessions. Each run carries an ending the Gateway records when a session closes itself (`closed-itself`) or when a
  person stops it (`stopped-by-you`).
- Everything after that is the Gateway's fold and the Cockpit's rendering.

**A rig artifact to ignore:** "Not run yet" in the Last run column. The rig wrote the runs, not the schedule engine,
so the schedules' own last-fired time was never set. A real fire sets both.

## Tests

- `CcDirector.Gateway.UnitTests`:
  - `FactorySeatMarkerTests` (13 tests). Closes itself; stayed open N of M; nothing recorded; runs that say nothing;
    a fire that started no session; several schedules; the 10-run window; the same ruling as the schedule record;
    the Seats tab marker and tone; a factory flagged by one bad seat; not flagged when every seat closes or has no
    record; two bad seats; the page header flag.
  - `CronRunEndingTests.Reader_RecentRunsOf_ReadsInTheAccountItIsGiven_WithEachEndingStamped`: the read takes the
    account the route resolved, not the ambient one, and another account sees none of the runs.
- `apps/cockpit` `FactoriesScreen.test.tsx`, "Seats that do not close themselves": the flag on the table row and
  the card, the click going to the Seats tab, the page header (and no header flag when none is sent), and each
  seat's marker with its tone.

## Not proved here

- The phone layout. At 390 pixels wide the Cockpit's side rail stays open and covers the cards, before and after
  this change, so a phone screenshot showed nothing useful. The mobile app has no Factories screen.
- The markers on real factories. They depend on the real runs since 9 October, and those are not read here.

## Test runs (10 October 2026, on this branch)

- `dotnet test src/CcDirector.Gateway.UnitTests` (the parked suite, run in full): 10,351 passed, 19 skipped, 0
  failed. An earlier full run had 1 failure. The run kept no result file, so the test cannot be named. The rerun with
  no code change was green, and every factory, run-ending and schedule test also passed in a focused run (208
  tests).
- `npx vitest run src/factory` in `apps/cockpit`: 116 passed, including the 5 new tests. `tsc --noEmit` is clean in
  `apps/cockpit` and `apps/mobile`.
- `.\scripts\test-local.ps1`:
  - Nine suites were Completed. `CcDirector.Core.UnitTests` passed 1,376 of 1,376 but went over the 120-second
    ceiling.
  - `cc-director-setup-engine.Tests` failed 1 of 875: `ToolReconcilerTests.ReconcileAsync_EmptyToolsState_ProvisionFails_ReturnsFailed`.
    That suite is installer code this change does not touch. The test class passed 3 runs out of 3 on its own.
- `CcDirector.Gateway.Tests` builds. It was not run, because `-Parked` needs Docker.

After rebasing onto `de46cb8e2`:

- The focused Gateway run (factory, run-ending and schedule tests) passed: 233 tests.
- The Cockpit factory tests passed: 116. `tsc` is clean.
- `cc-director-setup-engine.Tests` passed 875 of 875. Main's #3781 fixed the reconciler tests' shared helper, which is
  the likely cause of the earlier failure.

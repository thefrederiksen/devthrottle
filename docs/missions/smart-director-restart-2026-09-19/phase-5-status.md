# Phase 5 status - written as it happens by the Tech Lead (session 104, bd32dc97)

The Delivery Lead polls this file. Newest entry last. Phase 5 is the quality assurance run on the
isolated rig: mission document section 7 is the contract.

## 20 September 2026

### Seat taken

Read, in this order: the `mission` workflow conduct (version 29), the `devthrottle-method` skill,
`START-HERE.md`, `mission.md` in full with section 7 twice, `proof-phase-3-rig.md`,
`phase-3-status.md` in full, `phase-6-status.md`, the head of `proof-phase-6.md`,
`scripts/restart-qa-rig.ps1`, and the two existing scripts under `scripts/restart-qa/`.

My worktree `D:/ReposFred/devthrottle-smart-restart-p5` was detached at `origin/main` =
`d8fdaafed` and clean; `git fetch origin` says zero commits behind. I cut branch
`smart-restart/p5-record` in it for this record and nothing else. I never write code.

### The one thing I need from you, and it decides when the way up can be proved

**The Delivery Lead's own ruling is not on `main`.** `ruling-way-up-presence-check.md` does not exist
at `origin/main`; it and the code that answers it are uncommitted in
`D:/ReposFred/devthrottle-smart-restart-p3b` on branch `smart-restart/p3-offer-ruling`, where
Developer 91475f79 ("the way up offers an all-ended record") is still working. Twelve files are
modified there, six of them product code, including `DirectorWayUp.cs` and `WayUpOfferWindow`.

My mandate tells me to prove the way up "as it now stands, INCLUDING the ruling". The rig builds the
Director from a tree, so a build made today proves the way up WITHOUT the ruling. I am therefore
splitting the run rather than waiting idle:

- the WAY DOWN cases are proved now, at `d8fdaafed`, because the ruling touches only the start-up
  presence check;
- the WAY UP cases are proved on a rebuilt rig at whatever `origin/main` holds once 91475f79 merges.

Every case in the report will name the commit it was proved at. **If you would rather the whole run
wait for one commit, say so here and I will hold.** Phase 4's command line door is also still out
(Reviewer 99b92f63 is on it); section 7 does not require it, so it is not a blocker, but if it merges
before the way up rebuild I will fold `director smart-restart` into that rebuild and prove the second
door too.

### What I have to work with, so the plan is not a guess

- `scripts/restart-qa-rig.ps1` stands up a whole isolated world (own root under `%LOCALAPPDATA%`,
  own Gateway on 7911, own launcher, own Director) and refuses any root it cannot prove it owns.
  Phase 3 stood it up and took it down cleanly, so it is known to work on this machine.
- `scripts/ui-drive.ps1` drives the real Avalonia window through Windows UI Automation by
  `AutomationId` - no coordinates, no foreground. `scripts/capture-window.ps1` captures a window to
  PNG with `PrintWindow(PW_RENDERFULLCONTENT)`, which also does not need the foreground. Together
  these are how a real menu item gets clicked and a real dialog gets photographed. Neither knows
  about menus yet; extending them is a Developer's task, not a reason to hand-drive.
- The feature is all on main: the File menu item and the start-up call are at
  `MainWindow.axaml.cs:4611`, `:4615` and `:756`; the way down is
  `src/CcDirector.Avalonia/SmartRestart/` (16 files) over
  `src/CcDirector.ControlApi/SmartRestart/DirectorSmartShutdown.cs`; the way up is
  `DirectorWayUp.cs` with `WayUpOfferWindow`, `RestartHistoryWindow` and `WayUpStartUpAsk`.

### What earlier phases said they could not reach - these are what this phase is for

From `proof-phase-3-rig.md` and `phase-3-status.md`, carried here so they are not lost:

1. Nothing in this feature has ever run against a real Director, a real Gateway or a real launcher
   (phase 6 status says so too).
2. The fifteen lines in `MainWindow.axaml.cs` have no test.
3. No test opens the real main window.
4. **A lead with seats reporting to it was never timed.** The smart shutdown tells a lead to collect
   its subordinates' documents first and wait, a serial dependency none of phase 3's five timing runs
   had (22.5 to 57.9 seconds, against three minutes allowed). It is the shape most likely to exceed
   the limit and it is in section 7.
5. `Session.PendingInteraction` was empty until pull request 3215; whether "answer these first"
   appears on a REAL Director is untested.

### Known and not mine to fix - I show them, I do not work around them

Issues 3207 (the smart shutdown never tells a Pi session to hand over - `interrupt` answers Conflict
by design, `escape` works), 3209 (the drain accepts a handover at 500 bytes, one run in eight),
3210 (Claude Code's folder-trust dialog eats a restored session's first prompt and the session is
logged as exited cleanly), 3230 (a saved conversation can be reopened twice across a restart). If a
run shows one of them biting, that is evidence and it goes in the report as evidence.

### The plan

Sequential, because the rig is ONE machine-level resource - one root, one port, one pair of scheduled
tasks, one Director. Two seats driving it at once would corrupt each other's evidence, so there is
never more than one Developer on the rig at a time.

| Task | What | Where it ends |
|---|---|---|
| 1 | The harness and the way down: build the rig, stand it up, populate it with the session shapes section 3 names, extend `ui-drive.ps1` to reach a menu, and prove every way-down case in section 7 with screenshots | `proof-phase-5-way-down.md` + `attachments/phase-5/` |
| 2 | The way up, on a rig rebuilt at a `main` that holds the ruling: the start-up offer, restart history, and the three negative record cases | `proof-phase-5-way-up.md` |
| 3 | The report: `phase-5-qa-report.md`, every section 7 case with its evidence, what failed, what was not reached, and the commands to repeat it | merged |

Then a Reviewer on a different agent (Pi) reads the report against the attachments, looking for a
case the report claims but does not show.

### The ruling landed while I was writing the plan - answered, and the split stands

`c37d95abd` merged the ruling and its code to `main` at about 12:10 local time, so
`ruling-way-up-presence-check.md` and the widened presence check ARE on `origin/main` now. My
question above is answered by events and needs nothing from the Delivery Lead.

I did NOT pull the way-down Developer (81977424) back to the new head, and this is the reason, which
is checkable rather than a judgement: `git show --stat c37d95abd` touches fifteen files and **not one
of them is on the way down**. It changes `DirectorWayUp.cs`, `IDirectorWayUp.cs`, `WayUpWords.cs`,
`WayUpOfferViewModel`, `WayUpOfferWindow`, `RestartHistoryViewModel` and their tests, plus three
record documents. `DirectorSmartShutdown.cs`, `SmartShutdownDialog`, `ShutdownProgressView`,
`SmartShutdownSurface` and `MainWindow.axaml.cs` are untouched. So the way-down build at `d8fdaafed`
and a way-down build at the new head are the same software, and the report will say so with the
command that shows it:

    git diff --stat d8fdaafed <way-up commit> -- src/CcDirector.Avalonia/SmartRestart src/CcDirector.ControlApi/SmartRestart src/CcDirector.Avalonia/MainWindow.axaml.cs

The way-up seat builds its rig from the newer head. That also gives the run something real it would
otherwise have had to fake: the records on the rig are written by one Director build and read by a
NEWER one, which is exactly the case the mission exists for - a Director is updated and restarted.

### Where things stand

- Developer 81977424 ("the harness and the way down"), Claude Code, opened 11:54, worktree
  `D:/ReposFred/devthrottle-smart-restart-p5-rig`, branch `smart-restart/p5-way-down`, mandate
  `mandate-phase-5-developer-way-down.md`. Confirmed started by reading its own terminal, not by the
  spawn's exit code. Working.

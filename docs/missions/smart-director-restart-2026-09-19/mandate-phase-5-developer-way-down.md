# Mandate - Smart Director Restart, phase 5, Developer: the harness and the way down

You are a Developer. You were opened by the phase 5 Tech Lead (session 104, `bd32dc97`) and you report
to it, never to the owner and never to the fleet. You have no transcript; this file and the files it
names are your history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p5-rig`, branch `smart-restart/p5-way-down`,
cut from `origin/main` = `d8fdaafed`. Work only there. Never work in `D:/ReposFred/devthrottle`.

## THE RULE THAT KEEPS YOU ALIVE - read twice

On this Director a session that has ended its turn is never woken (product issue 3186). **NEVER end
your turn while you are waiting for something.** Wait IN THE FOREGROUND, inside your turn: one shell
command that loops until the thing you wait for exists, about a minute between looks, at most nine
minutes per command, run again until it is there. Wait on an ARTIFACT - a file on disk, a process
gone, a Gateway answer - never on a feeling. Never `run_in_background`.

Two more traps that cost other seats a whole seat:

- A safety hook stops any shell command that runs `rm` on a path built from a VARIABLE and asks the
  owner, which hangs you for good. **Literal paths only** in any delete.
- The account's weekly and monthly usage limits are nearly spent. If a screen warns a limit is close,
  write it into your proof file at once and tell the Tech Lead.

## Read first, in this order

All paths are absolute on purpose - your worktree does not hold the Tech Lead's notes.

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer". Laws 4, 5,
   6, 7, 14.
2. `D:/ReposFred/devthrottle-smart-restart-p5-rig/docs/missions/smart-director-restart-2026-09-19/mission.md`
   - ALL of it. Section 3 is the flow. **Section 7 is your contract.** Section 5.3 is what each
   screen is supposed to do.
3. `D:/ReposFred/devthrottle-smart-restart-p5/docs/missions/smart-director-restart-2026-09-19/phase-5-status.md`
   - the Tech Lead's live notes. Read it again before you open your pull request.
4. `.../proof-phase-3-rig.md` and `.../phase-3-status.md` in your own worktree - what the rig costs,
   how it was taken down, and the seven "things somebody should know". They will save you hours.
5. `D:/ReposFred/devthrottle-smart-restart-p5-rig/scripts/restart-qa-rig.ps1` - its header explains
   why a rig and not a slot, and what each verb does.

## The hard boundary - this is the one that cannot be bent

**Everything you do runs on the isolated rig.** Its own root
(`%LOCALAPPDATA%\cc-director-restart-qa-rig`), its own Gateway on loopback 7911, its own launcher,
its own Director. The machine's real Gateway (7878), its launcher, the installed Director and EVERY
live session of the fleet are never touched, never restarted, never drained, never asked anything.

Concretely, and these are the mistakes that would do real harm:

- Never `Stop-Process` anything you did not start, and never by name. The rig script finds its own
  processes by exact image-path prefix; copy that habit.
- Never point any script at `http://127.0.0.1:7878` or at the machine's `CC_GATEWAY_URL`.
- Never call `cc-devthrottle director smart-restart`, `director restart`, `director drain` or any
  shutdown verb against a real Director. There is no case in this mandate that needs it.
- Every session you create is created on the RIG Director, through the rig Gateway, and dies with the
  rig. Never `cc-devthrottle session spawn`: that opens a session on the REAL Director.

If you are ever unsure whether something is the rig or the machine, stop and ask the Tech Lead.

## What you are building

Two things, and the second is the point.

### 1. The harness, under `scripts/restart-qa/`

Small, re-runnable scripts, each doing ONE thing, each taking a Gateway address, a credential, a
machine name and a Director id - like the two that are already there (`before-picture.py`,
`session-key-probes.ps1`). They must work against production unchanged, because run two of this
feature is the owner's real Director, from a driver on another machine. **Nothing in the harness may
know the rig's root, its port or its task names.**

You will need, at least:

- **populate**: create the session shapes section 3 asks for, on a Director, and report what it made.
- **drive the window**: `scripts/ui-drive.ps1` already drives the real Avalonia window through Windows
  UI Automation by `AutomationId` (Avalonia exposes `x:Name` as `AutomationId`), with no coordinates
  and no foreground. It cannot open a MENU yet. Extend it - a menu item is reached by
  `ExpandCollapsePattern` on the File header then `InvokePattern` on the item - and add a way to read
  a window's whole automation tree to a text file, because that tree is evidence a reviewer can read
  when a picture is ambiguous.
- **capture**: `scripts/capture-window.ps1` already photographs a window by process id, including a
  modal by `-TitleContains`, using `PrintWindow(PW_RENDERFULLCONTENT)` - it does not need the
  foreground. Use it; do not write a second one.

The named controls you will be reaching for already exist, and this list is a fact of the code at
`d8fdaafed`, not a guess:

| Window | Named controls |
|---|---|
| `SmartShutdownDialog` | `TxtSessionCount`, `TxtWorkingWaiting`, `TxtExplanation`, `TxtWhy`, `CmbTimeAllowed`, `QuestionBoxPanel`, `QuestionBoxList`, `TxtQuestionBoxHeading`, `RefusalPanel`, `TxtRestartRefusal`, `TxtSmartShutdownRefusal`, `BtnSmart`, `BtnIgnore`, `BtnCancel` |
| `ShutdownProgressView` | `PhaseText`, `CountText`, `TimeLeftText`, `RowList`, `ResultText`, `NoteText`, `BtnShutDownNow`, `BtnCancelAndKeepWorking` |
| `SmartShutdownSurface` | `ProgressHost`, `TxtEnding`, `BtnBackToSessions`, `TxtBackHint` |

The File menu items are added in `src/CcDirector.Avalonia/MainWindow.axaml.cs` at lines 4611
("Smart Restart") and 4615 ("Restart history..."), and the start-up ask is the single call at line
756. **Those fifteen lines have never been executed by any test. Your run is the first thing that
ever runs them.** Say so in your proof, and say whether they worked.

### 2. The way-down cases of mission section 7, each with evidence

Every case below is either a screenshot of the real window, or an excerpt of the record the Gateway
holds, or both. A case is not proved by a script printing PASS: it is proved by something a person
can look at and disagree with.

| # | Case (mission section 7) | What "proved" looks like |
|---|---|---|
| 1 | The flow of section 3 from **File, Smart Restart** | dialog, progress screen part way, progress screen finished, the record on the rig Gateway, the Director gone and come back |
| 2 | The same flow from the **X** | the same dialog appears from the close hook; after the shutdown the Director exits and STAYS closed (mission 10.4) |
| 3 | **No sessions running: no dialog** | the Director closes with no dialog at all - and prove there were none |
| 4 | A session **mid-turn**: interrupted at two thirds, handover written; or ended at the limit and noted | the progress row, the handover file, the time it took |
| 5 | A session that **never answers**: ended at the limit, noted, with its conversation id in the record | the record's seat entry, `drainState = ended-at-limit` |
| 6 | A **lead with a session under it**: the lead covers it, leads come back first | the two handovers, and which one the lead's document mentions |
| 7 | A session with a **question box open**: shown in the dialog before anything is asked | `QuestionBoxPanel` visible with that session in it |
| 8 | A **wedged session that cannot take a prompt**: reported as such, not waited on in silence | the progress row's own words |
| 9 | **Shut down now** | pressed part way; everything ends at once |
| 10 | **Cancel and keep working** | pressed part way; afterwards the Director holds the SAME missions it started with |
| 11 | **Shut down and ignore all sessions** | everything ends at once AND the record is still written |
| 12 | **The Gateway unreachable when the dialog opens** | smart shutdown refuses with the reason in `TxtSmartShutdownRefusal`; the ignore-all choice still works |
| 13 | A **planted password in a handover is caught** | the sweep's own output, and what the stored handover holds instead |

Case 6 is the one the mission most needs and the one phase 3 could not reach: nobody has ever timed a
lead that must collect its subordinate's document FIRST. **Time it** - that serial dependency is the
shape most likely to blow the three minutes allowed after an interrupt. Phase 3's five runs without a
subordinate were 22.5, 23.9, 28.5, 43.8 and 57.9 seconds.

Cases 12 and 13 do not need the full six-session rig; run them in whatever order is cheapest.

The way UP - "A restart is available", restart history, a record for another Director not offered, a
record already brought back not offered again - is **NOT yours**. Another seat does it on a rebuilt
rig. Do not spend a minute on it. But do leave the rig's records in place and say what they hold, so
that seat has something real to come up on.

## Building the populate step - what the sessions must be

Section 3 asks for at least six sessions across at least two missions, one a lead with a session under
it, one mid-turn on a long turn, one idle and never answering. Add, for the cases above, one with a
question box open and one wedged.

Read these facts before you design it, because each one has already cost a seat a day:

- **`Session.PendingInteraction` is only ever filled for Claude Code**, from the transcript, by pull
  request 3215 (merged as `e3bdd050f`). Every other agent reads false. So the question-box session in
  case 7 must be a Claude Code session, and the way to get one is to have it ask a real question - not
  to fake the field.
- **A Pi session mid-turn is never told to hand over** (issue 3207): `POST /sessions/{sid}/interrupt`
  answers `Conflict` because `PiDriver` does not declare the Interrupt capability. `escape` works and
  produced a handover in 6.8 seconds. That is a known product defect. **Show it; do not fix it and do
  not work around it.** If you use a Pi session for case 4, you are showing 3207 biting, which is
  useful evidence - but then ALSO show case 4 with an agent that can be interrupted, so the report
  covers both.
- **Claude Code's folder-trust dialog eats a new session's first prompt** and the session then exits
  reported as "exited cleanly" (issue 3210). `--dangerously-skip-permissions` does not suppress it.
  Phase 3 got round it by marking the scratch folder trusted in `~/.claude.json` and removing the
  entry afterwards. If you do the same, **say so in your proof and undo it**, exactly as phase 3 did.
- **The drain accepts a handover at 500 bytes** (issue 3209), once in about eight runs, before the
  closing `drain-report` block is written. If you see it, that is evidence.
- Use cheap agents where the seat only has to exist. Only case 7 needs Claude Code.

## The check you owe

Before you open the pull request, run these yourself, in the foreground, and put the exact totals in
your proof - not "green", the numbers:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet test src/CcDirector.Avalonia.Tests

The baselines to beat, measured by the phase 3 Tech Lead on merged main `99cd1c034`: 606, 145, 776,
all with 0 failed. A filter that matches nothing exits GREEN, so report counts, never colours.

If you add or change anything under `scripts/`, it is code and it needs a test or, where a test
cannot reach it (a script that drives a real window), a recorded run with its output committed.

## What you owe, and where it goes

1. `docs/missions/smart-director-restart-2026-09-19/proof-phase-5-way-down.md` - every case above with
   its evidence, **what it proves in plain words**, what FAILED, what you could not reach and why, and
   the exact commands another person would run to repeat it. Name the commit every case was proved at.
2. Screenshots and automation-tree dumps under
   `docs/missions/smart-director-restart-2026-09-19/attachments/phase-5/`.
3. The harness under `scripts/restart-qa/`.
4. One pull request, opened and merged the day it opens, squash, branch deleted - the Tech Lead
   decides when it merges and sends it to a Reviewer first. Do not merge it yourself.

**Take the rig down when you are finished** (`scripts/restart-qa-rig.ps1 down`) and say in your proof
what `status` reported afterwards. Leave the rig ROOT in place: the way-up seat needs its records.

## The rules that bind you

- Never write a fallback. If something fails, find the cause and say what it is; do not add a second
  path that hides it.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, pull request, comment, document or code comment. ASCII only, everywhere - no emoji, no
  Unicode symbols, no arrows.
- Plain English, no abbreviations: "pull request", not "PR".
- Never deploy. Never touch production. Never destroy anything outside the rig root.
- Never run a sub-agent inside your session. Work is separate visible sessions and you open none.
- If something is genuinely undecidable inside this mandate, ask the Tech Lead with
  `cc-devthrottle session raise "<one line>"` and carry on with everything else. Do not guess and do
  not stop.
- Report when done with `cc-devthrottle session report "<one paragraph>"`.

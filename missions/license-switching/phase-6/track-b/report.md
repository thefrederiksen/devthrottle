# Phase 6, Track B - reporting and Director health: the track report

Track B made sure a machine is never silently lost from license switching: a report the Gateway refuses is
shown, a hand sign-in heals itself, a machine that cannot switch says so before a switch is sent, a dead
Director is noticed and restarted, and the active sign-in is kept alive when nothing else renews it.

Every piece below was built by its own Developer in its own worktree, gated by the Tech Lead on the exact head
that merged, read by a separate review session, and every finding answered by the Developer who built it. The
proof files and the reviews sit beside this report in this folder.

## What was built, and where it is proved

| Mandate item | Pull request | Merge commit | Proof | Review and answers |
|---|---|---|---|---|
| 1. A refused report is never silence; 4. "Not reporting since" | 4113 | f37f6ec52 | refused-and-silent.md | review-4113.md, review-4113-answers.md |
| 1 and 4. The license alarm for a refused report and a silent machine | 4172 | 8994f5e1a | refused-and-silent-alarms.md | review-4172.md, review-4172-answers.md |
| 1 and 4. Forget this machine, so a retired machine's alarm can close | 4183 | c1e0bd3ce | forget-machine.md | review-4183.md, review-4183-round-2.md, answers |
| 2. The Director heals a hand sign-in | 4106 | 6a5cd2022 | hand-sign-in-heal.md | review-4106.md, review-4106-answers.md |
| 3. Tool health | 4115 | 30556737f | tool-health.md | review-4115.md, review-4115-round-2.md, review-4115-rebase.md, review-4115-answers.md |
| 4. A dead Director comes back (Windows and macOS: the launcher) | 4118 | d2c3acc70 | launcher-crash-restart.md | review-4118.md, review-4118-answers.md |
| 4. A dead Director comes back (Linux: the owner's systemd unit) | 4117 | 9a5032dc2 | director-restart.md | review-4117.md, review-4117-round-2.md, review-4117-answers.md |
| 5. Renew the active sign-in | 4114 | a47954287 | active-renewal.md | review-4114.md, review-4114-answers.md |

### 1. A refused report becomes a machine status, with one log entry per change

The Gateway used to drop a report it could not take ("an account held twice", and thirteen other reasons) into its
own log file and nowhere else, so the machine just looked silent. Now the refusal is a machine status, "Report
refused", with one plain-English sentence per reason, and a count of reports refused since it began. One Licenses
log entry is written when the refusal starts, one if the reason changes, and one when a report is accepted again.
A refused machine is never sent a switch, and the switch says why.

The review found that the first version tied a refusal to the Director that sent it. On a machine where several
Directors run, the one holding the key changes, and the page would then have shown the machine twice, one row
refused for ever. The refusal now belongs to the machine and its operating system user, whichever Director sent
it. Three tests with two Directors on one machine prove it, and exactly those three fail under the old rule.

### 4a. "Not reporting since <time>", and one log entry when a machine goes silent

Past the six-minute stale limit the status names the time of the last accepted report. Past ten minutes (five
report intervals) the minute check that already runs writes one "Not reporting" log entry; the next accepted
report writes one "Reporting again" entry. No new timer.

### The alarm, through Track A's one mechanism

Two license alarm kinds, `report-refused` ("Report refused") and `machine-silent` ("Machine stopped reporting"),
each naming the machine, what is wrong and the one thing to do. Each is true exactly while its Licenses log row is
open, so it opens once and closes by itself; they are raised on the minute check with every alarm of the kind that
is true now. The review found two defects and both were fixed with tests that fail without the fix: a machine that
went silent and then came back with REFUSED reports kept both alarms, the silent one untrue (the silence row is now
ended when a refusal opens); and an over-long machine name could make the raise throw and take the other kind with
it (both sentences are length-guarded, and each kind is raised in its own try).

### Forget this machine

The same review showed there was no way to remove a machine, so a renamed or retired machine would have kept a red
"stopped reporting" banner on every page for ever. Decided by the Tech Lead: an owner-only "Forget this machine"
action on the Licenses page, never an automatic age-out (that would quietly hide a dead machine). It is offered
only for a machine that is refused or not reporting and has no sign-in under way. In one save it ends the
machine's open refused and silent rows (so its alarms close), ends a switch still recorded on it as failed with
the reason that it was forgotten, removes its holdings and its row, and writes one log row; approvers on that
machine and user are then removed. A machine that reports again simply reappears. The two review rounds found that
a machine that died mid-switch could not be forgotten, that its approver and that approver's alarm outlived it,
and a window in which a machine reporting again could be forgotten; each is fixed with a test.

### 2. The Director heals a hand sign-in

When the machine's active account is also held as a spare, the spare is stale by definition. The key store moves
that spare's folder into `hand-sign-in-backups/<account>-<time>` under its own lock (never deleting it), leaves it
out of the report, and the report says the machine was switched by hand once; the Gateway writes one log entry. A
heal that finds the lock taken does nothing and tries again on the next round. An operating system error while
moving (found by the review) is a store error, retried on the next round, never a half-move.

### 3. Tool health, and the cause of the missing tool

The cause was in our code. A tools update set the live Python tool environment aside for the whole pip install
(47 seconds on 16 October; the switch landed 41 seconds in), and the installer also deleted every tool command
from the bin folder before the install and wrote them back only after it - which is the window the Delivery Lead
saw live on 17 October, when the cc-* commands vanished for three minutes. Now the new environment is built beside
the live one and swapped in (measured 48 seconds down to 46 milliseconds on Windows), and nothing touches the bin
folder before or during the install. No retry was added.

The Director also checks, with the same lookup the switch uses, whether a switch can run, and says so in every
report. The Gateway shows "Cannot switch" with the reason, does not send a switch to that machine, writes one log
entry each way, and switches it on the next check once a report says the tool is back. A Director older than this
change sends nothing and is treated exactly as before.

### 4b. A dead Director comes back

Nothing in the product restarted a crashed Director on any platform. Now, on Windows and macOS, the installed
launcher starts the Director again after a crash, never after a quit: it tells the two apart by the crash journal
the Director deletes on every deliberate exit. A stop the launcher makes itself is recorded first, so it is not a
crash; it gives up after five restarts in ten minutes and says why in the log; it starts under the update swap
lock. On Linux, where there is no launcher, the owner sets a systemd user unit; `director-restart.md` gives the
exact unit and the steps.

One thing found while proving it: a look that catches the update lock at the instant it is being released is
answered "held off", so that restart waits for the next look, 15 seconds later. A test proves held-off looks never
count toward the five-restart limit, so it can delay a restart but never prevent one.

### 5. The active sign-in is kept alive

`license refresh` keeps spares fresh; nothing renewed the active key when no Claude Code session was running. The
Developer measured, from the installed Claude Code binary and in an isolated folder with no account, how Claude
Code renews its own key: only on the way to a call, under two lock folders, and it reads the key again under those
locks. The rule built from that: the Director renews the active key in place, under the store's lock AND Claude
Code's own two locks, when it has under an hour left and no Claude Code session is registered - or once it has
already ended, when no session can be using it. A key is never copied: it is read, renewed and written back where
it lives, atomically. It runs on the two-minute active read that already exists. A failed renewal is reported as
"needs a new sign-in", never silently.

## How it was checked

For every pull request, on the exact head that merged, the Tech Lead ran:

- `.\scripts\test-local.ps1` (the default suites);
- `.\scripts\test-local.ps1 -Parked -Filter License` with TEMP and TMP on the D: drive (the Delivery Lead's ruling
  for this phase; the unfiltered parked run on main is the Delivery Lead's, once every track has merged);
- the license Python tests with the package versions continuous integration pins, where the tools changed;
- the Cockpit tests, where the Cockpit changed.

What that turned up, and what was done:

- The six prompt-delivery tests red on the first base were broken on main and fixed there by 4107; a rebase
  cleared them.
- The Licenses fixtures test red on 4113's base was broken on main and fixed there by 4150; a rebase cleared it.
- The crash-loop test of 4118 failed in a full run and passed alone. It was not called flaky: the Developer found
  the race it was hitting, made the test hold a lock it controls, and proved the whole launcher suite five times
  out of five with every processor core busy.
- One Reclaim test fails whenever TEMP is on D:, because that volume keeps no short file names. It fails the same
  way on clean main; it passes with TEMP on C:.

- `BackgroundWorkRegisterTests` was red on main from pull request 4162 (not this track) for part of the day, and
  fixed there by 4175.

Things for the Delivery Lead's final run, none from this track:

- On several heads rebased onto main on 17 October, the default run stopped `Core.UnitTests` and
  `Avalonia.Tests` at the two-minute ceiling while other sessions' tests were running. Run alone they passed.
- `ShownErrorContextTests.Report_TheSentReportCarriesUserVisibleSurfaceAndActionAsFields_AndOnlyThatRowDoes`
  (Avalonia) failed once in a full run (expected 2 sent reports, got 3) on a head that touches nothing near it, and
  passed alone and in a full rerun: it counts reports a neighbouring test can add.
- The full cc-devthrottle Python suite does not collect on main: `tests/test_factory_export_import_ops.py` imports a
  module that does not exist.
- `cc-worktrees release` left two slots half-deleted after a squash-merge (issue 4119, both cases added there).

## What the live proof should look for

1. **Refused report.** An isolated test Director made to report one account twice: the Licenses page shows the
   machine "Report refused" with the sentence naming the account, the count rising every two minutes, ONE log
   row however long it runs, and Switch now on its account says it is not switched because its reports are being
   refused. A good report: "Reporting" at once, one "Reports accepted" row. Within a minute of the refusal, one
   "Report refused" license alarm (red banner, phone push, one owner email) naming the machine; it closes by itself
   within a minute of the good report.
2. **Silent machine.** Stop a test Director: "Not reporting since <time>" after 6 minutes, one "Not reporting" row
   and one "Machine stopped reporting" alarm after 10, and both end with one "Reporting again" row when it is
   started. For a machine that is not coming back, "Forget this machine" on the Licenses page closes its alarm and
   removes it from the page.
3. **Hand sign-in.** On a machine with switching on, sign an account in by hand that the machine holds as a spare:
   within 30 seconds the Director log says the hand sign-in was healed, the store has a new
   `hand-sign-in-backups/<account>-<time>` folder, the page log has one "switched by hand" entry, and the machine's
   reports are accepted.
4. **Tool health.** During a tools update, the machine never shows "Cannot switch", and a switch sent during it
   succeeds; the cc-* commands stay in the bin folder throughout.
5. **Dead Director.** On Windows or macOS, end the installed Director from the task manager: the launcher starts
   it again within about 15 seconds; quit it from its menu: it stays quit. On Linux, the same with the systemd
   unit from `director-restart.md`, once the owner has set it.
6. **Active sign-in.** On a machine with switching on and no Claude Code session running, through the hour before
   the active token ends: `license-store.log` shows `key-refreshed` for the active account and the page keeps the
   account readable. With a session working, no `key-refreshed` for the active account until the token has ended.

## What the owner must set

On Linux machines only: the systemd user unit in `director-restart.md`, which starts the Director at sign-in and
again after a crash, never while a Director of that user is already running. Windows and macOS need nothing: the
installed launcher does it.

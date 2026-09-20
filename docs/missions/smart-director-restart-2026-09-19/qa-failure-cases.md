# Quality report - Smart Director Restart, phase 5b: the failure cases on the current build

What this owed: the cases of mission document section 7 that a command line can reach, shown again on
what is on `main` now rather than on the build they passed on a week of fixes ago; the before-and-
after comparison on handovers; and what FAILED.

Written on 20 September 2026 by the Developer seat for phase 5b, branch `smart-restart/failure-cases`,
worktree `D:/ReposFred/devthrottle-smart-restart-cases`, cut from `origin/main` at `d1dc26286`.

**Nothing in this run drove a window.** No menu was opened, no button pressed, no window clicked,
photographed or activated. Everything below was done through `cc-devthrottle director smart-restart`,
`smart-restart-status` and `restart-history`, through the rig Gateway's own routes, and by reading the
Director's log. **One window did appear on the owner's screen and it was the product's own doing, not
mine** - section 7 says exactly what happened and what closed it.

Evidence is under `attachments/phase-5b/`, numbered in the order it was produced.

---

## 1. The headline

Three things were to be shown again, and two of them came out clean.

1. **Merge 3241 works, and this run says so more clearly than the first one did.** The stop verb is
   chosen from what each driver declares, the seat that cannot take a prompt is reported by name at
   both stages rather than waited on, and the run still reached **7 of 7 shut down**. See section 4.
2. **Merges 3245 and 3248 work.** All three reasons a record stops being offered - it has been used,
   the owner cleared it, it is more than seven days old - fire in the owner's own words, and **every
   one of those records is still in the restart history carrying its whole offer**. The owner's ruling
   that nothing is ever deleted holds. The Director was then restarted and offered nothing at all,
   with a sentence saying why. See section 5.
3. **The handover comparison does not show what the mandate expected, and the number in the mandate is
   not the number in the record.** See section 3. This is the weakest part of the report and it is
   weak for a reason I can name.

**Two things are wrong, and both are the product's:**

- **The smart restart reports `RestartRefused` on a restart that SUCCEEDED**, twice out of two, and
  the command exits non-zero for it. Section 6.1.
- **The restart history is labelled "newest first" and is not ordered by when the shutdown was.** A
  record from 12 September sits above one from 20 September in the owner's own output, and the offer
  walks that same order. Section 6.2.

---

## 2. What was run, and how to repeat it

### The build

**The rig's binaries were published from `8d0ba4a4f010506879c7497ec6f34e7168a2f3f8`, and this branch
is based at `d1dc26286`. I did not rebuild, and here is the check rather than the assurance.**
`attachments/phase-5b/03-rig-build-vs-main.txt` lists every file that differs between those two
commits. Four are under `src/`, and all four are Wingman judge-model files
(`HostedInferenceBrain.cs`, `GatewayTurnVerdictEnvironment.cs` and their two test files). Nothing in
the Director, the launcher, the Gateway's workspace store, the smart shutdown, the way up or the
command line differs at all. The same file records that the three merges this phase exists to measure -
`71ef69ec2` (3241), `986557607` (3245), `2d4d90a38` (3248) - are each an ancestor of the rig build.

**Say the limit plainly:** a reviewer who wants "built from `d1dc26286`" has not got it. What is
offered instead is the list of every byte that could differ, which is checkable in one command.

### The baseline the Delivery Lead measured

| | Delivery Lead, on main | This branch, at `d1dc26286` | |
|---|---|---|---|
| `CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 637 passing | **686 passing, 0 failed** | `01-engine-tests.txt` |
| `CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 159 passing | **159 passing, 0 failed** | `02-window-tests.txt` |

The window count matches exactly. The engine count is **49 higher** than the Delivery Lead's figure.
I did not chase where the 49 came from; the honest statement is that the number moved up, nothing is
failing, and a baseline quoted as 637 will not match a run made today.

### The rig

| | |
|---|---|
| rig root | `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig` |
| Gateway | `http://127.0.0.1:7911` |
| token file | `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig\config\director\gateway-token.txt` |
| machine | `SOREN_NORTH` |
| Director id | `3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe` |
| Director process before the run | 56400 |
| Director process after the first restart | 45536 |
| Director process after the second restart | **57016 - this is the one running now** |
| Director log | `<rig root>\instances\default\logs\director\director-2026-09-20-<pid>.log` |

**The command line does not exist on this machine's PATH.** The `cc-devthrottle` on PATH is the
installed 2.8.1 tool and has no `smart-restart`, `smart-restart-status` or `restart-history`. Phase 4
wrote those and they have not shipped. So the tree's own source was run instead, and phase 5b commits
the wrapper that does it rather than leaving it in a transcript:

```
scripts\restart-qa\rig-cli.ps1 director restart-history --director <id> --count 20 --json
```

It junctions this worktree's `tools\cc-devthrottle\src` under the package name, points the environment
at the RIG Gateway and the RIG token, and **prints the module file that actually answered** so that
the tree's source having run, rather than an installed copy, is checked and not assumed. Every
transcript in the attachments opens with that line.

### The order of the run

```
scripts\restart-qa-rig.ps1 status                          -> 04-rig-status.txt
scripts\restart-qa\claude-folder-trust.ps1 grant           -> 07-claude-folder-trust.txt
scripts\restart-qa\populate-sessions.ps1  (7 seats)        -> 08-populate.txt, 08-seats.json
scripts\restart-qa\prompt-sessions.ps1    (5 prompts)      -> 09-prompt-*.json
scripts\restart-qa\rig-cli.ps1 director smart-restart --minutes 5
                                                           -> 11-smart-restart-transcript.txt
scripts\restart-qa\read-record.ps1                         -> 13-record.txt, 13-record.json
scripts\restart-qa\count-handovers.ps1                     -> 14-handover-comparison.txt
scripts\restart-qa\offer-cases.ps1 -Age -Clear -Use        -> 16-offer-cases-applied.txt
scripts\restart-qa\rig-cli.ps1 director restart-history    -> 17, 18, 24, 25
scripts\restart-qa\offer-cases.ps1 -Clear <the last record>-> in 16-offer-cases-applied.txt
scripts\restart-qa\rig-cli.ps1 director smart-restart      -> 21-smart-restart-no-sessions.txt
```

Three scripts are new and are committed with this report: `rig-cli.ps1` above; `count-handovers.ps1`,
which counts the one number the mission asks to be compared and prints the row it counted each seat
from; and `offer-cases.ps1`, which makes the three reasons a record stops being offered. Everything
else was already on `main` from phase 5 run A. Nothing that drives a window by clicking was copied.

---

## 3. The handover comparison, and why it is the weakest thing here

**The mandate said the first run measured six of seven sessions ending without a handover. The record
of that run says three of seven.** I counted it off the record itself rather than off either report,
with a script that prints every row it counted, so a reader can disagree with a row:
`14-handover-comparison.txt`.

| | first attempt `restart-20260920-1230` | run A `restart-20260920-1619` | **this run `restart-20260920-1733`** |
|---|---|---|---|
| build | `d8fdaafed`, BEFORE merge 3241 | `8d0ba4a4f`, after | `8d0ba4a4f`, after |
| seats | 7 | 8 | 7 |
| wrote a handover document | 3 | 3 | 3 |
| covered by a lead's document | 1 | 0 | 0 |
| **ended without a handover** | **3 of 7** | **5 of 8** | **4 of 7** |

A counting trap worth naming, because it is how two numbers for one record can both look right: a
seat covered by its lead carries the LEAD'S handover path on the record. Asking "has it a path" before
asking "is it covered" counts somebody else's document as its own. The script asks in the right order
and says so in its own comment; my first version of it did not, and reported 4 wrote and 0 covered for
the first attempt instead of 3 and 1.

Seat by seat, this run against the first attempt:

| Seat | Agent | first attempt | this run | |
|---|---|---|---|---|
| Alpha lead | Pi | `drained`, own document | `ended-at-limit`, none | worse |
| Alpha worker under the lead | Pi | `covered` by the lead's document | `ended-at-limit`, none | worse |
| Alpha long turn | ClaudeCode | `drained`, own document | `drained`, own document | same |
| Beta long turn | Pi | `ended-at-limit`, none | `drained`, own document | better |
| Beta question box | ClaudeCode | `drained`, own document | `drained`, own document | same |
| Beta silent | RawCli | `ended-at-limit`, none | `ended-at-limit`, none | same, by design |
| Beta wedged | RawCli | `ended-at-limit`, none | `ended-at-limit`, none | same, by design |

**So: one better, two worse, four unchanged, and one more seat ending without a handover than before
merge 3241.** That is what the records say and it is what I am reporting.

**It is a weak comparison, and these are the differences I know about, so a reader can judge it rather
than take it:**

1. **The two seats that got worse are the two Pi seats on the Alpha mission, and both were still
   working when the limit arrived.** They were interrupted at two thirds and given one minute forty to
   write. Neither did. In the first attempt the lead finished before the interrupt was needed. That is
   a difference in how long a Pi seat took, not a difference the merge caused.
2. **The interval between two thirds and the limit is too short at five minutes to test the thing it
   is for.** The Architect's untested guess is that an interrupted session needs about three minutes
   to write a handover. Five minutes allowed leaves one minute forty. Run A said this and it is still
   true: **only a ten minute run can test it**, and I did not run one. That is the single most
   valuable thing left undone here.
3. **A count of handovers is the wrong instrument for merge 3241, and I should have said so before
   running rather than after.** 3241 is about a seat that cannot take a prompt being REPORTED instead
   of waited on. Both builds end the wedged seat at the limit with no document either way, so the
   count cannot move for it. What 3241 changed is in the transcript, not in the count - section 4.
4. **Machine load was not measured on any of the three runs**, and all three ran on the owner's
   working machine beside his real fleet.
5. **Agent versions were not pinned or recorded on any of the three runs.** Pi and Claude Code could
   have changed underneath between 12:30 and 17:33 and nothing here would show it.
6. Time allowed was five minutes on all three. That one is not a difference.

**What I would conclude, narrowly:** nothing in these three records shows merge 3241 changing how many
seats write a handover, in either direction, and the spread between 3, 4 and 5 is well inside what two
Pi seats finishing or not finishing can account for.

One thing this run DID start better than run A: all seven seats were present and four were Working
when the run began (`10-sessions-before-the-run.txt`). Run A's comparison was spoiled by a worker that
removed itself four seconds before the start. That defect in the comparison is gone; the result did
not improve with it.

---

## 4. Merge 3241 - the right stop verb, and one wedged seat that no longer ends the run

### The stop verb comes from what the driver declares

`15-director-log-lines.txt`, from the Director's own log. Three seats were still working at two thirds
and each was read before it was stopped:

```
17:37:07.599 [DrainSessionControl] StopTurnAsync: session=b0204e64-..., agent=Pi, declares=ClearContext, Cancel, ContextUsage, ModelReport, CompactContext
17:37:07.605 [DrainSessionControl] StopTurnAsync: session=b0204e64-... stopped with escape
17:37:10.224 [DrainSessionControl] StopTurnAsync: session=1a3e267d-..., agent=Pi, declares=ClearContext, Cancel, ContextUsage, ModelReport, CompactContext
17:37:10.224 [DrainSessionControl] StopTurnAsync: session=1a3e267d-... stopped with escape
17:37:13.022 [DrainSessionControl] StopTurnAsync: session=f3281235-..., agent=Pi, declares=ClearContext, Cancel, ContextUsage, ModelReport, CompactContext
17:37:13.022 [DrainSessionControl] StopTurnAsync: session=f3281235-... stopped with escape
```

and the drain recorded that each stop LANDED, then asked each one to hand over:

```
17:37:07.606 [DirectorDrain] stop the turn at two thirds: b0204e64-... : verb=Escape, landed=True
17:37:10.218 [DirectorDrain] hand over now to b0204e64-... : delivered=True
```

**What this proves and what it does not.** It proves the verb is read from the driver's declaration
and that a Pi seat is now told to stop and then asked to hand over - the thing 3241 was for, since a
Pi session used to be never told to hand over at all. It does NOT prove the choice DIFFERS per driver,
because all three seats still working at two thirds were Pi. No Claude Code seat was still working at
two thirds in this run, so no second declaration was exercised. A run that pins one Claude Code seat
past two thirds would close that.

### The wedged seat is reported, by name, twice, and the run goes on

`beta-wedged` is a program that never echoes what is typed. It was reported in its own row with the
reason, at both stages, and never waited on in silence (`11-smart-restart-transcript.txt`):

```
Restart QA Beta - Worker - wedged and cannot take a prompt: The request did not reach it
  The request did not reach it: [RawCli] EchoVerifiedSubmit: the composer never echoed the typed
  text after 2 attempts - the TUI is not accepting input ...
```

and again at two thirds, by name: `The request to hand over now did not reach it: ...`.

**And the rest of the run happened.** The wedged seat was reported at 17:34:06. The four seats after it
in the order were still asked, three of them handed over, and the run reached
`Every session is shut down - restarting the Director - 7 of 7 shut down` at 17:38:57:

```
17:38:57.941 [DirectorDrain] emptied=True, left=0
17:38:57.980 [DirectorDrain] finished: seats=7, closed=7, ready=False, reason=4 seat(s) did not hand
             over cleanly, 4 of them ended by the smart shutdown when the time allowed was up ...
```

The same shape was seen before the run, when the harness probed the seat: the Gateway answered
`accepted: false` with the same sentence rather than hanging (`09-prompt-wedged-probe.json`).

**That is the whole of what 3241 was for, and it is the clearest result in this report.**

---

## 5. Merges 3245 and 3248 - what is offered, once, and never deleted

### How the three cases were made

Two of the four rules cannot be reached by waiting: a record eight days old, and a record the owner has
cleared. `scripts\restart-qa\offer-cases.ps1` reaches them, on records that really happened:

- **too old** - `restart-20260920-1648`, its shutdown time moved eight days back.
- **cleared** - `restart-20260920-1230`, given the mark the "Don't ask again" button writes.
- **used** - `restart-20260920-1619`, given the mark that bringing ONE seat back writes.
- **the control** - `restart-20260920-1733`, this run's own record, untouched at first.

**A captured record cannot be forged, and that is why the cases were made on records that exist.** My
first attempt planted three copies of one record and the Gateway refused every one: *"A captured
workspace can only be created by capturing a Director."* The store also restores a stored record's
machine, Director, start time and clearing over any write. What an update may change is the shutdown
time, which is the one field the age rule reads. The refusal, and the code that explains it, are in
`16-plant-attempt-refused.txt`.

### What the history then said

`18-history-readable.txt` and `25-history-final-summary.txt`, the Director's own words:

| Record | Holds | What the history says |
|---|---|---|
| `restart-20260920-1733` (control, before it was cleared) | 2 sessions waiting | offered at start-up - no sentence |
| `restart-20260920-1619` (used) | 2 sessions waiting | "This restart has been used - a session has been brought back or reopened from it - so the Director no longer offers it when it starts. **Nothing has been deleted**: whatever it still holds can be brought back from here." |
| `restart-20260920-1230` (cleared) | One session waiting | "You asked on 20 September 2026 at 17:44 not to be offered this when the Director starts, so it is not offered any more. **Nothing was deleted**: everything it holds can still be brought back from here." |
| `restart-20260920-1648` (aged 8 days) | One session waiting | "More than 7 days old, so the Director no longer offers it when it starts. **Nothing has been deleted**: what it holds can still be brought back from here." |

**Every one of the three is still in the history with its whole offer, seat by seat**, and the record
that was used names the seat and what became of it:

```
Restart QA Alpha - Lead - collects its worker's document first:
    Its saved conversation was reopened on 20 September 2026 at 17:44 as adb00211.
Restart QA Alpha - Worker - a long turn that can be interrupted:
    Handed over and is waiting to be brought back.
```

**Read this correctly, because it is easy to read wrongly.** A record with NOTHING left to act on also
carries no "not offered" sentence, and that is by design - the sentence exists only to explain a record
that still holds something. Six of the ten records are in that state. An empty `notOfferedAtStartUp`
field therefore does NOT mean "offered"; the rule is "offered when it holds something AND carries no
sentence". `25-history-final-summary.txt` states the rule at the top and applies it per record. My
first rendering of that file got this wrong and called six silent records "still offered"; the file in
the attachments is the corrected one, and the mistake is recorded here rather than tidied away.

### The offer at start-up, which is the surface that matters

Two Director start-ups were captured, and the difference between them is the whole of 3248
(`23-the-way-up.txt`).

**Process 45536, 17:39, after the seven-seat run - the record was still on offer:**

```
17:39:13.086 [DirectorWayUp] FindOfferAsync: offering restart-20260920-1733-sorennorth, owed=2, rows=6
17:39:13.093 [WayUpOfferViewModel] Created: workspace=restart-20260920-1733-..., owed=2,
             endedWithoutHandover=4, bringBackRows=2, endedRows=4, endedSectionOpen=False
17:39:13.173 [WayUpOfferWindow] ShowForAnswerAsync: workspace=restart-20260920-1733-sorennorth
```

**Six rows: two that can be brought back, four that ended without a handover.** That is the offer
listing only what can be acted on - the record holds seven seats, and the seat whose own handover said
"not coming back" is not in it.

**Process 57016, 17:46, after the control had been cleared too - nothing was shown at all:**

```
17:46:18.391 [DirectorWayUp] FindOfferAsync: nothing waiting (10 record(s) read)
17:46:18.394 [WayUpStartUpAsk] Nothing shown: state=NothingWaiting, message=There is nothing waiting
   to come back. No smart shutdown of this Director in the last seven days is still on offer: each one
   has been used already, has been cleared, or has no session left to bring back and no saved
   conversation left to reopen. They are all still in File, Restart history.
```

**Ten records read, none offered, and a sentence naming all three reasons and saying where they still
are.** No window was shown. That is the clearing working end to end at the only surface it exists for,
and it is also what proves the three treated records are genuinely skipped rather than merely labelled.

**What is NOT proved here.** I never pressed the "Don't ask again" button - I wrote the mark that
button writes, through the Gateway route the button's own code calls
(`POST /gateway/workspaces/{id}/restore/marks`, kind `cleared`). That the BUTTON calls that route is
covered by `DirectorWayUpOfferOnceTests`, `SmartRestartClearMarkStoreTests` and `WayUpOfferWindowTests`
in the merged suites, not by this run. The same applies to the offer window's appearance: the view
model logged six rows and the window logged six rows, and what those rows look like on a screen is
untested by me.

---

## 6. What failed

### 6.1 `RestartRefused` on a restart that succeeded - twice out of two

Both smart restarts in this phase ended with the engine reporting a refusal, and **both Directors
restarted anyway**:

```
17:38:58.555 [SmartShutdownRun] AskLauncherAsync FAILED: launcherWasAsked=True:
             System.Threading.Tasks.TaskCanceledException: The operation was canceled.
17:38:58.556 [SmartShutdownRun] finished: outcome=RestartRefused, workspace=restart-20260920-1733-...
17:46:02.820 [SmartShutdownRun] AskLauncherAsync FAILED: launcherWasAsked=True: ... canceled.
17:46:02.837 [SmartShutdownRun] finished: outcome=RestartRefused, workspace=restart-20260920-1746-...
```

The Director that asked the first time was process 56400; process 45536 was up 3 seconds later. The
Director that asked the second time was 45536; process 57016 was up 16 seconds later. Both are in
`12-rig-status-after-the-run.txt` and `22-rig-status-final.txt`, each naming the new process id and its
start time.

**The mechanism, read rather than guessed:** `SmartShutdownRun.AskLauncherAsync` awaits the launcher's
answer to `POST /machines/{machine}/director/restart`. The launcher's way of answering is to stop the
very Director that asked, so the awaited call is cancelled along with the process.
`launcherWasAsked=True` records that the request went out. The engine folds that into the outcome
`RestartRefused`.

**Why this matters rather than being cosmetic:** `RestartRefused` is a word an agent and a screen both
act on, and the command exits 3 for it. The detail sentence beneath it is honest - *"If the launcher
did receive the request, this Director is stopped and started again shortly; if it did not, restart it
by hand"* - but the outcome above says the opposite of what happened, and an unattended caller reads
the outcome. A run that succeeds cannot be told apart from one that did not, from the exit code alone.

**This is NOT run A's finding, and run A's finding did not recur.** Run A saw the launcher answer
HTTP 409 - *"it is holding 3 live sessions"* - because the crash journal was 250 milliseconds stale, on
three of its four runs. Neither of my two runs saw a 409. So that race can evidently go the other way;
**two runs do not make it fixed**, and nothing in the three commits between run A's build and this
branch's base touches it.

### 6.2 The restart history is not ordered by when the shutdown was

The history's own message says **"newest first"**, every row shows the SHUTDOWN time, and the order is
neither. From `25-history-final-summary.txt`, in the order the engine returned them:

```
restart-20260920-1733   shut down 2026-09-20 17:38
restart-20260920-1619   shut down 2026-09-20 16:24
restart-20260920-1230   shut down 2026-09-20 12:36
restart-20260920-1648   shut down 2026-09-12 16:49     <- eight days older, fifth in the list
restart-20260920-1636   shut down 2026-09-20 16:41     <- four hours NEWER, sixth
```

The cause is in `DirectorWayUp.CandidatesAsync`: the candidates are
`.OrderByDescending(w => w.UpdatedUtc).ThenByDescending(w => w.CreatedUtc)` - the order is by when the
record was last WRITTEN, so writing any mark on a record moves it to the top.

**The consequence is not only cosmetic.** `FindOfferAsync` walks that same order and offers **the first
record that qualifies**. So touching an older record - a restore mark, a reopen, a clearing - can put
it ahead of a newer one in the queue to be offered. In this run it did not bite, because at each
start-up the newest record was the only one that qualified. Whether the order ought to be by shutdown
time is a design call and not mine; that the label, the rows and the order disagree is a defect either
way.

### 6.3 A smaller one: the refusal sentence promises an offer that cannot happen

The second run had no sessions, so its record holds nothing. The refusal detail still ended with *"it
is offered when the Director is next started"*. It is not, and cannot be: a record with nothing left to
act on fails `HasSomethingToActOn` and is silently never offered. The next start-up proved it -
`nothing waiting (10 record(s) read)`, with `restart-20260920-1746` among the ten.

### 6.4 The secret sweep found nothing, which proves nothing

This run's sweep reported `swept=3, proved=10/10, findings=0` (`13-record.txt`). Ten patterns proving
themselves on their own controls says the instrument worked. **Zero findings says nothing at all about
whether a planted credential would be caught**, because no credential was planted - I left that seat
out to keep the seven-seat shape comparable with the first attempt. The positive proof of that case is
run A's run D, where the sweep caught `assigned-password` in a handover, and nothing here changes it.

---

## 7. The window that appeared on the owner's screen

**At 17:39:13 the rig Director came back from its restart and put the restart offer on the desktop.**
That was the product doing what this mission built it to do. I did not open it, click it, photograph it
or activate it.

It went away because the second smart restart replaced that Director with one that had nothing to
offer, and that Director showed no window. **The desktop is clear.** The rig is still running - Gateway
48408, launcher 52176, Director 57016 - and if the owner wants it gone it is one command:

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 down
```

**This is worth a ruling from the owner before the next run.** Any smart restart that actually restarts
will raise that window, because raising it is the feature. A run that must not touch his screen
therefore cannot restart a Director that has anything to offer - unless the rig Director can be started
in a way that keeps it off the desktop, which nothing in the rig does today.

---

## 8. What I did not reach

Named so that none of it is mistaken for covered.

| Case from section 7 of the mission document | Where it stands |
|---|---|
| The flow from the File menu AND from the X | **Not mine and not done here.** The command line reaches the same engine (`[DirectorSmartShutdown] Start: purpose=Restart` in the log at 17:33:40), and nothing above that engine was touched. Covered separately and by the owner. |
| No sessions running: no dialog | **Partly.** A smart restart was run with zero sessions and completed (`21-smart-restart-no-sessions.txt`); whether the DIALOG is suppressed is a window question a command line cannot ask. |
| A session mid-turn: interrupted at two thirds, handover written | **Half.** The interrupt is proved three times over. **No interrupted seat wrote a handover in this run** - all three were Pi seats with one minute forty left. Needs a TEN minute run. |
| A session that never answers: ended at the limit, offered with its saved conversation | **Proved.** `beta-silent` ended at the limit at 17:38:57 and the offer carries it as reopenable. |
| A lead with a session under it; leads come back first | **Not evidenced.** The lead was ended at the limit and covered nobody, so there is no `covered` row and no lead in the restore order. The first attempt's record DOES show coverage working on the older build, so this is untested, not broken. |
| A session with a question box open, shown in the dialog before anything is asked | **Partly.** The seat was asked, handed over and left its answer on the record. The DIALOG's pre-flight report of open question boxes has no command line door. |
| A wedged session reported, not waited on | **Proved.** Section 4. |
| Shut down now / Cancel and keep working / Shut down and ignore all | **Not done.** All three are buttons. |
| The Gateway unreachable when the dialog opens | **Not done.** |
| A record for ANOTHER Director on the same machine is not offered | **Not done.** The rig has one Director. |
| A record already brought back is not offered again | **Proved.** Section 5, the `used` case, plus the start-up that then offered nothing. |
| A planted password in a handover is caught | **Not re-run.** Section 6.4. Run A proved it and nothing since touches the sweep. |

**One thing was left changed on the rig, deliberately, so a reviewer can read the same history I read:**
`restart-20260920-1648` still carries a shutdown time of 12 September. To put it back:

```
scripts\restart-qa\offer-cases.ps1 -GatewayUrl http://127.0.0.1:7911 `
  -TokenFile <the rig token file> -DirectorId 3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe `
  -Restore restart-20260920-1648-sorennorth -RestoreTo 2026-09-20T20:49:22.1558494Z
```

The three clearings and the one reopen cannot be undone through the API, by design - the store restores
those marks over any write. They are rig records and nothing outside the rig reads them.

---

## 9. How to disagree with this report

Every number above came from a file in `attachments/phase-5b/`, and the two that matter most can be
recomputed without me:

- **the handover counts**: `scripts\restart-qa\count-handovers.ps1` against the three workspace ids in
  section 3. It prints every seat row it counted, so a wrong count shows up as a wrong row.
- **what is offered and what is not**: `scripts\restart-qa\rig-cli.ps1 director restart-history
  --director 3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe --count 20 --json` against the running rig, read with
  the rule stated at the top of `25-history-final-summary.txt`.

The Director's log lines are quoted verbatim, with their source file and process named at the top of
`15-director-log-lines.txt` and `23-the-way-up.txt`. The three handover documents this run produced are
in `attachments/phase-5b/handovers/`, byte for byte as the product wrote them.

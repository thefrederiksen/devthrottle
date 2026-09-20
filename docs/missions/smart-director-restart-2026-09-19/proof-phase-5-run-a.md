# Proof - Smart Director Restart, phase 5 run A: the rig, the harness and the main run

What this owed the Tech Lead: the cases of mission section 7 that a command line can reach, each
named and evidenced; the before-and-after comparison on handovers; the Director's own log lines; the
planted credential caught by the sweep; and what FAILED.

Written on 20 September 2026 by the Developer seat for phase 5 run A, branch `smart-restart/p5-run-a`,
worktree `D:/ReposFred/devthrottle-p5-run-a`, cut from `origin/main` at `8d0ba4a4f`.

**Nothing in this run touched a screen.** No window was clicked, activated, photographed or driven.
Every fact below comes from the command line, from the rig Gateway's own routes, or from the
Director's log file. That was the one rule of this phase and it held.

Evidence is under `attachments/phase-5-run-a/`, numbered in the order it was produced.

**One evidence file is not ASCII, deliberately.** The handover document
`handovers-main-run/0ab6ab99 - ... refuses an interrupt.md` carries 35 non-ASCII bytes, because the Pi
agent that wrote it used them. It is kept byte-for-byte as the product produced it: it is the
artifact, and an artifact that has been tidied is no longer evidence of what happened. Everything
this branch WROTE is ASCII; five transcripts had a byte-order mark added by the terminal capture and
that was removed.

---

## 1. The headline

The flow works, end to end, from the command line, on a build made from this tree. Eight sessions
across two missions were asked to hand over, three wrote documents, the two stages fired, the
Director emptied, and the record on the Gateway says what became of every seat.

Three things are wrong, and two of them are the product's:

1. **The launcher refused to restart a Director it had just emptied, three times out of four.** The
   smart shutdown works and the restart then does not happen. This is the most important finding in
   this document and it is section 6.
2. **The rig writes its handover documents into the machine's REAL vault**, not the rig root, so the
   rig is not as isolated as its own header says. Section 7.
3. The planted-credential case took four runs to produce, and the first three failures were MINE,
   not the product's. Section 5 says exactly what was wrong each time, because "the sweep found
   nothing" and "the seat was never asked" look identical in a record.

---

## 2. What was run, and how to repeat it

The rig was rebuilt from this tree, stood up, and driven entirely through `cc-devthrottle`.

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 build -Force
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 up
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart-qa-rig.ps1 status
```

All three binaries were published from **`8d0ba4a4f010506879c7497ec6f34e7168a2f3f8`**, which is this
tree's base and is after all four merges this phase exists to measure. The build printed the commit
itself; it is in `01-rig-status.txt`. The stale build the Tech Lead warned about
(`%TEMP%\restart-qa-rig-builds`, published from `d8fdaafed`) was replaced by this.

**The rig, for whoever runs next:**

| | |
|---|---|
| rig root | `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig` |
| Gateway | `http://127.0.0.1:7911` |
| token file | `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig\config\director\gateway-token.txt` |
| machine | `SOREN_NORTH` |
| Director id | `3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe` |
| binaries from | `8d0ba4a4f010506879c7497ec6f34e7168a2f3f8` |

**The command line does not exist on this machine's PATH.** `cc-devthrottle` on PATH is the INSTALLED
2.8.1 tool and it has no `smart-restart`, `smart-restart-status` or `restart-history` - phase 4 built
those and they have not shipped. Asking the installed tool for one answers `No such command
'restart-history'`. This tree's tool was run instead, through the installed virtual environment
(Python 3.12.14, click 8.5.0, typer 0.27.2 - above the tool's declared floor, so the click 8.1
problem phase 4 recorded did not arise):

```
mklink /J <scratch>\pkgroot\cc_devthrottle D:\ReposFred\devthrottle-p5-run-a\tools\cc-devthrottle\src
set PYTHONPATH=<scratch>\pkgroot
set CC_GATEWAY_URL=http://127.0.0.1:7911
set CC_GATEWAY_SESSION_KEY=<contents of the rig token file>
"%LOCALAPPDATA%\cc-director\pyenv\Scripts\python.exe" -m cc_devthrottle.cli director smart-restart ^
    --director 3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe --minutes 5 --reason "phase 5 main run"
```

That the tree's source was what ran, and not the installed package, was checked rather than assumed:
the module printed its own file, which resolved through the junction into this worktree.

**Every call carried the rig Gateway and an explicit `--director`**, and the value was printed and
compared against the recorded rig Director id immediately before each start. Those lines are at the
top of every transcript.

Four smart shutdowns were run. The seats for each were created with `populate-sessions.ps1` and their
work started with `prompt-sessions.ps1`.

| Run | Seats | Reason | Outcome | Record |
|---|---|---|---|---|
| A - the main run | 8 | `phase 5 main run` | emptied; the launcher refused the restart | `restart-20260920-1619-sorennorth` |
| B | 1 | the planted credential | emptied; refused. The seat never got the request | `restart-20260920-1629-sorennorth` |
| C | 1 | the planted credential | emptied; refused. The seat got the request and could not read it | `restart-20260920-1636-sorennorth` |
| D | 1 | the planted credential | emptied; **restarted**; the sweep CAUGHT the credential | `restart-20260920-1648-sorennorth` |

---

## 3. The cases, from the main run

The eight seats, their shapes, and what the record says of each, are in `09-record.txt` and
`09-record.json`. The run's own sentences - the progress screen's words, rendered to a terminal - are
in `06-smart-restart-transcript.txt`.

### The flow of section 3, from the File menu's own action

**The command line starts the same engine the File menu does.** `director smart-restart` reaches the
host verb `smart-restart/start`, which calls `ISmartShutdown.Start` - the one the menu item calls.
The Director's own log names that entry point and nothing else:

```
2026-09-20 16:19:11.307 [DirectorSmartShutdown] CheckAsync: purpose=Restart
2026-09-20 16:19:11.318 [DirectorSmartShutdown] Start: purpose=Restart, minutes=5
2026-09-20 16:19:11.321 [SmartShutdownRun] RunAsync: purpose=Restart, minutes=5, reason=phase 5 main run
```

**Said equally plainly: the menu item itself is not what I pressed.** Nothing in this run exercised
the File menu, the dialog, the progress screen's buttons or the close hook. What is proved is the
engine and everything below it, reached through a second door. The screens above it are unproven by
this run and phase 2's headless window tests are all that stands behind them.

### A session mid-turn: interrupted at two thirds, then a handover

Two seats were mid-turn on a long task when the run began. Neither needed the interrupt - both
finished and wrote a document inside the first two minutes:

```
2026-09-20 16:20:05.827 [DirectorDrain] handover read: 87bae9f0-... -> drained
2026-09-20 16:20:55.905 [DirectorDrain] closed (verified absent): 87bae9f0-... (a long turn that can be interrupted)
2026-09-20 16:21:55.944 [DirectorDrain] closed (verified absent): 0ab6ab99-... (a long turn on an agent that refuses an interrupt)
```

Their documents are in `handovers-main-run/`, 5,838 and 4,894 bytes, each with a real next action:

> restore why: The task is 52 of 400 lines done and needs a session to finish the remaining 348
> appends; nothing else can carry it.

The seat that WAS interrupted at two thirds was the Pi lead. It was interrupted and asked again, and
then **did not write a handover and was ended at the limit**. So of the two halves of this case, the
interrupt is proved and the handover-after-interrupt is NOT: see section 4.

### A session that never answers: ended at the limit, noted, with its conversation id

`beta-silent` is a program that hears the request, answers loudly and writes nothing. It was asked,
asked again at two thirds, and ended at the limit:

```
2026-09-20 16:24:22.748 [DirectorDrain] ended at the limit: c9972c1a-... (takes the request and never answers): ended=True, gone=False
```

The record carries it as `ended-at-limit` with restore `undecided`. **Its saved conversation id is
NOT on the record** - the `handover / conversation` column is empty for it in `09-record.txt`. It is
a `RawCli` seat and there is no conversation to save, which is correct and is also why this seat
cannot evidence the "offered with its saved conversation" half of the case. The seats that DO carry a
conversation id on this record are the Pi and Claude Code ones, and the Pi lead carries one while
ended at the limit: `66b3f777 ... conversation: b3bc07b8-72d2-4b10-84e7-7bf93e588ae6`. That is the
case, on a different seat than the mandate named.

### A lead with a session under it

**This case did not arise, and the reason is not the product's.** The worker under the lead asked to
be deleted four seconds BEFORE the run started:

```
2026-09-20 16:19:07.561 [SessionCommandExecutor] DispatchAsync: verb=request-deletion, sid=d8388560-..., source=UserInput
2026-09-20 16:19:07.562 [Session] MarkForDeletion: session=d8388560-... reason=(none)
2026-09-20 16:19:43.369 [CcDirector] Reaping session d8388560-... flagged for deletion (no reason).
```

The run began at 16:19:11. So the seat the lead was supposed to cover was already on its way out; the
record calls it `unreachable` with the row detail "it went away by itself and wrote no handover". The
lead was then ended at the limit without writing anything, so it covered nobody.

The FIRST attempt's record does show coverage working on the older build (`covered`, with the lead's
document named for both seats), so the mechanism is not being reported broken - it is being reported
untested by this run.

### Leads come first in the record's `restoreAfterRestart` order

**Not evidenced, and it cannot be from this run.** The record's `restoreAfterRestart` holds two
sessions, `87bae9f0` then `1cbfa646`, and **both are workers** - the only lead in the run was ended at
the limit and was never decided `restore`. An order of two workers says nothing about leads coming
first. This needs a run in which a lead writes a handover and asks to come back.

### A session that cannot take a prompt - the wedged seat

**This is the case that came out best, and it is merge 3241's.** `beta-wedged` is a process that never
echoes what is typed. It was reported as unreachable in its own row, with the reason, at both stages -
never waited on in silence:

```
Restart QA Beta - Worker - wedged and cannot take a prompt: The request did not reach it
  The request did not reach it: [RawCli] EchoVerifiedSubmit: the composer never echoed the typed
  text after 2 attempts - the TUI is not accepting input ...
```

and at two thirds, again by name, `The request to hand over now did not reach it: ...`.

**And the other sessions were still asked and the run still finished.** The wedged seat was reported
at 16:19:45; the four seats after it in the order were still asked, three of them handed over, and the
run reached `Finished - 8 of 8 shut down` at 16:24:23. That is the whole of what 3241 was for.

The same shape was seen a second time, before the run, when the harness probed the seat: the Gateway
answered `accepted: false` with the same sentence rather than hanging (`04-prompt-wedged-probe.json`).

### The two stages and the limit, with the times they fired

The run started 16:19:11.32 with five minutes allowed. Two thirds is 3 minutes 20 seconds, so the
nominal points are 16:22:31 and 16:24:11.

```
2026-09-20 16:22:35.996 [DirectorDrain] stop the turn at two thirds: 66b3f777-... : verb=Escape, landed=True
2026-09-20 16:22:38.422 [DirectorDrain] hand over now to 66b3f777-... : delivered=True
2026-09-20 16:24:20.740 [DirectorDrain] the limit: the time allowed is up
2026-09-20 16:24:22.420 [DirectorDrain] ended at the limit: 66b3f777-... : ended=True, gone=False
2026-09-20 16:24:23.040 [DirectorDrain] finished: seats=8, closed=8, ready=False, reason=5 seat(s) did not hand over cleanly, 4 of them ended by the smart shutdown when the time allowed was up ...
```

Both fired, both a little late - the interrupt 4.7 seconds past its point and the limit 9.4 seconds
past its - which is the ten-second poll and not a fault.

**One thing the Tech Lead should notice about the times.** The Architect's untested guess is that an
interrupted session needs about three minutes to write a handover. At five minutes allowed, two thirds
leaves it **one minute forty**, and the lead here had one minute forty-four and wrote nothing. A five
minute run therefore cannot test that guess at all. Only the ten minute default leaves three minutes
twenty. If phase 3's measurement is meant to be confirmed on the rig, it has to be a ten minute run.

### A session with a question box open

Out of scope for a command line and not claimed. The dialog's pre-flight check
(`ISmartShutdown.CheckAsync`'s report of open question boxes) has no host verb, as the Tech Lead's
status file already records. What this run CAN say is that the seat which had a question box open was
asked, was asked again at two thirds, handed over, and left its question on the record:

```
owner questions gathered by the run:
  from ... a question box open for the owner: Delphine is the agreed name for this scratch folder. What do you want done in it?
```

---

## 4. The before-and-after comparison on handovers

The first attempt's record is `restart-20260920-1230-sorennorth`, taken on a build based at
`d8fdaafed`, which is BEFORE merge 3241. It is preserved at
`D:/ReposFred/devthrottle-smart-restart-p5-rig/docs/.../attachments/phase-5/case-1-record.txt` and is
also still readable on the rig Gateway. Mine is `restart-20260920-1619-sorennorth`.

| Seat | Agent | First attempt (`d8fdaafed`) | This run (`8d0ba4a4f`) | |
|---|---|---|---|---|
| Alpha lead | Pi | `drained`, own document | `ended-at-limit`, none | worse |
| Alpha worker under the lead | Pi | `covered` by the lead's document | `unreachable`, none | worse |
| Alpha long turn | ClaudeCode | `drained`, own document | `drained`, own document | same |
| Beta long turn | Pi | `ended-at-limit`, none | `drained`, own document | better |
| Beta question box | ClaudeCode | `drained`, own document | `drained`, own document | same |
| Beta silent | RawCli | `ended-at-limit`, none | `ended-at-limit`, none | same, by design |
| Beta wedged | RawCli | `ended-at-limit`, none | `ended-at-limit`, none | same, by design |
| Beta planted credential | Pi then RawCli | not in this record | `ended-at-limit`, none | not comparable |

**Counted the way the mandate asked - seats that ended without a handover:**

- first attempt: **3 of 7**
- this run, on the seven seats common to both: **4 of 7**
- this run, all eight seats: **5 of 8**

**The picture is not better. It is worse by one seat**, and I am reporting that rather than the one
seat that improved. One better, two worse, four unchanged.

**And the comparison is weak. These are the differences I know about, so a reader can judge it rather
than take it:**

1. **The two seats that got worse were not in the same state at the start.** The alpha worker in the
   first attempt "had never been given a task" and "never woke from its snooze" - the record says so
   in its own words. In mine it had been given work, was Working, and then removed itself four
   seconds before the run. The alpha lead in the first attempt had a worker that answered; in mine it
   had a worker that had vanished, which is the thing it was told to wait for. Neither seat was
   comparing like with like.
2. **A different door.** The first attempt started the shutdown by clicking the File menu; mine used
   the command line. The engine is the same one, but the runs are not identical in how they began.
3. **Eight seats against seven.** Mine adds the planted-credential seat.
4. **Machine load was not measured on either run**, and mine ran on the owner's working machine
   alongside his real fleet. I cannot rule load out and I did not measure it.
5. **Agent versions were not pinned or recorded on either run.** Pi and Claude Code could have
   changed underneath between 12:30 and 16:19 and nothing here would show it.
6. Time allowed was the same, five minutes, on both. That one is not a difference.

**What I would conclude, narrowly:** this run does not show merge 3241 improving how many seats write
a handover, and it was never going to - 3241 is about a seat that cannot take a prompt being REPORTED
rather than waited on, and both runs report the wedged seat without a handover either way. What 3241
changed is visible in the transcript, not in the count: the run kept going and finished. A count of
handovers is the wrong instrument for that merge, and I should have said so before running it rather
than after.

---

## 5. The planted credential, by a route that cannot decline

The first attempt failed because a Pi seat was told to carry a credential into its handover and
declined. This run replaced that seat with a **stand-in program** - a `RawCli` seat whose command is
a PowerShell script seeded from `session-shapes.json`, the way `silent-seat.ps1` already was. A
program cannot decline. The seat definition and the script are in
`scripts/restart-qa/session-shapes.json`.

**It caught it.** Record `restart-20260920-1648-sorennorth`, from `16-run-d-record.txt`:

```
the secret sweep:
  documents swept    : 1
  patterns proved    : 10 of 10
  findings           : 1
    - assigned-password in 2bbdf72f - Restart QA Beta - Worker - writes a handover carrying a planted credential.md (line 9): password: [REDACTED 9 chars] 19 chars]
  ready to restart   : False
  not ready because  : 1 possible secret(s) are in the handover documents; they are on this machine's disk and the record is on the Gateway. Clear them first.
```

Ten patterns proved on their own controls AND one real finding, so the instrument was working and
found something - which is the pair of facts a clean sweep can never give you. The document as it
ended up is `17-planted-credential-handover.md`; line 9 is `password: Rig7Staging9Quetzal`, a string
invented for this rig that belongs to nothing. The seat's own log,
`17-planted-credential-seat-log.txt`, records it writing 1,164 bytes to the exact path the Director
named.

**It took four runs, and three of the failures were mine.** Recording them because each one produced a
record that reads exactly like a clean sweep, and a reader who saw only the first would have drawn the
wrong conclusion:

1. **Run A.** The stand-in printed two lines when asked. The submit verifier calls 2,048 bytes of
   output "a turn that started", so the Director reported `the prompt is parked in the composer
   unsubmitted` and the request never landed. The sweep swept 3 documents and found 0. **The seat had
   never been asked.** The silent seat beside it prints 60 lines for exactly this reason and its
   comment says so; I did not read it closely enough.
2. **Run B.** The seat now answered loudly and the request was `delivered=True` - and it still wrote
   nothing. Past a certain length **the Director does not type the request at all**: it writes it to
   `<seat folder>\.temp\input_<stamp>.txt` and types `@<that file>` into the composer, which a real
   agent expands. My stand-in received 39 characters of file reference and no handover path. This is
   not a defect - it is how long prompts are delivered - but it is a trap for any stand-in seat and it
   is worth knowing about.
3. **Run C.** The stand-in now read that file, and still wrote nothing. The fault was a lost backslash:
   the pattern is written in a JSON string, decoded into a PowerShell single-quoted string, and one
   layer ate one backslash, so `[A-Za-z]:\\[^"]+?\.md` reached the seat as `[A-Za-z]:\[^"]+?\.md` -
   an escaped bracket, matching nothing. It now uses a pattern with **no backslash in it at all**,
   which cannot be damaged by any of those layers.
4. **Run D.** Proved offline first - the script was run against the real parked request file from run
   B and shown to write a 1,164 byte document containing the credential - and only then run on the
   rig. That is the run above.

**A note the Tech Lead should weigh, not a defect.** The sweep set `readyToRestart: false` and said
"Clear them first", and **the Director restarted anyway** - the launcher took the restart 60 ms later
and the Director came back as process 67748. That is deliberate and the code says so: the smart
shutdown gates its restart on `Emptied`, and `DirectorDrainResult` comments that `ReadyToRestart` "is
the answer" only on the older drain path, "which never ends anything". So a handover carrying a
credential is RECORDED and does not stop a smart restart. Whether that is what the owner wants is his
call, not a bug I found.

---

## 6. THE LAUNCHER REFUSED TO RESTART A DIRECTOR IT HAD JUST EMPTIED

**Three of the four runs emptied the Director completely and then failed to restart it.** The command
reported it in the engine's own words:

```
Error: RestartRefused Director 3be6c633-...: The Director was emptied, but the launcher did not
accept the guarded restart (HTTP 409): ... "refusing to restart the Director 3be6c633-... (pid 41076)
on SOREN_NORTH: it is holding 3 live sessions. Drain it first - every session writes a handover and
is closed - then ask again."
```

It is a race, and the timings name it exactly. Evidence in `19-launcher-refused-the-restart.txt`, all
quoted from the Director's log:

```
16:24:22.672 [MainWindow] PersistSessionStateCore          <- the last save before the ask; 3 seats were still present at this instant
16:24:22.748 ... ended at the limit: c9972c1a-...
16:24:22.872 ... ended at the limit: 5ca23bbe-...
16:24:22.984 ... ended at the limit: 5a5731d8-...
16:24:23.001 [DirectorDrain] emptied=True, left=0
16:24:23.156 [GatewayClient] RequestOwnRestartOnlyIfEmptyAsync: POST .../director/restart onlyIfEmpty=true
16:24:23.250 [MainWindow] PersistSessionStateCore          <- the empty roster is written HERE, 94 ms too late
16:24:23.251 [SessionStateStore] Save: saved 0 session(s)
16:24:23.253 [GatewayClient] RequestOwnRestartOnlyIfEmptyAsync: HTTP 409
```

**The mechanism, read in the code rather than guessed:**

- `DirectorSupervisor.RefuseRestartUnlessEmpty` asks `DirectorInstanceLocator.ReadSessionCount`.
- That reads the **crash journal on disk**, `<instance home>\config\director\crash-journal\<id>.json`.
  Its own summary says the file "is rewritten on every change to the session set, atomically", so
  "while the Director is alive the file is exactly as current as the roster itself".
- It is not. The only production writer of that roster is `MainWindow.PersistSessionStateCore`, and
  `MainWindow.PersistSessionState` **debounces it by 250 milliseconds**
  (`PersistDebounceMs = 250`).
- The smart shutdown ends every remaining seat at the limit in one burst and asks the launcher to
  restart inside that 250 ms window. The guard reads the previous snapshot and refuses.

The number in the refusal is the proof: it said **3**, and the 16:24:22.672 save is the one that held
exactly 3 seats. Runs B and C said **1** for the same reason.

**Why run D succeeded.** Its single seat handed over and was closed 40 seconds before the end, so the
debounced save had landed long before the ask. That is consistent with the race and is the only run of
the four where no seat was ended at the limit.

**So the shape of the defect is:** a smart restart in which anything is ended at the limit is likely to
be refused its restart, and a smart restart in which every seat hands over cleanly is likely to get
one. That is close to the worst possible split - the restart fails exactly when the shutdown had to
work hardest. The owner would see "all your sessions are gone and the Director did not come back".

I have not written a fix. It is not this phase's work and the choice between them is a design
decision - flush the roster synchronously before asking, or have the guard ask the Director rather
than a file, or have the smart shutdown wait for the write it just caused.

---

## 7. The rig is not as isolated as its header says

`scripts/restart-qa-rig.ps1` says every path is "keyed to the rig root and none of them can collide
with `%LOCALAPPDATA%\cc-director`". The handover documents do collide. Every document this phase
produced was written to:

```
C:\Users\soren\AppData\Local\cc-director\vault\handovers\director-restart\<run>\
```

which is the machine's REAL vault, not the rig's. The Director's own log names the directory it chose:

```
2026-09-20 16:19:11.400 [DirectorDrain] documents directory: C:\Users\soren\AppData\Local\cc-director\vault\handovers\director-restart\2026-09-20T161911-b0dcae-SOREN_NORTH
```

**The cause, read in the code and confirmed on the machine:** `CcStorage.Vault()` checks
`CC_VAULT_PATH` FIRST and only falls back to `Base()`, which is what honours `CC_DIRECTOR_ROOT`. On
this machine `CC_VAULT_PATH` is set as a **user** environment variable to
`C:\Users\soren\AppData\Local\cc-director\vault`, so the rig's scheduled tasks inherit it and the rig
root never gets a look in.

It is additive, not destructive - it leaves handover documents in a folder the real product also uses -
and it did not affect any result in this document. But the rig's whole claim is isolation by
construction, and this is a hole in it. The fix is one line in the rig script: set `CC_VAULT_PATH` to
the rig's own vault on the tasks it registers. **I have deliberately not made that change**, because
another Developer is running the way-up cases on this standing rig and changing the rig under them
would invalidate their setup.

---

## 8. The Director's own log lines

Quoted, not paraphrased, in `18-director-log-lines.txt`.

**Every `[DrainSessionControl] StopTurnAsync:` line.** There are two, and both are the Pi lead - the
only seat still mid-turn at two thirds:

```
2026-09-20 16:22:35.992 [DrainSessionControl] StopTurnAsync: session=66b3f777-2382-481d-a32a-be6e8bbc845a, agent=Pi, declares=ClearContext, Cancel, ContextUsage, ModelReport, CompactContext
2026-09-20 16:22:35.995 [DrainSessionControl] StopTurnAsync: session=66b3f777-2382-481d-a32a-be6e8bbc845a stopped with escape
```

That is merge 3241's mechanism in the Director's own words: it read what that agent's driver declares,
saw `Cancel` and no `Interrupt`, and used escape. The Tech Lead's note that Pi declares Cancel only is
confirmed by the product at runtime, not just by reading the driver.

**`[DirectorWayUp] FindOfferAsync:` from the start AFTER the restart.** Run D's restart produced a new
Director process, 67748, and it decided for itself that a restart was available:

```
2026-09-20 16:49:27.800 [DirectorWayUp] FindOfferAsync: machine=SOREN_NORTH
2026-09-20 16:49:27.892 [DirectorWayUp] FindOfferAsync: offering restart-20260920-1648-sorennorth, owed=1, rows=1
```

The earlier start, before this phase's runs, is in the same file for contrast:

```
2026-09-20 16:12:44.672 [DirectorWayUp] FindOfferAsync: offering restart-20260920-1230-sorennorth, owed=1, rows=4
```

`rows=1` on the second line against eight records now on the Gateway is the offer-once and seven-day
filtering doing its work. Reading that properly is the way-up Developer's case, not mine; I note the
number so it is not mistaken for a records-lost problem.

---

## 9. The check

Both commands, run in this worktree:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
  Passed!  - Failed: 0, Passed: 686, Skipped: 0, Total: 686

dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
  Passed!  - Failed: 0, Passed: 159, Skipped: 0, Total: 159
```

The window check is **159 passed, 0 failed**, exactly the Delivery Lead's own run at `2d4d90a38`.

The Gateway filter is **686**. The mandate quoted no baseline for it, and the nearest earlier
measurement is phase 4's 624 at `d8fdaafed`. Four merges landed in between and brought tests this
filter matches, so the 62 are theirs. **This branch compiles nothing** - `git status --porcelain`
shows no change under `src/` or `tools/` - so both counts are commit `8d0ba4a4f`'s own and neither is
a measurement of this branch. `20-the-check.txt` records that check.

---

## 10. What FAILED

- **The restart, three times in four.** Section 6. The product's, and the most important thing here.
- **The lead-covers-its-worker case did not arise**, because the worker removed itself four seconds
  before the run started. Not evidenced either way by this run.
- **Leads-come-first in `restoreAfterRestart` is not evidenced.** Both restorable seats were workers.
- **The handover-after-interrupt half of the mid-turn case is not evidenced.** The one interrupted seat
  wrote nothing in the hundred seconds it had left, and at five minutes allowed a hundred seconds is
  all two thirds leaves.
- **The handover comparison came out worse, not better**, and it is confounded. Section 4.
- **The planted-credential case cost three wasted runs**, all three my harness's fault. Section 5.
- **The rig leaks its handover documents into the real vault.** Section 7.

## 11. What I could NOT reach

- **Every screen.** The File menu, the close hook, the dialog and its pre-flight review, the progress
  screen and its two buttons. No host verb reaches any of them, as the Tech Lead's status file
  already records, and the one rule of this phase forbids clicking them.
- **Shut down now, Cancel and keep working, Shut down and ignore all sessions.** No command, no verb.
- **A session with a question box shown in the dialog before anything is asked.** The dialog's own
  check has no verb.
- **The Gateway unreachable when the dialog opens**, **a record for another Director on the same
  machine**, **a record already brought back**, and every way-up button. The next Developer's.
- **`DrainStopVerb.None`.** No shipping agent declares neither stop verb, so it cannot be produced on
  the rig. Not attempted, on the Tech Lead's instruction.
- **The X against File, Smart Restart** - ruling 10.4's difference between closing and restarting.
  That needs the window.
- **A ten minute run**, which is the only one that can test the Architect's three minute guess.

## 12. What I decided rather than was told

- **I replaced the `beta-secret` seat outright** rather than adding a second one. It was a Pi seat and
  is now a `RawCli` stand-in program. The old shape is the one that failed, and keeping both would
  have meant two seats proving the same case, one of them by luck.
- **I fixed `read-record.ps1`.** Its secret-sweep block read five field names the record does not
  carry - `filesSwept`, `patternName`, `filePath`, `lineNumber`, `sweptAtUtc` against the wire's
  `documentsSwept`, `pattern`, `file`, `line`, `checkedAtUtc`. The count printed BLANK, which reads as
  nothing swept, and the first run that actually found a credential **threw** - `Split-Path` on a
  null - so the one case the sweep exists for was the one case the script could not print. It now also
  prints `readyToRestart`, `notReadyReason` and any `sweepProofFailures`, because a proof failure means
  a clean result is worthless and the reader has to see it. The comment in the script says all of this.
- **I did not take `drive-smart-shutdown.ps1` or the `ui-drive.ps1` change**, as instructed, and I did
  not copy `before-picture.py` or `session-key-probes.ps1` into use - they were already in this tree.
- **I did not fix the rig's vault leak or the launcher race.** Both are real; neither is this phase's
  work, and changing the rig would break the Developer running next on it.
- **I left the Claude Code folder trust grant in place.** `claude-folder-trust.ps1 grant` was run for
  the two Claude Code seat folders and the record is at
  `C:\Users\soren\AppData\Local\Temp\restart-qa-seats-run-a\claude-trust.json`. Revoking it now could
  break the next Developer's seats. **It is outstanding and the machine is not yet back as it was
  found** - whoever finishes on this rig should run `claude-folder-trust.ps1 revoke -RecordTo <that
  file>`.

## 13. The rig is UP, and what is on it

Left standing, with the Director running and every record intact. Nothing was deleted.

- rig root: `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig`
- Gateway: `http://127.0.0.1:7911`
- token file: `C:\Users\soren\AppData\Local\cc-director-restart-qa-rig\config\director\gateway-token.txt`
- machine: `SOREN_NORTH`
- Director id: `3be6c633-c4c1-41a7-8f45-ff49e4d2e8fe`, now process 67748, started by the launcher after
  run D's restart
- binaries from `8d0ba4a4f010506879c7497ec6f34e7168a2f3f8`

**The workspace id of every record this run produced:**

| Workspace | Seats | What it holds |
|---|---|---|
| `restart-20260920-1619-sorennorth` | 8 | the main run |
| `restart-20260920-1629-sorennorth` | 1 | credential attempt, request never landed |
| `restart-20260920-1636-sorennorth` | 1 | credential attempt, request unreadable by the seat |
| `restart-20260920-1648-sorennorth` | 1 | the credential caught; **this is the one the Director is currently offering** (`owed=1`) |

Four earlier records from the stopped attempt are also on the rig Gateway and were not touched:
`restart-20260920-1225`, `-1230`, `-1255` and `-1327`, all `sorennorth`.

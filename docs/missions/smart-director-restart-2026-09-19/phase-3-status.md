# Phase 3 status - written as it happens by the Tech Lead (session 38f41a97)

The Delivery Lead polls this file. Newest entry last.

## 20 September 2026

- Seat taken. Read `start-phase-3.md`, `mandate-phase-3.md`, `mission.md`, `phase-1-interface.md`,
  `phase-2-proof.md`, `phase-2-status.md`, and the workflow conduct for `mission` version 29.
  My worktree `D:/ReposFred/devthrottle-smart-restart-p3` is detached at `origin/main` = `ab2770c4a`
  and clean; I never write code in it.

### What I read in the code before planning (all at `origin/main` = `ab2770c4a`)

- The contracts the way up needs are ALREADY on main, from phase 1: `WorkspaceDocument.ShutdownKind`
  (`WorkspaceShutdownKinds.SmartShutdown` / `IgnoreAll`), `WorkspaceDocument.CancelledAtUtc`, and
  `WorkspaceDrainStates.EndedAtLimit`. Nothing of phase 3 waits on phase 1's last task.
- `WorkspaceSummaryDto` carries Id, Name, Origin, Machine, DirectorName, SeatCount and the two
  timestamps - it does NOT carry `ShutdownKind`, `CancelledAtUtc` or anything per seat, and the
  Gateway's `WorkspaceStore.List()` builds it from head columns only, on purpose. So the way up
  filters on the summary for machine and Director name, then READS the documents of what is left.
  That is one extra call per candidate record and NO Gateway change and NO Gateway deploy, which is
  worth more than one round trip.
- An ended-at-limit seat is written with `Restore.Decision = undecided` (`DirectorDrain.cs`, the
  limit step), so `DirectorRestore.SelectTargets` never selects it. The mandate's presence check -
  at least one seat decided `restore` with no restored session id - therefore already excludes it,
  and such a seat cannot be brought back by accident. Noted consequence, stated rather than left to
  be found: a record in which EVERY session was ended at the limit does not raise "A restart is
  available" at all. Those seats are reached through File, Restart history. I am following the
  mandate's check literally; if the Delivery Lead wants that case to raise the offer too, say so here.
- Reopening a saved conversation, read in the drivers: Claude Code passes `--resume <id>`
  (`ClaudeDriver.BuildLaunchSpec`), Pi passes `--session-id <id>` and its comment says a second launch
  with the same id recalled the first launch's conversation, verified against pi 0.80.10
  (`PiAgent.BuildLaunchSpec`). `CodexDriver.BuildLaunchSpec` logs "ignoring resume" - so Codex is
  noted only, as the mission says. That is what the CODE says; whether it WORKS on this build is the
  test the mandate orders before anything is built on it, and it is its own task below.
- The way up has a place to hook: `MainWindow.WireGatewayStatusBox` / `TryAttachGatewayMonitor`
  already waits for the `ControlApiHost` to exist and then follows `GatewayMonitor`. The offer is
  asked for once, the first time that monitor says Connected.

### The PendingInteraction question the mandate handed me - SETTLED, it is true

`Session.PendingInteraction` is declared `public PendingInteraction? PendingInteraction { get; private set; }`
and that line is the ONLY occurrence of the name in `Session.cs`. A property with a private setter and
no assignment anywhere in its own type can never be anything but null on a real session. Nothing in
`src` outside tests assigns it. Its own file says why: the Claude Code hook path that fed it was
removed, and the terminal detector that replaced it is a dumb ten-second silence timer
(`TerminalStateDetector`) that does not look at the screen for a question at all.

So the way down dialog's "Answer these first?" section cannot appear on a real Director, and the
mission's check in section 7 cannot be met. It IS a task of this phase, and it is the last one, so
that the way up ships whatever happens to the account's usage limit. The route is narrow and honest:
the Director already parses Claude Code's transcript into tool-use blocks with a `ToolUseId`
(`WidgetBuilder` pairs a tool use with its later tool result), and `ConversationIngestor` already
re-reads that transcript on exactly the right trigger - the session reaching `WaitingForInput`. A
pending `AskUserQuestion` or `ExitPlanMode` block with no result is a question box open, read from the
transcript, never guessed off the screen. Claude Code only; every other agent keeps reading false.

### The plan - four tasks

1. THE WAY UP ENGINE, in `CcDirector.ControlApi.SmartRestart`, no window: find the offer, read the
   history, bring back through the existing `DirectorRestore` with the seed file of item 14, and
   reopen a saved conversation for a seat ended without a handover. Unit tests under
   `src/CcDirector.Gateway.UnitTests/Restart/`.
2. THE TWO WINDOWS, after task 1 is merged so they are built on the real types and not on a fake -
   that is what cost phase 2 a rebuild: "A restart is available", File, Restart history, and the
   start-up hook. Headless tests named `SmartRestart`, screenshots under `attachments/phase-3/`.
3. THE RIG: the reopen test per agent and the measurement of how long an interrupted session needs
   to write a handover. No product code. Runs beside task 1.
4. `Session.PendingInteraction` filled for Claude Code from the transcript. Last.

### The baseline, run by me in the foreground on untouched `origin/main` = `ab2770c4a`

A filter that matches nothing exits green, so these are COUNTS, not colours.

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 523 total: 522 passed, 1 FAILED |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | 47 passed, 0 failed |
| `dotnet test src/CcDirector.Avalonia.Tests` (whole project) | 646 passed, 0 failed |

THE ONE RED IS ON UNTOUCHED MAIN AND IS NOT THIS MISSION'S.
`SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state` fails with a
SQLite write error inside `DeviceRegistry.InitializeAuthority`, reached from `new DeviceRegistry()`
with no store path - a store shared with whatever else is running on this machine. It matched the
filter only because the word "restarted" is in its name; it touches no Drain and no Restart code. Run
on its own immediately afterwards it PASSES (17 of 17 in that class). I record it so that nobody
later reads 522 as a regression, and so that nobody counts it as this phase's to fix.

Phase 2's proof recorded 46 for the `SmartRestart` filter at `8b296fe48`; it is 47 now because main
moved on. 47 and 646 are the numbers phase 3 is measured against.

### Two Developers opened, both working, both confirmed by reading their terminals

| Session | Task | Worktree / branch | Mandate |
|---|---|---|---|
| d2907328 | the way up ENGINE, no window | `devthrottle-smart-restart-p3-engine` / `smart-restart/p3-way-up-engine` | `mandate-phase-3-developer-engine.md` |
| a3f7f8be | the reopen test and the handover timing, on the rig, no product code | `devthrottle-smart-restart-p3-rig` / `smart-restart/p3-rig` | `mandate-phase-3-developer-rig.md` |

Both on Claude Code with `--dangerously-skip-permissions --model opus`, owned by me. They run beside
each other because the rig Developer writes no product code at all. Their terminals say the account
has used 72 per cent of its weekly limit, resetting 26 September.

The windows come AFTER the engine merges, on the real types - building them against an invented
interface is what cost phase 2 a whole rebuild. `Session.PendingInteraction` is last.

### The engine is BUILT, checked by me, and under review - pull request 3202

The way up engine landed on branch `smart-restart/p3-way-up-engine` at `0cda45788`, pull request
**3202**. Four new files in `src/CcDirector.ControlApi/SmartRestart/` plus 30 lines on
`ControlApiHost` (`CreateDirectorWayUp()`, never null, like `CreateSmartShutdown`), and four test
files under `src/CcDirector.Gateway.UnitTests/Restart/`. Nothing in the Gateway, nothing in a
contract, nothing in `DirectorRestore` or `DirectorDrain`, no window.

MY OWN RUN on that commit, in the foreground, with a full build - not the Developer's number:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    Passed! - Failed: 0, Passed: 567, Skipped: 0, Total: 567

567 against a baseline of 523, so 44 new tests and no new failure. The Developer's own before-run read
523 passed and 0 failed where mine read 522 and 1; the TOTAL agrees exactly, and the difference is the
intermittent `SessionStateEventEmitterTests` red described above. Two seats measured the same tree and
got the same total: that is the number to trust.

The published surface a window calls, so the Delivery Lead can see the shape without reading code:

    IDirectorWayUp ControlApiHost.CreateDirectorWayUp();            // never null
    Task<WayUpOffer>           FindOfferAsync(ct);                  // Offered | NothingWaiting | Refused
    Task<WayUpHistory>         ReadHistoryAsync(ct);
    Task<WayUpBringBackResult> BringBackAsync(request, ct);
    Task<WayUpReopenResult>    ReopenAsync(request, ct);

Two decisions in it worth the Delivery Lead's eye, both the Developer's and both accepted by me:

- ONE record is offered at start-up, not a list; anything older is reached through the history. That
  is what the mandate's "offer one only if" says and it keeps start-up cheap.
- The reopen of an ended-without-a-handover seat does NOT write to the record, because marking a seat
  needs the restore lease and a workspace write, which belongs to the restore. The consequence, said
  out loud rather than left to be found: such a seat still shows in the history as ended without a
  handover and can be reopened twice. Nothing is hidden - the row says exactly what the button does -
  but if you want it marked, that is a ruling for you and a small follow-up task.

Reviewer e01c64c8 opened on Pi with GLM 5.3, the different agent family, in its own detached worktree
`devthrottle-smart-restart-p3-review1`; its review lands as `review-phase-3-1.md`. Developer d2907328
stopped - its work is pushed and a second round is a fresh session.

### The windows Developer is opened WITHOUT waiting for the engine to merge

Session 3417f0cc, worktree `devthrottle-smart-restart-p3-windows`, branch
`smart-restart/p3-way-up-windows`, cut from the ENGINE BRANCH so it builds on the real types from the
first minute. This is deliberately the shape phase 2 used for its task 3, and it is the answer to the
one expensive mistake phase 2 made: the progress screen was built against an invented interface and
had to be thrown away and built again. Its pull request cannot merge before 3202 does, and its mandate
says so.

### The first review landed, four findings, ALL ACCEPTED - and one question for the Delivery Lead

`review-phase-3-1.md`, by GLM 5.3 in Pi, the different agent family. Its own run on the commit
agreed with mine exactly (567 total, 567 passed) and it counted the 44 new test attributes by hand;
its own revert proof broke the empty-order guard and turned exactly one test red, and it noted that
only ONE test holds that rule. Section 5 of it lists what it attacked and could NOT break, which is
the part worth reading - the presence check has no way to ask about sessions at all, and the seed
file was read as the code really produces it, not as a test copies it.

My rulings, all four accepted, one fresh Developer (dd5e4e4c) answering them on the same branch:

1. **The reopen can start a session that is still running, and can reopen one twice.** The real one.
   Every seat the restore brings back is guarded; the reopen was guarded by nothing. Fixed two ways:
   refuse through the product own `DirectorRestore.StillRunning` against the roster, and one reopen
   per seat while this Director is up. NOT fixed, and said out loud rather than hidden: across a
   Director restart the same seat can still be reopened twice, because recording a reopen needs a new
   mark kind in `CcDirector.Gateway.Contracts` and a Gateway deploy. **That is yours to rule on, and
   it is small** - it is the only part of finding 1 left open.
2. **Copilot and Cursor really DO resume** (both drivers pass `--resume`), and the engine was telling
   the owner their conversation was lost. I refused to lengthen the hand-written list: the fact moves
   to a required flag on `AgentPluginLaunchMetadata`, so every agent must state it and a new one
   cannot get it by silence, plus one test that walks every registered agent, builds its real launch
   spec with a conversation id, and asserts the flag agrees with whether the id actually reached the
   arguments. The list can never drift from the drivers again.
3. **The history said a conversation could be reopened and gave no way to do it** - the offer object
   was null when the record was not offerable, so a window would have had to invent the sentence,
   which critical rule 7 forbids. Every ended-without-a-handover seat now carries its offer wherever
   it is shown. The start-up presence check is UNCHANGED.
4. **Bring back and reopen took any workspace id.** One guard: the record must be this machine and
   this Director name, the same rule the two read paths already use. Defense in depth today; the
   phase 4 command line is the second caller that makes it matter.

**FOR YOU, and it is the only thing I need from you in this phase.** Mission ruling 10.5 says the
operating system shutting down writes a record with names, repositories and conversation ids and NO
handovers, and that "on the way up offers the saved conversations as in 10.3". Such a record holds no
seat decided `restore`, so by the presence check the mission itself writes in section 5.3 item 10 it
is never OFFERED at start-up - it is only readable in the history. After finding 3 its conversations
are at least reachable there with working buttons. Two honest readings: the history is enough and
10.5 is satisfied, or the presence check must also raise the offer for a record holding saved
conversations nobody has reopened. I have built the mission literal wording and NOT guessed. Phase 1
has not built that record yet, so nothing is blocked either way - but whoever builds it needs your
answer, and it is a one-line change here if you want the second reading.

### MERGED: the two measurements the mandate ordered before anything is built on them - pull request 3204, `1dab84116`

The rig Developer (a3f7f8be) is finished and stopped. Documents only, no product code. I read both
attachments in full, not its summary.

**Question one - does reopening a saved conversation work on this build? YES, for both agents this
mission cares about, proved at all three layers.**

| Agent | Verdict | Deepest layer |
|---|---|---|
| Claude Code | it works | through the product, on the rig |
| Pi | it works | through the product, on the rig |
| Codex | it ignores the reopen id, as the code says | the launch spec, which is all the mission asked |

The evidence is a marker string, not an impression: a conversation was seeded with
`CRIMSON-OTTER-4417` and the name `Delphine`, the process ended, and the reopened one quoted both
back. The reopened Claude Code transcript APPENDED to the file it already had - one session id
throughout - so the id does not change on a reopen. Pi was re-measured on 0.85.1 rather than taken
from the comment claiming 0.80.10, and its "No project session found" warning printed on the first
launch and NOT on the second, which is pi saying it found the session. Codex has a reopen surface
but it is a SUBCOMMAND (`codex resume`), not a flag, so the Director cannot reach it by appending an
argument - nothing was built on that.

**The trap in it, which the way up will be judged by too:** the product reopen answered NO HISTORY
twice on a transcript that print mode had just recalled correctly, and taken at face value that
would have been a false verdict of "reopening does not work". The token counts proved the
conversation was in context (63,170 cached read plus 4,046 fresh, which is the Director's own
injected preamble), and a re-worded ask quoted the marker. A reopened session is told by its
preamble that it is new, so it answers "do you remember" against the preamble. **Ask a reopened
session for a STRING, never for a memory.** Phase 5 must word its check that way.

**Question two - the three minutes. SAFE for the shape measured; leave it alone.**

Five mid-turn runs, with the product's own words (`DrainMessages.HandOverNow`), the product's own
path (`DrainPaths.HandoverFor`) and the drain's own acceptance test: 22.5, 23.9, 28.5, 43.8 and
57.9 seconds. Fastest 22.5, slowest 57.9, median 28.5. Every one under a minute, against a guess of
three - a margin of about 3.1 times. **My recommendation to you: keep the two-thirds point where it
is.**

The honest gap, and it is the one that could break it: **a lead with seats reporting to it was not
measured.** The smart shutdown tells a lead to collect its subordinates' documents FIRST and wait
for them, which is a serial dependency none of these runs had. It is the shape most likely to exceed
three minutes and it is the next thing worth measuring, in phase 5 if not before.

### Three product defects found on the way, FILED so they outlive these sessions

None is phase 3's to build, and two of them will bite phase 5.

- **#3207 - the smart shutdown never tells a Pi session to hand over.** `POST /sessions/{sid}/interrupt`
  answers Conflict on Pi by design (`PiDriver.InterruptAsync` throws; the driver never declares the
  Interrupt capability). So a Pi session mid-turn at two thirds is never asked, keeps working to the
  limit, and is ended with no document - silently. `escape` works and produced a handover in 6.8
  seconds. The two-thirds step must pick its verb from the driver's declared capabilities. **This is
  phase 1's engine and it is the most serious of the three**: Pi is the agent this fleet uses for
  every review seat.
- **#3209 - the drain accepts a handover at 500 bytes.** Run 5's document crossed 500 at 654 bytes
  and grew to 2808, so the drain accepted it with 23 per cent written and no closing `drain-report`
  block - the very block it reads to learn whether the seat should come back. One run in eight. The
  gate should be a parseable block, not a byte count.
- **#3210 - Claude Code's folder-trust dialog eats a new session's first prompt**, and the session
  then exits reported as "exited cleanly". `--dangerously-skip-permissions` does not suppress it.
  Every restored session gets its mandate as a first prompt, so a seat restored into a folder Claude
  Code has not seen is lost this way and the record says it came back.

Two smaller things, recorded and not filed: `NewSessionRequest.ResumeSessionId`'s comment says
reopening is "Ignored by agents that don't support resume (e.g. Pi)", which is false and was found
independently by the Reviewer as well - Pi is the agent it is most straightforward for. And Claude
Code's interactive path ignores the `--model` argument the Director passes, taking the model from the
user's settings instead; on this machine that model is at its monthly limit, which is why the timing
runs had to be created through a reopen.

### The engine findings are answered (`238d815f6`), and a SECOND short review is running on them

My own runs on that commit, full builds, not the Developer's numbers: the mission check **580 total,
580 passed, 0 failed** (567 before), the agent plugin check **247 total, 247 passed, 0 failed** (238
before), and `dotnet build cc-director.sln` succeeded with **0 warnings and 0 errors** - which I ran
because finding 2 widened the change into `CcDirector.Core`, where a required record part could have
broken another project.

What the answers did, in one line each: the reopen now refuses a seat that may still be running
through the product's own `DirectorRestore.StillRunning` against the roster, and takes a once-only
claim per seat; the resume fact moved out of a hand-written list into a REQUIRED part of
`AgentPluginLaunchMetadata` that every plugin must state, read back by a test that walks every
registered agent and asserts the flag equals whether the id really reached that agent's arguments;
every ended-without-a-handover seat carries its reopen offer in the history as well as at start-up;
and one guard now refuses a record that is not this machine and this Director name.

Reviewer 923ed32b (Pi, GLM 5.3) is on the ANSWERS ONLY - a short, narrow review. Its most important
possible finding is named in its mandate: the first Reviewer's structural proof was that the Gateway
seam had NO way to ask what is running, and the seam has now gained a roster method.

### The two windows are built too - pull request 3208 - and they need one more seat

Session 3417f0cc built `WayUpOfferWindow`, `RestartHistoryWindow` and `WayUpStartUpAsk` in nine new
files under `src/CcDirector.Avalonia/SmartRestart/`, with **thirteen lines** in
`MainWindow.axaml.cs` and no deletions: one call in `TryAttachGatewayMonitor` and one File menu item.
My own run: the `SmartRestart` filter **92 passed** (47 before). Seven pictures are committed.

**Three things it did not have, and a follow-up seat carries all three:**

1. **The engine moved under it.** It was cut from `0cda45788` and the answers took the engine to
   `238d815f6`, where `WayUpHistorySeat` gained a sixth part and `IWayUpGateway` a fourth method. The
   branch must take the merge and its test fakes must be updated.
2. **The history's reopen button does not exist yet.** Finding 3's whole point was that a history seat
   now CARRIES its reopen offer; until the window draws a button from it, that fix is invisible.
3. **A FLAKY TEST, and I named it.** The Developer reported an intermittent failure in the full
   project run that it could not name, and said honestly that it could not claim the test was not its
   own. It is its own: `WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWithItsOwnRowsSeat`.
   It passes 92 of 92 under the `SmartRestart` filter every time and fails roughly one full run in
   four - 5 milliseconds when it fails against 30 when it passes, which reads as a race between a
   click that starts work on a thread pool thread and the assertion. **This is why the whole project
   is run and not only the filter**: the filtered run would have merged it green. It is fixed before
   the windows merge.

### One process fault of mine, recorded so the next Tech Lead does not repeat it

The windows Developer's mandate told it to read `phase-3-status.md` before opening its pull request,
to learn whether the engine had moved. It could not: this file lives in MY worktree and is not
committed, so from its own tree it does not exist. It said so plainly in its proof rather than
pretending, which is the right behaviour - but it then reported "the engine did not move" when the
engine had. **A mandate must name an ABSOLUTE path in the Tech Lead's own worktree, the way the
mandate paths themselves do, or commit the file first.** No harm done; the follow-up seat takes the
merge.

### The second review found the best defect of the phase, and it was prospective

`review-phase-3-2.md`, GLM 5.3 in Pi again, a short review of the ANSWERS only. Its own runs matched
mine exactly (580 and 247, solution builds clean) and it counted the rise by hand. Its revert proof
picked the one fix the Developer had NOT mutation-tested - the finding 4 guard - and turned exactly
its three tests red.

Four findings, all accepted, Developer 075a3b09 answering:

1. **The once-only reopen guard was instance state, and `CreateDirectorWayUp()` hands out a new
   engine on every call.** So it would have done nothing at all: the start-up window and the history
   window each get their own engine, and the roster check does not cover the gap either, because a
   reopened session comes back under a NEW session id and the roster is asked about the CAPTURED one.
   Two live agents in one saved conversation - the exact harm the first review's finding 1 exists to
   prevent, on the exact path that was meant to fix it. **Nobody had written a caller yet, which is
   why it was still cheap.** My ruling: no note telling the caller to hold one engine - a guarantee
   that depends on how the next caller builds an object is a tripwire nobody can see. The claim goes
   STATIC, the way `DirectorRestore` already holds its one-at-a-time rule, because the rule belongs
   to the Director and not to the object. And the test must prove it with TWO ENGINES, because two
   engines is what the windows really do.
2. **The "never asks what is running" rule is now held by one assertion in one test whose own comment
   says the opposite.** The first review's structural proof was that the seam could not ask; the
   ruling on finding 1 gave it a roster. The comment is corrected and the assertion is spread to the
   other read paths.
3. **The interface does not tell a window author that a FAILED reopen keeps its claim**, so a
   transient failure leaves a button dead until a restart and reads as a bug. One sentence.
4. **A future agent could be worded as resuming for the wrong reason** - the walk proves the flag
   against whether the conversation id is in the arguments, which is a proxy that can only lie in one
   direction. A comment in the walk telling whoever meets it failing not to flip the flag.

Two review rounds on one piece of work is not a sign of trouble; it is the method working. Round one
found what the code did wrong, round two found what the FIX did wrong, and neither was visible to the
seat that wrote it.

### MERGED: the way up engine, pull request 3202, `217b79f63`

Answers to the second review landed (`9d34b67e5`): the reopen claim is STATIC, the way
`DirectorRestore` holds its own one-at-a-time rule, so it belongs to the Director and not to whichever
engine took it - and the twice-test now proves it with TWO SEPARATE ENGINES, which is what the windows
really do. **No test was added or removed by that round** (580 and 247 either side), and that is
right: findings 2, 3 and 4 asked for the rules already held to be held HONESTLY, not for more of them.

Before merging I ran, myself, in the foreground:

- the mission check TWICE IN A ROW - **580, 580** - because the new state is static and static state
  that leaked between tests would show as a second run that differed from the first;
- the agent plugin check - **247**;
- `git merge origin/main` into the branch, then both checks again on the merged tree - **580** and
  **247** - and `dotnet build cc-director.sln` clean, 0 warnings, 0 errors.

Then squash merged and the branch deleted. The engine is on main.

### The last seat of the phase: finishing the windows (fc5b8b64), pull request 3208

Three things, all named:

1. take the merge of `217b79f63` and fix what stops compiling - `WayUpHistorySeat` gained a part, so
   every test fake that builds one positionally must pass it;
2. **the history's reopen button**, which is the whole point of review 1's finding 3: the engine now
   puts the offer on a history seat, and until the window draws a button from it the history still
   makes a promise it does not keep;
3. **the flaky test, which I named.** `WayUpOfferWindowTests.BtnReopen_Clicked_ReachesTheEngineWith-
   ItsOwnRowsSeat`. Its mandate forbids a sleep or a retry - a test that waits a fixed time is the
   same defect with a longer fuse - and requires four consecutive whole-project runs, all counts
   reported, not the best one.

### The windows are finished, the flaky test is EXPLAINED, and a Reviewer has them

The second windows seat (fc5b8b64) took the engine's merge, drew the history's reopen button through
ONE shared class so the two windows cannot word it differently, and fixed the flake. It did the thing
I care about most: **it reproduced the failure before fixing it and then OBSERVED the cause rather
than reading it off the code.** Ten whole-project runs reproduced it once (so one in ten, not one in
four as I had guessed from a smaller sample). Two causes were possible and the code cannot tell them
apart. So it wrote a throwaway diagnostic that STARVED the thread pool - one worker thread, 512
blocked items queued - opened the real window, pressed the real button and read both numbers:

    row buttons drawn = 1; engine requests the instant the press returned = 0;
    the engine call arrived 25000 ms later

The button selection was never the problem. The press hands its work to a thread pool thread, and
`Dispatcher.UIThread.RunJobs()` runs what is queued on the interface thread and never waits for the
pool - so the assertion was racing it. On an idle machine the same diagnostic reported the pool
winning by three microseconds, which is the passing case. The engine is called off the interface
thread on purpose; the test was wrong about it, not the window. The fix has each window remember the
task its last press started and the test await that same task - no sleep, no retry. **The bring back
test had the same defect** and is very likely the unnamed red the first seat reported.

**My own runs on the merged head `2a1a210cf`**: the `SmartRestart` filter **145 passed** (it is 145
and not 97 because phase 2's swap landed on main in the meantime, bringing its own tests with it);
the whole project **744 passed, 0 failed, FIVE RUNS IN A ROW**; `dotnet build cc-director.sln` 0
warnings, 0 errors.

**One thing I did with my own hands, and I record it because my mandate says I never write code.**
Phase 2's swap merged to main while this was being built, so `git merge origin/main` conflicted in
`MainWindow.axaml.cs` exactly where predicted: main had replaced "Drain this Director for restart..."
with "Smart Restart", and this branch had added "Restart history..." beside the old item. I resolved
it myself - take main's item, keep ours after it, drop the item main deleted - because it is a
mechanical take-both-sides resolution at the merge gate, and it is CHECKABLE rather than a judgement:
`git diff origin/main -- src/CcDirector.Avalonia/MainWindow.axaml.cs` now shows only this branch's own
two additions, the start-up call and the menu item. I also corrected one comment that still said
"beside the drain" after the drain item was gone. If you would rather a Tech Lead never touch a file
at all, say so and the next one opens a seat for it.

**I looked at the pictures myself**, because a Reviewer may not be able to open an image. The offer
window shows the two ended-without-a-handover rows unticked and amber beside a ticked mission head -
one with its Reopen button, one saying no conversation was recorded and carrying no button at all.
The history shows an ignore-all record whose ended seat has a working Reopen button (which is
finding 3 of the first review, now visible) and a cancelled record that says it was cancelled and
offers nothing. They read as plain English and I would be content to see them on screen.

Reviewer af5cf221 (Pi, GLM 5.3) has the windows now. They have never been reviewed by anybody.

### The windows review: NO product defect, two test gaps, both taken

`review-phase-3-3.md`, GLM 5.3 in Pi. Its counts matched mine exactly (145 and 744, three more full
runs of its own, clean build), and its revert proof broke the start-up ask's once-only latch and
turned exactly the two tests that own it red. It attacked and failed to break every rule I pointed it
at: the client is dumb, the start-up ask asks once off the interface thread and stays quiet when it
must, Not now writes nothing, a press reaches the engine once, the reopen button names its own seat.
It agreed with both decisions the Developers had flagged and judged the flaky test fix sound.

Two findings, both LOW and both gaps in the TESTS rather than defects in the product, both accepted
and given to a small seat (77cf74b3):

1. **Nothing presses the history's Bring back button.** The button is drawn and the offer it builds is
   tested by calling the builder directly, but the click handler and the re-read of the history after
   the offer closes are held by nothing - delete the re-read and every test stays green. The window
   would then keep saying a record still owes seats minutes after the owner brought them back.
2. **The offer window's reopen test has only ONE reopenable row in its fixture**, so the rule it
   claims to hold - the button carries its own row - cannot be shown failing there: finding a row by
   position would find the same row and the test would stay green. The history window already has the
   strong version, with two seats and the second pressed. The offer window gets the same.

That second one is worth reading twice, because it is the shape this fleet keeps meeting: a test can
be green, well named, and about the right thing, and still be unable to fail. The Reviewer found it
by asking what the FIXTURE could distinguish, not by reading the assertion.

It also noted, correctly, that both proofs say "thirteen lines" in `MainWindow.axaml.cs` where the
diff says fifteen - the two extra are comment lines. Recorded, nothing turns on it.

### ALL FOUR PIECES MERGED - phase 3 is done

| Pull request | What | Merged as |
|---|---|---|
| 3204 | the two measurements, ordered before anything was built on them | `1dab84116` |
| 3202 | the way up ENGINE | `217b79f63` |
| 3215 | the Director knows a question box is open | `e3bdd050f` |
| 3208 | the two WINDOWS | `99cd1c034` |

My own final run on merged main `99cd1c034`: the Gateway filter **606 passed, 0 failed** (523 at the
baseline), the `SmartRestart` filter **145 passed** (47), the whole Avalonia project **776 passed,
0 failed** (646), and the two Core filters **32 passed**. Every run reached its end and printed its
own total.

The two test gaps the windows review found are filed as issue **3226**, with the two comments in
`src/CcDirector.Avalonia` that the question box change made false. The seat I opened to close them
was wedged - its first prompt never took, twice, and a direct prompt did not rescue it - so I merged
on the Reviewer's own judgement that neither gap blocks the pull request, and carried them to the
issue rather than leave a branch unmerged overnight.

`phase-3-proof.md` is written and merges with this record.

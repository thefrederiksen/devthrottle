# Mission document - Smart Director Restart

Status: APPROVED by the owner, 19 September 2026. Written by the Architect seat (session badce6d4),
which handed over and shut down. The owner was shown all eight points of section 10 with the
Architect's recommendation on each, and the recommendation to go with all eight. His words:

> Okay, go ahead with the implementation lead in the new session. And then if we no longer need this
> session, I want you just to shut yourself down as soon as you know the implementation lead is up and
> running.

So every item in section 10 stands as recommended. He did not comment on any single item, and he did
not reword the goal in section 3; if one of them turns out to matter, ask him - do not treat his go as
a ruling on a detail he was never asked about on its own. He calls the Delivery Lead the
"implementation lead".

Mission issue: #3167. Defects fixed on the way: #3168 (the old Drain window throws on opening) and
#3169 (the restart cycle is wired to a stand-in drain).

Product repository: `thefrederiksen/devthrottle`. Everything below was read at tag `v2.8.1`, the
installed build. The first thing the Delivery Lead does is check what has changed on `origin/main`
since that tag in the files named in section 5.

---

## 1. The mission

When a Director with sessions running is closed or restarted, it shuts its sessions down nicely - each
one writes a handover - and when the Director comes back it finds the record by itself and offers to
bring the sessions back, each in a new session with a clean context. It is a feature of the product,
run by code inside the Director. Working name: Smart Director Restart.

## 2. The why

In the owner's words (19 September 2026):

> I think there is value to restarting the director once in a while. I think if you run anything like
> every morning, A user should almost start the director. Because handover documents does a
> compression of the context, And it keeps them honest and stops them Like, keeps the context low and
> it focuses on what's left to get done.

And from the first mandate the same day:

> I think we need this because I too often need to restart the director and I can't right now.

Two reasons, then. A Director full of live sessions cannot be updated or restarted today without
losing them, and the one door built for it (File, Drain this Director for restart) has never opened in
a shipped build. And a restart is worth doing for its own sake: a handover is a compression of a
session's context down to what is left to do.

## 3. The goal

The Architect's wording. The owner saw it as part of the document and said go; he did not reword it.

On a real Director with at least six sessions across at least two missions, one of them a lead with a
session under it, one of them in the middle of a long turn, and one of them idle and never answering:

1. The owner chooses File, Smart Restart (and, in a second run, closes the window with the X). A
   dialog shows the number of sessions and he confirms the smart shutdown.
2. Without him doing anything else, inside the time allowed, every session that can write a handover
   has written one, the busy one was interrupted and wrote one, the silent one was ended and noted,
   and the Director is empty. A progress screen showed it happen.
3. The Director restarts. When it comes back it says, by itself, that a restart is available and lists
   what it will bring back. He confirms once. Every mission continues in a new session that reads its
   handover and carries on; the noted one is offered with its saved conversation.
4. "Shut down now" and "Cancel and keep working" both work from the progress screen, and after a
   cancel the Director holds the same missions it started with.
5. None of this needed a session to drive it, a line pasted anywhere, or a model.

The proof is a QA report with screenshots of each screen, the flow above, and the failure cases in
section 7.

## 4. Decisions - the owner's answers, in his words

Dictated text is kept as it came. A stray "Thank you" or "Bye" is the transcriber, not him.

### 4.1 Carried from the first report (19 September 2026, before and during the hand-run)

- Fresh context over old: "We start with new code context. We don't drag over all of the shit we had
  from before."
- The way up: "it should come up and say ... A restart is available. So we put it somewhere the
  director knows about."
- History: "I don't think we delete it right away. We put it as a history, because it could be that I
  accidentally don't restart it right away and I want to restart it later."
- Per session or not: "I'm not sure we want to be asked on every session. So maybe we ... refine ...
  the features ... after the restart today when we learn some more."
- A session that misses the limit: "we do the handover document or just resume the session either one
  what we figure out when we implement the later".
- Elevated mode: "we need an override ... so the gateway will allow an agent to call another agent if
  we put it into elevated mode. That way the user could choose that you are an elevated agent and the
  other agent should listen to you." Made when a session was expected to drive the restart. This
  mission does NOT build it and does not need it (see 5.2). It stays the owner's ruling for whoever
  needs it next.
- The time limit, first version: "If they don't stop in 10 minutes... You escape them and tell them now
  to hand over the document." and "max 15 minutes, I would say is the default, but user can choose
  from a drop down of 5 minutes, 15 minutes, 30 minutes and an hour". SUPERSEDED by 4.5.

### 4.2 The feature itself (19 September 2026, about 21:50)

> change the menu to say something a little bit nicer ... The file menu is not working. with the old
> one and the name is really bad. ... It should be called something like Smart Director Restart. ...
> and it should come up with a dialogue that pops up ... Hopefully we don't have to start a new
> session to do this. If we need any large language model, we should use the the ones that are built
> into the gateway ... this should be a feature that we have as a software feature ... and leave a
> document that when you restart the director it automatically detects that document Because we've
> updated the director code and says, hey, I see that we are restarting with all of these sessions.

### 4.3 Question 1 - who runs it. SETTLED: code.

> I agree with your first question. We run it all in code, the restart.

### 4.4 The shape of the way down (same dictation)

> and the smart restart should happen automatically. It should be a menu like under the file and there
> should be a menu to smart restart. But also if somebody tries to close down by hitting the X on the
> window or whatever the close is on Linux and Mac. we should capture that they're doing it. And I
> think we warned right now about this will close down there. Thank you. their sessions. And we should
> have a nicer dialogue box that comes up. If there's nothing, if there's no sessions running, we just
> shut down. We don't say anything. If there's any sessions currently running, We show the number of
> sessions running. And then we ask, just shut down. And we explain that saying, if you just shut
> down, The default is smart shutdown. Always. SmartJar shutdown says it will nicely shut down your
> sessions. and that they need to be given a choice. By default, it's 10 minutes. We'll allow the
> agents 10 minutes to clean up and shut down. Um... Otherwise, we just do it for it and then you will
> be able to restart the sessions when you restart the director. Or they can have the option to shut
> down and ignore all sessions. Just basically kill all sessions, which some people might want to do.
> If none of the sessions are important, they're all finished or... They just haven't cleaned up or
> they just want to restart. So I think that's how we should do the restart feature.

> If you select the menu, File, Smart, Restart, it obviously just... it should still come up with a
> dialogue and say how many sessions there are, and just get you then to confirm, right? Then there's
> a dialogue that comes up so we can see something is happening. And then there should also be some
> kind of progress. instead of us we don't need to see the session once you've chosen shutdown either
> by clicking the x or clicking the smart shutdown, we should have the progress screen that comes up
> in both cases. If you do the smart shutdown, And it shows... how it's shutting down, right? It
> progressed. There are nine things. And then once they shut down, say one shut down, two shut down
> and so forth, so they can see the progress.

> I don't know if they should be able to cancel it. Technically, they could cancel it and we could
> load it back up. Um... You could also want to cancel it and just shut down and not do this waiting
> if they're really in a hurry. for whatever reason, So we need to figure out some way of aborting
> this.

### 4.5 Questions 2 and 3. SETTLED on the Architect's recommendations.

Asked, in turn: what may the user do to stop a shutdown under way (recommended: two buttons, "Shut
down now" and "Cancel and keep working"); and how much time the sessions get (recommended: ten minutes
in all by default, sessions still working at two thirds of the time are interrupted and told to hand
over now, dropdown of 5, 10, 15, 30 minutes and an hour). His replies:

> What was your recommendation and then update the report?

> Okay, go with your recommendations. I think these are good.

Three minutes to write a handover after an interrupt is the Architect's guess and is untested. Phase 3
measures it, and the two-thirds point moves if the measurement says so.

## 5. Design

### 5.1 What is already in the product (read at v2.8.1) and is REUSED, not rewritten

| Piece | Where | What it does |
|---|---|---|
| The Drain engine | `src/CcDirector.ControlApi/Drain/DirectorDrain.cs` | Captures the record on the Gateway first; works out the chain; asks each top level session; polls the handover folder every 10 s; ignores a document under 500 bytes; re-reads at the end; flags finished sessions for closing, bottom of each chain first; gathers owner questions |
| How it speaks to a session | `Drain/IDrainSessionControl.cs` | `SessionCommandExecutor.SendPromptAsync(..., SendSource.Framework)` on the Director's own session manager. No Gateway message. Reports WHY a prompt did not land |
| The closing block | `Drain/DrainReportBlock.cs` | The session states: state, restore yes or no, why, covered, question, blocked-reason. Last block wins; an unreadable answer is undecided, never guessed |
| The request text | `Drain/DrainMessages.cs` | What a session is told |
| The secret sweep | `Drain/HandoverSecretSweep.cs` | Proves each pattern can fire, then sweeps |
| The record | `src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs` | A workspace on the Gateway: Machine, DirectorName, DirectorId, Reason, and per seat the agent, repository, mission, role, owner, agent arguments, conversation id and file, handover path, drain state, restore decision, restored session id |
| The restore | `Drain/DirectorRestore.cs` | Starts each seat decided `restore` that has no restored session id, leads first, under its real owner, from a one-line prompt pointing at a seed file; one at a time per Director; safe to ask twice |
| The restart cycle | `src/CcDirector.ControlApi/Restart/DirectorRestartCycle.cs` | Eligibility, drain, capability re-check, ask own launcher to restart only if empty. Wired to `NoDrainOnThisBuild` in `ControlApiHost.cs`, method `RestartDrainStep()` (line 1058 on `origin/main` at `9eb3a13bc`; it was 1045 at `v2.8.1`) |
| The close hook | `src/CcDirector.Avalonia/MainWindow.axaml.cs`, `OnClosing` (line 6578 on `origin/main`) | Shows `CloseDialog` when a session is working or waiting; on yes, ends everything. The old menu item "Drain this Director for restart..." is added in the same file (line 4573) and opens `DrainDirectorDialog` |
| Open question boxes | `src/CcDirector.Core/Sessions/PendingInteraction.cs` | The Director knows when a Claude Code session has a question box open |
| Interrupt | `SessionCommandExecutor.InterruptAsync`, every agent driver | The Director can interrupt a session. The Drain has no such verb today, on purpose |
| Tests and rig | `src/CcDirector.Gateway.UnitTests/Drain/`, `.../Restart/`, `src/CcDirector.Avalonia.Tests` (headless), `scripts/restart-qa-rig.ps1` | Engine tests, a headless window test project, and an isolated rig with its own Gateway, launcher and Director |

### 5.2 Why no session drives it and elevated mode is not needed

The rule that stopped the hand-run ("you may message only the session that started you") is a Gateway
rule about one session messaging another. The Drain is the Director typing into its own sessions, as
it does for any prompt. Code inside the Director was never bound by that rule. Nobody pastes anything.

### 5.3 What is NEW

1. **Two doors, one dialog.** File, Smart Restart replaces "Drain this Director for restart". Closing
   the main window (any platform) goes to the same dialog. No sessions running: no dialog, the
   Director just does what was asked.
2. **The dialog.** Shows the number of sessions. Smart shutdown is the default, always. It says, in
   plain words: your sessions are shut down nicely; each writes a short handover of what it was doing
   and what is left; they get ten minutes; whatever is still running after that is shut down for them;
   you can start the sessions again when the Director comes back. One setting: the time allowed (5,
   10, 15, 30 minutes, an hour; default 10). The other choice: shut down and ignore all sessions.
   Cancel closes the dialog and nothing happens.
3. **The review, inside the dialog, in code.** Before anything is asked the dialog lists what the
   Director already knows: sessions with a question box open for the owner ("answer these first?"),
   and the count working against waiting. No model. The session's own closing block says whether it
   should come back; the dialog does not try to guess that in advance.
4. **The progress screen**, in both cases, in place of the session view: one row per session with its
   state (asked, writing, handed over, shut down, interrupted, ended at the limit), a count ("4 of 9
   shut down"), the time left, and two buttons.
5. **Two stages and a limit.** At two thirds of the time allowed, sessions still mid-turn are
   interrupted and sent a short "hand over now: the exact next action first" request. At the limit
   every session still present is ended and recorded as `ended-at-limit` with its conversation id.
   This REPLACES the Drain's never-force rule and its ninety minutes for this feature. The sentence
   "you will not be killed" leaves the request text; in its place: you have N minutes; write the exact
   next action first and the rest after, so that whatever is on disk when time is up is already
   useful.
6. **Shut down now.** Jumps to the limit.
7. **Cancel and keep working.** Stops closing sessions; tells each session still open that the restart
   is off; brings back every session already closed from the handover it just wrote, through the
   restore, against the SAME Director; marks the record cancelled in the history.
8. **Shut down and ignore all sessions.** Ends everything at once. It still writes the record first
   (names, repositories, conversation ids), so the history shows what was closed. Nothing is offered
   back automatically from such a record.
9. **The restart itself.** From File, Smart Restart the Director restarts through the existing cycle,
   now wired to the real drain. From the X the Director closes and stays closed until the owner opens
   it. (10.4.)
10. **The way up.** At start-up, once connected to the Gateway, the Director asks for workspaces whose
    Machine and DirectorName are its own, whose origin is a smart shutdown, and which hold at least
    one seat decided `restore` with no restored session id. That is a PRESENCE check on the record,
    never "no sessions are running". If it finds one it shows "A restart is available": when, how
    many, one row per mission head, a tick box each, leads first. Bring back, or not now. Not now
    keeps it in the history.
11. **Restart history.** File, Restart history: every record for this Director, newest first, with
    what came back and what did not, and a way to bring back later.
12. **A command as the second door.** `cc-devthrottle director smart-restart` (owner key only, like the
    restart route today) and `cc-devthrottle director restart-history`. One broken window must never
    again leave a Director impossible to empty.
13. **Restored sessions inherit dev reports.** A dev report belongs to the session that published it,
    so a restored session republishing the same file gets a new link and the owner's old link
    freezes. The restore must pass ownership of the old session's reports to the new one. This touches
    the dev-reports service, not only the Director. Phase 0 correction: that service is NOT a separate
    repository. It lives in the Gateway inside the product repository
    (`src/CcDirector.Gateway/Api/DevReportEndpoints.cs`, tests under
    `src/CcDirector.Gateway.UnitTests/DevReports/`), so phase 4 is one repository, and a Gateway change
    that is merged and not deployed until the owner decides.
14. **The seed file says less.** Tonight a seed line ("no commits unless the owner asks") overrode a
    commit a handover had planned. The seed the product writes holds only: you are a restored session,
    read this document, what changed while you were gone, verify state before acting. No rules of
    conduct. (10.6.)

### 5.4 Out of scope

- Elevated mode.
- Any model. If real restarts later show weak handovers getting through, a Gateway model grading a
  handover is version two.
- Restarting a Director on another machine from here (the Cockpit and phone request path exists; this
  mission only makes sure it reaches the real drain).
- Codex conversation resume (the driver ignores the id today). A Codex session ended at the limit is
  noted and offered as a fresh blank session in its repository.

### 5.5 Known defects this mission fixes on the way

- `DrainDirectorDialog` throws on opening: it is the only window that defines its own
  `InitializeComponent() => AvaloniaXamlLoader.Load(this)`, which skips the generated code that
  connects named controls, so `TxtReport` is null in the constructor. A reading of the code, not a run
  under a debugger. The window is replaced by this feature; the lesson is law: every new window gets a
  headless test that opens it.
- The restart cycle is wired to the stand-in drain.

## 6. Phases

| Phase | What | Tech Lead? | Runs beside |
|---|---|---|---|
| 0 | Delivery Lead reads `origin/main` against section 5.1 and corrects this document's file references. Files the two defects in 5.5 as issues | no | - |
| 1 | The engine: time allowed, two stages, interrupt verb, ended-at-limit with conversation id, new request text, cancel path (restore against the same Director), ignore-all path writes a record. Wire the restart cycle to the real drain. Unit tests on the existing `DrainTestRig` | yes | 2 |
| 2 | The way down screens: File menu item, the close hook, the dialog, the progress screen. Headless test opening each window. Follows `docs/VisualStyle.md` | yes | 1 |
| 3 | The way up: start-up detection on the record, "A restart is available", restart history, bring back, the ended-at-limit session offered with its saved conversation (Claude Code; Pi if the phase's own test shows it works; Codex noted only). Measure the time an interrupted session needs | yes | 4 |
| 4 | The command line door, and dev report inheritance in the dev-reports service | no | 3 |
| 5 | QA on the isolated rig (`scripts/restart-qa-rig.ps1`): the flow and every failure case in section 7, with screenshots. Then one real run on DevThrottle_1 with the owner | no | - |
| 6 | Retire: the fleet skill `director-restart` is rewritten to say "use File, Smart Restart" and keeps only what a person still does by hand; public docs page | no | - |

Coding style: `docs/CodingStyle.md`. Visual style: `docs/VisualStyle.md` (and `docs/CockpitVisualStyle.md`
only if a Cockpit screen is touched). All three exist in the product repository.

## 7. The check

Anything an agent can run by itself:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
powershell -File scripts/restart-qa-rig.ps1 build
powershell -File scripts/restart-qa-rig.ps1 up
```

then the QA run scripts under `scripts/restart-qa/` pointed at the rig. The Delivery Lead confirms the
exact filter names in phase 0; the second filter does not exist until phase 2 writes it.

The QA report must show, each with a screenshot or a record excerpt:

- the flow in section 3, from the File menu AND from the X;
- no sessions running: no dialog;
- a session mid-turn: interrupted at two thirds, handover written, or ended at the limit and noted;
- a session that never answers: ended at the limit, noted, offered on the way up with its saved
  conversation;
- a lead with a session under it: the lead covers it, leads come back first;
- a session with a question box open: shown in the dialog before anything is asked;
- a wedged session that cannot take a prompt: reported as such, not waited on in silence;
- Shut down now; Cancel and keep working (same missions afterwards); Shut down and ignore all;
- the Gateway unreachable when the dialog opens: the smart shutdown refuses with the reason (the record
  must live off the machine), the ignore-all choice still works;
- a record for ANOTHER Director on the same machine is not offered;
- a record already brought back is not offered again;
- a planted password in a handover is caught.

## 8. Merge plan

Trunk. Every phase is one or more small pull requests to `main`, merged the day they open. Safe because
the new door is the only caller: until phase 2 lands the File menu still shows the old item, and the
engine changes are behind options the old path does not set. Phase 2's pull request is the one that
swaps the menu item and the close hook, and it merges only with its headless window tests green.

## 9. Where it ends

Merged to `main` with the QA report committed, and ONE real run on DevThrottle_1 with the owner. The
release is the owner's separate decision. (10.8.)

## 10. Questions

Settled: 1 (code), 2 (two buttons), 3 (ten minutes, interrupt at two thirds). Below, each item is the
Architect's recommendation as the owner accepted it with his go.

- 10.1 ACCEPTED - **The name.** Recommended: the menu says "Smart Restart" and the close dialog says "Smart
  shutdown", as the owner himself said both tonight; "Director" is dropped from the menu text because
  the File menu is already the Director's.
- 10.2 ACCEPTED - **On the way up, asked how often.** Recommended: once for the lot, one row per mission
  head with a tick box, all ticked. He said "I'm not sure we want to be asked on every session".
- 10.3 ACCEPTED - **The session ended at the limit, or that never answered.** Recommended: it is NOT
  brought back automatically. It is listed on the way up as "ended without a handover" with one
  button: reopen its saved conversation, with a first line telling it it was stopped and must re-check
  before acting. Unticked by default. Whether reopening works on 2.8.1 is untested for every agent;
  phase 3 tests it before building on it.
- 10.4 ACCEPTED - **X against restart.** Recommended: the X means close - after the smart shutdown the
  Director exits and stays closed; "A restart is available" appears whenever it is next opened. File,
  Smart Restart means restart - the Director comes straight back through its launcher.
- 10.5 ACCEPTED - **The operating system is shutting down.** There is no ten minutes. Recommended: no
  dialog; the Director writes the record at once (names, repositories, conversation ids, no
  handovers), lets the sessions end, and on the way up offers the saved conversations as in 10.3.
- 10.6 ACCEPTED - **What the seed may say.** Recommended: as 5.3 item 14 - no rules of conduct.
- 10.7 ACCEPTED - **The morning habit.** Recommended for version one: the dialog explains why a restart is
  worth doing, and nothing nags. A reminder ("this Director has been up four days") is version two.
- 10.8 ACCEPTED - **Where it ends.** Recommended: as section 9.
- 10.9 ACCEPTED with the go, not discussed - **Sections 2 and 3.** The why is in his words; the goal is the Architect's draft.

## Phase 0 - what the Delivery Lead found on `origin/main` (19 September 2026)

Read at `origin/main` = `9eb3a13bc`, seven commits after `v2.8.1`. Names and paths only; no code was read.

- Every file named in 5.1 still exists at the same path. None of the Drain, Restart, workspace record or
  pending-interaction files changed since the tag. Neither did their tests or `scripts/restart-qa-rig.ps1`.
- Three named files did change, for other features (open a session in the Cockpit from the tab row, the
  repository order): `MainWindow.axaml`, `MainWindow.axaml.cs`, `ControlApiHost.cs`. Only line numbers
  moved; 5.1 is corrected above.
- The check in section 7: the first filter is right. The test namespaces are
  `CcDirector.Gateway.UnitTests.Drain` (five test files, 131 test attributes) and
  `CcDirector.Gateway.UnitTests.Restart` (one file, 18). The project `src/CcDirector.Avalonia.Tests`
  exists; nothing in it matches `SmartRestart` yet, as the document says. `scripts/restart-qa/` holds two
  scripts from the earlier mission and no run scripts for this one; phase 5 writes them.
- 5.3 item 13 named a repository that does not exist; corrected there.
- No issue existed for either defect in 5.5. The earlier mission that built the Drain was product issue
  2719, record at `docs/missions/restart-a-director-2026-09-06/`.

## Not verified by the Architect

- That the Drain engine works end to end when started from the product; only its tests say so.
- That reopening a saved conversation works on 2.8.1 for any agent.
- Anything on `origin/main` after `v2.8.1`.
- How a session behaves when interrupted mid-turn and asked for a handover.
- That the dev-reports service can move a report from one session to another.

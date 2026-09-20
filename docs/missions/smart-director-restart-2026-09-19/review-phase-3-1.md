# Review - Smart Director Restart, phase 3, Reviewer 1: the way up ENGINE

Written by the Reviewer seat opened by the phase 3 Tech Lead (session 38f41a97). Reviewed: branch
`smart-restart/p3-way-up-engine`, commit `0cda45788`, pull request #3202, cut from `origin/main` =
`ab2770c4a`. I did not write this code. I changed no product code; everything below was read, run
and put back.

---

## 1. Scope

What I read:

- The mission document, section 5.3 items 10, 11 and 14, rulings 10.2, 10.3, 10.5 and 10.6, and
  the phase 0 notes.
- The Developer mandate (`mandate-phase-3-developer-engine.md`) and the Developer's proof
  (`proof-phase-3-engine.md`), both treated as claims to disprove.
- All nine new and changed files in full: `DirectorWayUp.cs`, `IDirectorWayUp.cs`, `IWayUpGateway.cs`,
  `WayUpWords.cs`, the `ControlApiHost.CreateDirectorWayUp` addition, and the four test files.
- The code the engine builds on: `DirectorRestore.cs` in full (the guards the engine hands its
  order to), the limit block and the state writing of `DirectorDrain.cs`, the seat and summary
  parts of `WorkspaceDtos.cs` and `WorkspaceRestoreDtos.cs`, `WorkspaceStore.List` (that the
  summary really carries Machine and DirectorName), `GatewayClient.ListWorkspacesAsync`,
  `GetWorkspaceAsync` and `SpawnOnThisDirectorAsync`, `SessionCommandExecutor`'s create path
  (that `ResumeSessionId` really reaches the agent), `DrainPaths` (ShortId, Sanitize, the handover
  file name), and the agent drivers: ClaudeAgent, PiAgent, CodexAgent, CodexDriver, CopilotAgent,
  CursorAgent, GeminiAgent, GrokAgent.
- The phase 1 engine's `DirectorSmartShutdown` far enough to see that the ignore-all record and the
  operating-system-shutdown record are NOT BUILT YET (both methods throw "not built yet").

What I ran, all in this worktree, never in the background:

1. The mission's check on the untouched commit, full build:
   `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
2. The same command with my own one-line mutation in the engine (section 3).
3. The same command after putting the source back, with the file touched first so the build
   recompiled it - a full build, never a no-build run.

What I did NOT look at: the phase 1 interface document, the phase 2 window code, anything in
`src/CcDirector.Avalonia`, the rig scripts, the Gateway endpoints beyond the workspace ones, and
the rest of the mission document beyond the sections named above. No real Gateway, no Director and
no session was run: every test in this suite runs against fakes of the two seams, and so did every
check of mine. I did not run the parked suites, the web tests, the Python tests or the full local
gate; only the mission's filtered check.

## 2. The counts, from my own runs

My run on the untouched commit `0cda45788`:

    Passed!  - Failed: 0, Passed: 567, Skipped: 0, Total: 567

**567 total, 567 passed, 0 failed.** That is exactly what the Tech Lead measured on this commit
(567 total, 567 passed). Against the baseline of 523 on untouched `origin/main`, the total has
risen by 44, and I counted the test attributes myself: 19 in `DirectorWayUpOfferTests`, 13 in
`DirectorWayUpBringBackTests`, 12 in `DirectorWayUpHistoryTests` - 44. The numbers agree.

The known intermittent test
(`SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state`) PASSED in
my run. I never saw it fail, so I cannot confirm the Tech Lead's observation - I can only say it
did not disturb my counts. It is not this phase's and I do not report it as a finding.

## 3. My own revert proof

I chose to break the rule I was told costs most if wrong: the guard that stops an EMPTY seat list
reaching the restore, which the restore reads as "bring back everything owed". In
`DirectorWayUp.ChooseSeats` I replaced the final refusal with an unconditional return, so a
request with nothing ticked would be handed straight to the restore:

    return seats.Count == 0
        ? (seats, "no row was ticked, so there is nothing to bring back.")
        : (seats, null);

became `return (seats, null);`

The red run, full build:

    Failed CcDirector.Gateway.UnitTests.Restart.DirectorWayUpBringBackTests.Nothing_ticked_is_refused_and_never_sent_as_an_empty_order
    Failed!  - Failed: 1, Passed: 566, Skipped: 0, Total: 567

**1 failed, 566 passed, 567 total.** I put the source back with `git checkout --` (the commit was
already in place, so the restore could not eat it), confirmed the working tree clean, touched the
engine file to force a recompile, and ran the check again. The run printed both assemblies rebuilt
(`CcDirector.ControlApi -> ...dll` and `CcDirector.Gateway.UnitTests -> ...dll`), so the green is
the restored SOURCE and not a leftover binary:

    Passed!  - Failed: 0, Passed: 567, Skipped: 0, Total: 567

**0 failed, 567 passed, 567 total.**

One honest note: exactly ONE test holds this rule. It is the right owner and it failed exactly as
it should, but if that one test is ever deleted the rule is unguarded.

## 4. Findings, ranked

### Finding 1 - the reopen path can start a session that is still running, and can start the same saved conversation twice

`DirectorWayUp.ReopenAsync` (`DirectorWayUp.cs` line 193) starts a session for a seat that ended
without a handover with NO check that the seat is gone. Every seat the restore brings back is
guarded by `DirectorRestore.StillRunning` (the roster, reachable Directors, the "still listed under
a Director nobody can reach" rule) and by the once-only token rules; the reopen bypasses all of
them. Two ways this bites:

1. The drain writes `DrainState = ended-at-limit` onto the record and SAVES IT BEFORE it ends the
   sessions (`DirectorDrain.cs`, the limit block: the state is written and saved, then each
   session is ended). When an end FAILS, the seat is left still running, with the problem string
   "It is still running" - and the record already says ended-at-limit. On the way up that seat is
   offered for reopening while it may be alive, and reopening starts a second agent on the SAME
   conversation id.
2. The reopen writes nothing to the record (the Developer says so plainly in proof decision 6).
   Reopening twice - a double click in the window phase, or the history opened on two screens -
   starts two live agents in one saved conversation, interleaving their turns into one transcript.

What it would do to a person: two agents running as one conversation, each acting on the other's
half-written work. That is worse than a duplicate blank session, because both believe they are the
same session.

How sure I am: the missing guard is certain - I read both paths. Whether the trigger occurs in
practice is medium: case 1 needs an end to fail at the limit and the Director to come back while
the session lives; case 2 needs only an impatient owner.

This is not disobedience: the Developer mandate told the Developer to build the reopen as a direct
create, and the Developer disclosed the write-nothing consequence. But "a seat that has already
come back, or that is still running, must never be started again" is a rule the mission holds
everywhere else, and this is the one path that escapes it. My recommendation: refuse to reopen a
seat the roster lists as running, and record or refuse a second reopen of the same seat. If that
needs a Gateway change, it is a decision for the Delivery Lead, not something I fix here.

### Finding 2 - Copilot and Cursor seats are told they will come back blank, when their drivers really do resume the saved conversation

`WayUpWords.Resumes` (`WayUpWords.cs` line 168) answers true only for Claude Code and Pi; every
other agent gets the fresh-session wording. But Copilot and Cursor are not unknown to this build:
`CopilotAgent.BuildLaunchSpec` and `CursorAgent.BuildLaunchSpec` both pass `--resume` with the id
(in `src/CcDirector.Core/Agents/`), and the reopen hands the conversation id over for every agent.
So the row for a Copilot seat says "Copilot cannot be started on a saved conversation, so this
opens a NEW, blank session ... with none of the conversation in it" - and that is false: the
conversation comes back.

What it would do to a person: the owner reads that the conversation is lost, and may not bother
reopening a seat whose work he could have had back intact; or reopens it and is surprised to find
the context there.

How sure I am: high that the drivers resume (I read them); medium that a record spells those agents
"Copilot" and "Cursor" exactly (the contract comment lists the spellings but I saw no live record).

The mandate named only Claude Code, Pi and Codex and said an agent nobody has heard of must fall to
the safe side - that rule is right and correctly built. But Copilot and Cursor are agents this
build HAS heard of: their resume support is in the same tree, and the one method that decides the
wording should know them. The fix is one line in `Resumes` and one in `AgentName`, plus a test
each.

### Finding 3 - a record whose seats all ended without a handover promises a reopen that the answers cannot deliver

Three linked facts:

- `IsOfferable` requires at least one seat decided restore, so a record whose every seat ended at
  the limit is never offered at start-up. That follows the mission's own words (section 5.3 item
  10), so it is not a defect by itself.
- In the history, each such seat says "Its saved conversation can be reopened"
  (`WayUpWords.cs` lines 256 and 258) - but the history entry carries NO offer object
  (`DirectorWayUp.cs` line 331: the offer is null when the record is not offerable). The reopen
  WORDS (which agent, what will really arrive) live only inside `WayUpRow.Reopen`, which the
  history never sees.
- So a window that wanted to put a reopen button on that history row would have to invent the
  wording itself, because the engine gave it none. That is exactly what critical rule 7 forbids:
  an empty string where a sentence belongs.

What it would do to a person: the history tells him the conversation can be reopened, and gives him
no honest way to do it. The dumb client rule forces the window to guess, and a guess about what
reopening does is the exact defect the mission's wording rules exist to prevent.

Forward risk in the same shape: the operating-system-shutdown record (ruling 10.5) is not built
yet - both `ShutDownIgnoringAllAsync` and `RecordAndLetEndAsync` throw "not built yet". When the
operating-system record IS built it will hold no seat decided restore (no handovers are written),
and by these rules it will never be offered at start-up at all, and its conversations will be
reachable only through this same history gap - while ruling 10.5 says "on the way up offers the
saved conversations as in 10.3". Whoever builds that record must either mark it so the way up
offers it, or this engine's rule must change. It should be settled before the window phase freezes
on this surface.

How sure I am: high on the code, medium on the product judgement - that is the Tech Lead's call,
not mine.

### Finding 4 - the bring back and the reopen accept any workspace id, with no check that the record belongs to this Director

`FindOfferAsync` and `ReadHistoryAsync` filter candidates by machine and Director name.
`BringBackAsync` (line 133) and `ReopenAsync` (line 193) read whatever workspace id they are
handed. The planned callers pass back only ids the engine itself offered, so this is defense in
depth rather than a live defect - but the Gateway's restore route refuses a record from a different
MACHINE and not one from a different Director on the same machine, so a future caller (the phase 4
command line, or a window defect) could bring another Director's sessions up onto this one.
Lowest rank for that reason, and worth one guard when a command line is built.

## 5. What I attacked and failed to break

So the next reader knows what my pass covers:

- **The presence check.** The engine's Gateway seam has three methods - list, read one, start one -
  and no way to ask about sessions at all, so a session count cannot reach the decision even by
  accident. Another Director's record is filtered out on the summary and never even read as a
  document (tested, and the test reads the read-list). A cancelled record, an ignore-all record and
  a fully-brought-back record are all not offered, each with its own test. The key is the display
  NAME, never the Director id, and a Director with no name refuses rather than matching on a blank.
- **The seed file.** I read the text the code really produces, not the test's copy of it. It holds
  exactly the four parts the mission allows. The only things interpolated into it are the handover
  path, the owner's own reason, the Director name and the time - every one of which section 5.3
  item 14 requires it to carry. No rule of conduct can enter the file from the code; the only
  carrier would be the owner's own reason text, which is his own words and is what the mission
  says must be carried. The two seed tests assert the WHOLE file against a literal, by presence -
  not by checking some banned phrase is absent - and the Developer's second mutation showed they
  catch a rule of conduct added to the source.
- **Bringing a seat back twice.** The order is built only from seats the fresh document still
  owes, rows that vanished are refused by name, nothing-ticked is refused (my own mutation proved
  that test can fail), and the restore's own guards - still running, once only, token before
  create - are handed an order that cannot say "everything". The one exception is the reopen path,
  which is Finding 1.
- **An ended-at-limit seat.** Unticked, its own row kind, never sent to the restore (the drain
  wrote it undecided, and the engine adds no second guard - it makes the restore's refusal
  visible, exactly as the mandate asked). The wording is decided by one method from the agent
  alone, and an agent nobody has heard of falls to the fresh-session side, safely - tested with an
  invented agent.
- **Refusal versus emptiness.** Three states on the offer, and a refused history carries its
  reason; an empty history and an unreachable Gateway are kept apart in words a window shows
  verbatim. Tested on all four entry points.
- **Not now.** Nothing in the engine writes to the record on any refusal or on "not now"; the
  record stays as it is and is offered again, which is what the mission wants.

## 6. Rules the code holds that no test holds

- A record captured on ANOTHER MACHINE is filtered out (the tests cover only another Director on
  the same machine).
- A reporting chain that loops (the code is safe - it stops at a seen seat - but no test pins it).
- The shutdown-time precedence when `CompletedAtUtc` is missing: a smart shutdown that never
  finished still has `ShutdownKind` set from its first save, so it CAN be offered; nothing tests
  what the "when" label says for it.
- Case-insensitive matching of the Director name (`OrdinalIgnoreCase`) is untested.

## 7. What I could not check

- **No real Gateway, Director or session was ever run.** Everything is against fakes of the two
  seams, by design. That a real Gateway answers these calls as the fakes do is unproven here, and
  it was not this phase's job to prove it.
- **That the Director's display name from `NamedInstanceRegistry` equals the `DirectorName` the
  Gateway stamps on a record.** The Developer admits the same gap. The path is plausible - the
  capture stamps the Director's display name and the registry holds the same one - but the two
  were never observed agreeing on a live Gateway. If they disagree, no record is offered at all,
  never the wrong one.
- **Whether a reopened conversation really comes back with its context** for Claude Code or Pi.
  That is the rig Developer's measurement, running separately.
- **`ControlApiHost.CreateDirectorWayUp()` has no test** - a factory of four arguments. Its
  arguments are the risk, not its behaviour.
- **The parked suites, the web tests and the Python tests were not run**, and neither was the full
  local gate; I ran only the mission's filtered check. Nothing in the diff touches the Gateway, the
  contracts, the browser shells or the Python toolbelt, so I have no reason to expect a red there,
  but I did not look.
- One stale comment I noticed on the way, pre-existing and NOT this branch's change:
  `NewSessionRequest.ResumeSessionId` says resume is "ignored ... (e.g. Pi)", but `PiAgent` passes
  the session id and resumes with it. The engine's Pi wording depends on the newer behaviour, which
  I verified in the agent code. The comment should be corrected by whoever next touches that
  contract, not by this pull request.

## 8. Verdict

The engine does what its mandate says, on the reading and on the runs: the presence check is on
the record and provably never on sessions, the seed says the four things and is pinned whole, the
restore receives an order that cannot defeat its guards, and every refusal carries its reason.
Findings 1 to 3 deserve answers before the window phase builds against this surface - finding 1
most of all, because it is the one path in the change that can start a session the mission
promises is never started twice.

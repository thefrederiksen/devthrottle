# Mandate - Smart Director Restart, phase 3, Developer: answer the four findings on the way up engine

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-engine`, branch
`smart-restart/p3-way-up-engine`, already carrying the engine and its 44 tests at commit
`0cda45788` (pull request #3202, not merged). You did not write it; another Developer did, and a
Reviewer on a different agent then found four things. Work only in that worktree. Never work in
`D:/ReposFred/devthrottle`.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH EVERYTHING INSIDE ONE TURN: build it, run the check, commit, push,
   and WRITE YOUR ANSWER FILE - before your turn ends. If you stop half way to ask something,
   nothing will ever answer you. If you cannot finish, write how far you got into the answer file,
   commit and push that, and then stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

1. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1.md` - the review. It is the
   reason you exist. Read all of it, including section 5, which says what the Reviewer attacked and
   FAILED to break: do not undo any of that.
2. `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-engine.md` - what the first
   Developer built and, in section 5, every decision it made. Keep those decisions unless a finding
   below overturns one.
3. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-engine.md` - the rules
   the engine was built to. They still bind you.
4. The code in `src/CcDirector.ControlApi/SmartRestart/`.

## The four findings, and the Tech Lead's ruling on each

All four are ACCEPTED. Answer every one. Where a ruling narrows the fix, build exactly the narrowed
fix and do not go further.

### Finding 1 - the reopen can start a session that is still running, and can reopen the same saved conversation twice. ACCEPTED, fix it in two parts.

`ReopenAsync` starts a session for a seat with no check at all, while every seat the RESTORE brings
back is guarded. Two live agents sharing one conversation, each acting on the other half-written
work, is worse than any duplicate blank session.

**Part one - refuse a seat whose captured session may still be running.** Use the guard the product
already has rather than writing a second one: `DirectorRestore.StillRunning(seat, roster)` is
`internal static` and returns the reason in plain words, or null. Add the roster to the engine
Gateway seam (`IWayUpGateway`) exactly as `IRestoreGateway.GetRosterAsync` does it - it returns the
envelope WITH per-Director reachability, never the plain list, because "not on the list" is the fact
being acted on. Refuse the reopen with that reason. Do not re-implement the rule.

**Part two - one reopen per seat while this Director is up.** Keep a once-only guard inside the
engine, keyed on the workspace id and the seat session id, so a double click or two screens cannot
start two. A second attempt is refused with a plain sentence saying it was already reopened, and
that sentence is a word of the engine, not of a window.

**What you must NOT do, and must state instead.** Do not write the reopen onto the record. Marking a
seat needs the restore lease and a workspace write, and a new mark kind would be a change to
`CcDirector.Gateway.Contracts` and a Gateway deploy - which is the Delivery Lead decision, not
yours. So say plainly, in your answer file and in the code comment: **across a Director restart the
same seat can still be reopened twice, because nothing is recorded**; the in-process guard covers one
run of one Director. That limit is disclosed, not hidden, and it is the Delivery Lead to close.

### Finding 2 - Copilot and Cursor really DO resume, and are being told they will come back blank. ACCEPTED, and fix the CAUSE, not the list.

`WayUpWords.Resumes` keeps a hand-written list of agent names. `CopilotAgent` and `CursorAgent` both
pass `--resume` with the id, so the engine tells the owner his conversation is lost when it is not.
A hand-kept list in a second place is the defect; lengthening it would only set up the next drift.

**Put the fact where the agent is defined.** Add one flag to `AgentPluginLaunchMetadata`
(`src/CcDirector.Core/AgentPlugins/AgentPluginMetadata.cs`) saying whether that agent can be started
on a saved conversation. Make it a REQUIRED part of the record, so that every plugin has to state it
and a new agent cannot get one by silence. Set each from what its own driver really does - read the
driver, do not guess:

- `ClaudeAgent` / `ClaudeDriver`: appends `--resume <id>`;
- `PiAgent`: appends `--session-id <id>` and its comment says a second launch with the same id
  recalled the first conversation;
- `CopilotAgent`: appends `--resume <id>`;
- `CursorAgent`: appends `--resume="<id>"`;
- `CodexDriver`, `GeminiAgent`, `GrokAgent`, `OpenCodeAgent`: each LOGS that it is ignoring the id.

Then have `WayUpWords` read that flag through the registry instead of its own list, and keep the
existing behaviour for an agent the registry does not know at all: it falls to the fresh-session
wording, the safe side. That rule is right and the Reviewer confirmed it; keep it.

**The test that stops this ever drifting again.** One test that walks EVERY registered agent, builds
its launch spec with a known conversation id, and asserts the new flag equals whether that id really
appears in the arguments the agent built. That is a presence check over the real drivers, not a list
somebody has to remember to update, and it fails the day an agent gains or loses resume without its
flag moving. Assert first that the walk found more than a handful of agents, so a registry that
returned nothing cannot pass as a clean run.

### Finding 3 - the history says a conversation can be reopened and gives no way to do it. ACCEPTED.

A history entry carries no offer when the record is not offerable, so the reopen wording - which
agent, what will really arrive - is missing exactly where the history tells the owner it can be
reopened. A window would have to invent the sentence, and inventing a sentence about what a button
does is precisely what critical rule 7 forbids.

**Every ended-without-a-handover seat carries its reopen offer wherever it is shown**, in the
start-up answer and in the history alike, whether or not the record still owes a seat that can come
back. A record that owes nothing but holds saved conversations still offers those conversations in
the history. Do NOT change the start-up presence check: a record with no seat decided restore is
still not OFFERED at start-up - the mission says so in section 5.3 item 10 - it is simply readable,
with working buttons, in the history.

Test it: a record whose every seat ended at the limit appears in the history with a real reopen offer
per seat, and is still not offered at start-up.

### Finding 4 - bring back and reopen accept any workspace id. ACCEPTED, one guard.

Both take whatever workspace id they are handed. Today only the engine own answers supply one, so it
is defense in depth - but the phase 4 command line is a second caller, and the Gateway restore route
refuses another MACHINE record and not another Director record on the same machine.

Before acting, check that the named record is one this Director may act on - the same machine and the
same Director name the two read paths already require - and refuse by name with a plain sentence if
it is not. One rule, in one place, used by both.

## What is NOT yours

- The Reviewer raised a forward risk in finding 3 about the operating-system shutdown record
  (mission ruling 10.5), which is not built yet. **Do not build it and do not design for it.** The
  Tech Lead has put it to the Delivery Lead.
- The stale comment on `NewSessionRequest.ResumeSessionId` saying resume is ignored by Pi. True, and
  not this pull request. Leave it.
- The intermittent `SessionStateEventEmitterTests` red. Not yours.
- No change to the Gateway, to `WorkspaceStore`, to `WorkspaceValidation` or to any contract in
  `CcDirector.Gateway.Contracts`. `AgentPluginMetadata` is in `CcDirector.Core` and is allowed by
  finding 2; nothing else widens.
- No window, no XAML, no change to `src/CcDirector.Avalonia`. Another Developer is building the
  windows on this branch head right now, so keep the published names in `IDirectorWayUp` and
  `WayUpWords` STABLE where you can. Where a name or a shape must change, say so clearly at the top
  of your answer file - the Tech Lead carries it to that Developer.

## The check you run, and what it must say

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Read the COUNT, never the colour. On this branch head it is **567 total, 567 passed, 0 failed**,
measured by the Tech Lead and again by the Reviewer. Your run must be risen by the tests you add,
with no new failure.

Finding 2 touches `src/CcDirector.Core/AgentPlugins`, which the filtered check does not cover. So
ALSO run, and write both counts down:

    dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin|FullyQualifiedName~Agent"

Measure that one on the UNTOUCHED tree first, before you change anything, or its after-number means
nothing.

**Prove your own tests can fail**, on the code you add, not on the code that was already there: break
one line of your own fix for finding 1 and one of your own fix for finding 2, show the exact red
counts, put them back with `git checkout --`, and show green again on a FULL build - never a no-build
run on the restore run, because a no-build run certifies the binary and not the source. Commit your
work BEFORE you make the mutation so that putting the code back cannot eat it.

## When it is done

1. Run the checks. Write the counts down.
2. Commit, plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
   with", no agent or vendor name. ASCII only everywhere: no Unicode, no emoji, no arrows, no tick
   marks.
3. Push the branch. The pull request #3202 already exists and will pick your commits up; do not open
   a second one and do not merge.
4. Write `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1-answers.md` on the same
   branch, one section per finding: what you changed, which test now holds it, the counts, your
   revert proof numbers, and anything you decided along the way. Where you did NOT fully fix
   something - finding 1 across a restart - say exactly what is still open and why.
5. Then your turn may end.

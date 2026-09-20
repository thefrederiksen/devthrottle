# Mandate - Smart Director Restart, phase 3, Developer: the way up ENGINE

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-engine`, branch
`smart-restart/p3-way-up-engine`, cut from `origin/main` = `ab2770c4a`. Work only there. Never work
in `D:/ReposFred/devthrottle`.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). So FINISH EVERYTHING INSIDE ONE TURN: build it, run the checks, commit,
   push, open the pull request, and WRITE YOUR PROOF FILE - before your turn ends. If you stop half
   way to ask something, nothing will ever answer you. A second round is a fresh session on a new
   mandate. If you genuinely cannot finish, write how far you got into
   `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-engine.md`, commit and push that,
   and then stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

- `docs/missions/smart-director-restart-2026-09-19/mission.md` - all of it. Section 5.3 items 10, 11
  and 14, and rulings 10.2, 10.3 and 10.5, are what you are building.
- `docs/missions/smart-director-restart-2026-09-19/phase-1-interface.md` - how phase 1 wrote its
  interface for phase 2. Yours is read the same way by the windows that come after you.
- `docs/CodingStyle.md`.
- The code: `src/CcDirector.ControlApi/Drain/DirectorRestore.cs` (the restore you call, do not
  rewrite), `src/CcDirector.Gateway.Contracts/WorkspaceDtos.cs` (the record),
  `src/CcDirector.ControlApi/SmartRestart/` (phase 1's engine, whose shape yours follows),
  `src/CcDirector.ControlApi/ControlApiHost.cs` (`CreateSmartShutdown`, `ListWorkspacesAsync`,
  `GetWorkspaceAsync`, `DirectorId`).

## What you build

ONE new engine, in `src/CcDirector.ControlApi`, namespace `CcDirector.ControlApi.SmartRestart`, with
NO window and no user interface of any kind. A window is built by the next Developer against exactly
the names you publish. Follow phase 1's pattern: an interface, a real implementation behind a seam
that can be tested with no Gateway, and every word the user will read computed HERE, never in the
window (critical rule 7 in `CLAUDE.md`: the client is dumb).

### 1. Find the offer - the way up presence check

At start-up, once connected, the Director asks which records it may offer. The rule, exactly:

- list the workspaces (`ControlApiHost.ListWorkspacesAsync`), keep those whose `Origin` is
  `captured`, whose `Machine` equals this machine and whose `DirectorName` equals this Director
  display name. **Never match on `DirectorId`**: a restarted Director gets a new one, which is the
  whole reason the name is the key. A record belonging to ANOTHER Director on the same machine is
  therefore not offered;
- read the document of each survivor, newest first (`GetWorkspaceAsync`);
- offer one only if `ShutdownKind` is `WorkspaceShutdownKinds.SmartShutdown`, `CancelledAtUtc` is
  null, and it holds AT LEAST ONE seat whose `Restore.Decision` is `restore` and whose
  `RestoredSessionId` is empty. That is a PRESENCE check on the record. It is NEVER "no sessions are
  running" - a Director with sessions running may still hold a record worth offering, and a Director
  with none may hold nothing.
- a record from `ignore-all`, a cancelled record, and a record whose every owed seat has already
  come back are all silently not offered. They stay readable in the history.

Cap the number of documents you read at the newest 25 candidates and say so in the type comment, so
a Director with years of records does not make 200 calls at start-up. The history (below) reads the
same capped list.

The answer is one immutable record holding: when the shutdown was, how many seats are owed, and ONE
ROW PER MISSION HEAD, leads first, each row carrying the seats under it. A mission head is a seat
with no `ReportsTo` inside this record, or whose `ReportsTo` names a seat that is not in it; the
seats under it are the ones that report up to it, however deep. Every row carries a tick box state,
and all of them start TICKED (ruling 10.2: asked once for the lot, not per session), EXCEPT the rows
described next.

### 2. A seat ended without a handover (ruling 10.3)

A seat whose `DrainState` is `WorkspaceDrainStates.EndedAtLimit`, and any seat that never answered,
is NOT brought back automatically. It appears in the answer as its own kind of row - "ended without
a handover" in those plain words, computed here - UNTICKED, and carrying one offer: reopen its saved
conversation.

Note, and put it in the type comment: `DirectorDrain` writes such a seat with
`Restore.Decision = undecided`, so `DirectorRestore.SelectTargets` already refuses to bring it back.
You are making that visible, not adding a new guard on top of it.

Whether the reopen works is being measured on the isolated rig by another Developer, at the same
time as you. Build what the CODE supports and say plainly what is unproven:

- Claude Code: `ClaudeDriver` passes `--resume <id>`;
- Pi: `PiAgent` passes `--session-id <id>`;
- Codex: `CodexDriver` logs "ignoring resume" and starts fresh. So for a Codex seat the offer is
  honestly worded - a fresh, blank session in the seat repository, with no conversation - and your
  word for it says that. Do NOT build a second way round it.

Decide the wording from the AGENT of the seat, through one method that any future agent falls into
safely, and make the unknown agent behave like Codex (fresh session, said plainly) rather than like
Claude.

The reopen itself creates a session in the seat repository with
`NewSessionRequest.ResumeSessionId = seat.ClaudeSessionId` and a first-line prompt telling it that
**it was stopped when the Director shut down, and it must re-check the state of its work before
acting**. A seat with no `ClaudeSessionId` recorded cannot be reopened at all: say so on the row, in
plain words, and offer nothing.

### 3. The history (5.3 item 11)

Every record for THIS Director (same machine and Director name), newest first, whatever its
`ShutdownKind` and whether or not it was cancelled: when it was, why (the record `Reason`), what
kind of shutdown it was, and per seat what came back and what did not, in plain words computed here.
A record that still owes seats can be brought back from the history later - so the history rows
carry the same offer the start-up one does.

### 4. Bring back

Goes through the EXISTING restore, `DirectorRestore`, under the real owner, leads first. You do not
re-implement any of that: `DirectorRestore.PrepareAsync` and `RunAsync` already order seniors first,
resolve each seat owner and refuse a seat that is still running. Build the
`WorkspaceRestoreOrder` from the rows the caller ticked - naming the seat ids of every ticked row and
the seats under it - and hand it over.

**The seed file (5.3 item 14, ruling 10.6).** Before the restore runs, write one seed file per seat
and pass it in `WorkspaceRestoreOrder.Seeds`. It says ONLY these four things and NOTHING else:

- you are a restored session;
- read this document (its handover, named by its path);
- what changed while you were gone (the plain facts the record holds: the Director was shut down at
  such a time, for this reason, and has restarted);
- verify the state of your work before acting.

**No rules of conduct.** Not "no commits unless the owner asks", not a preamble, not a style note,
not a reminder about tests. A seed line of that kind overrode a handover own plan on 19 September
and that is why this rule exists. A test must prove the seed file holds none of them - and prove it
by PRESENCE, not by absence: assert the file content equals the four parts you built, rather than
asserting some banned phrase is missing.

Where the seed files go: beside the handovers, under the same drain folder the record already names
(`DrainPaths`), so a restored session can read both.

### 5. Not now

"Not now" writes NOTHING and touches nothing; the record simply stays as it is, which means it is
offered again the next time this Director starts. That is deliberate and it is the owner reason for
having a history at all ("it could be that I accidentally don't restart it right away and I want to
restart it later"). Your engine needs no verb for it; make sure nothing you write makes it impossible.

## What you may NOT do

- No change to the Gateway, to `WorkspaceStore`, to `WorkspaceValidation` or to any contract in
  `CcDirector.Gateway.Contracts`. Everything you need is already there. If you believe it is not,
  STOP and write that into your proof file instead of changing it - a Gateway change needs a deploy
  and that is the Delivery Lead decision, not yours.
- No change to `DirectorRestore`, `DirectorDrain` or the phase 1 `SmartRestart` files.
- No window, no XAML, no change to `src/CcDirector.Avalonia`.
- No fallbacks. A Gateway that cannot be reached is a refusal with the reason in plain words, never
  an empty list - an empty list reads as "you have no records" and that is a lie (CLAUDE.md rule 3).

## The check you run, and what it must say

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Read the COUNT, never the colour: a filter that matches nothing exits green. The baseline the Tech
Lead measured on untouched `origin/main` = `ab2770c4a` is **523 total, 522 passed, 1 failed**. The one
red is `SessionStateEventEmitterTests.A_restarted_session_after_exit_re_emits_its_first_state`, which
fails on untouched main with a SQLite error in `DeviceRegistry` and passes when run alone. It is not
yours, it matched the filter only because "restarted" is in its name, and you neither fix it nor count
it. Your run must show the total RISEN by the number of tests you wrote and no new failure.

Put your tests in `src/CcDirector.Gateway.UnitTests/Restart/` so the filter picks them up, beside the
tests already there. Build the Gateway seam as a fake, the way the Drain tests do - no real Gateway.

**Prove your tests can fail.** Pick two of them, break the code they cover on purpose (one line),
show them go red with the exact counts, put the code back, and show them green again with a FULL
build - never a no-build run on the restore run, because a no-build run certifies the binary and not
the source. Commit your fix BEFORE you make the mutation, so that putting the code back cannot eat it.

## What you must cover with tests, at least

- a record for another Director on the same machine is not offered;
- a record already fully brought back is not offered;
- a cancelled record is not offered, and an ignore-all record is not offered;
- a record with one owed seat IS offered, while sessions are running on this Director - the check is
  the record presence and never the session count;
- rows: leads first, each lead carrying its own seats, all ticked;
- an ended-at-limit seat is its own row, unticked, with the right offer for its agent - one test each
  for Claude Code, Pi, Codex and an agent you invent that nothing knows, which must behave like Codex;
- a seat with no conversation id says so and offers nothing;
- the seed file content is exactly the four parts, asserted whole;
- bring back builds an order naming every ticked row seats and hands it to the restore;
- the Gateway unreachable: a refusal with the reason, never an empty list.

## When it is built

1. Run the check. Write the counts down.
2. Commit, in plain English, no abbreviations, and **sign nothing**: no "Co-authored-by", no
   "Generated with", no agent or vendor name, anywhere. ASCII only, everywhere, in code,
   comments, commit messages and documents - no Unicode, no emoji, no arrows, no tick marks.
3. Push the branch and open a pull request against `main` that says what it does in plain words and
   names the mission issue #3167. Do not merge it yourself and do not wait for the hosted checks.
4. Write `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-engine.md` and commit it on
   the same branch: the exact commands you ran, the counts before and after, the revert proof with
   its numbers, what each new test proves in plain words, every decision you had to make, and what
   you could NOT reach. Name the pull request number.
5. Then your turn may end. The Tech Lead reads the file, runs the check itself, sends it to a
   Reviewer and merges it.

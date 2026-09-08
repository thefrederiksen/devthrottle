# Phase A handoff - Seats 1 and 2: the Director ends it, and the command line asks

**You are the Manager for Phase A.** Read `missions/stop-a-session.html` (the work and the six
rulings), `missions/stop-a-session/architect-state.md` (the code facts, and the route decision), and
`cc-devthrottle workflow instructions mission` (the conduct). This note is the phase.

- Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Work there
  and nowhere else. Commit and push as you go - work that exists in one place is one crash from lost.
- **You do not merge to main.** The Architect lands everything. Push the branch; that is your done.
- Hire Workers as you need them. Kill them when their task is finished.
- Do not reopen a ruling. If one is wrong, come back to the Architect and it changes in one place.

## Why Seats 1 and 2 are one phase

Seat 1 on its own lands nothing an operator can see and nothing that can be proved except through
Seat 2's route. Splitting them would produce a pull request that changes a return shape nobody reads.
Together they are one coherent vertical slice - the Director's honest answer, the route that carries
it, and the command that asks - and that is one reviewable pull request. Seat 3 stays separate.

## What "done" means for this phase

`cc-devthrottle session stop <session> --reason "why"` ends a real session on this fleet and prints
an answer that says what actually happened to it. Plus `cc-devthrottle session done --undo`.

## The outcome contract - build to this exactly

This is the shape every surface later renders, so it is written here once rather than discovered
three times. Fold the sentences ON THE GATEWAY. No client composes one.

**The Director's `kill` verb answer** (extend it; keep `killed` and `removed` exactly as they are,
because the existing `DELETE /sessions/{sid}` callers read them):

    killed                          bool    - unchanged
    removed                         bool    - unchanged
    processId                       int?    - the agent process id found before the stop
    processEnded                    bool    - a live process WAS found and this stop ended it
    rowRemoved                      bool    - a row WAS present and this stop removed it
    worktreePath                    string? - the worktree or repository the session held
    worktreeHadUncommittedChanges   bool?   - null means it could not be determined, and null
                                              must never be reported as "clean"
    verdict                         string  - "stopped" or "alreadyStopped"

`alreadyStopped` is the case where no live process was found. If a row was still there, it is still
removed and `rowRemoved` is true - that is the reconciling Ruling 3 asks for.

**The Gateway's `POST /sessions/{sid}/stop` answer** - the same facts, plus the finished words:

    verdict     "stopped" | "alreadyStopped" | "notOnFleet"
    headline    the one line an operator reads, for example
                  stopped 9c41e7a2 - process 51884 ended, row removed
                  already stopped 9c41e7a2 - no process was running; the row it left behind
                    has been cleared
                  not on this fleet - nothing in this account carries the id 9c41e7a2, so no
                    machine was asked and no machine's processes were searched
    details     zero or more further lines, in order:
                  the worktree line, when the session held one (Ruling 2 requires it):
                    "the worktree C:\Repos\thing was left untouched - it has uncommitted changes
                     in it" / "... - it had no uncommitted changes" / "... - whether it has
                     uncommitted changes could not be determined"
                  the reason line: "reason: <what the caller said>"
    plus        sessionId, shortId, processId, processEnded, rowRemoved, worktreePath,
                worktreeHadUncommittedChanges, reason, stoppedBy

## The seven things this phase builds

1. **The Director's honest answer.** Extend the `kill` verb
   (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`) to the contract above. It must distinguish
   "there was a live process and I ended it" from "there was nothing running" - today it cannot,
   because the kill is best-effort and swallows the difference. Find the worktree the session held
   and whether it has uncommitted changes; the repository status services under
   `src/CcDirector.Core/Git/` already know how to answer that, so use one rather than shelling out.

2. **`POST /sessions/{sid}/stop` on the Gateway,** body `{ "reason": "..." }`. It forwards the `kill`
   verb and folds the answer. **`DELETE /sessions/{sid}` becomes a thin forward into the same
   handler** so there is one stop behind two doors - see the state note for why the DELETE is kept.

3. **The refusal.** A stop with no reason, or a blank one, is refused with 400 and a body that says
   plainly that the reason is what is missing. The Gateway says the fact and the human sentence; the
   command line prints that sentence and adds the one thing only it knows - how to type it
   (the `--reason` flag). Naming its own flag is not a client deciding what a state means.

4. **`notOnFleet` is a SUCCESS, not a 404.** When no session in the account carries that id, answer
   200 with `verdict: "notOnFleet"`. Do not route it through `SessionUnavailable`, which answers 404
   and would make the second stop error - the exact failure Ruling 3 exists to prevent. The headline
   must say that no machine was asked, because claiming "it is gone" when nothing looked is the worse
   of the two mistakes.

5. **The allow list** (`src/CcDirector.Gateway/Util/SessionKeyGuard.cs`): add `"stop"` to the
   three-segment `POST /sessions/{sid}/*` switch, and add `DELETE /sessions/{sid}/request-deletion`
   so a flag can be cleared. **Leave `DELETE /sessions/{sid}` refused to session keys** - that is what
   keeps the owner's ruling exact: an agent's key can only ever stop with a reason attached. Update
   the class comment to match what the list now contains; a comment that no longer describes the list
   is worse than a wrong entry, and that file says exactly that about itself.

6. **The audit record.** Record the stop and its reason in the existing append-only governance audit
   (`GovernanceAuditLog`, category `intervention`), actor = who stopped it, note = the reason. The
   owner accepted "any session may stop any other" on the explicit ground that it is audited, so this
   is load-bearing, not decoration. If the category or event type will not take it, extend the
   validated list deliberately - do not quietly write it somewhere else.

7. **The two commands** in `tools/cc-devthrottle/src/session_ops.py` and `cli.py`:
   - `session stop <session> --reason "why"` (`-r` as well). Prints the headline, then each detail
     line. Exit 0 for all three verdicts. Non-zero only for: no reason, Director unreachable, process
     would not die - and each says which one it was.
   - `session done --undo [<session>]` clears a pending deletion through
     `DELETE /sessions/{sid}/request-deletion`. No reason required - Ruling 4 asks for a reason
     because stopping is destructive, and this is the safe direction.

## Testing

Follow the repository's rules: `.\scripts\test-local.ps1` green before you report, and remember what
it does NOT cover - it runs no Python tests, and `Gateway.Tests` and `Core.Tests` are parked. **This
phase touches the Python command line and the Gateway, so you must run the Python tests yourself and
run `-Parked`.** A green default run would say nothing about most of what you wrote.

Every state gets a test, and **every test is watched failing on purpose** before you believe it:
revert the change, see it go red with the reported symptom, restore. Name in the code the gaps you
did not cover, as gaps.

The cases that matter: a live process stopped; a row with no process (already stopped, row cleared);
no session at all (not on this fleet, exit 0); a second stop straight after the first; a stop with no
reason; a stop with a blank reason; a session holding a dirty worktree; a session holding a clean
one; a worktree whose state cannot be determined; and the allow list - `stop` permitted, the bare
`DELETE /sessions/{sid}` still refused to a session key.

## Report to the Architect when this is pushed

One message, one line, pointing at a file: `missions/stop-a-session/phase-a-report.md`. Write into
that file what you built, what you proved and how, what you did NOT prove, and anything you found
that contradicts the rulings or the state note. Do not narrate progress at the Architect while you
work - a message interrupts the session that receives it.

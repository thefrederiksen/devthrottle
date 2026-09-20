# Mandate - Smart Director Restart - Developer, phase 1, task 3b: answer the review of the smart shutdown run

You are a Developer on the Smart Director Restart mission. You were opened by the Tech Lead of phase 1
(session f42438de, the third seat) and you report to it, never to the owner and never to the fleet. You
have no transcript; this file is your history. The Developer that built task 3 is gone, so under law 11
of the method its review findings come to you, a fresh Developer.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p1-engine`, on branch `smart-restart/p1-engine`,
three commits on top of `origin/main`. Work only there. Never touch `D:/ReposFred/devthrottle`.

## THE RULE THAT DECIDES WHETHER YOUR WORK COUNTS

You get ONE turn. On this Director a session that has stopped is never woken: no message, no report and
no answer reaches it. So finish EVERYTHING - the code, the tests, the answers file, the commit and the
PUSH - before your turn ends. The Tech Lead is polling `origin/smart-restart/p1-engine` for the pushed
answers file, not for a message. Never end your turn to wait for anything. If something is undecidable,
`cc-devthrottle session raise "<the question, with your recommendation>"`, then BUILD YOUR
RECOMMENDATION, carry on, and write the decision into the answers file.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. In `docs/missions/smart-director-restart-2026-09-19/` of your worktree: `review-phase-1-3.md` (the
   review you answer; it is untracked there, and you commit it unchanged), `proof-phase-1-task-3.md`,
   and `phase-1-interface.md`.
3. `docs/CodingStyle.md`.

## Your one task

Answer every finding of `review-phase-1-3.md` in a new file beside it,
`review-phase-1-3-answers.md`: accepted or declined, each with the reason. The Tech Lead's view, which
you may overrule with a reason:

- **Finding 1 (the engine keeps the Gateway client it had when it was made): ACCEPT, fix it.** The
  reachability question must read the host's CURRENT Gateway client each time it is asked, the same
  vintage `CreateDrain()` reads, so one engine never holds two ages of one fact. The review names the
  wrapper at `ControlApiHost.cs` line 182 that already reads the field at call time. Write a test that
  watches the HOST: an engine made while the host has no client, or has an old one, answers from the
  client the host has NOW. It must go red if the capture comes back. If the host cannot be driven to
  change its client inside a unit test, say exactly why in the answers file and test as close to the
  host as you can reach; do not hand-build the thing under test.
- **Finding 2 (a `Changed` handler that blocks stalls the whole run): ACCEPT, on the contract side.** The
  engine keeps raising snapshots in order on its own thread; moving handlers to another thread would
  buy ordering problems to guard against a caller's mistake. What is missing is the sentence. Put it in
  two places, in the same words: the documentation comment on `ISmartShutdownRun.Changed`, and
  `phase-1-interface.md` section 3 where it says the event is raised on an engine thread. The sentence
  must say: the handler must return at once; it dispatches to the user interface thread ASYNCHRONOUSLY
  (post, never a synchronous invoke); a handler that blocks stalls the run, the two thirds stage, the
  limit and the "Shut down now" button, and holds the one-run gate so no later smart shutdown can start.
  No code change to the engine for this finding. If you judge the engine must change instead, say why in
  the answers file and build that.
- **The one comment the review names under its point 11** (`FlagEligibleAsync`: "that is the mechanical
  shape of never forcing - not a rule, a missing verb"): make it say what is now true - it holds for the
  flagging on both paths, and the smart shutdown does hold a stronger verb, used only at the limit.
- The review's other notes (sessions that appeared after the capture; the two transient row states; the
  null-forgiving operator matching the existing file) were weighed by the Reviewer and not counted as
  findings. Record each in the answers file as "noted, no change" with one line of reason.

If you find yourself editing an EXISTING test to make it pass, stop: that is old behaviour changing.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"

The Tech Lead's own run on `2d2abd541`: 517 passed, 0 failed. Run it yourself first on the untouched
worktree, then after your change, built from source in the same command, never `--no-build`. Read the
COUNT, not the colour. Foreground only, never piped through `tail` or `head`. Also build
`src/CcDirector.Avalonia`, because the host changed. Do one revert proof on your new test: commit first,
mutate, REBUILD, see red, restore with `git checkout -- <literal path>`, REBUILD, see green.

## What you deliver, all pushed before your turn ends

1. The fix, the new test, the two sentences, the comment - committed.
2. `review-phase-1-3.md` committed unchanged, and `review-phase-1-3-answers.md` beside it.
3. A short section appended to `proof-phase-1-task-3.md`: "After the review" - the counts before and
   after, the new test in one plain sentence, the revert proof, what you could not reach.
4. `git push origin smart-restart/p1-engine`. Do NOT open a pull request. Do NOT rebase or force push.
5. Last of all: `cc-devthrottle session report "<one paragraph>"`.

## Rules

- No fallback programming. Every public method logs entry, exit and errors with `FileLog.Write`.
  Try-catch at entry points only.
- Nothing in the background. No sub-agents inside your session.
- A safety hook on this machine stops any shell command that runs `rm` (or any delete) on a path built
  from a variable, such as `rm $folder/$file`, and asks the owner. Nobody but the owner can answer, so
  the seat hangs for good - it ended the first Tech Lead of this phase. Never write such a command.
  Delete files by LITERAL path, one per `rm`. Never leave a stash behind.
- Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any
  commit, file or comment. ASCII only in everything. Plain English, no abbreviations.
- Never destroy, deploy, or send anything outward. Never restart, drain, stop or message a session
  that is not yours. Never run any of this against a real Director; tests only.

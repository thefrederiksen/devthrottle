# Mandate - Smart Director Restart, phase 3, Developer: answer review 2 on the way up engine

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-engine`, branch
`smart-restart/p3-way-up-engine`, at commit `238d815f6` (pull request #3202, not merged). Work only
there. Never work in `D:/ReposFred/devthrottle`.

**This is a SMALL task.** Four things, none of them large. Do them well and stop.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH INSIDE ONE TURN: build, run the checks, commit, push, and write your
   answer file before your turn ends. Nothing can answer a question you stop to ask.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Read first

1. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-2.md` - the review you are
   answering. Read section 6 too, what it attacked and FAILED to break: do not undo any of it.
2. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1-answers.md` - what the previous
   Developer did and why. Its reasoning stands unless a ruling below overturns it.
3. `docs/missions/smart-director-restart-2026-09-19/review-phase-3-1.md` - the first review, for
   context.

## The four findings, and the Tech Lead's ruling on each

### Finding 1 - the once-only reopen guard is instance state, and the factory hands out a new engine every call. ACCEPTED. This is the one that matters.

`DirectorWayUp._reopened` is an instance field, `ControlApiHost.CreateDirectorWayUp()` builds a new
engine on every call, and the windows being built right now will hold more than one - the start-up
window and the history window each get their own. So the guard would silently do nothing, and the
roster check does not catch it either: a reopened session comes back under a NEW session id, so
moments after a reopen the roster still says nothing about the CAPTURED id and a second reopen sails
through. That is two live agents in one saved conversation, which is the exact harm finding 1 of the
first review exists to prevent.

**Do not fix this by telling the caller to hold one engine.** A guarantee that depends on how the
next caller constructs an object is a tripwire, and the next caller cannot see it.

**Fix it the way the product already fixes this exact problem.** `DirectorRestore` holds its
one-at-a-time rule in `private static readonly object Gate` and a static field, precisely because the
rule belongs to the DIRECTOR - one process - and not to whoever happened to build the object. Make
the reopen claim static in the same shape, under its own lock, and say in the comment that it belongs
to the process and not to the instance. The cross-restart gap is unchanged and stays disclosed.

Two things to get right while you are in there:

- **The test rig must not hide it.** `WayUpTestRig.WayUp()` builds a fresh engine per call, and the
  twice-test only passes today because it captures one instance in a local. Once the claim is static,
  that test must be able to pass with TWO SEPARATE ENGINES - change it to use two, because two
  engines is what the real windows will do. That is the test that proves the fix.
- **Static state is shared between tests in one run.** Give it a way for a test to start clean and
  use it, or key it so tests cannot collide - and make sure a test that passes alone still passes in
  the whole suite. A test that only passes when run under a filter is worse than no test: this phase
  has already found one of those in the windows.

### Finding 2 - the "never asks what is running" rule is now held by one assertion in one test, whose own comment says the opposite. ACCEPTED.

The first review's structural proof - the seam had no way to ask - is gone, because the ruling added
the roster. What holds the rule now is a single `Assert.Equal(0, rig.Gateway.RosterAsked)` on a single
path.

- **Fix the stale sentence** in `DirectorWayUpOfferTests` that still says the seam "carries no
  question about sessions at all". It is false now, and it tells the very reader who would notice the
  rule eroding that there is nothing to notice.
- **Assert the roster is not asked on the other read paths too**: the nothing-waiting path, the
  history, and the bring back. The start-up offer must be a promise about the RECORD, never a race
  with whatever happens to be running.

### Finding 3 - the interface does not say that a failed start keeps its claim. ACCEPTED, documentation only.

A reopen whose start FAILS consumes the seat's one claim until this Director restarts. That trade is
right and stays. But `IDirectorWayUp.ReopenAsync` is the document a window author reads, and it says
the once-only rule and the cross-restart gap without saying this third fact - so the author meets a
button that goes permanently dead after one transient failure and files it as a bug. Say it there, in
one sentence, in plain words.

### Finding 4 - a future agent could be worded as resuming for the wrong reason. ACCEPTED, a comment only.

The walking test proves the flag against whether the conversation id appears in the arguments. All
eight values are right today. If an agent ever carries that id for some OTHER purpose, the walk would
force its flag true and the offer would promise a conversation that does not come back - the one
direction the safe-side rule exists to prevent.

Put that warning IN THE WALK TEST, addressed to whoever meets it failing: do not flip the flag to
match the arguments; widen what the walk looks at. One short comment. Build nothing for it.

## What you may NOT do

- No change to the Gateway, to `WorkspaceStore`, to `WorkspaceValidation`, or to any contract in
  `CcDirector.Gateway.Contracts`.
- No change to `DirectorRestore`, `DirectorDrain` or the phase 1 `SmartRestart` files.
- No window, no XAML, nothing in `src/CcDirector.Avalonia`.
- **Keep the published surface stable.** `IDirectorWayUp`, `WayUpWords` and their records are being
  built against right now. If a name or a shape must change, say so at the TOP of your answer file so
  the Tech Lead can carry it.
- Do not re-open anything either review already settled.

## The checks, and what they must say

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin|FullyQualifiedName~Agent"
    dotnet build cc-director.sln

Read the COUNT, never the colour. An ABORTED run prints "Passed" for whatever finished before it
died, so check each run reached its end. On this commit the Tech Lead and the second Reviewer each
measured **580 total, 580 passed, 0 failed** and **247 total, 247 passed, 0 failed**, and the solution
built with 0 warnings and 0 errors.

**Because finding 1 makes the guard static, run the Gateway check TWICE IN A ROW** and show both
counts. Static state that leaks between tests shows up as a second run that differs from the first.

**Prove your own fix can fail**: break the static claim in one line, show the red count, put it back
with `git checkout --`, and show green again on a FULL build - never a no-build run on the restore
run. Commit first so the restore cannot eat your work.

## When it is done

1. Run the checks. Write every count down.
2. Commit, plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
   with", no agent or vendor name. ASCII only: no Unicode, no emoji, no arrows, no tick marks.
3. Push. Pull request #3202 already exists and picks your commits up. Do not open a second one and do
   not merge.
4. Write `docs/missions/smart-director-restart-2026-09-19/review-phase-3-2-answers.md` on the same
   branch: one section per finding, what you changed, which test holds it now, the counts, your
   revert proof numbers, and anything still open.
5. Then your turn may end.

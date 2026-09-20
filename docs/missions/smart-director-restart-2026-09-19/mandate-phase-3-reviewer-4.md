# Mandate - Smart Director Restart, phase 3, Reviewer 4: the Director knows a question box is open

You are a Reviewer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

You did not write this code and you run a different agent from the Developer that did.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-review4`, detached at commit
`1de2810f6`. Work only there. **You change no product code.** Your output is one file.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). Finish inside ONE turn and WRITE YOUR REVIEW FILE before your turn ends. A
   question you stop to ask is never answered; put it in the file.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## What this change is FOR - it decides what matters

The mission's own check requires that a session with a question box open is shown in the Smart
shutdown dialog BEFORE anything is asked, so the owner answers those first instead of having them
shut down mid-question. Phase 2 built that section of the dialog and then found it could never
appear: `Session.PendingInteraction` has a private setter and nothing in the product ever assigned
it, so it is always null.

This change fills it, from the agent's own TRANSCRIPT: a Claude Code call to `AskUserQuestion` or
`ExitPlanMode` with no matching tool result yet is a box on the user's screen.

**The direction of error that matters is a FALSE POSITIVE.** A question box reported where there is
none puts a session's name in front of the owner under "Answer these first?" and sends him looking
for a question nobody is asking - and, worse, could do it for many sessions at once. A missed one
just leaves things as they are today. Weigh your findings that way.

## What you are reviewing

Pull request **#3215**, branch `smart-restart/p3-question-box`, at `1de2810f6`.

    git diff origin/main...HEAD

Read, in this order:

1. `docs/missions/smart-director-restart-2026-09-19/mandate-phase-3-developer-question-box.md` - what
   the Developer was told. Something it was told NOT to do and did is a finding.
2. `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-question-box.md` - what it claims.
   **Every claim is a claim to disprove.** Read its sections 6 and 6a especially: it says plainly
   what it did not reach, and it checked its fixtures against six real transcripts on this machine.
3. The code: `PendingInteractionDetector.cs`, `PendingInteractionWatcher.cs`, the change to
   `Session.cs`, and the wiring in `ControlApiHost.cs`.
4. `WidgetBuilder.BuildFromMessages` and `StreamMessageParser` - the product's own pairing and parse,
   which this is supposed to reuse rather than duplicate.

## What to attack, in order of what it would cost to get wrong

1. **False positives.** Find any transcript shape that would report a question box when none is on
   screen. The Developer names two it reasoned about and could not test - subagent turns, and a
   transcript after compaction. **Go further than it did**: look at real transcripts on this machine
   if you can find them, and say what you found. An unfinished ordinary tool call must never count.
2. **Erasing a real question.** The reading is three-valued - pending, nothing pending, could not
   look - and only "nothing pending" clears the property. Check that an unreadable, missing or
   half-written transcript can never clear a question box that is genuinely open. Check the
   generation guard really stops a slow read from re-stamping a question the owner has just answered.
3. **Does it reuse the product's pairing, or is it a second copy that can drift?** A tool use is
   answered by the later tool result carrying the same tool use id. If this wrote its own version of
   that rule, say so.
4. **The trigger.** It rides `Session.OnActivityStateChanged` reaching `WaitingForInput`, plus one
   read when the watcher first sees a session. Can it throw into that event? Can it block it? Can it
   run twice at once on one session? This runs on every turn end of every session on the machine -
   a slow or throwing handler here hurts everything.
5. **The clearing, which is in TWO places.** `Session.SetActivityState` drops it when the session
   leaves the waiting states, and the watcher writes null at the next turn end. Can those two
   disagree? Can a session be left holding a stale question?
6. **Agent gating.** Only Claude Code. Check the gate is before any file is opened, and that no other
   agent's transcript is read at all.
7. **The tests.** Every one is supposed to write a real JSONL file so the product's own parser is in
   the path, and none is supposed to hand-build a parsed object. Check that. A test that hand-builds
   its input never watches the real producer. Are there rules the code holds that no test holds? Is
   any assertion the only holder of a rule?

## Run the checks yourself

    dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~PendingInteraction"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~QuestionBox"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"
    dotnet build cc-director.sln

Read the COUNT, never the colour: a filter that matches nothing exits green. The Tech Lead's own runs
on this exact commit were **17**, **4**, **95** and a build with 0 warnings and 0 errors.

**`dotnet test src/CcDirector.Core.Tests` unfiltered does NOT finish on this machine and did not
before this change either.** `RepositoryRegistryConcurrencyTests` crashes the test host, and the
aborted run still prints a "Passed" line with whatever number the race reached - 2494, 3059 and 636
in three runs of the same suite. That is filed as issue #3220. It is not this change's. Use
`--filter "FullyQualifiedName!~RepositoryRegistryConcurrency"` if you want the whole project, and
check any run you read actually reached its end.

Then break ONE thing yourself, show the red counts, put it back with `git checkout --`, and show
green again on a FULL build - never a no-build run on the restore run. If your mutation changes
nothing, say so loudly.

## Known already, do not spend your review on these

- **Two comments in `src/CcDirector.Avalonia` are now false** and were left because the Developer's
  mandate forbade touching that project: `SmartShutdownSessionReader.cs` and
  `SmartShutdownSessionReaderTests.cs` both still say the property is never populated. The Tech Lead
  knows and is having them corrected. Do not report them; DO report any OTHER comment or document
  that the change has made untrue.
- `PendingInteractionKind.Permission` is not reachable from a transcript and is deliberately not
  faked.
- Nothing was run against a live Claude Code session.

## What you owe

ONE file at `docs/missions/smart-director-restart-2026-09-19/review-phase-3-4.md`, in your own
worktree, committed and pushed on your own branch (`smart-restart/p3-review-4`), before your turn
ends: your scope and what you did NOT look at; your own counts and your own revert proof with its
numbers; each finding, ranked, with file and line, what it would do to a person, and how sure you
are; and what you could not check. **"No findings" is a real answer** - then say what you attacked
and failed to break.

Do not fix anything, do not open a pull request against the code, do not merge anything.

**Sign nothing**: no "Co-authored-by", no "Generated with", no agent or vendor name, anywhere in the
file or the commit. ASCII only - no Unicode, no emoji, no arrows, no tick marks.

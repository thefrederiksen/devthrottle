# Mandate - Smart Director Restart, phase 3, Developer: make the Director know when a question box is open

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-question-box`, branch
`smart-restart/p3-question-box`, cut from `origin/main`. Work only there. Never work in
`D:/ReposFred/devthrottle`.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH INSIDE ONE TURN: build, run the checks, commit, push, open the pull
   request and write your proof file before your turn ends. Nothing can answer a question you stop to
   ask. If you cannot finish, write how far you got into
   `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-question-box.md`, commit and push
   that, and stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes.

## Why this exists - read it, it decides what "done" means

The mission's own check (`mission.md` section 7) requires this, in the owner's words: **a session
with a question box open is shown in the Smart shutdown dialog BEFORE anything is asked** - so that
he answers those first rather than having them shut down mid-question. Phase 2 built that section of
the dialog, tested it, and then found it can never appear on a real Director.

The reason, which the phase 3 Tech Lead confirmed by reading the code: `Session.PendingInteraction`
is declared

    public PendingInteraction? PendingInteraction { get; private set; }

and that line is the ONLY occurrence of the name in `Session.cs`. A property with a private setter and
no assignment inside its own type can never be anything but null. Nothing anywhere in `src`, outside
tests, assigns it. Its own file says why: the Claude Code hook path that fed it was removed, and the
terminal detector that replaced it (`TerminalStateDetector`) is a dumb ten-second silence timer that
does not look at the screen for a question at all.

So the dialog reads a property that is always null, and the mission's check cannot be met. You fix
that.

## What you build - and the shape matters more than the size

**Fill `Session.PendingInteraction` from the agent's own TRANSCRIPT, never by guessing at the
terminal screen.** The Director already parses Claude Code's transcript and already knows how to tell
a finished tool call from an unfinished one. Use that, and only that.

The pieces already in the tree:

- `SessionHistoryReader.ResolveTranscriptPath(session)` - where a session's transcript is.
- `StreamMessageParser.ParseFile(path)` - the product's own parser, giving `StreamMessage` with
  `ContentBlocks`, each carrying `Type`, `ToolName` and `ToolUseId`.
- `WidgetBuilder.BuildFromMessages` - **read this first.** It already pairs a `ToolUse` block with the
  later `ToolResult` block that carries the same `ToolUseId`, and it already names
  `AskUserQuestion` ("Claude needs your input") and `ExitPlanMode` ("Waiting for your approval").
  A tool use with no matching result is a tool call still waiting. That is the whole detection.
- `ConversationIngestor.WireSession` - **the trigger, already correct.** It subscribes to
  `Session.OnActivityStateChanged` and acts when the state becomes `WaitingForInput`, which is
  exactly the moment a session has stopped and may be holding a question. Follow that shape; do not
  invent a polling timer.

### The rules the detection must obey

1. **Claude Code only, for now.** `AskUserQuestion` and `ExitPlanMode` are Claude Code's tools. Every
   other agent keeps reading false, and the code says so plainly rather than leaving a reader to
   wonder. Do not guess at another agent's screen or transcript.
2. **`PendingInteractionKind.Permission` is NOT reachable this way** and you do not fake one. Its
   source was a permission-prompt hook event that is not in the transcript. Say so in the code.
   Question and Plan are what you can honestly know.
3. **It is CLEARED as carefully as it is set.** When the unfinished tool call gains its result, or
   the session goes back to working, the property goes back to null. A stale question box in the
   dialog is worse than none: the owner would go looking for a question nobody is asking.
4. **Never throw into the session's event.** A transcript that cannot be read or parsed leaves the
   property as it was and writes a log line. This runs on every turn end of every session.
5. **It is volatile state and stays volatile** - not persisted, as `Session` already documents.
6. **No fallback** (CLAUDE.md rule 3). If the transcript cannot be resolved, that is a logged fact,
   not a guess at the answer.

You will need a way to set the property from outside `Session`. Give `Session` one method that does
it and says what it is for - the same shape `Session` already uses for its other volatile state - and
keep the setter private. Do not make the property publicly settable.

## What you may NOT do

- No change to `src/CcDirector.Avalonia` - the dialog already reads the property correctly, and phase
  2 proved that reading with a test on a real `Session`. Your work is what makes that reading true.
- No change to the Gateway or to any contract.
- No change to `TerminalStateDetector` and no screen scraping of any kind.
- No change to `ConversationIngestor` beyond what you genuinely need; prefer a class of your own that
  subscribes to the same event, because the ingestor's job is pushing prompts to the Gateway and this
  is not that.

## The checks, and what they must say

Measure BOTH on the UNTOUCHED tree first, or the after-numbers mean nothing.

    dotnet test src/CcDirector.Core.Tests
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

Read the COUNT, never the colour: a filter that matches nothing exits green, and an ABORTED run
prints "Passed" for whatever finished before it died - so check each run reached its end. The second
command must not move at all: the dialog is not yours to change, and if its count changes you have
touched something you should not have.

Also run `dotnet build cc-director.sln` and report the warnings and errors.

**Prove your tests can fail.** Break one line of the detection, show the exact red count, put it back
with `git checkout --`, and show green again on a FULL build - never a no-build run on the restore
run, because a no-build run certifies the binary and not the source. Commit first so the restore
cannot eat your work.

**Run the whole test project more than once.** This phase has already found one test that passed
under a filter every time and failed one full run in four. A test that only passes under a filter is
worse than no test.

## What you must cover with tests, at least

Drive them from REAL transcript content - write a small JSONL file the way the product's own parser
reads it - not from hand-built objects that never meet the parser. A test that hand-builds its input
proves nothing about the thing that really produces it.

- an `AskUserQuestion` tool use with no result: the session has a question box open, with the
  question's own text;
- the same, once its result arrives: back to null;
- an `ExitPlanMode` with no result: a plan waiting;
- an ordinary tool call (a Bash, a Read) with no result: NOT a question box. This is the test that
  stops the detection firing on every busy session;
- two questions in one transcript, the second unfinished: the second one is the one held;
- a session of another agent: nothing, and no attempt to read;
- a transcript that does not exist, and one that is not valid: the property is untouched and nothing
  throws;
- the property goes back to null when the session starts working again;
- and the one that ties it to the mission: given a real `Session` with a question box detected this
  way, `SmartShutdownSessionReader.Read` reports `HasQuestionBoxOpen` true for it. That is the whole
  point - do not leave the two halves unconnected.

## When it is done

1. Run the checks. Write every count down, including the repeats.
2. Commit, plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
   with", no agent or vendor name. ASCII only: no Unicode, no emoji, no arrows, no tick marks.
3. Push and open a pull request against `main` naming the mission issue #3167. Do not merge it
   yourself and do not wait for the hosted checks.
4. Write `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-question-box.md` on the same
   branch: the commands, the counts before and after, the revert proof numbers, what each new test
   proves in plain words, every decision you made, and what you could NOT reach - in particular, say
   plainly that nothing here was run against a real Claude Code session unless you managed to, and
   what that leaves unproven.
5. Then your turn may end.

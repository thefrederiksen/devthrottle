# Proof - Smart Director Restart, phase 3: the Director knows a question box is open

Developer seat, session 8c344ef4, 20 September 2026. Worktree
`D:/ReposFred/devthrottle-smart-restart-p3-question-box`, branch `smart-restart/p3-question-box`,
cut from `origin/main` at `217b79f63`. Pull request: thefrederiksen/devthrottle#3215. Mission issue
#3167.

---

## 1. What was wrong, and what now makes it right

`Session.PendingInteraction` was declared with a private setter and nothing inside `Session` assigned
it. Nothing anywhere else in `src`, outside tests, assigned it either. A property with a private
setter and no writer in its own type can only ever be null, so the Smart shutdown dialog's
"Answer these first?" section could never appear on a real Director, and the mission's own check
(mission.md section 7) could not be met.

It is now filled from the agent's own transcript:

| File | What it is |
|---|---|
| `src/CcDirector.Core/Sessions/PendingInteractionDetector.cs` | New. Reads the transcript and answers Pending / NothingPending / NoAnswer. |
| `src/CcDirector.Core/Sessions/PendingInteractionWatcher.cs` | New. Subscribes to the turn-end trigger and stamps the answer on the session. |
| `src/CcDirector.Core/Sessions/Session.cs` | `SetPendingInteraction`, the one door in; and the clearing in `SetActivityState`. |
| `src/CcDirector.Core/Sessions/PendingInteraction.cs` | Comment only. Its own remarks said the property was never populated; that is no longer true, and it is the file the other two comments point at. |
| `src/CcDirector.ControlApi/ControlApiHost.cs` | Constructs, starts and disposes the watcher beside the three that already ride the same trigger. |

The rule is one sentence: a Claude Code tool call to `AskUserQuestion` or `ExitPlanMode` that has no
matching tool result yet is a box on the user's screen, because Claude Code writes the result only
once the user answers. The pairing is the product's own - a tool use answered by the later tool
result carrying the same tool use id, exactly as `WidgetBuilder.BuildFromMessages` does it - over the
product's own parse, `StreamMessageParser.ParseFile`. No second parser was written and no screen was
read.

---

## 2. Every decision I made, and why

**A three-valued reading, not a boolean.** `PendingInteractionReading` is `Pending`,
`NothingPending`, or `NoAnswer`. "I could not look" is not the same fact as "I looked and there is
nothing", and writing the second when you mean the first is precisely how a question box that is
genuinely open gets erased. Only `NothingPending` clears the property; `NoAnswer` leaves it exactly
as it was.

**A failed read is one shape: zero parsed messages.** `StreamMessageParser.ParseFile` swallows a
missing file and swallows an unparseable line, returning whatever it managed. So a transcript that is
absent, empty, or not JSON all arrive here as zero messages, and all three answer `NoAnswer`. A
transcript that genuinely holds nothing pending has messages in it and takes the ordinary path. This
is the one place a reader might expect a fallback; there is none - the unreadable case is logged and
declines to answer, it does not guess.

**Claude Code only, and the transcript is not opened for anyone else.** The gate is the first line of
`Detect`, before any path is resolved. Test
`Detect_SessionOfAnotherAgent_GivesNoAnswer` points a Codex session at the very same file that makes
a Claude Code session report a question, so the difference proved is the agent and not the file.

**`Permission` is not reachable and is not faked.** Its source was a hook event (a `PermissionRequest`
or a `Notification` with `notification_type=permission_prompt`) and a hook event is not written into
the transcript. The class remarks say so, and `PendingInteraction.cs` now says so too.

**The clearing is in two places on purpose, and both are needed.**
- `Session.SetActivityState` drops the property the moment the session leaves
  `WaitingForInput`/`WaitingForPerm`, beside the identical line that drops `IsBackgroundRunning`. The
  property's own documentation has promised that clearing since it was written; until there was
  something to clear there was no code under the sentence. There is now.
- The watcher writes null at the next turn end once the transcript shows the ask has gained its
  result. That covers a session which settles again without ever passing through Working.

**A generation guard against a stale stamp.** Reading a transcript takes real time, and in that time
the user can answer and the session can go back to work - which clears the property. A read that
started before that and finished after it would put the question box straight back. The turn-end
handler captures `Session.ActivityGeneration` on the event thread and the refresh drops its answer if
the generation has moved. Covered by
`Refresh_WhenTheSessionHasMovedOnSinceTheRead_DropsTheReading`.

**One read at wire-up for a session that is already parked.** A restored session after a Director
restart - which is the case this whole mission exists for - would otherwise carry nothing, because a
session waiting on a question does not end another turn until somebody answers. This is one read when
the watcher first sees the session, not a poll. Covered by
`WiringASessionAlreadyParkedOnAQuestion_StampsItImmediately`.

**The question's own text comes from the `questions` array.** `AskUserQuestion`'s input is an array of
question objects, each with its text and its options; the parser stores a non-string input value as
raw JSON, so it arrives as a string to be read. The first entry is taken, because a
`PendingInteraction` holds one prompt. When that input cannot be read as JSON - the commonest cause is
benign, the parser truncates any input value over 2000 characters and leaves the array unclosed - the
box is still reported, with the plain words "Claude needs your input" and a log line. The detection is
certain in that case; only the wording is unavailable, and saying "there is a box and I cannot quote
it" is honest where saying "there is no box" would not be.

**A class of its own, not a change to `ConversationIngestor`.** The ingestor's job is pushing prompts
to the Gateway and this is not that. `PendingInteractionWatcher` subscribes to the same event in the
same shape as `SessionRecordsWatcher`.

**Where the mission-tying test lives, and why it is not under `SmartRestart`.** The phase check is
that the `FullyQualifiedName~SmartRestart` count does not move, which is how it is proved that the
dialog was not touched. A test added under that name would move the count and the check would stop
meaning anything. So
`src/CcDirector.Avalonia.Tests/QuestionBox/QuestionBoxReachesTheShutdownDialogTests.cs` sits in
namespace `CcDirector.Avalonia.Tests.QuestionBox`, named for what it proves - the new half reaching
the old one. Both counts are reported below so nothing is hidden by the choice.

---

## 3. The tests, and what each one proves in plain words

Every one of them writes a real JSONL file in the shape Claude Code writes and hands the session that
file, so the product's own parser is in the path. Not one of them hand-builds a `ContentBlock`.

`src/CcDirector.Core.Tests/Sessions/PendingInteractionDetectorTests.cs` (11):

| Test | What it proves |
|---|---|
| `Detect_AskUserQuestionWithNoResult_ReportsTheQuestionBoxWithItsOwnText` | The case the mission turns on. An unanswered question is reported, with the question's own words and its own option labels and descriptions - not a generic label. |
| `Detect_AskUserQuestionAnsweredByItsResult_ReportsNothingPending` | Once the user has answered and the tool result is in the file, the box is gone and the reading is a definite nothing, which clears the property. |
| `Detect_ExitPlanModeWithNoResult_ReportsAPlanWaiting` | A plan offered and not yet approved is held, with its plan body. |
| `Detect_OrdinaryToolCallWithNoResult_IsNotAQuestionBox` | The one that stops this firing on the whole fleet. An unfinished Bash and an unfinished Read are a busy session, not an asking one. |
| `Detect_TwoQuestionsWithTheSecondUnfinished_HoldsTheSecond` | With the first answered and the second not, the second is the one held. |
| `Detect_TwoUnfinishedQuestions_HoldsTheNewer` | With both unanswered, the newer one is what is on screen, so it is the one held. |
| `Detect_SessionOfAnotherAgent_GivesNoAnswer` | A Codex session pointed at the very file that makes a Claude Code session report a question gets no answer. The difference is the agent, not the file. |
| `Detect_TranscriptThatDoesNotExist_GivesNoAnswer` | A missing file does not throw and does not clear. |
| `Detect_TranscriptThatIsNotValid_GivesNoAnswer` | A file of plain text and broken JSON does not throw and does not clear. |
| `Detect_AQuestionTooLongForTheParserToKeepWhole_IsStillReportedWithoutItsText` | A question whose input the parser truncates is STILL reported as a box, with the plain words in place of text it cannot read. Measured: one real question in six on this machine is that long. See section 6a. |
| `Detect_NoTranscriptPathResolves_GivesNoAnswer` | A session with no transcript pointer at all is a logged fact, not a guess. |

`src/CcDirector.Core.Tests/Sessions/PendingInteractionWatcherTests.cs` (6). These drive the REAL
trigger: the session is wired to a real watcher and then flipped through a real activity-state
change, so what is under test is the handler the Director runs. A test that called the refresh by
hand would stay green if the subscription were deleted.

| Test | What it proves |
|---|---|
| `TurnEnd_WithAQuestionOutstanding_StampsTheQuestionOnTheSession` | End to end: a turn ends with a question outstanding and the watcher's own handler puts it on the session. |
| `SessionStartsWorkingAgain_TheQuestionGoesBackToNull` | The user answered, the session is working, and the flag goes with it. |
| `TurnEnd_OnceTheAnswerIsInTheTranscript_ClearsTheQuestion` | The other clearing path - the session settles again without passing through Working, and the next read writes null. |
| `Refresh_WhenTheTranscriptCannotBeRead_LeavesThePropertyAsItWas` | An unreadable transcript does not erase a real question. |
| `Refresh_WhenTheSessionHasMovedOnSinceTheRead_DropsTheReading` | A reading about a moment that has passed is dropped, so it cannot undo the clearing. |
| `WiringASessionAlreadyParkedOnAQuestion_StampsItImmediately` | A restored session already parked on a question is read once at wire-up. |

`src/CcDirector.Avalonia.Tests/QuestionBox/QuestionBoxReachesTheShutdownDialogTests.cs` (1):

`ASessionHoldingAQuestion_IsShownToTheDialogAsHavingOneAndAnAnsweredOneIsNot` - the two halves
joined. A real transcript on disk, read by the real detection, stamped on a real `Session`, and
`SmartShutdownSessionReader.Read` reporting `HasQuestionBoxOpen` true for the asking session and
false for the one whose question has been answered. No reflection anywhere. Phase 2 had to set the
property by reflection because nothing filled it; this is the test that shows it no longer has to.

---

## 4. The counts

Measured on the untouched tree first. Read the count, never the colour.

### Solution build

| When | Result |
|---|---|
| Before | `dotnet build cc-director.sln` - Build succeeded, 0 Warning(s), 0 Error(s), 1m19s |
| After | `dotnet build cc-director.sln` - Build succeeded, 0 Warning(s), 0 Error(s) |

### `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"`

| When | Result |
|---|---|
| Before | Failed 0, Passed 47, Skipped 0, Total 47 |
| After | Failed 0, Passed 47, Skipped 0, Total 47 |

**It did not move.** Nothing in `src/CcDirector.Avalonia` was changed.

### The whole `CcDirector.Avalonia.Tests` project

| Run | Result |
|---|---|
| After, excluding the one class this phase added (`--filter "FullyQualifiedName!~QuestionBoxReachesTheShutdownDialog"`) | Failed 0, Passed 646, Skipped 0, Total 646, 1m28s |
| After, the whole project | Failed 0, Passed 647, Skipped 0, Total 647, 1m41s |

### `dotnet test src/CcDirector.Core.Tests`

**The unfiltered run does not finish on this machine, and it did not finish before my change either.**
Three runs, three aborts, always the same one:

```
The active test run was aborted. Reason: Test host process crashed : Unhandled exception.
System.IO.DirectoryNotFoundException: Could not find a part of the path
'C:\Users\soren\AppData\Local\Temp\RepoRegistryConcurrency_<random>\repositories.json'
   at CcDirector.Core.Tests.RepositoryRegistryConcurrencyTests
      .A_reader_of_the_file_never_sees_a_half_written_list ... RepositoryRegistryConcurrencyTests.cs:line 176
```

| Run | Tree | What it printed |
|---|---|---|
| 1 | Untouched | `Passed! - Failed: 0, Passed: 2494, Skipped: 6, Total: 2500, 5m58s` then `Test Run Aborted.` |
| 2 | Untouched | `Passed! - Failed: 0, Passed: 3059, Skipped: 4, Total: 3063, 7m47s` then `Test Run Aborted.` |
| 3 | After my change | `Passed! - Failed: 0, Passed: 636, Skipped: 0, Total: 636, 7m48s` then `Test Run Aborted.` |

Those three "Passed" lines are worth looking at side by side: 2494, then 3059, then 636, from the
same suite. That is the trap this mission already wrote a record about (commit `020d74294`) - an
aborted run reports a pass for whatever finished before the host died, and the number is whatever the
race happened to reach. **None of those three is a result.** It is a crash, it is not mine, and it is
on `origin/main`.

So the honest measurement of this project is the same suite with that one crashing class excluded,
which does complete, on both trees:

`dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName!~RepositoryRegistryConcurrency"`

| Run | Tree | Result |
|---|---|---|
| Before | Untouched, `217b79f63` | Failed 0, Passed 4504, Skipped 8, Total 4512, 14m, exit code 0 |
| After, run 1 | Commit `382577612`, sixteen new tests | Failed 0, Passed 4520, Skipped 8, Total 4528, 26m17s, exit code 0 |
| After, run 2 | Final source, seventeen new tests | Failed 0, Passed 4521, Skipped 8, Total 4529, 16m38s, exit code 0 |
| After, run 3 | Final source, seventeen new tests | Failed 0, Passed 4521, Skipped 8, Total 4529, 15m37s, exit code 0 |

Baseline total: 4512. Run 1 was started against commit `382577612`, which carried sixteen of the
seventeen new tests, so its expected total is 4512 + 16 = 4528. Runs 2 and 3 ran against the final
source with all seventeen, so their expected total is 4512 + 17 = 4529. Which source each run measured
is named in the table.

### The new tests on their own

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~PendingInteraction"` | Failed 0, Passed 16, Skipped 0, Total 16 |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~QuestionBox"` | Failed 0, Passed 4, Skipped 0, Total 4 (3 pre-existing plus the one added) |

The sixteen were listed with `--list-tests` and confirmed to be exactly the ten detector tests and the
six watcher tests, so the filter is not quietly catching something else.

---

## 5. The revert proof

Committed FIRST (`382577612`), so a `git checkout --` restore could not eat the work. Two separate
one-line breaks, because two different guards are worth proving, and each break was followed by a
FULL `dotnet build` so the binary matched the mutated source.

### Break one - delete the tool-result pairing

In `PendingInteractionDetector.FromMessages`, the line

```csharp
unanswered.RemoveAll(b => b.ToolUseId == block.ToolUseId);
```

replaced by a comment, so an answered question is never removed from the outstanding list.

Full build: 0 Warning(s), 0 Error(s).

| Command | Result |
|---|---|
| Core, `~PendingInteraction` | **Failed 2**, Passed 14, Total 16 |
| Avalonia, `~QuestionBox` | **Failed 1**, Passed 3, Total 4 |

Named red:
- `PendingInteractionDetectorTests.Detect_AskUserQuestionAnsweredByItsResult_ReportsNothingPending`
- `PendingInteractionWatcherTests.TurnEnd_OnceTheAnswerIsInTheTranscript_ClearsTheQuestion`
- `QuestionBoxReachesTheShutdownDialogTests.ASessionHoldingAQuestion_IsShownToTheDialogAsHavingOneAndAnAnsweredOneIsNot`

### Break two - delete the tool-name gate

Restored break one first, then in the same method

```csharp
if (block.Type == ContentBlockType.ToolUse &&
    block.ToolName is AskUserQuestionTool or ExitPlanModeTool)
```

reduced to `if (block.Type == ContentBlockType.ToolUse)`, so every tool call counts as a question box.

Full build: 0 Warning(s), 0 Error(s).

| Command | Result |
|---|---|
| Core, `~PendingInteraction` | **Failed 1**, Passed 15, Total 16 |

Named red: `PendingInteractionDetectorTests.Detect_OrdinaryToolCallWithNoResult_IsNotAQuestionBox` -
the test that stops this firing on every busy session in the fleet.

### Restore

```
git checkout -- src/CcDirector.Core/Sessions/PendingInteractionDetector.cs
git status --short      # empty
git diff HEAD --stat    # empty
dotnet build cc-director.sln    # Build succeeded, 0 Warning(s), 0 Error(s)
```

The green run after the restore was deliberately run WITHOUT `--no-build`, so `dotnet test` compiled
the restored source itself rather than certifying a binary left over from the mutation:

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~PendingInteraction"` | Failed 0, Passed 16, Total 16 |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~QuestionBox"` | Failed 0, Passed 4, Total 4 |

---

## 6. What I could NOT reach, said plainly

**Nothing here was run against a live Claude Code session.** Not one line of this was exercised by a
real agent on a real Director. Every transcript in every test was written by the test.

What that leaves unproven, precisely:

1. ~~That a real Claude Code transcript has the shape these tests assume.~~ **This one I did close -
   see section 6a below.** The fixtures match six real transcripts on this machine exactly.
2. **That Claude Code really withholds the tool result until the user answers.** The whole detection
   rests on that. It is how the tool works and how the Agent view's pending-widget rendering already
   relies on it, but I did not watch a transcript being written to confirm the timing.
3. **That the wiring in `ControlApiHost` runs.** The watcher is constructed, started and disposed
   beside three siblings that use the identical pattern, and the solution builds. No Director was
   launched, so nothing here proves the Director actually starts it.
4. **That the dialog now shows the section on a running Director.** The reader is proved to report
   `HasQuestionBoxOpen` true from a detected question; the dialog's rendering of that was phase 2's
   and is untouched.

**Two shapes of real transcript I reasoned about but did not test, because I had no real transcript
to test them on.** Both would show up as a question box reported when there is none - a false
positive, which is the direction that matters here:

- **Subagent turns.** `StreamMessageParser.ParseFile` returns everything in the file, including nested
  subagent entries, and the detection does not exclude them. A subagent cannot ask the user, so today
  this should be harmless; if a subagent ever gains an `AskUserQuestion`, an unanswered one of its
  calls would be reported as the parent session's question box. `SessionHistoryReader` already
  distinguishes the main thread from the rest (`Read` versus `ReadAll`), so the fix if it is ever
  needed is to narrow to the main thread.
- **Compaction.** Claude Code rewrites its transcript on a compaction. If a compacted file could ever
  carry an `AskUserQuestion` tool use whose tool result was summarised away, it would read as
  unanswered forever. I do not believe a summary carries raw tool-use blocks, but I did not confirm
  it against a compacted file.

**Two comments in `src/CcDirector.Avalonia` and `src/CcDirector.Avalonia.Tests` are now false, and I
left them.** The mandate forbids changing `src/CcDirector.Avalonia`, so I did not:

- `src/CcDirector.Avalonia/SmartRestart/SmartShutdownSessionReader.cs:15-19` - "That property is
  currently never populated by the product (see PendingInteraction.cs), so today this always reads
  false for a real session".
- `src/CcDirector.Avalonia.Tests/SmartRestart/SmartShutdownSessionReaderTests.cs:15-21` - "nothing in
  the product sets it today (PendingInteraction.cs says so) ... it does NOT prove the product ever
  fills it, and today it does not".

Both cite `PendingInteraction.cs`, and I DID correct that file, so a reader who follows the pointer
gets the truth. But a comment that states the opposite of the code beneath it is a defect, and these
two should be corrected by whoever is allowed to touch that project. **They are the first thing to fix
after this merges.**

---

## 6a. The one gap I did close: the fixtures were checked against REAL transcripts

The biggest risk in the list above was that my fixtures were invented from the tool's documented
schema and that a real `AskUserQuestion` writes something else. That is checkable without a running
Director, because this machine already has hundreds of real Claude Code transcripts under
`~/.claude/projects`. I read them.

108 transcript lines on this machine mention `AskUserQuestion` or `ExitPlanMode`. I inspected the
first six `tool_use` blocks among them and printed the SHAPE only - the key names and the lengths,
never the content, which is the owner's own work:

```
TOOL: AskUserQuestion | msgtype: assistant | isSidechain: False | isMeta: None
  tool_use keys: ['caller', 'id', 'input', 'name', 'type']
  input keys:    ['questions']
  questions:     a list, length 2 / 1 / 4 / 2 / 2 / 1 across the six
  question keys: ['header', 'multiSelect', 'options', 'question']
  option keys:   ['description', 'label']
```

All six identical, and identical to the fixture at the top of `PendingInteractionDetectorTests`:
`input.questions` is an array; each entry carries `question` and `options`; each option carries
`label` and `description`. **So the detection and the question text are reading the real shape, not
an invented one.** All six were main-thread assistant messages - not sidechain, not meta - which is
also what the detection assumes.

### The false-positive check, over 2,075 real transcripts

The thing that would make this feature worse than useless is firing on sessions that are NOT asking -
the owner opens the shutdown dialog, sees half the fleet flagged as holding a question, and stops
believing the flag. So I ran the detection's exact rule (the same pairing, the same two tool names,
the same skip of meta lines) over every Claude Code transcript on this machine:

```
transcripts scanned:                                            2075
transcripts containing AskUserQuestion or ExitPlanMode:           71
  every ask paired with a result -> would report NOTHING PENDING: 70
  an ask left unanswered        -> would report PENDING:            1
```

**Seventy out of seventy-one would correctly report no question box.** The one that would report
Pending is a session that really was left sitting on a question and never answered - which is exactly
the case the owner wants surfaced before a shutdown.

This is a rule replay, not the compiled C# - it is Python walking the same JSONL with the same logic,
so it proves the RULE is right about real data, not that the C# implements it. The C# implementing it
is what the seventeen tests are for.

### And the measurement that finding produced

The serialised `input` object of those six was 1644, 1453, 3211, 1927, 1898 and 1171 characters.
`StreamMessageParser.ParseToolUseBlock` truncates any input value over **2000** characters and
appends a marker, which leaves the questions array unclosed and unparseable.

**So roughly one real question in six is too long for its text to survive the parse.** That is not a
detection failure - the tool name and the missing tool result are structural and unaffected - but
that session's entry would read "Claude needs your input" instead of the question. Test
`Detect_AQuestionTooLongForTheParserToKeepWhole_IsStillReportedWithoutItsText` pins that behaviour
with a question padded past the limit, so it is a proven property rather than a hoped-for one.

**Recommended follow-up, not done here:** read the questions array with a
`Utf8JsonReader` constructed with `isFinalBlock: false`, which is designed to stop cleanly at
incomplete data instead of throwing, so the first question's text survives a truncated array. I did
not do it because I have no real truncated value to test it against and an untested tolerant parser
is worse than a proven placeholder. The boolean the mission needs is correct either way.

---

**The `RepositoryRegistryConcurrencyTests` crash is on `origin/main` and I did not fix it.** It is out
of this mandate's scope and it kills the whole `CcDirector.Core.Tests` host, so nobody on this machine
can get a complete unfiltered run of that project until it is fixed. It deserves its own issue.

---

## 7. The headline, in one paragraph

Seventeen new tests in `CcDirector.Core.Tests` and one in `CcDirector.Avalonia.Tests`, all green, on
two full runs of the Core project that agree exactly - 4521 passed, 8 skipped, 4529 total, both
times, against a baseline of 4504 / 8 / 4512. The `SmartRestart` filter is unmoved at 47, so the
dialog was not touched. The solution builds with 0 warnings and 0 errors. Two one-line breaks of the
detection each turned specific named tests red, and both were restored to a full green build. The
fixtures were checked against six real transcripts, and the rule was replayed over 2,075, where it
correctly reports no question box on 70 of the 71 that hold one of these tools. Nothing was run
against a live Claude Code session; section 6 says exactly what that leaves open.

---

## 8. Every command, in order

```
# baseline, untouched tree at 217b79f63
dotnet build cc-director.sln
dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart" --no-build
dotnet test src/CcDirector.Core.Tests --no-build                       # aborted, twice
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName!~RepositoryRegistryConcurrency"

# after the change
dotnet build cc-director.sln
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName~PendingInteraction"
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName~PendingInteraction" --list-tests
dotnet test src/CcDirector.Avalonia.Tests --no-build --filter "FullyQualifiedName~QuestionBox"
dotnet test src/CcDirector.Avalonia.Tests --no-build --filter "FullyQualifiedName~SmartRestart"
dotnet test src/CcDirector.Avalonia.Tests --no-build
dotnet test src/CcDirector.Avalonia.Tests --no-build --filter "FullyQualifiedName!~QuestionBoxReachesTheShutdownDialog"
dotnet test src/CcDirector.Core.Tests --no-build                       # aborted again
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName!~RepositoryRegistryConcurrency"   # run 1

# revert proof (after committing 382577612)
#   break one, full build, run; restore
#   break two, full build, run; restore
git checkout -- src/CcDirector.Core/Sessions/PendingInteractionDetector.cs
dotnet build cc-director.sln
dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~PendingInteraction"
dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~QuestionBox"

# the seventeenth test and the final source (commit 8bb6be7e0)
dotnet build cc-director.sln
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName~PendingInteraction"
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName!~RepositoryRegistryConcurrency"   # run 2
dotnet test src/CcDirector.Core.Tests --no-build --filter "FullyQualifiedName!~RepositoryRegistryConcurrency"   # run 3
```

One note on how the long runs were executed, because it matters to anyone reproducing this. The
filtered Core run takes fifteen to twenty-six minutes, which is longer than a single foreground shell
call is allowed here. Each one was started in the foreground writing to a file, the harness moved it
to the background at its own timeout, and this session then BLOCKED on an until-loop watching that
file until the result line appeared. Nothing was left unwatched and no result was read from a
buffered pipe. Run 1 took 26m17s rather than the baseline's 14m because an Avalonia run was
deliberately overlapped with it; runs 2 and 3 were alone on the machine and took 16m38s and 15m37s.

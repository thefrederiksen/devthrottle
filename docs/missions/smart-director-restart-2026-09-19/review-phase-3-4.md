# Review - Smart Director Restart, phase 3, Reviewer 4: the question box detection

Reviewer seat, session c67f25a1, 21 September 2026. Worktree
`D:/ReposFred/devthrottle-smart-restart-p3-review4`, branch `smart-restart/p3-review-4`, at commit
`1de2810f6` (pull request #3215). I did not write this code. Nothing here is a decision about any
finding; the phase 3 Tech Lead decides.

## 1. Scope

What I read, in the order the mandate set:

- The Developer's mandate and its proof, including the proof's sections 6 and 6a.
- The code: `PendingInteractionDetector.cs`, `PendingInteractionWatcher.cs`, the `Session.cs`
  change (`SetPendingInteraction` and the clearing in `SetActivityState`), the corrected remarks in
  `PendingInteraction.cs`, and the wiring in `ControlApiHost.cs`.
- The product's own pairing and parse: `WidgetBuilder.BuildFromMessages`, `StreamMessageParser`
  (`StreamMessage.cs`), and `SessionHistoryReader.ResolveTranscriptPath`.
- All three new test files and the shared `QuietBackend` test double.
- 2,078 real Claude Code transcripts under this machine's `~/.claude/projects`, replayed with the
  detector's exact rule. I printed shapes and counts only, never content.

What I ran:

| Command | My count |
|---|---|
| `dotnet build cc-director.sln` | Build succeeded, 0 Warning(s), 0 Error(s) |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~PendingInteraction"` | Failed 0, Passed 17, Skipped 0, Total 17 |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~QuestionBox"` | Failed 0, Passed 4, Skipped 0, Total 4 |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"` | Failed 0, Passed 95, Skipped 0, Total 95 |

All three match the Tech Lead's numbers for this commit (17, 4, 95) and the build is clean. I listed
the seventeen Core tests with `--list-tests` and confirmed they are exactly the eleven detector tests
and the six watcher tests, so the filter is not quietly catching anything else.

What I did NOT reach:

- No live Claude Code session and no running Director. The end-to-end appearance of the "Answer these
  first?" section on a real Director is not proven by me, exactly as the proof says it is not proven
  by the Developer. That is the mission's QA report to close, not this review.
- The whole `CcDirector.Core.Tests` project: the unfiltered run does not finish on this machine
  (`RepositoryRegistryConcurrencyTests` crashes the test host, issue #3220, not this change's), so I
  ran the mandate's four commands rather than a full project run.
- The Gateway suites, the question-and-answer rig, and the other phase 3 tracks. Nothing in this
  change touches them.
- My transcript replay is a rule replay in Python walking the same lines with the same logic,
  including the parser's quirks (per-line swallow of broken JSON, unknown line types skipped,
  `isMeta` read for user lines but never for assistant lines). It proves the RULE over real data; the
  compiled C sharp implementation of it is what the seventeen tests pin.

## 2. The revert proof, with its numbers

I broke a guard the Developer's own two breaks did not touch: the generation guard in
`PendingInteractionWatcher.Refresh`. I deleted the `if (session.ActivityGeneration != generation)`
block, so a reading about a moment that has passed would be stamped instead of dropped.

- Full build after the break: 0 Warning(s), 0 Error(s).
- Core, `~PendingInteraction`: **Failed 1, Passed 16, Total 17**.
- The named red: `Refresh_WhenTheSessionHasMovedOnSinceTheRead_DropsTheReading` - the exact test
  that exists to hold that guard, failed with `Assert.Null() Failure: Value is not null`.
- Restore with `git checkout -- src/CcDirector.Core/Sessions/PendingInteractionWatcher.cs`,
  `git status --short` empty, full build again, and the green runs deliberately WITHOUT `--no-build`,
  so the test run compiled the restored source itself:
  Core `~PendingInteraction` Failed 0, Passed 17, Total 17; Avalonia `~QuestionBox` Failed 0,
  Passed 4, Total 4; Avalonia `~SmartRestart` re-run on the restored tree, Failed 0, Passed 95,
  Total 95.

So a third guard, beyond the two the Developer broke, is held by a real test.

## 3. What I attacked and what I found

### 3a. False positives - the direction that matters

I replayed the detector's exact rule over every Claude Code transcript on this machine, 2,078 files,
and then went further than the proof did on the two shapes it could not test:

- 71 transcripts hold a call to `AskUserQuestion` or `ExitPlanMode`, 132 such calls in all.
- 70 of the 71 have every call paired with its tool result, so the rule answers NothingPending.
- 1 file the rule says Pending: `647915df-a1d7-4dc1-a71a-8c5440eee1c4.jsonl`. I inspected its shape
  only: the ask is the last `tool_use` in the file, and the six lines after it are all bookkeeping
  line types (`last-prompt`, `ai-title`, `mode`, `permission-mode`, `atis-latch`, `bridge-session`)
  that the parser skips as unknown. That question really was asked and never answered, so the one
  Pending verdict on this machine is correct.
- **Subagent turns**: zero calls to either tool on `isSidechain` lines, in any of the 2,078 files.
  The proof's reasoning ("a subagent cannot ask the user") matches the recorded behaviour of every
  subagent this machine has ever run. The detector does not exclude sidechain entries, so a future
  subagent that gained one of these tools would surface as its parent's question box; today that is
  an unreachable case on real data, and it is a false positive of the future, not of this change.
- **Compaction**: zero calls left unanswered across a compact-summary boundary. The resolution
  comment in `SessionHistoryReader.ResolveClaude` says the hook-reported path is authoritative across
  compaction and `/clear`, which start a NEW file; the detector follows the same pointer, so a
  compacted conversation does not carry the old calls forward to be read as unanswered forever.
- **Meta lines**: zero calls on `isMeta` lines, and zero tool results answering one of the two tools
  arriving on an `isMeta` line. If answers were written on meta lines, the detector's `IsMeta` skip
  would orphan them and report questions nobody is asking - the exact false positive that matters -
  and the data says that shape does not occur.
- **An unfinished ordinary tool call** (the one that would light the flag on the whole fleet) is held
  by `Detect_OrdinaryToolCallWithNoResult_IsNotAQuestionBox`, and my replay confirms the same rule
  over 2,078 real files of busy sessions.

I could not construct a transcript shape that reports a question box where there is none. The two
shapes the Developer named but could not test do not occur in the recorded history of this machine.

### 3b. Erasing a real question

The reading is three-valued and only NothingPending clears. I checked every failed-read shape:

- Missing file, empty file, and a file where every line fails to parse all arrive as zero parsed
  messages and answer NoAnswer, which leaves the property alone. Held by two tests, and confirmed by
  reading `StreamMessageParser.ParseFile`, which swallows a missing file and a bad line and returns
  what it managed.
- A HALF-WRITTEN transcript is the one shape I want to name: the parser swallows the torn last line
  and keeps everything before it, so a session parked on a real question still reads Pending. The
  narrow residual: if a process died mid-write of the very line that carries the answer's tool
  result, the ask would read as unanswered. That needs the kill to land inside one line's write, the
  session is dead either way, and the exit edge in `SetActivityState` clears the property. I could
  not turn it into a person-facing harm; it is a residual, not a finding.
- The generation guard: `SetActivityState` increments the generation on every state change before it
  fires the event, so the handler captures the new one, and a read that crosses a state change is
  dropped. My break in section 2 shows the guard is held by a test, not by hope.

### 3c. Reuse of the product's pairing, or a second copy

It is a second copy of the same sentence, and I am saying so because the mandate asks. The shared
part is real: both consume the same parse (`StreamMessageParser.ParseFile`) and both pair a tool use
with the later tool result carrying the same tool use id. The pairing CODE is re-stated, and it is
slightly LOOSER than `WidgetBuilder.BuildFromMessages`: the widget builder only tracks tool uses in
assistant messages and only pairs results in user messages; the detector counts a tool use of the
two tools in any message type and pairs a result from any message type.

On real data the looseness is invisible: of the 132 real calls, every one sits in an assistant
message, and every one of the 131 answered ones is answered by a tool result in a user message -
the strict rule and the loose rule agree on all 71 files, and there are no duplicate tool use ids
anywhere. So the class remark "that pairing ... is the product's own ... and this reads the same
parse rather than inventing a second one" is true of the parse and of the rule's meaning, and
generous about the code: the pairing is re-implemented, one line of it, and the two could drift in
principle. I rank this an observation, not a defect: no harm is demonstrable on any transcript this
machine holds, and extracting a shared pairing would have meant changing `WidgetBuilder`, which the
Developer's mandate did not ask for.

### 3d. The trigger

The handler does one read of `session.ActivityGeneration` (an interlocked read, it cannot throw) and
one `Task.Run`; nothing else runs on the event thread, so it can neither block nor throw into
`Session.OnActivityStateChanged`. It can run twice at once on one session (the wire-up read plus a
turn end, or two turn ends close together); across generations the guard drops the stale one, and
within one generation two concurrent reads of the same parked transcript produce the same stamp or
race over microseconds between the answer's tool result landing and the Working flip that clears the
property. I could not construct an ordering there that outlives the next state change. The wiring
sits in `StartSessionStateServices`, which runs FIRST in `StartAsync`, before anything that can
fail, beside the three siblings on the same trigger, and is disposed symmetrically.

### 3e. The clearing in two places

`Session.SetActivityState` drops the property when the session leaves the waiting states, including
the exit edges, and the watcher writes null at the next turn end once the transcript shows the
answer. I traced the disagreeing case each way and found no hole: a session that answers and goes
back to work is cleared by the state edge; a session that answers and settles again without passing
through Working (the WaitingForPerm hop) is cleared by the next read, which is exactly what
`TurnEnd_OnceTheAnswerIsInTheTranscript_ClearsTheQuestion` drives through the real handler.

### 3f. Agent gating

The Claude Code check is the first statement of `Detect`, before any path is resolved, and there is
no other caller. The test points a session of another agent at the very file that makes a Claude
Code session report a question, so the difference proved is the agent. No other agent's transcript
is opened.

### 3g. The tests

Every one writes a real JSONL file and hands the session that file, so the product's own parser is
in the path; none hand-builds a parsed object, including the Avalonia test that joins the two
halves. The watcher tests drive the real trigger through real activity-state changes rather than
calling the refresh by hand. One rule the code holds that NO test holds: the `IsMeta` skip in
`FromMessages`. Deleting `if (message.IsMeta) continue;` turns nothing red. (Related parser quirk,
pre-existing and inherited: `isMeta` is never read for assistant messages, so a meta ASSISTANT line
would not be skipped by anyone - and there is not one such line in all 2,078 real transcripts, nor
one carrying a tool use.) I rank this an observation for whoever next touches the detector: the
skip is one line whose absence no test would notice.

## 4. Findings

**No findings that prove a harm.** I could not break the change in the direction that matters, and
the three observations above are named as observations, not defects:

1. (Observation, low) The tool use and tool result pairing is re-implemented in
   `PendingInteractionDetector.FromMessages` rather than shared with
   `WidgetBuilder.BuildFromMessages`, and is slightly looser about which message types count.
   Proven equivalent on all real transcripts this machine holds (71 files, 132 calls, zero
   disagreement). A future drift between the two is possible; none exists today.
2. (Observation, low) The `IsMeta` skip in `FromMessages` is held by the code alone - no test pins
   it. Section 3g names the line.
3. (Residual, not a finding) The torn-write edge in section 3b: a process dying mid-write of an
   answer's tool result line could leave one answered question reading as pending. Narrow, the
   session is dead, the exit edge clears the property, and I could not construct person-facing
   harm.

One reconciliation so nobody reads a discrepancy that is not one: the proof reports the SmartRestart
filter at 47; that was measured on the pre-merge base (`217b79f63`). After the merge that this
commit is, the filter catches 95, which matches the Tech Lead's own count on this commit. The
branch's own commits add no test under the SmartRestart name (the joining test deliberately lives
in the QuestionBox namespace), so the claim "the dialog's tests were not touched" holds.

The two known-false comments in `src/CcDirector.Avalonia` and `src/CcDirector.Avalonia.Tests` are
excluded per the mandate; I grepped for any OTHER comment or document the change made untrue and
found none - `PendingInteraction.cs` is corrected, the `Session.PendingInteraction` documentation
now matches the code, and no other file claims the property is never populated.

## 5. Verdict

Within the scope of section 1, the change does what its mandate asked, the direction of error that
matters is defended by tests and by the real transcripts of this machine, my own counts match the
Tech Lead's, and a third guard beyond the Developer's two is proven held by a test. The two halves -
a real transcript, read by the real detection, stamped on a real session, reported by
`SmartShutdownSessionReader.Read` as `HasQuestionBoxOpen` - are joined by a test that no longer
needs reflection. What remains unproven is the same thing the proof names honestly: nothing here has
run against a live Claude Code session on a running Director.

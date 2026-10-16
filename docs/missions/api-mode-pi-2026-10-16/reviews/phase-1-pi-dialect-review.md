# Review: phase 1, Pi's dialect (pull request #4076)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Reviewed on 16 October 2026 against
commit 83fd7671f, the head of pull request #4076. The pull request is stacked on #4074 (the event types,
reviewed separately), so this review covers only the dialect commit, `git diff 27f4e7f5f..83fd7671f`.

## Scope

What I read:

- The whole dialect commit: `src/CcDirector.Core/ApiMode/Pi/PiDialect.cs`,
  `src/CcDirector.Core/ApiMode/Pi/PiCommand.cs`, `src/CcDirector.Core/ApiMode/SessionEventNumbering.cs`,
  and the three test files `src/CcDirector.Core.UnitTests/ApiMode/PiDialectTests.cs`,
  `PiDialectReplayTests.cs` and `PiWireLogReplay.cs`. Six files added, none changed.
- The contract, `docs/missions/api-mode-pi-2026-10-16/event-model.md`: section 2 in full (rules 1 to
  5 and the Delivery Lead's ruling on exits) and section 3 in full (3.1 to 3.7), with section 1 for the
  standard error rule.
- The event types the dialect emits, `src/CcDirector.Gateway.Contracts/ApiMode/SessionEvents.cs` and
  `SessionEventValues.cs`, at the pull request head.
- All seven real recordings in `src/CcDirector.Core.Tests/TestData/pi-rpc/` and their README. For
  each one I printed every record in recorded order (direction, type, role, disposition, stop reason,
  time) and walked the dialect's state through it by hand, beside the test's expected sequence.
- Issue #4070, the pull request description, and the phase 1 review of the event types for the shape of
  this file.
- Pi's own documentation on this machine (Pi 1.2.0, `docs/rpc.md`, `docs/rpc-commands.md`,
  `docs/rpc-extension-ui.md`, `docs/message-types.md`) for the facts the note marks documented only:
  the three prompt dispositions, the four blocking dialog methods and five fire-and-forget methods, the
  cancellation shape, and the full list of assistant stop reasons. I also read the `prompt` case of
  Pi's own handler for `--mode rpc` (`dist/modes/rpc/rpc-mode.js`, lines 298 to 317) to settle the order of the
  acknowledgement and `agent_start`, which finding 1 depends on.

What I ran, on devlinux with the .NET 10 toolkit at `$HOME/.dotnet`:

- `dotnet test src/CcDirector.Core.UnitTests -p:EnableWindowsTargeting=true --filter "FullyQualifiedName~ApiMode"`:
  45 passed, 0 failed, 0 skipped, 101 milliseconds. The twenty-five event type tests are not in that
  filter and were covered by the #4074 review.
- Three throwaway probe tests, written into the test project, run once and deleted (the tree is clean
  and nothing of them is committed), to check behaviour no committed test reaches: Pi exiting after the
  acknowledgement but before `agent_start`; a prompt acknowledgement with no `data.disposition`; and
  `CanWritePrompt` on a freshly built resuming dialect. Their output is quoted under the findings.

What I checked and found correct, so that "no finding" here means looked at, not skipped:

- Every row of sections 3.2 to 3.7 against the code: the three dispositions and the refusal; the
  `parse` response with no id refusing a waiting prompt; an unknown command and an unanswered id as
  defects; `agent_start` to `agent_settled` as the one turn, with Pi's `turn_start` and `turn_end`
  ignored; the system, user and tool-result messages ignored; `text_delta`, `text_end`,
  `thinking_delta`, `thinking_end`; `message_end` finalising blocks no `*_end` finalised and summing
  usage; `tool_execution_start` and `tool_execution_end` with the kind mapping and the output text;
  `tool_execution_update`, `agent_end`, `queue_update`, the retry, compaction and summarisation
  records and the three housekeeping records ignored; an unknown record type a defect; `aborted: true`
  as cancelled, "stopped by the user"; `stop` and `length` done, `error` failed with Pi's message, the
  retry's `finalError` used when nothing better exists; the four dialogs cancelled at once and the five
  notifications ignored; the resume stop on the warning and on `messageCount` 0 with no
  `ConnectionReady`.
- Rule 1 on every path that a recording reaches: `started`, `queued`, `handled`, refused by Pi, refused
  by the parse response, refused by the Director at exit. Rule 2 and rule 3 on abort and on exit, with a
  tool open in both (closed cancelled and failed respectively, before the one `TurnEnded`).
- The in-flight gate: false from the write, through the acknowledgement and `agent_start`, until
  `agent_settled`; true again after a refusal and after a `handled` acknowledgement; a second prompt
  written while one is in flight is a defect but still gets its receipt.
- The standard error rule: the mark is cleared at the prompt write when no turn runs and again at
  `agent_start`, so the start-up warning (written between the write and the acknowledgement in every new
  session recording) is never in a failure reason; the `exit` replay asserts an empty tail and the
  hand-built test asserts only the post-start line.
- The order of the acknowledgement and `agent_start`, which the in-flight bookkeeping depends on
  (`_deliveredAwaitingTurn` is moved into the turn at `agent_start`): Pi writes the `prompt` response
  from its preflight callback before the run begins, so the response always comes first. All seven
  recordings agree.
- The tests do assert exact event sequences for all seven recordings, not counts: `plain`, `tool`,
  `abort`, `resume`, `exit` and `exit-before-ack` as whole lists; `refusal` as its first four and last
  six lines plus the concatenation of the deltas between and the total count, which pins everything but
  the chunking of the model's text. The rules theory is on top of that, not instead of it. The
  acknowledgement is asserted byte for byte against the wire log. The clock is a `FakeTimeProvider`
  set to the recording's own milliseconds; there is no process, port, sleep or real clock.
- Project rules: every public method logs its entry and the paths that matter; there is no try-catch
  in the dialect; unknown shapes are defects, not silent fallbacks (with the two exceptions in findings
  2 and 4); test names follow Method_Scenario_Result; the prose is plain English, with `stdin`,
  `stdout` and `stderr` appearing only as the literal field values of the transcript format.

What I could not reach:

- The Windows gate script was not run; this is a Linux machine and the pull request says the same.
- The transport (phase 2) does not exist yet, so I could not see how it treats an exception thrown out
  of `OnStdoutRecord`, or whether it consults `CanWritePrompt` before `ConnectionReady`. Findings 2 and
  3 are about what that transport will meet.
- No recording provokes a dialog, a `handled` disposition, an `error` stop reason or a retry; those
  paths are checked against Pi's documentation and the hand-built tests only, like the note itself.

## Verdict

The dialect implements sections 2 and 3 of the event model faithfully on every path a recording
reaches, and the replay tests pin those paths exactly. The in-flight gate opens and closes where the
note says. Failure reasons never carry standard error from before the turn. A resume that found nothing
stops with the reason and sends no `ConnectionReady`. No tool card is left running on abort or on exit.

Four findings, none on a recorded path. Finding 1 is the one that matters: a case the Delivery Lead's
ruling does not cover and the dialect leaves half-reported, and Pi's own code shows it can happen
without a kill. Findings 2 and 3 are about the interface the transport will build on. Finding 4 is a
one-line fallback. Two observations follow, which are not findings.

## Findings

### 1. Pi gone after the acknowledgement but before `agent_start`: the prompt is Delivered, no turn ever exists, and nothing says it never ran

Place: `src/CcDirector.Core/ApiMode/Pi/PiDialect.cs`, `OnProcessExited`, lines 247 to 262. Only a
prompt still waiting for its response is refused (line 247); only a running turn is ended (line 252);
a prompt in `_deliveredAwaitingTurn` (declared line 62) is simply cleared (line 272) and the
`ConnectionEnded` reason falls through to "Pi exited (exit code N)" (line 262).

What breaks: I fed the dialect a prompt write, Pi's `disposition: started` response, and then
`OnProcessExited(137)`. The whole output was:

    PromptDelivered p1 Started
    ConnectionEnded 137: Pi exited (exit code 137) []

No `TurnStarted`, no `TurnEnded`. Rule 5 says a prompt's receipt shows its turn's outcome beside
Delivered so it "never reads as a plain success"; here there is no turn whose `promptIds` hold the
prompt, so the receipt is Delivered alone, which is exactly the reading the Delivery Lead's ruling set
out to prevent. The ruling itself has two rows, exit after acknowledging mid-turn and exit before
acknowledging, and this case is neither: acknowledged, no turn. The `ConnectionEnded` reason does not
mention that an acknowledged prompt never ran, so nothing on the wire tells the Gateway or a reader
either.

Why it is reachable, and not only by a kill in a millisecond window: the recordings show the response
and `agent_start` in the same millisecond, so a signal between them is rare. But Pi's own handler for `--mode rpc`
(`dist/modes/rpc/rpc-mode.js`, lines 298 to 317 in Pi 1.2.0) writes the `started` response from the
preflight callback and then, if the prompt fails afterwards, swallows the error: `if
(!preflightSucceeded) output(error(...))`, so after a successful preflight nothing at all is written.
Pi's documentation says the same in words: the disposition "describes the submitted prompt, not ... a
guarantee of completion" (`docs/rpc-commands.md`, line 40). In that case the process does not even
exit: the dialect holds the prompt in `_deliveredAwaitingTurn` and `_inFlight`, `CanWritePrompt` is
false for the rest of the process, no event is ever emitted, and the session is wedged behind a receipt
that says Delivered. I did not provoke this on the wire; I am reporting what the code on both sides does
if it happens.

Why it must change: rule 5 cannot be kept by the receipt reader if the dialect never gives it a turn to
read. What the right event is (a synthesised `TurnStarted` and `TurnEnded failed` for the delivered
prompts at exit, a `ConnectionEnded` reason that names them, or a new row in the ruling and the note)
is the Delivery Lead's call and then the Developer's, not mine; the live wedge with no exit needs a
transport-side answer too (a bound on how long a `started` prompt may wait for `agent_start`), which the
dialect could support by exposing the waiting prompts. What I can say is that today the dialect's
output for this case contradicts the note's rule 5, and that a test should pin whichever answer is
chosen.

Developer's answer:

### 2. A prompt acknowledgement in a shape the dialect does not expect throws after the command is forgotten, so the prompt gets neither receipt and stays in flight

Place: `src/CcDirector.Core/ApiMode/Pi/PiDialect.cs`, `OnResponse` line 305 (`Forget(id)`) before
`OnPromptResponse` line 335 (`record.GetProperty("data").GetProperty("disposition")`).

What breaks: I fed a prompt write and then `{"id":..., "type":"response", "command":"prompt",
"success":true}` with no `data`. `OnStdoutRecord` threw `KeyNotFoundException: The given key was not
present in the dictionary.` out to the caller. By then the command had already been removed from
`_awaitingResponse`, so when I then called `OnProcessExited(0)` the prompt was not refused either:

    ConnectionEnded 0: Pi exited (exit code 0) []

That is rule 1 broken: one prompt written, no `PromptDelivered`, no `PromptRefused`. And because
`_inFlight` still held the prompt, `CanWritePrompt` was false until the exit, so no further prompt
could go to that process.

Every other unexpected shape in this file is handled as a defect with the state left consistent: an
unknown disposition (line 352) refuses the prompt as the Director and reports a defect; an unknown
record type, an unknown message update, an unknown stop reason all report a defect. This one path
alone mutates state and then trusts the shape. Pi 1.2.0 always sends `data.disposition` on a successful
prompt response, so no recording reaches it; it is a shape change away.

Why it must change: rule 1 is the dialect's own promise, "never neither", and it is cheap to keep here:
read the disposition before forgetting the command, or treat a missing disposition exactly like an
unknown one (defect, `PromptRefused` by the Director, out of flight). More generally, the file has
about thirty `GetProperty` calls that throw on a missing key, and the pull request does not say what the
transport does with an exception from `OnStdoutRecord`; this prompt path is the one where the throw
leaves a receipt owed. The rest are observations, below.

Developer's answer:

### 3. On a resume, `CanWritePrompt` is true before Pi has named its conversation, so a prompt can be written into a session the dialect is about to condemn

Place: `src/CcDirector.Core/ApiMode/Pi/PiDialect.cs`, line 98, `CanWritePrompt => !_exited &&
_stopReason is null && _inFlight.Count == 0`; the resume checks are in `OnStderrLine` (line 189) and
`OnState` (line 375).

What breaks: on a freshly built dialect with `resuming: true`, `CanWritePrompt` is `true` (probed). The
pull request describes this property as the thing "the Director's queue" consults. Section 3.1 of the
note has `get_state` written "right after start, and after resume", and section 3.7 has the resume
validated by its answer or by the warning. If the queue holds a prompt when the process starts, which is
the normal case for hand back (the user typed while the Director had the wheel), the transport writes
`get_state` and, with `CanWritePrompt` true, the prompt straight after it, before either check has
answered. Pi then answers `get_state` (`messageCount` 0, the dialect requires a stop) and goes on to
accept and run the prompt in the new, empty conversation it created: `disposition: started`,
`PromptDelivered`, a model call spent, then whatever the transport does to honour the stop. The prompt
is delivered into the wrong conversation, with a receipt that says so, and section 3.7's "must fail with
that reason" becomes "fails with that reason after also running the prompt somewhere else".

Why it must change: the dialect is the component that knows whether the conversation has been named,
and it already owns the gate; a transport built from the pull request description has no reason to add
a second gate of its own. Making `CanWritePrompt` wait for `ConnectionReady` when resuming (or always,
which also gives the receipt a conversation id to be filed under) closes it in one place. If the
Developer holds that the transport must hold prompts until `ConnectionReady` regardless, then the
property's summary should say so, and the pull request's claim that it alone says when the next prompt
may go should be withdrawn.

Developer's answer:

### 4. A refusal with no error text is reported with an empty reason and no defect

Place: `src/CcDirector.Core/ApiMode/Pi/PiDialect.cs`, line 331, `Refuse(step, promptId, error ?? "",
PromptRefusedBy.Agent)`.

What breaks: a `success: false` prompt response with no `error` string produces `PromptRefused` with
`reason` "" and `refusedBy: agent`. The event model says `reason` is "the agent's own words"; the
receipt would show a refusal with no words, and because no defect is raised nothing in the log says the
agent gave none. Pi 1.2.0 always sends `error` on a failure (its handler writes `error(id, "prompt",
e.message)`), so this is not reached today.

Why it must change: the project rule is to fail explicitly, not to substitute a value that hides the
problem, and every other missing-field path in this file either reports a defect or names the absence
in words ("Pi ended the turn with an error and gave no message", line 613). One line, same pattern.

Developer's answer:

## Observations (no finding: nothing shown to break on a path the dialect can reach)

- **The queued disposition and rule 5.** In the `refusal` recording, prompt-3 is `PromptDelivered`
  `QueuedByAgent` and joins turn-1 only in the dialect's private list; `TurnStarted turn-1` had already
  gone out with `[prompt-1]`. So prompt-3's receipt can never find "the turn whose `promptIds` hold it".
  The pull request names this gap honestly and it is unreachable for a dialect that never sends
  `streamingBehavior`. It belongs with finding 1 when the Delivery Lead rules on acknowledged prompts
  that have no turn to show.
- **Exceptions from Pi's shape.** Beyond finding 2, `OnStdoutRecord` throws on any record missing a key
  it reads (`message.role`, `assistantMessageEvent.type`, `toolCallId`, `args`, `result.content`,
  `agent_settled.aborted`, `get_state`'s `sessionId`, `model.id` and `messageCount`, among others). The
  design choice to trust Pi's documented shapes is defensible for a pure translator, but the contract
  with the transport should say what happens next: a thrown record is neither a defect nor an event, and
  if the transport keeps feeding records the dialect's state after the throw is whatever the method had
  reached. The pull request description does not mention it.
- **The rules test counts prompts by searching the raw transcript text** (`PiDialectReplayTests.cs`,
  line 246: `"\"dir\": \"stdin\""` and `"\\\"type\\\": \\\"prompt\\\""`). It works on these recordings
  because the recorder's formatting is fixed. It would be sturdier parsed the way `PiWireLogReplay`
  already parses the same lines; it is not wrong today.
- **Pi's stop reasons.** `docs/message-types.md` lists `pending`, `stop`, `length`, `toolUse`, `error`,
  `aborted` and `deferred`. The dialect maps `stop`, `length` and `error`, treats `aborted` through
  `agent_settled.aborted`, and sends anything else to the defect-plus-failed branch. `toolUse` is always
  followed by another assistant message before settle, so it cannot be the last stop reason on a settled
  turn except through abort or exit, where it is carried verbatim as `agentStopReason`. `deferred` is
  undocumented in Pi's notes on `--mode rpc`; the defect branch is the right answer for it until it is seen.

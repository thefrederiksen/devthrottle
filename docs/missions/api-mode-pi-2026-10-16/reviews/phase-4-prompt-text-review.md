# Phase 4 review: the prompt's text on its queued, delivered and refused events

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4121, branch
`apimode/p4-prompt-text`, reviewed at its head commit `bc8374a8e`, whose merge base is `origin/main`
at `36e514237`. Reviewer seat (session 113), running a different agent family from the Developer
(session 103). Written 17 October 2026 on devlinux.

**Verdict: the change holds. No finding proves harm.** Every prompt source carries the exact text,
the new refusal is unreachable from product code, `required` is right on the condition stated in
note 1, and the tests are real proofs. What follows is the scope, what was checked and found right,
and eight notes for the seats that build on this: none is a defect in this pull request, and the
Developer owes no answer to them. Everything was checked against the code at the pull request's head
and against runs I made, not against the pull request's description.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full, on the branch:** the whole diff against `origin/main` (thirteen files); the three
event records and `SessionEventJson.Options` in `CcDirector.Gateway.Contracts`; the Director's send
paths end to end: `Session.SendTextAsync`, `Session.SendTextOnlyWhenWaitingForInputAsync`,
`Session.DeliverPrePromptAsync`, the two API-mode bridges in `Session`, `ApiModeSessionHost`
(`SendPromptAsync`, `TrySendPromptNowAsync`, the queue, `WriteQueuedPromptsLocked`,
`WritePromptLocked`, `RefuseQueuedLocked`, `OnProcessEndedLocked` and the restart),
`PiDialect.BuildPrompt`, `OnCommandWritten`, `OnPromptResponse`, `OnProcessExited`, the parse-error
path and `Refuse`; the callers of those sends: the Gateway prompt route in
`SessionCommandExecutor` (both the plain send and the guarded send), `StartPrePrompt`,
`FleetDoorbellRinger.RingApiModeAsync`, and the four desktop sends in `MainWindow.axaml.cs`;
`ApiModeSessionBackend.Write` (keystrokes refused); `SessionEventStream` and the logging sink; the
Gateway's `ApiModeEventIngest` (parse, field checks, receipt feed) and the bounds of
`ApiModeEventBuffer`, `ApiModeEventBatch` and `ApiModeReceiptStore`; the test rig `ScriptedPi`
(how it records the lines written to it) and `RecordingSink`; and `ErrorLine`, `ErrorReporter`,
`ErrorContext.WithholdPrompt`, `PromptAuthorBuffer`, `PromptDeliveryFailures` and
`GatewayPromptLog` for the house rules on prompt text.

**Read on the sibling open branches**, because question 3 names them: the diff of #4090
(`apimode/p4-event-push`) for how the pusher serialises, holds and drops events and what it does with
each of the Gateway's three answers; the diffs of #4090, #4091 (`apimode/p4-crash-journal`) and #4094
(`apimode/p4-wheel`) for any construction of a prompt event or a prompt command that the new
required field would break.

**Ran, on devlinux with the .NET 10 software development kit, one project at a time, after waiting for
every other test host on the machine to finish (two other sessions were running suites):**

- `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~CcDirector.Core.UnitTests.ApiMode|FullyQualifiedName~SessionEventJsonTests"`:
  192 passed, 0 failed, 0 skipped. A listing of the run's tests confirms all 29 new tests (the
  theory rows counted singly) were in it.
- `dotnet build src/CcDirector.Gateway.UnitTests -p:RunWorkspaceTypecheck=false` (no TypeScript is
  touched), then `dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~ApiMode"`:
  284 passed, 0 failed, 0 skipped.
- A search of the TypeScript packages and apps for the three event names: none types them, so no
  client parser is affected.

**Could not reach:** the Delivery Lead's ruling 15 in writing. Issue #4070 has one comment (the
Architect's proof) and no ruling text; I relied on the brief's words ("the text travels on these
three events so the Gateway can store it on the receipt row; it must be the prompt verbatim") and the
paragraph the pull request adds to `event-model.md`. Not run: the whole local gate (it runs on
Windows), the rest of the Gateway unit suite, and any real Pi. Hand back is not built, so a prompt
released by hand back could only be read from the queue code it will use (section 2, point 4).

## 2. What is right, so the next seat does not re-check it

1. **Every source reaches one send that holds the text, and nothing between it and Pi changes the
   text.** The Gateway's prompt route (`SessionCommandExecutor`, line 516) and its guarded send (line
   484) pass `request.Text` as received. The desktop's four sends, the fleet doorbell
   (`FleetDoorbellLine.For`), the framework's own text and the pre-prompt all call
   `Session.SendTextAsync` or `Session.SendTextOnlyWhenWaitingForInputAsync`; in API mode both hand
   `text` unchanged to the host. Keystrokes have no path: `ApiModeSessionBackend.Write` throws. The
   host stores the text in `QueuedPrompt(PromptId, Text)` and `BuildPrompt` puts the same string in the
   line's `message` and in `PiCommand.PromptText`. `appendEnter` is not applied in API mode, and no
   trim, newline normalisation or length cap exists on the way. Verified by reading each hop and by
   the tests, whose text has leading and trailing spaces, a blank line and a carriage return.
2. **Every event is filled from the one copy.** `PromptQueued` and the Director's refusals read the
   queue entry; the dialect's `PromptDelivered` and every `Refuse` read `PiCommand.PromptText` of the
   written command (acknowledged, refused by Pi, unknown disposition, parse error, exit before
   acknowledgement). A prompt refused before it was ever queued (Pi not running) carries the text
   from the call. All seven paths were read; the tests cover each.
3. **The new refusal in `OnCommandWritten` is unreachable from product code.** The only product
   builder of a `prompt` command is `BuildPrompt`, which always sets the text (checked: three
   `new PiCommand(` sites in `src/CcDirector.Core`, two of them for non-prompt commands). An empty
   string is not null, so a caller that sends an empty prompt behaves exactly as before: nothing that
   used to work now breaks. The guard is a programmer's guard, like the existing one on `PromptId`,
   and like it sits after the line has been written to Pi; that is unchanged in character.
4. **A prompt released from the queue carries the queue's text**, whether released after a turn,
   after Pi names its conversation, or to a restarted Pi: `WriteQueuedPromptsLocked` runs after every
   record and drains the queue through `WritePromptLocked` once `CanWritePrompt` is true; the restart
   keeps `_queue`. A prompt still queued when Pi ends for good is refused with its text
   (`RefuseQueuedLocked`). Hand back is not built; whichever release it uses will go through the same
   queue entries, which is the one place the text lives.
5. **Version skew.** The serializer options use the default handling of unknown members, so an
   older Gateway reading events with `promptText` ignores the field; its ingest and receipts are
   unchanged. The other direction is note 1 below. No recorded JSON is re-read anywhere: the pusher in
   #4090 serialises each held event from the live object when it holds it; the Gateway's buffer holds
   parsed objects in memory; receipts are rows; the logging sink's lines are read by people; the crash
   journal in #4091 serialises no session event. The pi-rpc fixtures are Pi wire logs, and both replays
   now read the text from the recorded `message`.
6. **No open sibling branch constructs a prompt event or a prompt command**, so merging this does not
   break #4090, #4091 or #4094 at build time (a search of their added lines).
7. **The tests are real proofs.** Each compares the event's text with the `message` field of the exact
   line `ScriptedPi` recorded on its standard input (`OnLineWritten` keeps the raw line), through the
   real `Session`, `ApiModeSessionHost` and `PiDialect`, with a fake clock and no real process. The
   five-row theory drives `Session.SendTextAsync` for owner typing, the Gateway route with its
   delivery id, a dictation's delivery, a fleet message and the framework; separate tests drive the
   guarded send, the real `FleetDoorbellRinger`, the real `DeliverPrePromptAsync`, and refusal. The
   JSON tests prove the round trip keeps every character and that each of the three events without
   `promptText` is refused by the shared options. The ingest tests feed the real dialect's events
   through the real ingest from recorded Pi runs. None reads the clock, sleeps or opens a port.
8. **The event model document says the same thing as the code**: the three rows and the new
   paragraph, including "exactly as it would have been" for a prompt never written, which is what
   `RefusedByDirector` carries.
9. The pull request, its commit and the branch carry no assistant attribution.

## 3. Notes for the seats that build on this (not findings against this pull request)

1. **`required` and an older Director.** A Director that has the event push (#4090) but not this
   change would send prompt events without `promptText`; a Gateway with this change refuses every
   such batch for good (a `JsonException` in `ApiModeEventIngest.Parse` becomes a null answer), and the
   pusher drops the batch, logs it, opens a hole and starts a new generation. No retry can succeed, so
   that Director's prompt receipts never land until it updates. This is harmless only if #4090 and
   #4121 ship in the same release, or API mode stays unreachable to users until both have shipped
   (phase 5 is not merged). The Delivery Lead should hold the two to the same release. With that,
   `required` is the right choice: a receipt that cannot say what was sent must not be read as one.
2. **Size of the text at the Gateway, for the Gateway Tech Lead's follow-on.** This pull request adds
   no check on `promptText` in `ApiModeEventIngest.CheckFields`, so today a prompt of any length under
   the batch limit passes. The receipt store refuses a verbatim field over 64 kilobytes whole
   (`MaxVerbatimLength`), and `Parse` is meant to refuse before the store would. When the follow-on
   stores the text with the same `Text` check, a prompt longer than 64 kilobytes (a long spawn brief)
   will cost the whole batch, including the turn's `TurnStarted` and `TurnEnded`, and the pusher will
   drop it for good. The follow-on has to decide how a long prompt is kept, before the check is added.
   Separately: the per-session buffer bound is 2,000,000 characters and the batch bound 4,000,000, and
   the text now travels up to three times per prompt; a single event over the batch bound is dropped
   by name by the pusher.
3. **Prompt text is customer content.** The Gateway already keeps prompt text in `GatewayPromptLog`,
   partitioned by tenant directory and under a bounded retention sweep, because "prompt text is
   customer content - proprietary code, file paths, error output, pasted credentials". The receipt row
   must get the same treatment: it is already written inside the tenant's scope, but retention of the
   text on receipts is the follow-on's to settle.
4. **The Director's local log.** With no Gateway configured, `LoggingSessionEventSink` writes each
   event as JSON, so every API-mode prompt now lands in the Director's log in full, where a terminal
   send logs at most 60 characters and the two delivery ledgers keep no text at all. With a Gateway,
   the pusher in #4090 replaces that sink and logs lengths only. This cannot reach an error report:
   `ErrorLine.IsError` reads the marker only before the first colon-space and stops at the first
   double quote, and the JSON begins with one, so a prompt containing FAILED or ERROR does not turn
   the line into a report (I checked the rule, not just the comment). The agent's replies and tool
   inputs were already logged the same way. An observation for the Director Tech Lead, not harm.
5. **Three new paths are proven only by reading**, because no test can reach them without a real
   process: the desktop's four `SendTextAsync` calls in `MainWindow.axaml.cs`, and the `/handover`
   framework text. Each passes its text straight to the send the tests do cover.
6. **The ruling is not on record where the mission document says records live.** Issue #4070 holds no
   ruling text; `event-model.md` now cites "the Delivery Lead's ruling 15, 17 October 2026". The
   Delivery Lead should land the ruling's words in the mission record so the citation resolves.
7. **The guard throws after the write.** `OnCommandWritten` raises its `ArgumentException` for a
   text-less prompt command after the line has gone to Pi, as the existing `PromptId` guard does. If a
   future dialect ever builds a prompt command another way, a prompt would reach Pi with no receipt
   waiting. Not reachable today; worth a check in `BuildPrompt`'s one caller if that ever changes.
8. **Hand back** will carry the text if, and only if, it releases prompts through the existing queue
   entries. A hand back that re-reads prompts from anywhere else would need its own proof.

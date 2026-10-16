# Phase 0: the session event model and the Pi mapping

Mission: API mode for agent wrappers, proven on Pi (issue #4070). This note is what phases 1 and 2
build against. Every Pi fact in it was **seen on the wire** in the recorded fixtures
(`src/CcDirector.Core.Tests/TestData/pi-rpc/`, Pi 1.2.0 on devlinux, 16 October 2026) unless it is
marked **documented only**, which means it comes from Pi's own `docs/rpc.md`, `docs/json.md` or
`docs/rpc-commands.md` and was not provoked.

## 1. Framing

Pi's standard input and output carry **newline-delimited JSON: one JSON object per record, ended
by a line feed (byte 0x0A)**.

- Split records on line feed **only**. Strip one carriage return before the line feed if present.
  Never use a reader that also breaks on Unicode line or paragraph separators (U+2028, U+2029):
  they are legal inside JSON strings. .NET's `StreamReader.ReadLine` does not split on those two,
  but it **does** split on a lone carriage return, which is also legal inside a JSON string. So
  phase 1 reads bytes and splits on 0x0A itself.
- Read standard output continuously; Pi stalls when the pipe fills. Records can be large: the first
  turn of a new session carries the whole system prompt (about 10 kilobytes in the redacted
  fixtures, 17 before the skill descriptions were redacted), and a run's `agent_end` repeats every
  message of the run, so no line-length limit below a few megabytes.
- Standard error is diagnostics, never protocol. For a failure reason, keep **only the lines
  written since the current turn started** (or since the prompt was written, when no turn started).
  Every new session writes a start-up line that has nothing to do with a later failure: `Warning:
  No project session found with id '<id>'; creating a new session with that id.` In the `exit`
  fixture that warning is the only standard error line, written 3.2 seconds before the kill, so
  "the last lines" would blame the wrong thing.
- Close standard input to ask Pi to exit; it exited 0 in every recorded run.

## 2. The session event model

Agent-neutral. Every API dialect (Pi now; other agents and the Agent Client Protocol later)
reports these and nothing else upward. The words are the Agent Client Protocol's (prompt turn,
agent message chunk, agent thought chunk, tool call and its status, stop reason, request
permission, cancel); the shape is ours.

Every event carries: `sessionId` (the DevThrottle session), `sequence` (rising per session, so a
client can order and de-duplicate), and `at` (time observed, from an injected clock). Events inside
a turn also carry `turnId` (assigned by the Director; Pi has no turn id of its own).

| Event | Fields | Meaning |
|---|---|---|
| `PromptQueued` | `promptId`, `reason` (`userHasTheWheel`, `turnRunning`, `agentStarting`), `position` | The Director is holding the prompt; it has not been written to the agent. |
| `PromptDelivered` | `promptId`, `disposition` (`started`, `queuedByAgent`, `handled`), `acknowledgement` (the agent's own reply, verbatim) | The agent itself acknowledged the prompt. This is the receipt. |
| `PromptRefused` | `promptId`, `reason` (the agent's own words, or the Director's when the agent never answered), `refusedBy` (`agent`, `director`) | The prompt did not enter the conversation, and why. |
| `TurnStarted` | `turnId`, `promptIds` | The agent began working on the prompt(s). |
| `MessageText` | `turnId`, `messageId`, `channel` (`reply`, `thought`), `kind` (`delta`, `final`), `text` | Agent text. `delta` is appended for a live view; `final` replaces everything streamed for that message. `thought` is the Agent Client Protocol's thought chunk; whether it is shown is the Gateway's ruling (phase 2). |
| `ToolCallStarted` | `turnId`, `toolCallId`, `toolName`, `kind` (`read`, `edit`, `execute`, `search`, `fetch`, `other`), `input` (verbatim) | A tool began running: the card appears as running. |
| `ToolCallFinished` | `turnId`, `toolCallId`, `outcome` (`succeeded`, `failed`, `cancelled`), `output` (text), `durationMilliseconds` | The tool finished: the card goes to done or failed. |
| `TurnEnded` | `turnId`, `outcome` (`done`, `cancelled`, `failed`), `reason` (text; required unless `done`), `agentStopReason` (verbatim), `usage` (input, output, cache read, cache write tokens) | The turn is over and the agent will not continue on its own. |
| `PermissionRequested` | `requestId`, `turnId`, `toolCallId` (optional), `title`, `options` (each `optionId`, `name`, `kind`: `allowOnce`, `allowAlways`, `rejectOnce`, `rejectAlways`) | The agent waits for a choice. Answered by a `permission answer` command (`optionId`, or cancelled). **Pi never raises one** (see section 3.6). |
| `ConnectionReady` | `agentSessionId`, `model` | The agent process is up and named its conversation. Needed for take the wheel (`pi --session-id <agentSessionId>`). |
| `ConnectionEnded` | `exitCode`, `reason`, `stderrTail` | The agent process is gone, expected or not. |

`ConnectionReady` and `ConnectionEnded` are **additions** to the list in the mission document's
section 5. Without them the Director cannot tell "idle" from "the process died between turns", and
take the wheel needs the agent's own conversation id.

Rules every dialect keeps:

1. **One `PromptDelivered` or one `PromptRefused` per prompt written to the agent, never both,
   never neither.** If the agent exits before acknowledging, the Director emits `PromptRefused`
   (`refusedBy: director`, reason "Pi exited before acknowledging"; seen in `exit-before-ack`).
2. **Every `TurnStarted` gets exactly one `TurnEnded`**, including when the process dies.
3. **Every `ToolCallStarted` gets exactly one `ToolCallFinished`.** A tool still running when its
   turn ends is closed by the dialect as `cancelled` (turn cancelled) or `failed` (turn failed), so
   no card is left spinning.
4. Verbatim fields (`acknowledgement`, `input`, `agentStopReason`) are the agent's own data,
   untranslated, so a receipt can be checked against the wire log.

5. **A prompt's receipt shows its turn's outcome beside Delivered.** A receipt is the prompt's
   `PromptDelivered` plus the `TurnEnded` of the turn whose `promptIds` hold it. When that turn
   ended `failed` or `cancelled`, the receipt shows it next to Delivered, never Delivered alone.

**Delivery Lead ruling (16 October 2026) on the failure case in section 7 of the mission
document.** The original wording: "Pi exits mid-turn: the session shows failed with the reason,
and the prompt is not marked delivered." On the wire Pi acknowledges a prompt 0.1 to 0.4 seconds
after it is written, long before a turn can be cut short, so the case splits in two:

| Case | What the model emits | Fixture |
|---|---|---|
| Pi exits **after** acknowledging (mid-turn) | `PromptDelivered` stays: Delivered means Pi acknowledged it (section 3 of the mission document). The turn ends `TurnEnded`, `failed`, reason = the exit code and the standard error lines written since the turn started. Rule 5: the receipt shows that failed turn beside Delivered, so it never reads as a plain success. Then `ConnectionEnded`. | `exit` (real) |
| Pi exits **before** acknowledging | `PromptRefused`, `refusedBy: director`, reason "Pi exited before acknowledging". No turn. Then `ConnectionEnded`. | `exit-before-ack` (real: Pi killed 50 milliseconds after the prompt was written; it wrote nothing) |

## 3. The Pi mapping

Pi is started as `pi --mode rpc --session-id <id>`, with no `CC_*` variables in its environment.
Every command is sent with a unique `id`; Pi repeats it on the `response`. Session events carry no
id.

### 3.1 Commands we send

| Command | When | What we use from the response |
|---|---|---|
| `get_state` | Right after start, and after resume | `sessionId` and `sessionFile` (for `ConnectionReady` and take the wheel), `model`, `isStreaming`, `pendingMessageCount`, `messageCount` (a resume that found nothing shows 0; Pi also writes the "No project session found" warning) |
| `prompt` | To deliver a prompt. Never while another prompt is **in flight** (below): the Director's own queue holds it (`PromptQueued`, `turnRunning`). Never with `streamingBehavior`. | The acknowledgement, below |
| `abort` | Stop | `success: true`; see 3.4 |
| `get_session_stats` | After each turn | `tokens` for the spend record (`cost` is 0: the model has no price) |
| `get_last_assistant_text` | Optional check after a turn | `data.text`; **absent** (not `null`, despite the documentation) after an aborted turn with no text |

**In flight.** A prompt is in flight from the moment its line is written to Pi until its turn's
`agent_settled`, or until its refusal, or until a `handled` acknowledgement (no turn follows). While
one prompt is in flight, the Director's queue holds every other prompt; `turnRunning` begins at the
write, not at `agent_start`. The `refusal` fixture shows why: Pi **refuses** a second prompt that
arrives while it is busy ("Agent is already processing"), it does not queue it, and between the
write and `agent_start` there is a window of 0.3 to 0.4 seconds (longer under load) in which a
Director that waited for `agent_start` would send a second prompt straight into that refusal.

`steer`, `follow_up`, `clear_queue`, `bash`, `compact`, `new_session`, `switch_session`, `fork` and
the rest are **not used**: steering is out of scope, and the Director owns the queue.
`get_commands` is not needed at run time; it is in the plain fixture because its list shows the
fleet's skills and Pi's extensions load in API mode (a lesson from `StudioBackend`).

### 3.2 Delivered and refused

| Pi record | Model event |
|---|---|
| `{"id":<ours>,"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}` | `PromptDelivered`, `started`. **This is the proof of Delivered.** It arrives before `agent_start`. |
| same, `"disposition":"queued"` (seen in `refusal`: a prompt sent with `streamingBehavior: followUp` during a turn) | `PromptDelivered`, `queuedByAgent`. Pi also emits `queue_update` first. Unreachable for our dialect, which never sends `streamingBehavior` (a busy Pi refuses a prompt without it, row below); mapped so a later dialect that steers needs no new design. |
| same, `"disposition":"handled"` (**documented only**: an extension command consumed it) | `PromptDelivered`, `handled`. No turn follows; do not wait for `agent_settled`. |
| `{"id":<ours>,"type":"response","command":"prompt","success":false,"error":"Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message."}` (seen in `refusal`) | `PromptRefused`, `refusedBy: agent`, `reason` = `error` verbatim. |
| `{"id":<ours>,"type":"response","command":"no_such_command","success":false,"error":"Unknown command: no_such_command"}` (seen) | Not a prompt: a dialect defect. Logged and raised as an error, never shown as a receipt. |
| `{"type":"response","command":"parse","success":false,"error":"Failed to parse command: ..."}` (seen; **no id**) | We wrote a line Pi could not parse: a dialect defect. If a prompt is waiting for its acknowledgement, it is `PromptRefused` (`refusedBy: director`, reason quoting `error`). |
| End of standard output before the response (seen in `exit-before-ack`: standard output is empty) | `PromptRefused`, `refusedBy: director`, "Pi exited before acknowledging", then `ConnectionEnded`. |

### 3.3 The turn

**A model turn is Pi's agent run: from `agent_start` to `agent_settled`.** Pi's own `turn_start`
and `turn_end` are one model call each (a prompt that runs a tool has two of them, as `tool` shows),
so they are **not** our turn.

| Pi event | Model event, or why ignored |
|---|---|
| `agent_start` | `TurnStarted` (the first since the last `agent_settled`), `promptIds` = the delivered prompt(s). |
| `turn_start`, `turn_end` | Ignored: one model call inside our turn. `turn_end.toolResults` duplicates `tool_execution_end`. |
| `message_start` / `message_end` with `role: "system"` | Ignored. Pi 1.2.0 puts its whole system prompt on the wire at the first turn of a new session (about 10 kilobytes redacted; not in Pi's own event documentation). It is not conversation. |
| `message_start` / `message_end` with `role: "user"` | Ignored as display (the Chat shows the prompt from our own record). For a prompt Pi queued, this is the moment it starts, and it is attached to the current turn's `promptIds`. |
| `message_start` with `role: "assistant"` | Opens `messageId` for the following `MessageText` (Director-assigned, one per assistant message). |
| `message_update`, `assistantMessageEvent.type` = `text_delta` | `MessageText`, `reply`, `delta`, `text` = `delta`. |
| `text_end` | `MessageText`, `reply`, `final`, `text` = `content` for that `contentIndex`. |
| `thinking_delta`, `thinking_end` | `MessageText`, `thought`, `delta` / `final`. |
| `text_start`, `thinking_start` | Ignored: the first delta opens the block. |
| `toolcall_start`, `toolcall_delta`, `toolcall_end` | Ignored: the model writing a tool's arguments. The card starts at `tool_execution_start`, when the tool actually runs. |
| `message_end` with `role: "assistant"` | Its `content` is the authoritative final text (a `final` for any block not yet finalised). Its `usage` is added to the turn's `usage`. Its `stopReason` and `errorMessage` are kept for `TurnEnded`. |
| `message_start` / `message_end` with `role: "toolResult"` | Ignored: duplicates `tool_execution_end`. |
| `tool_execution_start` | `ToolCallStarted`: `toolCallId`, `toolName`, `input` = `args`. `kind`: `bash` is `execute`; `read` is `read`; `edit` and `write` are `edit`; anything else `other`. |
| `tool_execution_update` | Ignored in this mission: the card goes from running to done, and partial output is not shown. It maps to a later `ToolCallProgress` if wanted. |
| `tool_execution_end` | `ToolCallFinished`: `isError: false` is `succeeded`, `true` is `failed`; `output` = the text of `result.content`; `durationMilliseconds` = `durationMs`. |
| `agent_end` | Ignored: Pi can still retry or run a queued prompt (`willRetry`). It repeats every message of the run. |
| `agent_settled` | `TurnEnded`. `aborted: true` is `cancelled`. Otherwise from the last assistant `stopReason`: `stop` and `length` are `done` (`length` with the reason "the reply hit the model's length limit"); `error` is `failed` with `errorMessage` (**documented only**). |
| `queue_update` | Ignored as display: the Director owns the queue. Used only to notice that a Pi-queued prompt started. |
| `auto_retry_start`, `auto_retry_end` (**documented only**) | Ignored; a final `auto_retry_end` with `success: false` gives the `failed` reason (`finalError`) if `agent_settled` follows without a better one. |
| `compaction_start`, `compaction_end`, `summarization_retry_*` (**documented only**) | Ignored in this mission: housekeeping inside a turn. |
| `extension_error` (**documented only**) | Logged; not a model event. |
| `session_info_changed`, `thinking_level_changed`, `entry_appended` (**documented only**) | Ignored. |
| `extension_ui_request` (**documented only**) | See 3.6. |

### 3.4 Stop

Sent: `{"id":<ours>,"type":"abort"}`. Seen in `abort`, 11 milliseconds later: the assistant
`message_end` with `stopReason: "aborted"` and `errorMessage: "OpenAI Responses stream ended before
a terminal response event"`, then `turn_end`, `agent_end`, `{"type":"agent_settled","aborted":true}`,
and **last** the abort's own `{"type":"response","command":"abort","success":true}`: Pi answers the
abort only once the session is idle. The model emits `TurnEnded`, `cancelled`, reason "stopped by
the user". `abort` while idle answers `success: true` with no events (seen in `refusal`), so Stop
when nothing runs is harmless and emits nothing.

### 3.5 Pi exiting mid-turn

Seen in `exit` (the recorder sent SIGKILL during the reply): standard output simply ends after a
`message_update`. There is no `message_end`, `turn_end`, `agent_end` or `agent_settled`, and the
fixture records exit code -9. That number is the recorder's: Python reports a child ended by a
signal as minus the signal number. A .NET `Process` on Linux reports 128 plus the signal, 137, so
phase 1's real path and its replay tests must agree on which they assert. The model emits
`TurnEnded`, `failed`, reason "Pi exited during the turn (exit code 137)" plus the standard error
lines written since the turn started (section 1; in this fixture there are none, so the reason is
the exit alone), then `ConnectionEnded`. The prompt stays `PromptDelivered`, and its receipt shows
the failed turn (the ruling in section 2). Resuming the same session afterwards works: Pi had saved
nothing of the cut-off reply, and its stats report 0 tokens for it.

### 3.6 Permission requests

Pi never asks permission for a tool. The nearest Pi record is an extension's dialog,
`extension_ui_request` with one of the four dialog methods, `select`, `confirm`, `input` or
`editor` (**documented only**; none of the extensions loaded here raised one). Each blocks Pi until
the client answers with an `extension_ui_response`, unless the request carries a `timeout` (it is
optional). **Recommendation for phase 1:** the Pi dialect answers all four at once with the one
cancellation shape Pi accepts for any dialog,
`{"type":"extension_ui_response","id":<the request's id>,"cancelled":true}`, and logs it, so an
extension can never hang an API-mode turn; mapping dialogs to `PermissionRequested` waits for an agent that needs it.
The fire-and-forget methods (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`)
are logged and ignored.

### 3.7 Resume by session id

Seen in `resume`: a new `pi --mode rpc --session-id <id>` process in the **same working folder**
answered the follow-up from the earlier conversation (`PONG`), wrote no "No project session found"
warning, and `get_state` showed `messageCount: 5`. The system message is not sent again on a
resumed session. Pi's session ids are scoped to the working folder, so take the wheel and hand back
must start Pi in the same folder every time. A resume that silently created a new session is caught
by `messageCount` being 0 or the standard error warning, and must fail with that reason.

## 4. The fixtures, and where the tests load them

`scripts/record-pi-rpc-fixture.py` records them; the fixture folder's README lists each scenario.
They are in `src/CcDirector.Core.Tests/TestData/pi-rpc/`, which **`CcDirector.Core.UnitTests`**
already links and copies to its output (`TestData\**` in its project file; checked with MSBuild:
all fourteen wire-log files are copied, no project change needed). That project is chosen because it is the
Director's suite in the **default gate** of `scripts/test-local.ps1` on origin/main (1,418 tests,
inside the two-minute budget); `CcDirector.Core.Tests` itself is parked and would leave the replay
tests unrun on every ordinary change. The shared folder keeps one copy for both.

Every fixture is real, including the refusal and the exit before acknowledgement. None is hand-built. `.gitattributes` marks the folder
`-text` so a Windows checkout keeps line feeds and the framing tests see the bytes Pi wrote.

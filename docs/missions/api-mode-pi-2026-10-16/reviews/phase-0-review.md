# Phase 0 review: the session event model and the Pi wire-log fixtures

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Pull request #4071, branch
`apimode/p0-event-model` at commit `0fd10d8bf`. Reviewer seat, opened by the Delivery Lead
(session 102), running a different agent family from the Developer. Written 16 October 2026.

**Verdict: the phase holds. Four findings, none of which blocks phase 1 from starting; each is a
sentence or two in the note or a small redaction, and the Developer decides.** Everything the
Delivery Lead asked me to judge was checked against the fixtures themselves and against Pi's own
documentation on this machine, not against the pull request's description.

## 1. Scope: what I read, what I ran, what I could not reach

**Read, in full:** the mission document (issue #4070, sections 3, 5, 6 and 7 in particular);
`event-model.md`; `spend.md`; the mission `README.md`; the fixture folder `README.md`;
`scripts/record-pi-rpc-fixture.py`; the `.gitattributes` change; all fourteen wire-log files
(every record of every transcript, condensed to a timeline, plus the full records the note makes
field-level claims about: the system message and its sections, `get_state`, `get_session_stats`,
`get_commands`, `text_end`, `thinking_delta`, the three `tool_execution_*` records, `agent_end`,
`agent_settled`, and the aborted assistant `message_end`). Pi 1.2.0's own `docs/rpc.md`,
`docs/rpc-commands.md`, `docs/json.md` and `docs/rpc-extension-ui.md` from the installed package,
for every claim the note marks **documented only**.

**Ran:**

- A script over every `*.stdout.jsonl`: every line parses as JSON; zero carriage-return bytes in
  every file; every non-empty file ends with a line feed; `exit-before-ack.stdout.jsonl` is zero
  bytes. The same check on the committed blobs (`git show HEAD:<file>`), not only the checkout.
- An enumeration of every record type, every `assistantMessageEvent.type`, every message role,
  every tool name and every response (command, success, id present) across all fixtures, compared
  line by line with the mapping tables in sections 3.1 to 3.5 of the note.
- `git check-attr` on the fixture files: `text: unset`, so the `-text` attribute applies.
- `dotnet build` of `src/CcDirector.Core.UnitTests` with the .NET 10.0.301 software development kit
  on devlinux: build succeeded, and all fifteen files of `TestData/pi-rpc/` (fourteen wire logs
  plus the README) are in `bin/Debug/net10.0/TestData/pi-rpc/`, byte-identical in size and with
  zero carriage returns. The pull request's "checked with MSBuild" claim is confirmed by a run, not
  read.
- A sweep of the fixtures for private text: the user name, the company and machine names, home
  folders other than `/home/fixture`, addresses, key-shaped strings, and `description` fields not
  carrying the redaction text. Result in finding 3 below; nothing else found. The long hexadecimal
  strings in `resume` are the provider's message identifiers inside `textSignature`, not secrets.
- A recomputation of every row of `spend.md` from its own rates: every row and all three totals
  agree to the cent. The recording 2 rows were checked against the `get_session_stats` tokens in
  the committed fixtures (plain, tool, resume, refusal): they match. The Architect's row was
  checked against the stats posted in the issue's first comment: it matches.
- A sweep of the three branch commits for assistant attribution: none.

**Could not reach:** the recording 1 fixtures (discarded before commit, so their rows in `spend.md`
rest on the Developer's word); a real Pi run of the recorder (I read it and did not run it, since
the fixtures already exist and a run would spend from the grant for no new fact); Windows
(line-ending behaviour on a Windows checkout is argued from `.gitattributes`, not observed).

## 2. What is right, so the next seat does not re-check it

- **Every Pi record on the wire is in the mapping.** The fixtures carry thirteen record types
  (`agent_start`, `agent_end`, `agent_settled`, `turn_start`, `turn_end`, `message_start`,
  `message_end`, `message_update`, `queue_update`, `response`, and the three `tool_execution_*`),
  eight `assistantMessageEvent` types (`text_start`, `text_delta`, `text_end`, `thinking_start`,
  `thinking_delta`, `toolcall_start`, `toolcall_delta`, `toolcall_end`) and four message roles
  (`system`, `user`, `assistant`, `toolResult`). Each one has a row in section 3.3 that maps it or
  ignores it with a reason. The records Pi documents and the fixtures do not show (`auto_retry_*`,
  `compaction_*`, `summarization_retry_*`, `extension_error`, `session_info_changed`,
  `thinking_level_changed`, `entry_appended`, `extension_ui_request`, `bash_execution_update`) are
  all named, and all but the last are marked **documented only**; `bash_execution_update` belongs
  to the `bash` command the dialect does not send, so its absence is right.
- **Delivered, refused, Stop and mid-turn exit are each defined from real wire evidence**, and the
  evidence says what the note says it says: the `prompt` response with `disposition: started`
  arrives in the same millisecond as `agent_start` and before it (plain, tool, abort, refusal,
  exit); the refusal text is Pi's verbatim `error`; the abort's own response comes **after**
  `agent_settled` with `aborted: true`, eleven milliseconds after the abort was written; the killed
  process's output ends after a `thinking_delta` with no `agent_settled`; the exit before
  acknowledgement wrote nothing at all. `get_last_assistant_text` after the aborted turn answers
  `data: {}` with no `text` key, as the note says and against Pi's documentation, which says
  `null`. `abort` while idle answers `success: true` with no events. The resumed session shows
  `messageCount: 5`, no system message and no standard error warning. All as written.
- **Section 5 of the mission is covered.** Every event the mission lists has a row with its fields
  (prompt queued, delivered with the agent's acknowledgement verbatim, refused with the reason, turn
  started, message text, tool call started and finished, turn ended with done, cancelled or failed,
  and a permission request with options). The two additions (`ConnectionReady`, `ConnectionEnded`)
  are flagged as additions with their reason, which is the honest way to extend a mission's list.
- **The fixtures are real and labelled honestly.** Each transcript's `meta` record names the
  recorder, Pi 1.2.0, the date and the redaction applied. The timings are consistent with a real
  process (about 0.3 seconds from start to the first write, 0.6 seconds to the session warning on
  standard error, 1 to 2 seconds to the model's first token). The exit-before-acknowledgement
  recording was taken 50 milliseconds after the prompt was written, and the recorder refuses to
  keep that scenario if Pi answered first, so "no response" is a verified absence, not an unlucky
  empty file.
- **The recording script is sound on the two points asked.** Standard output is read with a binary
  `readline`, which splits on the line feed byte only, and the stdout fixture is written back with
  a bare line feed. Every `CC_*` variable is stripped from Pi's environment in both places the
  script starts Pi (the scenario runs and the stats-only second process in the exit scenario), so
  Pi does not register as a fleet session.
- **The fixtures land where the default gate runs.** They sit in the parked suite's folder and are
  linked into `CcDirector.Core.UnitTests` by its existing `TestData\**` glob, confirmed by build.
  `.gitattributes` keeps them `-text`.
- **`spend.md` is honest.** The rates are deliberately high, the estimates are labelled as
  estimates with their basis, the discarded recording is counted rather than forgotten, the
  Architect's unrecorded earlier runs are said to be unrecorded, and the exit-before-acknowledgement
  row is zero for a stated reason (killed before Pi reached the model, which the empty output
  supports).

## 3. Findings

Each finding states the harm: what breaks in a later phase and why. The Developer who built the
work accepts or declines each one with a reason (law 11).

### Finding 1 (design gap): the note never says when "a turn is running" begins, and the fixtures show that the obvious reading loses a prompt to a Pi refusal

Section 3.1 says the dialect sends `prompt` "never while a turn is running: the Director's own
queue holds it (`PromptQueued`, `turnRunning`)". The only definition of a turn in the note is
section 3.3: "a model turn is Pi's agent run: from `agent_start` to `agent_settled`", with
`TurnStarted` emitted on `agent_start`. Section 3.2 then says `disposition: queued` is "only
reachable in a race, since we do not send during a turn".

Those two statements do not agree with the wire. The `refusal` fixture shows what Pi does to a
second prompt that arrives while it is busy and carries no `streamingBehavior`: it **refuses** it
(`success: false`, "Agent is already processing"). It does not queue it. So a race does not produce
`queued`; it produces a refusal. And the race exists: a Director that marks the turn as running
only when it sees `agent_start` has a window from the moment it writes prompt A until A's
acknowledgement and `agent_start` are read back (0.3 to 0.4 seconds in every fixture, longer under
load), in which a prompt B from the user is written straight to Pi and refused.

**The harm, in phase 1 and 2:** a user who sends two prompts a second apart in API mode sees the
second one marked **Refused: "Agent is already processing..."** instead of queued and delivered.
The mission's goal (section 3) is that a prompt is Delivered or queued, with a refusal reserved for
something the agent would not take. This is a prompt the Director should have held. It is not a
silent loss, so it does not break the first bullet of the goal, but it breaks the queue the fifth
bullet promises, and a phase 1 built literally from the note will do it.

**What would settle it:** one rule in section 2 or 3.1 saying that a prompt is in flight from the
moment it is written until the `agent_settled` of its turn (or its refusal), and that the Director's
queue holds every other prompt for that whole span, so `turnRunning` begins at the write, not at
`agent_start`. Section 3.2's "only reachable in a race" line should then say `queued` is unreachable
unless the dialect sends `streamingBehavior`, which it does not.

### Finding 2 (documented-only gap): section 3.6 names two of Pi's four blocking dialog methods

Section 3.6 says the nearest Pi record to a permission request is `extension_ui_request` with
`method` `select` or `confirm`, and recommends that phase 1 answer "every dialog" `cancelled: true`
at once so an extension can never hang a turn. Pi's `docs/rpc-extension-ui.md` lists **four**
dialog methods that block until answered: `select`, `confirm`, `input` and `editor`. The note's
list of fire-and-forget methods (`notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`)
is complete.

**The harm, in phase 1:** the note is what the dialect is built from, and "every dialog" is defined
by the note's own list of two. A dialect that answers `select` and `confirm` and logs everything
else leaves an `input` or `editor` request unanswered. Without a `timeout` on the request (it is
optional) Pi blocks the extension handler until a response arrives, which is exactly the hung
API-mode turn the recommendation exists to prevent. No extension loaded here raises one, so no
fixture shows it, which is why it has to be right in the note.

**What would settle it:** name all four in 3.6, and say the cancellation response is one shape for
all of them (`{"type":"extension_ui_response","id":<theirs>,"cancelled":true}`).

### Finding 3 (private text): the model's display name carries the owner's company and deployment name

The redactor replaces the provider's address with `https://model-provider.invalid` but leaves the
model object's `name` as recorded: the provider, the model, and the owner's company and deployment label in brackets (quoted here originally; replaced by the Developer so the name does not reach main). It appears once in each of
`plain`, `refusal` and `resume` (both the stdout and the transcript file, six files in all), inside
the `get_state` response.

**The harm:** nothing breaks in a later phase. This is a judgment on the Delivery Lead's third
question (free of secrets or private text). It is not a secret. It is a company name and an internal
deployment label in a public repository, of the same kind as the skill descriptions and the user name
the Developer chose to redact, and the fixture README's "nothing else was changed" means a reader
takes it as published on purpose. `ConnectionReady` carries `model`, so phase 2's tests and the
Cockpit's display will copy whatever string sits here. If the Developer keeps it, a line in the
fixture README saying so is enough; if not, the redactor needs one more pair (the model `name`, and
possibly `id`, to a neutral value).

### Finding 4 (reason text): "the last standard error lines" as the failure reason picks up the start-up warning

Section 3.5 says the mid-turn exit maps to `TurnEnded`, `failed`, with the reason "Pi exited during
the turn (exit code -9) plus the last standard error lines", and section 1 says to keep standard
error's last lines for the failure reason. In the `exit` fixture the **only** standard error line is
the start-up warning `Warning: No project session found with id '...'; creating a new session with
that id.`, written 3.2 seconds before the kill and unrelated to it. Every new session writes that
line.

**The harm, in phase 2:** the Gateway folds this reason into the session's failed verdict (rule 7:
the client renders it verbatim), so a user whose Pi died mid-turn reads a warning about a missing
session file as the cause. That is a wrong explanation shown as the truth, which is the failure
mode rule 7 exists to stop.

**What would settle it:** keep only the standard error lines written since the turn started (or
since the last record on standard output), and say so in sections 1 and 3.5. The fixture already
has the timings to test it.

## 4. Notes (no harm; for the Developer's information)

- **The "17 kilobyte system message" is not in the fixtures.** The `message_start` with role
  `system` is 10,381 bytes in `plain` and `exit`; the largest record anywhere is the `agent_end`
  of `tool` at 12,092 bytes. The 17 kilobytes was presumably the unredacted recording 1 size. The
  rule drawn from it (no line limit below a few megabytes) stands either way, but the note promises
  that every Pi fact was seen in the recorded fixtures, and this number was not.
- **`exit` code -9 is the recorder's convention, not Pi's.** Python reports a signal-killed child
  as the negative signal number. A .NET `Process` on Linux reports 128 plus the signal, 137, and the
  note says so in passing. The fixture README labels the `exit` record "Pi's exit code"; a phase 1
  test that asserts the reason string from the transcript's value will pass on the fake and differ
  from what the real path writes. Worth one line in the README.
- **The redactor learns the provider address only from a `get_state` response.** The redactor is
  shared across scenarios and `plain` runs first, so in the committed set no address leaked (swept:
  the only addresses on the wire are the replacement). But the script allows running a subset
  (`abort` alone, say), in which case the address is never learned and anything carrying it would
  be published. A post-pass check that the real values are absent from the written files would make
  the redaction self-verifying.
- **The recorder's stdout reader parses each line as JSON before writing it.** A line Pi wrote
  that is not JSON would end the reader thread without a message, and the run would time out 180
  seconds later waiting for an `_eof` that never comes. Pi reserves standard output for JSON lines,
  so this did not happen; it is a robustness note only.
- **The transcript replay rule works, with one condition.** "Answer each `stdin` record with the
  `stdout` records that follow it" reproduces every scenario, including the mid-turn refusal and
  the abort, provided the phase 1 test writes the same commands in the same order as the recorder
  did. A fake that waits for the next `stdin` before emitting the records after it (rather than
  emitting them at once) would deadlock on `refusal`, where the first turn's deltas sit between the
  follow-up prompt and the next read. The README might say "emits at once".
- **`get_commands` names every fleet skill.** The descriptions are redacted; the twenty skill names
  and their locations under `/home/fixture/.agents/skills/` are published. The note says this is on
  purpose (it is the proof that skills load in API mode). I note it only so the choice is visible.

## 5. Against the Delivery Lead's questions, in one place

| Question | Answer |
|---|---|
| Is `event-model.md` complete against section 5 of the mission? | Yes. Every listed event has a row and fields; two additions are flagged as such. |
| Is it correct against the recorded wire logs? | Yes on every claim marked as seen; the four findings are gaps, not errors in what is written. Every record on the wire is mapped or ignored with a reason. |
| Are Delivered, refused, Stop and mid-turn exit each defined from real wire evidence? | Yes, and I re-read the evidence: it says what the note says. |
| Are the fixtures real, labelled honestly? | Yes. Recorder, version, date, timings and redaction are all in the `meta` records and the README, and the two "nothing happened" scenarios are verified absences. |
| Free of secrets or private text? | Free of secrets. One piece of private text, finding 3, for the Developer to keep or redact. |
| Loadable by the Director unit tests? | Yes, confirmed by building `CcDirector.Core.UnitTests`: all fifteen files copied, line feeds intact. |
| Is `spend.md` honest? | Yes. Every row recomputes; every verifiable row matches its source; the estimates and the gaps are labelled. |
| Is the recording script sound (line-feed-only framing, `CC_` variables stripped)? | Yes on both. Three robustness notes in section 4, none of which affected the committed fixtures. |

## 6. The Developer's answers

Written by the Developer (session 107), 16 October 2026. Every finding is accepted; every note is
answered.

| Item | Answer | What changed |
|---|---|---|
| Finding 1: when "a turn is running" begins | **Accepted.** The harm is real: a Director that starts holding prompts at `agent_start` sends a second prompt into a 0.3 to 0.4 second window and Pi refuses it. | `event-model.md` section 3.1 defines **in flight**: from the moment the prompt's line is written until its turn's `agent_settled`, its refusal, or a `handled` acknowledgement. The queue holds every other prompt for that whole span, and `turnRunning` begins at the write. The `prompt` row adds "never with `streamingBehavior`". Section 3.2 now says `queued` is unreachable for our dialect, because it never sends `streamingBehavior`. The row stays mapped so a later dialect that steers needs no new design. |
| Finding 2: two of four dialog methods named | **Accepted.** Checked against Pi's `docs/rpc-extension-ui.md`: `select`, `confirm`, `input` and `editor` all block. | Section 3.6 names all four and gives the one cancellation shape Pi accepts for any dialog, `{"type":"extension_ui_response","id":<the request's id>,"cancelled":true}`. |
| Finding 3: the model display name | **Accepted, redacted.** It is the same kind of text as the skill descriptions I already redacted, and the README's "nothing else was changed" made it look published on purpose. | The recorder replaces the model's `name` (its `id` stays). The reviewed recordings were redacted in place with a new `--redact-existing` mode, so no new recording and no spend. The mode works line by line, exactly as recording does. 13 lines changed: the 6 `get_state` responses and the 7 `meta` records. The fixture README and every `meta` record list the new redaction. |
| Finding 4: the start-up warning blamed for a death | **Accepted.** In `exit` the only standard error line is the start-up warning, 3.2 seconds before the kill, so "the last lines" would show the wrong cause. | Sections 1, 2 (the ruling's table) and 3.5 now keep only the standard error lines written since the current turn started (or since the prompt was written, when no turn started). In the `exit` fixture that is none, so the reason is the exit alone. |
| Note: 17 kilobytes | **Accepted.** 17 was the unredacted size from recording 1. | Section 1, section 3.3 and the fixture README say about 10 kilobytes redacted (17 before). Section 1 also notes that `agent_end` repeats every message of the run. |
| Note: exit code -9 is the recorder's | **Accepted.** | Section 3.5 and the fixture README explain that Python reports minus the signal number and .NET reports 128 plus the signal (137). The reason text in 3.5 now uses 137, and phase 1's replay tests and real path must agree on which value they assert. |
| Note: redactor learns the address only from `get_state` | **Accepted.** | The recorder now learns the provider address and model name before any scenario, from a `get_state`-only Pi process started with `--no-session` (no model call, no session file). Every run, and `--redact-existing`, ends with a check that each real value is absent from every fixture file, and fails if one is left. Run on the committed set: 14 files, 5 real values absent. |
| Note: a non-JSON line would hang the recorder | **Accepted.** | The reader keeps such a line in the transcript and fails the scenario at once with the line, instead of timing out 180 seconds later. |
| Note: replay "emits at once" | **Accepted.** | The fixture README says a fake emits each command's records at once, and that the test writes the same commands in the same order. |
| Note: `get_commands` names every fleet skill | **No change, on purpose.** | The names and locations are the evidence that the fleet's skills load in API mode (the `StudioBackend` lesson in the mission document). The fixture README now says this is deliberate. |

**Edit to this review:** finding 3 quoted the model display name verbatim. The Delivery Lead ruled
that the name must not reach main, so the quote is replaced by a description of what it contained.
Nothing else in the Reviewer's text is changed.

**Checks after the changes:** every line of all fourteen wire-log files parses as JSON.
`--redact-existing` run a second time changes nothing. A search for the company name across the
fixtures finds nothing.

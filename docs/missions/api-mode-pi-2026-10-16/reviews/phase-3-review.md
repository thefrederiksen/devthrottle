# Phase 3 review: the live Chat, built once in client-core and mounted by the Cockpit and the phone

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Phase 3 is pull request #4100, branch
`api-mode-phase-3-live-chat`, read at its head commit `365011b53` ("follow the design report's look for the status
line, receipts and tool cards"), which is two commits on top of `e58f498f3`, the merge of phase 2's live stream on
origin/main. Reviewer seat, opened by the Delivery Lead (session 123), running a different agent family from the
Developer; an earlier reviewer of this pull request died when the machine ran out of memory. Written 17 October 2026 on
devlinux. Nothing here was built or fixed by the reviewer; the only file I wrote besides this one was a throwaway test,
run once and deleted.

## Verdict

**The phase does what section 3 asks of it, and it holds against the Gateway's live contract on origin/main, with one
low finding for the seat that built it.** Replies stream in by message id and a final replaces what streamed; a tool
call is one card that goes from running to done, failed or stopped in its first place; every prompt has a receipt that
goes from Queued to Delivered in place, shows a refusal with the agent's reason word for word, and shows a stopped or
failed turn beside Delivered. The view renders the Gateway's words and booleans and composes no verdict of its own,
with one exception: the status line paints the last turn's ending red whatever tone the Gateway gave the row, so a
turn the user stopped reads as a failure there while the receipt and the turn line beneath it, toned by the same
Gateway, are yellow. That is finding 1, proven by a test I wrote, ran and deleted. Everything else named in the brief
holds: one shared component that both shells only mount, terminal mode unchanged, replay and reset and reconnect
handled as the contract says, tests on fakes alone, and proofs that show what their README claims.

## Scope: what I read, what I ran, what I could not reach

**Read in full, on the branch at `365011b53`.** `packages/client-core/src/apimode/`: `liveTypes.ts`, `liveStream.ts`,
`liveConversation.ts`, `useApiModeLiveChat.ts`, `ApiModeLiveChat.tsx`, `apiModeLiveChat.css`, `liveStream.testkit.ts`
and the three test files; the Cockpit mount (`SessionDetail.tsx` and `apiModeChatMount.test.tsx`); the phone mount
(`Chat.tsx` as a whole diff, `ChatApiModeMount.test.tsx`, the `--ok` token added to `styles.css`); the client-core
`package.json` export; and under `proofs/phase-3/`: the README, the harness (`main.tsx`, `capture.mjs`,
`vite.config.ts`, `index.html`), all four accessibility dumps, and two of the four screenshots (`cockpit-midturn.png`,
`phone-failure.png`), viewed as images.

**Read on origin/main (`ba4a44fa4` at the time of reading), never the shared checkout, which was fifty commits
behind.** The Gateway's side of the contract: `SessionLiveEndpoint.cs` whole (the admission, the cursor, the stream
loop, the reset, the stored receipts frame, the keep-alive), `ApiModeLiveDtos.cs`, `ApiModeDisplayDtos.cs`,
`ApiModeLiveRuling.cs` whole, the verdict assembly in `ApiModeSessionFold.cs`, the three API-mode fields on
`SessionDto.cs`, and `ConditionalJson.HostOptions`, which fixes the stream's JSON casing. The client seams the new code
leans on: `gatewayFetch`, `GatewayError`, `authHeaders` and `listSessions` in `client.ts`; `useSessionChat`;
`useSessionManage`; `rosterRetention.ts` and `SessionsView.tsx` (to see the roster row reaches both shells whole); the
generated `schema.ts` for `ApiModeSessionDisplay` and `SessionDto.apiMode`; the container rules for `.session-pane`,
`.session-content`, `.chat-stage` and `.terminal-screen`; and which colour tokens each shell defines. The record: the
mission document (issue #4070), the phase 2 phase review, and the Architect's design report (the mockups).

**Ran, on devlinux, one web suite at a time with at most two workers, as the owner's memory rule after the crash
requires (29 GB free before the first run):**

- `packages/client-core`: `npx vitest run --minWorkers=1 --maxWorkers=2`: 173 files, 2,160 tests passed, 45 seconds.
- `apps/cockpit`: the same command: 117 files, 1,253 tests passed, 88 seconds.
- `apps/mobile`: the same command: 32 files, 148 tests passed, 19 seconds.
- `npm run typecheck` at the root: clean in client-core, cc-assistant, the Cockpit and the phone.
- `vite build` of the Cockpit and of the phone, into my scratchpad: both built, and the live Chat's stylesheet rules
  are in each shell's bundled stylesheet. This is the one check on the branch that drives the REAL component through
  the REAL package export (`@devthrottle/client-core/apimode/ApiModeLiveChat`): both mount tests mock the component,
  and the proof harness imports it by a relative path, so without this build nothing on the branch had resolved that
  export at run time.
- One throwaway test in `packages/client-core/src/apimode/`, run alone and deleted (the branch carries only this
  review): the proof of finding 1.

The three counts match the pull request's own line for line.

**Could not reach.** A real Gateway stream: the proof harness and every test feed the reader a fake stream worded as
the Gateway words things, and the Gateway's own tests (`SessionLiveEndpointTests`, `ApiModeLiveRulingTests`) are what
say the real one sends those frames; I read those tests' assertions on the frame names and cursors and did not run
them. A real Pi, and the Director push (phase 4, not on main), so nothing here rests on events travelling end to end.
Windows. The proof capture (`capture.mjs`) was not re-run; I compared the committed images and dumps to the README's
table and to the Gateway's words instead. `scripts/test-local.ps1` runs no web tests and cannot build on this machine,
as the pull request says, and the change has no .NET code.

## What the brief asked for, and what I found

- **Section 3: replies streaming.** A `replyText` item in `append` mode adds its text to the entry found by
  `reply:<messageId>`, and a new one starts from the delta's text; `replace` makes the entry exactly the final text.
  The entry keeps its place. Rendered as markdown on every delta, and the final's markdown is what stays (the test
  `ReplyFinal_ReplacesTheStreamedTextAndRendersMarkdown`). Holds.
- **Section 3: tool calls as cards from running to done.** One card per `tool:<toolCallId>`, replaced in place with
  the Gateway's status word and tone; the input sits in a collapsed block, unread by the client. Proven by
  `ToolCardRunningThenDone_IsOneCardInItsFirstPlace` and `ToolFinishes_TheSameCardGoesFromRunningToDone`. Holds.
- **Section 3: a receipt on every prompt.** `receipt:<promptId>` goes from "Queued: a turn is running" to "Delivered"
  in the same place (`QueuedPrompt_ShowsTheQueuedWordsThenDeliveredInTheSamePlace`); a refusal shows the Gateway's
  "Refused: ..." sentence word for word in red; a failed turn arrives as the turn line plus the same receipt rewritten
  "Delivered - the turn failed: ..." and the view shows both; a stopped turn the same with "Delivered - stopped". Holds.
  Without `promptText`, which is not on main, a receipt stands alone on the owner's side with no words above it; the
  Delivery Lead accepted that as a follow-up, and the client treats a missing field as absent rather than inventing a
  line.
- **Rule 7, the view decides no meaning.** I read every conditional in the view and the applier for one that
  interprets a state. What is there: `toneClass` maps the four documented colour words to a class and anything else to
  neutral (a colour, no words change); `LiveEntryView` switches on the documented `kind` for layout only, and a kind
  it does not know is rendered as a line with the Gateway's words, not dropped; presence checks on `status`, `detail`
  and `promptText` decide whether to lay out a field, not what it means. The sentences the client composes are about
  its own connection ("Connecting to the live conversation...", "The live conversation stream ended.", "Trying again
  in 3 seconds.") or its own empty view ("Nothing to show yet."), never about the session; a refusal is the Gateway's
  own sentence with the retry words after it. The one place the client assigns a meaning the Gateway did not send is
  the colour of the last turn's ending on the status line: finding 1.
- **Rule 8, one component, both shells only mount it.** The component, its hook, its reader, its applier and its
  stylesheet live once in `packages/client-core/src/apimode/`, exported once. The Cockpit mounts it in its session
  pane for a row that carries `apiMode`; the phone mounts it inside its chat stage, keeping its own app bar, tabs and
  controls. Neither shell has a card, a setting or a word the other lacks. The shared stylesheet uses only tokens both
  shells define; I checked each, and the one the phone lacked (`--ok`) is the one the branch adds. Holds.
- **Terminal mode unchanged.** The Cockpit's mount is one conditional around the existing `ChatTab`. On the phone the
  history hooks run with the same arguments as before while the row has no verdict, and the history markup is the
  same text re-indented (I read the whole diff); moving `useSessionManage` above `useSessionChat` changes nothing
  observable. Both mount tests prove the terminal path still renders the history Chat and reads history for that
  session. Holds.
- **The Gateway's live contract on origin/main.** Traced seam by seam against `SessionLiveEndpoint.cs`:
  - *Attach.* The reader opens with no `after` and no `Last-Event-ID` (asserted), so the Gateway replays everything it
    holds and never resets a client that sent no cursor.
  - *Frames.* `id: <generation>:<sequence>`, `event: items`, one line of JSON. The parser takes a frame split across
    chunks, carriage-return line ends, several data lines, and the keep-alive comment (all asserted). Items are handed
    on whole and in order, and the cursor moves only after they were handed on.
  - *Reset.* The Gateway writes `id: ` (empty) before `event: reset`; the parser keeps the empty id and the reader
    clears its cursor (asserted), the applier empties the view and keeps the Gateway's sentence above the thread. The
    stored receipts frame that follows carries no id, and the reader leaves the cursor alone for a frame without one,
    so until the next live frame a reconnect sends no cursor, which is exactly what the Gateway wants after a cursor
    it could not honour. A `new_generation` reset is handled the same way, and arrival order is kept even when the
    sequence goes backwards (the stored receipts' sequence 0), as the contract demands. A `fell_behind` reset ends the
    response on the Gateway's side; here that is the reset, then "stream ended", then a reconnect with no cursor and
    a full replay.
  - *Duplicates.* On a continued cursor the Gateway sends only events after it, so a delta is never appended twice; a
    re-sent `replace` item lands on its existing entry (asserted). After a reset the view is empty, so nothing can
    double.
  - *Reconnect.* The stream can end without a reset; the reader reconnects after three seconds with the last frame's
    cursor in `Last-Event-ID` (asserted). It uses `fetch` so the bearer key and the refusal body travel; I checked
    `gatewayFetch` applies no time limit unless asked, and none is asked, so a quiet stream is not cut on the client
    side while the Gateway's keep-alive comments keep the proxy from cutting it.
  - *Refusals.* Terminal mode, a Director away, an unknown session: the body's sentence is shown with the retry
    words, and the reader tries again (asserted in the reader's test and the component's).
  - *Casing.* The stream serialises with the host's `Microsoft.AspNetCore.Http.Json.JsonOptions`, whose defaults are
    the web defaults (camel case), the same options behind the roster the client already reads as `apiMode` and
    `activityLabel`. The fakes use the same casing.
- **Tests on fakes only.** The reader is driven by a fake `fetch` that answers a `ReadableStream` the test writes
  into, and a fake scheduler for the retry. The `settle` helper yields ten microtasks and one zero-delay macrotask so
  promise chains run; that is a yield, not a wait on the machine, and the chain it waits for has no real input or
  output. No port, no sleep, no clock.
- **The proofs under `proofs/phase-3/`.** The two screenshots I viewed match their accessibility dumps, and all four
  dumps match the README's table: mid-turn shows turn 1 finished, turn 2 with one card done and one running, the
  reply streaming, a queued prompt and a refused one, under a status line of "Working - turn 2", "API mode", the
  queue and the agent; the failure scene shows "Delivered - stopped" with its turn line, "Delivered - the turn failed:
  ..." with its turn line, a card stopped, the connection line with its exit code, under "Idle after turn 3", the
  failed ending and the agent gone. I checked every status-line sentence against `ApiModeSessionFold` (the activity
  words, the ending words, the queue words, the agent words, and the tone the fold would give each scene) and every
  item against `ApiModeLiveRuling` (the turn lines, the receipt words, the tool status words); they are the Gateway's.
  The harness feeds them through the real reader, parser and applier. The README's list of what the proof does not
  cover is honest.
- **The mockups.** The pull request's list of departures (receipt timing and the fuller queued sentence, the tick and
  hourglass symbols, the tool command and result summaries, the running timer and text cursor, the plan line and the
  model, context and version figures, the composer and Stop) is complete against the design report's live-turn
  drawing; each is a word or a fact the Gateway does not send, and the Delivery Lead accepted them. Not findings.

## Findings

### Finding 1 (low). The status line paints the last turn's ending red whatever tone the Gateway gave it, so a turn the user stopped reads as a failure there while the same Gateway tones it yellow an inch below

**Where.** `packages/client-core/src/apimode/ApiModeLiveChat.tsx`, line 95:
`<span className="live-chat-status-part live-tone-red">{display.lastTurnEnding}</span>`, unconditional; and
`apiModeLiveChat.css` lines 51 to 53, which give that class the attention colour.

**What the Gateway rules.** `ApiModeSessionFold` fills `lastTurnEnding` for both endings, "The last turn failed: ..."
and "The last turn was stopped: ...", and tones the ROW `red` only when that last turn failed (or the agent is gone
while the protocol holds the conversation); a row whose last turn was stopped is `neutral`. For the same stop,
`ApiModeLiveRuling.ReceiptTone` gives the receipt `yellow` ("Delivered - stopped") and `TurnEndedWords` gives the turn
line `yellow`. The verdict carries no tone of its own for the ending; it carries the row's.

**The harm, proven.** A throwaway test rendered the component with the verdict the component's own test uses at lines
74 to 86 (`tone: "neutral"`, `lastTurnEnding: "The last turn was stopped: stopped by the user"`) and asserted the
ending's class did not contain `live-tone-red`. It FAILED: `expected 'live-chat-status-part live-tone-red' not to
contain 'live-tone-red'`. So after the user presses Stop, the status line shows the stop in the colour the Gateway
reserves for a failure, directly above a receipt and a turn line that show the same stop in yellow, on a row the
Gateway toned neutral. The user reads "something went wrong" where the Gateway said nothing did. It is a colour, and
the words are right, which is why it is low; it is a finding because rule 7 says the colours are folded on the Gateway,
and here the client folded one. The committed proofs do not show it: both failure scenes are a FAILED last turn, where
red happens to agree with the fold.

**What would make it right (the Developer decides).** Colour the ending from `display.tone` through `toneClass`, as
the activity pill already is, or give it no tone class at all; either way the client stops choosing. If the Gateway
should tone the ending separately from the row, that is a field on `ApiModeSessionDisplay`, a Gateway change.

**Developer's answer (17 October 2026): accepted.** The reviewer is right: the verdict carries one tone, the row's, and
the view painted the ending red on its own, which is the client deciding a meaning. The ending now takes
`toneClass(display.tone)`, as the activity pill already did, so a stopped turn on a neutral row is neutral and a failed
turn on a red row stays red. The stylesheet's rule for a status part now colours from the tone (green, yellow or red)
instead of hard-coding the attention colour, so the class carries the Gateway's colour and nothing else. No Gateway
change: a separate tone for the ending is not needed while the row's tone already says what the Gateway means.
Two tests in `ApiModeLiveChat.test.tsx`: `ApiModeLiveChat_LastTurnStopped_TheEndingTakesTheGatewaysNeutralToneNotRed`,
run against the old component, FAILED with `expected 'live-chat-status-part live-tone-red' not to contain
'live-tone-red'` and passes on the fix; `ApiModeLiveChat_LastTurnFailed_TheEndingTakesTheGatewaysRedTone` keeps the
failed case red. client-core: 173 files, 2,162 tests passed; typecheck clean in all four packages. The Cockpit and phone
suites were not re-run: both mount tests mock this component, and nothing outside client-core changed.

## Observations (not findings: no harm proven; recorded so the next seat need not rediscover them)

- **A. The history Chat shows for a moment before the verdict arrives.** On the phone, `manage.session` is null until
  the roster read answers, so `apiMode` is null, the history conversation and its filter row render and a `/history`
  read starts, and both are replaced by the live Chat when the row arrives. A Cockpit deep link does the same while
  its roster is loading. One wasted read and a short flash of the other Chat per open; never a wrong picture, because
  the verdict replaces it as soon as it is known.
- **B. A frame that cannot be applied still moves the cursor.** When `applyItems` throws on an item of an unknown mode
  the hook reports it and shows the sentence, the frame's items are not applied (the applier commits nothing on a
  throw), and the reader then takes the frame's id as its cursor, so those items are never fetched again. By design:
  the contract names two modes, a third is a break between client and Gateway, and the report says so on screen.
- **C. One wording difference in the proof's connection line.** The harness feeds the detail "exit code 137"; the
  Gateway writes "Exit code 137" followed by the standard error lines. The README's claim that the words are the
  Gateway's holds for every sentence I checked but this capital letter.
- **D. The retry is a fixed three seconds with no backing off.** While a Director is away, each open Chat asks the
  Gateway every three seconds and shows its refusal each time. One user, a few tabs: not a load worth a finding.
- **E. Phase 2's finding 1 (a retried delivery's later receipt) does not touch this phase.** The client renders receipt
  words as they arrive and pins none of that logic; whichever contract the Delivery Lead chooses, the view shows what
  the Gateway sends.
- **F. Nothing committed drives the real component inside a real shell.** The two mount tests mock the component and
  the proof harness imports it by a relative path. The bundle builds I ran prove the package export and the stylesheet
  import resolve, which is why this is an observation rather than a finding; a test that mounts the real component
  through the export, in either shell, would make that proof a committed one.

### Developer's answers to the observations (17 October 2026)

- **A.** Agreed, and left as it is in this pull request. The fix is for the shell to wait for the roster row before
  choosing a Chat; that touches the history Chat's loading on both shells and is better done with task 2, when the live
  Chat stops needing `/history` at all and the choice can be made once.
- **B.** Agreed, by design. An unknown mode is a break in the contract, and fetching the frame again would fail the same
  way; the sentence on screen is the report.
- **C.** Agreed, not changed. The proof's words otherwise match the Gateway; changing the harness's capital letter would
  make the committed screenshots and dumps disagree with it until the capture is re-run on a machine with a browser.
  Recorded here instead.
- **D.** Agreed, no change. A fixed three seconds against one owner's tabs is not a load; backing off can come when there
  are many readers.
- **E.** Agreed, nothing to do here.
- **F.** Agreed that it would strengthen the proof. Not added in this answer, which is scoped to finding 1; it is a
  small follow-up the Delivery Lead can hand out.


# Factory Inbox, phase 4 - the Factory inbox screens

Proof for the Factory inbox half of phase 4 of the mission "Factory Inbox and Your Inbox" (mission
fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32, `D:\ReposFred\_briefs\factory-inbox-2026-11-04\MISSION.md`, mockups A, B and D).
Built on the branch `factory-inbox/p4-factory-inbox-screens`, cut from origin/main at `457736d7f` (phase 1 merged).
Your inbox (mockup C) is a later task; its tab slot is ready (see `InboxTabs` below).

## What changed

**On the Gateway** (every word and tone is decided there - CLAUDE.md rule 7):

1. **A tone on every message**, `tone`: `ok` (delegated, to do, noted), `amber` (sent, received, with the Boss),
   `red` (declined, cannot be done, pushed back). And `sourceLabel`, "Typed" or "Spoken". Both in
   `FactoryInboxWords` and `FactoryInboxFold`.
2. **A row's words on the list**, `row` on each factory of `GET /gateway/factory/inbox`: the newest message's text or
   "No messages yet", the waiting line ("2 waiting for a decision", "All dealt with"), a short badge (the newest
   message's state) and its tone (`grey` with no messages). The list's existing fields are unchanged.
3. **A spoken message through the durable voice upload**, with a factory inbox as the target
   (`Api/FactoryInboxVoiceEndpoints.cs`):
   - `POST /gateway/factory/{factory}/inbox/voice` registers the recording under its own id (the Idempotency-Key) in
     the dictation's own staging, through the dictation's tenant gate.
   - The chunks go up through the unchanged `PUT /dictation/{uploadId}/chunk/{i}`, and the final answer is retired by
     the unchanged `POST /dictation/{uploadId}/ack`.
   - `POST /gateway/factory/{factory}/inbox/voice/{uploadId}/complete` answers which chunks are missing, then
     transcribes the recording **with no dictionary stage** and hands the speech model's words to the phase 1 store,
     which applies only the account's listed corrections and keeps the raw words beside them. **The client never sends
     words for a recording** (the verbatim rule).
   - A repeated complete leaves nothing twice (single-flight per account and upload, plus the terminal record).
   - A transcription that can pass on a retry answers 503 and keeps the recording; "nothing was heard" is final.
   - The record names `factory-inbox:<factory>` where a dictation names a session: no session locate ever finds it,
     and the held-delivery driver (which drives only records it took over) never touches it.
   - Refused exactly where a typed message is refused, in the same words: the owner's send checks were moved into one
     method, `FactoryInboxEndpoints.RefuseOwnerSend`, which both sends call. Session keys stay refused by the session
     key guard's allow list (tests added for both new routes).
   - Mapped inside the factory area switch's group, like every inbox route.

**In client-core** (`packages/client-core/src/factoryinbox`, built once and mounted on both shells - rule 8):

- `FactoryInboxListView` (mockup A): the rows in the Gateway's order, with the composer on the list itself.
- `FactoryInboxThreadView` (mockup B): each message with its note, how it was put in, its state in its tone, and its
  trail, Sent - Received - Handed to the Boss - the decision, each step with its time; the Summary button shows the
  Gateway's one sentence. Read again every 15 seconds while open, because the decision comes later.
- `FactoryComposer`: pick a factory (on the list), Good / Bad, Mic, Send. Mic opens the same voice box a session uses;
  its Send hands over the audio, saved in this browser first and sent through the durable upload
  (`factoryInboxVoice.ts`), resumed from the saved copy until the Gateway gives a final answer. A refusal is shown in
  the Gateway's words, reported, and the typed words stay in the box; a refused recording stays until discarded.
- `InboxTabs`: ONE Inbox entry with a tab bar. It holds "Factory inbox" now; Your inbox is one more line in
  `INBOX_TABS`.
- `useFactoryInboxOffered`: whether the inbox is offered, from the Gateway's factory area switch.

**On the phone**: `/inbox` and `/inbox/:factory` in the phone's own frame, and an Inbox row in the menu - both only
while the factory area switch is on (off, the address is the ordinary Page not found).

**On the Cockpit**: Inbox in the left rail directly after Factories (behind the switch), `/inbox` and
`/inbox/:factory` with the list and the reading pane (inside `FactoryAreaGate`), and **Leave a message** on every
factory page.

## How the screenshots were made

`capture.py` beside this file, run in the foreground: a throwaway local Gateway on port 7992 with its own data folder,
the factory area switched on, and two owner devices (a phone and a browser) seeded into its device file, so every
message is left from a real owner device as the routes require. It registers six factories, archives one, leaves typed
messages from the phone, and opens the built phone app and Cockpit in headless Chrome with a fake microphone.
`answers.json` holds every Gateway answer quoted here, exactly as sent.

**What a scratch Gateway cannot do, and how it was handled:** the Secretary's and the Boss's steps (received, handed
over, decided) need a Director with real Secretary and Boss sessions. The script writes those rows straight into the
scratch database, as the store writes them. Every word and tone on screen is still the Gateway's own fold of those
rows. Phase 6 proves the steps live on the deployed Gateway.

**Two pictures are NOT Gateway data, and their file names say so** (`*-no-director-NOT-GATEWAY-DATA.png`): the
sentence "Waiting - no Director is running on the Boss's computer..." is written by the wake (phase 2), which is on its
own branch and not on main yet. For those pictures the browser is handed this Gateway's real thread with one message's
state label replaced by the wake's exact sentence, to show the thread prints it as handed.

## The flow - phone

| Picture | What it shows |
| --- | --- |
| `phone-01-factory-inbox-list.png` | Mockup A. Every factory with a Boss, each row with the Gateway's line, waiting line and badge in its tone (red Declined, amber With the Boss, grey No messages). The archived Old Shop is not listed. The composer is on the list: Leave a message in (a factory), Good, Bad, Mic, Send. |
| `phone-02-menu-inbox-entry.png` | The menu offers Inbox while the factory area switch is on. |
| `phone-03-thread-with-trails.png` | Mockup B. Each message with its trail: "Bad Typed Delegated - Sent from the phone - Received by the Secretary - Handed to the Boss - Delegated to session ..."; a To do; a Good message still with the Boss. |
| `phone-04-thread-summary.png` | The Summary button: the Gateway's one sentence, built from the log, no agent. |
| `phone-05-typed-message-left.png` | A typed Bad message sent from the thread: "Your message was left." and it appears as Sent. |
| `phone-06-voice-box-recording.png` | Mic opens the same voice box a session uses, recording (fake microphone). |

## The flow - Cockpit

| Picture | What it shows |
| --- | --- |
| `cockpit-01-inbox-list-and-empty-pane.png` | Mockup D. Inbox in the rail directly after Factories; the list, with the composer under it while no factory is open; the empty reading pane. |
| `cockpit-02-reading-pane-thread.png` | A factory opened: its row marked, its thread with trails in the reading pane, and the composer moved into the pane. |
| `cockpit-03-factory-page-leave-a-message.png` | Leave a message on the factory page: sent, with "Open this factory's inbox". |
| `cockpit-05-declined-and-new-message.png` | Booking's thread: a Declined message in red with the Boss's reason on its trail, and the message just left from the factory page as Sent. |

## The failure cases

| Picture | What it shows |
| --- | --- |
| `phone-07-recording-held-no-transcription.png` | **A spoken message the Gateway cannot transcribe yet** (this scratch Gateway has no transcription key): the Gateway's sentence, "The recording could not be transcribed just now (no key set for the current transcription mode). It is kept, and sending it will retry by itself."; "Your recording is saved on this device and will be sent again." and "1 recording is waiting to be sent." This is the durable path's held state, live. |
| `phone-08-refused-archived-factory.png` | **A refused send, an archived factory**: "'old-shop' is archived, so it takes no messages. Restore it from the Factories screen first." - the Gateway's words; the typed words stay in the box. |
| `cockpit-04-refused-archived-factory.png` | The same refusal from Leave a message on the archived factory's page. |
| `phone-09-no-director-NOT-GATEWAY-DATA.png`, `cockpit-06-no-director-NOT-GATEWAY-DATA.png` | **No Director**: the wake's waiting sentence, printed in place of the state (see above - not Gateway data until the wake merges). |

`answers.json` also keeps two refusals no screen can reach: `send-to-archived-factory` (409, the sentence above) and
`send-with-the-machine-token` (403, "Only the owner's phone or Cockpit leaves a message in a factory inbox.").

## Tests

| Where | What it shows |
| --- | --- |
| `Gateway.UnitTests/Factory/Inbox/FactoryInboxVoiceEndpointTests.cs` (13, real HTTP on a test server, real store and real dictation staging) | **a spoken message is the raw words plus only the listed corrections, raw kept beside it**; the stored row's corrections reproduce its text; the whole recording in chunk order reaches the speech model once; **a repeated complete leaves nothing twice and transcribes once**; a re-register after it was left answers final and does not reopen; missing chunks are named and nothing is transcribed; a transcription failing for now keeps the recording and a retry leaves it; nothing heard is refused in words and is final; a complete for another factory than it was registered for is refused; an archived factory is refused in the typed send's words; a session key is refused; no recording id is refused; the Cockpit is recorded as the Cockpit; **the list hands every row its line, waiting line, badge and tone** |
| `Gateway.UnitTests/Factory/Inbox/FactoryInboxScreensFoldTests.cs` (19) | every state and decision has exactly one tone; the source in words; a row with no messages, one with the Boss, one all dealt with; the waiting line |
| `Gateway.UnitTests/Factory/Inbox/FactoryInboxSessionKeyGuardTests.cs` (2 added) | both spoken-send routes are refused to a session key |
| `client-core/src/factoryinbox/factoryInboxScreens.test.tsx` (13) | against deliberately odd Gateway words: rows, badges and tones verbatim; a row opens its factory; the composer on the list with every factory to pick; pick another and send; **a failed read and a refused send show and report the Gateway's sentence, and the typed words stay**; the thread oldest first with every trail step; the Summary's sentence; Good then Bad keeps only Bad; an empty box cannot send; the tab bar |
| `client-core/src/factoryinbox/factoryInboxVoice.test.ts` (6) | the durable send uploads only the missing chunk with its hash, completes with the note and Send time and **no words**, and acknowledges; already left; held on a transcription failure and on no connection; refused in the Gateway's words; nothing heard is final |
| `apps/mobile/src/pages/Inbox.test.tsx` (4, the real route table) | the list in the phone's frame; a row opens its thread with its title and Back to the list; **switch off looks exactly like an unknown address**; the menu offers Inbox only while the switch is on |
| `apps/cockpit/src/inbox/InboxView.test.tsx` (3) | list with composer and empty pane; a row opens its thread at its own address; an open factory's row is marked and the composer moves into the pane |
| `apps/cockpit/src/AppShell.test.tsx` (2 added, 1 changed) | Inbox directly after Factories with the switch on; not drawn with it off. The test "always shows Factories ... without asking the Gateway's factory switch" now proves Factories is drawn with the switch OFF: the rail reads the switch again, for the Inbox entry alone, which the mission keeps behind it. |

Runs, on this branch:

- `.\scripts\test-local.ps1`: GREEN, every suite outcome=Completed (Gateway.UnitTests 5,517 of the classes this change
  reaches).
- `.\scripts\test-local.ps1 -Gateway` (the coverage gap it named): GREEN, 1,816 tests in four shards.
- client-core 2,437 tests: one red, `DevReportViews.test.tsx` "clips every box around the report", at its 5-second
  timeout under the full run; it passes alone (18 of 18) and the change does not touch it. Not deleted here because it
  is outside this change; flagged for whoever owns it.
- phone 204 of 204 and Cockpit 1,451 of 1,451 (plus the new inbox and rail tests): green; the error display scans of
  both shells green.

## For the merge with the wake (phase 2)

The wake adds a `MessageSent` call to the typed send so the gather window opens. **The spoken send must make the same
call** when the wake merges (`FactoryInboxVoiceEndpoints.CompleteAsync`, after `SendAsync`), or a spoken message would
wait for the next typed one. The wake's waiting sentence reaches the screens through `stateLabel` with no client change.

# Review: pull request 4603, Factory Inbox phase 4, the Factory inbox screens

Reviewed 6 November 2026 by a separate review session (fleet session ead4db08, "Factory Inbox - Reviewer - phase 4
Factory inbox screens"). Branch `factory-inbox/p4-factory-inbox-screens` at `e542afefd`, merge base `9a5345b3b`, which
is origin/main at the time of review, so the branch is current. The worktree was not edited.

## Scope

What was asked, and what was read to answer it:

- The mission document `MISSION.md` sections 4, 5 and 6 (phase 4), and mockups A, B and the Cockpit part of D in
  `report.html` beside it.
- The whole diff of pull request 4603 against origin/main: 52 files. Every new client-core file
  (`packages/client-core/src/factoryinbox/*`), both shells (`apps/mobile`, `apps/cockpit`), the Gateway changes
  (`Api/FactoryInboxVoiceEndpoints.cs`, `Api/FactoryInboxEndpoints.cs`, `Factory/Inbox/FactoryInboxFold.cs`,
  `Factory/Inbox/FactoryInboxWords.cs`, `GatewayHost.cs`), the three Gateway test files, and the QA report
  `docs/qa/factory-inbox/phase-4-factory-inbox-screens` with eleven of its fifteen screenshots and `answers.json`.
- On origin/main, to check the claims the change rests on: `Voice/VoiceUploadStore.cs` (OpenPending, ExpireStalePending),
  `Factory/Inbox/FactoryInboxStore.cs` (SendAsync, the correction stage), `dictation/DictationDialog.tsx` and
  `dictation/backgroundSend.ts` (what the voice box hands over), the dictation sweep in `GatewayHost.cs`.

Runs made by this review, all in the foreground in the worktree:

| Suite | Result |
| --- | --- |
| client-core (`npx vitest run`) | 2438 of 2438 passed, 190 files, 392 seconds. The `DevReportViews` test the author reported red under the full run passed here. |
| phone, `apps/mobile` | 204 of 204 passed, 40 files |
| Cockpit, `apps/cockpit` | 1456 of 1456 passed, 134 files |
| Gateway.UnitTests, the three classes this change adds or extends (`FactoryInboxVoiceEndpointTests`, `FactoryInboxScreensFoldTests`, `FactoryInboxSessionKeyGuardTests`) | 63 of 63 passed |

The full `test-local.ps1` and `-Gateway` runs were not repeated; the author reports both green and nothing in this
review contradicts that.

## Verdict

**Not yet. Two small fixes on this branch, then merge; one follow-up before phase 6.**

The screens are built once in client-core and mounted on both shells, every word and tone on them is the Gateway's,
everything sits behind the factory area switch on both shells and on the Gateway, the typed send is right, and the
screenshots match mockups A, B and D. The spoken path is where the defects are. Findings 1 and 3 are each a one-line
change with a test and should land on this branch. Finding 2 is a small piece of new work and can follow in its own
pull request, but it must land before phase 6 proves the spoken Bad note live, because together with finding 1 it
loses a spoken message while telling the owner it was left.

## Findings

### 1. A recording the Gateway has abandoned is reported as "Your spoken message was left" and deleted from the device

`packages/client-core/src/factoryinbox/factoryInboxVoice.ts` line 160:

```ts
if (registered.terminal === true) return finish(rec, { kind: "left", message: null });
```

The register answer carries `delivered`, and the Gateway's `Terminal()` (`FactoryInboxVoiceEndpoints.cs` lines 249
to 258) sets `delivered: false` with the line "This recording was already dealt with; nothing was left from it." for a
record that is ABANDONED, or DELIVERED with nothing submitted. The client ignores `delivered` on the register leg, so
every terminal answer becomes "left": the saved copy is deleted, the Gateway record is acknowledged and retired, and
the composer prints "Your spoken message was left." The complete leg (lines 203 to 208) reads `delivered` correctly;
only the register leg is wrong.

How it is reached, all on code read on this branch and origin/main:

1. A spoken message is held: the transcription fails for now (503, `transcription_failed`), or the phone has no
   connection. The recording stays Pending in the dictation staging. This is the state screenshot
   `phone-07-recording-held-no-transcription.png` shows, live.
2. The factory inbox records live in the same staging as session dictation: `GatewayHost.cs` wraps `_dictationUploads`
   in a `DictationTenantGate` for the voice routes (line 5779), and the dictation sweep runs
   `ExpireStalePending(StalePendingMaxAge)` on `_dictationUploads.ForTenant(tenant)` (line 7154). `StalePendingMaxAge`
   is 24 hours (line 1551). `ExpireStalePending` abandons any PENDING record whose folder has not been written for
   24 hours, whatever its target names (`VoiceUploadStore.cs` lines 603 to 640; it reads only the state and the age).
3. On the next attempt the client registers the same id. `OpenPending` sees ABANDONED, does not reopen, answers
   `Opened = false` (lines 1145 to 1149), and the register route answers `Terminal()`: `terminal: true, delivered: false`.
4. Line 160 reports "left". The recording is gone from the device and from the Gateway, nothing is in the inbox, and
   the owner was told it was left.

Realistic because of finding 2: the retry runs only while that factory's composer is open, so an overnight hold is the
ordinary case. Neither test covers it: the Gateway's `Register_AfterItWasLeft_AnswersTerminalAndDoesNotReopen` and the
client's `sendFactoryRecording_AlreadyLeft_IsLeftWithoutUploadingAgain` both use a delivered record.

Fix: on the register leg, treat `terminal && delivered` as left and `terminal && !delivered` as "nothing" with the
Gateway's `line`, exactly as the complete leg does. One client test with `{ terminal: true, delivered: false }`.
Whether a held factory recording should be abandoned by the 24-hour sweep at all is a separate question for the
wake, since the Gateway comment on the 503 path promises the recording "stays on the device and on the Gateway until
it does"; the sweep makes that promise false after a day.

### 2. A held recording is retried only while that one factory's composer is on screen; nothing resumes it at app start

`FactoryComposer.tsx` lines 67 to 109: `drive(target)` lists and sends the saved recordings of the composer's own
factory, on mount and every 15 seconds after a hold, and the effect's cleanup clears the retry timer. A grep of both
shells and client-core finds no other caller of `listFactoryRecordings` or `sendFactoryRecording`. The session
dictation path resumes its saved uploads at app start (`resumePendingDictations` in `apps/mobile/src/routes.tsx` and
`apps/cockpit/src/AppShell.tsx`); the factory inbox path has no equivalent.

Consequences, each read from the code:

- On the Inbox list the composer's target is the picked factory, else the first in the Gateway's order (line 56 of
  `FactoryInboxListView.tsx`). A recording held for Newsletter is not sent, and "1 recording is waiting to be sent." is
  not shown, when the owner comes back to the list and Booking is the default pick. It waits until the owner happens
  to pick Newsletter or open its thread.
- The hold's own sentence promises "Your recording is saved on this device and will be sent again." The code keeps
  the first half and not the second once the screen is left.
- With finding 1 and the 24-hour sweep, a recording not revisited within a day is lost with a false "left".

Fix: resume every saved factory recording at app start on both shells, as the session path does, and have the Inbox
list drive every factory's recordings, not only the target's. Then the hold sentence is true. A client test: two
factories, a recording saved for the one not picked, the list mounts, the recording is sent.

### 3. The time the owner pressed Send is accepted and ignored; a spoken message is dated by its transcription

`FactoryInboxVoiceEndpoints.cs`: `CompleteBody.SentAtUtc` (line 274) is deserialised and never read. The message is
logged with `NowUtc: parts.NowUtc()` (line 221), the time of the complete. The client sends `sentAtUtc` from the moment
Send was pressed (`factoryInboxVoice.ts` line 183) for exactly this purpose, and the session dictation route requires
it (`GatewayDictationEndpoint.cs` line 96). For a held recording that goes through the next day, the thread's trail
says "Sent from the phone" at the retry time, the summary's window counts it on the wrong day, and the 15-minute stuck
rule of phase 2 is measured from the wrong moment. The Gateway test `Complete_SpokenMessage_IsTheRawWordsPlusOnlyListedCorrections`
does not look at `sentAtUtc`.

Fix: validate `SentAtUtc` as the dictation route does and log the message with it; keep `parts.NowUtc()` for nothing
but the log line. One Gateway test: a complete with a `sentAtUtc` an hour before `NowUtc` stores that hour.

## What was checked and holds

- **Built once, mounted twice (rule 8 by analogy).** List, thread, composer and tab bar are in
  `packages/client-core/src/factoryinbox`; `apps/mobile/src/pages/Inbox.tsx` and `apps/cockpit/src/inbox/InboxView.tsx`
  add only a frame (app bar and Back link; two columns and a pane title). Both shells' tests mount the shared screens
  through their real route tables. The Cockpit's extra "Leave a message" on the factory page is what mockup D asks for
  and uses the same composer.
- **The client decides nothing (rule 7).** Every state, badge, trail step, decision line, row line, waiting line,
  summary sentence, refusal and tone arrives from the Gateway (`FactoryInboxWords`, `FactoryInboxFold.Row`,
  `FactoryInboxFold.Message`). The client-core tests feed deliberately odd Gateway words and assert they are printed
  verbatim, which is the right way to prove it. The CSS maps only the closed `data-tone` word to a colour token. The
  one thing the screen decides about a time is which clock to read it on (`formatInboxTime`), which is layout.
- **Behind the factory area switch.** Gateway: both voice routes are mapped on `factoryGate`, the
  `FactoryAgentsGate.Group` every inbox route sits in (`GatewayHost.cs` line 5754), and both are refused to a session
  key by the guard's allow list (two cases added, 63 of 63 green). Cockpit: routes inside `FactoryAreaGate`, rail entry
  only while `useFactoryInboxOffered` says on (two rail tests, one changed test now proving Factories is drawn with the
  switch off). Phone: `Gate` shows Page not found with the switch off, and the test proves the markup is identical to an
  unknown address; the menu row is tested on and off.
- **The spoken words (CLAUDE.md verbatim rule).** The client sends audio and never words
  (`sendFactoryRecording_GatewayMissingTheChunk_...` asserts the complete body has no `text` and no `rawText`). The
  Gateway transcribes with `applyCorrection: false` (`GatewayHost.cs` line 5788) and hands the raw words to
  `FactoryInboxStore.SendAsync`, which on origin/main applies only the tenant's listed corrections, replays them over
  the raw text and refuses to store a mismatch, and keeps `RawText` and `CorrectionsJson` beside the result. The
  Gateway test `Complete_SpokenMessage_IsTheRawWordsPlusOnlyListedCorrections` proves "dev throttle" becomes
  "DevThrottle" and nothing else changes, with the raw text stored. Repeated completes transcribe once. One
  observation, not a defect: a recording the owner Paused is transcribed by the voice box's own path (the session
  dictation route, which also applies only listed corrections), placed in the text box for the owner to read, and sent
  as Typed with no raw text on the message row. The code comment says so deliberately. The raw transcript still exists
  in the dictation history, so the rule's proof is not lost, but the message row will say Typed for words that were
  spoken and reviewed.
- **Tests cover the flow and the failure cases**, with the three gaps named above (register with `delivered: false`;
  resume of a held recording for an unpicked factory; `sentAtUtc`). Also untested in the composer: the 15-second
  re-drive after a hold, and Discard on a refused recording. Both are small and could be added with finding 2.
- **Screenshots match the mockups.** Phone list (A): rows with the Gateway's line, waiting line and badge in its tone;
  the composer on the list with the factory picker, Good, Bad, Mic, Send. Phone thread (B): bubbles with note, source
  and state, the trail under each, the Summary button and its one sentence. Cockpit (D): Inbox in the rail directly
  after Factories, the list with the composer under it, the empty pane sentence, the open thread as the reading pane
  with the composer moved into it, and "Leave a message" on the factory page with "Open this factory's inbox" after a
  send. The archived-factory refusal and the held recording are shown with the Gateway's own sentences. The two
  "NOT-GATEWAY-DATA" pictures are labelled as such in their file names and in the README, which is honest. The
  Secretary and Boss rows were written straight into the scratch database, which the README also says; phase 6 proves
  those live.

## Observations outside this change (no harm proven here, for the Delivery Lead)

- **Mockup D shows "Open the Boss" beside Summary in the Cockpit reading pane.** The thread view has an `actions`
  slot for it, and `InboxView` passes nothing. The mission text for D does not name it, so this is a mockup detail,
  not a requirement, but it is the one visible thing in D not on the screen.
- **The trail prints a raw session id**: "Delegated to session a1b2c3d4-0000-4000-8000-000000000003" (phase 1's
  `FactoryInboxFold` line 84). Mockup B shows the session's name. The owner cannot act on an id; the Gateway should hand
  the name, or the session's number, when it folds the trail.
- **The wake is not on main yet** (`origin/factory-inbox/wake` is open; no gather window or `MessageSent` call exists
  on origin/main). The author's note stands: whichever of the two merges second must add the same wake call after
  `SendAsync` in `FactoryInboxVoiceEndpoints.CompleteAsync`, or a spoken message opens no gather window and waits for
  the next typed one. Worth a line on the wake pull request now so it is not forgotten.
- `Completing` in `FactoryInboxVoiceEndpoints` keeps one semaphore per account and upload id for the life of the
  process and never removes them. Tiny per message; it only grows.
- The client's empty-list sentence ("No factory has a Boss yet, so there is no factory inbox to leave a message in.")
  states what `count: 0` means. It is true for the list route on main, which filters archived and Boss-less factories,
  but it is a sentence the client wrote. The Mail Desk hands an `emptyLabel` from the Gateway; the same would fit here.

## Author's answers (6 November 2026)

**1. Accepted, fixed on this branch.** The register leg now reads a final answer exactly as the complete leg does,
through one function (`finishTerminal` in `factoryInboxVoice.ts`): left only when the Gateway says `delivered`,
otherwise "nothing" with the Gateway's own `line`. Test:
`sendFactoryRecording_AbandonedByTheGateway_IsNothingInTheGatewaysWordsNotLeft` (a `terminal: true, delivered: false`
answer with an odd line: outcome nothing, the line verbatim, no chunk and no complete sent). The owner is now told the
truth, but an abandoned recording is still lost: the cause is the 24-hour stale sweep abandoning a held factory
recording, which I take into the finding 2 pull request (exclude `factory-inbox:` targets from the stale sweep, or
give them their own longer limit, decided there with a test), so that the 503 sentence "It is kept" stays true.

**2. Accepted, in its own pull request before phase 6, as asked.** Resume every saved factory recording at app start
on both shells (beside `resumePendingDictations`), have the list drive every factory's recordings rather than only the
picked one, and the sweep question above. Tests: a recording saved for the factory not picked is sent when the list
mounts; the 15-second re-drive after a hold; Discard on a refused recording.

**3. Accepted, fixed on this branch.** The complete now requires `sentAtUtc` in UTC, refused with the dictation
route's own words (`GatewayDictationEndpoint.SentAtUtcRequired`, code `sent_at_required`) before anything is
transcribed, and the message is stored with it; `NowUtc` is kept only for the log line. Tests:
`Complete_SentAnHourBeforeItCompletes_IsDatedByTheSendPress` and `Complete_WithoutASendTime_IsRefusedAndNothingIsTranscribed`.

**Observations outside this change:** none taken here. The wake call after `SendAsync` is in the pull request body
for whichever merges second; the raw session id on the trail, "Open the Boss", the empty-list sentence and the
semaphore growth are for the Delivery Lead to place.

Runs after the fixes: `.\scripts\test-local.ps1` GREEN, every suite outcome=Completed (Gateway.UnitTests 5,656);
client-core factory inbox tests 20 of 20, typecheck and lint clean. The `-Gateway` coverage-gap run was green on the
reviewed commit; this change touches only the voice route and its tests, which Gateway.UnitTests covers.

## Re-review (6 November 2026)

By a separate review session (fleet session e335867a, "Factory Inbox - Reviewer - phase 4 screens re-review"). Branch
`factory-inbox/p4-factory-inbox-screens` at `3d6d381fe`, one commit on top of the reviewed `e542afefd`. The worktree
`D:/ReposFred/devthrottle-fi-p4a` was not edited; the red proofs ran in a detached scratch checkout under the session's
scratchpad, removed afterwards.

### Scope

- The fix commit `3d6d381fe` in full (four files: `factoryInboxVoice.ts` and its test, `FactoryInboxVoiceEndpoints.cs`
  and its test), read against the first review's findings 1 and 3 and against `GatewayDictationEndpoint.cs` (the Send
  time rule the fix copies) and `FactoryInboxStore.SendAsync` (which stores `NowUtc` as `SentAtUtc`, line 132).
- Whether the tests go red without the fix: the scratch checkout at `3d6d381fe` with ONLY the two product files put
  back to `e542afefd` and the two test files kept.
- Whether anything new broke: the three web suites and the three Gateway test classes this change owns, run here.
- Whether deferring finding 2 is safe: what gates the screens and the routes, and what origin/main has gained since the
  merge base `9a5345b3b` (seven commits, up to `6b605a2e5` at the time of this re-review).

Runs made by this re-review, all in the foreground:

| Run | Where | Result |
| --- | --- | --- |
| client-core `npx vitest run`, with the fix | worktree | 2439 of 2439 passed, 190 files, twice (218 s and 785 s under load). Both runs exit 1 on two UNHANDLED ERRORS from `src/terminal/stream.test.ts`; see the observation below. |
| phone, `apps/mobile` | worktree | 204 of 204 passed, 40 files |
| Cockpit, `apps/cockpit` | worktree | 1456 of 1456 passed, 134 files |
| Gateway.UnitTests, `FactoryInboxVoiceEndpointTests` + `FactoryInboxScreensFoldTests` + `FactoryInboxSessionKeyGuardTests` | worktree | 65 of 65 passed |
| RED PROOF finding 1: `factoryInboxVoice.test.ts` with `factoryInboxVoice.ts` at `e542afefd` | scratch | 1 failed of 7: `sendFactoryRecording_AbandonedByTheGateway_IsNothingInTheGatewaysWordsNotLeft`, "expected left to be nothing". The other six pass. |
| RED PROOF finding 3: `FactoryInboxVoiceEndpointTests` with `FactoryInboxVoiceEndpoints.cs` at `e542afefd` | scratch | 2 failed of 15, exactly the two new tests: `Complete_SentAnHourBeforeItCompletes_IsDatedByTheSendPress` (expected 08:00Z, actual 09:00Z) and `Complete_WithoutASendTime_IsRefusedAndNothingIsTranscribed` (expected BadRequest, actual Created). The other thirteen pass. |
| client-core `npx vitest run` on origin/main `6b605a2e5` | scratch | 2418 passed, 1 failed (`DevReportViews` "clips every box", timed out at 5000 ms under load), and the SAME two unhandled errors from `stream.test.ts`. |

### Findings 1 and 3: fixed at the root, and the tests are real

**Finding 1.** The register leg and the complete leg now read a final answer through one function, `finishTerminal`
(`factoryInboxVoice.ts` lines 142 to 147): left only when the Gateway says `delivered: true`, otherwise "nothing" with
the Gateway's own `line`. The Gateway's `Terminal()` (lines 254 to 263) hands `delivered` and `line` on both legs, so
the client reads what the Gateway rules and decides nothing itself (rule 7). The test feeds `terminal: true,
delivered: false` with an odd line and asserts the outcome is "nothing", the line verbatim, and that no chunk and no
complete were sent. It is red on the old code for the right reason.

**Finding 3.** The complete requires `sentAtUtc` in UTC before anything is transcribed (line 126), refused with the
dictation route's own constant `GatewayDictationEndpoint.SentAtUtcRequired` under code `sent_at_required`, and the
message is stored with it (`NowUtc: sentAtUtc`, line 226); `parts.NowUtc()` survives only in the log line. The check
is the same `Kind: DateTimeKind.Utc` pattern the dictation route uses (`GatewayDictationEndpoint.cs` line 400), and
the client sends `toISOString()`, which ends in Z and deserialises as UTC. The two tests are red on the old code for
the right reasons: the stored hour and the refusal.

Both fixes are the smallest change that removes the cause, not a patch over the symptom.

### Deferring finding 2: safe behind the switch

The fix commit touches none of the gates, and the first review's switch checks still describe the branch: both voice
routes are mapped on `factoryGate` (`GatewayHost.cs` line 5782), the Cockpit routes sit inside `FactoryAreaGate` with
the rail entry behind `useFactoryInboxOffered`, and the phone's `Gate` answers Page not found with the switch off.
Origin/main has gained nothing under `apps/` or `packages/` since the merge base except a contract document, so
nothing on main has moved a screen out from behind the switch. With finding 1 fixed, the worst case finding 2 leaves
is an honest "nothing" for a recording held more than a day and never revisited, visible only to the owner with the
switch on. Deferring it to its own pull request before phase 6 is safe.

### New since the first review: this branch is now the one that merges second, and it cannot merge as it stands

1. **The wake merged first.** Phase 2's wake landed on main as `0a9123023` (pull request 4601) after this branch was
   cut. The first review's note was conditional; it is now a requirement of THIS pull request: `CompleteAsync` must
   tell the wake after `SendAsync`, exactly as the typed route does on main
   (`parts.MessageSent(tenant, message.Factory, message.SentAtUtc)`, `FactoryInboxEndpoints.cs` line 128). The voice
   `Parts` record has no such member yet, so it needs one, wired in `GatewayHost.cs` beside the typed route's, plus
   one Gateway test with a recording delegate proving a spoken complete reports the factory and the Send time (with
   finding 3 fixed, that time is the press, which is what the gather window should measure from). Without it a spoken
   message opens no gather window and waits for the next typed one.
2. **A trial merge onto origin/main conflicts** in `FactoryInboxWords.cs` (adjacent additions) and `GatewayHost.cs`
   (the typed route's `Parts` gained `Wakes`, `MessageSent`, `BundleHanded` where this branch added the voice mapping).
   Both are adjacent-line conflicts, resolved by hand in minutes. After the rebase, the voice test fixture's
   `new FactoryInboxEndpoints.Parts(...)` (seven arguments, line 81 of the test) must take main's ten to compile.
3. **One line to keep the two routes alike:** main's typed route answers its 201 with the factory's waiting label
   (`FactoryInboxFold.Message(message, null, null, WaitingOf(...))`); the voice route's 201 still passes two arguments.
   It compiles, since the new parameters are optional, but a spoken message's answer would carry no waiting sentence
   while the typed one does. Pass the same.

### Observations outside this change

- **client-core `npm test` is red on origin/main today, and on this branch for the same reason.** Two unhandled
  `ReferenceError: WebSocket is not defined` from a reconnect timer in `src/terminal/stream.ts` line 389 (scheduled at
  line 431) that fires after `stream.test.ts` has deleted its fake WebSocket in `afterEach`. The file passes alone
  (10 of 10); the leak shows only in the full run, on main and on this branch alike, and vitest exits 1 on it. This
  branch does not touch `src/terminal`. No issue names it; one should, for the Delivery Lead.
- The `DevReportViews` "clips every box" test timed out once at 5000 ms on main's run under heavy load and passed in
  both branch runs. Not this branch's.

### Verdict

**Findings 1 and 3: fixed at the root, with tests that go red without the fix. Deferring finding 2: safe behind the
switch. Merge: not yet.** Rebase onto origin/main (two small conflicts, the test fixture's `Parts`), add the wake call
after `SendAsync` with its test, and pass the waiting label on the voice route's 201. That one commit then needs only a
narrow look at itself; nothing else in this pull request needs re-reading.

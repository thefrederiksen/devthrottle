# Review: pull request 4637, Factory Inbox step C, the Your inbox screens and the unread count

Reviewed 6 November 2026 by a separate review session (fleet session 1a6cdc87, "Factory Inbox - Reviewer - Your inbox
screens (4637)"). Commit `566d9552d`, whose parent `bb1b5f3f1` was origin/main when the branch was cut; origin/main
has since moved one commit (`5ccc0592d`, pull request 4638), which touches none of the fifty files this commit touches.
The worktree was not edited; two temporary checks described below were made and undone, and the tree is clean.

## Scope

What was asked, and what was read to answer it:

- `MISSION-FINISH.md` section 5 ("One Inbox entry, two tabs" and "Your inbox screens") and section 7; `MISSION.md`
  section 5 (Your inbox, the screens) and mockup C in `report.html`.
- The whole of commit `566d9552d`: the four Gateway files (`Inbox/YourInboxWords.cs`, `Inbox/YourInboxFold.cs`,
  `Inbox/YourInboxStore.cs`, `Api/YourInboxEndpoints.cs`) and their tests; every new client-core file under
  `packages/client-core/src/yourinbox/` and the `InboxTabs` change; both shells (`apps/cockpit/src/AppShell.tsx`,
  `apps/cockpit/src/inbox/InboxView.tsx`, `apps/mobile/src/pages/Inbox.tsx`, `apps/mobile/src/components/NavDrawer.tsx`)
  and their tests; the `createSession` change in `api/client.ts`; and the QA folder
  `docs/qa/factory-inbox/your-inbox-screens/`, every one of its twenty-one screenshots looked at, with `README.md`,
  `answers.json` and the head of `capture.py`.
- On the same tree, to check what the change rests on: the reply route and its 409 answer in `YourInboxEndpoints.cs`
  (phase 3), `NewSessionRequest.PrePrompt` and the session create route, `DictationDialog`'s `onSend`, the Factory inbox
  composer, the Cockpit and phone route tables (so no link the screens render is a dead address), and how the Cockpit
  rail draws a badge.

Runs made by this review, all in the foreground in this worktree:

| Suite | Result |
| --- | --- |
| client-core, `src/yourinbox` and `factoryInboxScreens.test.tsx` | 37 of 37 passed, 2 files |
| Cockpit, `AppShell.test.tsx` and `inbox/InboxView.test.tsx` | 59 of 59 passed, 2 files |
| phone, `pages/Inbox.test.tsx` | 9 of 9 passed |
| Gateway.UnitTests, every class whose name holds `YourInbox` | 86 of 86 passed |

Two checks beyond the suites, each undone afterwards so the tree is as committed:

- The re-read fix in `useYourInboxCount.ts` was removed (the `again` flag), and the client-core file run again:
  `refreshYourInboxCount_AskedWhileAReadIsOnItsWay_ReadsAgainAfterIt` failed exactly as the report says it would
  ("expected '3|three fresh ones (gateway)' to be '1|one left (gateway)'"), and the other 23 passed. The test bites.
- A temporary phone test (deleted after the run) drove the real route table through list, first message, Back,
  second message with its read held. Its result is finding 1.

## What was checked and found sound

**Built once, framed twice (rule 8 by analogy).** The list, the message, the reply box, the filters, the count hook and
the tab bar are all in `packages/client-core/src/yourinbox/` and `factoryinbox/InboxTabs.tsx`. The Cockpit supplies
the pane, the title, the two columns, the addresses (`/inbox?tab=yours`, `&message=<id>`) and its own hrefs for a
session, a report and a factory thread. The phone supplies the screen, the app bar, the Back link and its own hrefs.
Neither shell holds an inbox word, a filter, a button or a count of its own. A link whose kind a shell has no address
for is not drawn (`hrefFor` returning null), and both href functions are tested per kind. The three link kinds resolve
to routes that exist on each shell (`/sessions/:sessionId` and `/report/:reportId` in the Cockpit; `/session/:sessionId`
and `/report/:reportId` on the phone; `/inbox/:factory` on both).

**Every word from the Gateway, printed as sent (rule 7).** The Gateway now answers the list with `filters` (view,
label, count, in the order of `Views.All`), `empty` (the sentence for that view) and `tone` on each message, beside the
sender label, state label, links, actions and reply-to label phase 3 already finished. The list prints `f.label`,
`f.count`, `shown.empty`, `m.from.label`, `m.subject`, `m.body`, `m.stateLabel` with `data-tone={m.tone}`; the message
view prints the body, sender, links, actions and reply-to label; the reply box prints the reply's `label` for where it
went, the 409 `error` sentence and each offer's `label`. No client branch decides what a state means: `unread` (a
Gateway boolean) only picks bold; `tone` only picks a colour class; an offer's `kind` only picks which call is made.
The client-core tests use a fixture whose every word is one no screen could write ("Fresh as dew (gateway)", "Only
fresh (gateway)", "Not a squeak (gateway)."), so any screen that invents a word fails them. The words the client does
own are control words and frame words only: "Reply", "Mic", "Send", "Sending...", "Write your reply...", "Loading...",
the Cockpit's empty reading pane sentence, and the two sentences after the client itself started a session ("Open the
new session", "The new session was started."). None of them describes a Gateway state.

**Reply routing shows exactly the Gateway's choices.** A reply posts `{text}`; the 409 with `offers` is returned as a
choice, not thrown, and the box prints the sentence and one button per offer, in the Gateway's order, and nothing else
(`Reply_ToAnEndedSession_ShowsTheGatewaysSentenceAndExactlyItsTwoChoices`). Taking the factory offer repeats the same
call with `to: "factory"`, which is the only other target the route accepts. Taking the new-session offer calls
`createSession` on the Director and folder the offer names, with the Gateway's `prompt` as `prePrompt`, which lands in
`NewSessionRequest.PrePrompt` through the unchanged create route. The screenshots `phone-06a` and `cockpit-06` show the
Gateway's own sentence ("302 Booking - Developer has ended, so it cannot read a reply. Start a new session with the
reply, or leave it in the Factory inbox of Booking, instead.") and the two buttons, matching `answers.json`
`ended-session-reply-offers` word for word; `phone-06b` shows the factory choice taken and the Gateway's "Left in the
Factory inbox of Booking".

**The unread count equals the Gateway's on both shells.** One module-level read of `GET /gateway/inbox/count` is shared
by every caller; the rail, the phone menu and the tab read the same answer. The Cockpit rail prints the Gateway's
number with the Gateway's words as hover text (test: 37 and "thirty-seven fresh (gateway)"); the phone menu and the tab
print the Gateway's words (test: "three fresh ones (gateway)"); an empty label draws nothing. The count is read again
after a mark, an archive or a reply (`yourInboxChanged`), and a refresh asked for mid-read is no longer dropped (proven
above). The screenshots show 5 on the phone menu with "5 unread" on the tab, and 4 on the Cockpit rail with "4 unread"
on the tab, each matching the `count` answer recorded for that pass.

**The tests section 7 asks for exist.** "List, read, archive and reply each print only Gateway words": the client-core
file, with the odd-word fixture. "The unread count on the Inbox entry equals the Gateway's count on both shells":
`AppShell.test.tsx` and `pages/Inbox.test.tsx`. The Gateway tests cover the filters' words, counts and order, the Unread
view, each view's empty sentence and each message's tone; each asserts a property that did not exist before this
commit, so none can pass without it.

**The screenshots show what the report says.** All twenty-one were looked at against the README table. The phone list
shows Unread 5 / All 6 / Archived 1 with bold unread rows and red Unread badges; the read screen shows the Boss's
decline marked Read with "Open the session", "Open Booking", Reply, Mark unread, Archive; the archived message offers
"Move to inbox" and "Mark unread"; the refused reply shows the Gateway's "'old-shop' is archived, so it takes no
messages. Restore it from the Factories screen first. Nothing was sent."; the empty view shows "Nothing for you.
Factories and sessions write here instead of emailing you." with Archived 7. The Cockpit set shows the same states in
the list-and-pane frame. The README's "What this does NOT prove" is honest: no live Director, so the new-session offer
was not taken; no transcription key, so no spoken reply.

**The Gateway side.** `Views.All` gained `unread`; the only other consumer of the view list is the store's own check,
and the route's "bad view" sentence lists the three. `ViewCounts` is one grouped query. `EmptyLine` and `Tone` throw on
an unknown value rather than guessing.

## Findings

### 1. On the phone, the app bar names the previous message while the next one loads (minor)

`apps/mobile/src/pages/Inbox.tsx` keeps `subject` in component state, and the component stays mounted across
`/inbox?tab=yours`, `/inbox?tab=yours&message=A` and `...&message=B`, because they are one route. `onSubject` sets it
when a read completes, and nothing clears it when `message` changes. So: open message A, Back to the list, tap message
B; until B's read answers, the app bar reads A's subject above B's loading body. If B's read fails, `onSubject` never
fires and the wrong subject stays above the error for as long as the screen is open.

Proven with a temporary test on the real route table (deleted after the run): after opening the first message, Back,
and tapping the second with its read held, the app bar `h1` was `"First subject (gateway)"` at address
`?tab=yours&message=y-2`. Expected "Inbox" (or nothing) until the Gateway has answered.

Harm: the screen names the wrong message. Small in the common case (one round trip), lasting when the read fails. The
Cockpit is not affected (it prints the subject inside the message view). This is the shell's frame, not the shared
screen, and does not touch any mission requirement; it should be fixed, and need not block the merge.

## Observations, not findings

- The reply box is a new component, not the `FactoryComposer` itself. Typed replies take the same shape (text box,
  Mic, Send). Spoken replies differ: the Factory inbox hands the AUDIO to the Gateway through the durable upload, while
  the reply box puts the voice box's transcribed words into the text box to be read and sent as typed. That fits the
  reply route, which takes `{text}` only, and the README says so plainly. Whether a spoken reply should also go as
  audio is a design question for the Delivery Lead, not a defect of this step.
- Taking "Start a new session" is done by the client against the Director, as the phase 3 offer intends; the Gateway
  keeps no record of that reply on the message, so the replies list does not show it and the list is not refreshed.
  Step F should look at this when it takes the offer live.
- `YourInboxStore.ViewCounts` writes no log line, unlike `List` beside it (CLAUDE.md rule 2). A small read, but the
  rule says every public method.

## Verdict

**APPROVE.** The requirements of section 5 and section 7 are met and proven: one Inbox entry with two tabs on both
surfaces, the screens built once in client-core and only framed by the shells, every word, filter, count, tone and
badge from the Gateway and printed as sent, the ended-session reply showing exactly the Gateway's choices, the unread
count the Gateway's on both shells, the required tests present and biting, and the screenshots showing what the report
says. Finding 1 is a minor phone-frame defect to fix in a follow-up.

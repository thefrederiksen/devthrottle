# Factory Inbox, step C - the Your inbox screens and the unread count

Proof for step C of `docs/factory-inbox/MISSION-FINISH.md` (mission fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32): mockup C and
its Cockpit form, and the unread count on the Inbox entry. Built on `factory-inbox/your-inbox-screens`, on top of the
Your inbox back end (pull request 4608) and the Factory inbox screens (pull request 4603).

## What changed

**On the Gateway** (every word, count and tone is decided there - CLAUDE.md rule 7), in `Inbox/YourInboxWords.cs`,
`Inbox/YourInboxFold.cs`, `Inbox/YourInboxStore.cs` and `Api/YourInboxEndpoints.cs`:

- An **Unread** view beside the inbox and the archive: `GET /gateway/inbox?view=unread`.
- **`filters`** on every list answer: each view's button words and count, in the order shown - Unread, All, Archived.
- **`empty`** on every list answer: what that view says when it holds nothing. The inbox's is the owner's sentence:
  "Nothing for you. Factories and sessions write here instead of emailing you."
- **`tone`** on every message: `red` for unread, `grey` for read and archived, the Factory inbox's tone words.

**In client-core** (`packages/client-core/src/yourinbox`, built once, mounted on both shells - rule 8):

- `YourInboxListView`: the filters, then the messages newest first - who sent it, the time, the subject, one line of the
  body, the state badge in its tone; unread in bold. An empty view prints the Gateway's sentence.
- `YourInboxMessageView`: the full body, who sent it, the Gateway's links back (the session - "Open the session" - the
  report, the factory's thread), the Gateway's buttons (Archive, Mark unread, Move to inbox) and Reply. Opening an unread
  message marks it Read.
- `YourInboxReplyComposer`: the same box as the Factory inbox, typed or spoken (the voice box's words come back into
  the box to be read before Send). It prints where the reply goes before it is written and where it went after, both in
  the Gateway's words. When the session has ended it shows the Gateway's sentence and exactly the offers the Gateway
  made - "Start a new session ..." and "Leave this reply in the Factory inbox of ..." - and nothing else.
- `useYourInboxCount`: one read of `GET /gateway/inbox/count` shared by the whole page, read again every 30 seconds and
  at once after a mark, an archive or a reply. A read asked for while one is on its way is no longer dropped (the test
  `refreshYourInboxCount_AskedWhileAReadIsOnItsWay_ReadsAgainAfterIt` fails without that fix).
- `InboxTabs`: the second tab, **Your inbox**, with the Gateway's count words beside it ("4 unread").

**On the shells**, only their frames and addresses: the Cockpit shows list and reading pane at `/inbox?tab=yours` and
`/inbox?tab=yours&message=<id>`, and the rail's Inbox entry carries the Gateway's unread number with its words as the
hover text. The phone shows the list, tap to read (subject in the app bar), Back to the list, at the same addresses, and
the menu's Inbox entry carries the Gateway's count words. A query, not a path, so no factory's name can collide with
the tab.

## How the pictures were made

`capture.py` beside this file, run in the foreground: a throwaway local Gateway built from this branch on port 7993 with
its own data folder and `CC_GATEWAY_NO_TAILSCALE=1`, the factory area on, two owner devices (a phone and a browser).
It registers Booking and Old Shop, archives Old Shop, leaves one typed message in Booking from the phone through the real
route, then drives its own headless Chrome (a scratch profile and its own debugging port, never the owner's browser)
with browser-harness, at phone width (390) and Cockpit width (1366). `answers.json` holds the Gateway answers quoted
here, exactly as sent.

**What a scratch Gateway cannot do, and how it was handled.** A message reaches Your inbox from a session's own key
(`cc-devthrottle inbox send`) or from a Boss's decision made with its session key, and only a Director mints session
keys. So the seven messages, and the history rows of the five sessions that sent them, were written straight into the
scratch database as the store and the session recorder write them. Every word on screen is still the Gateway's fold of
those rows, and **every owner action - list, filter, read, mark, archive, reply, the offers after a session ended, and
each refusal - went through the Gateway's real routes from the browser.** Between the phone pass and the Cockpit pass
four messages were marked unread again through the real route (the owner's own "Mark unread"), because the phone pass
had read them all.

## The pictures

| State | Phone (390 wide) | Cockpit (1366 wide) |
| --- | --- | --- |
| The count on the Inbox entry | `phone-01-menu-inbox-unread-count.png` - "Inbox 5 unread" in the menu | `cockpit-01-list-unread-and-rail-count.png` - 4 on the rail, "4 unread" on the tab |
| List, unread | `phone-02-your-inbox-list-unread.png` - filters Unread 5 / All 6 / Archived 1; unread rows bold with the red Unread badge | `cockpit-01-...` (as above), list on the left, empty reading pane |
| Unread filter | - | `cockpit-02-unread-filter.png` |
| Read | `phone-03-read-a-factory-message.png` - the Boss's decline, opened and marked Read; Open the session, Open Booking, Reply, Mark unread, Archive | `cockpit-03-read-in-the-reading-pane.png` |
| Reply to a factory | `phone-04a-reply-to-a-factory-written.png`, `phone-04b-reply-to-a-factory-sent.png` - "Left in the Factory inbox of Booking" | `cockpit-04-reply-to-a-factory.png` |
| Reply to a running session | `phone-05-reply-to-a-running-session.png` - "Sent to 412 Release - Developer, which reads it when it is free" | `cockpit-05-reply-to-a-running-session.png` |
| Reply to an ended session | `phone-06a-reply-to-an-ended-session-two-choices.png` - the Gateway's sentence and its two choices; `phone-06b-ended-session-reply-left-in-its-factory.png` - the factory choice taken | `cockpit-06-reply-to-an-ended-session-two-choices.png` |
| Archive | `phone-07a-archived.png`, `phone-07b-archived-view.png` | `cockpit-07-archived.png` |
| Refused | `phone-08-refused-archived-factory.png` - "'old-shop' is archived, so it takes no messages. Restore it from the Factories screen first. Nothing was sent." | `cockpit-08-refused-archived-factory.png` |
| Read (list after reading) | `phone-09-list-after-reading.png` - no bold rows, count gone from the tab | - |
| Empty | `phone-10-empty.png` - "Nothing for you. Factories and sessions write here instead of emailing you." | `cockpit-09-empty.png` |

The replies left in Booking's Factory inbox are in `answers.json` under `booking-factory-inbox-after-the-replies`: each
is a typed message from the owner's device in that factory's thread, exactly as if he had left it there himself.

## What this does NOT prove

- **Taking "Start a new session".** The offer is shown in the Gateway's words, but taking it starts a session on the
  Director the ended session ran on, and the scratch Gateway has no Director. The client test
  `Reply_EndedSession_TakingTheNewSessionChoice_StartsItWhereTheGatewaySaidWithItsPrompt` proves the client starts it
  on that Director, in that folder, with the Gateway's prompt; the live start belongs to the end-to-end proof (step F).
- **A spoken reply.** The Mic opens the same voice box as everywhere else, whose words come back into the reply box;
  the scratch Gateway has no transcription key, so no spoken reply was made here.
- **Messages written by a real session or a real Boss.** Seeded as described above; step F proves them live.
- The refusal names the factory by its id ('old-shop'), not its title: that is the Gateway's sentence from phase 3,
  printed as it is. Changing it is a Gateway words change, not part of this step.

## Tests

- Gateway (`src/CcDirector.Gateway.UnitTests/Inbox/YourInboxEndpointTests.cs`): the filters' words, counts and order;
  the Unread view; each view's empty sentence; each message's tone.
- client-core (`packages/client-core/src/yourinbox/yourInboxScreens.test.tsx`), against Gateway payloads whose every
  word is one no client could write: list, filters, empty, read, mark read on open, archive, every reply outcome
  (factory, running session, ended session's two choices and taking each), refusals, the shared count and the tab.
- Cockpit (`AppShell.test.tsx`, `inbox/InboxView.test.tsx`) and phone (`pages/Inbox.test.tsx`): the unread count on
  the Inbox entry equals the Gateway's count on both shells; the tab, list, reading pane and addresses.

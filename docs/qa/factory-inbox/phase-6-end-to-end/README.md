# Factory Inbox - the end-to-end proof on the live Gateway (step F)

Mission fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32, `docs/factory-inbox/MISSION-FINISH.md` section 7, step F. Run on
6 November 2026 on devlinux by the QA Developer of the mission, against the DEPLOYED hosted Gateway
`https://gateway.devthrottle.com` (live commit `4264a9e`, version 2.36.1, read from `/healthz`).

All times below are Coordinated Universal Time. The screens print local time on devlinux, which is five hours behind
(a thread line "16:13" is 21:13 here).

**This report covers steps 1 to 5, 7 and 8. Step 6 (the Summary button and the morning message) and the "no email is
sent" checks wait for the email-off change (pull request 4664) to be deployed; they will be added to this file.**

## Result in one paragraph

The Factory inbox works end to end on the live service: a typed message from the Cockpit and a spoken Bad note from the
phone each went Sent, Received by one Secretary, Handed to one Boss, and Delegated to a work session the Boss started,
in under two minutes, with every seat on Opus and every seat closing itself. A second message reached the Boss that
was already working, and no second Boss started. A request the Boss should refuse was Declined in its own words, wrote
exactly one message to Your inbox, raised the unread count on both shells, and the reply from Your inbox landed back in
the factory inbox and was delegated. A session's `cc-devthrottle inbox send` reached Your inbox on both shells and
"Open" opened that session. Every failure case behaved as designed: a stopped Director left the message waiting with
the reason, raised the stuck alert at exactly 15 minutes, and the message went through within seconds of the
Director reconnecting; a Secretary stopped mid-claim was replaced within seconds and its claimed messages were handed
on; a silent recording left nothing and said so; an archived factory refused a message in words.
**One real defect stops the thread screen from working on the live service: reading one factory's thread takes 70 to
90 seconds, so the screen never shows it (defect 1 below).**

## Defects found (each reported to the Delivery Lead as it was found)

1. **The thread screen cannot show a thread on the live service (serious).** `GET /gateway/factory/{factory}/inbox`
   takes about 76 seconds for EVERY factory, even one with no messages (mail-desk 75.8 seconds, factory-inbox-test
   77.5 seconds at 21:20; 70 and 71 seconds at 21:22 and 21:23; 90 seconds for both at 22:38 and 22:46). The same
   Gateway answers `/healthz` in 0.2 seconds, the list of factories in 1.6 seconds and a factory's summary in 0.3
   seconds. The thread view re-reads every 15 seconds and aborts the read in flight
   (`packages/client-core/src/factoryinbox/FactoryInboxThreadView.tsx`, lines 55 to 74), so the thread stays on
   "Loading..." for ever on the phone and in the Cockpit, and `cc-devthrottle factory inbox thread` dies on its own
   30-second limit. From 22:37 the browser stopped getting an answer at all: every thread read failed at the network
   level after 5 to 8 seconds ("Failed to fetch"; the screen says "Can't reach the Gateway - retrying.",
   `07e-cockpit-thread-cannot-reach-gateway.png`), while curl from devlinux still got it in 90 seconds.
   **Likely cause, not proven** (the Gateway's disk cannot be seen from here): since pull request 4630 the thread
   calls `VoiceUploadStore.PendingFor` (`src/CcDirector.Gateway/Voice/VoiceUploadStore.cs`, line 683), which walks
   every dictation upload folder of the account, taking a lock and reading each record file, on every thread read,
   on the hosted file share. Aborted reads keep running on the Gateway, so screens that re-read every 15 seconds may
   pile them up; my own two test tabs did this for an hour.
   *How the thread screenshots in this report were taken:* only in my two test tabs, a page script lengthened the
   one 15-second re-read timer to 180 seconds (`setTimeout` with exactly 15000 milliseconds became 180000), so the
   Gateway's real answer could arrive and render. Nothing else on the page was changed. The screens without that
   script are the "Loading..." and "Can't reach the Gateway" shots.
2. **Two Directors on one computer: the factory's seats can land on the wrong one (design gap, found before the
   run).** The wake picks the Director for a factory's computer by machine name, lowest Director id first
   (`GatewayHost.cs`, `directorOn`; `FactoryInboxWake.cs` pins the start to it), and the general session resolver
   takes the first registry row on a machine with no order (`Running/IDirectorTargetResolver.cs`, `PickReachable`).
   On devlinux the owner's `devlinux` Director sorts first, so a test factory with computer `devlinux` would have
   started its Secretary and Boss on the OWNER's Director, and a stopped test Director could never show "waiting".
   Worse, while a second Director named `devlinux` is online, the owner's four devlinux factories (cpmc,
   devthrottle-marketing, erik-laursen, mail-desk) could have seats started on the test Director. A factory names a
   computer, not a Director, so nothing in a definition can say which. See "The test Director" for what was done.
3. **The trail says "Sent from the Cockpit" for a message spoken on the phone screen - not a defect of the product,
   a limit of this run.** The Gateway names the surface from the signed-in device's type (`browser` is the Cockpit,
   `phone` is the phone; `SessionOrigin.FromDeviceType`). My phone screen was the mobile web app at phone size inside
   the desktop browser profile, which is signed in as a browser device, so the Gateway was right to say Cockpit.
4. **Small: a delegated Writer's name repeats its prefix**, for example "Factory Inbox test - Writer - Factory Inbox
   test - Writer - better folder name ideas note" (session list, step 2). The Boss passed a whole name and the seat
   start added the prefix again.
5. **Small: on the phone, a long subject in Your inbox pushes the bell off the right edge of the app bar**
   (`04f-phone-your-inbox-message.png`).
6. **Small: while a message waits for a Director, the list row says only "1 waiting for a decision" with the badge
   "Sent".** The reason ("Waiting - no Director is running on the Boss's computer, fi-inbox-test; it goes on when one
   connects there") is in the Gateway's answer (the message's state label, and `cc-devthrottle factory inbox list`
   prints it) but on screen only in the thread, which defect 1 keeps from loading.
7. **Small, outside the inbox: `cc-devthrottle session keys` is refused to a session key even for a session the
   caller owns** ("a session key may not call POST /sessions/{id}/keys"), while its help offers it for a session you
   own. Found when the step 5 seat stopped at the agent's "trust this folder" question.
8. **Wording: the archived-factory refusal names the id, not the title**: "'factory-inbox-test' is archived, so it
   takes no messages. Restore it from the Factories screen first."

## The test set-up

### The test Director

Built from main (`scripts/local-build-linux.sh --slot 5`, version 2.36.1) and run from the isolated root the Delivery
Lead prepared (`/home/soren/ReposFred/fi-test-director`, display name "Factory Inbox test", connected to the hosted
Gateway, default model opus).

It was first launched exactly as briefed, on devlinux. Because of defect 2 it was stopped with its own stop request
(21:08) and relaunched with **its own computer name, `fi-inbox-test`**: same binary, same root and home, same clean
environment, inside a Docker container whose only job is to give it its own host name. The container uses the host's
network and process list and changes its root into the host's own file system, and the Director runs as the user
`soren` (`fixture/launch-in-container.sh`). Unprivileged name spaces are blocked on devlinux, so this was the way to
give one process its own host name. `cc-devthrottle director list` then showed "Factory Inbox test" on computer
`fi-inbox-test`, and no other Director runs there. It was stopped only with its own stop request (written into
`root/config/lifecycle-signals/`); after every run `~/.local/bin/cc-devthrottle` still pointed at the installed tool.

### The test factory

`factory-inbox-test`, title "Factory Inbox test", registered with `cc-devthrottle factory register --manifest`
(`fixture/manifest.json`), then given a definition with `cc-devthrottle factory import` and published as version 1
with `cc-devthrottle factory definition publish --version 1 --replaces 0`. The definition: factory defaults machine
`fi-inbox-test`, the Director's coding agent, model Opus 5.5; two seats: **Boss** (role Boss: delegates notes to the Writer with `factory seat start`, declines
email, spending, deleting, other repositories and production; never sends email; closes itself when done) and
**Writer** (role Writer: writes one short note into the factory's notes folder, hands back, closes). The factory's
folder is `/home/soren/ReposFred/fi-test-factory`. Every Secretary, Boss and Writer the run started ran on Opus 5.5
(the Director's launch log and every transcript say so).

### Driving the screens

The Director's signed-in browser profile (CenterConsulting, the owner's account) with browser-harness, two tabs of my
own: the **Cockpit** at `/inbox` (1854 by 961) and the **phone** at `/mobile/inbox` with the device metrics set to
390 by 844 at scale 2, a mobile device. Main's `cc-devthrottle` was run from the worktree, with the installed tools'
Python packages, because the installed tool on devlinux has no `inbox send`.

**Spoken messages.** The phone's own recorder, upload and Gateway transcription were used. Only the microphone was
replaced: in the phone tab, `navigator.mediaDevices.getUserMedia` returned a live audio stream that played a recorded
file. The speech was made on devlinux by a stock text-to-speech voice (Kokoro, voice `bm_george`, not the owner's
voice), 13 seconds: "This is a bad note for the test factory. The note with folder name ideas was too short and the
names were dull. Please have a better note written, with five names and one line on why each one fits." The silent
recording for step 7 was six seconds of digital silence.

**Session lists.** `cc-devthrottle session list --machine fi-inbox-test` was taken before and after each step, and
during each step every 10 seconds together with the factory's inbox row (`cc-devthrottle factory inbox list`); each
change was written down. They are in `session-lists/`. The test computer runs nothing but this factory's seats, so the
filtered list is the whole picture of what the test started.

## Step 1 - a typed message from the Cockpit

**Done:** at 21:13:07 typed "Step 1 test: please have a short note written that lists three plain-English names we
could give the notes folder of this test factory." in the Cockpit thread composer and pressed Send.

**Seen:** the composer said "Your message was left." The Gateway's trail, in its own words:
Sent from the Cockpit 21:13:10 - Received by the Secretary 21:14:34 - Handed to the Boss 21:14:35 - Delegated to
session 54dacb01-a92b-4c78-9807-a442e0f082ed 21:15:01. From the test Director's log: **one** Secretary
(98abebb9) started at 21:14:10, exactly 60 seconds after Send (the gather window), and closed at 21:15:22; **one** Boss
(5f66f871) started at 21:14:36 and closed at 21:16:22; the Boss started the Writer (54dacb01) at 21:14:57, which wrote
`notes/folder-name-ideas.md` and closed at 21:15:52. All three on Opus 5.5.

The stages passed faster than one screenshot each could be taken, so the thread screenshot shows the whole trail at
once.

| Phone | Cockpit |
|---|---|
| ![](00-phone-inbox-before.png) Before: every factory, no messages. | ![](01a-cockpit-thread-empty.png) Before, the test factory picked. *Taken by mistake at phone width (the phone tab's size was still set).* |
| ![](01e-phone-thread-delegated.png) The trail on the phone. | ![](01c-cockpit-sent.png) Just after Send: "Your message was left." *Also at phone width by mistake.* |
| | ![](01d-cockpit-thread-delegated.png) The trail in the Cockpit. |

Session lists: `session-lists/01-before.txt` (nothing on the test computer), `01-after-decision.txt` (all three seats
had already closed themselves); the starts and ends above are from the Director's log.

## Step 2 - a spoken Bad note from the phone

**Done:** at 21:33 on the phone thread: Bad, then Mic; the recording played into the recorder for 14.7 seconds; Send
in the voice box at 21:34:23.

**Seen:** "Your spoken message was left." The stored text equals the raw transcript byte for byte, and the words are
what was spoken: "This is a bad note for the test factory. The note with folder name ideas was too short and the names
were dull. Please have a better note written with five names and one line on why each one fits." (the verbatim rule
holds). Source Spoken, note Bad. Trail: Sent 21:34:23 - Received by the Secretary 21:35:38 - Handed to the Boss
21:35:48 - Delegated to session 8dc0c6c4-0af2-4986-a6a0-8f4f568b9a24 21:36:16. Session list every 10 seconds
(`session-lists/02-poll.txt`): **one** Secretary (number 156, d1a347d7) from 21:35:25, **one** Boss (number 170,
3dde71e8) from 21:35:51, the Writer (number 171, 8dc0c6c4) from 21:36:17; all on Opus 5.5; all gone by 21:37:23. The
Writer wrote `notes/folder-name-ideas-2.md`.

The trail says "Sent from the Cockpit": see defect 3. The voice box showed "Nothing is coming from your microphone"
after the speech ended, at 0:15, because the stream was silent from then on.

| Phone | Cockpit |
|---|---|
| ![](02a-phone-recording-bad-note.png) Recording, Bad pressed. | ![](02d-cockpit-thread-spoken-delegated.png) The trail in the Cockpit: Bad, Spoken, Delegated. |
| ![](02b-phone-recording-done.png) At 0:15, after the speech. | |
| ![](02c-phone-spoken-sent.png) "Your spoken message was left." | |
| ![](02e-phone-thread-spoken-delegated.png) The trail on the phone, with the session id. | |

## Step 3 - a second message while the Boss is alive

**First try (not a proof, kept because it shows a correct behaviour):** message A (21:46:45, Cockpit) was delegated by
Boss ebefa456; its Writer finished in a minute and the Boss closed itself at about 21:50:15. Message B (21:49:18, phone)
was handed over at 21:50:37, after the Boss had gone, so the wake correctly started a new Boss (85c1dcf5). One Boss at
a time, but not what step 3 must show. `session-lists/03a-poll.txt`, `03b-poll.txt`.

**Second try (the proof):** message A asked for a long note (21:53:57, Cockpit), to keep the Boss working while its
Writer wrote. When A read "With the Boss" (21:55:18), message B was sent from the phone (21:55:22). Session list
(`session-lists/03c-poll.txt`): Boss number 123 (38bac783) started at 21:55:18 and stayed alive; B's Secretary
(number 137) handed B over at 21:56:37; **no second Boss started**; Boss 123 decided B at 21:56:49 (Delegated to the
Writer f7955ba8) while its first Writer (32de16f7) was still writing. The Gateway's record names Boss 38bac783 as the
decider of both A and B.

| Phone | Cockpit |
|---|---|
| ![](03b-phone-message-b-sent.png) First try, B left. | ![](03a-cockpit-message-a-sent.png) First try, A left. |
| ![](03d-phone-message-b2-sent.png) Second try, B left while the Boss works. | ![](03c-cockpit-message-a2-sent.png) Second try, A left. |
| ![](03f-phone-thread-step3.png) Both tries on the phone, every trail. | ![](03e-cockpit-thread-step3.png) The whole thread so far in the Cockpit. |

## Step 4 - a request the Boss should refuse, and a reply from Your inbox

**Done:** at 22:04:06 from the Cockpit: "Step 4 test: please email every customer on our list a 50 percent discount
offer today, and pay for a newsletter tool with the company card to send it."

**Seen:** one Secretary (732ff59a), one Boss (bff32842); **Declined** at 22:05:44 (`session-lists/04-poll.txt`). The
thread shows the Boss's reason in its own words: "Outside this factory's work. This is a test factory that only writes
short notes into the notes folder. Emailing customers outside DevThrottle and paying for a newsletter tool on the
company card are both things this factory may not do (no external messages, no spending). ..." **Exactly one** new
Your inbox message, from "Factory Inbox test - Boss", subject "Declined: Step 4 test: ...", with the reason and the
owner's message quoted. The unread count rose to 1 on both shells: the Cockpit rail ("Inbox 1") and tab ("Your inbox 1
unread"), the phone tab and the phone menu ("Inbox 1 unread"). Opening it on the phone marked it Read; Mark unread
put it back (and the menu count with it).

**Reply:** at 22:10:19 from the Cockpit's Your inbox, Reply: "Step 4 reply from Your inbox: agreed, nothing is sent.
Instead please have the Writer draft the offer as a short note in the notes folder." The screen said "Left in the
Factory inbox of Factory Inbox test 17:10". It appeared in the factory inbox at once as Sent, was received at 22:11,
handed over, and Delegated at 22:13:33 to a Writer (1de3c5de), which wrote `notes/discount-offer-draft.md`
(`session-lists/04-reply-poll.txt`). Nothing was emailed.

| Phone | Cockpit |
|---|---|
| ![](04e-phone-your-inbox-unread.png) Your inbox, 1 unread. | ![](04a-cockpit-refusal-request-sent.png) The request left. |
| ![](04f-phone-your-inbox-message.png) The message: the Boss's reason, Open, Reply, Mark unread, Archive. | ![](04b-cockpit-your-inbox-unread.png) Declined on the list; the rail says Inbox 1 and the tab 1 unread. |
| ![](04d-phone-menu-inbox-count.png) The phone menu: "Inbox 1 unread". | ![](04c-cockpit-your-inbox-list.png) Your inbox: the new message Unread. |
| ![](04k-phone-thread-declined-and-reply.png) Declined with the reason, then the reply Delegated. | ![](04g-cockpit-your-inbox-reading.png) Reading it. |
| | ![](04h-cockpit-reply-typed.png) The reply typed. |
| | ![](04i-cockpit-reply-sent.png) "Left in the Factory inbox of Factory Inbox test". |
| | ![](04j-cockpit-thread-declined-and-reply.png) The factory thread: Declined, then the reply Delegated. |

## Step 5 - a session writes to Your inbox

**Done:** a seat of my own on the test Director (number 146, cffb02a7, Opus 5.5) ran
`cc-devthrottle inbox send --subject "Step 5 test: a session writes to Your inbox" --body "..."` at 22:21 (main's
tool; the installed one has no `inbox send`). Its answer: `sent: Your inbox`, id 801f88b2-733f-4cb2-874d-d08ff5413328,
from "146 Factory Inbox - QA test seat - step 5 inbox send", state Unread, one link.

**Seen:** the message Unread on both shells, naming the session. Its link, in the Gateway's words "Open 146 Factory
Inbox - QA test seat - step 5 inbox send", opened `/session/cffb02a7-...` in the Cockpit and
`/mobile/session/cffb02a7-...` on the phone, both showing that session live, with the command's output. The seat was
then stopped (`session-lists/05-*.txt`). (A first seat, 1e4e4043, in the new factory folder, stopped at the agent's
"trust this folder" question; my Enter chose "No, exit" and it ended. See defect 7.)

**Not yet checked: that no email was sent.** `inbox send` sends none by design; the check against the mail sender
waits for the email-off deployment.

| Phone | Cockpit |
|---|---|
| ![](05a-phone-your-inbox-session-message.png) Unread, from session 146. | ![](05b-cockpit-your-inbox-session-message-unread.png) Unread, from session 146. |
| ![](05e-phone-session-message-reading.png) Reading it, with the Open link. | ![](05c-cockpit-session-message-reading.png) Reading it, with the Open link. |
| ![](05f-phone-open-the-session.png) Open: the session on the phone. | ![](05d-cockpit-open-the-session.png) Open: the session in the Cockpit. |

## Step 6 - the factory summary and the morning message

Waiting for the Delivery Lead's word (email off, pull request 4664). Not run yet.

## Step 7 - failure cases

### A Secretary stopped mid-claim: the next one takes it

**Done:** three Bad notes from the Cockpit at 22:24:31 to 22:24:36 (a Bad note makes the Secretary write a memory
note between its claim and its hand-over, which widens the gap). A script watched the list route twice a second; the
row turned Received at 22:25:48.7 and the Secretary (ac271725) was stopped with
`cc-devthrottle session stop --reason "..."` at 22:25:48.7; its process had ended by 22:25:50.

**Seen:** a new Secretary (4b0de1f1) started within 5 seconds (22:25:55) and the three messages were Handed to the Boss
at 22:26:19, while only the new Secretary was alive; the Boss (c4bed4e2) decided all three Noted at 22:26:35
(`session-lists/07b-poll.txt`). The thread does not name which Secretary handed the bundle; that it was the new one is
read from the times.

| Phone | Cockpit |
|---|---|
| ![](07c-phone-thread-secretary-stopped-next-took-it.png) Received 17:25, Handed 17:26, Noted. | ![](07a-cockpit-three-bad-notes-sent.png) The three Bad notes left. |
| | ![](07b-cockpit-thread-secretary-stopped-next-took-it.png) The same trails in the Cockpit. |

### The Director stopped: the thread says why, the stuck alert, and it resumes on reconnect

**Done:** the test Director was stopped with its own stop request at 22:34:19 (gone at 22:34:23; the Gateway listed
it "stopped"). At 22:34:34 a message was left from the phone.

**Seen:**
- After the 60-second window the Gateway's state for the message read: "Waiting - no Director is running on the Boss's
  computer, fi-inbox-test; it goes on when one connects there" (`cc-devthrottle factory inbox list`, and the
  message's state label in the Gateway's answer). The phone and Cockpit list rows said "1 waiting for a decision"
  with the badge Sent. The thread itself could not be shown: see defect 1 and `07e-cockpit-thread-cannot-reach-gateway.png`.
- **The stuck alert** appeared at 22:49:34, exactly 15 minutes after the message: "The Factory inbox of Factory Inbox
  test is stuck - A message was not received in 15 minutes: no Director is running on fi-inbox-test, the Boss's
  computer, so no Secretary can start. Start the Director on fi-inbox-test." It showed under Needs you on both shells.
- **Resumes on reconnect:** the Director was started again at 22:50:33. A Secretary started at 22:50:50, the message
  was handed over at 22:51:01 and Noted at 22:51:13; both seats closed by 22:52:09 (`session-lists/07k-poll.txt`).
  The stuck alert then left the alerts by itself ("Nothing needs you").

| Phone | Cockpit |
|---|---|
| ![](07d-phone-sent-while-director-stopped.png) The message left while the Director was stopped. | ![](07e-cockpit-thread-cannot-reach-gateway.png) The thread could not load (defect 1); the list row: "1 waiting for a decision", Sent. |
| ![](07j-phone-stuck-alert.png) The stuck alert, with the reason. | ![](07i-cockpit-stuck-alert.png) The stuck alert in the Cockpit's alerts. |
| ![](07l-phone-alerts-after-reconnect.png) After reconnecting: "Nothing needs you". | |

### A spoken message with no speech

**Done:** at 22:41:54, from the phone's list composer with Factory Inbox test picked: Mic, six seconds of silence,
Send. (The voice box warned "The microphone has stopped sending audio".)

**Seen:** "Nothing was heard in the recording, so nothing was left in the factory inbox." The factory's inbox still
held only the one waiting message.

| Phone |
|---|
| ![](07g-phone-recording-silence.png) Recording silence. |
| ![](07h-phone-silence-answer.png) "Nothing was heard in the recording, so nothing was left in the factory inbox." |

### An archived factory is refused in words

**Done:** with the phone's thread page of the test factory already open, the test factory (mine, never an owner's)
was archived from its Cockpit page at 22:57 (the confirmation: it leaves the Factories list, keeps its history and
memory, and can be restored). Then a message was sent from the phone page that was opened before the archive.

**Seen:** refused in words: "'factory-inbox-test' is archived, so it takes no messages. Restore it from the Factories
screen first." The typed text stayed in the box. The factory left the Factory inbox list on both shells, and its page
no longer offered "Leave a message". It was then restored for step 6 ("Factory Inbox test is back on the Factories
list") and will be archived again at the end of the run.

| Phone | Cockpit |
|---|---|
| ![](07p-phone-archived-factory-refused.png) The refusal, in the Gateway's words. | ![](07m-cockpit-factory-page-before-archive.png) Before: Leave a message, Archive factory. |
| | ![](07n-cockpit-archive-confirm.png) The archive confirmation. |
| | ![](07o-cockpit-factory-archived.png) Archived: no Leave a message; Restore. |
| | ![](07q-cockpit-inbox-list-without-archived-factory.png) The Factory inbox list without it. |

## Step 8 - the running cost of one Secretary and one Boss for one bundle

From each seat's own agent transcript (token use counted once per reply) and the test Director's log (start and end).
Every seat of the run is in `token-use.md`.

**One bundle, step 1 (one typed message):**

| Seat | Replies | Fresh input | Cache write | Cache read | Output | Working | Alive (start to end) |
|---|---|---|---|---|---|---|---|
| Secretary 98abebb9 | 6 | 12 | 17,511 | 240,832 | 786 | 0.6 minutes | 1.2 minutes |
| Boss 5f66f871 | 8 | 18 | 19,569 | 340,379 | 1,198 | 1.1 minutes | 1.8 minutes |
| **Both** | 14 | 30 | 37,080 | 581,211 | 1,984 | 1.7 minutes | Send to decision: 1 minute 51 seconds |

The Boss's "alive" time includes waiting for its Writer to hand back and the Director's 30-second reaping round.
The Writer that did the work is not counted here (it cost 25,307 cache write, 127,781 cache read and 698 output
tokens).

**Across the whole run** (11 Secretaries, 9 Bosses): a Secretary used a median of 17,612 cache-write, 241,093
cache-read and 896 output tokens (cache read 153,955 to 430,625; output 590 to 1,763) and worked 0.3 to 0.6 minutes.
A Boss used a median of 20,301 cache-write, 340,379 cache-read and 1,287 output tokens (cache read 158,509 to 634,958;
output 709 to 3,087) and worked 0.3 to 2.4 minutes; the longer Bosses were the ones that waited for their Writers or
decided a second bundle. Almost all the volume is cache reads of the seat's opening (brief, rules and skills), which
every reply re-reads. No money figure is given: the price per token was not checked for this report.

## Evidence: what this run did NOT prove

- **Step 6** (the Summary button, the morning message carrying it) and **every "no email is sent" check**: waiting for
  the email-off deployment.
- **The thread on screen at its normal re-read.** Every thread screenshot needed the lengthened re-read timer in my
  own test tabs (defect 1). Without it the owner's screens show "Loading..." or "Can't reach the Gateway".
- **"Sent from the phone".** No message in this run came from a device signed in as a phone (defect 3), so the phone's
  surface label was never seen.
- **A real phone and a real microphone.** The phone was the mobile web app at phone size in a desktop browser; the
  spoken words came from a synthetic voice played into the page's recorder through a replaced `getUserMedia`. The
  durable upload, the transcription, the verbatim store and the thread were real; the microphone and the device's
  handling of audio were not.
- **The thread showing the "waiting - no Director" reason.** Seen in the Gateway's answer and the command line, not on
  screen (defect 1).
- **Which Secretary handed the bundle after the mid-claim stop.** Read from the times, not from a record naming it.
- **A second physical computer.** The test Director ran on devlinux in a container with its own host name; the
  Gateway saw it as its own computer, but it shared devlinux's processor, memory, disk and network.
- **An undefined factory** (registered with a folder but no definition) and **a night-start Boss rung mid-run**: not
  exercised; the test factory is defined.
- **Cancel, Pause and a held recording** on the voice box: not exercised.
- **The cost in money**: tokens and minutes only.

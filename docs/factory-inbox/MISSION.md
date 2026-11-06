# Mission: Factory Inbox and Your Inbox

Mission id fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32. Mission document, final, 4 November 2026. Written by the
Architect with the owner; every question answered. The owner's report (with mockups) is dev report
20185fde-cc3d-43bf-a024-e33e9737fd0e, and a copy of it sits beside this file as `report.html` - open it for the
mockups A to D. The owner's answers are stored on that report (`cc-dev-reports answers --report 20185fde-cc3d-43bf-a024-e33e9737fd0e`).

## 1. The mission

Replace email with two DevThrottle inboxes: a Factory inbox for each factory, so the owner can leave its Boss
work in seconds, and Your inbox for the owner, so the fleet can tell him things without email.

## 2. The why

The owner, 4 November 2026: talking to a Boss means waiting for it to fire up; he wants to message any factory
quickly, for example while listening to a video. "We're starting to send a hell of a lot of email around, and it's
really annoying because emails are not connected to my sessions." "It's a really bad way of communicating and it
adds work to me. It doesn't minimize." "I want to remove all the emails I have in my inbox in the morning."

## 3. The goal (approved by the owner, word for word)

"From the phone or the Cockpit I can leave any factory a typed or spoken message in seconds, and never wait for
anything to start. Its Secretary receives it and hands it to the Boss. The Boss delegates it and never does it
itself. Every message shows that it was received and what was decided. I only hear back when something cannot or
should not be done, and I hear it in my DevThrottle inbox, not by email. Factories stop emailing me."

The proof is the end-to-end QA report in section 7, which proves exactly these sentences.

## 4. Decisions (the owner's words, 4 November 2026)

- Input: "from the phone or from the cockpit ... I should be able to type it, but I should also be able to use our
  normal transcription, our normal box as input."
- Logged: "messages to the boss should be obviously logged. So if they're missed, they can be found again. And then
  when they're handled, they should also be clearly said that they were dealt with, that they were listened to."
- Outcomes vary: "It could be like a brand new feature they need to add or lots of instructions."
- The Secretary: "What we spin up is not the normal boss to deal with the inbox. It's the secretary of the boss ...
  deals with the inbox and then hands it off to the boss." "The secretary filters out for the boss and then the boss
  deals with it." It "can shut itself down when it's finished dealing with the inbox or it can stay open as long as
  it's working on things."
- The Boss only delegates: "the boss should never deal with things ... we also write this into our factory skill. The
  boss is just a delegator ... It can add a to-do item to do later. It can spin up a session, do it right away. But it
  can't be doing it itself."
- The Boss judges: "Boss should always judge if things are good and bad, but that's the boss's job to push back on me
  if I say something stupid."
- Few replies: "I want to know if it cannot be done or it should not be done. But if it is just handled ... it should
  just be in the memory of the factory and maybe we could summarize it in the morning if I ask for a factory summary."
- Your inbox: "an inbox now for the user ... kind of like email, and then I can check those in the morning."
  "Reachable both on the phone and on the cockpit."
- Two kinds: "there's a difference between sending me a message as the user, and then just leaving a message in the
  factory. Because factory messages, nobody has to read other than the boss."
- Surfaces: phone AND Cockpit.
- DEPLOY GRANT, THIS MISSION ONLY: "this needs to be fully deployed and tested. So it also means that there needs to
  be mock-ups for this." Each phase is deployed with the deploy-hosted-gateway skill and tested live. Never by hand.
- The wake was left to the Architect ("review the code and tell me how would we actually do this"): answered in
  section 5 and accepted with the goal.

Answers on the report (all the Architect's recommendation):

| Question | Answer |
|---|---|
| Names | "Factory inbox" (messages left in a factory) and "Your inbox" (messages to the owner). On screen both sit under ONE Inbox entry with two tabs. Add both to the terminology skill. |
| Boss not running at handover | Start the Boss at once. It delegates and closes, so the run is short. |
| Factory emails | When Your inbox ships, the factory morning email becomes a Your inbox message and the email stops. Built-in skills change so "email me" and "tell me" mean Your inbox. A real email only when the owner names someone else, or says "send me an email". |
| Replying from Your inbox | Yes. A reply to a factory's message goes into that factory's inbox. A reply to a session's message goes to that session if it is still running; if it has ended, the reply says so and offers to leave it in its factory or start a new session. |
| Where "the Boss only delegates" is written | Both the Gateway's Boss opening and the factory-interviewer skill (devthrottle_internal), AND the factory definition check flags a Boss brief that tells the Boss to do the work itself. |
| Gather window | 60 seconds after the first message. |

## 5. Design

### The path of one message

You type or speak and press Send -> the Gateway logs it (Sent) -> 60 seconds later the Gateway starts a Secretary,
or rings the one already running -> the Secretary files it (Received) -> hands one bundle to the Boss (With the Boss)
-> the Boss decides each item: Delegated / To do / Noted / Declined / Cannot be done (or Pushed back).

Only Declined, Cannot be done and Pushed back also write a message into Your inbox. Everything else is quiet and
visible on the factory's thread and in the factory summary.

### What wakes whom - an event, never polling

The Gateway writes every message, so the write is the event. Reuse, do not rebuild:

| Moment | Gateway does | Reuse |
|---|---|---|
| A message arrives | Opens a 60-second gather window for that factory, or joins the open one. | Fleet Manager event batching window (`Fleet/FleetManagerEventService.cs`). |
| Window closes, no Secretary alive | Starts ONE Secretary on the Boss's computer; one start in flight per factory. | Factory seat start (`Factory/Definition/FactorySeatStart.cs`, the Talk and schedule path); the trigger in-flight lock (`Factory/Triggers/TriggerService.cs`). |
| Window closes, a Secretary alive | Rings it with one line ("N new factory inbox messages - run cc-devthrottle factory inbox list") only when it is waiting for a prompt; never while it works or holds the owner's draft. | Fleet doorbell (`Messaging/FleetDoorbell.cs`) and the guarded prompt send (`FleetManagerPromptSend` Busy / OwnerDraft). |
| Secretary hands a bundle | Boss session of this factory alive -> ring when idle (including a night-start Boss). None alive -> start ONE Boss at once, as itself (brief, memory, skills - the night-start seat kit, NOT the rules-only kit of the Boss start door). | Trigger `IsLastSessionAlive`; session `Factory` / `FactorySeat` fields; for an undefined factory the Gateway remembers the Boss session it started, as a trigger remembers `LastSessionId`. |
| Nobody picks it up | Not Received within 15 minutes, or a bundle not decided within 2 hours -> alert "the Factory inbox of X is stuck", with the reason. | Doorbell stuck rule; a new `IAlertSource` (`Alerts/IAlertSource.cs`). |
| No computer | The message waits; the thread says "waiting - no Director is running on the Boss's computer"; retried when that Director connects. Never another computer. | The Talk route's "no other computer" rule (`Api/FactoryTalkEndpoints.cs`). |

Every factory with a Boss seat gets an inbox, defined or not. Refused in words: unknown, archived, or Boss-less
factory (the Boss start door's checks in `Api/SpawnFactory.cs` TryStartTheBoss).

### The Secretary

- A product-given seat for every factory with a Boss. Nobody defines it. Runs on the Boss's computer, in the factory,
  with the factory's rules plus the inbox commands, nothing else.
- Reads new messages, merges duplicates, attaches what each is about (run, report, record), marks each Received,
  writes a Good or Bad note into factory memory as a judgement for the Boss, and hands ONE bundle to the Boss.
- Never judges, never decides, never starts work sessions - enforced: a Secretary's session key cannot start seats
  (see `Factory/Definition/FactorySeatDelegation.cs`).
- Closes itself (`cc-devthrottle session done`) when the inbox is empty and its bundle is handed over; if rung while
  working it stays open.

### The Boss with a bundle

- `cc-devthrottle factory inbox bundle`, then one decision per item: `decide <id> delegated --session <id>`,
  `todo`, `noted`, `declined --why "..."`, `cannot --why "..."`, `pushback --why "..."`.
- Delegated is accepted ONLY with the id of a session that Boss actually started. To do adds a row to the factory's
  to-do list (a factory record kind; add one if none exists). That makes "never does it itself" checkable.
- The Boss opening gets the rule: the Boss only delegates; it judges good and bad and pushes back on the owner.

### Your inbox

- Messages to the owner from a Boss (declined / cannot / pushback), from each factory's morning summary (replacing
  the factory email, `Factory/Email/`), and from any session: `cc-devthrottle inbox send --subject "..." --body "..."`.
  Each shows who sent it and links to its session, report or factory.
- Unread, Read, Archived. A count on the Inbox entry. Not an alert (alerts = answer now; dev reports = documents with
  questions). Nothing is deleted by the system.
- Reply routing as decided above.

### Screens (mockups A to D in report.html)

- A. Factory inbox list, with a composer on the list itself (pick a factory, Good / Bad / Mic / Send).
- B. One factory's thread: each message with its trail (Sent - Received - decision) - all words decided on the
  Gateway, the client only prints them (CLAUDE.md rule 7).
- C. Your inbox list.
- D. The Cockpit: Inbox in the left rail, list, reading pane; the factory page also gets "Leave a message".
- Built once in `packages/client-core`, mounted in `apps/mobile` and `apps/cockpit` (the Settings rule, CLAUDE.md
  rule 8, applies by analogy).

### Voice

The existing durable voice box and upload (`Api/GatewayDictationEndpoint.cs`), with the target a factory inbox
instead of a session. Under the verbatim rule (CLAUDE.md): stored text = speech model output plus only hand-listed
corrections; raw text kept beside it.

### The factory summary

Built by the Gateway from the log (no agent): what came in since a time, and the decision on each. Shown by the
thread's Summary button and carried by each factory's morning message in Your inbox.

### Out of scope

Email to other people; fleet messaging between sessions; messages to seats other than the Boss.

### Why not fleet messaging

A fleet message is addressed to a session id; the Boss is often not running, and fleet messages go stuck after
three rings. The Factory inbox is addressed to the factory.

## 6. Phases

| Phase | What | Seats |
|---|---|---|
| 0. Words and rules | Terminology entries (Factory inbox, Your inbox, Secretary, the decision words). The Boss-only-delegates rule in the Gateway Boss opening (`Factory/Talk/FactorySeatOpening.cs` and the night-start opening), the factory-interviewer skill, and the definition check (`Factory/Definition/FactoryDefinitionCheck.cs`). Built-in skill copies regenerated from the shipped file (CLAUDE.md, one source). | One Developer. |
| 1. Factory inbox store, routes, commands | Table and migrations (SQLite `Data/Migrations` and `CcDirector.Gateway.Migrations.Postgres`) for messages, bundles, decisions. Owner routes (send, list, thread, summary) and session routes (list, receive, bundle, decide). `cc-devthrottle factory inbox` commands, documented in `docs/cli-reference.md`. Claims lapse when the claiming session ends (model: dev report claims, `DevReports/`). States decided on the Gateway. New routes added to SessionKeyGuard deliberately. | Tech Lead + two Developers. |
| 2. The wake | Gather window, one-start-per-factory lock, the Secretary seat start and its key limit, ring when idle, Boss start or ring on a bundle, stuck alert, waiting with no computer. Must test a night-start Boss rung mid-run. | Same Tech Lead. |
| 3. Your inbox | Store, `cc-devthrottle inbox send`, Unread / Read / Archive, reply routing, Inbox count. | One Developer, parallel with phase 2. |
| 4. Screens | Mockups A-D in client-core, mounted on phone and Cockpit; typed and spoken input; Good and Bad; states; summary. Behind the factory area switch until the phase is whole. | Two Developers, after phase 1 routes merge. |
| 5. Email off | Factory morning email -> Your inbox message, email stops. Built-in skills: "email me" / "tell me" = Your inbox. Definition check flags a seat brief that emails the owner. Start with a search for every place that emails the owner today. Lands LAST, after Your inbox is proven. | One Developer. |
| 6. Proof and deploy | End-to-end QA report (section 7) on the deployed Gateway. Deploy after every phase with deploy-hosted-gateway. | Delivery Lead. |

Reviewer for every piece of work: a separate review session, started with `cc-devthrottle session review`
(in the owner's reviewer order; never Pi, Grok or Gemini). Style guides: `docs/CodingStyle.md`,
`docs/VisualStyle.md`, `docs/CockpitVisualStyle.md`.

## 7. The check

Every task: `.\scripts\test-local.ps1` green AND `.\scripts\test-local.ps1 -Parked` (Gateway changes; needs Docker);
client-core and mobile web tests; a separate review.

Gateway tests that must exist and pass:

- Three messages in one second start exactly one Secretary.
- A running Secretary is rung; no second one starts.
- A ring is never typed while the session works or holds the owner's draft.
- A live Boss session is rung, never doubled.
- Delegated without a started session is refused.
- A Secretary's key cannot start a seat.
- A claim held by an ended session is taken by the next one.
- A message not Received in 15 minutes raises the stuck alert.
- A spoken message equals the raw transcript plus only listed corrections.
- Declined, Cannot be done and Pushed back each write exactly one Your inbox message; Delegated, To do, Noted write none.

End to end on the DEPLOYED Gateway, a test factory on a test Director in slot 5 (CLAUDE.md rule 0b - launch via the
scheduled task, never kill the owner's Directors), driven through phone and Cockpit with browser-harness,
screenshots and the session list at every step:

- A spoken Bad note from the phone: one Secretary starts, marks Received, hands over, closes; one Boss starts,
  delegates to a work session; the thread shows the full trail.
- A second message while the Boss is alive: no second Boss; the decision arrives.
- A request the Boss should refuse: Declined on the thread and one new Your inbox message; a reply from Your inbox
  lands back in the factory inbox.
- A session runs `cc-devthrottle inbox send`: it appears on phone and Cockpit; no email is sent.
- Failure cases: Director stopped (thread says why; resumes on reconnect); Secretary killed mid-claim (next one
  takes it); stuck alert; archived factory refused in words.

The QA report is committed beside the code.

## 8. Merge plan

Each task merges to main the same day on a green local run and a review, then is deployed (grant above). Safe
because phases 1-3 add new tables and routes and change no existing answer; screens stay behind the factory area
switch until phase 4 is whole; the email switch-off (phase 5) is the only change to existing behaviour and lands last.

## 9. Where it ends

Merged to main, deployed, tested live, QA report committed. The owner shuts the Delivery Lead down.

## 10. Questions

All answered (section 4). Open items the build must settle with a test, not a guess:

- Whether a factory to-do list exists as a record kind (if not, phase 1 adds one).
- How to limit a session key to "may not start seats" (phase 2 proves it).
- How a night-start Boss behaves when rung mid-run (phase 2 tests it).
- The running cost of one Secretary plus one Boss per batch (measure in phase 6 and report it).
- Every place that emails the owner today outside the four built-in skills (phase 5 starts with a search).


## Owner ruling, 6 November 2026 - the Boss the owner's agent starts IS the inbox's Boss

Soren, in chat: "Yes, I also want to allow those." This overturns the Tech Lead's ruling of 5 November 2026 (wake
review, option (a)) for a Boss the Gateway RECORDED as started by a session in no factory (issue #4598,
`factory_boss_starts`). Such a Boss decides bundles, starts seats, and the wake rings it rather than starting a seat-kit
Boss beside it. A rules-only session in the Boss seat with no record (a Boss's child, a restored child, a start-door Boss
from before the record existed) is still not the Boss. Recorded in the repository at
`docs/factory-inbox/reviews/phase-2-wake-review.md`, answer 1.

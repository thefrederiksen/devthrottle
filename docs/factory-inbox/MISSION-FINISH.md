# Mission: Finish the Factory Inbox and Your Inbox

Mission id fb77e0c6-2fc8-4cd8-beb0-5be020b6fe32 (the same mission, finishing run). Written 6 November 2026 by the
Delivery Lead of the first run (session 111) at the owner's request: "a full mission you can implement from a new
session end to end, with the QA report at the end with screenshots showing how the inbox is working."

**This document sits on top of `MISSION.md` beside it.** `MISSION.md` holds the decisions, the design and the
owner's words; it still wins on anything this document does not say. This document says what is DONE, what is LEFT,
and exactly how to finish it. The owner's report for this document, with screenshots and mockups, is
`finish-plan.html` beside it (published as dev report b1a8fe2f-5d4b-4af6-b51a-8e68d3b70c1c).

All of the mission record lives in the repository under `docs/factory-inbox/`: `MISSION.md`, this file, `report.html`
(the original mockups A to D), `finish-plan.html`, `phase-1-plan.md`, and `reviews/`. Paths below like
`reviews/x.md` mean `docs/factory-inbox/reviews/x.md`. (The first run kept them in
`D:\ReposFred\_briefs\factory-inbox-2026-11-04` on SOREN_NORTH; that folder is no longer the record.)

## Where this run happens - the owner's words, 6 November 2026

"I'd really like to start it on the Dev Linux computer ... and run the end-to-end on dev Linux." "Start it with an
Opus model, the default Opus model." "We shouldn't really use tail scale for anything anymore."

- **This finishing run is driven from devlinux** (Ubuntu, the Director named `devlinux`). The Delivery Lead seat,
  every Developer and every reviewer it opens, its gate runs and the end-to-end proof all run on devlinux. Work in
  worktrees off origin/main under `/home/soren/ReposFred/`. Every branch named below is on GitHub; fetch it.
- **Every seat runs on Opus** unless it is a reviewer started by `cc-devthrottle session review` (that command picks
  the reviewer by the owner's order). Spawn a Developer with
  `--agent ClaudeCode --args "--model opus --dangerously-skip-permissions"`. Never leave a seat on a default model.
- **No Tailscale.** Do not use, configure or depend on Tailscale for anything. Every test Gateway still starts with
  `CC_GATEWAY_NO_TAILSCALE=1` so it cannot touch a tailnet setting. The phone and Cockpit screens are driven against
  the hosted Gateway (`https://gateway.devthrottle.com`), which needs no tailnet.
- **First, prove the machine can do the whole job, before any other step** (law: check, do not assume). On devlinux:
  `pwsh` runs `scripts/test-local.ps1`; Docker runs (the `-Parked` gate needs it); the .NET SDK pinned in
  `global.json` (10.0.403) is installed; `node`/`npm` run the client-core, cockpit and mobile web tests; `gh` is signed
  in to `thefrederiksen/devthrottle` and can merge and start the deploy workflow; `cc-dev-reports` publishes; a
  browser can be driven with browser-harness for the screenshots; and a second, TEST Director can run on devlinux
  beside the one hosting your seat (see step F). Write what you found into `docs/factory-inbox/devlinux-readiness.md`.
  Anything missing that you cannot install without the owner (an administrator password, a sign-in): ask him ONE
  plain question, with a click-or-paste answer, and carry on with everything else meanwhile.

## 1. The mission

Finish the two inboxes so the owner uses them every day: put the built screens in front of him, build Your inbox,
turn the factory emails off, and prove all of it on the live service with a QA report full of screenshots.

## 2. The why

The owner, 4 November 2026: "We're starting to send a hell of a lot of email around, and it's really annoying because
emails are not connected to my sessions." "I want to remove all the emails I have in my inbox in the morning." And
on 6 November: "when will I start seeing an inbox menu in my cockpit and my mobile?" The back end is live; nothing he
can see is. Every day it stays that way the emails keep coming.

## 3. The goal

Unchanged from `MISSION.md` section 3, word for word:

"From the phone or the Cockpit I can leave any factory a typed or spoken message in seconds, and never wait for
anything to start. Its Secretary receives it and hands it to the Boss. The Boss delegates it and never does it
itself. Every message shows that it was received and what was decided. I only hear back when something cannot or
should not be done, and I hear it in my DevThrottle inbox, not by email. Factories stop emailing me."

Proven by the end-to-end QA report of step F below, with screenshots from the phone and the Cockpit at every step,
committed beside the code AND published to the owner as a dev report.

## 4. Where it stands (6 November 2026, 11:30 UTC)

Live on the hosted Gateway (main at 761c24802 and later):

| Piece | State |
|---|---|
| Phase 0 - words and rules (the Boss only delegates) | Merged, deployed. |
| Phase 1 - Factory inbox store, routes, `cc-devthrottle factory inbox` commands | Merged, deployed. |
| Phase 2 - the wake: gather window, Secretary start and key limit, ring when idle, Boss start or ring on a bundle, stuck alert, waiting with no Director | Merged (4599, 4601, 4607), deployed and watched. |

Built and NOT yet on main:

| Pull request | What | State |
|---|---|---|
| 4608 `factory-inbox/p3-your-inbox` (worktree `devthrottle-fi-p3`) | Phase 3 Your inbox back end: store, routes, `cc-devthrottle inbox send`, Unread / Read / Archive, reply routing (factory and session), the factory offer for an ended seat, exactly one Your inbox message for Declined / Cannot be done / Pushed back in the decision's own transaction, AddYourInbox migrations after AddFactoryInboxWake. | Review APPROVE, no finding (`reviews/phase-3-your-inbox-review.md`, four notes for the author). Author's parked run green except the then-red main test that 4606 has since fixed. Needs: your own gate, merge. |
| 4610 (worktree `devthrottle-idle-tree`) | Test-only fix: `IdleTreeInspectionWatcherTests.ManyParksInsideTheGap_ShareOneTrailingInspection` raced the thread pool under load. | Review APPROVE (`reviews/idle-watcher-test-review.md`). Needs: your own gate, merge. |
| 4603 `factory-inbox/p4-factory-inbox-screens` (worktree `devthrottle-fi-p4a`) | Phase 4 Factory inbox screens, mockups A, B and the Cockpit part of D: the list with the composer on it, the thread with each message's trail and the Summary button, Inbox in the Cockpit rail with a reading pane, "Leave a message" on the factory page, spoken send through the durable voice upload. QA screenshots in `docs/qa/factory-inbox/phase-4-factory-inbox-screens/`. | Re-review: findings 1 and 3 fixed at the root; finding 2 safely deferred; **merge NOT yet** - rebase onto origin/main (two small conflicts in the test fixture's `Parts`) and add the wake call the re-review names (a spoken message must open the gather window from the press time, as a typed one does). Read the re-review section at the end of `reviews/phase-4-factory-inbox-screens-review.md` first. |

Not started: Your inbox screens (mockup C), phase 4 finding 2, phase 5 email off, phase 6 proof.

Every seat of the first run is shut down. All their work is pushed; no worktree holds uncommitted changes.

## 5. Design of what is left

The design in `MISSION.md` section 5 stands. Additions for the finishing run:

### One Inbox entry, two tabs - on both surfaces

- Cockpit: **Inbox** in the left rail under Work (already built by 4603), with a count of unread Your inbox
  messages beside it. The page has two tabs: **Factory inbox** (built) and **Your inbox** (to build). The tab slot is
  ready (`InboxTabs` in client-core).
- Phone: **Inbox** in the menu (built by 4603), the same two tabs, the same count.
- Built once in `packages/client-core`, mounted in `apps/cockpit` and `apps/mobile`. Each shell supplies only its
  frame. If you find yourself writing inbox content inside a shell, stop: it belongs in client-core (CLAUDE.md rule 8
  applied by analogy).
- Every word, tone, count and badge is decided on the Gateway and printed verbatim (CLAUDE.md rule 7).

### Your inbox screens (mockup C, and its Cockpit form)

- **List:** newest first. Each row: who sent it (a factory's Boss, a factory's morning summary, or a session by its
  name), the subject, one line of the body, the time, and Unread in bold with a tone badge. Filters: Unread / All /
  Archived (Gateway-provided labels and counts).
- **Reading a message:** the full body; where it came from with a link to its factory thread, session or report; the
  buttons **Reply**, **Archive**, and, when the sender is a session, **Open the session**. Opening marks it Read.
- **Reply:** the same composer as the factory inbox (typed or spoken). The Gateway routes it (built in 4608): to the
  factory's inbox for a factory message; to the session if it is running; if it has ended, the Gateway's answer
  offers "Leave it in its factory" or "Start a new session" and the screen shows exactly those two choices.
- **Cockpit:** list on the left, reading pane on the right, like the Factory inbox tab.
- **Phone:** list, tap to read, back to the list.
- Empty state, Gateway words: "Nothing for you. Factories and sessions write here instead of emailing you."

### Phase 4 finding 2 (before the proof)

A spoken message whose recording is held (transcription failed and retry is pending) must never be lost while the
screen says it was left: the 24-hour sweep that abandons held recordings must not drop a factory-inbox recording
without the thread saying so in the Gateway's words. Read finding 2 in the review file; its fix is its own pull
request.

### Email off (phase 5, lands last)

As `MISSION.md` phase 5: start with a search for every place that emails the owner today (Gateway `Factory/Email/`,
built-in skills, the factory-interviewer skill, scheduled run definitions, cc-tools). The factory morning email
becomes a Your inbox message carrying the factory summary; the email stops. Built-in skills: "email me" and "tell
me" mean `cc-devthrottle inbox send`. The definition check flags a seat brief that emails the owner. A real email
only when the owner names someone else or says "send me an email". Built-in skills are changed in ONE place
(`src/CcDirector.Gateway/Skills/Content/<id>.skill.md`), the `.claude/skills` copy regenerated, then deployed.

## 6. Steps

Run them in this order. B and C run in parallel once A is merged; D starts after C merges; E lands last; F proves it.

| Step | What | Seats | Done when |
|---|---|---|---|
| A. Land what is built | Gate and merge 4610, then 4608. A new Developer takes 4603: rebase, the wake call, answer the re-review; re-review; gate; merge. Deploy and watch. Confirm the factory area switch is ON for the owner's account on the live Gateway (read it, do not guess); if it is off, ask the owner before switching it. | Delivery Lead; one Developer for 4603. | The owner can open Inbox, Factory inbox on the phone and in the Cockpit, live. Tell him the same hour. |
| B. Finding 2 | The held-recording fix of section 5. | One Developer. | Merged with a test that loses the message without the fix. |
| C. Your inbox screens | Section 5. Plus the unread count on the Inbox entry. QA screenshots of every state (list, unread, read, reply to a factory, reply to a running and to an ended session, archive, empty, refused). | One Developer. | Merged, deployed. The owner can read Your inbox live. Tell him. |
| D. Email off | Section 5. | One Developer. | Merged, deployed. The search result is committed as a list of every owner email found and what became of each. |
| E. Proof | Step F below, on the deployed Gateway. | Delivery Lead runs it, or one QA Developer it opens and watches. | QA report committed and published. |

Review for every pull request: `cc-devthrottle session review <worktree>` - it moves through the owner's reviewer order by itself.
Codex refused its model on every start on 5 and 6 November. Never Pi, Grok or Gemini.

## 7. The check

Every pull request: `.\scripts\test-local.ps1 -Parked` (needs Docker) run by the Delivery Lead in its OWN gate
worktree (never a Developer's), "RESULT: GREEN" AND every suite `outcome=Completed` in the TRX verdict block; plus
client-core, cockpit and mobile web tests for screen changes (the local gate runs no web tests); plus a separate
review. A red only in a test the pull request does not touch: run that test alone at least five times, prove it is
not this change, merge, and give it to a Developer to fix at the root or remove. Never call a test flaky.

Gateway tests that must exist and pass are listed in `MISSION.md` section 7, all already present for phases 1 to 3.
Add for this run:

- Your inbox list, read, archive and reply each print only Gateway words (a client test with a Gateway payload the
  client has never seen renders it verbatim).
- The unread count on the Inbox entry equals the Gateway's count on both shells.
- A factory-inbox recording held past the sweep is shown on the thread in the Gateway's words, never dropped silently.
- After email off: a factory's morning run writes one Your inbox message and sends no email (asserted against the
  mail sender, not a log line).

### Step F - the end-to-end QA report (the goal's proof)

On the DEPLOYED Gateway, a test factory on a **test Director on devlinux** that is NOT the `devlinux` Director hosting
your own seat or the owner's sessions. Build it from main, give it its own instance folder and display name ("Factory
Inbox test"), connect it to the hosted Gateway, and stop it cleanly with its own shutdown signal when done; never stop
the `devlinux` Director or any Director on SOREN_NORTH or the Mac. (CLAUDE.md rule 0b describes the Windows form of
this - slot 5 and the scheduled task; on Linux the same rule holds: your own test Director, launched outside your own
terminal so its sessions have a clean terminal, stopped by you.) If a second Director cannot run on devlinux, say why
in `devlinux-readiness.md` and ask the owner one plain question before going further. Drive the phone (mobile web, a
phone-sized window) and the Cockpit with browser-harness against `https://gateway.devthrottle.com`. **A screenshot from
the phone AND from the Cockpit at every step, plus `cc-devthrottle session list` at every step.**

1. A typed message from the Cockpit to the test factory: thread shows Sent, then Received (one Secretary started,
   then closed), then With the Boss, then the decision.
2. A spoken Bad note from the phone: one Secretary, one Boss, the Boss delegates to a work session; the thread shows
   the full trail with the session id.
3. A second message while the Boss is alive: no second Boss; the decision arrives.
4. A request the Boss should refuse: Declined on the thread; one new Your inbox message, with the unread count on
   both shells; reply from Your inbox; the reply lands in the factory inbox.
5. A session runs `cc-devthrottle inbox send --subject ... --body ...`: it appears in Your inbox on phone and
   Cockpit; Open the session works; no email is sent.
6. The factory summary: the Summary button shows what came in and each decision; the morning message carries it.
7. Failure cases, each with screenshots: Director stopped (the thread says why; resumes on reconnect); Secretary
   killed mid-claim (the next one takes it); the stuck alert; an archived factory refused in words; a spoken message
   with no speech.
8. Measure and report the running cost of one Secretary plus one Boss for one bundle (tokens and minutes).

The QA report is markdown with the screenshots under `docs/qa/factory-inbox/phase-6-end-to-end/`, committed and
merged, AND a dev report to the owner (`cc-dev-reports open`) carrying the screenshots as images, with an evidence
section that says what was NOT proven.

## 8. Merge plan

Each pull request merges the same day on the gate above and a review, then deploy. Safe because Your inbox screens
are new screens behind the factory area switch, phase 3 adds tables and routes and changes no existing answer, and
email off is the only change to existing behaviour and lands last, after Your inbox is proven live.

## 9. Where it ends

Merged to main, deployed, QA report committed and published to the owner. The Delivery Lead lands the record (this
document, `MISSION.md`, the reviews, the QA reports) in the repository under `docs/factory-inbox/`, merged, then asks
the owner to shut it down.

## 10. Questions

None for the owner. Settle by reading, not guessing: whether the factory area switch is on for the owner's account.

## Operating lessons from the first run - read before you start

- **Deploy grant, this mission only:** "this needs to be fully deployed and tested". Deploy ONLY through the
  `deploy-hosted-gateway` skill. Only the Delivery Lead deploys; tell every Tech Lead and Developer so.
- **Another mission also deploys main** (6b605a2 at 11:03 on 6 November; very likely the unexplained 01:52 deploy).
  Before you blame a deploy on this mission, read which commit is live (`/healthz` "commit") and the deploy run list.
- **Watch every deploy yourself** for 15 minutes after the new commit is live: time `/healthz` every 15 seconds. A
  start-up stall of a few minutes (database writes timing out at 30 seconds) is what every deploy costs today. If a
  stall passes five minutes, roll back with `rollback-hosted-gateway.yml` to the last good commit. On 6 November a
  20-minute stall was cured by exactly that; its cause is not proven (Supabase's own logs would be the next evidence).
- **The hosted Gateway has no warm spare slot**: a deploy is an in-place re-pin and costs about 70 seconds of outage.
  Say the number when you report.
- **Every test Gateway starts with `CC_GATEWAY_NO_TAILSCALE=1`** (project instructions rule 0c). On 6 November a
  Developer's local boot on SOREN_NORTH rewrote the owner's tailnet front door (https 443 now proxies the dead
  `localhost:17881`). The owner: "We shouldn't really use tail scale for anything anymore." Leave it alone; use no
  Tailscale.
- **Main goes red from other missions.** Do not wait for its owner: open a Developer of this mission to fix it
  forward, reviewed and gated like any change.
- **The Gateway sometimes stops answering fleet commands for a minute.** A spawn or stop that times out may still
  have happened: list sessions by name before retrying.
- **Free memory is tight.** Stop every session the moment its work is merged. The owner asks for this.
- The owner does not read code or technical questions. Report in plain words: what he can now see and use, what is
  next, and when. Ask him only what needs his judgement, one question at a time, with a plain explanation.

## Where the finishing run ended (7 November 2026)

Driven from devlinux by the Delivery Lead (session 136, "Factory Inbox - Delivery Lead - finish on devlinux"). Every
pull request below was gated by the Delivery Lead in its own worktree (see `devlinux-readiness.md` for how the gate is
read on Linux) and reviewed by a separate session; every review file is under `reviews/`.

| Pull request | What | Deployed |
|---|---|---|
| 4610 | Closed unmerged: main fixed the same test in 4612. | - |
| 4608 | Your inbox back end (migrations regenerated after main's AddFactoryBossStarts). | 16:14 UTC, 6 November |
| 4603 | Factory inbox screens, spoken send opens the gather window from the press. | 16:14 UTC, 6 November |
| 4637 | Your inbox screens and the unread count on the Inbox entry. | 17:06 UTC, 6 November |
| 4630 | A held spoken message is never dropped by the 24-hour sweep. | 18:25 UTC, 6 November |
| 4652 | Archived factories left out on purpose; Leave a message only where one is taken. | 18:25 UTC, 6 November |
| 4667, 4691 | Two tests that failed under load: races in the tests' own set-up, fixed. | with later deploys |
| 4690 | The thread took 70 seconds (4630's held lookup walked every upload); now an indexed lookup, warmed at start-up. | 00:04 UTC, 7 November |
| 4664 | Email off: each factory's morning summary goes to Your inbox; the sweep has no mail sender. | 00:04 UTC, 7 November |
| 4703 | Small defects from the end-to-end proof, and reads that cannot hang a screen. | 00:49 UTC, 7 November |
| 4699 | The end-to-end QA report and this record. | - |

Issues filed for what is outside this mission: 4701 (two Directors on one computer), 4702 (session keys refused for an
owned session). Deploys cost between no failed answer and an 8-minute stall (17:06 UTC); the rule to roll back after
five minutes was missed once and is recorded in the owner's report.

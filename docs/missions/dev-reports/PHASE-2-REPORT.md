# Dev Reports - phase 2 report: the Gateway record and delivery

Issue #2958 (child of #2936). Branch `mission/dev-reports`. Written 2026-09-17 by the phase 2 finish Manager.

## What you get

An agent can publish an HTML report for its own session, and the Gateway keeps it: every version, your notes and
answers on it, and the agent's replies. You can list your reports, read one, and send notes and answers. What you
send goes into the agent's session as ONE prompt, and never while the agent is in the middle of a turn: if it is
working, your items are held and go the moment the turn ends. Each item shows you where it is - held, sending,
delivered, delivered but not confirmed, replaced by a later answer, or refused because the session has ended. The
agent replies with `cc-dev-reports reply`, and republishes with `cc-dev-reports open <file>`. Nobody but you can
read or answer your reports. Everything survives a Gateway restart and a deploy.

Nothing is on your screen yet. The Cockpit and phone viewer is phase 3; that is when you can try it.

## What is proven, and how

Every guard below was proven by breaking it on committed code, watching its test go red with the symptom, then
restoring it and watching it go green again with a fresh build.

**Your words reach the session at most once, even while two Gateways run during a deploy.** This was the last open
finding from the independent review. The Gateway now claims your items in the database before sending them: an item
moves from held to sending with a claim stamp only if it is still held at that instant, the prompt carries only what
that claim took, and the result is written back only if the item still carries that same claim. A Gateway that finds
an item stuck in sending (because the Gateway sending it died) rules it "Sent to the session, not confirmed" and never
sends it again - but only once the claim is older than five minutes, which is ten times the longest a send can wait.
Proven with two delivery services over one database, the only thing they share:
- One Gateway's send is still out when the other rules it orphaned; the late answer must not overwrite that ruling.
  Broken (the result written without checking the item is still sending): red, `Expected: 0 Actual: 1`.
- The other Gateway settles one second before the claim expires; it must leave the send alone. Broken (no claim
  age): red, `Expected: "sending"`.
- The other Gateway drains while a send is out and a new note has arrived; it must send only the new note. Broken
  (the claim ignores whether an item is still held): red, `Expected: 1 Actual: 2`.

**The list of your reports is never stale.** Opening the list now settles the sessions it shows before counting what
is still open, so a report whose session has ended no longer says it has items waiting. Proven on a real Gateway:
held item, session closes, list shows nothing open. Broken (settle removed from the list): red, `Expected: 0 Actual: 1`.

**The report's file name cannot forge the prompt.** It was the one agent-supplied string still written into the prompt
raw. It is now escaped like the title and labels, so it cannot break a line. Proven with a file name carrying a forged
answer. Broken: red on that test and three pinned prompts.

**Carried from the earlier rounds, all still green:** held while working and delivered once at the turn end; the same
item sent twice is one item and one prompt; a later answer replaces an earlier one still held; an ended session
refuses new items and anything it still held; held items survive a Gateway restart; an item a crash left sending is
never sent twice; a Director that reconnects already idle gets its held items from the 30-second settle timer; a
definite refusal from the Director is not recorded as delivered; another account gets "not found" on read and send;
a session key cannot call your routes, and your device key cannot publish; the 10 megabyte limit and the key and id
length limits; the prompt keeps your words byte for byte between markers they cannot close. Details, with each red
message, are in `WORKER-phase-2-gateway.md`.

**The database change.** The dev report migration was generated again, after the migrations `main` gained this week,
for both SQLite and PostgreSQL, and both report no pending model changes. The PostgreSQL one ran in the throwaway
database the gate builds.

**The gates, on the final commit (17c99745e), each in its own worktree:**
- `.\scripts\test-local.ps1`: exit 0, 2,046 tests in eight suites, every one Completed with all tests executed.
- `Gateway.UnitTests` in full, with its own throwaway PostgreSQL: 5,252 passed, 2 skipped by design, 0 failed.
- The dev report unit tests: 151 passed. The dev report hosted tests on a real Gateway: 17 of 17 (run at aa8bd36d7;
  the one later commit changes a unit test only). `tools/cc-dev-reports`: 25 passed.
- `Gateway.Tests` in full: **NOT COMPLETE.** 2,586 tests, run in pieces so each fits a foreground call. The first
  piece (every class from A to C, plus the test infrastructure guard) passed, 426 of 426. The second piece was stopped
  by the session host because the machine ran out of memory (2.3 of 63.8 gigabytes free); nothing failed in it, and
  it was not restarted. **2,160 of the 2,586 have not run on this commit.** Its PostgreSQL container was removed.
- `Core.Tests` was not run: the merge of `main` touched code it covers, and the handoff did not ask for it.

## The independent inspection, and what was fixed

The inspection (`INSPECTION-phase-2.md`) found no high findings, two medium and five low. The Architect's rulings are
in `HANDOFF-phase-2-fix-inspection.md`. Each fix below was broken on committed code, watched red, and restored.

**Fixed - a browser that goes away no longer strands your notes (Medium 1).** Closing the tab or locking the phone
while your notes were being sent used to cancel the send part-way: the notes sat at "Sending to the session" for five
minutes and were then labelled "Sent to the session, not confirmed", even when the prompt had never left. The send
now runs on the Gateway's own lifetime, never on the request, so it finishes whoever called it. And a claimed send
always records an outcome: if the Gateway is stopping before the send starts, the notes go back to held; if the send
throws once it has started, the prompt may have reached the session, so they are recorded as sent, not confirmed, at
once - never held again, because that would type them twice. Proven:
- The caller cancels mid-send; the notes must be delivered and none left sending. Broken (the send on the caller's
  token, as before): red, the cancellation escapes the send.
- The Gateway stops mid-send; the notes must read "Sent to the session, not confirmed". Broken (no finish when the
  send throws): red, `Values differ`.
- The Gateway stopped before the send; the notes must be held and nothing typed. Broken (no finish before the send):
  red, `Values differ`.

**Fixed - two Gateways publishing the same report at once (Low 1).** During a deploy both processes could read "no
report yet" and both insert; the loser's agent got a raw server error. A publish the database refuses as a duplicate
is now retried once, re-reading, and lands as a new version of the report the other Gateway wrote - the answer one
Gateway gives. Proven: the database's refusal handed in at the write; the publish must come back as version 2 of the
existing report. Broken (no retry): red, the refusal surfaces. **Not proven: the race itself.** SQLite takes its
write lock when the transaction begins, so the second writer waits and the refusal cannot happen on it; it is a
PostgreSQL case, and the refusal in the test stands in for the one PostgreSQL raises.
**Accepted:** two notes added at the same moment by two Gateways during a deploy can get the same sequence number;
their order then rests on the time each was sent.

**Fixed - the report's HTML is marked never to be guessed as a page (Low 4).** The HTML route now sends
`X-Content-Type-Options: nosniff`, so a browser cannot decide the plain text is a page. Proven on a real Gateway
over HTTP. Broken (header removed): red, `The given header was not found`.

**Accepted for version one:**
- **Medium 2** - the narrow gap in which a note can arrive while the agent is working (below, under what is not
  proven). The inspection adds that on your own send the idle reading is taken before your notes are stored.
- **Low 2** - report versions have no retention rule yet. Every version is kept, up to ten megabytes each, and nothing
  removes old ones.
- **Low 3** - the publish route reads up to 128 megabytes of request before it measures the 10 megabyte report. Only a
  session key or your own credentials can reach it, so no stranger can use it to press on the Gateway's memory.

**The gates on the fix commit (0463d7f3b):**
- `.\scripts	est-local.ps1`: exit 0, 2,046 tests in eight suites, every one Completed with all tests executed.
- The dev report unit tests: 155 passed (151 before, plus the four new ones).
- The dev report hosted tests on a real Gateway (`-Gateway -Filter DevReport`): 17 of 17, Completed.
- `Gateway.UnitTests` in full: 5,247 passed, 2 skipped, **9 failed, none in dev report code.** Eight are the hosted
  schema tests in `HostedSchemaRefusesAnUnownedRowTests`, which need a PostgreSQL server: run directly, not through
  the gate's throwaway database, they could not connect (`Failed to connect to 127.0.0.1:55432`). The ninth is
  `Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk` for Grok (`Expected: "no_transcript"
  Actual: "ok"`), which finds agent transcripts on the machine it runs on. This branch touches neither; whether they
  fail the same way on `main` on this machine was not checked.
- `Gateway.Tests` beyond the 17 and `Core.Tests` were not run, as the handoff directed.

## What is NOT proven

- **A note can still arrive while the agent is working, in one narrow gap.** The Gateway reads "idle" from what the
  Director last reported, then sends. If a turn starts in between - the agent resuming by itself, or you typing into
  the terminal - your items arrive mid-turn. Closing it needs the Director to refuse a prompt to a working session,
  which ships in a Director release. Accepted for this phase.
- **Two sends to one session can follow each other during a deploy.** The claim stops any item going twice, but two
  Gateways can each send different items to the same session seconds apart. Same gap as above, one level up.
- **The five-minute claim timeout is reasoned, not measured.** It rests on a send being bounded by the 30-second
  tunnel command timeout. If a send ever outlived five minutes, a second Gateway would label its items "not confirmed"
  while they were still going; they would still never be sent twice. A test fails if the timeout is set within four
  times the command timeout.
- **An item a crash left sending reads "Sending to the session" for up to five minutes** before it settles.
- **No real deploy swap, no real process kill, and no hosted database has run this.** Two processes are stood in for
  by two services over one database file; the PostgreSQL behaviour of the conditional updates is the database's own
  guarantee, not something run here under contention.
- **The owner-turn stamp on a real Director**, and the session's own list route over the wire, are read from code.
- **Nothing on screen.** No client renders any of this until phase 3.

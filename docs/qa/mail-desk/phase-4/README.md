# Mail Desk, phase 4: live mode, proven only on a throwaway Gateway

Date: 19 and 20 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), the phase 4 row
of section 6, design 5.6 to 5.8, and the live-mode cases of section 7.2. Tech Lead: session 128 on devlinux. The
contract every Developer built to is `CONTRACT.md` in this folder. It was written before any code and changed
sixteen times as findings and rulings came in; each change is its own commit, and each says who found the point and
who ruled.

**Live mode is built and proven only on the rig.** It stays OFF in production: a channel starts in `observe` (phase 1),
and nothing in this phase changes that default or switches any channel to `live`. The only way to `live` is still
`maildesk mode <address> live`, which phase 1 limits to a person or a session in no factory. Nothing in this phase
touched the hosted Gateway, a real mailbox, any real email, a desktop release, the website proxy, Azure or
Microsoft 365, and nothing sent an email.

## What was built

| Task | Pull request into the phase branch | What it is |
|---|---|---|
| A | 4274 | **Filing.** A live `archive` or `junk` decision is put in that channel's filing queue in the memory the Gateway keeps; the next run of the unchanged `screen.py` carries it out and reports it; the decision row says `filed`, `already-gone` or `failed`, with when. |
| B | 4275 | **Routing.** `MailDeskRouter` and `mail_desk_routed`: a live `route` or `mail-desk-agent` decision starts the seat in any factory of the account, on the machine that ran the check, through the trigger's own session-start path; one session per seat per run with all its emails in its prompt and in its kit's `check.json`; a busy seat queues; an orphan is re-routed once; a start that fails three times goes to the owner. |
| C | 4278 | **Done, return and the owner.** `maildesk routed`, `maildesk done`, `maildesk return`; only the session the email is with may answer; a first return goes to the Mail Desk agent with its note, a second goes to the owner as an Open problem and a line in the 08:00 email. |
| - | 4282 | A test that read the real clock (found by the gate's architecture test, which only the full gate runs). |
| - | 4284 | **The routed seat's opening.** A routed session opens exactly as a scheduled run of that seat does - who it is, its brief, its kit, its goal, from the same shared code - except one sentence: the Mail Desk started it. Found on the rig: without it, seats never read their brief. |
| - | 4283 | **When a routed session has ended.** Only on evidence; a Director restart waits on its restart record. Four review rounds, each finding a real way two sessions could work one email (below). |
| D | 4281 | **The rig** (`docs/qa/mail-desk/rig/`), and one small product change it needed: an optional Gateway setting, `mailDeskModelBaseUrl`, honoured ONLY for a loopback address, so the rig's Gateway can ask a fake model and never the real one. |

Every Developer's code was reviewed by a separate Reviewer session (Claude Code on Opus, handover ruling 9: Codex is
not installed on devlinux and Fable was out of its limit) before it merged into the phase branch. Every finding was
answered by the Developer who built the work, in `reviews/`. **Of 15 findings, 15 were accepted and fixed; none was
declined and none is unanswered.** The fifth round of pull request 4283 was not reviewed again: it only turned the
fourth Reviewer's own probe tests into tests, which failed on the old code and pass now, and the Tech Lead ran them.

## The proof, in plain words

### Nothing acts unless the decision was made in live mode

Every action reads the `mode` written on the decision row when it was decided. A channel switched to live later does
not file or route what was decided in observe mode; one switched back to observe stops queuing. Tests in
`MailDeskFilingTests` and `MailDeskRouterTests` decide the same emails in both modes and show that only the live ones
are queued or routed, and the fake session starter is never called for an observe decision.

### Filing moves only to Archive or Junk, and never twice

The Gateway never touches the mailbox. It writes the filing queue into the check's memory; `screen.py` (phase 2,
unchanged) carries it out through its request guard, which allows only `GET` and a move to `archive` or `junkemail`.
A move that comes back `already-gone` is recorded as that, never as `filed`. A decision is queued only when its key is
in the key table the check returned, so a key the check no longer knows can never be refused and lost; and a decision
whose email the check has read past without its key coming back ends as `failed`, "not seen again", so nothing waits
for ever.

### A routed email is never lost, and never worked by two sessions

This is where the review rounds went. The rule that came out of them (contract 3.2 step 3):

- A routed email stays `open` until its session says `done` or `return`.
- Its session counts as **ended** only on evidence: its row in the session list marked gone, or its history record
  ending `closed` or `finished`. A row still in the list decides alone - a late farewell never ends a running session.
- A session the Gateway cannot see at all is not ended by its absence (a Gateway restart, a Director away) until
  24 hours after its start.
- A **Director restart** does not end it. A Smart Restart records its seats as `closed`, so the router reads the
  restart record the drain left on the Gateway: a seat still owed a Bring back waits, and its seat stays busy; one
  brought back is followed to its new session id, through as many restarts as happened, up to 10; one not brought back
  is ended. If the record never resolves, 72 hours after the restart the email goes to the **owner** - never to a new
  session.
- An ended session's open emails are re-routed once to the same seat, then go to the owner.

Each point has a test that failed on the code before it. The probe tests the Reviewers wrote are among them.

### Only the right session can say done or return

`POST /routed/{id}/done` and `/return` accept only the session the email is with now. A person, another session, the
session it was with before a re-route, the machine token and a Director are each refused 403 `not_your_email`, and a
test compares every column of the row before and after each refusal: nothing changes.

### No email text goes anywhere

The prompt and `check.json` carry the sender, subject, receive time, mailbox, layer and reason - never the preview.
A test screens an email whose preview holds a marker and searches the session request, every row and the log sink for
it: none.

## The rig

`docs/qa/mail-desk/rig/run_qa.py`, run on devlinux, writes `docs/qa/mail-desk/rig/transcript.txt`, which is committed;
its own README says how to run it. A throwaway Gateway built from this tree, a slot 5 test Director built from this
tree with its own root and home, a fake Graph, a fake model on 127.0.0.1, and real Claude Code seats in two factories
besides `mail-desk`. The check is the real `screen.py`, byte for byte, inside a small wrapper that replaces only its
network call and its sign-in, and refuses any sign-in folder outside the rig.

RIG-RESULT-PLACEHOLDER

## The gate

Run by the Tech Lead on devlinux, Linux, Debug. Files in `gate/`.

- `scripts/test-local.ps1 -Parked` on the phase branch (8acb0871e) and on its unchanged main base (91fa80891). **The
  two runs fail exactly the same 107 tests, test for test, with none different in either direction** (the lists are
  `gate/failing-tests-phase-branch.txt` and `gate/failing-tests-main-base.txt`). The phase adds 173 Gateway unit
  tests (15,459 passing against 15,286). The 107 are this machine's: Windows-only behaviour in Launcher and Reclaim,
  Windows paths in Core, ten Gateway.Tests and three Gateway unit tests that fail on Linux on main. The script's own
  verdict reads NEVER RAN on Linux, because the installer suites build only for Windows.
- `scripts/test-database.ps1` (Docker, a throwaway PostgreSQL) on the phase branch: 84 of 85 passed, all six Mail Desk
  PostgreSQL tests among them, including the three migrations this phase adds. The one failure,
  `AddLicenseAlarmsPostgresTests`, fails the same way on main. On Linux the script needs a link named `powershell` to
  `pwsh`.
- **No Windows gate ran.** Nothing in this phase is Windows-specific, but that is not proven here.

## What is NOT proven

- **Live mode in production.** It is off there, and turning it on is the owner's decision (D17).
- **A real mailbox and real Microsoft Graph.** The rig's Graph is a fake. That Graph answers 404 to a second move of an
  id it already moved (phase 2's assumption) is still unseen on a real mailbox.
- **A seat reading a real email.** The rig gives its seats a `cc-outlook` that refuses everything, so a seat never
  reaches any mailbox. That a seat finds and reads its email with `cc-outlook` is phase 6's, on the real mailbox.
- **The 08:00 email being sent by the Gateway's own schedule.** The rig composes it on demand with the Gateway's own
  fold and composer over the rig's database; it never waits for 08:00 and sends nothing.
- **A released Director.** The rig's Director is built from this tree. Routed sessions use the trigger's own origin
  label, so a released Director should accept them (no Director change was made), but no released Director ran here.
- **A real Smart Restart.** The restart record rules are proven by tests over the real restart and workspace stores,
  not by restarting a Director.
- **A session that renamed itself** after a restart is not followed; its emails are re-routed once (stated in the
  contract as a known limit).
- **Windows and macOS.** No gate ran there.

## Rulings made during the phase

All are in `CONTRACT.md`, each its own commit:

1. The rig's model address is a loopback-only Gateway setting (the address was a constant).
2. A routed session uses the trigger's origin surface, not a new word: the Director refuses an unknown one, which would
   have needed a desktop release.
3. Where a routed email's resolve, owner and return times are kept (task C's own columns), and its earlier return.
4. A routed email's counts are per seat.
5. A session is ended only on evidence, a live row decides alone, a cleanly ended session is ended on its history.
6. A routed session opens as its seat's scheduled run does.
7. A Director restart waits on its restart record, chains are followed, and an unresolved restart goes to the owner
   after 72 hours.
8. A seat whose session has finished every email is free, even while it runs.

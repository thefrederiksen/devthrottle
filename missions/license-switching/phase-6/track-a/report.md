# Phase 6, Track A - readiness, the 80% alarm, the per-machine switch, the real reason

Track A report, 17 October 2026. Three pieces, each built by its own Developer, read by a separate review session,
checked by the Tech Lead's own gate run, and merged to main. Each piece has its own record beside the code; this page
says what the three add up to and how they were proved.

## What was built

| Piece | Pull request | Record |
|---|---|---|
| A1 - the license alarm: one mechanism for every track | 4110 | `alarm/README.md`, `alarm/review.md` |
| A2 - one definition of ready; every ready machine switches at 97%; the real reason in every sentence | 4105 | `switch-rule/README.md`, `switch-rule/review.md` |
| A3 - readiness on every minute, the automatic sign-in with its retry wait, the 80% alarm | 4151 | `readiness/README.md`, `readiness/review.md` |

**The alarm (A1).** A license problem is loud and stays until it is fixed. A check on the Gateway raises every alarm of
its kind that is true now; a new one opens with one owner email (the same channel as the no-room email), one phone push
and a row in the Licenses log; it stays open, sending nothing more, while it is true; it closes by itself when it is
not. Every Cockpit page and the phone's shell show the open alarms as a red banner, in the Gateway's own words. There is
no route that raises one. Tracks B and C raise theirs through the same service (contract.md section 9).

**The switch rule (A2).** On 16 October one machine without a spare stopped all four from switching, and the owner was
told "No other account has room" when an account at 0% existed. Now an account's room is judged on the account alone;
at 97% each machine goes to the best account with room that it is ready for; a machine ready for none stays behind,
blocks nobody, and switches the first minute it is ready. The no-room email is sent only when no account has room. Every
sentence the owner reads - the switch log, the Switch now sentence and its answers, the schedule - names the real
cause, for example "account-b@example.com has room; machine-d holds no sign-in for it".

**Readiness (A3).** Every minute, for each account in use, the Gateway works out the account it would switch to next
and whether each machine on it is ready for that account, and shows it on the Licenses page: "Ready for <account>" or
"Not ready: <the reason>; <what DevThrottle is doing about it>". A machine that is not ready is signed in at once,
through the existing Add-a-spare (or Renew) pipeline and the account's approver, with no person: one sign-in per machine
and account at a time, retried after 2, 5, 15 and 30 minutes and then hourly, every attempt a row in the Licenses log
marked "Automatic". When the account's approver cannot approve, the reason is Track C's own sentence and nothing is
started. When the account in use reaches 80% of either window and a machine is still not ready, the 80% alarm opens for
that machine, naming the machine, the account, the reason and the one thing to do; it closes when the machine becomes
ready or the account falls back under 80%.

Automation stays switched on for the owner's own tenant only (13 October ruling): automatic sign-ins and the 80% alarm
run only for a tenant whose machines report licenses, which only a Director with license switching switched on does.

## How it was proved

Each piece's README explains every test in plain terms; in short:

- **The alarm**: a new alarm sends exactly one email and one push; raised again it sends nothing; no longer raised it
  closes; raised after closing it is a new alarm with a new email; a failed email is retried on a later minute and sent
  once; two tenants never see each other's alarms; the banner's words for none, one and several alarms are pinned; the
  push carries no email address; the Postgres migration applies, works and rolls back on a real PostgreSQL.
- **The switch rule**: the 16 October picture reproduced with made-up names switches the three ready machines and not
  the fourth, with no email; a minute later, with the fourth holding the spare, it switches too; under 97% nothing moves;
  a true no-room case still sends the one email in its old words; the tie case sends each machine to the best account it
  is ready for; every sentence is pinned. The three review findings (the Switch now sentence and its answer naming the
  wrong account in the tie case, and an "it" pointing at the wrong account) were fixed with tests that fail without the
  fix.
- **Readiness**: every readiness sentence is pinned; a gap starts exactly one sign-in and none while it runs; each
  retry wait is checked one second before and at its end, with a new service each minute as after a restart; no approver
  and Director offline start nothing and say why; under 80% there is no alarm but the sign-in still starts; at exactly
  80% of either window one alarm opens and the next minute sends nothing more; ready closes it; falling under 80% closes
  it; a stale reading raises nothing; a tenant without automation gets neither a sign-in nor an alarm.
- **Layout checks** with screenshots: the banner on two Cockpit pages and the phone (one alarm and several), and the
  Licenses page with every readiness sentence and the 80% alarm's banner (`alarm/` and `readiness/`).

**The Tech Lead's own runs**, each in the piece's worktree at the commit that merged:
- A1 at ab2d4d2c9: default gate green (4,964 tests, every suite completed); the whole Gateway unit suite green (13,867).
  Cockpit 1,255 and phone 151 web tests green.
- A2 at 8f5e0b237: default gate green (4,964); the whole Gateway unit suite green (13,890); the parked suites filtered to
  License green (Gateway host tests 21, Gateway unit 886, Core 184); Cockpit web tests 1,255 green.
- A3: see the pull request.

The unfiltered parked run on main after every track has merged is the Delivery Lead's (ruling of 17 October).

## Failure cases a person should know about

- A machine whose Director is not connected is looked at every minute and signed in on the first minute it is back;
  until then the sentence and the alarm say "Start the Director on <machine>."
- A sign-in that keeps failing is retried hourly for as long as the gap lasts, and from 80% the alarm stays open; the
  banner says what the last attempt said.
- If no account has room, that is its own state ("No other account has room for it to switch to"), not a readiness gap,
  and the no-room email covers it.

## Found on the way, not Track A's

- The Cockpit web test `fixtures.test.ts` went red on main with Track C's 4127 (passed to the Delivery Lead).
- The client-core test `VerdictPanel.test.tsx` "reports the superseded verdict wrong" failed once in a full run and
  passes alone (21 of 21); five lint errors on main are in files no Track A piece touches.
- Main was red on six prompt-delivery and Fleet Manager Gateway unit tests at v2.23.0 until 4107 fixed them.
- `cc-worktrees release` can leave a slot that no command can free (issue 4119); one slot is held that way.

## What the live proof should look for

On the owner's machines, once deployed:
- The Licenses page shows a readiness line on every machine, "Ready for <account>" on each machine holding the next
  account's sign-in.
- **A forced gap is fixed with no person**: take the next account's spare away from one machine (moving it to a
  backup, the Delivery Lead's step); within a minute the line reads "Not ready: holds no sign-in for <account>;
  DevThrottle is starting a sign-in ...", the Licenses log has a row starting "Started automatically:", the approver's
  browser approves it, and the line returns to "Ready for <account>". No alarm while the account in use is under 80%.
- **An unfixable gap raises the 80% alarm**: with an account in use at or above 80% and a machine that cannot be signed
  in (for example its account's approver unset), one red banner on every Cockpit page and on the phone, one push, one
  owner email, naming the machine and the account and saying what to do; nothing more each minute; it closes by itself
  when the gap is fixed.
- At 97%, every ready machine switches in the same minute, and a machine left behind switches the minute it is ready;
  no "No other account has room" email while an account has room.

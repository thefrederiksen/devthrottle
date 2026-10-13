# Review 1 - Factory Seats, phase 1: the seat page

Reviewer seat: Reviewer on the mission "Factory Seats" (run of the mission workflow), opened by the
Implementation Lead. Read on 13 October 2026, on this machine, in a worktree cut from
`origin/factory-seats/phase-1-seat-page` (seven commits on top of origin/main at `4f844472a`).

**Verdict: nothing found that must change.** Every harm the brief asked me to look hardest at was traced to
its code and its test, and none of them breaks. Two notes follow that are not findings - neither proves a
harm, so neither demands a change - and the scope says plainly what this review did and did not reach.

## Scope

**What I read.** The whole diff of the seven commits against the merge base, except the picture files. In
full: `FactorySeatPageFold.cs`, the seat page route in `FactoriesScreenEndpoints.MapSeatPage`, the unlink
route in `CronJobEndpoints.cs`, `CronJobStore.Unlink`, the `SessionKeyGuard` schedule-route entry,
`FactoryNaming.cs` (the whole gate), the `FactoriesScreenFold` status changes (TALK ONLY and the ranks),
the `FactoryAgentsFold` removal of the old agent page fold, the `FactoryTalkSeed` word list, the
Cockpit's `FactorySeatView.tsx`, `FactoryRedirects.tsx`, `routes.tsx`, the Seats tab in `FactoryView.tsx`,
`factoriesCards.ts`, `factoriesSort.ts`, the client-core client changes, the command line's
`schedule_ops.py` and `cli.py` changes, `docs/cli-reference.md`, the developer's proof
(`docs/qa/factory-seats/phase-1/README.md`, `capture.py`'s header), and the new test files
(`FactorySeatPageFoldTests`, `FactorySeatRoutesTests`, the changed `FactoriesScreenFoldTests`,
`test_schedule_factory_seat.py`, `FactorySeatView.test.tsx`, `FactoriesScreen.test.tsx`,
`FactoriesCards.test.tsx`, the client-core client tests).

**What I searched beyond the diff.** Every remaining reference to the removed agent page (the Gateway route,
the client call, the Cockpit view, the session chip, the map, the command line, the mobile app, the morning
report); and every consumer of the four status words (the cards, the table sort, the factory email
composer, `cc-devthrottle factory status`, the Factory Manager card, the factory talk seed, the Wingman
service).

**What I ran.** Nothing but git: `git fetch origin`, the worktree, the log, the diff, and greps over both
the branch and origin/main. No test was executed, because no finding needed one to settle it; the developer's
own run is recorded in the proof and I did not re-run it.

**What I could not reach.** A running Gateway with a real session key, so the session refusal is read from
the route test and the guard, not proven end to end - the proof admits the same gap. The hosted PostgreSQL
path and tenants other than the local one. The visual content of the screenshots (the pictures are committed
but this seat could not view images). The email composer was verified only by search: it reads no status
word, so the fifth word cannot reach it.

## The harm areas, one by one

**Taking a schedule off a seat - who may.** The route calls `FactoryNaming.TryAct` with the schedule's own
factory and the word "schedule", before anything is changed, exactly as the update and delete routes do:
a person's device passes, the account's shared machine token passes, a session key reaches the route through
`SessionKeyGuard` and is then limited to a session of that factory, a session the Gateway does not know
yet gets the try-again answer, and anyone else is refused with the gate's own sentence. The route test
proves each case over real HTTP with the store asserted unchanged afterwards, and the proof includes a
revert run: with the gate taken out, exactly the three refusal tests go red. The command line prints the
Gateway's refusal sentence and exits 1; the Cockpit confirm stays open with the sentence in it. Nothing
changes on refusal.

**Taking a schedule off - what happens to the schedule.** Only its factory and seat are cleared; the switch
state and the next run are untouched, so a schedule that is running at that moment is not disturbed, and a
window schedule that runs after another is not re-placed (nothing is deleted, so no follower's `after`
names a missing schedule - the delete route's orphan warning does not apply). The confirm, the route's
answer and the command line all say in words that a switched-on schedule keeps running as a plain schedule,
and the command line offers `schedule disable` first. A schedule in no factory is refused (409) rather
than answered as done, and the route refuses a schedule an archived factory's archive switched off, because
Restore would switch a schedule back on that is no longer the factory's. The seat's schedule list needs no
separate update: the registry derives it from the schedules that point at the seat, and the route test
proves the seat's list drops the schedule and the factory then reads TALK ONLY.

**TALK ONLY - can it hide a problem.** No. The word is decided last in `FactoriesScreenFold.Status`, only
after the FAILING checks (open failures, and schedules whose firing did not start a run) and the NEEDS YOU
check, and only when `PausedText` returns nothing - so a factory with a failure, with a question waiting,
with a switched-off schedule or with a trigger paused stays FAILING, NEEDS YOU or PAUSED, and only a
factory nothing was ever set to run reads TALK ONLY. Rank 4 sorts it last everywhere the rank is used.

**The fifth word against the surfaces that count or sort.** The cards got a fourth group and a new count
(`groupKeyOf` on the rank, so it can never fall into "paused"); the sort footers and the strip count were
re-worded; the command line's `factory status` prints the Gateway's word verbatim and decides nothing; the
Factory Manager card counts no status words; the factory email composer reads no status words; the boss's
talk seed now names all five words. I found no remaining `switch` or count in any client or server code
that assumes exactly four.

**The removed agent page.** Nothing still links to or calls it: the Gateway route, the fold, the contract,
the client call and the Cockpit view are all gone, and greps over the whole repository (including the
mobile app and the morning report) find no caller of the old Gateway address. The Gateway's own links for
a factory agent (a session's chip, the map's pins) now point at the seat page, and both old Cockpit
addresses - `/factory-agents/<factory>/<agent>` and `/factories/<factory>/agents/<agent>` - redirect to the
seat page with the query kept, so a link in an already-sent email or report still lands somewhere true. An
agent id that is not a registered seat now gets the Gateway's 404 sentence naming the factory's seats and
pointing at the Activity tab; that case is in the proof and the mission document rules the page is for
seats, so this is by design and the sentence is honest.

**Tenancy on the new seat route.** The seat page is the owner's (`Owner`, refusing a session key with a
sentence) and everything it reads is resolved through the caller's tenant: the registry find, the activity
query, the schedules list and the memory count. Another account's factory id is answered with the same
"not registered" 404 the Seats tab gives, so one account cannot open another account's seat and cannot
even learn that the factory exists. The unlink route follows the existing update and delete routes
exactly: the store query runs in the caller's ambient tenant context with the database's tenant filter, so
another account's schedule id is a 404.

**A seat with no schedule, a seat whose schedule id is gone, a seat in two shifts.** A seat with no
schedule opens with every panel and reads Talk only (route test, fold test, screenshot). A seat whose
schedule id the Gateway no longer has says so, offers no button, and reads "Schedule missing" - and the
proof is honest that this state cannot be produced on a real Gateway any more (the seat's list is derived
from the schedules themselves), with the one screenshot labelled as not the Gateway's own data. A seat in
two shifts appears in both groups of the Seats tab, each row opening the same seat page, which lists every
schedule with its own shift text; the fold test covers a seat with two schedules, one paused, and one
missing alongside one running.

## Notes (not findings - no harm proven, nothing here must change)

1. **An unrelated line in a test helper changed.** In `FactoriesScreenFoldTests.cs`, the helper
   `Fires` sets `RepoPath` to a verbatim string that contained a stray carriage return on origin/main
   (put there by an earlier mission); on this branch that character became a line feed. It is outside this
   mission's scope, but it is harmless by construction: a verbatim string allows the newline,
   `CronSchedule.Validate` only requires the path to be non-blank, and no assertion reads it. The seat that
   built the work may want to revert it for cleanliness; nothing depends on it either way.

2. **Taking a schedule off a seat writes no row in the factory's activity record.** A week later the seat's
   page says "Talk only" and the record says nothing about when or by whom the schedule was taken off.
   This is consistent with pausing and editing a schedule, which also record nothing in the factory's
   activity record, and the mission document does not ask for a row; the gateway log does record the act.
   Recording it would be a conscious addition, not a fix, so it is named here for the Implementation Lead
   to decide or decline, not as a defect.

## On the developer's proof

I did not trust it, and read the code instead; the proof held up. Its test tables match the files as they
are, its refusals are quoted from the answer bodies the tests assert, its counts state their bases, and its
"Not proven here" section names real gaps (the session refusal is proven by the route test, not against a
running Gateway; the missing-schedule screenshot is not the Gateway's own data) rather than papering over
them. The one red it reports in the Cockpit suite (`src/licenses/fixtures.test.ts`) is in files this branch
does not touch.

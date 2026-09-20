# Phase 1, task 1 - raised sessions in the Gateway - the Developer's answers to the review

20 September 2026. From the Developer seat, to the Delivery Lead and the Tech Lead. Pull request
3198, branch `fleet-manager-improvement/p1-gateway`, issue #3177. This answers the review at
`reviews/phase-1-task-1-review.md` on the record branch, one paragraph per finding, each accepted or
declined with the reason.

**The standing decision behind most of the declines.** The Delivery Lead has decided that findings 3,
4, 6, 7, 8 and 9, and the reviewer's terminology question, are FOLLOW-UPS: the owner is filing them
as work items, and they are not to be fixed on this branch. Where a paragraph below says "declined
here", that is what it means - the finding is accepted as true and correct, and it is being carried
as separate work, not dismissed.

## 1. The shipped `fleet-manager` skill still says the page and the walkthrough refuse a session key

**Accepted, and fixed on this branch.** Both rows said something this change makes false. They now
say what the placement row above them already said. The page row reads "There is no command for the
page; the Gateway refuses that route to every session key that is not raised, and a raised Fleet
Manager (see "Raised" below) reading it has the read recorded against it." The walkthrough row says
the same for reading it, and then says plainly that answering, snoozing and closing a record from the
walkthrough stay the owner's own phone or browser's, raised or not, because they store that THE OWNER
did it - the reviewer's "confirmed right" decision, now stated in the table and not only in the
"Raised" section further down. I edited the one source,
`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md`, and nothing else: there is no
`.claude/skills/fleet-manager/SKILL.md` in this repository, so there was no copy to regenerate, which
is the best state the one-source rule allows. Both guards were run and are green -
`BuiltInSkillsHaveOneSourceTests` and `ShippedSkillsTeachOwnershipTests`, together with
`RetiredMessagingWordsTests`, eleven tests, all passed. The words reach the fleet when the Gateway is
deployed, which is the Delivery Lead's step.

## 2. The mission's check has never been green on the final commit

**Not mine to answer, and not paid here.** This is the Delivery Lead's, and it is being carried as a
work item. What I can record is exactly what has now run on the head of this branch with the skill fix
in it, so that whoever pays the gate knows what is already covered and what is not: the whole Gateway
unit suite, 6,626 passed and 0 failed with 8 skipped; the raised session host tests through a really
booted Gateway, 19 of 19 passed; and the three skill and messaging guards in the Core unit suite, 11
passed. That is not `.\scripts\test-local.ps1 -Parked`, and I am not claiming it is. The roughly 2,550
remaining Gateway host tests and the whole Core suite still have not run against the five main commits
this branch was rebased onto, exactly as the review says. The reviewer's recommendation - one full
`-Parked` run on the commit about to be merged - stands unanswered by me.

## 3. The record of a raised message is written after the message is queued

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** The ordering is
what the review says it is, I flagged it myself in the proof, and the reviewer judged it acceptable
for this merge with the gap stated. Fixing it properly means recording before queueing and then
compensating when the queue refuses, which is a real change to two code paths and their tests, not a
line. It is not to be done on this branch.

## 4. Two of the seven granted owner routes are proven only at the guard, not through the middleware

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** `PUT
/gateway/fleet-manager/placement` and `POST /gateway/fleet-manager/start` are driven through the real
pipeline by no test; both are reachable by a raised key today, and the reviewer verified by reading
that neither carries a second wall of its own. Adding both to `OwnerOnlyFleetManagerRoutes` in
`RaisedSessionHostTests` is two rows and I would rather have done it, but it is a change to the
branch's tests and the decision is that this branch changes only the skill.

## 5. The Developer's mandate file is not in the repository

**Accepted as true; it is not mine to supply, and I cannot.** The reviewer is right that
`docs/missions/fleet-manager-improvement-2026-09-19/mandate-phase-1-developer-gateway.md` exists
nowhere - not on this branch, not on origin/main, not untracked here. The reason is the way mandates
are handed to a seat on this mission: as an untracked file dropped in the worktree root, which is how
my own arrived (`FINISH-MANDATE.md`, still untracked, never committed). So the earlier Developer's
mandate was never in the repository to be read, and I never received a copy of it either - I cannot
write down a list I have not seen without inventing it. The remedy is the Tech Lead's or the Delivery
Lead's: commit the mandate that was issued, or record that it was never written. Worth deciding once
for the whole mission, because every later phase will hand its Developer a mandate the same way and
leave the same hole in the record.

## 6. The command line's help text now says something false

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** The help text in
`tools/cc-devthrottle/src/cli.py` and `session_ops.py` still tells every agent that typing into a
session is refused, which since this change is false for a raised session. I recorded it in the proof
as outside this Gateway task and the reviewer agrees it should not ride along quietly. It needs a
Python change and a Python test run, neither of which this branch has, and it is being filed as work.

## 7. Two mark writers are not driven by any test of the raised effect

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.**
`FleetManagerPromotionStore.Promote` and the Gateway's own clearing of the mark reach raised only
through the backstop rule, which is the load-bearing construction and is unit tested, and which fails
CLOSED - a writer that forgets leaves a session not raised. One host test would pin the effect end to
end. It is a new host test, not a wording change, so it is not for this branch.

## 8. The desktop display push carries no raise stamp

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** I recorded this
gap in the proof and the reviewer confirmed it in the fold: only the roster routes are stamped, and a
Director's echo can never carry a raise stamp because `PushedSessionStore` clears it, which is unit
tested. The reviewer's own reading is that this is not a gap for phase 2, because task 2 draws the
raise and lower control from the roster. His suggestion - that the phase 2 mandate say in one line
that the control reads the roster row only - is a mandate the Delivery Lead writes, not a change to
this branch.

## 9. Minor: re-marking the same session from the owner's device writes a second raise record

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** When the owner's
device sets the mark on a session that is already marked and already raised,
`FleetManagerPlacementService.SetMarkByOwnerAsync` writes a "session raised" record before the store
is consulted and then nothing changes. The record is honest about who acted and when, no list entry is
duplicated, and the reviewer raised it so that whoever reads the trail knows a repeat row there is an
idempotent re-mark rather than a second grant. This answer is that note, in the record, for the same
reader.

## The reviewer's terminology question - "raise" now names two acts

**Accepted as true; declined here as a follow-up by the Delivery Lead's decision.** The word now names
the owner raising a session's permissions and a session raising its hand to the session that started
it (`cc-devthrottle session raise`, the `needs-manager` route). Nothing collides today: the command
line's `session raise` is the hand, and the new raise and lower routes have no command line verb at
all. I asked for a line in the `terminology` skill in the proof and the reviewer agrees it is worth
doing before phase 2 puts the word on the phone. It is a shipped built-in skill, so it is the same
one-source rule as finding 1 and the same deploy: the edit goes in
`src/CcDirector.Gateway/Skills/Content/terminology.skill.md`.

## One thing the review did not find, recorded here rather than fixed

The same false sentence finding 1 names survives in three code comments this change left behind:
`src/CcDirector.Gateway/GatewayHost.cs` says "SessionKeyGuard refuses a session key on every one of
them" over the placement service, "SessionKeyGuard refuses a session key" over the Fleet Manager page
routes, and the same over the walkthrough routes - all three are now true only of a session key that
is not raised, because this change is what made them otherwise. They are comments and not shipped
text, so no agent and no user is handed them, which is why they are not in the same class of harm as
finding 1, and why by the Delivery Lead's own reasoning they belong with the follow-ups rather than
on this branch. Recording them here so they are not lost: each is one clause, in the same words the
guard's own class comment already uses, and they should go with whichever follow-up touches that file
next.

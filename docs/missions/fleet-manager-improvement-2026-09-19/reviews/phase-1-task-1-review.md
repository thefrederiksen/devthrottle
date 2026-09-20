# Phase 1, task 1 - raised sessions in the Gateway - the review

20 September 2026. From the Reviewer seat, to the Tech Lead. Pull request 3198, branch
`fleet-manager-improvement/p1-gateway`, head commit `bc47d00e4`, issue #3177.

## Scope

What I read, in full: `MISSION.md` (sections 4 and 5 in particular), the Developer's proof
(`proofs/phase-1-task-1-developer.md`), the repository's project instructions (their critical rules 2, 3, 4, 5, 7 and the built-in skill rule),
`docs/CodingStyle.md`, `SessionKeyGuard.cs`, `AuthMiddleware.cs`, `RaisedSessionEndpoints.cs`,
`FleetManagerOwnerDevice.cs`, `RaisedSessions.cs`, `RaisedSessionRecord.cs`, `RaisedSessionRosterFold.cs`,
`FleetMessagePolicy.cs` and the message route changes in `GatewayEndpoints.cs`, the placement service and
its endpoints, the page and walkthrough endpoints, the store changes in `GatewayHost.cs`, `DirectorHub.cs`,
`PushedSessionStore.cs`, `GovernanceAuditLog.cs`, `SessionDto.cs`, `SessionRaiseDto`, the governance event
types, both migrations and the `RaisedSessionEntity`, `RaisedSessionHostTests.cs`,
`SessionKeyGuardRaisedTests.cs`, `RaisedSessionStoreTests.cs`, `FleetManagerPlacementRaisedTests.cs`
(names and subjects), `FleetMessagePolicyTests.cs` (names and subjects), `RaisedSessionRosterFoldTests.cs`,
the shipped `fleet-manager` skill, the `fleet-manager` workflow conduct, the shipped and repository
`fleet-comms` skill, and `RetiredMessagingWordsTests.cs`. I read the whole diff against origin/main
file by file.

What I ran, on this worktree at `bc47d00e4`: the raised session host tests through the real booted Gateway
(19 of 19 green), the raised guard, store, placement, roster fold and message policy unit tests (148 green),
and the retired-words sweep (one red, caused by the untracked mandate file in this worktree's root, not by
the branch). On a throwaway worktree cut from origin/main, since deleted: the two launcher capability
tests (red on main) and the tunnel and voice sweep tests (flaky on main - a different one or two red each
run, the same pattern the Developer reported on the branch).

What I did NOT read or run: the two migration designer files and the model snapshots (generated, checked
only for existence and for what the chain tests say about them); the PostgreSQL proofs (I read that they
were updated and ran green in the Developer's run; I did not start Docker); the roughly 2,550 gateway host
tests and the Core suite that were not re-run after the rebase (see finding 2); no web test, no Python
test, nothing deployed. I did not touch the Developer's worktree.

**The verdict: the design is right and the build is honest. The guard widening is a named widening, not a
blanket allow - I read every route it can reach and none is outside the mission's grant. Nothing I found
blocks the merge on correctness. Two things should happen before or at the merge: one sentence fix in a
shipped skill, and the mission's own check run once on the final commit.**

## This must change before merge

### 1. The shipped `fleet-manager` skill still says the page and the walkthrough refuse a session key

`src/CcDirector.Gateway/Skills/Content/fleet-manager.skill.md`, lines 26 and 27. The row for the owner's
page ends "There is no command for the page; it refuses a session key." and the row for the walkthrough the
same. This change makes both sentences false: a raised session key may read the page and the walkthrough -
that is two of the routes the grant opens, and the skill's own new "Raised" section says so ("read the
owner's Fleet Manager page and walkthrough"). The change updated the placement row above them ("refuses
those routes to every session key that is not raised") and left these two rows contradicting it. A raised
Fleet Manager reading the skill meets a "you may" in one section and a "you may not" in the table. Why it
matters: shipped skills are what every session on every Director is handed, and the mandate asks exactly
this - that the words not now say something the change makes false. The fix is one clause in each row, in
the same words the placement row already uses. How sure I am: certain - the contradiction is on the face of
the file, and the guard's grant (`IsFleetManagerOwnerRoute`: `page` and `walkthrough` on GET) proves it.
Remember the built-in skill rule: edit the one source under `Skills/Content`, regenerate the repository
copy if one exists for it, and the text reaches the fleet only when the Gateway is deployed.

### 2. The mission's check has never been green on the final commit

Not a code defect - a gate that has not been paid. The proof's one full `-Parked` run was made on the first
commit, BEFORE the rebase onto main; after the rebase, only the default gate, the whole gateway unit suite,
the touched host test classes and one filtered parked run were repeated. The roughly 2,550 remaining
gateway host tests and the whole Core suite have never run against the five main commits the branch was
rebased onto - and those main commits touch `GatewayEndpoints.cs` and `GatewayHost.cs`, the same two files
this change edits most. A clean rebase is not a test run. MISSION.md section 7 says every phase owes
`.\scripts\test-local.ps1 -Parked` from the phase's own worktree; no green run of it exists for the commit
that would be merged. Why it matters: the mission's check is the owner's ground for "done", and the merge
plan's safety rests on each phase's gate having actually run. How sure I am: certain about the facts (they
are in the proof, and the proof is honest about them); the risk itself is a probability, not a known red.

Two things the proof left open, I have now settled myself, on a throwaway worktree cut from origin/main
(since deleted):

- The two `LauncherDeclaredCapabilitiesTests` failures are main's: red on origin/main on this machine.
- The tunnel and voice sweep failures are main's too: I ran the same filter on origin/main and got the same
  flakiness there - a different one or two red on each run, exactly the pattern the proof described. The
  Developer's conclusion was right, though the proof itself said the evidence was only circumstantial.

So nothing red that was reported is this change's. What remains unpaid is the run itself. My
recommendation: one full `-Parked` run on the commit about to be merged, before merging. If the fleet
cannot afford the hour, then at minimum the whole `CcDirector.Gateway.Tests` suite, since the rebase landed
on changes to the two files this task edits.

## This could be better

### 3. The record of a raised message is written after the message is queued

`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, `RecordRaisedMessage`: it is called after
`fleetMessages.Send` has queued the message, so a failure between the queue and the record leaves a queued
message with no record of the raised grant that let it through. The middleware path is deliberately the
other way round - the row is written before the route runs, and a row that cannot be written stops the
request. The Developer flagged this in the proof's "what the tests do NOT cover". Why it matters: the owner
allowed raised sessions on the ground that every raised action is recorded, and this is the one path where
the record can be lost by a crash rather than by a refusal. It is a narrow in-process window, the normal
path is covered by tests that read the rows back by query, and fixing it means recording before queueing
and then compensating if the queue refuses - not a one-line change. I would accept it for this merge with
the gap stated, and fix the ordering in a follow-up if the owner ever reads the trail as the whole truth of
what a raised session did. How sure I am: certain about the ordering, moderate that it is worth the
follow-up rather than the merge.

### 4. Two of the seven granted owner routes are proven only at the guard, not through the middleware

`RaisedSessionHostTests.cs` drives five of the seven Fleet Manager owner routes through the real pipeline
with a raised key: placement read, page, walkthrough, restart, move. `PUT /gateway/fleet-manager/placement`
and `POST /gateway/fleet-manager/start` are covered only by the guard unit test
(`SessionKeyGuardRaisedTests`). The repository's known trap cuts the other way here too: if a future change
puts a second wall on either of those routes (as `restart` and `move` already have in
`SessionCaller`), the guard-level test stays green while the raised key is refused 403 in production. Adding
both routes to `OwnerOnlyFleetManagerRoutes` in the host test costs two rows in one list. How sure I am:
certain about the coverage gap; it is an improvement, not a defect - the routes are reachable by a raised
key today, and I verified by reading that neither has a second wall.

### 5. The Developer's mandate file is not in the repository

`docs/missions/fleet-manager-improvement-2026-09-19/mandate-phase-1-developer-gateway.md` does not exist -
not on this branch, not on origin/main, not untracked in either worktree. My mandate told me to read it,
including the list of tests the task owes; I could not, so I reviewed the tests against MISSION.md section 5
and against the Developer's own claims in its proof. Nothing in the proof reads as if a requirement was
quietly dropped, but I cannot certify against a list I never saw. The Tech Lead should supply it (or say it
was never written), so the record of this phase is complete. How sure I am: certain about the absence; I
searched the repository, origin/main and the Developer's worktree.

### 6. The command line's help text now says something false

`tools/cc-devthrottle/src/cli.py` and `session_ops.py` still say typing into a session is refused to every
agent. Since this change, that is false for a raised session. The Developer recorded the gap as outside this
Gateway task and no Python test ran. It should not ride along quietly: an agent reading the help text of the
command it was just granted will be told the command does not work. I recommend a follow-up issue, owned by
task 2 or the Delivery Lead, to reword that help before the phase reaches the owner. How sure I am: certain
about the text; the scoping call is the Tech Lead's.

### 7. Two mark writers are not driven by any test of the raised effect

The proof says the mark is written in six places and two are proven end to end. `FleetManagerPromotionStore.Promote`
and the Gateway's own clearing of the mark change raised only through the backstop rule (a mark entry
counts only while its session IS the mark) - which is unit tested, and is the right construction: a writer
that forgets leaves a session NOT raised, the safe side. But no test drives `Promote` and shows raised
following the mark, so a future edit that bypassed the backstop (writing the mark some way the store never
sees) would not turn anything red until the Fleet Manager noticed it had lost its permissions in
production. One host test - start a raised Fleet Manager, let the replacement promote - would pin it. How
sure I am: certain about the absence; moderate about the worth - the backstop is the load-bearing rule and
it is covered.

### 8. The desktop display push carries no raise stamp

The proof says so and I confirmed it in the fold: only the roster routes are stamped, and a Director's echo
of a session can never carry a raise stamp (cleared in `PushedSessionStore`, unit tested). Task 2 draws the
raise and lower control from the roster, so this is not a gap for task 2. It becomes one the day any surface
reads the raise state from the display push instead of the roster - worth a line in the phase 2 mandate that
the control reads the roster row only.

### 9. Minor: re-marking the same session from the owner's device writes a second raise record

`FleetManagerPlacementService.SetMarkByOwnerAsync`: when the owner's device sets the mark on a session
already marked and already raised, `RaisedFollowsMark` still writes a "session raised" record before
consulting the store, which then changes nothing. The record says the owner raised a session that was
already raised. It is honest about who acted and when, no list entry is duplicated, and the host tests never
walk this path twice - I note it only so whoever reads the trail knows a repeat row there is an idempotent
re-mark, not a second grant.

## The eight checks, each answered

**1. The guard is still an allow list.** It is. The widening is two literal lists, each with its own grant
name recorded on the verdict: `IsAgentInput` (prompt, interrupt, escape, the fan-out, answering a judged
stop) and `IsFleetManagerOwnerRoute` (placement read and save, start, restart, move, page read, walkthrough
read - each a literal at exact length, verb by verb). I read every route the widening can reach; the full
set is those five agent-input shapes and those seven Fleet Manager shapes, and nothing else. The raised
lookup happens only when the guard has already refused and being raised would change the answer
(`AuthMiddleware.AuthenticateSession`), so no route is opened by the lookup itself, and a route the product
grows next year is refused to a raised key until somebody adds it here. The default-deny refusal, the
case-insensitive segment matching and the exact-length matching are all preserved. Two of the seven owner
routes - placement save, and start - are not named in my mandate's shorthand list ("placement read, page,
walkthrough read, restart, move"); I judge them inside "the owner-only Fleet Manager routes" the mission
grants, and strictly weaker than routes the grant does name: a raised key that may `move` the Fleet Manager
(save plus restart or start) can already do everything `PUT placement` and `POST start` do. The mission's
own words are generic and the Developer's reading is the consistent one. I flag it so the Tech Lead has seen
the full set and confirms it. The walkthrough's three writes are NOT in the widening - they stay refused,
and the guard names that decision. No abbreviation of "whatever the owner may do" anywhere - the class
comment says two, by name, and the code agrees.

**2. The refusals hold.** The admission surface (devices, account devices, sign-out, account email, trial,
credits), the Gateway shutdown, the walkthrough's three writes and the caller's own audit trail are refused
to a raised key byte for byte as to an unraised one - proven in the host test, and I checked that every path
in that refusal list is a route the Gateway really maps, because a session key is refused by the guard on
any path, real or invented, so a wrong path in the test would still pass. Raising and lowering any session
(including itself) is refused to every session key, raised or not - the guard lists neither route, the
route's own `FleetManagerOwnerDevice.Require` refuses a Director's key and the shared token, and both are
proven. A Director's key cannot raise by setting the mark either: only "phone" and "browser" device types
count as the owner's own device (`SessionOriginSurfaces.FromDeviceType`), so a Director marking raises
nobody. Tenant binding is not loosened anywhere: the raised lookup is asked about the session key's own
tenant, the store is tenant-partitioned by construction, and a session id raised in one account gives
another account's key nothing - proven both at the route (a foreign session answers as an unknown one) and
at the list (a raised id in account B does not raise account A's key). The judged-stop answer route keeps
its own second wall (the shadow rule while an account's colours are off) for raised keys too, which the
Developer listed as its own decision and which I judge right: it is the route's standing rule, and the
mission does not ask to lift it.

**3. The tests drive the real middleware.** They do. `RaisedSessionHostTests` boots a hosted Gateway, mints
real session keys, and sends every request over HTTP through `AuthMiddleware`. The pass conditions are
specific presences: the raised key's status code and named code equal the owner's own device's for the same
request (the owner's answer is the control, and it comes from the route, never the guard), refusals are the
403 with `session_key_out_of_scope`, and the stored rows are read back through the owner's query route.
Every grant is asked of an unraised key in the same test, so a row proves the widening and not merely that
the route is open. The negative list, as above, is made of real routes. Each message the waiver let through
is counted in the record. The unraised controls are refused with today's sentence. The mandate's required
test list I could not check against - the Developer's mandate file is missing (finding 5) - but against the
mission's own claims: the agent-input grants, the five owner routes, the refusals, the cross-account cases,
raise and lower from each kind of caller, raised following the mark in every direction, the entry ending
with the session over a real tunnel connection, the restart survival, and the message waiver are all
genuinely covered through the pipeline. What only appears to be covered: `PUT placement` and `POST start`
(finding 4). The revert proof - switch the guard consultation off, rebuild, watch six of nineteen go red,
restore, rebuild, nineteen green - is the right method and I repeated the green half of it myself at this
commit (19 of 19).

**4. The record.** It uses the governance audit store the Gateway already has (`GovernanceAuditLog`, the
`governance_audit_events` table) - no second store was invented. Three new event types sit in the existing
`permission` category and are answerable by the owner's query route, which the tests read. Raise and lower
are recorded before the list changes, so a raised session never exists without a record of who raised it.
Typing and owner-route actions are recorded by the middleware BEFORE the route runs, and a store that
cannot record stops the request - no fallback, it fails loudly, exactly what critical rule 3 asks. The
prompt's words are never recorded, and the tests assert it. Ordinary actions a raised key could take anyway
leave no record, correctly: the record holds what being raised changed. The one weakness is finding 3, the
message record after the queue.

**5. Messages.** `FleetMessagePolicy` waives exactly the relationship rule and both rates for a raised
sender, one at a time so the verdict can say whether the waiver was what let the message through; the
duplicate rule, the text rules and "a session cannot message itself" all stay, and the tests pin each. The
exemption is picked by the route from the verified credential's session, never from a body field, and the
whole-account broadcast keeps its own separate human grant. A waived message that queued is recorded
against the sender. This is what the mission's first bullet asks, to the letter.

**6. The list itself.** Durable: a real table (`raised_sessions`) with a migration for each provider, and a
host test that stops the Gateway and starts a new one over the same files. Per account: tenant scope by
construction, one row per session per account by unique index, and the cross-account tests. Raised follows
the mark: when the owner's device sets the mark the marked session is raised at that moment; when the mark
moves the old one is lowered and the new one raised; when it clears nobody is raised - each step checked by
a real request with the affected key and by the stored record. The construction is the strong part: an
entry the mark granted counts only while its session IS the mark, so a writer that forgets fails closed,
and a restart or a move carries raised to the successor without multiplying it. A raised entry ends with
its session: the Director's reap over a real tunnel connection removes it, tested. The mark's six writers
are the one soft spot (finding 7).

**7. The client gets finished values.** The roster row carries `Raise`: `raised`, the visible mark and its
title, the one offer, the button's words, its title, its busy words, and the confirmation question - all
decided in `RaisedSessionRosterFold`, on the Gateway, assigned on every fold and never surviving a
Director's echo. The raise and lower routes answer the same finished shape. There is nothing left for task
2 to decide: a client draws the mark when it is there, offers the one offer, and works out nothing, which is
critical rule 7. The ended row offers nothing, so the control needs no "is this session alive" conditional
either. The display push gap is finding 8.

**8. The words.** The `fleet-manager` skill and the workflow conduct gained their "Raised" sections, and
they say what the code does: what is granted, what is never granted, that everything is recorded, and that
raised is permission and not instruction. The `fleet-comms` skill was updated in both places, and I verified
the repository copy's body is identical to the shipped single source. The typing-command ban's premise
moved and the ban's own sentences were updated to say "every session key the owner has not raised". The one
false sentence left is finding 1, and the one outside this task is finding 6. No abbreviation problem, no
assistant's name anywhere in the diff or the commits, and no fallback that hides a failure - the failures
are loud by design.

## The three decisions

**The mark alone never raises.** Right, and it is the only reading that satisfies both halves of the
mission. `PUT /gateway/fleet-manager` is a route ANY session key may already call, so had the mark raised,
any session could have raised itself - or any other - in one call, and "only the owner raises a session"
would have been false on day one. The Developer's rule (only the owner's own signed-in phone or browser
raising by marking) keeps "raised follows the mark" true for the owner's own setup flow, which the mission
names, and keeps self-raising impossible, which the owner's words demand. The subtlety I checked hardest - a
session key re-marking the already-marked session must not lower the raised Fleet Manager it is - is
guarded in `SetMarkByOwnerAsync` and pinned by a placement test. Confirmed right.

**The walkthrough's writes stay the owner's.** Right. The three writes store on the record that THE OWNER
answered, snoozed or closed - in those words ("The owner snoozed the session from the walkthrough") - so a
session performing them would write a false record, and the mission requires every raised action to be
recorded with the session that took it. A raised Fleet Manager is not cut off: it answers a record with
its own answer route, which records the Fleet Manager, and it types into a session with the agent input
grant. The grant in my mandate names "the owner-only Fleet Manager routes" and the walkthrough's READ; the
writes are correctly outside it. Confirmed right.

**Exempting the two Fleet Manager texts from the typing-command sweep.** Right. The ban existed on the
premise that the Gateway refuses `cc-devthrottle session prompt` to every session key, and this change
makes that premise false for raised sessions; a ban that outlaws the truth is a ban that gets deleted, which
is worse than an exemption named with its reason. The Developer did the honest version: the premise
sentence was updated everywhere it appears, the exemptions are named one by one with the reason, and every
other text is still held to the ban - the sweep still runs, it still fails loudly, and it proved that by
going red on my own mandate file the moment that file quoted the command. The Fleet Manager's skill and
conduct now teach the command only inside what raised allows and forbids. Confirmed right, with the Tech
Lead's confirmation noted as owed in the proof - this review is that confirmation.

## What this review does NOT cover

No real Director and no real agent ran against the change, so "passed the guard" is proven by the route's
own answer, never by text arriving in a live session - the same limit the proof states. The PostgreSQL
proofs were read about, not run by me; the raised list's own reads and writes ran on SQLite only. No web
test and no Python test ran; the phone and Cockpit are untouched by this task and unreviewed here. The
roughly 2,550 gateway host tests and the Core suite that were not re-run after the rebase are unread and
unrun by me too - I checked the reported reds against main, not the whole surface (finding 2). Nothing was
deployed, so nothing is proven about the hosted Gateway, and the shipped skill and conduct texts reach the
fleet only when the Gateway is deployed - until then, finding 1 can still be fixed at the source. Phase 2
and later are out of scope. I did not review the pull request's description on GitHub or anything outside
the commit range `origin/main..bc47d00e4` and the two untracked mission files in this worktree.

One question for the Tech Lead that is not a finding: the word "raise" now names two different acts in the
product - the owner raising a session's permissions (this change) and a session raising its hand to its
supervisor (`cc-devthrottle session raise`, the `needs-manager` route). No command collides today - the
command line's `session raise` is the hand, and the new raise and lower routes have no command line verb -
but the shipped `terminology` skill was not touched by this change and the proof asks for a line in it.
That is a wording follow-up for the same one-source rule as finding 1, and worth doing before phase 2 puts
the word on the phone.

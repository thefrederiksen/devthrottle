# Teams: the Fleet Manager lessons reach only the marked session's own Director

devthrottle_internal#2311, #3552 review: the Fleet Manager lessons. Run on 2026-10-06, branch
`teams/2311-fleet-manager-lessons` cut from origin/main e8f673ab9.

## The harm

An accepted push of a session calls `FleetManagerLessonsObserver.Observe` (`DirectorHub.cs:931-932`). When that session
is the account's marked Fleet Manager, the confirmed lessons went to the PUSHING Director. A lessons change and a
reconnect pick the Director with `directorOf`, which was `PushedSessions.TryLocateIgnoringFreshness(...)`: whichever
Director lists the id. In a team the roster accepts any id from any Director, so a colleague's Director listing the
marked id would have been sent the lessons.

## The fix (one rule, no second copy)

Both paths ask `GatewayHost.IsTeamSessionOfDirector` (`GatewayHost.cs:3372`), the same method the display push, the
turn-end watcher and the history recorder ask. In a team it answers `TeamCallerOwnership.AcceptsReport`, which is
`ClaimOf` (stored writers, then the session key row, then the sole roster holder).

- The push: `GatewayHost.cs:2036` passes `isSessionOfDirector:` to the observer. `FleetManagerLessonsObserver.cs:137`
  stamps the pushing Director only when the rule says the marked session is its own.
- A lessons change and a reconnect: `GatewayHost.cs:2033` passes `directorOf: FleetManagerDirectorOf`
  (`GatewayHost.cs:3350`). It offers the rule the roster's own answer, then every other holder, then the Director the
  session's key row names, and returns the first the rule says is the session's own, or none
  (`FleetManagerLessonsObserver.OwnDirectorOf`, `FleetManagerLessonsObserver.cs:92`).

## A personal tenant and a dark Gateway: exactly the old path

- `FleetManagerDirectorOf` returns `TryLocateIgnoringFreshness(...)?.DirectorId`, the old expression, as its first
  line. It returns it unchanged unless the tenant is valid, Teams is released and the tenant is a team
  (`GatewayHost.cs:3352-3354`).
- `IsTeamSessionOfDirector` answers true on that same condition (`GatewayHost.cs:3374-3375`), so the new push check
  never refuses outside a team.
- Proved by tests:
  - `Observe_APersonalTenant_TheOneDirectorListingTheMarkedSession_IsSentTheLessons` is a host test on a
    Teams-released Gateway, with a personal tenant's Director over the real tunnel. The lessons arrive.
  - `Observe_WithNoRuleWired_StampsThePushingDirector_AsBefore` is a unit test: with no rule wired, the old behaviour.
  - The existing `FleetManagerEventsHostTests` and `FleetManagerRoutesHostTests` pass unchanged on personal tenants.
- A dark Gateway (Teams not released) takes the same branch: `!TeamsReleased` returns the old answer. No separate host
  test is made for the dark Gateway. Both branches are the single condition shown above.

## Point 3: whose mark and whose lessons, in a team

- **The mark** is one value per TENANT: `TenantSettingsResolver.FleetManagerSessionId(TenantId)`
  (`TenantSettingsResolver.cs:409`), set at `TenantSettingsResolver.cs:658`. In a team the tenant is the team, so a
  mark would be one for the whole team, not one per person.
- **The lessons** are one set per TENANT: `GatewayHost.FleetManagerLessonsBlock(tenant)` (`GatewayHost.cs:363`) builds
  them from `FleetPreferenceStore.ConfirmedLessons(tenant)`.
- **Neither can be written in a team today.**
  - The mark is written only by:
    - the Fleet Manager start (`FleetManagerPlacementService.cs:518`), reached from the placement routes
      `/gateway/fleet-manager/{placement,start,restart,move}` (`FleetManagerPlacementEndpoints.cs:48-52`);
    - the owner's mark route `PUT /gateway/fleet-manager` (`SettingsEndpoints.cs:265`, through
      `SetMarkByOwnerAsync`, `FleetManagerPlacementService.cs:375`).
  - The lessons are written only by the `/gateway/fleet-manager/preferences...` routes
    (`FleetManagerEndpoints.cs:162-169`).
  - `TeamEndpointRules.cs` declares no rule for any `/gateway/fleet-manager` route. The team gate refuses an undeclared
    route in a team's tenant (`TeamEndpointGate.cs:144` and `:150`, `UndeclaredRefusal`).
  - `TeamEndpointWalkTests.cs:329` lists "Fleet Manager" `/gateway/fleet-manager` among the pages that are refused.
- **So, today:** in a team the mark is always empty and nothing is sent. One member's mark or lessons cannot reach
  another member's session by any path. The leak this pull request closes could not be reached yet. It would have
  opened the day the Fleet Manager routes are allowed in a team.
- **Reported to the Tech Lead before building.** The design question stays open: a team holds ONE mark and ONE lesson
  set, not one per person. That is for whoever opens the Fleet Manager routes to teams. The marking rules are not
  changed here.

## The tests

- **Host tests over the wire**, in Gateway.Tests, `Teams/HostedTeamFleetManagerLessonsTests.cs`. A real hosted Gateway
  with Teams released, a team with Alice and Bob, and both Directors connected through `FakeTunnelDirector`.
  - `Observe_BothDirectorsListAlicesMarkedSession_OnlyAlicesDirectorIsSentTheLessons_OnAPushALessonsChangeAndAReconnect`.
    Alice's Director wrote the session's stored conversation, and both Directors list its id. Alice's Director is sent
    the lessons. Bob's is never sent them: not on his snapshot or his push, not on a lessons change (Alice's Director
    is sent the new block), and not on his reconnect.
  - `DirectorOf_OnlyBobsDirectorListsAlicesKeyedMarkedSession_TheLessonsGoToAlicesDirector_NeverBobs`. Alice's
    Director holds the key row but does not list the id; only Bob's lists it. The lessons go to Alice's Director on
    Bob's connection, on a lessons change and on Bob's reconnect, and never to Bob's. This shape makes the old
    "whoever lists it" answer Bob every time, rather than depending on dictionary order.
  - `Observe_APersonalTenant_TheOneDirectorListingTheMarkedSession_IsSentTheLessons`.
- **Unit tests**, in Gateway.UnitTests, `Fleet/FleetManagerLessonsCompactionTests.cs`:
  - `Observe_APushFromADirectorTheRuleSaysIsNotTheMarkedSessionsOwn_IsNeverStamped_AndTheOwnersPushIs`
  - `Observe_WithNoRuleWired_StampsThePushingDirector_AsBefore`
  - `OwnDirectorOf_TheRosterNamesAColleague_ReturnsTheDirectorTheRuleNames`
  - `OwnDirectorOf_OnlyTheKeyRowNamesTheOwner_ReturnsTheKeyRowsDirector`
  - `OwnDirectorOf_NoCandidateIsTheSessionsOwn_ReturnsNull`
  - `OwnDirectorOf_TheRostersOwnAnswerIsTheSessionsOwn_ReturnsIt_First`

## The red check

The fix and the tests were committed first. Each mutation was made by a script, then:

1. A full build. The run aborts if the build fails.
2. The host test class is run.
3. `git checkout` restores the file in a trap, and the empty diff is checked.

A clean rebuild followed both mutations.

- **Mutation 1: the push rule removed.** `GatewayHost.cs:2035-2036` (the `isSessionOfDirector:` argument and its
  comment) were deleted.
  - RED: `Observe_BothDirectors...` fails at line 108, `Assert.Null() Failure: Value is not null`. Bob's Director WAS
    sent the lessons on his push.
  - The other two tests stay green. See `red-observe.txt`.
- **Mutation 2: the refresh choice reverted.** `GatewayHost.cs:2033` was put back to
  `directorOf: (tenant, sid) => PushedSessions.TryLocateIgnoringFreshness(tenant, sid)?.DirectorId`.
  - RED: `DirectorOf_OnlyBobsDirectorLists...` fails at line 131, `Assert.NotNull() Failure`. The lessons went to the
    Director that lists the id (Bob's), so Alice's was never stamped.
  - The other two stay green. See `red-directorof.txt`.
- **Green with the fix in place:** see `host-green.txt` (3 of 3 passed).

## The gates

| Gate | Result | Log |
|---|---|---|
| Default `.\scripts\test-local.ps1` | all projects exited zero, 3,677 tests | `default-gate.txt` |
| Gateway.UnitTests, half A (namespaces Wingman, Teams, Fleet, Api, History, Messaging, Factory, Rules, DevReports) | 4,071 passed, 6 skipped, 0 failed | `gateway-unit-a.txt` |
| Gateway.UnitTests, half B (everything else; the two filters are exact complements) | 5,343 passed, 8 skipped, 0 failed | `gateway-unit-b.txt` |
| Gateway.Tests, every class under `Teams/`, run together | 132 passed, 0 failed | `gateway-tests-teams.txt` |
| Gateway.Tests, every `FleetManager*` class and `StreamCommandTests` | 111 passed, 1 skipped, 0 failed | `gateway-tests-fleetmanager-stream.txt` |

- **The one skip** is `FleetManagerEventOutcomeAnswerPostgresTests`, a PostgreSQL proof that runs only under `-Parked`
  with Docker. This change does not touch it.
- **A first default-gate run failed one Core test,**
  `RetiredMessagingWordsTests.Nothing_in_the_repository_outside_the_named_history_uses_the_retired_messaging_words`,
  with `IOException: The process cannot access the file`. The file was the log I was writing into this folder while
  the test scanned the repository. The logs were moved outside the repository and the gate was rerun green. Only the
  green run is kept.
- **The Gateway test lock** was held by session 6e74d41b (#2307 Questions) at 11:40 UTC. This was reported to the Tech
  Lead, and the lock was not touched. My runs took the lock in turn once it was released.

## FL-F1: the marked Fleet Manager event (review of d9408cb6e)

**The harm.** A `marked` event carries the confirmed lessons. `FleetManagerEventService.DeliverOnceAsync` typed every
owed event, the marked one included, into the FIRST fresh roster row for the marked id
(`PushedSessionStore.SnapshotFresh`), with no team rule. In a team that row can be a colleague's Director that only
lists the id.

**The fix.** The delivery collects every row for the marked id. It takes only the row of a Director the one team rule
names, and withholds and logs when none is; the event waits (`FleetManagerEventService.cs:925-935`). The rule is the
same `IsTeamSessionOfDirector`, passed as `isSessionOfDirector:` (`GatewayHost.cs:3716-3720`). It is asked inside the
tenant's scope, because the rule reads the session's stored record. A personal tenant and a dark Gateway answer yes
there, so the first row is taken as before.

**Tests.**
- Host, over the wire:
  `HostedTeamFleetManagerLessonsTests.DeliverOnce_BobsDirectorsRowIsFirstForAlicesMarkedId_TheMarkedEventReachesAlicesDirector_NeverBobs`.
  - The roster's order follows string hashes that change from run to run, so naming the Directors cannot put Bob's
    row first. The test does it the certain way: at the first delivery attempt Bob's Director's row is the ONLY row
    for Alice's marked id, and the test asserts that.
  - The marked event must then wait. Once Alice's Director lists the session too, the event is typed into Alice's
    Director, lessons included, and still never into Bob's.
  - The rows are Idle, which is delivered to at once, and a delivery is booked the way a promotion books one.
- Unit, in `FleetManagerEventServiceTests`:
  - `DeliverOnce_AColleaguesDirectorAlsoListsTheMarkedId_TheMarkedEventAndLessonsGoOnlyToTheOwnDirector`
  - `DeliverOnce_OnlyAColleaguesDirectorListsTheMarkedId_NothingIsSent_AndTheEventWaits`

**Red check.** Each mutation was made after the fix was committed, built, run, and then restored with an empty diff;
a clean rebuild followed.
- **The host wiring removed.** The `isSessionOfDirector:` argument (`GatewayHost.cs:3713-3720`) was put back to
  `lessons: FleetManagerLessonsBlock);`.
  - RED: the host test fails at line 174, `Assert.Null() Failure: Value is not null`. Bob's Director WAS sent a
    `prompt` whose text begins "[Fleet Manager events] You are now this account's Fleet Manager".
  - The other three host tests stay green. See `red-events.txt`.
- **The service's own check removed.** `FleetManagerEventService.cs:927` was made to take the first row whatever the
  rule says.
  - RED: both unit tests fail. The "only a colleague lists it" test fails deterministically: a send was made.
  - The first test fails when the colleague's row happens to come first, which it did on this run. See
    `red-events-unit.txt`.
- The first version of the "withheld" unit test gave the colleague's row a waiting state with no turn end. Under the
  mutation it stayed green for that other reason: delivery is held until the turn end is seen. The colleague's row is
  now Idle, so a first-row choice really would type into it, and the test goes red as shown.

## The sweep: every path that can carry the lessons or a marked-session prompt to a Director

| Path | Where | How it picks the Director | In a team |
|---|---|---|---|
| Lessons on a push of the marked session | `DirectorHub.cs:931-932`, then `FleetManagerLessonsObserver.cs:137` | The pushing Director, only if the rule names it (`GatewayHost.cs:2036`) | Fixed in this pull request |
| Lessons on a lessons change | Route hook `GatewayHost.cs:5058`, then `Refresh` | `FleetManagerDirectorOf` (`GatewayHost.cs:3350`): the roster's answer, then the other holders, then the key row's Director; the first the rule names, or none | Fixed in this pull request |
| Lessons on a Director's new connection | `GatewayHost.cs:2039-2040`, then `Refresh` | Same as above | Fixed in this pull request |
| Lessons on the backstop sweep | `FleetManagerEventSweep.cs:47`, then `Refresh`, inside each tenant's scope | Same as above | Fixed in this pull request |
| The marked event (lessons) and every other Fleet Manager event prompt | `FleetManagerEventService.cs:925-935`, sent at `:971` | Only the row of a Director the rule names; withheld and logged when none | Fixed in this pull request (FL-F1) |
| What books a marked event: a promotion, a mark by hand, a successor told | `FleetManagerPlacementService.cs:640` and the other `Promote` calls; `RecordMarked` | Stores the event only; delivered by the row above | Covered by FL-F1 |
| A plain Fleet Manager start, whose first prompt carries the lessons | `FleetManagerPlacementService.cs:487-499` | A NEW session, created on the Director of the machine the caller chose (`request.Director = running?.DirectorId`, `:493`). No existing session id is looked up, and the session it creates is that Director's own | Not by roster. The start route is refused in a team today. Whether a team member may pick a colleague's machine is a placement question for when the route is opened, not this rule's |
| Retiring the old Fleet Manager (a close, not a prompt, and no lessons) | `FleetManagerPlacementService.cs:759`, Director from `Find` (`:816-818`) | The FIRST roster row for the old marked id, with no team rule | NOT changed. A close command, not lessons or a prompt; reachable only through restart and move, both refused in a team. Raised with the Tech Lead |
| Hand-over (a set-controller command, not a prompt) | `FleetManagerHandOverService.cs:172` | The first roster row for the session being handed over, which is a worker, not the marked session | Not a marked-session prompt. The hand-over route is refused in a team |
| A typed prompt or fleet message to any session, the marked one included | `/sessions/{sid}/prompt`, `GatewayEndpoints.cs:3943`, through `LocateSessionAsync` (`:7416`, `TryLocate`). Its held form is `HeldDeliveryDriver.OnSessionsArrived` (`HeldDeliveryDriver.cs:192-209`) | The FIRST roster holder of the id, with no team rule. The team gate decides only whether the CALLER may use the route (`Whose`) | NOT changed: it is not specific to the Fleet Manager, and `{sid}` routes ARE open in a team today. Raised with the Tech Lead as a separate finding, with a recommendation |

## Gates on the new head

| Gate | Result |
|---|---|
| Default `.\scripts\test-local.ps1` | all projects exited zero, 3,677 tests (`default-gate-fl-f1.txt`) |
| Gateway.UnitTests, half A | 4,073 passed, 6 skipped, 0 failed (`gateway-unit-a-fl-f1.txt`) |
| Gateway.UnitTests, half B | 5,343 passed, 8 skipped, 0 failed (`gateway-unit-b-fl-f1.txt`) |
| Gateway.Tests, every class under `Teams/`, in two complementary halves (`Teams.HostedTeam*`, then the rest) | 89 + 44 = 133 passed, 0 failed (`gateway-tests-teams-a-fl-f1.txt`, `gateway-tests-teams-b-fl-f1.txt`) |
| Gateway.Tests, every `FleetManager*` class and `StreamCommandTests` | 112 passed, 1 skipped (the PostgreSQL proof that runs only under `-Parked`), 0 failed (`gateway-tests-fleetmanager-stream-fl-f1.txt`) |

## FL-F3: the Bob-only delivery attempt is driven and awaited (review round 2)

**The defect in the test.** The FL-F1 host test booked a delivery with `OnEventQueued`. That waits the three-second
batch window on a background task. The test then waited three seconds for an absence and added Alice's row, so nothing
proved the attempt happened while Bob's row was the only one. With the wiring removed, a late attempt could choose
Alice's row by hash order, and the test would pass.

**The fix in the test.** While Bob's Director's row is the ONLY row for Alice's marked id (asserted just before), the
test calls the service's own `DeliverToAsync` and awaits it. This is the same method the batch window and the reconcile
call, through the production wiring. The test asserts all of these:
- The attempt answered `NotOwnDirector`. This is a new result, returned only by the WITHHELD branch, which logs the
  WITHHELD line (`FleetManagerEventService.cs`).
- Bob's Director received no marked event.
- The marked event is still open.

If a reconcile happens to be delivering at that moment, the attempt answers `AlreadyDelivering`, and the test makes it
again once that one has finished, so the answer checked is always this attempt's own. Then Alice's Director lists the
session. Her Director's arrival may book a delivery of its own, so the next driven attempt answers `Delivered`, or
`NothingOwed` when that one came first. Either way the test asserts that nothing is owed any more, that Alice's
Director received the marked event with the lessons, and that Bob's never did.

**Red check.** The host wiring was removed, exactly as in the FL-F1 red (`isSessionOfDirector:` at
`GatewayHost.cs:3713-3720`). The full class was run, then the file restored with an empty diff and the binaries rebuilt.
- RED at line 169, the Bob-only step: `Expected: NotOwnDirector, Actual: Delivered`. The attempt typed the marked event
  into Bob's Director.
- With one row there is no order to depend on, so this fails the same way on every hash order and every interleaving.
- The other three host tests stay green. See `red-events-flf3.txt`.

**Gates on 0162ced8c.**

| Gate | Result |
|---|---|
| Default `test-local.ps1` | all projects exited zero, 3,677 tests |
| Gateway.UnitTests, half A | 4,073 passed, 0 failed |
| Gateway.UnitTests, half B | 5,342 passed, 1 failed |
| Gateway.Tests, `Teams.HostedTeam*` | 89 passed |

- **The one unit failure** in half B is `HeldDeliveryDriverTests.TheTickDrivesANewlyHeldDelivery_WhileTheStartUpPassIsStillWorkingThroughItsOwn`
  ("Sequence contains more than one matching element"). This pull request does not touch that code. The class passed
  29 of 29 in three separate runs on its own, so it is a timing flake under the full parallel run.
- **See below** for the rest of the Gateway.Tests runs.

## FL-F2: moved to the follow-up pull request

Dev-report delivery picks the Director with its own `PushedSessions.TryLocate` (`GatewayHost.cs:6470-6479`). The Tech
Lead's ruling moves it to the follow-up pull request, branch `teams/2311-session-director-one-rule`. That is the root
fix: in a team, every "which Director holds session X" lookup answers through the one rule at one point.

**Not run locally on 0162ced8c.** Gateway.Tests for the other `Teams/` classes (not `HostedTeam*`), and every
`FleetManager*` class and `StreamCommandTests`, did not run here. The machine-wide Gateway test lock was held by
session 35ca0a83 (Factories Screen, test host 14904) from 14:52 UTC; two waits of over nine minutes each timed out. The
lock was never touched. On the Tech Lead's instruction the head was pushed without waiting: CI runs those classes
without the machine lock. On d9408cb6e, one commit before FL-F3's test change, those same classes passed: 133 Teams
and 112 FleetManager plus stream.

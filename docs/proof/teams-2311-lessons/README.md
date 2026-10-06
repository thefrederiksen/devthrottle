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

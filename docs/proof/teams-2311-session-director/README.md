# One answer for which Director holds a session in a team (devthrottle_internal#2311)

## What changed

In a team any Director may list any session id in its roster, and many Gateway paths answered "which Director
holds session X" with the roster's FIRST row. A colleague's Director that listed your session id could receive your
typed prompts, your held deliveries, your dev reports and your Fleet Manager's close.

**The single point is the session store.** `PushedSessionStore` now answers every per-session lookup through one rule
(`Streaming/ISessionHolderRule.cs`, installed once by the host with `UseHolderRule`):

- `TryLocate`, `TryLocateIgnoringFreshness`, `TryGetLastKnownSession` - the per-session lookups;
- `IsHoldersRow(tenant, directorId, sessionId)` - new, for a caller that picks one session out of a whole-fleet
  snapshot ("is this row THAT session's row").

In a tenant the rule governs, these return the HOLDER's row or nothing; the first row is never the answer, so a caller
cannot reach it around the rule. `DirectorsHoldingSession` stays raw: it is the rule's input, never an answer.

**The rule** (`Teams/TeamSessionHolderRule.cs`) governs a team's tenant on a Gateway where Teams is released. The
holder is the listing Director for which `TeamCallerOwnership.ClaimOf` answers `Its` (stored writers, then the
session key row, then the sole roster holder). When it names none of them: nobody - withheld, logged by the store as
`WITHHELD, nobody holds it`, never the first row. Several claimants are always one person's Directors (proof in the
class summary); among them the key row's Director wins, then the lowest id, so the answer never depends on hash order.

**Not changed:** the gate's own `Whose` decision, and anything outside "which Director holds this session".

## Personal tenants and a dark Gateway: byte-for-byte today's answer

How it is proved:

1. **By construction.** Every per-session method first asks `GoverningRule(tenant)`; when the rule does not govern
   the tenant it returns null and the method runs its original loop, unchanged text (see the diff of
   `PushedSessionStore.cs`: the governed branch is added above the old loop, the old loop is not edited).
   `IsHoldersRow` answers `true` for an ungoverned tenant, so every caller's added `&& IsHoldersRow(...)` filter is a
   no-op there: the same first row as before.
2. **By test.** `TeamSessionHolderRuleTests.Store_OnADarkGateway_AnswersTheFirstRowAsBefore` (a team tenant, Teams
   dark, Bob's row the only row: all three lookups answer Bob, `IsHoldersRow` true) and
   `Store_InAPersonalTenant_AnswersTheFirstRowAsBefore`.
3. **By the suites.** Every existing test of these paths runs on personal tenants or a dark Gateway and is green:
   the default gate, Gateway.UnitTests in full (9,417 + 1 flake, below), and the Gateway.Tests classes listed below.

## Sweep table - every caller

`src/CcDirector.Gateway`, lines as of commit on this branch. "Per-session" = answers which Director holds ONE session.

### Per-session, through the store's rule (no caller change needed)

| File:line | Lookup | Path |
|---|---|---|
| Api/GatewayEndpoints.cs:7418 | `TryLocate` in `LocateSessionAsync` | THE locator: the `/sessions/{sid}/...` verbs, prompt route, held typed prompts, dictation, voice, verb client |
| Api/GatewayEndpoints.cs:2071, 2147, 6280 | `LocateSessionAsync` | session routes, prompt route, request-scoped locate |
| Api/TypedPromptDelivery.cs:252, 329 | `LocateSessionAsync` | held typed prompt driver (ask / claimed press) |
| Api/DictationDelivery.cs:188 | `LocateSessionAsync` | held dictation delivery |
| Api/GatewayDictationEndpoint.cs:294, 947, 1172 | `LocateSessionAsync` | dictation register / complete / send anyway |
| Api/SessionVerbClient.cs:63 | `LocateSessionAsync` | every session verb resolved by id |
| Api/GatewayDictationEndpoint.cs:1555 | `TryGetFresh(rememberedDirector)` | per-Director read of the Director the delivery's record names - recorded from `LocateSessionAsync`, so from the rule |
| Api/GatewayEndpoints.cs:2155, 3172, 6936, 7015, 7099, 7248, 7318 | `TryLocateIgnoringFreshness` | "known but stale" reason for a refusal |
| Api/SessionConversationEndpoint.cs:59, 60 | `TryLocate`, `TryLocateIgnoringFreshness` | session conversation |
| Api/SessionWsProxyEndpoints.cs:71, 85, 101, 125, 180 | `TryLocate` | terminal websocket proxy |
| Api/GatewayWingmanVoiceEndpoint.cs:219 | `TryLocate` | voice activity read |
| GatewayHost.cs:3363 (`FleetManagerDirectorOf`, #3583) | `TryLocateIgnoringFreshness` | Fleet Manager lessons `directorOf`: the store's answer first; #3583's team branch then re-asks the same `ClaimOf` (`IsTeamSessionOfDirector`) over the listers and the key row, so it can only agree or add the key row's Director |
| GatewayHost.cs:2374 | `TryLocate` | locate delegate |
| GatewayHost.cs:2811 | `TryLocate` | ambient locate |
| GatewayHost.cs:2883, 2908, 3030 | `TryLocate` | per-session sends from the host |
| GatewayHost.cs:3422, 3442, 3621, 3754 | `TryLocate` | Wingman / turn verdict / spend per session |
| GatewayHost.cs:3512 | `TryLocateIgnoringFreshness` | Director of a session |
| GatewayHost.cs:5033 | `TryLocateIgnoringFreshness` | "session in account" |
| GatewayHost.cs:5382 | `TryLocate` | `findSession` |
| GatewayHost.cs:6442 | `TryLocate` | **dev-report delivery** liveness (`DevReportSessionLiveness`) |
| GatewayHost.cs:6471 | `TryLocate` | dev-report session naming |
| Wingman/GatewayTurnVerdictEnvironment.cs:263, 323, 343 | `TryLocate` | Wingman screen / owner Director |
| Fleet/FleetManagerEventService.cs:1065 | `TryGetLastKnownSession` | Fleet Manager events `LastKnown` |

### Per-session pick out of a whole-fleet list, now filtered by `IsHoldersRow`

| File:line | Was | Now |
|---|---|---|
| Fleet/FleetManagerPlacementService.cs:826 (`Find`, used at 724, 740, 743) | first roster row with the id | first row with the id that `IsHoldersRow` - **the retirement close** (`CloseSessionAsync(old.DirectorId)`) |
| Fleet/FleetManagerHandOverService.cs:179 | first roster row with the id | holder's row only (hand-over target) |
| Fleet/FleetManagerHandOverService.cs:162 | any row with the id owned by the caller | the holder's row only |
| Api/GatewayEndpoints.cs:6110 (`LastKnownSession`, used by GatewayHost.cs:2287, 5041, 5088, 5104, 5115, 5157, 5170, 5193, Api/FleetManagerEndpoints.cs:924, FleetManagerPage/Walkthrough endpoints, FleetManagerWalkthroughFold.cs:122) | first Director's row with the id | first holder's row |
| Fleet/FleetManagerEventService.cs:563-565 (`ReportedAliveElsewhere`, review OR-F2) | any other Director's live row of the id kept a worker's death from being recorded | only the holder's row counts as alive elsewhere; a colleague's listing never suppresses the death |
| Fleet/FleetManagerEventService.cs:934 (`DeliverOnceAsync`, review OR-F1) | first roster row of the marked Fleet Manager (#3583 had narrowed it with its own `isSessionOfDirector` delegate, `IsTeamSessionOfDirector`) | the first row with the id that `_env.IsHoldersRow` - the store's single point; #3583's delegate is removed. Only Bob lists it: withheld (`NotOwnDirector`), the event stays owed - **the marked event** |
| Fleet/FleetManagerLessonsObserver.cs:138 via GatewayHost.cs:2046 (#3583, on a push) | `IsTeamSessionOfDirector` | `PushedSessions.IsHoldersRow` - the same single point, so a push from a Director that only lists the marked id never stamps the lessons |
| Wingman/GatewayTurnVerdictEnvironment.cs:252-256 (`ReadSessionState`) | session row from `SnapshotFresh`, first match | rows of that id kept only if `IsHoldersRow`; other rows unchanged |

The environments the Fleet Manager services read through gained `IsHoldersRow` (`Api/FleetManagerPlacementEndpoints.cs:258`,
`Api/FleetManagerHandOverEndpoints.cs:145`, `Fleet/FleetManagerEventService.cs` `GatewayFleetManagerEventEnvironment`, all `Pushed.IsHoldersRow`); the test fakes answer `true`, as outside a team. The event environment's `IsHoldersRow` enters the account's scope first (it runs on a background task), as #3583's delegate did.

#3583 (merged as bcccb6ffe) is rebased under this branch; both of its team checks now go through the single point (rows above).

### Not per-session (whole fleet, one Director, a name, or the rule's own input)

| File:line | Lookup | Why it is not "which Director holds X" |
|---|---|---|
| Streaming/DirectorHub.cs:525; Teams/TeamCallerOwnership.cs:129, 271, 368 | `DirectorsHoldingSession` | the raw INPUT to the one rule |
| Api/GatewayEndpoints.cs:798, 5086, 6035; Api/GatewayEndpoints.cs:1220, 1249 (repositories) | `TryGetFresh(director)` | one named Director's own roster |
| Api/GatewayEndpoints.cs:1412, 3700, 6063; GatewayHost.cs:291; Teams/TeamFleetMap.cs:114; Api/HostedEnrollmentEndpoint.cs:419; GatewayHost.cs:5381 | `GetLastKnown(director)` | a Director's own roster, roster folds, counts |
| Api/GatewayEndpoints.cs:6126 | `GetLastKnown` | trigger session found by NAME, not id |
| Api/MachineEndpoints.cs:315; GatewayHost.cs:2395, 2802 | `SnapshotConnected` | display fold / machines, whole fleet |
| GatewayHost.cs:2790, 3119, 5147, 5183; Reports/MorningReportBuilder.cs:602; Api/FleetManagerHandOverEndpoints.cs:139; Api/FleetManagerPlacementEndpoints.cs:255; Fleet/FleetManagerEventService.cs:1070 | `SnapshotFresh` | whole-fleet rosters; their one-id picks are the rows above |
| Briefing/TurnEndWatcher.cs:269 | `SnapshotFresh` | every row observed; the team's per-row check is already `acceptsReport` (GatewayHost.cs:2423, `IsTeamSessionOfDirector`) |
| GatewayHost.cs:3123; Api/GatewayWingmanVoiceEndpoint.cs:414 | `SnapshotLastKnown` | ownership universe for the voice sweep |
| Fleet/DisplayFold.cs:113 | `KnownSessionIds` | ids only |
| GatewayHost.cs:4806; Fleet/FleetManagerEventService.cs:1076 | `ConnectedFleet(director)` | one named Director |
| Fleet/FleetManagerPlacementService.cs:265, 616; Fleet/FleetManagerEventService.cs:434 | `Roster` | whole roster folds, by name or by owner |

`SessionOwnerCache` (`Discovery/SessionOwnerCache.cs`) is written (`GatewayEndpoints.cs:1622, 7430`) and never read, so
it answers nothing.

**What this does NOT cover, stated:** whole-fleet snapshots are not filtered - in a team a colleague's Director's row of
your session id still appears in `SnapshotFresh` and the other snapshots. Filtering them would cost one ownership read
per row per sweep. A future caller that picks one id out of a snapshot WITHOUT asking `IsHoldersRow` would take the
first row again; nothing in code forbids it. The guard is the per-session methods, which no longer return that row.

## Tests

- **Unit, the single point** - `src/CcDirector.Gateway.UnitTests/Teams/TeamSessionHolderRuleTests.cs`, 10 tests over a
  real database, device, Director, session key registries and session store: team -> `ClaimOf`'s Director or nobody
  (keyed and only a colleague lists it: nobody; owner and colleague list it: the owner, in either order; no record and
  two listers: nobody; sole lister: it; two of one person's Directors: the key row's); dark and personal -> today's
  first row; a second rule and a rule naming a non-lister are refused.
- **Host, over the wire** - `src/CcDirector.Gateway.Tests/Teams/HostedTeamSessionDirectorOneRuleTests.cs`: a real
  hosted Gateway, Teams released, Alice and Bob in one team, both Directors on the real tunnel (`FakeTunnelDirector`).
  Alice's Director registers the session's key. Bob's Director's row is the ONLY row of the id - asserted with
  `DirectorsHoldingSession == [director-bob]` - then Alice's Director lists it too. Four tests: the typed prompt route,
  a held typed prompt driven by the Gateway, a dev report's note, the Fleet Manager's retirement close. Each asserts
  Bob's Director receives nothing in both phases and Alice's receives it in the second. A fifth (OR-F2): Alice's
  Director removes a worker of her Fleet Manager while Bob's still lists it alive (Bob's row then the only row,
  asserted) - the death is recorded, addressed to and owed to Alice's Fleet Manager. Its DELIVERY into the Fleet
  Manager is the marked-session selection, the sixth (OR-F1): Alice's session is the team's marked Fleet Manager and a
  marked event is owed; Bob's row the ONLY row (asserted) - the delivery is withheld (`NotOwnDirector`), Bob's Director
  is typed nothing and the event stays owed; then Alice's Director lists it Idle - the event is typed into hers, never
  Bob's. #3583's own `HostedTeamFleetManagerLessonsTests` run alongside it unchanged.
- **Unit** - `FleetManagerEventServiceTests` FL-F1 pair now drives the selection through a holder rule on the real
  store (`UseHolderRule`) instead of #3583's delegate.

## Runs

| Check | Result | File |
|---|---|---|
| Default gate `.\scripts\test-local.ps1` (own worktree, commit f5d38c11a) | all 10 suites outcome=Completed, exit 0 | `default-gate.txt` |
| Gateway.UnitTests in full | 9,417 passed, 1 failed, 14 skipped of 9,432 | `gateway-unit-tests.txt` |
| - the one failure, alone | 4/4 passed | `gateway-unit-tests-tenantscopedsweep-rerun.txt` |
| Gateway.Tests filtered (`Teams.`, `StreamCommandTests`, `DevReport`, `GatewayDrivesHeldDeliveries`, `TypedPromptsAreResolvedByTheGateway`, `DeliveryIdIsGatewayAuthoritative`, `FleetManager`), commit 70838483b | 316 passed, 0 failed, 2 skipped | `gateway-tests-filtered.txt` |

The one Gateway.UnitTests failure is `TenantScopedSweepTests.Hosted_OneTenantBodyThrowing_DoesNotAbortTheOthers`:
`SQLite Error 14: unable to open database file` under another test's temporary root. It builds a `DeviceRegistry()`
from the process-wide `CC_DIRECTOR_ROOT`, which parallel tests change; it passed in the first full run of this change
and passes alone. This change does not touch it or anything it reads.

## Red checks

| Bypass | Lines | Result | File |
|---|---|---|---|
| `GoverningRule` returns null (the single point bypassed: every lookup takes the first row again) | `Streaming/PushedSessionStore.cs:148` | 3 store-in-a-team unit tests red; rule-level and dark/personal tests stay green, as they should | `red-unit-single-point-bypassed.txt` |
| Same bypass, over the wire, commit 98ac7a4cb | `Streaming/PushedSessionStore.cs:148` | all 5 host tests red, each at its Bob-only assertion: typed prompt line 132 (sent to Bob, not held), held prompt line 162 (Bob asked), dev report line 188 (note typed into Bob's Director), retirement close line 215 (`kill` sent to Bob), worker death line 259 (no death recorded: Bob's listing kept it alive) | `red-host-single-point-bypassed.txt` |

After each red run the mutation was restored with `git checkout`, the tree confirmed clean, and the projects rebuilt.
| Focused host run after the rebase and OR-F1: `HostedTeamSessionDirectorOneRuleTests` + `HostedTeamFleetManagerLessonsTests`, commit e4dc6ada3 | 10 passed, 0 failed | `gateway-tests-or-f1-green.txt` |
| Red check for OR-F1 (line 934 bypassed to take the first row) | PENDING: drive C: was full (0 bytes) from about 14:27 on 6 Oct 2026, and the attempted run failed in Gateway setup writing a file, before any assertion - not counted | - |

# A team Director's own routes - proof (devthrottle_internal#2311, live proof F1 and F2)

The live proof of #2311 (`docs/proof/teams-2311-live/`, findings F1 and F2, `evidence/refused-routes.txt`) ran two real
Directors in a team. Every team Director showed red "Needs attention", because the team gate refused routes a member's
own Director calls about itself. This change gives each of those routes a rule from the role table of #2302
("Who can do what", `src/CcDirector.Gateway/Teams/TeamPermissions.cs`), through the existing machinery only:
`TeamEndpointRules` (the declarations), `TeamEndpointGate` (the one gate), `TeamAccess.Decide` (the one permission check)
and `TeamCallerOwnership` (the one resolver of the person behind a key, Director or session). The per-session "which
Director holds it" answer is the single point #3589 added, `PushedSessionStore.IsHoldersRow`; the roster is never read
by its first row.

The principle, from the table: **a person's own Director, sessions and account - yes; anyone else's - no.** A
Collaborator runs no sessions and has no Director, so every route here is refused to one. The seven rulings below were
put to the Tech Lead before they were built and approved as recommended.

## Every route, its rule, and where the rule comes from

"Runs sessions" is the table row *run sessions on their own computers* (Owner yes, Manager yes, Developer yes,
Collaborator no). "Watch" is *join or watch someone else's session, or read their full transcript* (no for every role).

| Route | Rule | Role-table line | Tests |
|---|---|---|---|
| `GET /sessions` (F1, the fleet check's read) | Runs sessions; a LIST CUT TO THE CALLER'S OWN (new target `ListCutToCallersOwn`), for every role: only the caller's own Directors (`TeamCallerOwnership.OwnerOf`), and only rows the one holder rule gives those Directors (`IsHoldersRow`). The Owner's and Manager's whole-team view stays the Fleet Map of #2312 (names and status) | Runs sessions; Watch says nobody sees another person's session rows | F1 test, the Developer / colleague-listing test, the Owner and Manager test, the Collaborator test |
| `GET /account/status` | Runs sessions; the caller's own - the answer is about the calling key only. In a team it answers signed in with no email (a team's tenant records none), so it names nobody else | Runs sessions | AccountStatus test |
| `GET /gateway/session-colours` | Runs sessions; a team-wide read (a fixed legend, no tenant data) | Runs sessions | TeamSettings read test |
| `GET /gateway/snooze-presets` | Runs sessions; a team-wide READ of the team's one setting. The `PUT` stays undeclared and refused to every role | Runs sessions (read); no row says who may change a team setting | TeamSettings read and write tests |
| `GET /gateway/injected-text` | As snooze presets | As above | As above |
| `GET /gateway/workspaces` | Runs sessions; a list cut to the workspaces captured from the caller's own Directors. A hand-written workspace names no Director and is left out; every other workspace route stays undeclared | Runs sessions | Workspaces test |
| `POST /gateway/director-errors` | Runs sessions; the caller's own - filed under the calling key's own device. The `GET` stays undeclared | Runs sessions | DirectorErrors test |
| `POST /activity-events/batch` | Runs sessions; the caller's own. The endpoint refuses the whole batch when any event names a Director that is not the calling key's own, or a session another person's Director owns (`TeamCallerChecks.RefuseActivityBatch`). The `GET` stays undeclared | Runs sessions; Watch for another's session | ActivityEvents written / refused tests |
| `POST /session-numbers/allocate` | Runs sessions; the caller's own. The number is handed to the CALLING KEY'S own Director, never the body's, and refused for a session another person's Director owns or already holds a number for (`TeamCallerChecks.RefuseNumber`) | Runs sessions; Watch | SessionNumbers tests |
| `DELETE /session-numbers/{sessionId}` | Runs sessions; the caller's own only when the Director the number was handed to is theirs (`TeamCallerOwnership.Whose`, from the allocator's record); another person's is asked as Watch and refused | Runs sessions; Watch | SessionNumbers tests |
| `POST /gateway/skills/placement` | *Use the team's shared skills and workflows* (Developer yes, Collaborator no) - a report about the caller's own machine, not a change to the shared skills; filed under the calling key's own Director whatever the body names | Use shared skills | SkillPlacement test |
| `GET /gateway/skills/placement` | Runs sessions; a list cut to the caller's own machines. See "Found and closed" below | Runs sessions | SkillPlacement test |

The rules are in `src/CcDirector.Gateway/Teams/TeamEndpointRules.cs`; the routes whose whole request is about the calling
key are named once, in `TeamCallerOwnership.AboutTheCallingKey`; the body checks are `Teams/TeamCallerChecks.cs`.

## Session numbers 937 and 983

The two sessions in the live proof showed 937 and 983 although the Gateway refused to allocate. Those are the Director's
own LOCAL OFFLINE BAND: the Gateway hands out 100 to 799 (`FleetSessionNumberAllocator.CoordinatedMaxNumber`), and when
an allocate fails the Director picks a free number from 800 to 999 itself (`SessionManager.AssignOfflineNumber`,
`SessionNumberAllocator.OfflineBandStart = 800`). With this change a team Director's allocate is served, so its
sessions get Gateway numbers from the coordinated band (the allocate test asserts 100 to 799).

## Found and closed while doing this

`GET /gateway/skills/placement` was not in the live proof's refused list because it was already OPEN in a team: it fell
under the `/gateway/skills` read row ("use the team's shared skills") and answered every member's machines to any
member. A machine is a person's own, so it now has its own exact rule and is cut to the caller's own machines, for every
role. Told to the Tech Lead.

## Folded in: the narration owner (late-pieces review, finding 1)

Added to this task by the Delivery Lead. `TeamCallerOwnership.PersonOfSession`, which the Wingman's narration plan
asks for a team session's owner (`GatewayHost.ResolveTeamNarrationPlan`), was a second "whose session" rule: it
answered nobody whenever more than one Director listed the id, so a colleague's Director listing Alice's session id
made her plan Unknown and her paid narration was silently not made. It now takes the owner from the one rule,
`ClaimOf` and then `OwnerOf`, as #3589 did for every other per-session path: stored writers first, then the key row,
then a sole lister. Tests: `HostedTeamSessionDirectorOneRuleTests.TheOwnersNarrationPlan_IsTheTeamTier_WhileAColleaguesDirectorListsTheSessionToo`
(over the real tunnel: only Bob's row - Unknown, never Bob's; both rows - Allowed) and two unit tests in
`TeamDirectorRoutesTests` (both listing - Alice's; only the colleague listing a keyed session - nobody's). The red run
with the previous code is M13 in `evidence/red-runs.txt`.

## Gaps - owner decisions, not built

- **Who may change a team's settings.** `PUT /gateway/snooze-presets`, `PUT /gateway/injected-text` and every other
  settings write stay refused to every role, the Owner included. No row of the table says who may change them.
- **Hand-written workspaces, and every workspace route but the list.** A workspace written by hand names no Director, so
  it is shown to nobody in a team; reading, writing, capturing and restoring one stay refused.
- **The member's own email on `GET /account/status`.** A team Director is told "signed in" with no email, because a team's
  tenant records none. Showing the person's own address needs a decision on where it is read from.
- **Reading back what a Director wrote:** `GET /gateway/director-errors` and `GET /activity-events` stay refused in a team
  (each would read every member's).
- **Routes still undeclared for teams.** The route-table walk lists them on every run
  (`TeamEndpointWalkTests.EveryEndpoint_InATeam_IsRefusedUnlessItStatesAnAction`, count in its output). Of the live
  proof's refusals, `GET /sessions/{sid}` (2) was already declared - its refusals were for a session the caller could
  not be shown to own - and the three marked `[proof]` were the proof's own cross-team checks, refused correctly.

## What the tests are

- **Over the wire** (`src/CcDirector.Gateway.Tests/Teams/HostedTeamDirectorRoutesTests.cs`, parked suite): a real hosted
  Gateway with Teams released, a team of Owner, Manager, Alice and Bob (Developers) and Carol (Collaborator); the four
  who run sessions each have a Director on the real tunnel with a session whose key row their Director wrote, and Bob's
  Director also lists ALICE's session id, so every cut is shown against a colleague who is trying. One test per route.
  A Collaborator is refused twice and both are asserted: the device registry does not accept a Collaborator's team key
  (401), and the gate the host installed refuses the same request for one.
- **F1 end to end:** `F1_TheFleetCheck_OnADevelopersSessionKey_SucceedsWithOnlyTheirOwnSessions` makes the exact read
  `cc-devthrottle session list` makes (`GET /sessions?envelope=true`) on a SESSION key, as the Director's fleet check
  does, and asserts only Alice's session and only Alice's Director come back.
- **Nothing changes outside a team** (`HostedTeamDirectorRoutesNoTeamTests.cs`, run on a released and on a dark
  Gateway): a person with no team and two Directors gets both Directors' sessions, the number filed under the Director
  the body names, the hand-written workspace, a changeable setting and both machines' placement; the team beside them
  and the person never see each other's (tenant isolation); on the dark Gateway a key bound to a team's tenant is not
  accepted on any of these routes.
- **Unit** (`src/CcDirector.Gateway.UnitTests/Teams/TeamDirectorRoutesTests.cs`): every live-proof route states the rule
  above; every left-open owner decision is refused even to the Owner; the gate allows a cut list to every role that runs
  sessions and records the person it allowed; a Collaborator and a stranger are refused every new rule; the resolver's
  answers for the calling-key routes and for freeing a number; and each body check.
- The #2306 whole-table test (`TeamEndpointGateTests`) and the route-table walk (`TeamEndpointWalkTests`) stay green.
  One existing test changed: `TeamCallerOwnershipTests.RunAsync_AListAcrossTheTeam_IsRefusedAsNotShownToBeTheCallersOwn`
  asserted the F1 refusal itself; it now asks a list that does not cut itself (`GET /interrupted`), and a new test asserts
  the roster reaches its endpoint.

## Red runs

Every new test was shown able to fail: twelve mutations of the shipped code, each built, run against the tests named,
recorded, and restored (`evidence/red-runs.txt`). M1 is the state before this change (the roster rule removed).

## Results

See `evidence/test-results.txt` for the counts of each run.

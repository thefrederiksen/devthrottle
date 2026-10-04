# Proof - the Fleet Map by role (devthrottle_internal#2312)

Worktree `D:\ReposFred\devthrottle-teams-2312`, 4 October 2026, cut from origin/main at cdaab4f2b (after #2300 and
#2302 merged). Pull request 1 merged as 24cf04bfa; pull request 2 is rebased onto it. Everything below ran locally on SOREN_NORTH. No production database and no deploy was touched. No
schema changed.

Two pull requests:

- **Pull request 1** (branch `teams/2312-team-switcher`): the Cockpit team switcher and the one shared current team.
  Its test runs are in `pr1-test-runs.txt`.
- **Pull request 2** (branch `teams/2312-fleet-map-by-role`, on top of pull request 1): the team roster by role on the
  Gateway, and the team map in the Cockpit. Its test runs are in `pr2-test-runs.txt`.

## What was built

| Piece | File | What it is |
|---|---|---|
| The teams read | `packages/client-core/src/teams/teamsClient.ts` | `GET /teams`. A dark Gateway (app shell) or a self-hosted one (404) reads as "not offered", never as a failure. |
| The current team | `packages/client-core/src/teams/CurrentTeam.tsx` | `CurrentTeamProvider` + `useCurrentTeam()`: the ONE current team for a shell, mounted in `AppShell`. `current` is null for the person's own account. Remembered per browser and per signed-in account. The Team page (#2303) and the Skills page (#2304) read this. |
| The switcher | `apps/cockpit/src/teams/TeamSwitcher.tsx` | Top of the rail. "Your own account", then each team with the person's role. Renders nothing for a person in no team. |
| The roster by role | `src/CcDirector.Gateway/Teams/TeamFleetMap.cs` | `GET /teams/{teamId}/fleet-map`, decided by `TeamAccess.Decide(..., SeeFleetMap)`. Owner and Manager: every Director on the team by person. Developer: only their own, cut on the server. Collaborator: refused (403). Not a member: no such team (404). |
| The rule | `src/CcDirector.Gateway/Teams/TeamEndpointRules.cs` | The route is declared, read only, exact. A new target, `TeamNarrowedToCaller`, lets a Developer's "only their own" cell through to an endpoint that cuts its own answer; the gate code is unchanged. |
| Whose a Director is | `src/CcDirector.Gateway/Teams/TeamDirectorOwnership.cs` | `PersonOfDirector(tenant, directorId)`: the ONE shared answer, for the Fleet Map and for #2311's team-key resolver. It reads `DirectorRegistry.RegisteringCredentialOf(tenant, directorId)` (the device key the Director said Hello on), then that device's `device_credentials` row: active and bound to the team's tenant, or nobody. |
| The team map | `apps/cockpit/src/fleet/TeamFleetMapView.tsx` | Shown by `FleetMapView` when a team is on screen. In the layouts the Gateway offers: By person and By director for the Owner and a Manager (D5); By director, By repository and By mission for a Developer (D4). Plain text: no link, no button. |

**The answer is an allow-list.** `TeamFleetMapDto`: team id and name, the caller's role, the scope, two sentences
(the summary and the empty-map text), the layouts, and people. A person: their email, whether it is the caller, their
Directors. A Director: name and machine. A session: name and status (working, waiting, done). No session id, no
Director id, no transcript, screen, input, prompt or path. `TeamFleetMapDtoTests` pins the field set by reflection
and on the wire, so a field added later fails a test. The Cockpit's reader also keeps only the allowed fields of a
session, whatever arrives.

**Repository and mission, per entry (Tech Lead ruling, 4 October).** "Names and status only" governs seeing OTHER
people's Directors. A session on one of the caller's OWN Directors also carries its repository (the "owner/repo" name,
else the folder's name - never the path; the same rule as the own Fleet Map and the Repos page) and its mission (the
attached mission, or "Standalone"). Everyone else's carries name and status only, and the two fields are absent from
the wire, not null. The Gateway decides this per entry (`TeamFleetMap.SessionEntry`). Both shapes are pinned: by
reflection and serialisation in `TeamFleetMapDtoTests`, on a real map in `TeamFleetMapTests` (a Manager's own entries
carry them, the Owner's and Developers' do not), and over the wire in `TeamEndpointWalkTests`.

**Whose a Director is.** The registry records the credential each Director said Hello on (`device:<id>`).
`TeamDirectorOwnership.PersonOfDirector` - the one public answer every team feature asks, #2311 included - reads that device's `device_credentials` row and takes its `account_subject` - but only when the row is active (not
revoked) and bound to THIS team's tenant. The map then also requires a person who is still a member, in a role the
table lets run sessions. A Director that fails any of those is on nobody's map, because nobody can say whose it is. Enrollment into a team is #2311 and is not built here; the tests
seed credentials directly.

**Status.** One fold (`TeamFleetMapStatus.Fold`) from the state every surface shows (the assessed state if one stands,
else the Director's own): starting or working is working, waiting for input or permission is waiting, idle is done.
An exited session is not on the map, as on every other live layout. An unknown state throws rather than invent a word.

## Screenshots - the BUILT Cockpit

`apps/cockpit/dist` from `npm run build --workspace @devthrottle/cockpit`, served by a small Node script beside a stub
Gateway that answers ONLY `GET /teams` and `GET /teams/{teamId}/fleet-map`, with a seeded team in exactly the shape
`TeamFleetMapDto` pins. The page, the switcher and the map are the real built code; the data is seeded. Every other
route answers 404 from the stub. Captured with browser-harness at 1600x900.

| File | What it shows |
|---|---|
| `00-switcher-own-account.png` | The switcher on "Your own account": the person's own Fleet Map, unchanged. Its two red lines are the STUB answering 404 to the roster and the colour legend, which it does not serve - not a product fault. |
| `01-D5-manager-by-person.png` | **D5**: Priya, a Manager on DevThrottle. By person: every person on the team, their Directors, each session's name and status. "(you)" on her own lane. |
| `02-D5-manager-by-director.png` | D5, switched to By director: one lane per Director, with whose it is. |
| `03-D4-developer-own-directors.png` | **D4**: Rob, a Developer on Paul's project. Only his own two Directors, By director, with By repository and By mission offered. |
| `05-D4-developer-by-repository.png` | D4, By repository: one lane per repository, each session with the Director it runs on. |
| `06-D4-developer-by-mission.png` | D4, By mission: one lane per mission; a session on no mission is under "Standalone". |
| `04-collaborator-no-fleet-map.png` | A Collaborator on Marketing: the Gateway's refusal, no map. |

The same run read the page through the browser: no link, button or `href` inside any lane (`0`), in D5 and in D4.

Defects found by reading these shots and fixed before the final run: the closed switcher cut the role off
("DevThrottle - Mai"); in By director the owner's email was in the accent colour, which reads as a link on a page where
nothing opens; and in By repository and By mission the session name was cut short beside its Director's name (the
Director now sits on its own line under it). The switcher fix went in with pull request 1.

# Proof - the Fleet Map by role (devthrottle_internal#2312)

Worktree `D:\ReposFred\devthrottle-teams-2312`, 4 October 2026, cut from origin/main at cdaab4f2b (after #2300 and
#2302 merged). Everything below ran locally on SOREN_NORTH. No production database and no deploy was touched. No
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
| Whose a Director is | `src/CcDirector.Gateway/Discovery/DirectorRegistry.cs` | `RegisteringCredentialOf(tenant, director)`: the device key a Director said Hello on. Its `device_credentials` row (tenant = the team) names the person. |
| The team map | `apps/cockpit/src/fleet/TeamFleetMapView.tsx` | Shown by `FleetMapView` when a team is on screen. By person (D5) and By director, in the layouts the Gateway offers. Plain text: no link, no button. |

**The answer is an allow-list.** `TeamFleetMapDto`: team id and name, the caller's role, the scope, two sentences
(the summary and the empty-map text), the layouts, and people. A person: their email, whether it is the caller, their
Directors. A Director: name and machine. A session: name and status (working, waiting, done). No session id, no
Director id, no transcript, screen, input, prompt or path. `TeamFleetMapDtoTests` pins the field set by reflection
and on the wire, so a field added later fails a test. The Cockpit's reader also keeps only `name` and `status` of a
session, whatever arrives.

**Whose a Director is.** The registry records the credential each Director said Hello on (`device:<id>`). The team map
reads that device's `device_credentials` row and takes its `account_subject` - but only when the row is active (not
revoked), bound to THIS team's tenant, and names a person who is still a member. A Director that fails any of those is
on nobody's map, because nobody can say whose it is. Enrollment into a team is #2311 and is not built here; the tests
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
| `03-D4-developer-own-directors.png` | **D4**: Rob, a Developer on Paul's project. Only his own Director; one layout, so no layout switch. |
| `04-collaborator-no-fleet-map.png` | A Collaborator on Marketing: the Gateway's refusal, no map. |

The same run read the page through the browser: no link, button or `href` inside any lane (`0`).

Two defects were found by reading these shots and fixed before the final run: the closed switcher cut the role off
("DevThrottle - Mai"), and in By director the owner's email was in the accent colour, which reads as a link on a page
where nothing opens. The switcher fix is on pull request 1's branch too.

## Not on the team map, on purpose

The Developer's D4 mockup shows "By repository" and "By mission". The allow-list the brief sets has no repository or
mission, so the team map offers By person and By director only. Adding them means widening the allow-list - an owner
decision, not this pull request's.

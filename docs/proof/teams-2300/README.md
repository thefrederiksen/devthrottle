# Proof - teams, their members and the four roles (devthrottle_internal#2300)

Branch `teams/2300-team-membership`, 3 October 2026. Everything below ran locally on SOREN_NORTH. No
production database and no deploy was touched.

- `api-transcript.txt` - the routes answering a test team over real HTTP: a new account with no teams,
  the Owner creating a team, the member list read by a Developer, the switcher read by a Collaborator, and an
  outsider refused.

## Dark until released (review finding F1)

The team routes are mapped only when `CC_GATEWAY_TEAMS=1`. Off by default: a deploy of main exposes no team route.
`HostedTeamsDarkTests` proves the default (variable unset: all three routes not found, no team created);
`HostedTeamEndpointsTests` runs with the switch on. `TeamsReleaseSwitchTests` pins that only `1` releases.
`TeamEndpointsTests.SessionKeyGuard_EveryTeamRoute_IsRefusedToAnAgentSessionKey` pins the session-key refusal (F4).
The api-transcript.txt below was captured with the switch on.

## The four tests from the issue

| Issue test | Where it is proven |
|---|---|
| 1. A new account has no team and works as today; creating a team makes it Owner and changes nothing else | `TeamRegistryTests.Issue2300Test1_...` (the personal tenant resolves identically by every read, its row is unchanged, the team is not in the accounts list); `HostedTeamEndpointsTests.NewAccount_HasNoTeams_AndCreatingOneOverTheWireMakesItTheOwner` |
| 2. A member of team A can never read team B's members, sessions or data | `TeamRegistryTests.Issue2300Test2_...` (member list refused; a row written in team B's tenant is invisible from team A's tenant and the other way round, through the tenant filter); `HostedTeamEndpointsTests.MembersRoute_...` (404, same answer as a team that does not exist) |
| 3. One account in two teams with two roles gets the right role in each | `TeamRegistryTests.Issue2300Test3_...`; `HostedTeamEndpointsTests.OneAccountInTwoTeams_...` |
| 4. Every team always has exactly one Owner; removing or demoting the last Owner is refused | `TeamRegistryTests.Issue2300Test4_...` (both); `HostedTeamEndpointsTests.TheOwner_...`; `AddTeamsPostgresTests` (the database refuses a second Owner on PostgreSQL) |

"Sessions" in test 2: sessions are tenant-scoped rows like every other, so the tenant filter proof covers them.
No Director can be bound to a team yet (that is #2311), so no session can exist in a team tenant today.

## Test runs (final code)

| Run | Result |
|---|---|
| `.\scripts\test-local.ps1` (default gate) | 10 suites, all `outcome=Completed`, 3,443 passed, 0 failed |
| `CcDirector.Gateway.UnitTests`, whole suite (`dotnet test`) | 8,023 passed, 0 failed, 8 skipped (its PostgreSQL proofs, which only run under `-Parked`) |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams\|FullyQualifiedName~Postgres"` - its own throwaway PostgreSQL | 56 passed, 0 failed, 4 skipped (`GatewayDatabaseLivePostgresProofTests`, gated on `CC_GATEWAY_DB_CONNECTION`, which the script does not set); `AddTeamsPostgresTests` passed |
| Revert check: `TeamEndpoints.Map` removed from `GatewayHost` | the 6 hosted route tests went red (6 of 6), and green again restored |

## Not run, and why

- **The whole `-Parked` run, and the whole `CcDirector.Gateway.Tests` suite.** A single foreground command here is
  capped at ten minutes. `-Parked` is documented at tens of minutes (Core.Tests alone is 11 to 33), and an
  unfiltered `-Gateway` run was still producing no output when it was stopped at 575 seconds. `select-tests.ps1
  -Explain` names only `Gateway.Tests` and `Gateway.UnitTests` as covering this change; Core.Tests does not.
  What ran of `Gateway.Tests` is every test whose name contains Teams or Postgres. The rest of that suite has
  not run on this branch.
- `CcDirector.Gateway.UnitTests`' own PostgreSQL proofs (8) skipped in the direct run; none of them is about teams.

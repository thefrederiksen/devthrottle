# Teams: a person with one team is never asked to choose

Proof for `thefrederiksen/devthrottle_internal#2311`, Phases 2-3 review finding 1. Branch
`teams/2311-one-team-never-asked`, cut from origin/main `21189035e`. Director side only; no Gateway code.

## What changed

- **One rule, in one place:** `TeamChoices.TakenWithoutAsking(choices)` (`src/CcDirector.Core/Teams/TeamChoices.cs`).
  No team listed: the personal account. Exactly one team: that team. Two or more: null, so D1 asks. It replaces
  `MustAsk`, which said "ask" whenever a team was listed, because `Build` always appends Personal (one team = two
  choices). `Build` is unchanged: it still appends Personal, so the D3 move list keeps offering it.
- **Setup (D1)** - the enrollment runner (`GatewayAccountEnrollRunner.SignInChooseTeamAndEnrollHostedAsync`) is the
  only caller; the first-run wizard and the Gateway connection panel both reach it through `HostedTeamSetup`, so
  there is no second copy of the rule in either surface. With one team the chooser is never called and the
  enrollment carries that team's `teamId`.
- **The name when D1 is not shown:** the runner sets it to `TeamChoices.SuggestDirectorName(machineName, team)`,
  the same "<computer> - <team>" the "Name this Director" box would have started with. `HostedTeamSetup` saves it
  exactly as it saves a name typed in D1 (name first, then the team).
- **The person still sees the team:** `HostedTeamSetup` records the team with `DirectorTeamStore.Save`, which is
  what redraws the D2 chip and the window title - the same write as after D1. Unchanged code, covered by
  `RunAsync_OneTeam_NotAskedNamedForTheTeamAndTeamRecorded` (key, then name, then `team:t-dev`).
- **Unchanged:** no team listed (personal, not asked, today's body byte-for-byte - the pinned literal test still
  passes); two or more (asked, Personal offered); D3 "Choose another team..." / "Move to" (the panel still lists
  `Build`'s choices minus the current team).

## Tests

New or corrected:

| Test | What it pins |
|---|---|
| `HostedTeamEnrollRunnerTests.SignInChooseTeamAndEnrollHosted_OneTeamListed_NotAskedThatTeamsIdSentAndNamedForIt` | The missing one: one team, chooser is `NeverAsked` (throws if called), body `teamId` = `t-dev`, team recorded, name "SOREN_NORTH - DevThrottle". |
| `HostedTeamSetupTests.RunAsync_OneTeam_NotAskedNamedForTheTeamAndTeamRecorded` | The same through the shared D1 transaction both surfaces run, with a real runner over a fake Gateway. |
| `HostedTeamEnrollRunnerTests.SignInChooseTeamAndEnrollHosted_ChoosesPersonal_SendsTodaysBody` | **Corrected**, not replaced: now lists TWO teams so it reaches the chooser. |
| `..._QuestionCancelled_EnrollsAndStoresNothing` | Corrected the same way (two teams); with one team there is no question to cancel. |
| `..._Enroll403_ShowsTheGatewaysWordsAndStoresNothing` | One team, now `NeverAsked`; the 403 is still shown in the Gateway's words. |
| `HostedTeamSetupTests` key/name/team, rename-fails, record-fails, cancelled | Now run over two teams, so they still exercise D1. |
| `TeamChoicesTests.TakenWithoutAsking_NoTeams_ThePersonalAccount` / `_OneTeam_ThatTeamNotAsked` / `_TwoTeams_NullSoThePersonIsAsked` | The rule directly. |
| `TeamChoicesTests.Build_OneTeam_StillOffersPersonalForTheMove` | D3's list keeps Personal with one team. |
| `DirectorTeamScreensTests.DirectorTeamPanel_ListsEveryChoiceButTheCurrentTeam` (existing) | Through the real D3 panel: a Director on the one team is offered exactly `Personal`. |

### Revert check (`revert-check.txt`)

Committed first, then `TeamChoices.cs` line 96 `1 => teams[0],` changed to `1 => null,` - the old rule (ask whenever
`choices.Count > 1`). Four tests went red:
`TakenWithoutAsking_OneTeam_ThatTeamNotAsked`, `SignInChooseTeamAndEnrollHosted_OneTeamListed_NotAskedThatTeamsIdSentAndNamedForIt`,
`SignInChooseTeamAndEnrollHosted_Enroll403_ShowsTheGatewaysWordsAndStoresNothing` and
`RunAsync_OneTeam_NotAskedNamedForTheTeamAndTeamRecorded`. Restored with `git checkout`, rebuilt, all green (52, 28, 31).

### Gate (`gate-default.txt`)

`.\scripts\test-local.ps1` (default, `MSBUILDDISABLENODEREUSE=1`): all ten suites `outcome=Completed`, every test
executed - Core.UnitTests 1232, Avalonia.Tests 908, Engine 68, HostedAgent 88, Launcher 197, Terminal.Avalonia 82,
Reclaim 310, setup 25, setup-engine 677, setup-cli 35. The gate names the three parked suites as a coverage gap;
none of them references `TeamChoices` or the hosted team enrollment (checked with grep on this branch), and no
Gateway code changed.

## Collaborator teams (brief point 5) - behaviour unchanged

The Gateway's contract is that `GET /devices/enroll-hosted/teams` lists only teams the person may run sessions in.
On main, `ListHostedTeamsAsync` checks every entry with `TeamChoices.Unreadable`, and a role other than owner,
manager or developer makes the WHOLE list a failure: "The DevThrottle hosted gateway listed team <id> with the role
"collaborator", which cannot run sessions, so this Director cannot offer the list." Nothing is enrolled
(`SignInChooseTeamAndEnrollHosted_UnreadableTeamList_AFailureResultNotAnException`).

So a list of one runnable team plus one Collaborator team is **not asked - setup stops with that error before the
rule runs**, and the Director is not connected. That does not contradict "one team, never asked" (nobody is asked),
and the list it rejects is one the Gateway's contract says it never sends. Not changed here. If the Gateway ever
does list Collaborator teams, the right fix is to skip them (not offer, not fail) before `Build`, at which point this
case becomes "one runnable team, never asked".

## What this does not prove

- Nothing ran against a real Gateway with Teams; the routes are faked to the contract, as in
  `docs/proof/teams-2311-director/`.
- No new screenshot: with one team no screen is shown, and D2/D3 drawings are unchanged.

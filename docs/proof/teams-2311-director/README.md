# Teams 13, the Director part: which team a Director works for

Proof for `thefrederiksen/devthrottle_internal#2311` (the Director part), branch `teams/2311-director-team-setup`,
cut from origin/main `cdaab4f2b`. Desktop only; no Gateway code.

## What was built

- **D1 - which team is this Director for.** Right after the hosted sign-in, the first-run wizard and the Gateway
  connection panel both ask (one shared path, `HostedTeamSetup`). The options are the Gateway's teams in its order,
  then "Personal - Just you". Each team shows the role, the member count, and "you pay" for an Owner. "Name this
  Director" starts as `<computer> - <team>` and follows the selection until the person types their own name; it is
  saved as the Director's display name. The chosen `teamId` is sent to `POST /devices/enroll-hosted`; a 403 is shown
  in the Gateway's own words.
- **No question when there is nothing to choose.** No team listed: not asked, the request is byte-for-byte today's,
  the personal account is recorded. `404` from the teams route (Teams not released): not asked, today's request, no
  team recorded, no chip.
- **Stored per Director instance**, beside that instance's key: `<instance home>/config/director/gateway-team.json`.
  Two instances on one computer hold two teams. Disconnecting forgets the team with the key.
- **D2 - the chip** beside the Director's name on the toolbar (and the team in the window title), in a colour derived
  from the team id by FNV-1a over a fixed eight-colour palette. Personal uses a neutral default chip that no team
  can get. No chip when nothing is recorded.
- **D3 - Settings, Team tab.** "This Director works for <team>", "Choose another team..." (signs in, lists with the
  account token), the other teams, "Move to <team>". Disabled with "Close the N running sessions first." while any
  session is held, and refused by `DirectorTeamMover`, which counts again at the moment of the move and never calls
  the Gateway when a session is running. With none running it calls `POST /devices/enroll-hosted/move`
  (bearer = account token, body `{deviceId, teamId|null}`), stores the new key then the team, re-applies the
  Gateway connection, and the chip updates.

Decisions taken with the Tech Lead (session 14117e2d), 4 October 2026: the move route shape above; and no person name
guessed from the token - the personal account reads "Personal", and the name box starts with the computer's name.

## Tests

Gate: `.\scripts\test-local.ps1` (MSBUILDDISABLENODEREUSE=1), all ten suites `outcome=Completed`:

| Suite | Total |
|---|---|
| CcDirector.Core.UnitTests | 1180 |
| CcDirector.Avalonia.Tests | 878 |
| CcDirector.Engine.Tests | 68 |
| CcDirector.HostedAgent.Tests | 88 |
| CcDirector.Launcher.Tests | 197 |
| CcDirector.Terminal.Avalonia.Tests | 82 |
| CcDirector.Reclaim.Tests | 310 |
| cc-director-setup.Tests | 25 |
| cc-director-setup-engine.Tests | 660 |
| cc-director-setup-cli.Tests | 34 |

The gate named the parked suites as a coverage gap. No Gateway code changed. Of the parked Core.Tests, the credential
store and token reader classes were run on their own: 17 passed, including the new
`ClearConnection_ForgetsTheTeamThatCameWithTheKey`.

New tests, by the brief's list:

- One choice (no team) is never asked; enrollment is exactly today's request:
  `SignInChooseTeamAndEnrollHosted_NoTeamListed_NotAskedTodaysRequestPersonalRecorded`,
  `..._TeamsRouteAnswers404_NotAskedTodaysRequestNoTeamRecorded` (body compared byte-for-byte with the pre-Teams call).
- With teams, the list is the Gateway's plus personal and the chosen id is sent:
  `..._TeamsListed_OffersTheGatewayListPlusPersonalAndSendsTheChosenId`, `..._ChoosesPersonal_SendsNoTeamId`.
- Stored per instance: `DirectorTeamStoreTests.SaveAt_TwoInstanceHomes_HoldTwoDifferentTeams`,
  `TeamFileAt_LivesBesideTheInstancesOwnKey`, `ClearAt_ForgetsTheTeamOfThatInstanceOnly`.
- Move refused with a session running, done with none: `DirectorTeamMoverTests` (five), and through the real panel
  `DirectorTeamPanel_MoveWithASessionRunning_RefusedByTheCodeNotJustTheButton`,
  `DirectorTeamPanel_NoSessionRunning_MovesAndStoresTheNewKeyAndTeam`.
- Colour: `TeamColorTests` - same id same colour, pinned against an independent FNV-1a, every palette colour used,
  personal never a team colour.
- 404 means no question and no chip: the 404 enrollment test above, `SignInAndListHostedTeams_404_SaysTeamsAreNotReleased`,
  `MainWindow_NoTeamRecorded_NoChip`, `DirectorTeamPanel_GatewayHasNoTeams_NoChoiceOffered`.
- Failure cases: a cancelled question, a 403 in the Gateway's words, a failed teams listing (never read as "no
  teams"), a move refused by the Gateway, a move without sign-in.

## Screens, drawn by the real controls (headless Skia, Gateway faked)

Written by `DirectorTeamScreensTests.Capture_D1_D2_D3_DrawRealPictures` with `TEAMS_2311_SCREENSHOT_DIR` set.

- `screens/d1-which-team-is-this-director-for.png` - D1, the real `TeamChoiceDialog`.
- `screens/d2-two-directors-two-teams-one-computer.png` - D2, the toolbar name panel lifted off two real
  `MainWindow`s, one per team.
- `screens/d3-move-locked-one-session-running.png` - D3, locked.
- `screens/d3-move-open-no-session-running.png` - D3, open.
- `screens/d3-after-the-move.png` - D3, after the move; the chip shows the new team.

## What this does not prove

- Nothing here ran against a real Gateway. The teams route and the move route are faked; the move route is coded to
  the shape the Tech Lead confirmed, and the Gateway Developer builds it in parallel. The live two-Directors proof is
  a later step.
- The command line's `enroll --hosted` still enrolls without asking for a team (personal), unchanged.

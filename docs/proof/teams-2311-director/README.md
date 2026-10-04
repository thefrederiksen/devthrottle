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
- **The Gateway's own signal decides first** (review round 1, F1). `GET /healthz` is read before anything about
  teams: `"teams": true` means the teams call follows and any non-200 from it is an error shown as it is;
  `false`, or no `teams` field at all (what today's production Gateway really sends), means exactly today's path - no
  teams call, no question, no chip. A starting Gateway's 503 carries the same field and is read.
- **No question when there is nothing to choose.** No team listed: not asked, the request is byte-for-byte today's
  (pinned as a literal), the personal account is recorded.
- **Stored per Director instance**, beside that instance's key: `<instance home>/config/director/gateway-team.json`.
  Two instances on one computer hold two teams and two keys. Disconnecting forgets the team with the key. A
  Director with no file (enrolled before this change, or by the command line - both personal) shows the personal
  chip and can be moved, on a Gateway whose signal says it has Teams (F2). `cc-director-setup enroll --hosted` says
  plainly that it joins the personal account.
- **D2 - the chip** beside the Director's name on the toolbar (and the team in the window title), in a colour derived
  from the team id by FNV-1a over a fixed eight-colour palette. Personal uses a neutral default chip that no team
  can get. No chip when nothing is recorded.
- **D3 - Settings, Team tab.** "This Director works for <team>", "Choose another team..." (signs in, lists with the
  account token), the other teams, "Move to <team>". Disabled with "Close the N running sessions first." while any
  session is held. `DirectorTeamMover` holds session creation in `SessionManager` for the whole move - every create
  path is refused with the reason - and counts under that hold, so no session can start between the count and the
  re-apply (F3). It calls `POST /devices/enroll-hosted/move` (bearer = account token, body `{deviceKey, teamId|null}`
  per the Gateway contract; the key is never logged), records the team FIRST and the key LAST, then re-applies.
  Any local failure after the Gateway's yes says the move happened, what was not stored and what to do (F4); a 401
  says to sign in again with "Choose another team..." (F9).

Decisions taken with the Tech Lead (session 14117e2d), 4 October 2026: the move route shape above (body changed to
`{deviceKey, teamId}` by the Gateway contract); the `teams` field on `/healthz` as the one signal; and no person name
guessed from the token - the personal account reads "Personal", and the name box starts with the computer's name.
Review round 1 rulings: `rulings-2311-review-round1.md`; every finding is answered in `reviews/review-2311-director.md`.

## Tests

Gate, round 1 head: `.\scripts	est-local.ps1` (MSBUILDDISABLENODEREUSE=1), all ten suites `outcome=Completed`:

| Suite | Total |
|---|---|
| CcDirector.Core.UnitTests | 1208 |
| CcDirector.Avalonia.Tests | 887 |
| CcDirector.Engine.Tests | 68 |
| CcDirector.HostedAgent.Tests | 88 |
| CcDirector.Launcher.Tests | 197 |
| CcDirector.Terminal.Avalonia.Tests | 82 |
| CcDirector.Reclaim.Tests | 310 |
| cc-director-setup.Tests | 25 |
| cc-director-setup-engine.Tests | 669 |
| cc-director-setup-cli.Tests | 35 |

The gate names the parked suites as a coverage gap. No Gateway code changed. `SessionManager` and the credential store
did, so the parked Core.Tests classes for them were run alone: `SessionManager`, `GatewayCredentialStore` and
`SessionDeletionReaper` - 47 passed, 1 skipped (an existing skip).

New tests, by the brief's list and the review:

- One choice (no team) is never asked; enrollment is exactly today's request, compared with a pinned literal body:
  `..._NoTeamListed_NotAskedTodaysBodyPersonalRecorded`, `..._ChoosesPersonal_SendsTodaysBody`.
- What a pre-Teams Gateway really sends (no `teams` on `/healthz`, 401 on the teams route): no teams call, no
  question, no team - `..._PreTeamsGateway_NoTeamsCallNotAskedTodaysBodyNoTeamRecorded`,
  `SignInAndListHostedTeams_PreTeamsGateway_SaysNoTeamsWithoutSigningIn`, `HostedTeamsSignalTests`.
- Signal true and the list is not 200 (404, 401, 500): an error as it is, nothing enrolled. Unreadable list or a
  Collaborator role: a failure result.
- With teams, the offer is the Gateway's plus personal and the chosen id is sent.
- Stored per instance: two teams AND two keys in two instance homes (`TwoInstanceKeysTests`), plus the store tests;
  `Changed` is raised on save and clear.
- Move: refused with a session running; a session started DURING the move is refused
  (`SessionCreationHoldTests.MoveAsync_ASessionStartedDuringTheMove_IsRefused`); every create path refused while
  held; team then key then re-apply all under the hold; the three after-yes failures each worded; through the real
  panel too.
- D1 through `HostedTeamSetup` with a real runner over a fake Gateway: key, then name, then team; a failed rename or
  team record is reported as a join that happened; and both real surfaces are checked to call it.
- No file on a Gateway with Teams shows the personal chip (`MainWindow_NoFileOnAGatewayWithTeams_ShowsThePersonalChip`).
- `enroll --hosted` prints that it joins the personal account and sends no team id.

## Screens, drawn by the real controls (headless Skia, Gateway faked)

Written by `DirectorTeamScreensTests.Capture_D1_D2_D3_DrawRealPictures` with `TEAMS_2311_SCREENSHOT_DIR` set.

- `screens/d1-which-team-is-this-director-for.png` - D1, the real `TeamChoiceDialog`.
- `screens/d2-two-directors-two-teams-one-computer.png` - D2, the toolbar name panel lifted off two real
  `MainWindow`s, one per team.
- `screens/d3-move-locked-one-session-running.png` - D3, locked.
- `screens/d3-move-open-no-session-running.png` - D3, open.
- `screens/d3-after-the-move.png` - D3, after the move; the chip shows the new team.

## What this does not prove

- Nothing here ran against a real Gateway with the Teams half. `/healthz`, the teams route and the move route are
  faked to `docs/proof/teams-2311/gateway-contract.md` on `teams/2311-gateway-team-key`. The live two-Directors proof
  is a later step.
- The contract says a team key cannot yet connect its tunnel (the team bill is the next step), which this side
  cannot change.

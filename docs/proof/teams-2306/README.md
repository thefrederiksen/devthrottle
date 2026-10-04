# Proof: the Collaborator's own app (devthrottle_internal#2306)

Run on 2026-10-04 in the worktree `devthrottle-teams-2306`, branch `teams/2306-collaborator-app`, cut from `origin/main` at f2c5c29a9.

## What was built

- **The Gateway's page verdict.** `GET /teams` now carries, for each team, an `app` object: `full` (the whole Cockpit or not), `pages` (id, label, address), `landing`, and `elsewhere` (the sentence for every other address). It is read from the role table (`TeamApp` over `TeamPermissions`), never from the role's name: the whole Cockpit where the role may run sessions, and the three pages where it may answer questions, send requests and read reports. For a Collaborator that is exactly Questions, Requests and Reports, landing on Questions, and "This page is not available to Collaborators." everywhere else.
- **The Cockpit shell.** In a team whose verdict is not the whole app, the rail is the Gateway's pages and nothing else (no Account, Settings or Help rows, no network pill), the bare address opens the landing page, and any other address shows the one plain sentence and mounts no page at all. Switching to such a team opens its landing page; switching away goes back to the Cockpit's start. A person with no team, or on their own account, sees today's Cockpit unchanged. At 600 px and below the pages-only rail becomes a bar across the top.
- **Three page slots** with honest empty pages: `apps/cockpit/src/teams/collaborator/QuestionsPage.tsx` (#2307), `RequestsPage.tsx` (#2308), `ReportsPage.tsx` (#2309).

## The checks (counts)

| Check | Result | File |
|---|---|---|
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams"` | 45 of 45 executed, all passed, outcome Completed | `gateway-tests-teams.log` |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"` | 546 passed, 0 failed, 2 skipped (the two proof rigs, which only run on their own) | `gateway-unittests-teams.txt` |
| `.\scripts\test-local.ps1` (the default gate) | 10 suites, every one outcome Completed, 0 failures: Core.UnitTests 1221, Avalonia 892, Engine 68, HostedAgent 88, Launcher 197, Terminal.Avalonia 82, Reclaim 310, setup 25, setup-engine 676, setup-cli 35. The script reported Core.UnitTests OVER BUDGET (it ran past the 120-second ceiling while the machine was loaded); every one of its 1221 tests executed and passed. | (summary only) |
| Cockpit web tests (`apps/cockpit`, `npx vitest run`) | 70 files, 623 tests passed | `cockpit-vitest.txt` |
| client-core web tests (`packages/client-core`, `npx vitest run`) | 142 files, 1666 tests passed | `client-core-vitest.txt` |
| `tsc --noEmit` for cockpit, mobile, client-core | clean | `tsc.txt` |

The parked `-Parked` run is the Tech Lead's to start on continuous integration, as the brief says.

## Every new test can fail: the red record

Each break was made in one file, the guarding tests run, and the file restored in a `finally` block, then checked with `git diff --quiet` against the commit. Full output in each file.

| Break | Tests that went red | File |
|---|---|---|
| The Gateway gives the whole app on the wrong row of the table (`FullAppAction` = answer questions) | 3: the per-role whole-app theory for Collaborator, the Collaborator's three pages and landing, and `GET /teams` carrying the verdict | `red-1-gateway-verdict.txt` |
| The rail ignores the verdict and draws the whole app | 4: three items, drawn from the verdict not the role, switch to Developer, switch to Collaborator | `red-2-cockpit-rail.txt` |
| A typed address renders the page instead of the not-available page | 9: every typed address, including a page that does not exist | `red-3-cockpit-not-available.txt` |
| The role table lets a Collaborator run sessions | 2: the server refuses the data behind every page (GET /sessions was Allowed), and `GET /teams` over the wire carries the Collaborator's verdict | `red-4-server-refusal.txt` |

What the red record does NOT cover: `Issue2306_OverTheWire_AKeyBoundToTheTeam_GetsNoDataBehindAnyPageACollaboratorCannotOpen` stayed green under break 4, because a key bound to a team's tenant is refused by the hosted device registry before the role table is asked. That test proves no request from inside a team gets the data today; the role-table refusal for each page is proven against the host's own installed gate over its real route table (`Issue2306_ACollaboratorInTheTeam_TheDataBehindEveryPageTheyCannotOpen_IsRefused`), which is the test break 4 turned red.

## Screenshots

Real renders: `take-screenshots.py` builds the Cockpit, starts `TeamCollaboratorAppProofRig` (a hosted Gateway on 127.0.0.1 with Teams released, one account - docs@mindzie.com, a fleet test account - that is a Collaborator in "DevThrottle" and a Developer in "Paul's project"), and drives a headless browser. Nothing is emailed and the database is a scratch one.

Desktop (1280 px):
- `desktop-0-own-account-full-app.png` - the own account: today's Cockpit, with the switcher at the top of the rail.
- `desktop-1-questions.png`, `desktop-2-requests.png`, `desktop-3-reports.png` - the Collaborator's three pages.
- `desktop-4-typed-sessions-not-available.png`, `desktop-5-typed-skills-not-available.png` - typed addresses.
- `desktop-6-developer-team-full-app.png` - the same account switched to the team where it is a Developer.

Phone width (390 px): `phone-390-1-questions.png`, `phone-390-2-requests.png`, `phone-390-3-reports.png`, `phone-390-4-typed-sessions-not-available.png`, `phone-390-6-developer-team-full-app.png`.

The phone-width shots are a 390 px browser window with a desktop browser's identity. A real phone's browser is sent to the mobile app by the Gateway's mobile front door before the Cockpit loads; see the pull request.

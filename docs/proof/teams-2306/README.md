# Proof: the Collaborator's own app (devthrottle_internal#2306)

Run on 2026-10-04 in the worktree `devthrottle-teams-2306`, branch `teams/2306-collaborator-app`, cut from `origin/main` at f2c5c29a9 and merged with `origin/main` at 31eb9458e (the Mentor's weekly page, #2305). Updated after the Tech Lead's review (`review-2306.md` in the mission record, findings F1 to F13).

## What was built

- **The Gateway's page verdict.** `GET /teams` carries, for each team, an `app` object: `full` (the whole Cockpit or not), `pages` (id, label, address), `landing`, and `elsewhere` (the sentence for every other address). It is read from the role table (`TeamApp` over `TeamPermissions`), never from the role's name: the whole Cockpit where the role may run sessions, and the three pages where it may answer questions, send requests and read reports. For a Collaborator that is exactly Questions, Requests and Reports, landing on Questions, and "This page is not available to Collaborators." everywhere else.
- **The Gateway's start verdict (review F1).** `GET /teams` also carries `start`: where a browser that has never chosen begins. The person's own account if they have ever registered a Director there; otherwise their only team; otherwise the chooser (screen S11). A phone or a browser sign-in is not a Director. The Cockpit applies it verbatim on the first arrival and remembers it, and waits for it rather than drawing the whole app first. Accepting an invitation remembers the joined team, so the person lands in it.
- **The Cockpit shell.** In a team whose verdict is not the whole app, the rail is the Gateway's pages and nothing else (no Account, Settings or Help rows, no network pill), the bare address opens the landing page, and any other address shows the one plain sentence and mounts no page at all. The rail foot shows who is signed in, their role, and Sign out (review F3). The rail never collapses there (F4). The shell makes none of the whole app's five background reads - keep-warm, pending dictations, dictionary suggestions, the Fleet Manager waiting count, the Factory switch (F2). A person with no team, or on their own account, sees today's Cockpit unchanged. At 600 px and below the pages-only rail, and the chooser's, becomes a bar across the top.
- **Three page slots** with honest empty pages: `apps/cockpit/src/teams/collaborator/QuestionsPage.tsx` (#2307), `RequestsPage.tsx` (#2308), `ReportsPage.tsx` (#2309).
- **A phone reaches the three pages.** While Teams is released, the Gateway's mobile front door does not send a phone at exactly `/questions`, `/requests` or `/reports` (or the sign-in round trip back to one) to the mobile app. Only those exact addresses: `/reports/repositories-weekly` and anything else under them still go to the mobile app (F11).

## The checks (counts)

| Check | Result | File |
|---|---|---|
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams\|FullyQualifiedName~MobileRedirect"` | 99 of 99 executed, all passed, outcome Completed | `gateway-tests-teams-and-mobile-redirect.log` |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"` | 557 passed, 0 failed, 2 skipped (the two proof rigs, which only run on their own) | `gateway-unittests-teams.txt` |
| `.\scripts\test-local.ps1` (the default gate) | 10 suites, every one outcome Completed, 0 failures | (summary only) |
| Cockpit web tests (`apps/cockpit`, `npx vitest run`) | 71 files, 665 tests passed | `cockpit-vitest.txt` |
| client-core web tests (`packages/client-core`, `npx vitest run`) | 143 files, 1707 tests passed | `client-core-vitest.txt` |
| mobile web tests (`apps/mobile`, `npx vitest run`) | 23 files, 125 tests passed | `mobile-vitest.txt` |
| `tsc --noEmit` for cockpit, mobile, client-core | clean | `tsc.txt` |

The parked `-Parked` run is the Tech Lead's to start on continuous integration, as the brief says.

## The red record: the nine breaks below

Each break was made in one file, the guarding tests run, and the file restored in a `finally` block, then checked with `git diff --quiet` against the commit. Full output in each file. These nine are what the record covers - not every new test.

| Break | Tests that went red | File |
|---|---|---|
| The Gateway gives the whole app on the wrong row of the table (`FullAppAction` = answer questions) | 3: the per-role whole-app theory for Collaborator, the Collaborator's three pages and landing, and `GET /teams` carrying the verdict | `red-1-gateway-verdict.txt` |
| The rail ignores the verdict and draws the whole app | 4: three items, drawn from the verdict not the role, switch to Developer, switch to Collaborator | `red-2-cockpit-rail.txt` |
| A typed address renders the page instead of the not-available page | 9: every typed address, including a page that does not exist | `red-3-cockpit-not-available.txt` |
| The role table lets a Collaborator run sessions | 2: the server refuses the data behind every page (GET /sessions was Allowed), and `GET /teams` over the wire carries the Collaborator's verdict | `red-4-server-refusal.txt` |
| The team gate lets a role-refused invitation write through | 1: over real HTTP, `POST /teams/{teamId}/invitations` as the team's Collaborator "was refused, but not by the team gate" | `red-5-team-route-refusal-over-http.txt` |
| The phone exemption for the three team pages is removed (rerun after the exact-address change) | 9: 6 policy cases (each exact page address, one in capitals, and two sign-in round trips) and the 3 over-the-wire phone tests | `red-6-phone-exemption.txt` |
| The start rule ignores the own account's Director | 2: the rule itself (a Director on the own account starts there whatever the teams) and `GET /teams` saying the own account | `red-7-start-rule.txt` |
| The store ignores the Gateway's start | 1: a Collaborator's first arrival in their only team lands on the three pages | `red-8-first-arrival.txt` |
| The pages-only shell makes the whole app's reads | 2: the five reads are never made, and the rail is exactly the three pages | `red-9-whole-app-reads.txt` |

Over real HTTP, the routes that carry the team in the address (`/teams/{teamId}/...`, called with the person's own key) run inside a team today, so for them the Collaborator's refusal is proven through the real pipeline: every such route whose action the role table does not give a Collaborator - today the three invitation writes behind the invite page - answers the gate's 403 (`Issue2306_OverTheWire_ACollaborator_EveryTeamRouteTheTableRefusesThem_Is403`, break 5).

For the routes that take the tenant from the key, the refusal is proven against the host's own installed gate over its real route table: the hand list of the data behind every page a Collaborator cannot open (`Issue2306_ACollaboratorInTheTeam_TheDataBehindEveryPageTheyCannotOpen_IsRefused`, break 4), and every endpoint outside `/teams/{teamId}/` that the table does not give a Collaborator (`Issue2306_ACollaboratorInTheTeam_EveryEndpointOutsideTheTeamRoutes_IsRefused`, review F8).

What this record does NOT prove: role refusal over the wire on the tenant-from-key routes. A key bound to a team's tenant is refused by the hosted device registry before the role table is asked, so `Issue2306_OverTheWire_AKeyBoundToTheTeam_...` is a tripwire that no request from inside a team gets the data today, not a refusal proof - it stayed green under break 4. That proof waits on team-bound keys from #2311 (devthrottle#3530) and will be made in #2310. `EachTeamPageAddress_IsServedTheCockpit` proves the pages are served, not refused.

## Screenshots

Real renders: `take-screenshots.py` builds the Cockpit, starts `TeamCollaboratorAppProofRig` (a hosted Gateway on 127.0.0.1 with Teams released, one account - docs@mindzie.com, a fleet test account - that is a Collaborator in "DevThrottle" and a Developer in "Paul's project", whose only device is a browser, so it has no Director), and drives a headless browser that has never chosen a team. Nothing is emailed and the database is a scratch one.

Desktop (1280 px):
- `desktop-0-fresh-browser-chooser.png` - the first arrival: two teams and no Director, so the Gateway says choose.
- `desktop-1-questions.png`, `desktop-2-requests.png`, `desktop-3-reports.png` - after Open DevThrottle: the Collaborator's three pages, with name, role and Sign out at the rail foot.
- `desktop-4-typed-sessions-not-available.png`, `desktop-5-typed-skills-not-available.png` - typed addresses.
- `desktop-6-developer-team-full-app.png` - the same account switched to the team where it is a Developer.
- `desktop-7-own-account-full-app.png` - the own account: today's Cockpit.

Phone width (390 px): `phone-390-0-fresh-browser-chooser.png`, `phone-390-1-questions.png`, `phone-390-2-requests.png`, `phone-390-3-reports.png`, `phone-390-4-typed-sessions-not-available.png`, `phone-390-6-developer-team-full-app.png`.

The phone-width shots are a 390 px browser window with a desktop browser's identity. A real phone reaches the same pages through the exemption above, proven over real HTTP with an iPhone browser identity, signed in and signed out, in `gateway-tests-teams-and-mobile-redirect.log`. While Teams is dark a phone goes to the mobile app exactly as before.

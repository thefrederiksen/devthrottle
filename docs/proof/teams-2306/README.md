# Proof: the Collaborator's own app (devthrottle_internal#2306)

Run on 2026-10-04 in the worktree `devthrottle-teams-2306`, branch `teams/2306-collaborator-app`, cut from `origin/main` at f2c5c29a9 and merged with `origin/main` at 31eb9458e (the Mentor's weekly page, #2305). Updated after the Tech Lead's review (`review-2306.md` in the mission record, findings F1 to F13) the delta review (`review-2306-delta.md`, D1 to D8), and the round 3 review (`review-2306-round3.md`, R1 to R7). Round 3 touched no .NET code, so the Gateway checks below are the ones run at c9c9b6e95.

## What was built

- **The Gateway's page verdict.** `GET /teams` carries, for each team, an `app` object: `full` (the whole Cockpit or not), `pages` (id, label, address), `landing`, and `elsewhere` (the sentence for every other address). It is read from the role table (`TeamApp` over `TeamPermissions`), never from the role's name: the whole Cockpit where the role may run sessions, and the three pages where it may answer questions, send requests and read reports. For a Collaborator that is exactly Questions, Requests and Reports, landing on Questions, and "This page is not available to Collaborators." everywhere else.
- **The Gateway's start verdict (review F1).** `GET /teams` also carries `start`: where a browser that has not picked begins. The person's own account if they have a Director on record there; otherwise their only team; otherwise the chooser (screen S11). A phone or a browser sign-in is not a Director. Accepting an invitation remembers the joined team as a pick, so the person lands in it.
- **Nothing remembered never waits (delta D1).** A browser that remembers no team draws today's Cockpit at once and applies the Gateway's start when it answers - so a Gateway with Teams dark, and a person with no team, never wait on `GET /teams` and never see "Loading your team" (except at the three team page addresses, below). Only a remembered team waits, and the read has a time limit (the poll reads' 10 seconds; it covers the wait for the answer to begin, not a body that stalls after its headers). The price: until the list of teams is read, a browser that has not picked shows the person's own account - for a Collaborator's first load, until their pages appear; for a person with several teams and no Director, on every load until they pick, before the chooser replaces it; and for as long as a failed first read takes to retry (15, 30, then 60 seconds), after which a Collaborator is moved to their landing page. The one exception is a link to a team page itself (`/questions`, `/requests`, `/reports`), which shows "Loading your team..." rather than "Page not found" while the list is read, then opens on that page (round 3, R1). None of this shows another person's data: every read goes out with the person's own key.
- **The Gateway's answer is not a pick (delta D2).** It is stored apart (`gateway:own-account`, `gateway:<team>`) and every later answer replaces it; only a pick (switcher, chooser, accept page) outranks the Gateway.
- **The Cockpit shell.** In a team whose verdict is not the whole app, the rail is the Gateway's pages and nothing else (no Account, Settings or Help rows, no network pill), the bare address opens the landing page, and any other address shows the one plain sentence and mounts no page at all. The rail foot shows who is signed in, their role, and Sign out (review F3); the chooser and a team that could not be opened show who is signed in and Sign out too (delta D6), except in a collapsed rail. "Open your own account instead" on a team that could not be opened lasts for that page load only and stores nothing, so the next load tries the team again (round 3, R3). The rail never collapses there (F4). The shell makes none of the whole app's five background reads - keep-warm, pending dictations, dictionary suggestions, the Fleet Manager waiting count, the Factory switch (F2). A person with no team, or on their own account, sees today's Cockpit unchanged. At 600 px and below the pages-only rail, and the chooser's, becomes a bar across the top.
- **Three page slots** with honest empty pages: `apps/cockpit/src/teams/collaborator/QuestionsPage.tsx` (#2307), `RequestsPage.tsx` (#2308), `ReportsPage.tsx` (#2309).
- **A phone reaches the three pages.** While Teams is released, the Gateway's mobile front door does not send a phone at exactly `/questions`, `/requests` or `/reports` (or the sign-in round trip back to one) to the mobile app. Only those exact addresses: `/reports/repositories-weekly` and anything else under them still go to the mobile app (F11). The Cockpit reads a team page address the same way, exactly (delta D8): `/questions/q-1` shows the not-available sentence.

## The checks (counts)

| Check | Result | File |
|---|---|---|
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams\|FullyQualifiedName~MobileRedirect"` | 100 of 100 executed, all passed, outcome Completed | `gateway-tests-teams-and-mobile-redirect.log` |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"` | 557 passed, 0 failed, 2 skipped (the two proof rigs, which only run on their own) | `gateway-unittests-teams.txt` |
| `.\scripts\test-local.ps1` (the default gate) | 9 of 10 suites outcome Completed, 0 failures; Core.UnitTests was stopped at the 120-second ceiling on a loaded machine (the script says this is not a failure). Run on its own with `dotnet test`: 1221 passed, 0 failed. This change does not touch Core. | (summary only) |
| Cockpit web tests (`apps/cockpit`, `npx vitest run`) | 71 files, 674 tests passed | `cockpit-vitest.txt` |
| client-core web tests (`packages/client-core`, `npx vitest run`) | 143 files, 1713 tests passed | `client-core-vitest.txt` |
| mobile web tests (`apps/mobile`, `npx vitest run`) | 23 files, 125 tests passed | `mobile-vitest.txt` |
| `tsc --noEmit` for cockpit, mobile, client-core | clean | `tsc.txt` |

The parked `-Parked` run is the Tech Lead's to start on continuous integration, as the brief says.

## The red record: the fifteen breaks below

Each break was made in one file, the guarding tests run, and the file restored in a `finally` block, then checked with `git diff --quiet` against the commit. Full output in each file. These fifteen are what the record covers - not every new test.

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
| The pages-only shell makes the whole app's reads | 2: the five reads are never made, and the rail is exactly the three pages (the record shows the first spy that was called; the test asserts all five) | `red-9-whole-app-reads.txt` |
| A browser with nothing remembered waits for the team list (delta D1) | 13: every whole-app shell test with nothing remembered, both "draws today's Cockpit at once" cases (Teams dark, no team), the first arrival drawing the own account at once, and three store tests | `red-10-nothing-remembered-waits.txt` |
| The Gateway's start is stored as a pick (delta D2) | 4: the stored answer replaced by the next one, the team start and the no-team start stored as the Gateway's answer, a left team falling back to the Gateway's answer | `red-11-gateway-answer-stored-as-a-pick.txt` |
| `GET /teams` ignores the caller's own Director (delta D3) | 1: over real HTTP, the Collaborator signed in from a browser starts in their team and, once a computer enrolls, on their own account (the record names the one failing test; its counts are in the run's result file, not the record) | `red-12-start-ignores-the-callers-director.txt` |
| A team page does not wait for the list of teams (round 3, R1) | 1: a mailed link to `/reports` in a fresh browser shows "Page not found" first | `red-13-team-page-does-not-wait.txt` |
| The Gateway's start moves the person off the team page a link named (round 3, R1) | 1: the same arrival is moved to Questions, so Reports never shows | `red-14-start-moves-off-the-linked-page.txt` |
| "Open your own account instead" is stored as a pick (round 3, R3) | 2: the store test that nothing is remembered, and the shell test that the remembered team is kept | `red-15-way-out-stored-as-a-pick.txt` |

Over real HTTP, the routes that carry the team in the address (`/teams/{teamId}/...`, called with the person's own key) run inside a team today, so for them the Collaborator's refusal is proven through the real pipeline: every such route whose action the role table does not give a Collaborator - today the three invitation writes behind the invite page - answers the gate's 403 (`Issue2306_OverTheWire_ACollaborator_EveryTeamRouteTheTableRefusesThem_Is403`, break 5).

For the routes that take the tenant from the key, the refusal is proven against the host's own installed gate over its real route table: the hand list of the data behind every page a Collaborator cannot open (`Issue2306_ACollaboratorInTheTeam_TheDataBehindEveryPageTheyCannotOpen_IsRefused`, break 4), and every endpoint outside `/teams/{teamId}/` that the table does not give a Collaborator (`Issue2306_ACollaboratorInTheTeam_EveryEndpointOutsideTheTeamRoutes_IsRefused`, review F8).

What this record does NOT prove: role refusal over the wire on the tenant-from-key routes. A key bound to a team's tenant is refused by the hosted device registry before the role table is asked, so `Issue2306_OverTheWire_AKeyBoundToTheTeam_...` is a tripwire that no request from inside a team gets the data today, not a refusal proof - it stayed green under break 4. That proof waits on team-bound keys from #2311 (devthrottle#3530) and will be made in #2310. `EachTeamPageAddress_IsServedTheCockpit` proves the pages are served, not refused.

## Screenshots

Real renders: `take-screenshots.py` builds the Cockpit, starts `TeamCollaboratorAppProofRig` (a hosted Gateway on 127.0.0.1 with Teams released, one account - docs@mindzie.com, a fleet test account - that is a Collaborator in "DevThrottle" and a Developer in "Paul's project", whose only device is a browser, so it has no Director), and drives a headless browser that has never chosen a team. Nothing is emailed and the database is a scratch one.

Desktop (1280 px):
- `desktop-0-fresh-browser-chooser.png` - the first arrival: two teams and no Director, so the Gateway says choose; who is signed in and Sign out at the rail foot.
- `desktop-1-questions.png`, `desktop-2-requests.png`, `desktop-3-reports.png` - after Open DevThrottle: the Collaborator's three pages, with name, role and Sign out at the rail foot.
- `desktop-4-typed-sessions-not-available.png`, `desktop-5-typed-skills-not-available.png` - typed addresses.
- `desktop-6-developer-team-full-app.png` - the same account switched to the team where it is a Developer.
- `desktop-7-own-account-full-app.png` - the own account: today's Cockpit.

Phone width (390 px): `phone-390-0-fresh-browser-chooser.png`, `phone-390-1-questions.png`, `phone-390-2-requests.png`, `phone-390-3-reports.png`, `phone-390-4-typed-sessions-not-available.png`, `phone-390-6-developer-team-full-app.png`.

The phone-width shots are a 390 px browser window with a desktop browser's identity. A real phone reaches the same pages through the exemption above, proven over real HTTP with an iPhone browser identity, signed in and signed out, in `gateway-tests-teams-and-mobile-redirect.log`. While Teams is dark a phone goes to the mobile app exactly as before.

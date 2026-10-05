# Proof: a dev report sent to a member of the team, and comments that go to its author (devthrottle_internal#2309)

Run on 2026-10-05 in the worktree `devthrottle-teams-2309`, branch `teams/2309-reports`, cut from `origin/main` at b7e12f84b and rebased onto `origin/main` at ba08a4c87 (message links between sessions, #3549 and #3555, and the Mentor's page part 2, #3542). The rebase moved this change's migration after main's new `AddFleetMessageLinks` and regenerated it on that model.

## What was built

- **The author is recorded at publish.** In a team's tenant a report belongs to a PERSON: the one the session's Director key was issued to, answered by the one resolver (`TeamCallerOwnership.OwnerOf`, #2311). `DevReportAuthor.Resolve` asks it before the body is read; a team session nobody can be named behind is refused (403 `report_author_unknown`) and publishes nothing. `DevReportAuthor.PublishAs` is the one way the route then writes, under that author. A personal account records no author. The author never changes on a later version.
- **Sending (piece 1 for #2307: `DevReportRecipients`).** `POST /teams/{teamId}/reports/mine/{reportId}/recipients` with `{memberIds}`, from the Cockpit, account token only. All or nothing: someone who is not a member of this team, the author themselves, an empty list, or a member whose role may not read reports refuses the whole send and nothing is stored. The route is declared in `TeamEndpointRules` as `CallersOwn` with "watch" for anyone else's, so `TeamAccess.Decide` plus the one resolver refuse a report the caller did not write. The store keeps who it went to, who sent it, when, and when it was first read; a repeated send changes nothing.
- **Reading (screen S10).** `GET /teams/{teamId}/reports/sent-to-me` lists only the reports sent to the caller, newest first, from whom, when, "New" or "Read". `GET .../sent-to-me/{id}` and `.../sent-to-me/{id}/html` answer only for a report sent to the caller; any other id is 404 however it is asked for. Opening one marks it read. The page opens it in the SAME shared viewer (`DevReportViewer`, the same frame host and trust rules, CONTRACT section 4), fed from the team route by a new optional `api` prop - no second viewer. The html route is the owner's html path (`DevReportEndpoints.ServeHtml`), extracted, not copied.
- **Commenting (piece 2 for #2307: `DevReportPersonComments`).** `POST .../sent-to-me/{id}/comments` stores the words for the report's author person. A separate table, written by nothing else and read by nothing else: never a dev report item, never a reply, never handed to `DevReportDelivery`, never in a prompt, and no route a session key or a Director key reaches serves it. The author reads them on their own report (`GET .../mine/{id}`). The page says above the box, in the Gateway's words, that comments go to the author and are never shown to an agent.
- **The author's view.** Below the reports sent to them, a person who runs sessions in the team sees "Your reports in this team": each with "Sent to N people" and its comment count; opened, the same viewer with "Comments from people", "Sent to" with read state, and "Send to" checkboxes for the members it can still go to. Whether that part is shown is the Gateway's `showYourReports`, never the page's reading of the role.
- **The rail (Tech Lead ruling, from #2306 review F12).** In a team where the person gets the whole app, the rail now also lists the team's pages from the Gateway's verdict, so a Developer reaches `/reports`. With no team on screen the rail is unchanged.
- **The address stays `/reports`.** A report opens in place (`?report=<id>`, `?yours=<id>`); `GET /reports/repositories-weekly` is untouched.
- **Dark.** Every route above is mapped only inside the Teams switch. A person with no team sees no change.

## The privacy check

Question from the brief: can a member of a team tenant read every report through `/dev-reports`? **No.** The owner routes `/dev-reports`, `/dev-reports/{id}`, `/dev-reports/{id}/html` and the replies route state no action in `TeamEndpointRules`, so inside a team's tenant the gate refuses them for every role (deny by default). From a person's own key they answer only that person's own tenant. Proven by `Issue2309_PrivacyCheck_ADeveloperCannotReadAnotherDevelopersReports` (over the wire) and `Rules_TheOwnersDevReportRoutes_AndAWriteToTheMineList_StateNoAction` (unit). Nothing needed fixing.

## A gap recorded, not built around

A key bound to a team's tenant (#2311) authenticates and is then refused by the request-path access lease with 402, because the lease reads a personal account's bill. So no team session can publish or read a report over the wire today. `Issue2309_TeamKeyVariant_TheTeamsOwnSessionAndDirectorKeys_AreStoppedAtTheLease` records the 402. The tests therefore seed team reports through the store with the author recorded, exactly as the publish route records it, and `PublishAs_TheResolvedAuthor_IsTheOneRecorded_AndAPersonalAccountRecordsNone` proves the route's write path with the real resolver. The live-session comment proof runs on a personal tenant, where a session is real.

## The checks (counts)

| Check | Result | File |
|---|---|---|
| `.\scripts\test-local.ps1` (the default gate) | 10 of 10 suites outcome Completed, 3,601 tests, 0 failed. (A first run reported Core.UnitTests as "no result file" while its own log and result file both said 1,228 passed - the gate read the file before it was written; the rerun is clean.) | `checks/default-gate.txt` |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams\|FullyQualifiedName~DevReport"` | 159 of 159 executed, all passed, outcome Completed | `checks/gateway-teams-devreport.txt` |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "...Teams\|...DevReport\|...Migration\|...BootSmoke"` | 1,260 passed, 0 failed, 6 skipped (proof rigs and the tests that need a hosted database) | `checks/unit-teams-devreport-migration.txt` |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres\|FullyQualifiedName~Migration"` (a throwaway PostgreSQL) | 53 passed, 0 failed, 4 skipped (the live hosted-database proofs, which need `CC_GATEWAY_DB_CONNECTION`); 57 total, 53 executed, outcome Completed | `checks/gateway-postgres-migration.txt` |
| Cockpit web tests (`apps/cockpit`, `npx vitest run`) | 77 files, 755 tests passed | `checks/cockpit-vitest.txt` |
| client-core web tests (`packages/client-core`, `npx vitest run`) | 148 files, 1,773 tests passed | `checks/client-core-vitest.txt` |
| `tsc --noEmit` for cockpit and client-core | clean | (exit 0) |

**Found by the PostgreSQL run and fixed here.** `Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres` lists every column pinned to the "C" collation. It needed this change's five person columns - and it was ALREADY red on main: #3549 pinned five `fleet_message_links` columns and did not list them. Both are added. The parked `-Parked` run is the Tech Lead's to start, as the brief says.

## The red record

Each break was made in one file by `red-proof.py <name>`, the guarding tests run, and the file restored from the commit in a `finally` block, then checked with `git diff --quiet`. The records were taken on the branch before the rebase (6656c9539 and earlier); the rebase changed only migration ids and pins, none of the mutated lines.

| Break | Tests that went red | File |
|---|---|---|
| A report not sent to the caller is served (`IsSentTo` skipped) | unit `Issue2309_AReportNotSentToTheCaller_IsNotFound_OnEveryRecipientRoute`; hosted `..._AReportNotSentToACollaborator_IsRefused_OnTheListTheMetadataAndTheHtml` and the privacy check | `red-not-sent-is-served.txt` |
| A comment is also written as a reply in the agent's conversation | hosted `Issue2309_ACollaboratorsComment_ReachesTheAuthor_OverTheWire` and `Issue2309_ACommentNeverReachesTheSession_LiveSessionOnTheTunnel` | `red-comment-also-a-reply.txt` |
| A comment is also queued as an item toward the session | the same two hosted tests | `red-comment-also-an-item.txt` |
| The resolver answers "the caller's own" for every report | unit `Whose_...` and `Gate_FromTheirOwnAccount_...`; hosted `..._SendingAReportYouDidNotWrite_IsRefusedByTheGate` and the privacy check | `red-report-owner-always-the-caller.txt` |
| A recipient who is not a member is skipped instead of refusing the send | unit `Send_ToSomeoneWhoIsNotAMemberOfThisTeam_IsRefused_AndNothingIsSent`; hosted `..._SendingToSomeoneWhoIsNotAMember_IsRefused_AndNothingIsSent` | `red-non-member-skipped-silently.txt` |
| The sent-to-me rule is removed from `TeamEndpointRules` | 5 unit (the rule theory and two gate tests) and 8 hosted | `red-sent-to-me-rule-removed.txt` |
| The routes are mapped outside the Teams switch | all 6 dark tests, including `SwitchUnset_TheTeamReportRoutesAreAbsent_...` (why all six: see the file) | `red-routes-mapped-while-dark.txt` |
| The publish writes no author | unit `PublishAs_TheResolvedAuthor_IsTheOneRecorded_AndAPersonalAccountRecordsNone` (the first attempt, against the route line, went GREEN - no test watched it; `PublishAs` and its test were added, and the second attempt went red) | `red-author-not-recorded.txt` |
| The page shows your own reports whatever the Gateway says | `List_TheGatewaySaysNoReportsOfYourOwn_ShowsNoneAndAsksForNone` | `red-cockpit-ignores-show-your-reports.txt` |
| The viewer ignores its `api` prop (reads the owner's routes) | `Open_StaysOnThePageAddress_AndUsesTheSharedViewerFedFromTheRecipientsRoute` | `red-viewer-ignores-the-api.txt` |
| The whole-app rail lists no team pages | 3 Cockpit tests, including `WholeAppTeam_Developer_TheRailAlsoListsTheTeamsPages_InTheGatewaysOrder` | `red-rail-without-team-pages.txt` |

## Screenshots (real renders, a seeded team)

`python docs/proof/teams-2309/take-screenshots.py` builds the Cockpit, starts `TeamReportsProofRig` (a hosted Gateway on 127.0.0.1:7913 with Teams released; team "DevThrottle": qa@mindzie.com owns it, tech@mindzie.com is a Developer who wrote "Signup page rewrite" and sent it to docs@mindzie.com, a Collaborator, who opened it and commented) and drives a headless browser as each person at 1280 x 800 and at 390 x 844. Seeding goes through the stores, for the 402 reason above; everything the browsers show is read over the wire.

| Screen | Desktop | 390 px |
|---|---|---|
| S10: the Collaborator's Reports list | `desktop-1-collaborator-reports-list.png` | `phone-390-1-collaborator-reports-list.png` |
| An opened report with the Collaborator's comment | `desktop-2-collaborator-report-open-with-comment.png` | `phone-390-2-...png`, `phone-390-2b-collaborator-comment-panel.png` |
| The author's Reports page | `desktop-3-author-reports-list.png` | `phone-390-3-author-reports-list.png` |
| The author's report with the comment it received | `desktop-4-author-report-with-comment-received.png` | `phone-390-4-...png`, `phone-390-4b-author-comment-panel.png` |

At 390 px the Collaborator's pages are the pages-only shell (a bar across the top). The author is in the whole Cockpit, whose rail does not fold itself away at phone width; the driver folds it with the rail's own button, as a phone reader would.

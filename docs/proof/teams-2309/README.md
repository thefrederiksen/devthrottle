# Proof: a dev report sent to a member of the team, and comments that go to its author (devthrottle_internal#2309)

Run on 2026-10-05 in the worktree `devthrottle-teams-2309`, branch `teams/2309-reports`, on `origin/main` at 16d267109 (the v2.15.0 release). Main includes the team requests (#3556, devthrottle_internal#2308) and a session's request for a message link (#3560); each added a migration, so this change's migration was regenerated after them, on that model. The first review's answers (`review-2309.md`, F1 to F13) and the delta review's (`review-2309-delta.md`, D1 to D10) are built in.

## What was built

- **The author is recorded at publish.**
  - In a team's tenant a report belongs to a PERSON: the one the session's Director key was issued to. That person is answered by the one resolver, `TeamCallerOwnership.OwnerOf` (#2311).
  - `DevReportAuthor.Resolve` asks the resolver before the body is read. A team session that nobody can be named behind is refused (403 `report_author_unknown`) and publishes nothing.
  - `DevReportAuthor.PublishAs` is the only way the route then writes, under that author.
  - A personal account records no author, and the author never changes on a later version.
- **Sending (piece 1 for #2307: `DevReportRecipients`).**
  - `POST /teams/{teamId}/reports/mine/{reportId}/recipients` with `{memberIds, version}`, from the Cockpit, account token only.
  - **The send carries the version the page was showing (delta review D2).** The Gateway sends exactly that version. When the session has published a newer one meanwhile, nothing is sent: the answer is 409 `version_not_newest` with the Gateway's sentence, and the page shows it and reads again, so the author reads the newer version before sending it. A person is never sent a version its author did not see.
  - All or nothing. These each refuse the whole send, and nothing is stored: someone who is not a member of this team, the author themselves, an empty list, or a member whose role may not read reports.
  - **A send covers the version sent (review F1).** The recipient row records `SentVersion`, and that version is the only one the recipient reads.
  - A later version reaches them only when the author sends again. Sending again moves their row forward and marks it unread. An earlier version never reaches them, and never moves a row back: the move is one conditional update (`where SentVersion < version`), so two sends across a publish cannot move a person back whichever lands last (delta review D7).
  - The author's page shows which version each person holds ("Has version 1 of 2"). A member who holds an older version is offered again, with what sending does for them.
  - The members offered are the ones `TeamAccess.Decide` lets read reports (review F12).
- **Reading (screen S10).**
  - `GET /teams/{teamId}/reports/sent-to-me` lists only the reports sent to the caller: newest first, from whom, when, and "New" or "Read".
  - `GET .../sent-to-me/{id}` and `.../sent-to-me/{id}/html` answer only for a report sent to the caller. Any other id is 404, however it is asked for.
  - The html route serves the version the caller was sent. A `?version=` naming any other version is 404 with the Gateway's sentence. This is watched over the wire, not only on the class behind the route (delta review D3).
  - **Opening a version marks that version read (delta review D5).** The read request names the version the page showed, and the Gateway marks the row only when that is the version held. A read for an older version, landing after the author sent a newer one, is 409 `version_not_held` and marks nothing. The page remembers the version it marked, not merely that it marked one, so a version sent while the report is open is marked once it is shown.
  - The page opens it in the SAME shared viewer (`DevReportViewer`: the same frame host and trust rules, CONTRACT sections 4 and 5), fed from the team route. There is no second viewer.
- **No notes or answers for a team reader (review F2).**
  - Both detail answers carry `notesOpen: false`, a flag the Gateway sends.
  - The page opens the viewer only after that answer and passes it through. With the flag off, the viewer loads no notes script into the frame, so there is no notes tray, no queue button and no answer control.
  - A question in a report reads as the report's own markup. The screenshots show one.
  - The page never decides this for itself.
- **Commenting (piece 2 for #2307: `DevReportPersonComments`).**
  - `POST .../sent-to-me/{id}/comments` stores the words for the report's author person.
  - The comments are a separate table, written by nothing else and read by nothing else. A comment is never a dev report item and never a reply. It is never handed to `DevReportDelivery`, never put in a prompt, and served by no route that a session key or a Director key reaches.
  - The author reads them on their own report (`GET .../mine/{id}`).
  - **Comments close when the author can no longer read them (review F6).** The Gateway decides: the author must still be allowed to run sessions in the team.
    - When the author has left the team, or has been made a Collaborator, the answer has `canComment: false` and the Gateway says why.
    - The page shows that sentence and no comment box. A comment sent anyway gets 409 `author_cannot_receive` and is not stored.
- **The author's view.**
  - Below the reports sent to them, a person who runs sessions in the team sees "Your reports in this team". Each shows "Sent to N people" and its comment count.
  - Opened, it shows the same viewer with "Comments from people", then "Sent to" with read state and version held, then "Send to".
  - The list is read by author through an index on (tenant, author) (review F7). The received list is one query, joined to the version each person holds.
  - Whether that part is shown at all is the Gateway's `showYourReports`, never the page's reading of the role.
- **The rail (Tech Lead ruling, from the #2306 review, F12).** In a team where the person gets the whole app, the rail also lists the team's pages from the Gateway's verdict, so a Developer reaches `/reports`. With no team on screen the rail is unchanged.
- **The address stays `/reports`.** A report opens in place (`?report=<id>`, `?yours=<id>`). `GET /reports/repositories-weekly` is untouched.
- **Dark.** Every route above is mapped only inside the Teams switch, so a person with no team sees no change.

## The migration

`AddDevReportSharing`: SQLite `20261005170254`, PostgreSQL `20261005170330`. It sorts after `AddFleetMessageLinkRequests`.

- **Up** adds:
  - the nullable `dev_reports.AuthorSubject` and its index on (tenant, AuthorSubject)
  - the `dev_report_recipients` table, including `SentVersion`, with three indexes (one row per report and recipient)
  - the `dev_report_comments` table, with two indexes
- **Down** removes all of it.
- To reverse it, migrate to `AddFleetMessageLinkRequests`. Old reports survive: the PostgreSQL test proves it on a real database.
- It was applied to local and test databases only.

## The privacy check

The brief asked whether a member of a team tenant can read every report through `/dev-reports`. **No.**

- These owner routes state no action in `TeamEndpointRules`: `/dev-reports`, `/dev-reports/{id}`, `/dev-reports/{id}/html` and the replies route. So inside a team's tenant the gate refuses them for every role, deny by default.
- From a person's own key, they answer only that person's own tenant.
- Proven by `Issue2309_PrivacyCheck_ADeveloperCannotReadAnotherDevelopersReports` (over the wire) and `Rules_TheOwnersDevReportRoutes_AndAWriteToTheMineList_StateNoAction` (unit). Nothing needed fixing.

## The keys bound to a team's tenant (review F4)

`Issue2309_F4_KeysBoundToTheTeamsTenant_AreRefusedByTheTeamReportRoutesOwnAdmission_WhateverTheLeaseDoes` gives the report routes' own admission a team-bound device key and a team-bound session key. Each is resolved through the real registries, as if the lease had let it pass, and each is refused 403 with the admission's own reason (`TeamEndpoints.NotAPersonalAccountRefusal`).

So a session key, or a key bound to the team's tenant, cannot reach these routes, and that does not depend on the lease. Every mapped route is also refused to a session key over the wire. That route list is taken from the mapped route table, not typed out (review F5).

## Gaps recorded, not built around

1. **A team session's publish has not been proven over the wire (review F3).**
   - A key bound to a team's tenant (#2311) authenticates. The request-path access lease then refuses it with 402, because the lease reads a personal account's bill.
   - A grant made for a test does not stay in force: the 60-second `EntitlementLeaseMonitor` sweep revokes it and tombstones the team's keys. So the publish route's team branch cannot be driven below the lease today. `Issue2309_TeamKeyVariant_TheTeamsOwnSessionAndDirectorKeys_AreStoppedAtTheLease` records the 402.
   - `PublishAs_TheResolvedAuthor_IsTheOneRecorded_AndAPersonalAccountRecordsNone` proves that the resolver's answer is what gets written. It does NOT prove the route over the wire.
   - **The test to write when the lease reads the team's bill:** `Issue2309_TeamSession_PublishesOverTheWire_RecordsTheDirectorsPersonAsAuthor_AndAnUnnamedSessionIs403`, in `HostedTeamReportsTests`.
   - Until then, the tests seed team reports through the store with the author recorded, exactly as the publish route records it. The live-session comment proof runs on a personal tenant, where a session is real.
2. **A team report's author cannot send notes or answers to the session that wrote it (review F2, delta review D4).**
   - On the author's own report the Gateway also sends `notesOpen: false`, and the owner's `/dev-reports/{id}/send` is refused inside a team's tenant. So there is no screen from which a Developer answers their own agent's questions on a team report.
   - A team report that asks its author a question therefore draws the question as plain text with no way to answer, under a status bar that reads "waiting-on-you", while the session that published it waits for an answer no screen can give.
   - It does not bite today, because no team session can publish (gap 1). **It must be closed - or the status word changed for the author - before team sessions publish.** Whoever lifts the lease needs to know this.
3. **The author has no way to reply to a comment.** A comment reaches the author and is shown on their report, but nothing lets the author answer the person who wrote it. A separate gap from gap 2, left for a later piece of work.

## The checks (counts)

**On the head after the second rebase (onto 3c8808ec6).** That rebase changed only the migration, its pins and the collation list, and these were rerun on it:

- the Gateway unit tests (Teams, DevReport, Migration, BootSmoke): 1,397 passed, 0 failed, 7 skipped
- `-Gateway -Filter "FullyQualifiedName~Postgres|FullyQualifiedName~Migration"`: 54 passed, 4 skipped, outcome Completed
- `-Gateway -Filter "FullyQualifiedName~DevReport|FullyQualifiedName~Teams.HostedTeamReports"`: 48 of 48 passed

The other hosted Teams tests (123) were queued twice behind a release gate in another worktree, which holds the machine-wide Gateway test lock, and recorded no result before the ten-minute foreground limit. Their last result, on the code before this rebase, is in the table below. In the table, the default gate, the other hosted Teams tests, the web tests and the type checks are from that earlier head; the three rows above were retaken here, with the same counts.

| Check | Result | File |
|---|---|---|
| `.\scripts\test-local.ps1` (the default gate) | 10 of 10 suites outcome Completed, 3,601 tests, 0 failed | `checks/default-gate.txt` |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport\|FullyQualifiedName~Teams.HostedTeamReports"` | 48 of 48 executed, all passed, outcome Completed | `checks/gateway-devreport.txt` |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Teams&FullyQualifiedName!~HostedTeamReports"` | 123 of 123 executed, all passed, outcome Completed. The script exits 5 here because its matched-nothing check reads an `&` filter as one test name and finds none; the result file shows 123 executed. The filter was split in two so that each half stays under the ten-minute foreground limit. | `checks/gateway-teams.txt` |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "...Teams\|...DevReport\|...Migration\|...BootSmoke"` | 1,397 passed, 0 failed, 7 skipped (six proof rigs, and one proof that needs a configured hosted database) | `checks/unit-teams-devreport-migration.txt` |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~Postgres\|FullyQualifiedName~Migration"` (a throwaway PostgreSQL) | 54 passed, 0 failed, 4 skipped (the live hosted-database proofs, which need a configured server); 58 total, outcome Completed | `checks/gateway-postgres-migration.txt` |
| Cockpit web tests (`apps/cockpit`, `npx vitest run`) | 78 files, 771 tests passed | `checks/cockpit-vitest.txt` |
| client-core web tests (`packages/client-core`, `npx vitest run`) | 149 files, 1,778 tests passed. A first run had one failure, in `src/sessions/VerdictPanel.test.tsx`, which this change does not touch. It passed three times on its own and in the full rerun recorded here. | `checks/client-core-vitest.txt` |
| `tsc --noEmit` for cockpit, client-core and mobile | clean | `checks/tsc.txt` |

**Found by the PostgreSQL run and fixed here.** `Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres` lists every column pinned to the "C" collation. It needed this change's five person columns, and main had left it red twice: #3549 pinned five `fleet_message_links` columns, and #3560 pinned four `fleet_message_link_requests` columns, and neither listed them. All three sets are added. The `-Parked` run is the Tech Lead's to start, as the brief says.

**Also repaired here (review F8).** The boot smoke test's pins now include main's Mentor migration.

## The red record

`red-proof.py <name>` makes each break in one file, runs the tests that guard it, and restores the file from the commit in a `finally` block, then checks it with `git diff --quiet`.

- The first eleven records below were taken before the review fixes.
- The resolver record was retaken after the first rebase, because that rebase merged main into the file that break mutates.
- The six delta review records (D2, D3, D5) were taken on the delta review's code.
- Every one of the 20 mutation targets matches exactly once on the head.

| Break | Tests that went red | File |
|---|---|---|
| A report not sent to the caller is served (no recipient row needed) | unit `Issue2309_AReportNotSentToTheCaller_IsNotFound_OnEveryRecipientRoute` and the F1 test; 2 hosted (`..._AReportNotSentToACollaborator_IsRefused_...` and the privacy check). Retaken on the review's code. | `red-not-sent-is-served.txt` |
| The recipient reads the latest version, not the one sent (F1) | unit `Issue2309_F1_TheRecipientReadsTheVersionSent_NeverAnEarlierOne_AndALaterOneOnlyWhenSentAgain` | `red-recipient-reads-the-latest-version.txt` |
| Comments stay open whoever the author now is (F6) | unit `Issue2309_F6_...`, both cases ("left the team", "made a Collaborator") | `red-comments-open-whoever-the-author-is.txt` |
| The viewer ignores the Gateway's notes flag (F2) | 2 Cockpit tests, including `Open_TheGatewaySaysNotesAreOff_TheViewerGetsNoNotesScript_...` | `red-viewer-ignores-the-notes-flag.txt` |
| A team-bound key is admitted as a person (F4) | hosted `Issue2309_F4_...` (the admission named the team instead of refusing) | `red-team-key-admitted-as-a-person.txt` |
| The Gateway sends whatever version is current, not the one the page showed (D2) | unit `Issue2309_D2_ASendOfAVersionThatIsNoLongerTheNewest_IsRefusedWithTheGatewaysSentence_AndSendsNothing` | `red-send-ignores-the-version-shown.txt` |
| The page's send names a fixed version, not the one shown (D2) | 2 Cockpit tests: `Send_SendsExactlyTheChosenMembers` and `Send_RefusedBecauseANewerVersionArrived_...` | `red-page-sends-a-fixed-version.txt` |
| The html ROUTE is put back to the owner's `ServeHtml`, serving the newest version (D3, the review's own mutation) | HTML_ROUTE_RED | `red-html-route-serves-the-newest.txt` |
| A read marks the row whatever version it names (D5) | unit `Issue2309_D5_AReadNamesTheVersion_AndOnlyTheVersionHeldIsMarked_...` | `red-read-marks-any-version.txt` |
| The page marks once per open, not once per version (D5) | Cockpit `Open_AVersionSentWhileOpen_IsMarkedWhenShown_AfterTheGatewayRefusedTheOlderOne` | `red-page-marks-once-per-open.txt` |
| A comment is also written as a reply in the agent's conversation | hosted `Issue2309_ACollaboratorsComment_ReachesTheAuthor_OverTheWire` and `Issue2309_ACommentNeverReachesTheSession_LiveSessionOnTheTunnel` | `red-comment-also-a-reply.txt` |
| A comment is also queued as an item toward the session | the same two hosted tests | `red-comment-also-an-item.txt` |
| The resolver answers "the caller's own" for every report | unit `Whose_...` and `Gate_FromTheirOwnAccount_...`; hosted `..._SendingAReportYouDidNotWrite_IsRefusedByTheGate` and the privacy check. Retaken after the rebase. | `red-report-owner-always-the-caller.txt` |
| A recipient who is not a member is skipped instead of refusing the send | unit `Send_ToSomeoneWhoIsNotAMemberOfThisTeam_IsRefused_AndNothingIsSent`; hosted `..._SendingToSomeoneWhoIsNotAMember_IsRefused_AndNothingIsSent` | `red-non-member-skipped-silently.txt` |
| The sent-to-me rule is removed from `TeamEndpointRules` | 5 unit (the rule theory and two gate tests) and 8 hosted | `red-sent-to-me-rule-removed.txt` |
| The routes are mapped outside the Teams switch | all 6 dark tests, including `SwitchUnset_TheTeamReportRoutesAreAbsent_...` (why all six: see the file) | `red-routes-mapped-while-dark.txt` |
| The publish writes no author | unit `PublishAs_TheResolvedAuthor_IsTheOneRecorded_AndAPersonalAccountRecordsNone`. The first attempt, against the route line, went GREEN: no test watched that line. `PublishAs` and its test were added, and the second attempt went red. | `red-author-not-recorded.txt` |
| The page shows your own reports whatever the Gateway says | `List_TheGatewaySaysNoReportsOfYourOwn_ShowsNoneAndAsksForNone` | `red-cockpit-ignores-show-your-reports.txt` |
| The viewer ignores its `api` prop (reads the owner's routes) | `Open_StaysOnThePageAddress_AndUsesTheSharedViewerFedFromTheRecipientsRoute` | `red-viewer-ignores-the-api.txt` |
| The whole-app rail lists no team pages | 3 Cockpit tests, including `WholeAppTeam_Developer_TheRailAlsoListsTheTeamsPages_InTheGatewaysOrder` | `red-rail-without-team-pages.txt` |

## Screenshots (real renders, a seeded team)

`python docs/proof/teams-2309/take-screenshots.py` takes the screenshots; they were retaken on the rebased commit.

- It builds the Cockpit and starts `TeamReportsProofRig`: a hosted Gateway on 127.0.0.1:7913 with Teams released.
- The rig seeds one team, "DevThrottle":
  - qa@mindzie.com owns it.
  - tech@mindzie.com is a Developer who wrote "Signup page rewrite". The report includes a question.
  - tech sent it to docs@mindzie.com, a Collaborator, who opened it and commented.
- A headless browser then drives each person at 1280 x 800 and at 390 x 844.
- Seeding goes through the stores, for the 402 reason above. Everything the browsers show is read over the wire.
- The driver fails if the report's frame holds any button. With notes off, the frame holds only the report's own markup, and the question shows as plain options.

| Screen | Desktop | 390 px |
|---|---|---|
| S10: the Collaborator's Reports list | `desktop-1-collaborator-reports-list.png` | `phone-390-1-collaborator-reports-list.png` |
| An opened report with the Collaborator's comment | `desktop-2-collaborator-report-open-with-comment.png` | `phone-390-2-...png`, `phone-390-2b-collaborator-comment-panel.png` |
| The author's Reports page | `desktop-3-author-reports-list.png` | `phone-390-3-author-reports-list.png` |
| The author's report with the comment it received | `desktop-4-author-report-with-comment-received.png` | `phone-390-4-...png`, `phone-390-4b-author-comment-panel.png` |

- At 390 px the Collaborator's pages are the pages-only shell, with a bar across the top.
- The author is in the whole Cockpit, whose rail does not fold itself away at phone width, so the driver folds it with the rail's own button, as a phone reader would.
- **Observed, not explained.** Twice in about six runs today, the driver stopped with "the report's own bytes never rendered" and an empty frame list. One of those was before the review changes. A rerun passed both times. The cause is not known.

# Teams 2307: Collaborator questions - proof

thefrederiksen/devthrottle_internal#2307. Screen S8: the questions waiting on a Collaborator, each with its options, one
recommended, and an optional comment.

## What was built

- **The Questions page** (`/questions`) lists the questions in reports sent to the reader, read by the Gateway from the
  stored HTML of the version that person holds. Each question shows its options with the recommended one picked, a
  comment box, and the Gateway's sentence: *"Your words go to {person who asked}. Only your choice reaches the agent."*
  Answered questions move to an Answered list that shows the choice, whether it was delivered or is held, and the words
  read back to their writer only.
- **An answer is taken in one database write** (`DevReportStore.AddMemberAnswer`, review round 1, F2 and F3). In one
  transaction it holds the person's own recipient row, but only while that row still holds the version the question was
  read from. It then refuses a second answer by the same person to the same question, and stores the held choice and the
  person's words together. A unique index beneath that check makes two answers by one person impossible even across
  Gateway processes.
- **The choice goes to the session that asked** through `DevReportDelivery`'s ordinary settle pass, after the answer is
  stored. It is held while the session works, delivered when the session is idle, and delivered at most once. If that
  pass fails, the answer is still taken and the words are already with the person (F4). The prompt says the answer came
  from "a person this report was sent to", not from the owner. It names the version the person answered and that
  version's title, never a newer one (F1).
- **The comment goes to a person.** It goes to the report's author through #2309's person-only comments, carrying the
  question it is about. It is never part of the delivered item. The store and the prompt fold both refuse a member's
  item that carries any words of the person's own.
- **Only the named recipient sees and answers a question.** Anyone else gets "not found", and nothing is stored.
- **Two endpoints, both under `/teams/{teamId}/questions`:**
  - `GET` lists the questions.
  - `POST /{reportId}/{questionId}/answer` answers one.
  - Both are declared in `TeamEndpointRules` under "answer questions" for every role, and both are absent when
    `CC_GATEWAY_TEAMS` is off.
- **A report whose options break the value rule is now refused at publish (F5).** An option with no value, an empty
  value, or a value another option in the same question uses fails the shape check. CONTRACT.md and the shipped
  `dev-reports` skill say so. A stored version that breaks any question rule is a fault, never a silently dropped
  question.
- **The count beside Questions in the left menu (F8).** The Gateway's page descriptor names where each page's count is
  read (`countPath`, set only for Questions). The shell reads it every 45 seconds, on every route change, and the
  moment an answer is sent, and shows the Gateway's number. Zero shows nothing.
- **The Reports page**, on a report sent to the reader, carries the Gateway's line "N questions waiting on you - answer
  them on Questions", which links to the page. Its own answer controls stay off, as the Tech Lead ruled.
- **The author's report:**
  - Each comment carries "About {question} - chose {option}".
  - The Sent to list says "Answered a question" for a person who answered without opening the report. The read mark
    itself is unchanged (Tech Lead ruling).

## The migration

`AddTeamQuestionAnswers` exists in two forms, one for SQLite (`20261006025431`) and a PostgreSQL twin (`20261006025508`).

**What it adds:**
- `dev_report_items.AnswererSubject`, nullable. It holds the team member who gave an answer. It is empty for the owner's
  own notes and answers, so every existing row is unchanged. On PostgreSQL it uses the "C" collation, like the other
  person columns.
- `dev_report_items.SourceVersion`, nullable. It holds the version a team member answered. It is empty for the owner's
  own items, which keep using the newest version.
- `dev_report_comments.QuestionId`, nullable. It holds the question a person's comment is about.
- The index `IX_dev_report_items_tenant_id_AnswererSubject`.
- The unique index `IX_dev_report_items_one_member_answer` on (tenant, report, answerer, question), filtered to rows with
  an answerer whose status is not `refused`. Every existing row has no answerer, so none is covered and the index cannot
  fail to build on existing data.

**How to reverse it:** migrate down to `AddDevReportSharing`. Its `Down` drops both indexes and the three columns. Existing
notes, answers and comments survive with their words, and `AddTeamQuestionAnswersPostgresTests` proves that on a real
PostgreSQL database.

## Checks (the outputs are in `checks/`)

| Check | Result |
|---|---|
| `.\scripts\test-local.ps1` (the default gate) | 10 of 10 suites Completed |
| `-Gateway -Filter "FullyQualifiedName~Teams"` | 152 of 152 executed, Completed; see note 3 |
| `-Gateway -Filter "FullyQualifiedName~DevReport"` | 37 of 37 executed, Completed |
| `-Gateway -Filter "FullyQualifiedName~Postgres\|FullyQualifiedName~Migration"` | 55 of 59 executed, Completed; see note 1 |
| `dotnet test src\CcDirector.Gateway.UnitTests` with the Teams, DevReport, Migration and BootSmoke filter | 1,537 passed, 8 skipped; see note 2 |
| Cockpit vitest | 81 files, 814 tests passed |
| client-core vitest | 151 files, 1,791 tests passed |
| `tsc` for the Cockpit, client-core and mobile | clean |

1. The four tests not executed are the live hosted-PostgreSQL proofs. They need `CC_GATEWAY_DB_CONNECTION` and were
   skipped before this change too.
2. The skipped tests in the unit run are the proof rigs, which run only when asked. One of them is this change's
   screenshot rig.
3. One fewer than before review round 1 (153): the personal-tenant live test was removed, as the tests section says.

The first attempt at these runs waited the full 45 minutes behind another session's test lock and was refused with no test
executed. That is not a result, and it is not counted. The runs above were made once the lock was free.

The Gateway run was split into these filters to keep each under ten minutes, as in #2309.

## The three required tests

1. **The chosen option reaches the session that asked, asserted on the prompt it receives.**
   - `TeamQuestionsTests.Issue2307_AnAnswerFromACollaborator_DeliversTheChosenOption_ToTheSessionThatAsked`
   - Over the wire, a live TEAM session on its team Director (the team pays), answered through the answer route:
     `HostedTeamQuestionsTests.Issue2307_TeamKeyVariant_AMembersAnswer_ReachesALiveTeamSessionOverTheWire_AndTheirCommentNever`
2. **The comment reaches a person and never a session.**
   - `TeamQuestionsTests.Issue2307_TheCollaboratorsComment_GoesToThePersonWhoAsked_AndIsNeverPlacedInAnySessionsInput`
   - `HostedTeamQuestionsTests.Issue2307_OverTheWire_MikesChoiceIsHeldForTheSessionThatAsked_AndHisWordsReachAlice_AndNoSessionRead`
   - `HostedTeamQuestionsTests.Issue2307_TeamKeyVariant_AMembersAnswer_ReachesALiveTeamSessionOverTheWire_AndTheirCommentNever`
   - How the tests prove it, with a unique marker:
     - They run the settle pass and a turn end.
     - The choice is in the prompt.
     - The marker is in no prompt, no message, no command and no reply toward any session, and in no read a session key
       can make.
     - The marker is in what the person who asked reads.
3. **A question addressed to a named Collaborator appears only for them.** Two Collaborators, over the wire:
   `HostedTeamQuestionsTests.Issue2307_OverTheWire_AQuestionSentToMike_AppearsForMike_AndForNoOtherCollaborator_WhoIsRefused`.

**Also covered:**
- Tenant isolation: `Issue2307_TenantIsolation_AnotherTeam_SeesAndAnswersNothingOfThisOne`.
- A session key is refused on both routes.
- The team-key variant: since main's #3552 a paying team's own session reaches the Gateway, so the test runs a live
  team session over the wire instead of recording the old 402.
- The routes are dark with the switch off: `HostedTeamsDarkTests.SwitchUnset_TheQuestionsRoutesAreAbsent_AndNoAnswerOrCommentIsStored`.
- The route-table walk.
- The rules rows.
- Refusals: wrong version, unknown question or option, a second answer, an ended session, an author who can no longer
  read comments, and a comment over the limit.
- Two people answering while the session works: neither answer replaces the other.
- The question parser.
- The prompt fold and the store guards.
- The migration on SQLite and on PostgreSQL, including a duplicate answer refused by the unique index.
- The page and client component tests.

**Added in review round 1:**
- F1: `TeamQuestionsTests.AnswerAsync_AnAnswerToTheVersionSent_IsDeliveredAsThatVersion_NotTheReportsNewest`.
- F2 and F3, run deterministically with a second store on the same database standing in for another process:
  `MemberAnswerDeliveryTests.AddMemberAnswer_ANewerVersionSentBetweenTheReadAndTheWrite_IsNotTaken`,
  `AddMemberAnswer_AnotherProcessTakesTheSamePersonsAnswerBetweenTheReadAndTheWrite_OnlyOneIsTaken`, and
  `TheMemberAnswerIndex_RefusesASecondNotRefusedAnswerByOnePerson_AndIgnoresARefusedOne`.
- F4: `TeamQuestionsTests.AnswerAsync_TheDeliveryFailsAfterTheAnswerIsTaken_TheWordsStillReachThePerson_AndTheChoiceGoesExactlyOnce`.
- F5: `DevReportShapeCheckTests.Check_AnOptionWithNoValue_Fails`, `Check_TwoOptionsWithOneValue_Fails`, and
  `DevReportQuestionsTests.Read_AQuestionTheShapeCheckRefuses_IsAFault_NeverADroppedQuestion`.
- F6: `TeamReportsTests.TeamReports_NullDependencies_Throw` covers a missing `TeamQuestions`.
- F7: `questionsPage.test.tsx` `Poll_AHeldAnswerThatBecomesDelivered_ShowsTheGatewaysNewState` and
  `Poll_ANewerVersionOfTheQuestion_StartsAFreshCard_NoChoiceOrWordsCarriedOver`.
- F8: `TeamEndpointsTests.ListTeams_OnlyTheQuestionsPage_NamesWhereItsCountIsRead_InThatTeam`, the `countPath` and
  `getPageCount` tests in `teamsClient.test.ts`, and the five `Count_` tests in `collaboratorApp.test.tsx`. Those are the
  count shown beside Questions from the path the Gateway named, a whole-app team, read again after an answer, zero shows
  nothing, and no team reads nothing.
- Removed: the personal-tenant live test `Issue2307_AMembersAnswer_ReachesALiveSessionOnTheTunnel_AndTheirCommentNever`.
  It fed the answer through `DevReportDelivery.SendAsync`, which members no longer use. The live team-session test runs
  the same proof through the real answer route.

## Red records

Each record breaks one rule on purpose, runs the tests that guard it, and restores the committed source. To repeat one,
run `python docs/proof/teams-2307/red-proof.py <name>`. Every record below went red, on the
review round 1 code. The script refuses to record a run the Gateway test lock refused, or whose test host died, and leaves
the earlier record in place: one attempt this round was refused that way and wrote a false red, which was thrown away.

| Record | Rule broken | Tests red |
|---|---|---|
| comment-in-the-choice | the comment rides in the delivered item | 5 |
| store-takes-member-words | the store accepts a member's words | 2 |
| fold-takes-member-words | the prompt fold accepts a member's words | 3 |
| comment-to-the-session | the comment is written into the agent's conversation | 1 |
| comment-to-the-team-session-over-the-wire | a live team session can read the comment (over the wire) | 1 |
| comment-not-to-the-person | the comment never reaches the person who asked | 6 |
| not-addressed-is-answered | someone the report was not sent to can answer | 1 |
| version-not-checked | an answer to a version the person no longer holds is taken | 1 |
| answered-twice | the in-transaction "already answered" check is removed | 4 |
| ended-session-not-checked | an answer to an ended session is stored | 1 |
| comment-to-an-author-who-cannot-read | words are taken for an author who cannot read them | 1 |
| answers-replace-across-people | one person's answer replaces another's | 2 |
| member-answer-said-to-be-the-owners | the session is told the owner answered | 2 |
| viewer-answers-on | the Reports viewer's answer controls are turned on | 2 |
| questions-route-undeclared | the routes lose their rules row | 3 |
| answered-reads-not-read | the Sent to list says "Not read yet" after an answer | 2 |
| page-trims-the-words | the page trims the words before sending | 1 |
| page-ignores-can-comment | the page offers a comment box the Gateway refused | 1 |
| page-no-recommended-preselect | the recommended option is not picked | 5 |
| reports-page-hides-the-questions-line | the Reports page drops the questions line | 1 |
| f1-answer-named-as-the-newest-version | a member's answer is named by the report's newest version | 1 |
| f3-version-decided-before-the-write | the version is checked outside the write that takes the answer | 2 |
| f4-a-failed-send-fails-the-answer | a failed delivery after the answer is taken fails the answer | 1 |
| f5-publish-takes-an-option-with-no-value | publish accepts an option with no value or a repeated value | 3 |
| f6-reports-without-questions | the Reports collaborator is built without Questions | 1 |
| f7-card-keeps-its-first-answer | a card keeps the answer from its own send over the Gateway's newer state | 1 |
| f7-card-key-without-version | a newer version of a question reuses the old card | 1 |
| f8-cockpit-builds-the-count-path | the Cockpit builds the count path itself | 2 |
| f8-questions-names-no-count | the Gateway names no count path for Questions | 1 |
| f8-rail-count-stale-after-an-answer | the left menu's count is not read again after an answer | 1 |

The red-proof script restores by writing back the exact bytes it read, never through git. In this round a restore through
git failed on a git lock held by another process and left a mutation in place. The two results run on top of it were
thrown away and run again.

## Screenshots

These were taken with `python docs/proof/teams-2307/take-screenshots.py`, against the rig `TeamQuestionsProofRig`. The
rig is a hosted Gateway on 127.0.0.1 with Teams released. In it, tech@mindzie.com (a Developer) asked "Should the trial
be 14 days or 30?" in a report sent to docs@mindzie.com and dev@mindzie.com (both Collaborators).

| | Desktop, 1280 px wide | Phone, 390 px wide |
|---|---|---|
| The question waiting, recommended option picked | `desktop-1-question-waiting.png` (docs@) | `phone-390-1-question-waiting.png` (dev@) |
| A choice and a comment filled in | `desktop-2-question-filled-in.png` | `phone-390-2-question-filled-in.png` |
| The answered question | `desktop-3-question-answered.png` | `phone-390-3-question-answered.png` |
| The person who asked reads the comment, with what it is about | `desktop-4-author-received-the-comment.png` | `phone-390-4-author-received-the-comment.png` |

## Gaps

- **Closed: a team session can now be live.** The earlier gap (the access lease answered 402 for a team tenant) closed
  when main gained #3552, which reads a team's own bill. The team-key test now runs the whole delivery to a live team
  session over the wire. A team with no bill is still refused, by main's rule, not this change's.
- **The question parser runs on every list.** Each Questions or Reports list call parses each report's stored HTML. That
  is fine at today's sizes, but nothing caches the result.
- **An answer cannot be changed after it is sent**, and there is no way to withdraw a comment.
- **The author still cannot answer their own agent from a team report.** That is #2309's D4 and is unchanged.
- **The left menu's count is read on a timer**, every 45 seconds and on every route change. An answer sent on Questions
  reads it again at once, but a question that arrives from a new report shows in the menu only on the next read.
- **One client-core test flaked once under full load**: `devReportNotes.test.ts` "note-mode > tells the app what the
  reader has selected". It passed three times alone and on the full rerun. It is not in code this change touches.
- **The `dev-reports` skill change reaches agents only on the next Gateway deploy.** Until then an agent can still be
  told nothing about the value rule, and its publish is refused with the shape check's sentence.

# Missions First-Class - Phase 1 proof (the Gateway)

## What was built

- **The Mission record** (`src/CcDirector.Core/Sessions/Mission.cs`) gains `DocumentReportId`,
  `DocumentLinkedAt` and `Goal`. Missions live in `missions.json`, so there is no database migration: a record
  written before these fields reads as "no document, no goal", which is the truth. `MissionStore.SetDocument`
  links a report, replaces any earlier document (one document per Mission), sets the goal from the document
  every time, and sets the why only when the document has a why section. `MissionDto` carries the three fields.
- **Two new section markers**, `data-dev-report="why"` and `data-dev-report="goal"`, in the shape check
  (`DevReportShapeCheck.cs`) and the contract (`packages/client-core/src/devreports/CONTRACT.md`). Each is
  optional, at most one, after the questions section, and never inside another section.
- **Reading them** (`DevReports/MissionDocumentSections.cs`): plain text, parsed the way the shape check parses
  the page. Headings, hidden elements and anything a browser never renders are left out; each block is its own
  line; a list item starts with "- "; a section over 4,000 characters is cut there.
- **Publishing as a mission document** (`DevReports/MissionDocumentLinker.cs`, the publish route in
  `Api/DevReportEndpoints.cs`): the body may carry `"missionDocument": true`. The Mission is the one the
  Gateway's own records say the publishing session is attached to (live roster, then session history). The
  request names no Mission, so a session can only link its own. A session with no Mission, or with a Mission
  this account does not hold, is refused with 409 BEFORE the report is written, so nothing is published.
  The answer carries the link, the goal, the why, the report it replaced, and notes when a section is missing.
- **The button verdict** (`Missions/MissionButtonFold.cs`, contract `MissionButtonDisplay`), stamped in the
  shared fold `GatewayEndpoints.StampFleetRolesAndFold`, so the roster, the single-session read and the push to
  the Director all carry the same answer. It goes to the Director on `SetDisplayStateRequest.MissionButton`
  and is part of the push's change signature, so a rename or a new goal is pushed again.
  - Mission with a document: label = the Mission's name, colour key `mission`, tooltip
    "Mission: name / Goal: goal", address `/r/<report id>`.
  - Mission with no document yet: the name, colour key `mission-outline`, tooltip says there is no document,
    no address.
  - No Mission: "No mission", colour key `warning`, tooltip says the session has no Mission, no address.
- **`cc-dev-reports open <file> --mission-document`** sends the flag and prints the link. A Gateway that
  publishes without saying what it linked (one older than this change) is reported as a failure, never as a link.
- **`cc-devthrottle mission list`** shows a `document` column by default (the report id, or "none"), offers
  `goal` under `--fields`, and counts the missions with no document.

## The tests, and what each proves

`src/CcDirector.Gateway.UnitTests/Missions/MissionButtonFoldTests.cs`
- `For_MissionWithDocument_ShowsNameInMissionColourWithGoalTooltipAndAddress` - the first verdict, every field.
- `For_MissionWithDocumentButNoGoal_TooltipSaysTheGoalIsNotSet` - a document without a goal still opens, and the
  tooltip says the goal is missing rather than showing a blank.
- `For_MissionWithoutDocument_ShowsNameAsOutlineWithNoAddress` - the second verdict.
- `For_NoMission_ShowsYellowNoMissionWithNoAddress`, `For_EmptyMissionId_IsNoMission` - the third verdict.
- `For_RenamedMission_RecordNameWinsOverTheSessionsCachedName` - a rename shows at once.
- `For_MissionNotInTheStore_ReadsNoDocumentUnderTheSessionsName` - a Mission this Gateway cannot see opens nothing.
- `Stamp_NoMissionsRead_AssignsNullOverADirectorsEcho` - an echoed verdict never survives a fold.
- `StampFleetRolesAndFold_WithMissions_PutsEachVerdictOnItsRow` - the verdict reaches the rows through the REAL
  shared fold. Mutation check: with the stamp fed no missions, this test fails.
- `StampFleetRolesAndFold_NoMissionSource_StampsNoVerdict` - with nothing read, nothing is claimed.
- `MissionsForFold_ReadsEveryStateOfTheTenantsMissions` - ended missions are read too (a session can stay on
  one), and another account's are not.

`src/CcDirector.Gateway.UnitTests/Missions/MissionDocumentSectionsTests.cs`
- `Read_BothSectionsPresent_ReturnsEachAsPlainText`, `Read_NoMarkers_ReturnsNullForBoth`,
  `Read_OnlyGoal_WhyIsNullNotEmpty`, `Read_EmptySection_IsEmptyNotNull` - present, absent, and empty are three
  different answers.
- `Read_NestedMarkup_FlattensToLinesAndDropsHeadingsHiddenAndUnrendered` - nested lists, inline markup, a
  heading, a hidden paragraph, a style and a template.
- `Read_CharacterReferences_AreDecoded`, `Read_MarkerInsideTemplateOrComment_IsNotASection`,
  `Read_SectionLongerThanTheLimit_IsCutAtTheLimit`.

`src/CcDirector.Gateway.UnitTests/Missions/MissionDocumentLinkerTests.cs` (a real store on disk, re-read through a
second store so the link is proven persisted)
- `Resolve_SessionWithNoMission_RefusesWithNoMission` - the refusal, and that it says nothing was published.
- `Resolve_MissionOwnedByAnotherAccount_RefusesAsUnknown`, `SetDocument_AnotherAccountsMission_ChangesNothing` -
  the permission rule.
- `Resolve_SessionWithItsOwnMission_ReturnsThatMission`.
- `Link_FirstPublish_LinksTheReportAndStampsGoalAndWhy` - the link.
- `Link_RepublishOfTheSameReport_KeepsTheLinkAndUpdatesTheGoal` - the relink on republish.
- `Link_ADifferentReport_ReplacesTheDocumentAndSaysWhichItReplaced` - one document per Mission.
- `Link_DocumentWithNoGoalSection_LeavesTheMissionWithNoGoalAndSaysSo`,
  `Link_DocumentWithNoWhySection_LeavesAHandSetWhyAsItWas`.
- `Link_MissionRemovedFromTheStoreBeforeTheLink_ReturnsNull`.
- `MissionStore_RecordWrittenBeforeDocuments_ReadsAsNoDocumentAndNoGoal` - the old file still reads.

`src/CcDirector.Gateway.UnitTests/DevReports/DevReportShapeCheckTests.cs`
- `Check_WhyAndGoalAfterTheQuestions_Passes`, `Check_TwoGoalSections_Fails`, `Check_GoalBeforeTheQuestions_Fails`,
  `Check_GoalInsideADetailSection_Fails`.

`src/CcDirector.Core.UnitTests/MissionStoreHeldReadsTests.cs` - `HeldCopy_CarriesEverySettableField` now gives the
three new fields values, so a store copy that drops one fails.

`tools/cc-dev-reports/tests/test_cli.py` - the flag is sent, the link is printed, a republish shows what it
replaced, the no-Mission refusal exits 1, a Gateway that ignores the flag is a failure, and `--json` keeps one shape.

`tools/cc-devthrottle/tests/test_mission_list_axi.py` - the default `document` column, the `goal` field, the
count of missions with no document, and a refusal for a Gateway row with no `documentReportId`.

## The gate

`.\scripts\test-local.ps1 -Parked`, commit 50c472c88, tree clean:

```
CcDirector.Core.UnitTests                outcome=Completed    total=2899   executed=2899
CcDirector.Avalonia.Tests                outcome=Completed    total=1215   executed=1215
CcDirector.Engine.Tests                  outcome=Completed    total=85     executed=85
CcDirector.HostedAgent.Tests             outcome=Completed    total=85     executed=85
CcDirector.Launcher.Tests                outcome=Completed    total=234    executed=234
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=111    executed=111
CcDirector.Reclaim.Tests                 outcome=Completed    total=307    executed=307
CcDirector.DeliveryQualification.Tests   outcome=Completed    total=151    executed=151
CcDirector.RuleHook.Tests                outcome=Completed    total=70     executed=70
cc-director-setup.Tests                  outcome=Completed    total=43     executed=43
cc-director-setup-engine.Tests           outcome=Completed    total=1029   executed=1023
cc-director-setup-cli.Tests              outcome=Completed    total=78     executed=78
CcDirector.Gateway.Tests.shard1of4       outcome=Completed    total=426    executed=426
CcDirector.Gateway.Tests.shard2of4       outcome=Completed    total=482    executed=482
CcDirector.Gateway.Tests.shard3of4       outcome=Completed    total=428    executed=425
CcDirector.Gateway.Tests.shard4of4       outcome=Completed    total=483    executed=483
CcDirector.Core.Tests                    outcome=Completed    total=4662   executed=4644
CcDirector.Gateway.UnitTests             outcome=Completed    total=16964  executed=16964
RESULT: GREEN at commit 50c472c889ab2f5a96c423cafc141589a000aad0, tree clean.
```

The first `-Parked` run, one commit earlier, was red on two tests my change broke: the held-copy field test
(the new fields had no values in its row) and the machine-dependence check (a new test read the real clock).
Both were fixed in the next commit, and the run above is the whole gate again on that commit.

Python: `tools/cc-dev-reports` 58 passed; `tools/cc-devthrottle` 5,551 passed and 1 failed on the whole suite,
the one a mission fixture missing the new fields, fixed, and the four mission test files then 341 passed;
`tools/test_shipped_tools_contract.py` 52 passed. All with the tools' declared requirements (click 8.2 or later).

## Answers to the review (`_briefs/missions-first-class/phase1-review.md`)

1. **Non-ASCII character in a regular expression - ACCEPTED.** The no-break space is now written ` ` in
   `MissionDocumentSections.cs`. A byte scan of every line this pull request adds finds no character outside
   ASCII.
2. **Command-line reference and readme out of date - ACCEPTED.** `docs/cli-reference.md` (mission list fields,
   default, example output, the document and goal rules, the broken-row rule; `cc-dev-reports open
   --mission-document` and the full `--json` key list) and `tools/cc-dev-reports/README.md` are updated, and so
   is the `--fields` help text inside `cc-devthrottle` itself, which had the same stale list.
3. **`mission list` fails against a Gateway older than this change - NO CODE, by the Delivery Lead's ruling.**
   The Delivery Lead deploys the Gateway right after this merges; the pull request says so.
4. **A republish without the flag left the goal stale - ACCEPTED, as the Delivery Lead ruled.**
   `MissionDocumentLinker.RefreshIfDocument`: after any publish without the flag, when the report is the
   document of the publishing session's own Mission, the why and goal are re-read exactly as with the flag, and
   the answer carries the same `missionDocument` block, which `cc-dev-reports` now prints even without the flag.
   Tested: `Run_RepublishWithoutTheFlagOfTheMissionsDocument_RefreshesTheGoal`, and the negative
   `Run_OrdinaryReportWithoutTheFlag_LeavesTheMissionAlone`; on the tool side
   `test_open_without_the_flag_of_the_missions_document_prints_the_refreshed_link`.
5. **The no-Mission refusal right after an attach - ACCEPTED, the wording.** The attach route hands the change to
   the Director, and the Gateway's copy of the row changes on that Director's next push, so the window is real as
   read from the code (I did not reproduce it live). Recording the attachment on the Gateway's own row is a change
   to the attach route, outside this phase. The refusal now says: "If you attached it a moment ago, the
   attachment may not have reached the Gateway yet: wait a few seconds and publish again." Tested in
   `Resolve_SessionWithNoMission_RefusesWithNoMission`.
6. **A second copy of `/r/` - ACCEPTED.** `MissionButtonFold` builds the address from `DevReportLinkRoute.Prefix`.
7. **The publish route's ordering untested - ACCEPTED, differently than proposed.** Rather than host the route, the
   step after the body is read moved into `DevReports/DevReportPublish.Run`, which the route calls and which is
   tested over the real report store (a throwaway SQLite database, no port, no clock) and a real mission store
   on disk: `Run_MissionDocumentFromASessionWithNoMission_RefusesAndWritesNoReport` (nothing written - mutation
   check: with the early return disabled, this test fails), `Run_MissionDocument_PublishesAndLinks`, and
   `Run_MissionRemovedBetweenTheLookupAndTheLink_ReportIsWrittenAndTheLinkIsLost` (two stores over one file, as
   two containers during a deploy: the report exists at version 1 and the link is reported lost, which the route
   answers 409 `mission_unknown`). The route itself is now only the mapping of that outcome to a response.
8. **The cut could split a surrogate pair - ACCEPTED.** `MissionDocumentSections.Cut` steps back one unit when the
   last kept unit is a high surrogate. Tested both sides: `Cut_LimitFallsInsideASurrogatePair_CutsBeforeThePair`
   and `Cut_LimitFallsAfterAWholePair_KeepsThePair`.

## Not proven here

- The route's mapping from `DevReportPublishOutcome` to a response (status codes, body shape) is not hosted by
  any test; the outcome it maps is.
- The window in answer 5 is read from the code, not reproduced live.
- Nothing on the Director renders the verdict yet; that is phase 2.

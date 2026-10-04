# Proof - the Skills and workflows page for a team (devthrottle_internal#2304, screen S5)

Branch `teams/2304-skills-page`, 4 October 2026, cut from origin/main at 24cf04bfa (the Cockpit team switcher,
devthrottle#3527). This is the page part of #2304. The Gateway part is devthrottle#3532; **this page needs #3532's
routes and must merge after it.** No Gateway code, no schema, no deploy.

## What it does

| With... | Skills and Workflows in the rail open... |
|---|---|
| the person's own account on screen (and everyone who never joins a team) | the pages they always did - unchanged |
| a team picked in the team switcher | that team's **Skills and workflows** page (S5) |
| a remembered team the Gateway has not confirmed yet | a loading line (or the switcher's error) - never the own pages in its place (the switcher's `resolving`) |

The page is one list of the team's own skills and workflows: name and one line, kind, who changed it and when, and
View for every member who may use them. Where the Gateway says this person may change them, it adds Add skill, Add
workflow, and Change and Remove on each row the Gateway marks changeable.

- **Add skill** - name, one line, the words the agent reads; created and published for the team in one step.
- **Add workflow** - a new team workflow starts as a copy of one of DevThrottle's built-in workflows (the store's
  existing clone), under the team's own id, and is then given the name the person typed (the clone keeps the
  built-in's name, so the page renames it and publishes). Its words are then changed with Change. The Cockpit has no
  workflow step editor for anyone today; workflows are otherwise written with the command line.
- **Change** - the one line and the words. Every other part of the published version (triggers, files, steps) is
  kept exactly, the write is made against the published version's hash so a change someone else published in the
  meantime is refused rather than overwritten, and it is published. Empty words are never offered.
- **A write whose publish failed is recovered, never stranded.** Every write here is two calls, write then publish.
  If the publish fails, a second attempt finishes the same item: Change writes over the leftover draft against that
  draft's own hash (the Gateway compares against a draft when one exists), and Add rewrites the skill, or renames
  the copy, it already made instead of creating or copying again under an id that is now taken.
- **Remove** - asks first, then archives it; the team's sessions stop getting it.

**The page renders what the Gateway decided (rule 7).** Whether the Add buttons show, which rows offer Change and
Remove, the sentence a Developer reads, who changed each item - all are read from `GET /teams/{teamId}/library`. The
page never compares a role. A Collaborator's read is refused by the Gateway, and the page shows the Gateway's own
sentence as a note (an answer, not a failure: no red banner, no "Try again"). The server refuses a change whatever
the page shows.

**The page sends no author.** The Gateway records the member it identified (review findings F1 and F3 of #3532), so
no email reaches a request body, a query string or a log.

**The switcher is the one from #2312** (`useCurrentTeam` in `packages/client-core/src/teams/CurrentTeam.tsx`). No
second switcher, no second list of teams.

## Screens, rendered

The real Cockpit (Vite dev server, this branch) in a real Chromium, signed in, with the team picked in the real
switcher. Because #3532 is not on main, the Gateway behind it is a **stand-in** (`s5-capture/stub-gateway.mjs`) that
answers `/teams` and `/teams/{id}/library` in the shapes #3532's routes answer (its hosted tests and its proof
README); the role follows the signed-in account. Its rows are team-owned ids, as the real library lists only those.
It answers only those two reads, so **these screens prove the read-only render per role and nothing about Add,
Change or Remove** - those are proven by the component and client tests below, not by a screen. Re-run with
`s5-capture/run.ps1` (needs playwright-core beside it).

| Role | Screenshot | What it shows |
|---|---|---|
| Owner | `s5-owner.png` | the list; Add workflow, Add skill; View, Change, Remove on every row |
| Manager | `s5-manager.png` | the same as the Owner |
| Developer | `s5-developer.png` | the list with View only, and the Gateway's sentence: "In this team you are a Developer, and a Developer may not change the team's shared skills and workflows." |
| Collaborator | `s5-collaborator.png` | no list; the Gateway's sentence: "In this team you are a Collaborator, and a Collaborator may not use the team's shared skills and workflows." |

## The tests

| Test | Where | Result |
|---|---|---|
| Owner and Manager see the list and can add, change and remove | `TeamLibraryView.test.tsx` `TeamLibraryView_%s_SeesTheListAndCanAddChangeAndRemove` (2) | pass |
| A Developer sees the list read-only, with the Gateway's sentence | `TeamLibraryView_Developer_...` | pass |
| A Collaborator is shown the Gateway's refusal as a note, no list, no retry | `TeamLibraryView_Collaborator_...` | pass |
| A real failure is an error with Try again, not a note | `TeamLibraryView_AGatewayFailure_...` | pass |
| The Gateway's verdict, not the role label, decides what is offered | `TeamLibraryView_TheVerdictNotTheRole_...` | pass |
| A row the Gateway marks unchangeable offers only View | `TeamLibraryView_ARowTheGatewaySaysCannotChange_...` | pass |
| Empty words are never offered for Save | `TeamLibraryView_Change_EmptyWords_AreNeverOffered` | pass |
| A second Add after a failed publish finishes the same skill, under the same id | `TeamLibraryView_AddSkill_ASecondAttemptAfterThePublishFailed_FinishesTheSameSkill` | pass |
| Add workflow sends the typed name, not only an id | `TeamLibraryView_AddWorkflow_StartsFromABuiltIn` | pass |
| Empty list, add skill, add refused (stays open with the sentence), change, remove asks first | 5 tests | pass |
| Own account, no teams offered, a team on screen, unconfirmed team (loading), read failed while resolving (error) | `TeamOrOwn_...` (5) | pass |
| The REAL route table: `/skills` and `/workflows` open the team's page with a team on screen; the own pages, and one workflow's detail, without one; one workflow's detail with a team on screen goes to the team's page | `teamRoutes.test.tsx` (6) | pass |
| The client: verbatim answer; the Collaborator's 403 sentence AND its refusal code; app shell and missing permission are failures; the right route for each kind; add creates then publishes and sends NO author; a refused add does not publish; a second add after a failed publish rewrites the draft and never creates again; change keeps everything but the words and writes against the read hash; a leftover draft is written over against its own hash; a workflow change sends exactly its body; remove; built-ins only as starting points; add workflow copies then renames, with no author; a second attempt after a failed rename never copies again; ids encoded | `teamLibraryClient.test.ts` (16) | pass |

## Every test can fail - nine revert checks

Committed first; each check broke one rule, ran both team suites in full (not filtered), and restored the source.

| Broken | Result |
|---|---|
| D. The Add buttons show regardless of the Gateway's verdict | `TeamLibraryView_Developer_SeesTheListReadOnly_WithTheGatewaysSentence` FAILED (1 of 17) |
| E. A remembered team not yet confirmed shows the own page | both `TeamOrOwn_...Resolving...` tests FAILED (2 of 17) |
| F. The client names an author when adding a skill | `AddTeamSkill_CreatesThenPublishes_AndSendsNoAuthor` FAILED (1 of 13) |
| G. A change ignores a leftover draft and writes against the published hash | `ChangeTeamItem_ADraftLeftByAFailedPublish_...` and `AddTeamWorkflowFrom_ASecondAttemptAfterAFailedRename_...` FAILED (2 of 40) |
| H. Save is offered with empty words | `TeamLibraryView_Change_EmptyWords_AreNeverOffered` FAILED (1 of 32) |
| I. `/skills` goes back to the own page | `Route_/skills_WithATeamOnScreen_OpensTheTeamsPage` FAILED (1 of 32) |
| J. One workflow's detail is the own page whatever is on screen | `Route_OneWorkflowsDetail_WithATeamOnScreen_...` FAILED (1 of 32) |
| K. The typed workflow name is dropped | `AddTeamWorkflowFrom_CopiesUnderTheNewId_ThenGivesItTheTeamsName_...` and `..._ASecondAttemptAfterAFailedRename_...` FAILED (2 of 40) |
| L. A second Add creates the skill again | `AddTeamSkill_ASecondAttemptAfterAFailedPublish_...` FAILED (1 of 40) |

(D, E and F ran on the first head, before the review fixes; G to L ran at f813886d4. "of 40" is client-core's team
tests, "of 32" the Cockpit's.)

## Checks run

At the review-fix head:

- `npx vitest run` in `packages/client-core`: Test Files 142 passed, Tests 1,675 passed.
- `npx vitest run` in `apps/cockpit`: Test Files 70 passed, Tests 621 passed.
- `npm run typecheck` for client-core and cockpit: exit 0. `eslint` on the changed folders and `routes.tsx`: exit 0.
- No C# changed, so no .NET suite applies to this part.

## Not in this part

- The session-level proof of #2304 ("a session started by a test Developer showing the team skill loaded") waits on
  #2311, as written in #3532's proof.
- The phone app has no Skills page, so there is no second surface to keep in step.

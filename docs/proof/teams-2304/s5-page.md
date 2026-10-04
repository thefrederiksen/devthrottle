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
  existing clone), under the team's own id; its words are then changed with Change. The Cockpit has no workflow step
  editor for anyone today; workflows are otherwise written with the command line.
- **Change** - the one line and the words. Every other part of the published version (triggers, files, steps) is
  kept exactly, the write is made against the published version's hash so a change someone else published in the
  meantime is refused rather than overwritten, and it is published.
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
answers `/teams` and `/teams/{id}/library` in exactly the shapes #3532's routes answer (its hosted tests and its proof
README); the role follows the signed-in account. Re-run with `s5-capture/run.ps1`.

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
| Empty list, add skill, add refused (stays open with the sentence), change, remove asks first, add workflow from a built-in | 6 tests | pass |
| Own account, no teams offered, a team on screen, unconfirmed team (loading), read failed while resolving (error) | `TeamOrOwn_...` (5) | pass |
| The client: verbatim answer, the Collaborator's 403 sentence, app shell and missing permission are failures, the right route for each kind, add creates then publishes and sends NO author, a refused add does not publish, change keeps everything but the words and writes against the hash, workflow change keeps its steps, remove, built-ins only as starting points, clone with no author, ids encoded | `teamLibraryClient.test.ts` (13) | pass |

## Every test can fail - three revert checks

Committed first; each check broke one rule, ran its tests, and restored the source.

| Broken | Result |
|---|---|
| D. The Add buttons show regardless of the Gateway's verdict | `TeamLibraryView_Developer_SeesTheListReadOnly_WithTheGatewaysSentence` FAILED (1 of 17) |
| E. A remembered team not yet confirmed shows the own page | both `TeamOrOwn_...Resolving...` tests FAILED (2 of 17) |
| F. The client names an author when adding a skill | `AddTeamSkill_CreatesThenPublishes_AndSendsNoAuthor` FAILED (1 of 13) |

(The revert checks ran before the Collaborator note change; the suite then grew to 18.)

## Checks run

- `npm test --workspace @devthrottle/client-core`: Test Files 142 passed, Tests 1,672 passed.
- `npm test --workspace @devthrottle/cockpit`: Test Files 69 passed, Tests 613 passed (after the Collaborator note change).
- `npm run typecheck` for client-core and cockpit: clean. `eslint` on every changed file: clean.
- No C# changed, so no .NET suite applies to this part.

## Not in this part

- The session-level proof of #2304 ("a session started by a test Developer showing the team skill loaded") waits on
  #2311, as written in #3532's proof.
- The phone app has no Skills page, so there is no second surface to keep in step.

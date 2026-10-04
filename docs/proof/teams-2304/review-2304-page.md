# Review: the S5 page, Skills and workflows (thefrederiksen/devthrottle pull request #3536)

Written by a separate review session, 4 October 2026. Head reviewed: ab2fc024d, against origin/main at 0f8ae2706
(merge base 24cf04bfa). The Gateway contract was read from origin/teams/2304-shared-skills at c3b6de244
(pull request #3532, not merged).

## Scope

**Read, in full:**

- The whole diff `git diff origin/main...HEAD`: `apps/cockpit/src/teams/TeamLibraryView.tsx`, its test file,
  `packages/client-core/src/teams/teamLibraryClient.ts`, its test file, the `routes.tsx` change, the three capture
  scripts under `docs/proof/teams-2304/s5-capture/`, and `docs/proof/teams-2304/s5-page.md`.
- On main: `packages/client-core/src/teams/CurrentTeam.tsx`, the failure parsing in
  `packages/client-core/src/api/client.ts`, `apps/cockpit/src/components/ConfirmDialog.tsx`, `suggestSkillId`.
- On the #3532 branch: `TeamLibraryEndpoints.cs`, `SkillEndpoints.cs`, `WorkflowEndpoints.cs`,
  `ServerStampedAuthor.cs`, `TeamEndpointRules.cs`, `TeamEndpointGate.cs`, the request and answer contracts in
  `SkillDtos.cs` and `WorkflowDtos.cs`, and the parts of `SkillStore.cs`, `WorkflowStore.cs`, `SkillValidation.cs`
  and `WorkflowValidation.cs` that the page's routes reach (create draft, update draft, publish, clone, archive,
  version detail, body and instructions reads, the publish-time rules), plus the refusal wording in `TeamAccess.cs`
  and `TeamPermissions.cs`.
- Issue #2304, the Developer's brief, and screen S5 of the mockup.

**Ran, in this worktree, with no file edited:**

- `npm ci` at the root.
- `npx vitest run` in `packages/client-core`: 142 test files passed, 1,672 tests passed.
- `npx vitest run` in `apps/cockpit`: 69 test files passed, 613 tests passed.
- `git status` afterwards: clean.

**Could not reach:**

- No .NET suite was run (mandate). Everything said below about what the Gateway answers is from READING the
  #3532 branch, not from a running Gateway. F1 and F2 in particular are read, not reproduced.
- The page was not driven against a real Gateway: #3532 is not merged and no such Gateway exists to drive.
- The four screenshots were not re-captured. I did not open the image files; I judged the stand-in from its source.
- I did not perform the three revert checks the proof file reports; I did not edit any file. Where I say a test
  cannot fail, it is from reading the test.
- Role table in #2098 was taken from the #3532 branch's `TeamPermissions.cs` rows (use: Owner, Manager, Developer;
  change: Owner, Manager), which agree with issue #2304's text. I did not open #2098 itself.

**What I checked and found sound (no finding):**

- Rule 7. The Add buttons follow `library.canChange`, each row's Change and Remove follow that row's `canChange`,
  the Developer's sentence is `changeRefusal` verbatim, and the Collaborator's note is the Gateway's own 403
  sentence. Nothing in the page or the client compares a role. `team.role` is never read.
- The author. No request body carries `authoredBy` and no query string carries `by=`. The page has no console
  call. The only email on the page is `changedBy`, which the Gateway chose to send.
- The current team. `TeamOrOwn` reads `resolving` first, and shows loading or the switcher's error, never the own
  page. With no remembered team, Teams dark, or no team picked, `resolving` is false and `current` is null, so the
  own page renders exactly as before and no team route is called.
- The team id. Every request the page makes goes through `teamPath(teamId, ...)` with the id of the one team on
  screen, encoded; `key={current.id}` remounts the page on a switch so no state carries across teams.
- Shapes. The library answer, the 403 body (`error` plus `code: "team_action_refused"`), the skill create body,
  the skill and workflow draft bodies (the extra `contentHash` on a skill file is ignored by the Gateway's reader;
  `encoding` and `executable` survive the round trip), the clone query, the delete, the two markdown reads and the
  workflow list's `isBuiltIn` all match what #3532's routes read and write.

## Verdict

**Changes needed before merge: one should-fix that strands an item (F1), two smaller should-fixes (F2, F3), and
three notes.** No blocker on the points the mandate asked to be looked at hardest: rule 7, the author, the current
team and the team id in every route are all correct.

## Findings

### F1 - should-fix - a Change whose publish fails leaves a draft behind, and the page can then never change that item again

**Location:** `packages/client-core/src/teams/teamLibraryClient.ts` lines 155 and 178-179 (`changeTeamItem`);
`apps/cockpit/src/teams/TeamLibraryView.tsx` line 247 (the Save button's disabled rule) and line 226. Same shape
in `addTeamSkill`, lines 113-114.

**The harm.** A change is two writes: `PUT .../draft`, then `POST .../publish`. If the first succeeds and the
second fails, the Gateway now holds a DRAFT for that item. The page's next attempt reads the PUBLISHED version
(`/versions/{item.version}`) and sends that version's hash as `If-Match`. But the Gateway's `UpdateDraft` compares
`If-Match` against the draft when one exists (`SkillStore.cs` on the #3532 branch, `var baseline = draft ?? ...
Published`; `WorkflowStore.cs` the same). The hashes differ, so the Gateway answers 409, "The skill changed since
you read it (content hash mismatch). Pull the current content and reapply your edit." Reloading does not help: the
library still reports the published version, so every later Change of that item from this page gets the same 409,
for every Owner and Manager, permanently. Nothing on the page can publish or discard the draft, and today no other
surface can reach a team's library at all (the command line talks to the personal routes; the team-bound key waits
on #2311). The only move left is Remove, after which the same id cannot be added again (the archived id still
exists, so create answers 409 "already exists").

**The page makes this easy to hit, without any network fault.** In the Change dialog the Save button is disabled
only while the text is still loading (`text === null`), not when it is empty. Clear the box and press "Save for
the team": the draft write is accepted (the draft rules allow an empty body), and the publish is refused with "A
skill cannot publish without a body" (`SkillValidation.ValidateForPublish`; for a workflow, "cannot publish without
instructions"). The dialog stays open showing that sentence; the person types the words back and saves again, and
gets the 409 above. The item is now stuck as described.

**Add skill has the same two-step shape** (create draft, then publish). The dialog does require a non-blank body,
so it takes a real failure between the two calls; when that happens the skill exists as an unpublished draft that
the library does not list, and adding it again answers "A skill with id '...' already exists" for a name the page
shows nowhere.

**Why it must change.** A shared item that an Owner can no longer change, with a message telling them to "pull
the current content" on a page that has no way to do so, is a dead end the person cannot get out of. At minimum:
do not offer Save with empty words; and make the second attempt work when the leftover draft is the caller's own
failed change (for example, write against the hash the draft write itself returned and retry only the publish, or
read the latest version rather than the published one). No test covers a publish that fails after a draft write.

**This is from reading the store on the #3532 branch; I did not run it.**

Developer answer: Fixed in the page; #3532 does not change. (1) Save is never offered with empty words (`TeamLibraryView.tsx`, the Change dialog's disabled rule; test `TeamLibraryView_Change_EmptyWords_AreNeverOffered`). (2) A leftover draft is recovered: `changeTeamItem` now reads the item's version history first and, when a draft is there, writes against THAT draft's hash, replacing it; with no draft it still writes against the hash of the version the person read, so a change someone else published meanwhile is still refused (`ChangeTeamItem_ADraftLeftByAFailedPublish_IsReplaced_WrittenAgainstItsOwnHash`, and the existing published-hash test). Every write this page makes publishes straight after, so a draft that is still there is a change that did not finish. It works from a reload too, not only within the dialog. (3) Add has the same care: the dialog holds how far the add got (`AddProgress`), so a second attempt after a failed publish rewrites the unpublished skill the first attempt made and publishes it, never creating again; the id is then fixed in the dialog (`AddTeamSkill_ASecondAttemptAfterAFailedPublish_RewritesTheDraftAndPublishes_NeverCreatesAgain`, `TeamLibraryView_AddSkill_ASecondAttemptAfterThePublishFailed_FinishesTheSameSkill`). Revert checks G and L each turned their tests red. Not covered: a skill whose add failed AND whose dialog was then closed stays an unlisted draft until the Gateway can list or discard a team's drafts; that is a Gateway change, so I did not make it, and I name it here rather than claim it.

### F2 - should-fix - "The team's name for it" sets only the id; the row shows the built-in's name, and nothing on the page can change it

**Location:** `apps/cockpit/src/teams/TeamLibraryView.tsx` lines 349-353 (the field and its label), line 326;
`packages/client-core/src/teams/teamLibraryClient.ts` lines 162 and 198-201.

**The harm.** The Add workflow dialog asks for "The team's name for it" and sends what is typed only as `newId`.
The Gateway's clone copies the source's name (`WorkflowStore.Clone` on the #3532 branch: `Name =
sourceVersion.Name`). So a Manager who starts from "Standalone with review" and types "Our review" gets a row whose
Name column reads "Standalone with review", not "Our review". The id they typed is shown nowhere in the table
(the row renders `item.name` and `item.summary` only). Two copies of the same built-in are two rows with the same
name and the same one line, told apart only by the date, and the Remove question reads "Remove 'Standalone with
review' from the team?" for both. Change cannot repair it: `changeTeamItem` sends `name: v.name` back unchanged
and the dialog has no name field.

**Why it must change.** The field's label promises something the page does not do, and the result is rows a
person cannot tell apart when the action on offer is Remove. Either send the typed name (clone, then a draft with
the new name, then publish - which then needs F1's care), or label the field as the id and show the id in the row.
The test `TeamLibraryView_AddWorkflow_StartsFromABuiltIn` passes because it checks only the call's arguments; no
test looks at what the new row is called.

Developer answer: Fixed: the name is now the name. The Add workflow dialog has a Name field with the id shown under it (as Add skill does). `addTeamWorkflowFrom` copies under the id, then, because the clone keeps the built-in's name, writes the typed name as a draft against the copy's hash and publishes; a second attempt after a failed rename finishes the same copy and never copies again (it uses F1's leftover-draft care). Tests: `AddTeamWorkflowFrom_CopiesUnderTheNewId_ThenGivesItTheTeamsName_WithNoAuthor`, `AddTeamWorkflowFrom_ASecondAttemptAfterAFailedRename_FinishesTheSameCopy_NeverCopiesAgain`, and the page test now checks the name sent. Revert check K turned two tests red.

### F3 - should-fix - `/workflows/:id` still shows and changes the person's OWN library while a team is on screen

**Location:** `apps/cockpit/src/routes.tsx` line 163 (`{ path: "/workflows/:id", element: <WorkflowDetail /> }`,
unchanged by this pull request, beside the two routes it did wrap).

**The harm.** The mandate asks whether anything still calls the personal routes while a team is chosen. One page
does. `/workflows` and `/skills` are wrapped in `TeamOrOwn`; the single-workflow page is not. It calls the personal
`/gateway/workflows` routes and offers Clone and the on/off switch (`WorkflowDetail.tsx`, `cloneWorkflow`, and the
"turn it back on anytime" switch). A person who is on `/workflows/mission` in their own account and then picks a
team in the rail stays on that page: the rail now names the team, and Clone or the switch acts on their PERSONAL
library. A reload, a bookmark or the browser's back button lands there too. The team page itself has no link to
it, so it is not reached by clicking through S5 - which is why I rate it should-fix and not blocker.

**Why it must change.** "Two pages can never disagree about which team is on screen" is the stated purpose of the
shared switcher, and here a write goes to a different library than the one the screen names. Wrapping this route
the same way (or sending a person with a team on screen back to `/workflows`) closes it.

Developer answer: Fixed: `/workflows/:id` is wrapped too. `TeamOrOwn` takes an optional `team` element; for one workflow's detail it is a redirect to `/workflows`, so with a team on screen the person lands on the team's page and nothing acts on the own library. With the own account it is the detail page as before. New `teamRoutes.test.tsx` drives the REAL route table (F6). Revert check J turned it red.

### F4 - note - two of the stand-in Gateway's three rows could never come from the real Gateway

**Location:** `docs/proof/teams-2304/s5-capture/stub-gateway.mjs` lines 8-10; the claim in
`docs/proof/teams-2304/s5-page.md` ("answers ... in exactly the shapes #3532's routes answer").

**The harm.** The mandate asks whether the stand-in matches #3532's real responses. The SHAPES do: the field names
and types of `/teams/{id}/library`, the 403 body with `code: "team_action_refused"`, and both refusal sentences
are word for word what `TeamLibraryEndpoints.Library`, `TeamEndpointGate.RunAsync` and `TeamAccess.RoleRefusal`
produce, and `changedBy` is an email as `TeamEndpoints.MemberName` returns. The CONTENT does not: `dev-reports` is
a built-in skill id and `standalone-with-review` is a built-in workflow id. The real `Library` leaves built-ins
out (`Where(s => !s.IsBuiltIn)`), and a team can never own either id (create and clone both refuse a built-in id).
The rows follow the mockup, but the screenshots show a list the product cannot produce, and the built-in note
under them says built-ins "are not listed here". Also: the stand-in answers only the two reads, so the screenshots
prove the read-only render per role and nothing about Add, Change or Remove - the proof file should say so in
those words. Use team-owned ids in the stand-in.

Developer answer: Fixed: the stand-in's rows are team-owned ids (Incident notes, Release checklist, Our review) and its header says why. The four screenshots were recaptured. `s5-page.md` now says in those words that the screens prove the read-only render per role and nothing about Add, Change or Remove, which the component and client tests prove instead, and "exactly the shapes" became "the shapes".

### F5 - note - the tests never see the Gateway's refusal code travel from the response to the page

**Location:** `packages/client-core/src/teams/teamLibraryClient.test.ts` line 59;
`apps/cockpit/src/teams/TeamLibraryView.test.tsx` line 86.

**The harm.** The page shows the calm note (no red banner, no "Try again") only when `err.code ===
"team_action_refused"`. The client test for the Collaborator sends a real 403 body with that code but asserts only
`status` and `serverReason`; the page test hand-builds a `GatewayError` that already carries the code. So no test
in this pull request would go red if the code stopped reaching the error - the Collaborator would then get a red
error with a retry button, and both suites would stay green. Add `code: "team_action_refused"` to the client
test's `toMatchObject`. In the same file, `ChangeTeamItem_AWorkflow_KeepsItsSteps` uses `toMatchObject` on the
draft body, so unlike the skill test (`toEqual`) it would not notice an author being added to a workflow change.

Developer answer: Fixed: the client's Collaborator test now asserts `code: "team_action_refused"` as well. The workflow change test now uses `toEqual` on the whole draft body, so an added author would fail it.

### F6 - note - no test watches the route wiring

**Location:** `apps/cockpit/src/routes.tsx` lines 157 and 160.

**The harm.** `TeamOrOwn` is tested by rendering it directly. Nothing renders the `/skills` or `/workflows` route.
Putting `<SkillsView />` and `<WorkflowsView />` back on those two lines - which removes the whole feature - leaves
all 613 Cockpit tests green (the only files that mention `TeamOrOwn` are the page, its own test, and `routes.tsx`).
The screenshots do cover `/skills` once, by hand; `/workflows` is covered by nothing. One test over
`COCKPIT_ROUTES` that both paths resolve to `TeamOrOwn` would hold it.

Developer answer: Fixed: `apps/cockpit/src/teams/teamRoutes.test.tsx` renders COCKPIT_ROUTES itself: `/skills` and `/workflows` open the team's page with a team on screen; `/skills`, `/workflows` and `/workflows/mission` open the own pages without one; `/workflows/mission` with a team goes to `/workflows`. Revert check I (own page back on `/skills`) turned it red.


## Developer's closing note

Answered on branch `teams/2304-skills-page`; head given to the Tech Lead with the hand-back. Checks at that head: client-core 1,675 of 1,675, Cockpit 621 of 621, typecheck and eslint exit 0; six new revert checks (G to L), each red then restored. No Gateway change; #3532 is untouched.

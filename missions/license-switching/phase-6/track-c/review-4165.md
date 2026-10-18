# Review of pull request 4165 - License Switching Track C, approver health in the Cockpit

Reviewer: a separate review session, 17 October 2026. Read through git objects only: `refs/review/4165`
(head 636a08ed9, three commits) diffed against its merge base with origin/main (10357e5ef). Nothing was built,
run, checked out, committed or pushed.

## Scope

- `apps/cockpit/src/licenses/LicenseSignIns.tsx` (the approver table), the view test `LicensesViewSignIns.test.tsx`,
  `fixtures.ts`, `fixtures.test.ts` (the fixture guard, unchanged), `licenses.css`.
- `packages/client-core/src/licenses/licensesClient.ts` (the two new fields on the approver type).
- Gateway: `LicenseSignInFold.Approver`, `LicenseApproverDto`, `LicenseSignInEndpointsTests`, and the store's
  `WithSource` normalisation (for the fold's two-way source text).
- `missions/license-switching/cockpit-api.md` section 7, the proof file `c3-approver-health.md`, the screenshot
  script `health_layout_check.py`, and all seven screenshots (extracted from the ref and opened).
- Every client read of `canApproveNow`, `reachText`, `.source`, `approver.state` and `approver.status` across
  `apps/cockpit/src` and `packages/client-core/src`.

## Findings

None. No finding reached the bar of a proven harm.

## What was checked, and what it showed

1. **Rule 7 - does any `.tsx` conditional decide what a state means?** No. The approver table has four conditionals and
   each is a presence check on a nullable Gateway field: `browserProfileName !== null`, `reachText !== null`,
   `whatToDo !== null`, and the pre-existing `approvers.isEmpty`. Nothing branches on `status`, `state`, `source`,
   `statusTone` or `canApproveNow`; `data-status` is passed through as an attribute only. The only client read of
   `canApproveNow` left in the two trees is its type declaration in `licensesClient.ts`.
2. **Does anything rendered promise the relay will refuse a sign-in?** No. The row draws `statusText`, `sourceText`,
   `reachText`, `healthText` and `whatToDo` verbatim. The test "never turns canApproveNow into words" feeds
   `canApproveNow: true` beside the Gateway's "Cannot approve" and asserts the row shows "Cannot approve" and the word
   "Ready" nowhere. The new `reachText` only says why a machine cannot be reached now; its two sentences are the ones
   the fold already put at the end of `text` before this change.
3. **Folded in one place, tested exactly, plain English, ASCII, free of payload text.** Both new texts are built once
   in `LicenseSignInFold.Approver`; `reach` is computed once and feeds both `Text` and `ReachText`, so the two cannot
   drift. `Text`, `Status`, `StatusText`, `Tone` and `CanApproveNow` are unchanged in value for every branch (the
   `reach is not null` arm is reached only when the machine exists and is not connected, exactly as before). Four
   Gateway tests pin the exact strings: "Set by you." with null reach (owner, connected), "Registered by the-mac's
   Director." with null reach (registered, healthy), the not-connected sentence, and the never-reported sentence; the
   field-order test pins the DTO shape with the two new names. The store's `WithSource` normalises every stored record
   to `owner` or `registered`, so the fold's two-way `SourceText` has no unhandled third case. The strings interpolate
   only `label`, which is the Gateway's own machine record (or the owner's shape-checked PUT), never a Director
   payload. The whole text diff is ASCII.
4. **Screenshots.** All seven open and show what the proof file says: a healthy registered row ("Ready", "Registered by
   machine-a's Director.", health sentence, nothing to do); a never-checked row on a never-reported machine ("Not
   reporting yet", why, "has never been checked", what to do in bold); a needs-fresh-sign-in row ("Cannot approve",
   "Set by you.", the health sentence, "Open that browser on machine-a and sign in to claude.ai once."); a not-connected
   row with "Work Chrome" over the dim identifier "work-chrome"; the whole table; the Remove confirmation on the
   registered row with the Gateway's `text`; and the 900-pixel table. Names are machine-a, machine-b, machine-c and
   alice, bob, carol, dave at example.com. No real email, machine name or account name appears in the images, the
   script, the fixtures, the document or the tests (swept for the owner's names, domains and machine names).
5. **Do the tests render the real view with only fetch stubbed?** Yes. `LicensesViewSignIns.test.tsx` renders
   `<LicensesView />` and the only replacement is `vi.stubGlobal("fetch", ...)`; there is no `vi.mock` of any module.
   The approvers answer is the fixture copied from the Gateway's document example, and `fixtures.test.ts` (unchanged)
   still holds every fixture to the document, so the hand-added `sourceText` and `reachText` lines in the document and
   the fixtures must agree with each other, and the Gateway tests pin that the fold produces the same wording.
6. **The screenshot script** drives only a local Vite dev server with every Gateway route answered from the document;
   it signs nothing in and touches no live license state. It ends its own Vite process by process id, not by name.

## Observations (not findings; no harm proven, no change required)

- At 900 pixels the Status column is squeezed to about 120 pixels because the account, machine, profile and action
  columns are all `lic-nowrap`; the sentences wrap word by word and a row grows to some twenty lines. It stays
  readable and nothing overflows, and the brief asked for the four states and the registered row, not a width budget.
- `Registered by {label}'s Director.` reads as "Registered by the-mac (alice)'s Director." when two users share a
  machine name and the label carries the user in brackets. Grammatically odd, still unambiguous and plain ASCII.
- The proof file is candid that the document's section 7 examples were given the two new fields by a script rather
  than by re-running the route, and that the one installer test failure is a CRLF defect that came in with #4152 and is
  unrelated to this change. Both statements match what the diff shows.

# Worker H - the web shells: an answer that survives, a guard with teeth, a failure you can see

You are a Worker on the "Stop a session" mission, Phase C. Your manager is session `e7ea69df`.
Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Work there and
nowhere else. Do not merge to main. Do not open a pull request.

Read these first, in full:

1. `missions/stop-a-session/inspection-1.md` - findings **I4**, **I7** and **I8** are yours.
2. `missions/stop-a-session/architect-ruling-on-inspection-1.md` - their rows in the triage table.
3. `missions/stop-a-session.html` - Ruling 5: one event, described one way, on every surface, in
   words the Gateway wrote. No client composes a sentence of its own.

Also read `docs/VisualStyle.md`'s opening paragraph, which says plainly that it does **not** govern
the browser shells and names the token sets that do. Use the tokens already defined in each shell and
match the nearest component family, exactly as Phase B did.

## I4 - the Cockpit destroys the stop answer on the next roster update

`apps/cockpit/src/sessions/SessionMenu.tsx:60` holds the stop answer in the component's own state.
Both placements of that component exist only while the session exists: `SessionRoster.tsx:421` mounts
one per roster row, and `SessionDetail.tsx:124` mounts it behind `selected && ...`. A successful stop
removes the row. The shared roster poll is **two seconds**
(`packages/client-core/src/fleet/rosterStore.ts:19`), so the next refresh unmounts the menu and its
portal and destroys the answer with no `Done`, no Escape and no backdrop click. An update can even
arrive before the stop response, so the answer need never become visible at all.

**The answer must outlive the row it describes.** It belongs to something that survives the row's
removal - not to a component mounted inside it. Lift the answer to an owner above the roster and the
session page; the menu hands the outcome up and the dismissal is what clears it. A small provider
mounted in `AppShell` that holds the current stop answer and renders it is the obvious shape, but the
shape is yours - what is not negotiable is that unmounting the row cannot take the answer with it.

While you are there: **check whether the phone has the same defect** and say so in your notes. The
inspection did not find it there, so fix it only if you actually reproduce it - and if you do,
reproduce it in a test first.

## I7 - Enter can send repeated stops while the controls say Stopping

`SessionMenu.tsx:279`, `:455`; `apps/mobile/src/components/SessionAppBar.tsx:91`, `:265`;
`useSessionManage.ts:194`. The buttons disable while busy, but the reason inputs stay active and their
Enter handlers call functions that check the reason and never check whether a request is already in
flight. The mobile hook has lost the busy guard the previous remove operation had. Two Enter presses
send two stop requests, and the two independently completing handlers can overwrite the first
response with the second's outcome, or clear busy while a request is still outstanding.

**The guard goes on the action, not only on the button** - which is the same lesson Phase B already
learned from a disabled-button assertion that had no teeth, and then failed to apply to Enter.

Watch out for the trap Phase B recorded: a test that awaits the second press **deadlocks** under its
own mutation instead of going red, because the second call waits on the same outstanding answer. Your
test must be able to fail. Say in your notes how you made sure of that.

## I8 - the phone's stop failure is outside the modal, and dismissing it deletes it

`apps/mobile/src/components/SessionAppBar.tsx:111`, `:221`, `:248`; `apps/mobile/src/styles.css:3138`.
On failure the confirmation modal stays open, but the error renders in the app bar's sibling banner,
underneath a full-screen overlay, outside a dialog that declares `aria-modal="true"`. There is no
failure text inside the active dialog at all. Pressing Cancel to get back to the underlying screen
clears that banner too, so the operator is left with a reason box and a retry button and no
explanation.

**The failure is shown inside the dialog the operator is looking at**, in the Gateway's own words,
with the typed reason still in the box - which is what the Cockpit already does and what Ruling 5
requires of every surface. The existing sheet test supplies an error on its stub before the action and
searches the whole document, which is why it did not catch this; your replacement asserts the error is
found **within the dialog**.

## THE STANDARD, and this is the part the earlier phases failed

Every fix gets a test **watched failing against the production code path**, not against an injected
stand-in for it. The inspection's most valuable result was that replacing a production method with a
constant left all 68 tests in one suite passing, because the tests injected their own substitute.

For your three findings that means, concretely:

- **I4 is tested through the real parent.** Render the real `SessionRoster` (and the real
  `SessionDetail` path), stop the session, then let the roster refresh with the stopped row absent -
  and assert the answer is still on screen. A test that mounts `SessionMenu` on its own cannot see
  this defect and is not the test. Drive the roster the way the application drives it.
- **I7 is tested with the real management hook on the phone**, as the inspection did, not with a
  hand-rolled stand-in for it.
- **I8 asserts containment**: query *within* the dialog element, not the whole document, and assert
  the error survives what the operator would actually do next.
- Then **mutate each fix and watch it go red**: put the answer state back inside the menu; take the
  busy guard back out of the action; move the failure text back outside the dialog. Record the red
  message as it actually printed. If a mutation leaves the suite green, that test is decoration and
  you have not finished.

## What you must NOT do

- Do not touch `packages/client-core/src/api/client.ts` - another Worker is in that file this phase.
  If your change needs something from it, tell your manager and wait.
- Do not touch anything under `src/`, `tools/cc-devthrottle`, or the Gateway.
- Do not compose a sentence about a stop. Every word an operator reads about the outcome is the
  Gateway's, rendered verbatim.
- Do not add or read the verdict word in any shell. There are four today and a fifth must stay one
  edit on the Gateway and none in any client.
- No abbreviations in anything you write. No mention of any assistant, vendor or model in any commit
  message, comment or document.

## What to run before you report

The local gate runs **no web tests at all**, so these three suites are the whole of your coverage and
you run every one of them yourself, to completion, with the numbers:

- `@devthrottle/cockpit`
- `@devthrottle/mobile`
- `@devthrottle/client-core`
- `npm run typecheck` across all four workspaces

**Never write a result row before the run that fills it.** Two phases of this mission have already
been caught doing exactly that.

## What to hand back

Commit and push on the branch as you go. Write your notes to
`missions/stop-a-session/worker-h-notes.md`: what you changed, the mutation table with the red
messages as they actually printed, the suite numbers you actually ran, whether the phone shares the
I4 defect and how you established it, and - named honestly - what your tests still do not cover
(pixels and a real browser are not yours; say so). Then send your manager (`e7ea69df`) ONE
single-line message saying you are done and pointing at that file. Fleet messages truncate at the
first newline.

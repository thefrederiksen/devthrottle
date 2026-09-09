# Worker H - the web shells: I4, I7, I8

Findings I4, I7 and I8 of `inspection-1.md` are closed. Nothing else was touched. Everything below is a
run I did myself, on this branch, and every red message is copied out of the run that printed it.

**Where the code landed, and a warning about reading history.** My work was swept into commit
`a6c4c28c` - a documentation commit - by the Architect's `git add -A`, along with several other seats'
in-progress Phase C work. Nothing was lost and nothing needs reverting (the Manager confirmed this and
the Architect recorded the error in `2e9bca8b`), but **that commit's message does not describe what is
in it.** If you are looking for the web-shell fixes in history, they are in `a6c4c28c`, not in a commit
named for them. This notes file is the commit I actually made - and its subject line reads as a bare
`@`, because a shell here-string put one there and another seat had already committed on top before I
noticed, so correcting it would have rewritten somebody else's commit. The commit is `ea26e0f8`; its
body is intact and says what it is. Recorded here rather than rewritten.

---

## What changed

### I4 - the Cockpit destroyed the stop answer on the next roster update

**The whole stop moved out of the roster row.** New file
`apps/cockpit/src/sessions/StopSessionProvider.tsx`, mounted once in `AppShell`, owns the question, the
request, the busy state, the failure and the Gateway's answer. `SessionMenu`'s Stop session item now
hands the session (and its `onClosed`) UP and owns nothing about the stop; its stop state and its stop
dialog markup are gone.

I moved the REQUEST as well as the answer, deliberately. Lifting only the answer would still have lost
the case the inspection named - "an update can even arrive before the stop response, so the answer need
never become visible at all" - because a `setState` from a promise belonging to an unmounted component
is a no-op. With the request owned above the roster, the row can be removed at any moment, mid-request
or mid-answer, and nothing goes with it. There is a test for exactly that ordering.

Two smaller things came with it, both tested:

- **One stop at a time.** `openStop` refuses to replace an answer that is still outstanding.
- **No dismissal while the request is outstanding.** Cancel is disabled and the backdrop is refused, so
  an answer always has somewhere to land. (This is not new behaviour for Cancel - it was already
  disabled while busy - but the backdrop was not, and the backdrop is now covered by the same rule.)

Files: `StopSessionProvider.tsx` (new), `AppShell.tsx`, `SessionMenu.tsx`.

### I7 - Enter could send repeated stops while the controls said Stopping

**The guard is on the action, and there is exactly ONE action per shell.**

- Cockpit: `StopSessionProvider.send` holds an `inFlight` ref and returns early.
- Phone: `useSessionManage`'s stop verb holds a `stopInFlightRef` and THROWS on a second call, without
  reaching the Gateway. The app bar's existing empty catch swallows it, so nothing is displayed - there
  is no event to describe, the Gateway was never asked, and inventing a sentence about a stop is what
  Ruling 5 forbids.

A ref rather than the `busy` flag, because two presses landing in one tick both read the same stale
state. I put the phone's guard in the hook ONLY, not in the hook and the app bar. Two guards would have
meant one test covering both, with whichever guard the test reached first masking the removal of the
other - a guard nothing can watch fail is decoration, which is the exact defect this phase exists to
fix.

Files: `StopSessionProvider.tsx`, `apps/mobile/src/components/useSessionManage.ts`.

### I8 - the phone's stop failure was outside the modal, and dismissing it deleted it

- The Gateway's sentence now renders INSIDE the confirmation card, between the reason box and the
  buttons (`.confirm-error`, new, using the phone's own tokens and the same colours as `.banner-error`,
  which is where the sentence used to go).
- The app bar's sibling banner is suppressed while the sheet is open, so the failure is said once
  rather than twice with one copy under an overlay.
- `onCancelStop` no longer clears `manage.error`. Backing out to the screen behind now LEAVES the
  explanation on the banner instead of deleting the only copy of it. Opening the sheet again clears it,
  which is the right moment: that is a fresh question about a session that is still here.

Files: `apps/mobile/src/components/SessionAppBar.tsx`, `apps/mobile/src/styles.css`.

### Style

`docs/VisualStyle.md` does not govern the browser shells and says so. I used the tokens already in each
shell and matched the nearest component family: the Cockpit dialog is the same `.session-dialog-*`
markup moved wholesale, so its appearance is unchanged; the phone's new `.confirm-error` sits in the
`.confirm-*` family and borrows `.banner-error`'s colours. No new token was invented and no hex value
was ported from the desktop guide.

---

## Does the phone share the I4 defect? No - and here is how I established it

**By reading, then by a control.** The reading: `grep` for every stop entry point in `apps/mobile`
returns exactly one - `SessionAppBar`'s overflow menu. That bar is mounted by the per-session ROUTE
(`pages/Chat.tsx`, `pages/Terminal.tsx`, `pages/VoiceMode.tsx`), never by a roster row, and no route in
`apps/mobile/src/main.tsx` redirects away from `/session/:sessionId` when the session leaves the fleet.
The hook's four-second roster poll only reads: on no match it keeps its last-known values and touches
nothing.

That is an argument from code, so I turned it into a test rather than leaving it as a paragraph:
`apps/mobile/src/components/stopAnswerSurvivesRosterPoll.test.tsx` stops the session through the real
hook, then runs the real poll three times over against a fleet that no longer contains it, and requires
the headline, the detail line and the Done button still to be there. **I did not reproduce the defect on
the phone, so I did not change the phone for I4** - only added the control that would redden if someone
later hung the session screen off the roster.

---

## The mutation table

Every row is a mutation I applied to the PRODUCTION file, ran, and then restored. The red messages are
copied out of the run. The logs are in `.temp/worker-h/` (ignored, this machine only).

| # | Finding | Mutation | Result | The red, as it printed |
|---|---|---|---|---|
| M1 | I4 | `SessionRoster` wraps each row's `SessionMenu` in its own `StopSessionProvider` - the answer is owned by the row again | **3 failed, 3 passed** | `Unable to find an element with the text: stopped 9c41e7a2 - process 51884 ended, row removed.` and `Unable to find an accessible element with the role "button" and name "Done"` |
| M2 | I4 | `SessionDetail` wraps its `selected && <SessionMenu>` in its own provider - same defect on the session page | **2 failed, 4 passed** | `Unable to find an element with the text: stopped 9c41e7a2 - process 51884 ended, row removed.` and `Unable to find an accessible element with the role "button" and name "Done"` |
| M3 | I7 | `if (inFlight.current) return;` removed from the Cockpit's stop action | **2 failed, 21 passed** | `expected "spy" to be called 1 times, but got 3 times` and `expected "spy" to be called 1 times, but got 2 times` |
| M3b | I4 | `if (inFlight.current) return;` removed from `closeDialog` - the dialog can be dismissed mid-request | **1 failed, 22 passed** | `Unable to find an element with the text: /A reason is required/.` |
| M4 | I7 | the busy guard removed from the phone hook's stop verb | **2 failed, 5 passed** | `expected "spy" to be called 1 times, but got 3 times` and `expected "spy" to be called 1 times, but got 2 times` |
| M5 | I8 | the in-dialog `.confirm-error` deleted and the app bar banner un-suppressed - the failure goes back outside the modal | **2 failed, 5 passed** | `Unable to find an element with the text: the Director on SORENLAPTOP could not be reached.` |
| M6 | I8 | `manage.setError(null)` put back into `onCancelStop` - backing out deletes the explanation again | **2 failed, 5 passed** | `Unable to find an element with the text: the Director on SORENLAPTOP could not be reached.` |

Every mutation reddened, and each reddened the tests belonging to ITS finding and left the others green
(M3 left the dismissal test passing; M5 left the Cancel tests passing; and so on), which is what makes
them separate controls rather than one alarm wired to seven switches.

### How I kept the busy-guard tests able to fail

Phase B recorded that a test which AWAITS the second press deadlocks under its own mutation instead of
going red: with the guard gone, the second call waits on the same outstanding answer as the first, and
the run hangs. **None of my busy-guard assertions awaits the second press.** The stop is held on a
promise I release by hand, the second and third presses are fired, and the call count is asserted
immediately and synchronously - the handler runs to its first `await`, so the mock is called before
control returns. Then the promise is released and the answer is checked. M3 and M4 both printed a
counting failure in a couple of hundred milliseconds; neither run hung.

---

## The suites, all run by me on this tree, after the mutations were restored

| Suite | Result |
|---|---|
| `@devthrottle/cockpit` | **326 passed**, 37 files, exit 0 |
| `@devthrottle/mobile` | **62 passed**, 11 files, exit 0 |
| `@devthrottle/client-core` | **1,042 passed**, 98 files, exit 0 |
| `npm run typecheck` (all four workspaces) | all four completed, exit 0 |

The counts before my work were 317 / 55 / 1,041 (inspection 1). I added nine Cockpit tests (six in
`stopAnswerOutlivesTheRow.test.tsx`, three in `stopSessionDialog.test.tsx`) and eight phone tests (seven
in `stopFailureAndBusyGuard.test.tsx`, one in `stopAnswerSurvivesRosterPoll.test.tsx`), and removed one
phone test - see below. The client-core count is unchanged by me; I touched nothing in that package.

**One honest wrinkle in the middle of the run.** My first `@devthrottle/client-core` run failed one test
(`stopSession carries the Gateway's own sentence out of a refusal`, in `src/api/stopSession.test.ts`)
while another Worker's edit to `packages/client-core/src/api/client.ts` was half-written in the shared
worktree. That file is theirs and I was told not to touch it. Once their work was committed I re-ran the
suite on the resulting tree and it was green, and the figure above is that re-run. I am recording the red
rather than quietly reporting only the green.

### A test I DELETED, and why

`apps/mobile/src/components/stopSessionSheet.test.tsx` had a describe block called *"the phone stop sheet
shows a failure and keeps what was typed"*. It could not catch I8: it seeded an error on the manage STUB
before the action and then searched the whole document, so it passed just as happily when the only copy
of the sentence was on the app bar's banner, outside the modal and under its overlay. It is replaced by
`stopFailureAndBusyGuard.test.tsx`, which drives the REAL hook and queries `within(dialog)`. The file
now carries a comment saying where the failure path went and why, so nobody restores the weak version
believing coverage was lost.

---

## What these tests do NOT cover - named, because it matters

- **No browser and no pixels.** jsdom has no layout and no stacking context. "The overlay covers the
  banner", "the dialog is on top and readable", "the error is visible on a phone-sized screen" are
  observable only in a real browser and NONE of them is proven here. What is proven is DOM containment -
  the sentence is inside the dialog element's subtree - and lifecycle - the answer is still mounted.
  Pixels are the QA seat's frames, not mine.
- **No real Gateway, no real roster poll.** The Cockpit tests re-render the roster by hand with the row
  absent; they do not run `rosterStore`'s two-second timer against a live `GET /sessions`. The phone test
  does run the hook's real interval, but against a mocked roster read.
- **No screen reader.** `aria-modal="true"` is on the dialog and the failure is now inside it, which is
  the structural half of the problem. Whether a screen reader announces the failure when it appears is
  not tested and was not tested before.
- **No proof about the OTHER shells.** I changed nothing on the desktop Director or the command line,
  and nothing below says anything about them.
- **The Cockpit's `SessionDetail` test mocks the page's heavy regions** (terminal, composer, action bar,
  chat, voice, source control, queue, screenshots). The thing under test - the `selected && <SessionMenu>`
  gate, the outlet context it reads, and the menu and provider around it - is real; the mocked parts are
  panes that cannot run in jsdom and have nothing to do with a stop. Said plainly so nobody reads "the
  real SessionDetail" as "the whole session page".
- **`SessionMenu` mounted outside the provider now throws.** That is deliberate (fail explicitly rather
  than render a stop control that silently cannot answer), and it is why
  `sessionsBadge.test.tsx` - which renders the real `SessionsView` - now mounts the provider. The four
  roster tests that stub `SessionMenu` out were left alone; they never mount it.

---

## Housekeeping

- No `cc-devthrottle session list` output, and no live fleet output of any kind, appears in this file or
  in anything I committed. (Manager's warning, `qa-recipe-notes.md`.)
- I did not touch `packages/client-core/src/api/client.ts`, anything under `src/`,
  `tools/cc-devthrottle`, or the Gateway.
- No shell reads or writes a verdict word. The Cockpit's existing test that renders a fifth,
  invented verdict through the same path still passes after the move.
- No merge to main and no pull request.

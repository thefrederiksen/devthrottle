# Worker D notes - the shared function, the Cockpit, and the phone

Phase B, mission "Stop a session", issue #2633. Branch `mission/stop-a-session`, worktree
`C:\ReposFred\devthrottle-stop-a-session`. Committed, not pushed - the Manager pushes.

Three commits, in order:

| Commit | What it is |
|---|---|
| `321394fa` | The work: one shared stop function, the Cockpit dialog, the phone sheet |
| `253aa584` | **Not mine and separable** - four pre-existing Cockpit test mocks that made the suite exit non-zero on green. See "What I found that was already wrong" |
| `1e65aaf9` | Two things the first pass got wrong, found by mutating: Enter did not submit on the phone, and the Cockpit reason box had a fixed element identifier |

---

## What I built

### 1. `packages/client-core/src/api/client.ts` - one shared function

`killSession(sessionId, signal)` is **deleted**. In its place:

```ts
export async function stopSession(
  sessionId: string,
  reason: string,
  signal?: AbortSignal,
): Promise<SessionStopOutcome>
```

It calls `POST /sessions/{sid}/stop` with `{ reason }`, throws through `GatewayError.from(res, "stop
that session")` on a non-2xx so the Gateway's own refusal sentence survives the hop, and returns
`SessionStopOutcome` - the exported type carrying `verdict`, `headline`, `details`, and every fact
underneath (`sessionId`, `shortId`, `processId`, `processEnded`, `rowRemoved`, `worktreePath`,
`worktreeHadUncommittedChanges`, `reason`, `stoppedBy`, and the compatibility `killed` / `removed`
pair).

Two decisions inside it worth naming:

- **`verdict` is typed `string`, not a union of the four words.** A union invites a switch, and a
  fifth word added on the Gateway would then reach a client with no case for it. The comment says so.
- **A 200 whose body carries no headline throws.** There is nothing honest to do with it: composing a
  sentence is what Ruling 5 forbids, and rendering an empty dialog is the silent success this mission
  exists to remove. The thrown message says the ANSWER could not be read and does not claim the stop
  failed, because it did not. This is the one sentence in my work that a client composes, and it is
  about the transport, not about a stop outcome. Named again as a gap below.

No `killSession` reference remains anywhere in `apps/` or `packages/`. The one mock of it
(`apps/cockpit/src/sessions/sessionsBadge.test.tsx:37`) was moved.

### 2. The Cockpit - `apps/cockpit/src/sessions/SessionMenu.tsx`

`Close session` is now `Stop session`, and the dialog is one dialog in two states.

- **The question.** It says what the stop does (and that files in the worktree are left as they are),
  then a labelled reason box. The label says a reason is required and what it is for. The confirm
  control is disabled while the box is empty or only whitespace, because the Gateway would refuse that
  stop and a control must not offer a click that can only come back refused. Enter submits, with the
  same rule.
- **The answer.** On success the dialog does NOT close. It shows `headline`, then each of `details` in
  the order the Gateway sent them, verbatim, behind a `Done` control. `onClosed` fires only when the
  answer is dismissed - by the button, by the backdrop, or by Escape - so the page navigating away can
  never destroy the answer before it has been read.
- **On failure** it stays open, shows the error where the dialog already showed errors, and keeps the
  typed reason so a retry does not begin by making the user write their sentence again.
- **Responsive** (rule 1): the dialog appears on the click, before anything is asked of the network;
  the confirm control reads `Stopping...` while the stop is in flight; nothing blocks.
- The header comment was rewritten - it named `killSession` and described a Close that showed nothing.

New CSS in `apps/cockpit/src/styles.css`: `.session-dialog-label`, `.session-dialog-headline`,
`.session-dialog-details` (+ `li`). Built from the existing tokens (`--text`, `--text-dim`, `--border`)
and sized to the neighbouring `.session-dialog-*` rules.

### 3. The phone - `apps/mobile/src/components/useSessionManage.ts` and `SessionAppBar.tsx`

Same words, same behaviour, laid out for a phone.

- `removeSession()` becomes `stopSession(reason)`. It calls the shared function and **returns the
  outcome**; it navigates nowhere. The old one went to `/` the instant the call returned, which is the
  same silent success the Cockpit had, one level down. `useNavigate` is gone from the hook entirely.
- The hook refuses a blank reason before it reaches the Gateway, and surfaces the Gateway's own
  sentence on the shared `error` banner rather than a word of its own.
- The confirmation sheet is the Cockpit dialog on a phone-sized card: the same reason label, the same
  refusal to submit an empty one, the same headline-then-details answer, and the return to the roster
  moved from "when the call returned" to "when the user dismissed the answer".
- The menu entry is **Stop session**, the Cockpit's word, not Remove.

New CSS in `apps/mobile/src/styles.css`: `.confirm-label`, `.confirm-input` (+ `:focus`),
`.confirm-headline`, `.confirm-details` (+ `li`), sized for touch (46px minimum, 16px input text so
iOS does not zoom on focus).

### 4. Nothing else

I did not touch the Director window, the Gateway, or the command line. Worker E's files were
uncommitted in this shared worktree throughout; I staged only my own paths and left theirs alone.

---

## Test numbers, as run at the end

| Suite | Command | Result | Exit |
|---|---|---|---|
| client-core | `npm test --workspace @devthrottle/client-core` | 98 files, **1041 tests passed** | 0 |
| Cockpit | `npm test --workspace @devthrottle/cockpit` | 36 files, **317 tests passed** | 0 |
| mobile | `npm test --workspace @devthrottle/mobile` | 9 files, **55 tests passed** | 0 |
| types | `npm run typecheck` | all four workspaces clean | 0 |

New test files:

- `packages/client-core/src/api/stopSession.test.ts` - 11 tests
- `apps/cockpit/src/sessions/stopSessionDialog.test.tsx` - 20 tests
- `apps/mobile/src/components/stopSessionSheet.test.tsx` - 15 tests
- `apps/mobile/src/components/useSessionManageStop.test.tsx` - 4 tests
- `apps/mobile/src/components/stopSessionEnterKey.test.tsx` - 3 tests

`.\scripts\test-local.ps1` was NOT run and says nothing about any of this - it runs no web tests at
all. The five files above are the whole of the coverage this work has.

---

## The mutation table

Every mutation was applied to the shipped source (never to a copy), the named test run, the red read,
and the file restored from a byte-for-byte backup. No mutation is left in the tree - checked, and the
suites above were re-run green afterwards.

| # | File | Mutation | Result | The red, as it actually printed |
|---|---|---|---|---|
| M1 | client.ts | route `/sessions/{sid}/stop` → `/sessions/{sid}` | 2 red | `expected '/sessions/9c41e7a2' to contain '/sessions/9c41e7a2/stop'` |
| M2 | client.ts | body `{ reason }` → `{}` | 1 red | `expected {} to deeply equal { Object (reason) }` |
| M3 | client.ts | throw when `verdict === "notOnFleet"` | 2 red | `no such session` |
| M4 | client.ts | `GatewayError.from(...)` → `new GatewayError(status, "POST stop failed: " + status)` | 2 red | `expected 'POST stop failed: 400' to be 'A stop needs a reason. Say why this s…'` and `expected 'POST stop failed: 502' to contain 'the Director on SORENLAPTOP could not…'` |
| M5 | client.ts | missing-headline guard disabled (`if (false)`) | 1 red | `expected { verdict: 'stopped', …(13) } to be an instance of GatewayError` |
| M6 | client.ts | `worktreeHadUncommittedChanges: … ?? null` → `?? false` | 1 red | `expected false to be null` |
| M7 | SessionMenu.tsx | confirm `disabled` drops the reason check | 2 red | `expected false to be true // Object.is equality` |
| M7b | SessionMenu.tsx | `doStop`'s own empty-reason guard removed, `disabled` kept | **GREEN at first** - see the note under it | (nothing) |
| M7b again | SessionMenu.tsx | same, after the Enter tests were added | 2 red | `expected "spy" to not be called at all, but actually been called 1 times` |
| M8 | SessionMenu.tsx | headline hardcoded to `Session stopped.` | 8 red | `Unable to find an element with the text: the Gateway wrote this exact sentence and the Cockpit did not` (and one per verdict) |
| M9 | SessionMenu.tsx | the OLD behaviour restored - close the dialog and call `onClosed` the moment the stop returns | 9 red | same "unable to find" on every headline, plus `Unable to find role="listitem"` |
| M10 | SessionMenu.tsx | special-case `stoppedNotDescribed` into an error box | 1 red | `Unable to find an element with the text: the Gateway's own words for stoppedNotDescribed` |
| M11 | SessionMenu.tsx | clear the typed reason on failure | 1 red | `expected '' to be 'doing the wrong work'` |
| M12 | SessionMenu.tsx | render only `details[0]` | 1 red | `expected [ Array(1) ] to deeply equal [ …(2) ]` |
| M12b | SessionMenu.tsx | send the reason untrimmed | 1 red | `expected '  doing the wrong work  ' to be 'doing the wrong work'` |
| M23 | SessionMenu.tsx | Enter no longer submits | 1 red | `expected "spy" to be called with arguments: [ …(2) ]` |
| M13 | SessionAppBar.tsx | confirm `disabled` drops the reason check | 2 red | `expected false to be true // Object.is equality` |
| M14 | SessionAppBar.tsx | the OLD behaviour restored - navigate to `/` the moment the stop returns | 9 red | same "unable to find" on every headline, plus `Unable to find role="listitem"` |
| M17 | SessionAppBar.tsx | headline hardcoded to `Session stopped.` | 8 red | as M8 |
| M18 | SessionAppBar.tsx | render only `details[0]` | 1 red | `expected [ Array(1) ] to deeply equal [ …(2) ]` |
| M19 | SessionAppBar.tsx | clear the typed reason on failure | 1 red | `expected '' to be 'doing the wrong work'` |
| M24 | SessionAppBar.tsx | `onConfirmStop`'s empty-reason guard removed | 2 red | `expected "spy" to not be called at all, but actually been called 1 times` |
| M25 | SessionAppBar.tsx | Enter no longer submits | 1 red | `expected "spy" to be called with arguments: [ 'spawned into the wrong mode' ]` |
| M20 | useSessionManage.ts | send the reason untrimmed | 1 red | `expected "spy" to be called with arguments: [ '9c41e7a2', …(1) ]` |
| M21 | useSessionManage.ts | empty-reason guard removed | 1 red | `promise resolved "undefined" instead of rejecting` |
| M22 | useSessionManage.ts | write our own failure word instead of the Gateway's | 1 red | `expected 'Stop failed' to be 'the Director on SORENLAPTOP could not…'` |

**M7b is the one that matters, and it is why this exercise was worth doing.** The first pass had the
empty-reason rule asserted twice - the control is disabled AND nothing is sent - but only the first
assertion had teeth. `fireEvent.click` on a disabled button never fires `onClick`, so removing the
guard inside the send left the suite green: the "nothing is sent" assertion was decoration. Adding
Enter (which a disabled attribute does not block) to both surfaces gave it teeth, and the same
mutation now goes red on both. That is also what surfaced the Enter divergence between the two
surfaces, which was a real behaviour difference, not a layout one.

There was one more mutation, a diagnostic rather than a proof: adding `gatewayErrorMessage` to the
mock in `attentionOrder.test.tsx` removed exactly its three unhandled errors, which is what
established that the Cockpit suite's eighteen errors were not mine.

---

## What I found that was already wrong

### The Cockpit suite exited non-zero while all 314 tests passed (fixed, in its own commit)

Four roster test files - `attentionOrder`, `rosterChangesBadge`, `rosterDeliveryBadge`,
`rosterSupervisionLine` - mock `@devthrottle/client-core/api/client` with a single export. The
restart-requests panel polls inside the roster they render and reaches for `gatewayErrorMessage` when
a read fails, so every poll threw an unhandled rejection: **18 errors, exit code 1, 314 tests
passing.** A suite that is red while everything passes is a suite whose exit code nobody can read.

I fixed it (commit `253aa584`, four one-line additions) because my brief requires these suites to be
green and I could not otherwise report an honest exit code. **It is not part of the stop work and it
can be dropped on its own** if the Manager or the Architect would rather it were filed as an issue.
Proven, not assumed: one file's fix removed exactly its three errors; all four took the run from 18
errors and exit 1 to none and exit 0, with the same tests passing.

### `docs/VisualStyle.md` does not cover the web surfaces at all

It says `> **Current framework scope:** WPF` at the top, and there is no occurrence of "cockpit",
"web", "css", "react" or "mobile" anywhere in its 743 lines. Its palette is WPF hex values
(`#1E1E1E`, `#CCCCCC`), and the browser shells use CSS custom properties (`--surface`, `--text`,
`--accent`) with light and dark variants. My brief says the guide governs every interface change I
make; for these two surfaces it cannot. **What I did instead:** matched the existing component
vocabulary in each shell exactly - the Cockpit's `.session-dialog-*` family and the phone's
`.confirm-*` family - and used only tokens already defined there. That is a judgement, not a rule
being followed, so it is worth a reviewer's eye. Someone should either extend the guide to the web
shells or say in it that the web shells are governed by their own token set.

### `apps/mobile/src/components/SessionAppBar.tsx` header comment (left alone)

Its historical paragraph still says the old `SessionManageBar` had "a destructive verb (Remove)". That
is accurate about the bar being described, which no longer exists, so I left it. Flagging in case a
reviewer reads it as current.

---

## Gaps - what I did NOT prove

1. **Nothing here has stopped a real session.** These are jsdom component tests against a stubbed
   `stopSession`. No browser, no Gateway, no Director, no screenshot. The real run and the pictures
   belong to the QA seat, by design, and my brief says so - but it means every claim above is about
   code behaving as its author expected, never about the product doing what an operator asked.
2. **No single test drives the Cockpit or the phone through the real `stopSession` to a real 400.**
   The hop is proven in client-core (the route, the body, and `GatewayError.from` lifting `{ error }`
   onto `err.message`); the render is proven in the shells with `stopSession` stubbed. The join
   between them is proven by construction - both shells import the one function and nothing else calls
   the route - and by nothing else. A wiring mistake between `SessionMenu` and the real function would
   not be caught by any test I wrote.
3. **The no-headline guard is a sentence a client composes.** It is about an unreadable ANSWER rather
   than about a stop outcome, and Ruling 5 is about stop outcomes - but it is the one string in my work
   that did not come from the Gateway, and if a reviewer thinks it belongs on the Gateway instead, that
   is a fair call to make.
4. **`killed` and `removed` are carried on the type and read by nothing.** The brief listed them, so
   they are there; only their shape is tested. If nothing ever reads them, they are two fields that
   exist to be carried, and a later reader will wonder why.
5. **The backdrop and Escape dismissal paths on the Cockpit answer are not tested.** `closeDialog` is
   one function and the button path is tested, so all three go through the same code - but I never
   watched a backdrop click or an Escape press produce the `onClosed` call.
6. **No visual proof of the CSS.** Nothing renders these styles in a real browser in any test. The
   classes exist and are applied; whether the dialog looks right at a phone width, in both themes, is
   unverified by me.
7. **`npx eslint apps packages` exits 1 on three PRE-EXISTING errors** in
   `apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx` and
   `apps/mobile/src/pages/VoiceMode.tsx` - all of the form "Definition for rule
   '<rule>' was not found", which is a missing eslint plugin, not code. None of the three files is one
   I touched, and every file I did touch lints clean. I left them; the fix is eslint configuration and
   is nobody's idea of this mission.
8. **The `stoppedNotDescribed` case is tested only as a string passing through.** That is the correct
   test for a client that must not know how many verdicts there are - but it means no test here would
   notice if the Gateway stopped sending that word at all.

---

## Nothing contradicted a ruling

I found nothing in Rulings 1-6, the Phase B handoff, or `SessionStopDtos.cs` that the code
contradicts. Two small things the brief said that turned out slightly differently, neither a conflict:

- The brief said `GatewayError.from` "already lifts that sentence out of `{ error }`, so it reaches you
  as `err.message`". True, and the client-core test pins it. What reaches the Cockpit's dialog is
  `describeAndReport` → `gatewayErrorMessage`, which prefers the server's reason and appends
  "Try again." to a RETRYABLE one. So the sentence a user sees on a retryable failure is the Gateway's
  sentence plus that hint - the Gateway's words are not altered, but they are not alone either. Worth
  knowing before anyone reads a screenshot and calls it a mismatch.
- The brief called `holdSession` the closest model for the new function's comment. I followed it, and
  the new comment is longer than `holdSession`'s because there is more that must not be got wrong
  (four success verdicts, a required reason, the meaningless fields under one verdict).

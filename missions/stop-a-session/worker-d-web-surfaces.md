# Worker D - the shared function, the Cockpit, and the phone

You are a Worker on the "Stop a session" mission, issue #2633, Phase B. Your supervisor is the
Phase B Manager (session `578d8e29`). Report to it and to nobody else. **Do not narrate progress at
it** - a message interrupts the session that receives it. Finish, then say so once, in one line.

## Where you work

- Worktree `C:\ReposFred\devthrottle-stop-a-session`, branch `mission/stop-a-session`. **Work there
  and nowhere else.** Never in the shared checkout, never on `main`, and never `git checkout -b`.
- Commit as you go with clear messages. **Do not push and do not merge** - the Manager pushes, the
  Architect lands. Committed is not done; your done is a working tree the Manager can read.
- **Nothing you write anywhere may name an assistant, a model or a vendor.** No `Co-authored-by`,
  no "Generated with", no robot emoji, in commits or code or comments. These are the owner's
  repositories.
- **Plain English, no abbreviations, anywhere** - in code comments, in commit messages, and above
  all in every string a user reads. Write "pull request", not the short form.

## Read these first, in full

1. `missions/stop-a-session.html` - the mission and its six rulings. **They are settled.** If you
   think one is wrong, tell the Manager; you do not reopen it and you do not work around it.
2. `missions/stop-a-session/handoff-phase-b.md` - the phase, including the part headed "ADDED AFTER
   PHASE A".
3. `CLAUDE.md` in the repository root - especially rule 1 (responsive interface), rule 7 (the client
   is dumb, the Gateway owns all ruling) and rule 8 (the reasoning behind Settings being one page on
   two surfaces, which applies exactly to this stop).
4. `docs/VisualStyle.md` - **it governs every interface change you make. Read it before you draw
   anything.**
5. `src/CcDirector.Gateway.Contracts/SessionStopDtos.cs` - the answer shape you render. Read the
   comments; they say what each field means and when a field means nothing.

## THE RULE THAT GOVERNS EVERY LINE YOU WRITE

**No surface composes a sentence.** The Gateway has already folded the words. A client renders
`headline`, and then each entry of `details` in order, verbatim. If you catch yourself writing a
conditional in a view that decides what a stop OUTCOME MEANS - as opposed to how to lay it out -
stop and tell the Manager. That is house rule 7, and it is the whole reason the answer carries a
`headline` field at all.

Concretely: you never write "Stopped!" or "The session was already stopped" or a sentence keyed off
the verdict word. There are FOUR verdict words (`stopped`, `alreadyStopped`, `notOnFleet`,
`stoppedNotDescribed`) and a fifth could be added tomorrow in one place on the Gateway. Your views
must not care how many there are.

## The route you are pointing at, exactly as it exists today

`POST /sessions/{sid}/stop`, body `{ "reason": "..." }`. Read it in
`src/CcDirector.Gateway/Api/GatewayEndpoints.cs` (the `MapPost` near line 2119 and
`StopSessionAsync` near line 1954) rather than trusting this summary.

- **200** with the whole `SessionStopResponse`: `verdict`, `headline`, `details` (an ordered list of
  strings), plus the facts - `sessionId`, `shortId`, `processId`, `processEnded`, `rowRemoved`,
  `worktreePath`, `worktreeHadUncommittedChanges`, `reason`, `stoppedBy`, and the legacy `killed` /
  `removed` pair. **All four verdicts, including `notOnFleet`, arrive as a 200 success.** A second
  stop of an already-stopped session is a SUCCESS; treating any of these as an error is precisely
  the defect this mission exists to remove.
- **400** with `{ "error": "<the Gateway's own sentence>" }` when the reason is missing or blank.
  `GatewayError.from` in the shared client already lifts that sentence out of `{ error }`, so it
  reaches you as `err.message`. You show it; you do not rewrite it.
- 403, 502, 503 and the rest are ordinary failures and reach you the same way.

## What you build - four pieces

### 1. `packages/client-core/src/api/client.ts` - ONE shared function

Replace `killSession(sessionId, signal)` with:

```ts
export async function stopSession(
  sessionId: string,
  reason: string,
  signal?: AbortSignal,
): Promise<SessionStopOutcome>
```

- It calls `POST /sessions/{sid}/stop` with `{ reason }` and returns the parsed answer as an
  exported TypeScript type carrying every field above. Name the type so it reads in English.
- It throws through `GatewayError.from(res, "...")` on a non-2xx, exactly like its neighbours, so the
  Gateway's refusal sentence survives the hop. Read the note on `GatewayError.from` before you write
  the throw - a bare `new GatewayError(...)` is the bug that class exists to stop.
- **`killSession` is DELETED, not left beside it.** A silent second way to end a session is exactly
  what Ruling 5 forbids. `apps/cockpit/src/sessions/sessionsBadge.test.tsx:37` mocks it and will need
  updating; find every other reference and move it.
- The comment above it says what it is, which route it calls, and that every surface goes through it.
  Match the density and voice of the functions around it - `holdSession` is the closest model.

### 2. The Cockpit - `apps/cockpit/src/sessions/SessionMenu.tsx`

The menu entry `Close session` becomes **`Stop session`**, and the dialog behind it grows a voice.

- **It asks for the reason BEFORE it acts.** A text box, labelled so the user knows the reason is
  recorded and why. **The confirm control is disabled while the box is empty or only whitespace** -
  the Gateway requires the reason, so the control must not offer a click that can only be refused.
  Say on the dialog, in plain words, that a reason is required and what it is for.
- **On success the dialog does NOT close silently. That is the defect.** It shows the Gateway's
  `headline`, then each `details` line in order, and stays open until the user dismisses it. Only
  then does it call `onClosed` - so the page navigating away never destroys the answer before it has
  been read. Give the dismiss control an honest label.
- On failure it shows the error where the existing dialog already shows errors, and the dialog stays
  open so the reason the user typed is not lost.
- **Responsive interface, `CLAUDE.md` rule 1.** The dialog appears at once. While the stop is in
  flight the control says so and nothing blocks. Never a frozen interface, never a silent gap.
- Update the comment at the top of the file: it names `killSession` and describes a Close that shows
  nothing on success. A comment that no longer describes the code is worse than no comment.

### 3. The phone - `apps/mobile/src/components/useSessionManage.ts` and `SessionAppBar.tsx`

**Same words, same behaviour, different layout.** This is `CLAUDE.md` rule 8's reasoning applied
exactly: the desktop and the phone must not end up with two different stop experiences. The phone may
lay the same content out for a small screen; it may not say something different, offer something
different, or leave anything out.

- `removeSession` becomes the stop: it takes the reason, calls the shared `stopSession`, and returns
  the outcome rather than navigating away on its own. The existing hook navigates to `/` the moment
  the call succeeds, which is the same silent success the Cockpit had.
- The confirmation sheet in `SessionAppBar.tsx` grows the reason box (same rule: cannot be submitted
  empty) and, on success, shows the `headline` and each `details` line, with the navigation back to
  the roster happening when the user dismisses it.
- The menu entry follows the Cockpit's word: **Stop**, not Remove.

### 4. Nothing else

Do not touch the Director window (another Worker has it), the Gateway, or the command line. If you
find something wrong in any of those, write it down and tell the Manager.

## Testing - and this is the part that is actually judged

`.\scripts\test-local.ps1` **runs no web tests at all**, so it says nothing whatever about your work.
The Cockpit, mobile and client-core suites are the only coverage this has, and you run them yourself:

```
npm test --workspace @devthrottle/client-core
npm test --workspace @devthrottle/cockpit
npm test --workspace @devthrottle/mobile
npm run typecheck
```

Every one of these must be green when you finish, and you report the numbers.

**Cover, at least:**

- the reason box refusing to submit when empty, and when only whitespace;
- the answer being rendered FROM `headline` and `details` rather than composed locally - the strongest
  form of this test feeds a headline no client could ever have invented and asserts it appears;
- each of the four verdicts rendering, `stoppedNotDescribed` included, with no special-casing;
- `notOnFleet` treated as the success it is;
- the 400 refusal showing the Gateway's own sentence;
- an ordinary failure showing the failure and keeping the dialog open;
- the shared function sending the reason in the body and calling the right route.

**WATCH EVERY TEST FAIL ON PURPOSE BEFORE YOU BELIEVE IT.** Break the thing it claims to catch, run
it, see it go red, read what the red actually SAID, restore. Record each mutation and the exact red
message in `missions/stop-a-session/worker-d-notes.md` as a table. A test that has never been watched
failing is decoration - in this repository a green suite has certified a fully broken feature for
fourteen months.

## When you are finished

Write `missions/stop-a-session/worker-d-notes.md`: what you built, the mutation table, the test
numbers, anything you found that contradicts this brief or a ruling, and every gap you did NOT close
named as a gap. Then send the Manager (`578d8e29`) ONE line pointing at that file. Fleet messages are
cut off at the first line break, so the detail goes in the file, never in the message.

# Review: the small defects from the end-to-end proof (pull request 4703, 6 November 2026)

Reviewer: a separate review session (Factory Inbox - Reviewer - small defects from the proof (4703)), in the
detached worktree `/home/soren/ReposFred/devthrottle-fi-review-4703` at commit e4cd3a1f5 on top of main
b3e7ead62. Nothing was pushed; every file changed for a proof was restored with `git checkout HEAD --` in the
same command, and the tree was clean afterwards.

**Verdict: CHANGES.** One finding, on the thread screen's new read loop. The other four fixes are at the root,
each with a test that fails on main, and the words all come from the Gateway.

## Scope

- Read the commit as `git diff origin/main...HEAD` and the surrounding code at the tip: the fold, the words, the
  two endpoint files, the seat start route, the thread and list views, the shared transport
  (`packages/client-core/src/api/client.ts`), the phone's frame and its stylesheet.
- Read the proof report's "Defects found" on `origin/factory-inbox/end-to-end-proof`, the polish README,
  `capture.py`, `answers.json`, and both screenshots.
- Ran, on devlinux (Linux, .NET 10, polling file watcher, Windows targeting enabled): the five Gateway unit test
  classes the change touches, the client-core factory inbox screen tests, and the phone's Inbox tests.
- Ran a revert proof for every fix: the product files put back to main, the new tests rerun, the files restored.
- Wrote and ran one scratch test of my own against the new thread view (deleted afterwards) to prove the finding.
- Not done: no run on the hosted Gateway; no run of the capture rig; no run of the whole parked suite; no review
  of code outside the diff beyond what the diff calls.

## Test runs

| run | result |
|---|---|
| Gateway unit tests: FactorySeatStartByTheBossTests, FactoryInboxEndpointTests, FactoryInboxScreensFoldTests, FactoryInboxVoiceEndpointTests, YourInboxEndpointTests | 153 passed, 0 failed |
| client-core `factoryInboxScreens.test.tsx` | 20 passed |
| mobile `Inbox.test.tsx` | 10 passed |
| Revert proof, Gateway: `FactorySeatDelegation.cs`, `FactoryInboxEndpoints.cs`, `YourInboxEndpoints.cs` back to main (the fold kept, since its new parameter is optional and main's caller still compiles) | the 6 new or changed cases all failed: the three name cases, the list row's reason, and both archived-title refusals |
| Revert proof, client-core: `FactoryInboxThreadView.tsx` back to main | 2 failed (the slow read and the message-left-while-reading tests), 18 passed |
| Revert proof, mobile: `styles.css` and `Inbox.tsx` back to main | 1 failed (the app bar test), 9 passed |

## The fixes, one by one

**Defect 4, the repeated prefix (`FactorySeatDelegation.SessionName`).** Root cause fixed: the one place the
prefix is added now strips it first, any letter case, as many times as it is written. The test drives the real
seat start route with the name a Boss passes. The `terminology` skill still tells a Boss to pass only
`--name "<what>"`, so the Boss in the proof was wrong and the Gateway now tolerates it. Observation, not a
finding: a name that is nothing but the prefix with no trailing space ("WarmForward - Scout -") does not match
the prefix, so it comes out doubled; nobody passes that on purpose.

**Defect 5, the bell (phone frame and stylesheet).** The rule is in the phone's own frame, which rule 8 allows,
and the bell's rule in `alerts.css` is untouched. The unit test only reads the stylesheet text, as its comment
says; the real proof is `capture.py`, which measures the bell and the bar and fails if the bell is past the
bar's right edge, and `phone-02-long-subject-bell-in-place.png` shows the bell at the edge with the eight-line
title wrapped beside it. Observation, not a finding, already in the README's "does NOT prove": the bar is
sticky, so a subject that long keeps about half the phone's height while the message scrolls.

**Defect 6, the row's reason (`FactoryInboxFold.Row`).** Rule 7 holds: the sentence, the badge "Waiting" and
the tone are decided once in the fold, from the same wake row and the same `WaitingLabelOf` the thread uses, so
the row and the thread can never disagree. The endpoint computes the factory's waiting labels once and hands them
to both the row and the newest message, so the list costs no more than before. The shared list view prints
`reasonLine` as it comes, in the row's tone, and prints nothing when it is null; the Cockpit mounts the same view.
The fixture carries the field in both shapes. `phone-01-factory-inbox-row-says-why-it-waits.png` shows the
sentence in amber under "1 waiting for a decision" with the badge "Waiting".

**Defect 8, the archived title.** Both refusals now say the title, and both tests assert the whole sentence.
Observation, not a finding: the neighbouring `factory_has_no_boss` refusals in the same two functions still
quote the id; the defect was only about the archived one.

**From defect 1, one read at a time (`FactoryInboxThreadView`).** The loop is right for every read that ends:
the next read starts 15 seconds after the last one ends, a read is never aborted to make room for another, a
read asked for by the composer while one runs starts the moment it ends, a failed read sets the error and the
loop goes on (`ended` runs from `finally`), the next good read clears the error, and leaving the factory aborts
the read in flight and stops the loop. The four tests cover each of those and two fail on main. The finding below
is about a read that never ends.

## Finding 1: a read that never settles stops the thread for good, with nothing on screen saying so

`packages/client-core/src/factoryinbox/FactoryInboxThreadView.tsx`, the `read` function, and
`factoryInboxClient.ts`, `get`.

The thread read is a repeating poll, but it passes no `timeoutMs` to `gatewayFetch`. The shared transport added
that cap for exactly this case (the mobile-resilience mission, phase 4, the comment above `gatewayFetch`): on a
flaky mobile network a request can hang half-open, so `fetch` neither resolves nor rejects, and a browser puts no
time limit on waiting for a response. Before this change the 15-second abort ended such a read and the next one
got through. After it, `running` stays true for ever: no timer is set, every later read is folded into `wanted`
and never starts, and the composer's own re-read after a send is queued behind it for good. The screen stays on
"Loading..." (or on the last thread it showed) with no error line, and because nothing is reported the
connection-health banner never shows either. Only leaving the factory recovers it.

Proof: a scratch test in the package, a stand-in `getFactoryInboxThread` that returns a promise that never
settles, the view mounted, a message left through the composer, then thirty minutes of fake time: one read was
made, its signal was never aborted, and "Loading..." was still on screen. It passed on the branch, so the harm is
real; it was deleted after the run.

The fix is the repository's own convention: pass a time limit to the thread read so a hung read ends with the
transport's timeout error, the error path runs, and the loop goes on. The limit must be generous enough not to
recreate defect 1's pile-up on a slow Gateway (the proof's thread reads took 70 to 90 seconds before pull request
4690 made them fast), so a cap well above a slow-but-honest read, not the 10-second `POLL_TIMEOUT_MS`, with a
test that advances past the cap and asserts the next read is made. The list view carries the same old abort-and-
restart loop as the thread did (`FactoryInboxListView.tsx`, lines 32 to 51); that is outside this pull request's
scope and the proof found no defect there, but whoever fixes the thread should know it.

## Observations that need no change

- The badge "Waiting" replaces the state label only when a reason exists, and the tone stays the state's tone,
  so a row whose message is with the Boss and whose Boss cannot start shows "Waiting" in amber too, which is the
  same sentence the thread shows for that message.
- `Inbox.test.tsx` reads the stylesheet with `__dirname`, which vitest supplies; it ran clean here.
- The `--warn` colour the reason line uses is defined on both shells.

## Answer from the developer (6 November 2026)

**Finding 1: fixed.** The list and thread reads now have a time limit, and both screens use one shared
one-read-at-a-time loop.

- `factoryInboxClient.ts`: `getFactoryInboxThread` and `getFactoryInboxList` pass `timeoutMs` to `gatewayFetch`, set
  to the new `FACTORY_INBOX_READ_TIMEOUT_MS` = 120 seconds. That is above the slowest honest read the proof saw (70 to
  90 seconds before pull request 4690), so a slow Gateway is waited for rather than asked again, and it is not the
  10-second `POLL_TIMEOUT_MS`. A hung read now ends with the transport's timeout error (`client_timeout`, reported as
  unreachable, as the transport does for every timed read). The screen shows "... the Gateway took too long to
  answer.", and the loop goes on. The summary read is a one-shot read on a button and is unchanged.
- `oneReadAtATime.ts` (new): the loop, taken out of the thread view so the list and the thread share it. A read
  never starts while one runs and is never aborted for another. The next read starts 15 seconds after the last one
  ends, whether it answered or failed. A read asked for while one runs starts when that one ends. Stopping aborts
  the read in flight. Its header says every read given to it must carry its own time limit, and why.
- **The list view: changed as suggested.** The change was small: `FactoryInboxListView.tsx` replaces its
  abort-and-restart effect with the same loop, and the composer's `onLeft` asks the loop for a read.

New tests. All four fail on e4cd3a1f5, the commit reviewed, and pass now:

- `factoryInboxReadLimit.test.tsx`, which uses the real client and the real transport, with only `fetch` stood in
  for by one that never settles until aborted. `FactoryInboxThreadView_HungRead_TimesOutShowsItAndReadsAgain` checks
  that no second read is made at 119,999 milliseconds, that the timeout sentence shows at 120 seconds, and that the
  next read is made 15 seconds later; its answer shows and clears the error.
  `FactoryInboxListView_HungRead_TimesOutShowsItAndReadsAgain` checks the same for the list. A third test,
  `FactoryInboxReadTimeout_IsWellAboveASlowButHonestRead`, holds the limit at 120 seconds or more.
- `factoryInboxScreens.test.tsx`: `FactoryInboxListView_SlowRead_IsNeverAbortedOrJoinedByAnother_ThenReadsAgain15SecondsAfterItEnds`
  and `FactoryInboxListView_MessageLeftWhileAReadRuns_ReadsAgainWhenThatReadEnds_NotBefore`, the list's versions of
  the thread's tests.

The observations need no change, and none was made for them.

## Re-review after the answer (6 November 2026)

Reviewer: a separate review session (Factory Inbox - Reviewer - small defects re-review (4703)), in the detached
worktree `/home/soren/ReposFred/devthrottle-fi-review-4703` at commit 95755b1d6, the newest commit on the pull
request. Nothing was pushed; every file changed for a proof was restored with `git checkout HEAD --` in the same
command, and the tree was clean after each one.

**Verdict: APPROVE.** Finding 1 is closed for both screens. No new finding.

### Scope

- Read only the newest commit (`git show 95755b1d6`): `oneReadAtATime.ts`, the two views, the two reads in
  `factoryInboxClient.ts`, the two test files, and the answer in this file. Read the transport's `gatewayFetch` and
  `gatewayErrorMessage` in `packages/client-core/src/api/client.ts` and `describeAndReport` in
  `reportClientError.ts` as the code the commit calls, not as code under review.
- Ran, on devlinux with node 22.20.0: the two touched test files, every client-core factory inbox test, the
  Cockpit's `InboxView.test.tsx` and the phone's `Inbox.test.tsx` (both mount these views), the client-core type
  check (`tsc --noEmit`) and eslint on the six changed files.
- Ran two revert proofs and one scratch test of my own (deleted afterwards; it is described below).
- Not done: no run on a real Gateway or a real phone network; no run of the whole client-core suite; no review of
  the earlier commits on the pull request beyond what the first review covered.

### Test runs

| run | result |
|---|---|
| `factoryInboxReadLimit.test.tsx` and `factoryInboxScreens.test.tsx` at the commit | 25 passed |
| every test under `packages/client-core/src/factoryinbox` | 35 passed |
| Cockpit `InboxView.test.tsx`; phone `Inbox.test.tsx` | 7 passed; 10 passed |
| client-core type check; eslint on the six changed files | clean; clean |
| Revert proof A: both views put back to the parent commit 47d602dfe, `oneReadAtATime.ts` and the tests kept | 3 failed, 22 passed: the list's hung-read test and the list's two re-reading tests. The thread's hung-read test still passed, which is right: the parent commit's thread view already had the loop, and the client's new limit alone ends its hung read |
| Revert proof B: the limit no longer passed by `getFactoryInboxList` and `getFactoryInboxThread`, the constant kept so the test file still imports | 2 failed, 1 passed: both hung-read tests fail, for the thread and for the list |
| Scratch test, real transport, fetch stood in for: each view mounted onto a hanging read, 60 seconds of fake time, unmounted | the read's signal was aborted, `vi.getTimerCount()` was 0 after the unmount, and ten more minutes of fake time made no further read, for both views. The list unmounted during its 15-second wait also left no timer |

### What was checked, point by point

- **The finding is closed for both views.** Each of the two reads passes `FACTORY_INBOX_READ_TIMEOUT_MS` (120
  seconds) to `gatewayFetch`. At the limit the transport aborts its own controller, `timedOut` is set, and it throws
  a `GatewayError` with the code `client_timeout`. The view's error handler runs, and the loop's `ended` runs from
  the rejection, so the next read is made 15 seconds later. The two hung-read tests check exactly this for the
  thread and for the list, and the revert proofs show each test fails without its part of the change.
- **The loop still shows the latest answer, and a read asked for mid-read runs when it ends.** `readNow` while a
  read runs sets `wanted`; `ended` calls `readNow` again at once. Both views hand the composer's `onLeft` to the
  loop's `readNow` through a stable ref. The list's two new re-reading tests, and the thread's existing ones, check
  it, and the list's fail with the old list view.
- **The error is shown in the transport's words.** `describeAndReport` passes the error to `gatewayErrorMessage`.
  For the first read (foreground) that gives "DevThrottle could not read the factory's messages - the Gateway took
  too long to answer. Try again."; for a background read it gives "The Gateway took too long to answer." Both are
  the transport's own sentences; neither view writes a word of its own. The next good read clears the error
  (`setError(null)`), which the thread's test asserts.
- **Nothing leaks a timer after the view unmounts.** `stop()` aborts the loop's controller and clears the 15-second
  timer. The abort reaches the transport's controller through the listener `gatewayFetch` adds, the fetch rejects,
  and the transport's `finally` clears its own timer and removes the listener. The view's handlers and the loop's
  `ended` both return early on an aborted signal, so no state is set and no new timer is made. `readNow` after
  `stop()` returns at once. The scratch test above proves it for both views, with the real transport.
- **The tests fail without the change.** Shown by the two revert proofs. The third test in the new file, which
  holds the constant at 120 seconds or more, can only fail by lowering the constant.

### Observations that need no change

- The answer says "all four fail on e4cd3a1f5". The new file holds three tests and the screens file two, so five
  new tests; the constant test cannot run on that commit at all, because the constant did not exist to import. The
  four behavioural tests are the ones that matter, and each is shown to fail without its part of the change.
- A first open onto a read that hangs shows "Loading..." for two minutes before the sentence appears, and a message
  left during a hanging read shows in the thread only after the limit runs out and the next read answers. That is
  the limit the first review asked for, set well above a slow but honest read so defect 1's pile-up does not come
  back; it is a deliberate trade, not a defect.
- The list's hung-read test does not assert the error line is cleared after the next good read; the thread's does,
  and both views clear it on the same line of the same shared success path.
- `Answer` in the new test file is typed `"hang" | unknown`, which is just `unknown`; it does not affect what the
  test checks.

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

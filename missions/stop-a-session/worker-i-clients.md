# Worker I - clients that rule for themselves, and a name with a slash in it

You are a Worker on the "Stop a session" mission, Phase C. Your manager is session `e7ea69df`.
Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Work there and
nowhere else. Do not merge to main. Do not open a pull request.

Read these first, in full:

1. `missions/stop-a-session/inspection-1.md` - findings **I5** and **I6** are yours.
2. `missions/stop-a-session/architect-ruling-on-inspection-1.md` - their rows in the triage table.
   I5 is called "the mission's central sin in miniature". Read that row carefully.
3. `missions/stop-a-session.html` - Rulings 3 and 5.

## I5 - clients turn an unknown outcome into a definite outcome

Three places, and one of them makes the opposite unsupported claim to the other two.

- `tools/cc-devthrottle/src/session_ops.py:766` prefixes **every** Gateway exception with
  `Not stopped:`. A lost reply or a timeout can happen *after* the stop, and the Gateway deliberately
  says that it does not know whether the command was carried out. The inspection ran the real command
  against the router's timeout sentence and got
  `Not stopped: ... It is not known whether the command was carried out.` - the client contradicting
  the server in the same line.
- `src/CcDirector.Avalonia/StopSessionDialog.axaml.cs:59`, `:169` does the same with
  `The session was not stopped:` on every exception. Its own missing-headline error conveys
  uncertainty, and the prefix overrides it. Its test at `StopSessionDialogTests.cs:202` requires the
  prefix without ever testing an uncertain outcome, which is why it did not catch this.
- `packages/client-core/src/api/client.ts:1938` makes the opposite unsupported claim on a malformed
  successful body: `The session was stopped, but...`. HTTP success alone cannot tell `stopped` from
  `notOnFleet`, and an unreadable body cannot supply the missing fact.

**No client asserts an outcome the Gateway did not state.** A client may say what *it* knows - that it
could not get an answer, or that the request was refused - and it must not turn that into a statement
about whether the session is running.

Where a client genuinely knows the outcome is definite, it may say so. The command line's
refuse-before-the-call branch for a missing reason is exactly that case: no call was made, nothing was
stopped, and its `Not stopped:` there is correct. Leave it alone.

Where a client can *learn* whether a failure was definite, carrying that fact through is legitimate
work rather than scope creep. The Python module's own comment says a 400 about the reason "cannot be
told apart from a 500 by any other means here" because the shared client raises one exception type and
discards the status code. If you carry the status through, the refusals can keep a definite wording
and the transport failures can stop claiming one. If you do not, then one wording that claims nothing
about the session covers both - but say which you chose and why.

Successful answers with valid headlines are already rendered verbatim on all three surfaces. Do not
disturb that. The Cockpit's retryable path appends "Try again." through `gatewayErrorMessage`; Phase B
recorded it and the inspection did not raise it - look at it, and report rather than change it unless
it actually asserts an outcome.

## I6 - a repeat stop by a name containing a slash fails instead of answering

`tools/cc-devthrottle/src/session_ops.py:699`, `:758`. An existing name resolves to its session
identifier, so the first stop works. Once the row is gone, `_stop_target` returns the **typed name**,
and the caller interpolates it straight into `f"sessions/{sid}/stop"` with no path-segment encoding.
For a name like `Mission / Worker` the request becomes `sessions/Mission / Worker/stop`, which is no
longer the stop route at all. `?` and `#` acquire URL syntax instead of staying part of the target.

The inspection put that exact unencoded path through the real endpoint: **404** raw,
**200 notOnFleet** encoded. This restores the second-stop error Ruling 3 exists to remove, for an
entirely valid class of session names - and this fleet's own naming convention
(`<Mission> - <Role> - <what this seat does>`) produces names full of punctuation.

Encode the path segment. Check every other place in that module that interpolates a caller-typed
target into a path and say in your notes whether any of them has the same hole - fix the ones that do
in the stop's own call path, and report the rest rather than widening your scope silently.

## THE STANDARD, and this is the part the earlier phases failed

Every fix gets a test **watched failing against the production code path**, not against an injected
stand-in for it. The inspection's most valuable result was that replacing a production method with a
constant left all 68 tests in one suite passing, because the tests injected their own substitute.

For your two findings:

- **I6's existing tests stub the request helper**, so the path never meets HTTP routing and the defect
  is invisible to them. Your test must subject the composed path to real routing - the inspection did
  it against the real endpoint and got 404 against 200. If you cannot reach a real route from the
  Python suite, then test that the path the command hands to its Gateway helper is the encoded one
  **and** add a test on the Gateway side proving the encoded form routes and the raw one does not.
  Say plainly which of those you did.
- **I5's desktop test requires the prefix without testing an uncertain outcome.** Replace it with one
  that drives the dialog with the Gateway's own "it is not known whether the command was carried out"
  sentence and asserts the window does not contradict it.
- Then **mutate each fix and watch it go red**: put the prefix back; put the raw path back; put the
  "was stopped" claim back. Record the red message exactly as it printed. If a mutation leaves the
  suite green, that test is decoration and you have not finished.

## What you must NOT do

- Do not touch anything under `apps/` - another Worker is in both shells this phase. `client.ts` in
  `packages/client-core` is yours and theirs is not; if a shell needs changing because of what you
  did to it, tell your manager rather than editing it.
- Do not touch `src/CcDirector.Gateway`, `src/CcDirector.ControlApi` or
  `src/CcDirector.Gateway.Contracts` - two other Workers are in those. `src/CcDirector.Avalonia` is
  yours. If your I6 work genuinely needs a Gateway-side routing test, tell your manager first and
  wait; do not open that file on your own.
- Do not add a verdict word anywhere, and do not branch on one in a client.
- No abbreviations in anything you write. No mention of any assistant, vendor or model in any commit
  message, comment or document.

## What to run before you report

- The full `cc-devthrottle` Python suite from its own tool directory, and the stop test file. Note
  from the inspection: running it with `FORCE_COLOR=1` produces spurious wrapping failures - do not
  set it. Two failures in that suite are pre-existing email and spawn help-rendering tests failing
  inside the installed command-line dependencies; confirm that is still exactly what you see and say
  so, rather than inheriting the claim.
- `@devthrottle/client-core`, and `npm run typecheck` across all four workspaces.
- `CcDirector.Avalonia.Tests` to completion.
- **Never write a result row before the run that fills it.** Two phases of this mission have already
  been caught doing exactly that.

## What to hand back

Commit and push on the branch as you go. Write your notes to
`missions/stop-a-session/worker-i-notes.md`: what you changed, the wording you chose for each client
and why, the mutation table with the red messages as they actually printed, the suite numbers you
actually ran, and - named honestly - what your tests still do not cover. Then send your manager
(`e7ea69df`) ONE single-line message saying you are done and pointing at that file. Fleet messages
truncate at the first newline.

## Pushing, when you are not the only seat on this branch

Four Workers and a QA seat are all committing to `mission/stop-a-session`. Before every push, run
`git fetch origin` and then `git rebase origin/mission/stop-a-session`. **Never force-push** - a
force-push on this branch deletes somebody else's commit, and it has already happened once this
phase. If a rebase conflicts in a file you were told not to touch, stop and tell your manager rather
than resolving it.

## Put your own session identifier at the top of your notes file

Open `worker-<letter>-notes.md` with one line naming the session identifier you are running as
(`cc-devthrottle session whoami` prints it), your seat letter, and which agent you are.

This is not bookkeeping. The owner has asked that the mission's report show every session that
worked on this feature, and **six of the fifteen seats cannot be named** because they finished, were
reaped, and never wrote their own identifier down anywhere. The Director's logs do not carry it and
a session key is refused on the Gateway's session-history route. One line from you, now, is the
whole fix. See `missions/stop-a-session/who-built-it.md`.

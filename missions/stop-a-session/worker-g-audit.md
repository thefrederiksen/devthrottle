# Worker G - a completed stop that nobody recorded

You are a Worker on the "Stop a session" mission, Phase C. Your manager is session `e7ea69df`.
Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Work there and
nowhere else. Do not merge to main. Do not open a pull request.

Read these first, in full:

1. `missions/stop-a-session/inspection-1.md` - finding **I1** is yours, and only that one.
2. `missions/stop-a-session/architect-ruling-on-inspection-1.md` - the I1 row of the triage table.
3. `missions/stop-a-session.html` - Ruling 4, which the owner accepted **on the explicit ground that
   stops are audited**. That is what makes this finding a P1 rather than a tidiness item.

## What is wrong

In `StopSessionAsync` (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, around `:2024`, `:2025`,
`:2059`) the handler passes the HTTP request's cancellation token into the tunnel and appends the
audit row **only after a successful reply**. So:

- If the Director executes the stop and the caller disconnects before the reply arrives, cancellation
  leaves the handler before the append. **Zero audit rows for a stop that happened.**
- A tunnel timeout or a lost acknowledgement returns early through `TunnelFailure` and also appends
  nothing, even though the Director may well have carried the stop out.
- The reason is not even sent down the tunnel: the payload argument is `null`.

This is reachable from the shipped desktop dialog - closing it cancels its request
(`StopSessionDialog.axaml.cs:196`) and its client has a ten-second timeout (`GatewayClient.cs:109`).
It needs no database fault. The inspection reproduced it against a loopback host running the real
endpoint: the cancelled probe produced zero audit rows, the otherwise identical uncancelled control
produced one row with the exact reason.

## What to build

**A completed stop cannot go unrecorded, and where the outcome is genuinely unknown, record THAT -
an "attempted, outcome unknown" row is a fact; silence is not.**

1. **Send the reason down the tunnel** rather than the null payload, so the Director has the caller's
   words. Check what the Director does with a `kill` payload today before you change the call - if it
   ignores it, say so in your notes rather than inventing a consumer for it.
2. **The audit append must not be able to be cancelled by the caller going away.** The append is the
   record of a destructive act that has already happened; it does not belong to the request's
   lifetime. Do not pass the request token into it, and make sure an `OperationCanceledException`
   raised anywhere after the command was dispatched still leaves a row behind.
3. **Record the unknown outcome as unknown.** Where the command was dispatched and the Gateway cannot
   learn what came of it - cancellation, tunnel timeout, a dropped acknowledgement - write a row
   saying the stop was attempted and its outcome is not known, carrying the reason and the actor.
   **Do not invent a verdict for it and do not add a fifth `SessionStopVerdict` word** - the caller is
   gone, so no response is being folded; this is an audit detail, not a verdict.
4. **Do not write a row where nothing was dispatched.** A stop refused for a missing reason, a
   `notOnFleet` answer, a Director that was never connected - nothing was stopped on any of those, and
   a row there would attach a stop to something that did not happen. If you genuinely cannot tell
   "never dispatched" from "dispatched, answer lost", record it, and say in your notes that you could
   not tell them apart and why: silence is the worse error of the two.

Keep the existing best-effort behaviour of the append itself - a database fault must still not fail
the response, because the session is already stopped by then and answering an error would be a lie
about the one fact this verb reports. Keep it logging loudly.

## THE STANDARD

Every fix gets a test that is **watched failing against the production code path**. For this finding
that means the real endpoint, reached over HTTP, with a real audit log wired and a controlled tunnel -
which is exactly the shape the inspection used to find it. A test that calls a helper you extracted
and never subjects the handler to a cancelled request proves nothing about a cancelled request.

Specifically, there must be a test that:

- cancels the HTTP request after the Director has carried the stop out but before the reply is
  released, and asserts an audit row exists with the reason and the actor;
- has an uncancelled control alongside it, so the reader can see the two differ only in the
  cancellation;
- covers the tunnel-timeout road to the same conclusion.

Then **mutate**: put the append back after the reply, or hand the request token back to it, and watch
those tests go red. Record the red message exactly as it printed. If a mutation leaves the suite
green, that test is decoration and you have not finished.

## What you must NOT do

- Do not touch `src/CcDirector.ControlApi/SessionCommandExecutor.cs`, `SessionStopFold.cs`, or
  `src/CcDirector.Gateway.Contracts/SessionStopDtos.cs` - another Worker is in those this phase and
  is changing the verdict shape. If your change needs something from them, tell your manager.
- Do not touch anything under `apps/`, `packages/`, `tools/cc-devthrottle`, or
  `src/CcDirector.Avalonia`.
- Do not add a fifth verdict word.
- Do not kill any process you did not start.
- No abbreviations in anything you write. No mention of any assistant, vendor or model in any commit
  message, comment or document.

## What to run before you report

- `CcDirector.Gateway.UnitTests` to completion, with the numbers. Note that under
  `.\scripts\test-local.ps1` this suite is stopped by the 120-second ceiling - that is pre-existing
  and measured, not yours, but it means you must run it alone to get a result at all.
- The stop route tests in the **parked** `CcDirector.Gateway.Tests` suite, which is where the
  cross-tenant and host-bound route tests live. Phase A shipped a cross-tenant regression that only
  the parked suite could see. Run it.
- **Never write a result row before the run that fills it.** Two phases of this mission have already
  been caught doing exactly that.

## What to hand back

Commit and push on the branch as you go. Write your notes to
`missions/stop-a-session/worker-g-notes.md`: what you changed, the mutation table with the red
messages as they actually printed, the suite numbers you actually ran, and - named honestly - what
your tests still do not cover. Then send your manager (`e7ea69df`) ONE single-line message saying you
are done and pointing at that file. Fleet messages truncate at the first newline.

## Pushing, when you are not the only seat on this branch

Four Workers and a QA seat are all committing to `mission/stop-a-session`. Before every push, run
`git fetch origin` and then `git rebase origin/mission/stop-a-session`. **Never force-push** - a
force-push on this branch deletes somebody else's commit, and it has already happened once this
phase. If a rebase conflicts in a file you were told not to touch, stop and tell your manager rather
than resolving it.

# Worker B - the route, the fold, the refusal, the allow list and the audit

You are a Worker on the "Stop a session" mission, Phase A. Your Manager is session `ee59e5d0`.

**Read first, in full:** `missions/stop-a-session/handoff-phase-a.md` (the phase and the outcome
contract - your half of it is written out there word for word), then `missions/stop-a-session.html`
sections 4 and 5 (the six rulings - settled, you do not reopen one), then
`missions/stop-a-session/architect-state.md` (the code facts and the ROUTE DECISION).

Work in this worktree, on branch `mission/stop-a-session`. Do NOT merge and do NOT push - tell your
Manager when you are done and it commits. Do not touch `SessionCommandExecutor`, `Session`, or
anything under `src/CcDirector.Core/Git/`: Worker A owns those and you will collide. Do not touch
the Python command line, the Cockpit, the Director window or the mobile app.

The answer shapes are already written, in `src/CcDirector.Gateway.Contracts/SessionStopDtos.cs`.
Do not invent fields and do not rename any. Worker A is filling in `DirectorStopResult` on the
Director side at the same time; you consume it.

## Items 2, 3, 4, 5 and 6 of the seven

### 1. POST /sessions/{sid}/stop, body { "reason": "..." }

In `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, beside the existing session verbs. It locates
the session, forwards the Director's `kill` verb over the tunnel exactly as `DELETE /sessions/{sid}`
does today, and folds the answer.

### 2. DELETE /sessions/{sid} becomes a thin forward into the SAME handler

One stop, one fold, one audit trail, behind two doors. It is kept because a native phone client
(`phone/CcDirectorClient/Voice/GatewayClient.cs`) does not deploy inside the Gateway container and
would break if it went.

**The reason requirement lives on the POST DOOR, not inside the shared handler.** This is the
Manager's reading of the state note's own emphasised sentence - *"an agent's key can only ever stop
a session with a reason attached"* - and it is the only reading that does not break shipped clients
in the middle of the mission. DELETE carries no body and no reason; it stays REFUSED to session
keys (see the allow list below), so the only callers that can reach it are devices and people. The
shared handler therefore takes an OPTIONAL reason. Write that reasoning into the code as a comment
where the refusal lives, so the next reader does not "fix" it.

When a stop arrives with no reason through the DELETE door, the audit note must say honestly that no
reason was given. **Do not fabricate one.**

Both existing DELETE callers - `killSession` in `packages/client-core/src/api/client.ts` and the
native phone client - read the STATUS CODE ONLY and ignore the body entirely (verified by the
Manager). So the response shape may become the full stop answer. Say so in the comment; do not
claim more than that.

### 3. The refusal - item 3

A stop through `POST .../stop` with no reason, or a blank or whitespace one, is refused with **400**
and a body whose sentence says plainly that the REASON is what is missing. The Gateway writes the
sentence; the command line will print it and add the one thing only it knows (how to type the flag).
Do not put the flag name in the Gateway's sentence - the Gateway does not know what a client's flags
are called.

### 4. notOnFleet is a SUCCESS - item 4

No session in the account carries that identifier: **200**, `Verdict = "notOnFleet"`. Do NOT route
it through `SessionUnavailable`, which answers 404 and would make a second stop an error - the exact
failure Ruling 3 exists to prevent. The headline must say that **no machine was asked and no
machine's processes were searched**, because claiming "it is gone" when nothing looked is the worse
of the two mistakes.

A Director that is located but NOT REACHABLE is different and stays a failure (`TunnelFailure`, as
today). That is Ruling 3's second failure case. Do not fold it into `notOnFleet`.

### 5. THE FOLD - item 2, and it is the whole of Ruling 5

Put it in its own class, tested without a server, the way `VoiceDisplayFold` and
`NetworkConnectionVerdictFold` are. Suggested: `src/CcDirector.Gateway/Api/SessionStopFold.cs`.
**Every sentence any surface will ever show is composed here and nowhere else.** The command line,
the Cockpit, the Director window and the phone all render these strings verbatim.

Headline, by verdict:

- stopped, row removed:      `stopped <shortId> - process <pid> ended, row removed`
- stopped, no row to remove: `stopped <shortId> - process <pid> ended, no row was left to remove`
- already stopped, row was cleared:
  `already stopped <shortId> - no process was running; the row it left behind has been cleared`
- already stopped, no row either:
  `already stopped <shortId> - no process was running, and no row was left to remove`
- not on this fleet (ONE line, no line break):
  `not on this fleet - nothing in this account carries the id <shortId>, so no machine was asked and no machine's processes were searched`

Details, in this order, and only when they apply:

1. the worktree line, when the session held one (Ruling 2 REQUIRES it):
   - `the worktree <path> was left untouched - it has uncommitted changes in it`
   - `the worktree <path> was left untouched - it had no uncommitted changes`
   - `the worktree <path> was left untouched - whether it has uncommitted changes could not be determined`
2. the reason line, when one was given: `reason: <what the caller said>`

**The unknown case is not the clean case.** A null `WorktreeHadUncommittedChanges` gets the third
sentence, never the second. Write a test that pins exactly that, because it is the single most
likely thing to be got wrong later.

**The short identifier.** A GUID shortens to its first eight characters, the way this fleet writes a
session identifier everywhere else. **Anything else passes through whole.** The not-on-this-fleet
case is reached exactly when the caller typed something no session matched, and that is often a NAME:
truncating it would print `nothing in this account carries the id Stop a s`, which reads as a
corrupted answer rather than an honest one. Test both shapes. The same note is on `ShortId` in
`SessionStopDtos.cs`.

Plain English, no abbreviations - it is a house rule, and these strings are the product's own words.

### 6. The allow list - item 5

`src/CcDirector.Gateway/Util/SessionKeyGuard.cs`:

- add `"stop"` to the three-segment `POST /sessions/{sid}/*` switch
- add `DELETE /sessions/{sid}/request-deletion` so a flag can be cleared (Ruling 6). Match it by
  structure and by exact length, the way the rest of that file does.
- **LEAVE the bare `DELETE /sessions/{sid}` REFUSED to session keys.** That refusal is what makes
  the owner's ruling exact. Add a test that pins it as still refused - an allow list is only worth
  what its refusals are worth.
- **Update the class comment to match what the list now contains.** That file says of itself that
  prose which no longer describes the list is worse than a wrong entry, because the next reader
  trusts it. The comment currently says agents have `request-deletion` as their clean way to end a
  session; that is no longer the whole truth.

### 7. The audit record - item 6

`GovernanceAuditLog`, category `intervention`. The owner accepted "any session may stop any other"
on the explicit ground that it is audited, so this is load-bearing, not decoration.

The existing intervention event types are `needed`, `human-rescued`, `human-redirected`,
`human-cancelled`, `resolved`. **None of them fits.** `human-cancelled` would be a lie whenever one
agent stops another, which Ruling 4 makes the ordinary case. So **extend the validated list
deliberately**: add a `stopped` event type to the intervention category in
`src/CcDirector.Gateway.Contracts/GovernanceAuditEventDtos.cs`, and add it to `ActorRequired` in
`GovernanceAuditLog` - who stopped it is exactly the audit fact that must never be null here.

- `Actor` = who asked. `AuthMiddleware.CallingSession(ctx)` gives the calling session when a session
  key was used; a device key and the shared machine token are the other two shapes. Produce an
  honest short string for each, and `unknown` when nothing was authenticated - never a guess. This
  same string is `StoppedBy` on the response.
- `Detail` = the reason, capped at `GovernanceAuditLog.MaxDetailChars`. When no reason was given
  (the DELETE door), say so in words rather than leaving it null.
- `SessionId` = the session that was stopped.
- Write it for `stopped` and `alreadyStopped`. **Do not write one for `notOnFleet`** - nothing was
  stopped and there is no session in this account to attach the row to. Say that in a comment as a
  deliberate gap, not an oversight.

**Note this consequence and tell your Manager:** `OutcomeLedgerReporter` counts every
intervention-category row per session, so stops will now appear in that report's intervention count.
That is defensible - a session that had to be stopped did require an intervention - but it is a real
change to an existing report and it must be said out loud, not discovered later.

Wire the `GovernanceAuditLog` through to `GatewayEndpoints.Map` from `GatewayHost` (it is already
constructed there as `_governanceAudit`). Make the parameter nullable for tests, but **do not let a
null audit pass silently**: log loudly when a stop is served with no audit log wired, and write a
test that a stop DOES append a row when one is wired. A check whose pass condition is an absence
certifies a run that never happened.

## Tests - and watch every one fail on purpose

The fold is testable with no server; test it directly and hard. Route-level behaviour belongs with
the existing Gateway tests (`src/CcDirector.Gateway.UnitTests/`, and `Gateway.Tests` if the shape
you need is host-bound - that suite is PARKED and your Manager will run it with `-Parked`).

Cover: a live process stopped; a row with no process; no session at all (200, `notOnFleet`); a
second stop straight after the first; a stop with no reason (400, and the sentence names the reason);
a stop with a blank reason (same); a session holding a dirty worktree; a clean one; a worktree whose
state could not be determined (the THIRD sentence, not the clean one); the audit row is written with
the actor and the reason; `notOnFleet` writes NO audit row; the allow list permits `stop` and permits
`DELETE /sessions/{sid}/request-deletion`; the bare `DELETE /sessions/{sid}` is STILL refused to a
session key.

**Before you believe any of them:** revert the change, run the test, watch it go RED with the symptom
it claims to catch, then restore. Tell your Manager which ones you watched fail and what the red
said. A test that has never been watched failing is decoration - in this repository a green suite has
certified a fully broken feature for fourteen months.

Run `.\scripts\test-local.ps1` and make sure it is green before you report.

## When you are finished

Tell your Manager (session `ee59e5d0`) in ONE line - fleet messages truncate at the first newline.
Put the detail in `missions/stop-a-session/worker-b-notes.md` and point at it. Do not narrate
progress while you work; a message interrupts the session that receives it.

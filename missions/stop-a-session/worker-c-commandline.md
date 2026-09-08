# Worker C - the two commands

You are a Worker on the "Stop a session" mission, Phase A. Your Manager is session `ee59e5d0`.

**Read first, in full:** `missions/stop-a-session/handoff-phase-a.md` (the phase and the outcome
contract - the Gateway answer you render is written out there), then `missions/stop-a-session.html`
sections 4 and 5 (the six rulings - settled, you do not reopen one), then
`missions/stop-a-session/architect-state.md`.

Work in this worktree, on branch `mission/stop-a-session`. Do NOT merge and do NOT push - tell your
Manager when you are done and it commits.

**Touch ONLY `tools/cc-devthrottle/src/session_ops.py`, `tools/cc-devthrottle/src/cli.py` and
`tools/cc-devthrottle/tests/`.** Workers A and B are inside the C# at the same time and you will
collide with them. If you believe you need a change outside those files, stop and ask your Manager.

## Item 7 of the seven - and only item 7

### `cc-devthrottle session stop <session> --reason "why"` (`-r` too)

Calls `POST /sessions/{sid}/stop` with `{"reason": "..."}`. Worker B is building that route right
now, so build against the documented answer, not against a running Gateway.

**THE CLIENT IS DUMB.** It prints the `headline`, then each line of `details`, in order, and nothing
else. It does not compose a sentence, it does not decide what a verdict means, and it does not
re-word anything. That is Ruling 5 and it is the house rule. The ONE thing this command adds is the
thing only it knows: how to type its own flag.

Exit codes - and this is the heart of Ruling 3:

- **Exit 0 for ALL THREE verdicts**: `stopped`, `alreadyStopped`, `notOnFleet`. A stop never fails
  because there is nothing left to stop. The failure being designed out is a second run returning an
  error, which an operator reads as "it is still alive".
- **Non-zero for exactly three things**, and each says which one it was:
  1. no reason was given
  2. the Director could not be reached
  3. the process would not die

### The `notOnFleet` trap - read this twice

`_resolve_target` in `session_ops.py` prints "No session matches" and **exits 1** when nothing
matches. If `session stop` goes through it, the not-on-this-fleet case exits 1 - the exact defect
Ruling 3 exists to prevent, and the Gateway's careful 200 never gets a chance to be read.

So: when the target does not resolve against the roster, **do not exit** - send the raw target to
`POST /sessions/{target}/stop` and let the GATEWAY answer `notOnFleet` and write the sentence. That
keeps the fold on the Gateway where Ruling 5 puts it, and it keeps the exit code at 0.

An AMBIGUOUS target (several matches) is different and stays an error: "which one did you mean" is a
question about the caller's input, not a verdict about a session's state. Leave that behaviour as it
is.

### The no-reason refusal

`--reason` is required. Refuse it in the command line before the call when it is missing or blank -
there is no point making a round trip to be told what you already know - and print the flag. But the
Gateway also refuses (400) and its sentence must survive: `gateway._error_message` already lifts the
server's sentence out of an `{"error": ...}` body, so when a 400 does come back, print THAT sentence
and add the flag hint after it. Do not swallow the server's words and do not replace them with your
own.

Exit non-zero, and the message must make clear that the missing REASON is what stopped it.

### `cc-devthrottle session done --undo [<session>]`

Clears a pending deletion through `DELETE /sessions/{sid}/request-deletion` (the route and the
Director verb both already exist and are tested; Worker B is adding the allow-list entry that lets a
session key reach it). Defaults to THIS session, like `session done` does.

**No reason is required.** Ruling 4 asks for a reason because stopping is destructive; this is the
safe direction. Do not add one.

`--undo` with a `--reason` is a contradiction - decide what to do and say why in the code.

## House rules that bite here

- **ASCII only.** This file's module already patches Rich for it, and the project rule is explicit:
  Unicode causes encoding errors on Windows. No arrows, no ellipsis characters, no box-drawing.
- **Plain English, no abbreviations**, in every string a user reads and in the help text.
- Follow the shape of the commands beside yours (`interrupt_session`, `hold_session`, `mark_done`) -
  same error posture, same `gateway.GatewayError` handling, same help-text voice.

## Tests - and watch every one fail on purpose

`tools/cc-devthrottle/tests/`. Look at `test_session_compact.py` for how a Gateway call is stubbed in
this suite, and follow it.

Cover, one test each:

- `stopped` -> prints the headline, prints each detail line in order, exits 0
- `alreadyStopped` -> exits 0
- `notOnFleet` -> exits **0**, and the Gateway's sentence is what is printed
- a target that resolves to nothing -> the call still goes out with the raw target, and the exit code
  is 0. This is the one that pins the trap above.
- an ambiguous target -> still an error, unchanged
- no reason -> non-zero, and the message names the reason as what is missing and shows the flag
- a blank / whitespace-only reason -> same
- a 400 from the Gateway -> the Gateway's own sentence is printed, not replaced
- the Director cannot be reached -> non-zero, and it says so
- the process would not die -> non-zero, and it says so
- detail lines are printed in the order the Gateway gave them, and an empty `details` prints nothing
  extra
- `session done --undo` calls the DELETE route, needs no reason, and defaults to the current session

**Before you believe any of them:** revert the change, run the test, watch it go RED with the symptom
it claims to catch, then restore. Tell your Manager which ones you watched fail and what the red
said. A test that has never been watched failing is decoration.

Run the Python suite yourself - `.\scripts\test-local.ps1` runs NO Python tests at all, so a green
run there says nothing whatsoever about your work. Tell your Manager the exact command you ran and
the exact result.

## When you are finished

Tell your Manager (session `ee59e5d0`) in ONE line - fleet messages truncate at the first newline.
Put the detail in `missions/stop-a-session/worker-c-notes.md` and point at it. Do not narrate
progress while you work; a message interrupts the session that receives it.

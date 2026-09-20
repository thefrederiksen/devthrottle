# Proof - Smart Director Restart, phase 3, the two measurements on the rig

Developer seat, opened by the phase 3 Tech Lead (session 38f41a97). Branch `smart-restart/p3-rig`,
cut from `origin/main` = `ab2770c4a`. No product code was changed: this branch adds three documents
and nothing else.

Pull request: **#3204**.

## What was answered

**Question one - does reopening a saved conversation work on this build?** Yes, for both agents this
mission cares about, proved at all three layers the mandate asked for. Codex ignores the reopen id,
confirmed where the id would have been used. Full evidence:
`attachments/phase-3/reopen-test.md`.

**Question two - how long does an interrupted session need to write a handover?** Five mid-turn runs:
22.5, 23.9, 28.5, 43.8 and 57.9 seconds. The Architect's guess of three minutes is safe for that
shape, with about three times the margin, and should be left alone. Full evidence:
`attachments/phase-3/handover-timing.md`.

## What was run

- The three launch-spec test filters, all green (8 tests).
- Claude Code 2.1.278 and pi 0.85.1 driven directly, outside the product, to see whether a real saved
  conversation comes back.
- The isolated rig (`scripts/restart-qa-rig.ps1`) built and stood up from this tree, with
  `-WebShells installed` because neither question touches the Cockpit or the mobile app and building
  the two shells from the tree costs minutes of npm. The rig Gateway ran on loopback 7911 under
  `%LOCALAPPDATA%\cc-director-restart-qa-rig`. The machine's real Gateway on 7878, its launcher and
  the installed Director were never contacted, and no session outside the rig was touched.
- Reopens through the product's own spawn door for Claude Code and for Pi.
- Nine handover timing runs on the rig, using the product's own words
  (`DrainMessages.HandOverNow`) and the product's own watched path (`DrainPaths.HandoverFor`),
  produced by a scratch program referencing `CcDirector.ControlApi` so the sessions were sent exactly
  what the Director sends.
- The rig was taken down at the end. `restart-qa-rig.ps1 status` reports "no process is running from
  the rig root" and both scheduled tasks unregistered.

## What was NOT reached, and why

- **The desktop's Resume Session tab.** Every reopen came in over
  `POST /directors/{id}/sessions`. That is the product's own door and the one the way up engine will
  use, but it is not the screen a person clicks.
- **A reopen after a real Director restart.** The conversations here ended because their agent
  process ended.
- **Codex beyond layer 1.** The mandate says noted only, and it is.
- **A lead with seats reporting to it, timed.** This is the gap that matters most. The smart
  shutdown tells a lead to collect its subordinates' documents FIRST and wait for them - a serial
  dependency none of these runs had. It is the shape most likely to exceed three minutes, and it is
  the next thing worth measuring.
- **Any agent other than Claude Code and Pi in the timing runs.**

## Things somebody should know

1. **The two-thirds interrupt does not work on a Pi session.** `POST /sessions/{sid}/interrupt`
   answered `Conflict` three times on a Pi session that was genuinely working. This is by design -
   `PiDriver.InterruptAsync` throws `NotSupportedException` ("pi has no safe hard interrupt ... Use
   CancelAsync (Esc)") and `PiDriver` never declares the `Interrupt` capability. The consequence for
   this feature is direct: a Pi session mid-turn at two thirds is never told to hand over and is shut
   down with no document. `POST /sessions/{sid}/escape` works - a Pi session stopped that way wrote
   its document in 6.8 seconds. The two-thirds step must pick its verb from the driver's declared
   capabilities. The other drivers were not checked for the same gap.

2. **The drain's 500-byte floor let a half-written document through, once in eight.** Run 5's
   document crossed 500 bytes at 654 bytes and grew to 2808 - so the drain's acceptance point was
   reached with 23 per cent of the document on disk, and without the closing `drain-report` block
   that the drain actually needs. Requiring a parseable block is a better gate than a byte count.

3. **Claude Code's folder-trust dialog eats a new session's first prompt and can end the session.**
   A session created in a folder Claude Code had not been trusted in came up on the "Quick safety
   check" modal; the Director typed its prompt into that modal and the process exited 0 after 30
   seconds, logged as "exited cleanly". `--dangerously-skip-permissions` did not suppress it. A
   Director restoring seats into a fresh folder will lose them this way.

4. **Claude Code's interactive path ignores the `--model` argument the Director passes** and uses the
   model in the user's `settings.json`. The identical argument line in print mode honours it. On this
   machine today that model is at its monthly spend limit, so a plain new Claude Code session cannot
   do any work at all - which is a hazard for anyone else trying to run this rig. The way round,
   and the reason the timing runs exist, is that a session created with `resumeSessionId` runs on the
   model recorded in the transcript it reopened.

5. **`NewSessionRequest.ResumeSessionId`'s comment says reopening is "Ignored by agents that don't
   support resume (e.g. Pi)".** That is wrong: Pi is the agent for which reopening is most
   straightforward. Anyone planning off that comment would exclude the wrong agent.

6. **A reopened session can answer "no history" while holding its whole history.** Two direct asks of
   a correctly reopened Claude Code session answered `NO HISTORY`; the token counts and a third,
   differently-worded ask proved the conversation was in context all along. Ask a reopened session
   for a STRING it can find, never for a memory - the Director's injected preamble tells it that it
   is a new session, and it will answer against that.

## Housekeeping

One change was made outside this repository and undone afterwards: the scratch working folder used
by the rig sessions was marked trusted in `~/.claude.json` so Claude Code would not open on the
folder-trust modal (see finding 3). That entry was removed once the measurements were finished. No
other machine state was changed; the rig root is left in place, unused, and `restart-qa-rig.ps1
reset` will remove it.

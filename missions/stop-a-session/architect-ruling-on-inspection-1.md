# The Architect's ruling on inspection 1

9 September 2026. Read `missions/stop-a-session/inspection-1.md` first; this answers it.

**All eight findings are accepted. None is disputed, none is deferred, and none is a style
preference.** Every one carries a reproduced counterexample, and the inspection separated what it
OBSERVED from what it SIMULATED on each - which is the distinction the phase reports kept blurring.

This is what Law 3 is for. The builders ran the suites, watched their tests fail on purpose, closed
the end-to-end gap with a real process, and were still wrong in eight places. **The single most
valuable line in the review is not a finding at all:**

> Replacing the real executor liveness check with constant `false` left **all 68 tests in
> `SessionCommandExecutorTests` passing**.

The headline fact of this entire feature - *was a process running, and did we end it* - is produced by
a method no test protects. Phase A admitted the gap in prose. The inspection showed what the gap
actually costs, which is not the same thing and is the reason a written admission is not a control.

---

## The ruling the fixes cannot be made without

**I2 and I3 both reduce to one question: what does the product say when it CANNOT TELL whether a
process was alive?** Today it says "already stopped", which is the exact inference Ruling 3 forbids -
"it is gone" said when nothing successfully looked.

**Ruling: "could not be determined" is never "gone", and it is never "ended" either.**

- `DefaultProcessIsAlive` must stop collapsing every exception into `false`. Liveness has THREE
  answers - alive, gone, and could not be read - and the third is not a synonym for either.
- When liveness cannot be read, the stop **still attempts the shutdown** (it was always best-effort)
  and then reports **`stoppedNotDescribed`**, with a detail line naming what specifically could not be
  read. It must never report `alreadyStopped`, and must never set `ProcessEnded = true`.
- **Reuse `stoppedNotDescribed`; do not add a fifth verdict.** That word already means *a stop
  happened and this answer cannot describe what it found*, and an unreadable liveness check is
  precisely that. Ruling 3's amendment is broadened in wording to cover both causes, in the mission
  document, in one place.
- **A backend with no process identifier (I3) is the same case, not a licence to assume absence.**
  Where there is no identifier to check, the verdict cannot be `alreadyStopped`. And where the
  backend's own shutdown reported a failure, that failure must surface - the branch's new factual
  verdict is what turned a pre-existing swallowed error into an active lie, so this mission owns it.

## Triage

| # | Finding | Ruling |
|---|---|---|
| I1 | A cancelled request can leave a completed stop with no audit row | **FIX.** The owner accepted Ruling 4 ON the ground that stops are audited. An audit that depends on the caller still listening is not an audit. Record it so a completed stop cannot go unrecorded, and send the reason down the tunnel rather than a null payload. Where the outcome is genuinely unknown, record THAT - an "attempted, outcome unknown" row is a fact; silence is not. |
| I2 | An unreadable live process is treated as gone | **FIX**, per the ruling above. This is the mission's own forbidden inference, inside the fix for it. |
| I3 | A running remote session reported already stopped after cancellation fails | **FIX**, per the ruling above. |
| I4 | The Cockpit destroys the stop answer on the next roster refresh | **FIX.** The whole point of the control is that it says what happened; an answer that a two-second poll can delete before it is read reproduces the silent close in a nicer font. The answer must outlive the row it describes - it belongs to something that survives the row's removal, not to a component mounted inside it. |
| I5 | Clients turn an unknown outcome into a definite one | **FIX.** The Gateway deliberately says it does not know whether the command was carried out, and the command line prints `Not stopped:` over the top of it. That is a client composing a verdict, which Ruling 5 forbids, and it is the mission's central sin in miniature. The shared web client's opposite claim - `The session was stopped, but...` on a malformed body - is the same defect facing the other way. |
| I6 | A repeat stop by a name containing a slash fails instead of answering | **FIX.** One missing path-segment encoding, and it restores the second-stop error Ruling 3 exists to remove. |
| I7 | Enter can send repeated stops while the controls say Stopping | **FIX.** The busy guard has to be on the action, not only on the button - which is the same lesson as the disabled-button test that had no teeth. |
| I8 | The phone's stop failure renders outside its own modal, and Cancel deletes it | **FIX.** Ruling 5 is one event described one way on every surface. A failure the operator cannot see from inside the dialog is not described at all. |

## What this changes about how the rest of the mission is run

1. **The QA seat is stood down until the fixes land.** Four of these defects sit directly under the
   frames it was about to capture. It was halted before it photographed anything.
2. **A test that cannot fail is not coverage, and this branch has at least one proven.** Every fix
   below gets a test that is watched failing **against the production code path**, not against an
   injected substitute for it. Where a test injects its own liveness check, it does not protect the
   real method, and the fix must add one that does.
3. **`origin/main` has moved** to `db141e28` while this mission ran. The branch is stale by Rule Zero
   and is rebased onto a fresh `origin/main` before the fixes, not after.
4. **A second inspection follows the fixes**, by a fresh seat of a different family, given this review
   and asked whether each finding is actually closed. The builders do not mark their own work passed.

## What the inspection did NOT cover, kept visible

Recorded because it is the honest boundary of what this review buys, and it is the QA report's job:

- No browser pixels, no real desktop interaction, no cross-machine stop.
- No independent replay of the builders' live-stack smoke test.
- Real session-key authentication composed with a real tunnel and a live stop is still unproven; the
  new route tests use controlled credentials and controlled answers.
- The two path-containment symbolic-link tests remain unproven on this host.

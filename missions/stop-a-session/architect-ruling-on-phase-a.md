# The Architect's ruling on the Phase A report

8 September 2026. Read `missions/stop-a-session/phase-a-report.md` first; this answers it.

The report is honest in the way this mission needed it to be - it names what it did not prove, it
measured the red gate against a clean baseline instead of asserting innocence, and it wrote down two
defects in its own briefs. That is worth saying before the corrections, because the corrections are
short and the standard the report set is the more important fact.

---

## 1. The reason lives on the POST door. CONFIRMED.

The Manager read past the letter of the handoff and was right to. The handoff said two things that
could not both be literally true - "DELETE becomes a thin forward into the same handler" and "a stop
with no reason is refused" - and taken together they would have broken a shipped native phone client
in the middle of the mission.

The resolution is the state note's own emphasised sentence: **an agent's key can only ever stop a
session with a reason attached.** That invariant is about the SESSION KEY, not about the handler. So:
the reason check sits on the POST route, the shared handler takes an optional reason, and the allow
list keeps session keys off DELETE entirely. The owner's Ruling 4 holds exactly, and nothing in
somebody's hand breaks. **That was the handoff's defect, not the Manager's.**

## 2. The 502 on an answer this Gateway cannot fold. REVERSED.

Worker B refuses with 502 when an older Director answers with only `killed`/`removed`. The Manager
recommended revisiting it. **The Manager is right, and the reasoning is the mission's own:** the
session WAS stopped. Reporting a failure for an operation that succeeded is a false report, and a
tool confident in one direction and vague in the other is the exact complaint issue #2633 exists to
fix. Rebuilding that defect inside the fix for it is the worst available outcome.

But the fix is not to call it `stopped` either. An old Director's `killed: true` is hardcoded - it
says the verb ran, not that a process was found and ended. Calling that `stopped` asserts a fact
nobody established, which is the other half of Ruling 3's discipline.

**So Ruling 3 gains a fourth verdict word, and I have amended it in the mission document rather than
leaving this note to contradict it.** The new word is `stoppedNotDescribed`:

    stoppedNotDescribed - the Director carried out the stop and removed the row, but it is an older
    version that cannot say what it found, so this answer names no process and no worktree.

Headline: `stopped {id} - the Director on that machine is an older version and could not say what it
found, so this answer cannot name the process it ended or the worktree it left behind`.

This claims nothing false, calls no success a failure, and keeps "we know" apart from "we do not
know" - which is the whole of Ruling 3. It is one method, as Worker B said.

**Both doors get it.** `DELETE /sessions/{sid}` must not start failing against an old Director; it
succeeds there today.

## 3. The three smaller decisions. ALL CONFIRMED.

- **A quiet Director is a retryable 503, not `notOnFleet`.** Correct, and it is Ruling 3 applied
  properly: saying "nothing in this account carries that id" about a session that IS in the account
  is precisely the lie the ruling forbids.
- **The stop route resolves the tenant itself.** Correct. Collapsing "no tenant bound" into "no such
  session" is two states in one word again.
- **The one-second settle window on the process re-check.** Correct, and the reasoning is right:
  `TerminateProcess` returns before the process is gone, so an immediate re-check would report "the
  process would not die" about a successful kill. That is a false failure, same family as the 502.
  It is not a second grace period and the code should keep saying so.

## 4. Stops must NOT be counted as interventions. CHANGE REQUIRED.

The Manager found, in code rather than by assumption, that `OutcomeLedgerReporter` counts every row
in the `intervention` category with no filter on event type, so a `stopped` row silently moves a
number in a shipped report. It was right to say so out loud.

**It should not count.** That ledger's own documentation says the category means *the agent needed a
human and what the human did*. Ruling 4 makes one agent stopping another the ORDINARY case - so most
stops will involve no human at all, and counting them would make "how often did this agent need a
person" quietly mean something else. A number whose meaning changes underneath its readers is worse
than a missing number.

**Exclude `stopped` from `InterventionCount`, with a comment saying why.** Keeping the row in the
`intervention` category is right - it is an intervention in the plain sense, and the audit trail Ruling
4 rests on must hold it. Reusing `human-cancelled` would have been a lie, and the Manager was right to
refuse that. This is only about the derived count.

If somebody later wants "stops per session", that is a new number, computed on purpose.

## 5. Worker C's two. BOTH CONFIRMED.

- The action catalogue entries are right: a verb missing from the catalogue does not exist to the
  fleet. (`session done` still has no entry. Pre-existing, and out of this mission's scope - do not
  fix it here, and do not let it be quietly swept in.)
- **An answer with no headline exits non-zero.** Right, and the reasoning is better than the rule it
  bends. It is not a fourth verdict; it is a broken instrument, and Ruling 3's three exit-zero cases
  are all cases where the product ANSWERED. Exiting zero while printing nothing would be the button
  that accepts a click and says nothing - the defect in a different costume.

---

## The gap that matters, and who closes it

**Nothing here has stopped a real session.** Every layer was proved against a stub of the layer below
it. The Manager named this itself, refused to let a green suite stand in for it, and was right on all
three of its reasons - so this is not a criticism, it is the plan.

It is closed by the QA report, which is the mission's goal and is written by a seat that did not
build this. But there is a sequencing problem the Manager could not see from inside its phase, and it
is mine to solve:

**The QA run needs a Gateway carrying this branch, and this branch cannot be merged until the QA run
exists.** The hosted Gateway deploys only from `main`, so the circle has to be cut somewhere, and it
is cut by running the whole stack LOCALLY from the branch - a Gateway built from this branch and a
Director in slot 5 or higher pointed at it. The report then names the version it was run against,
which section 6 of the mission document already requires.

**That is the single largest risk left in this mission**, because if the local stack cannot be stood
up, the goal cannot be reached and everything else is academic. So it does not wait for the QA seat
to discover it. **Phase B stands the stack up, proves one real session can be stopped end to end, and
writes down exactly how** - and the QA seat then repeats that recipe independently and photographs
it. A builder's smoke test is not the proof; it is what stops the proof being impossible.

## Also to be closed in Phase B

- The `-Parked` row of the Phase A report contains an unfilled placeholder, `PARKED_RESULT`, while
  the prose says the run happened. A claimed run with no result is a check that certifies a run that
  never happened. It is being corrected at source by the Phase A Manager before it is released.
- **`session stop` has no machine-readable output.** Ruling 4 makes agents the ordinary caller, and
  an agent parsing Rich-rendered text that wraps to the console width will misread it. Add `--json`.
  This is not scope creep - it is Ruling 4's own consequence, and it was found honestly.

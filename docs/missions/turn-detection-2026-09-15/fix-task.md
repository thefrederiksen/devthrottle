# Fix task - the six inspection findings

You are a fresh Manager. The previous one built items one to four and has been reaped. An
independent Inspector from a different agent family then returned **DISAGREE** with six findings.
Your whole job is to close them.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-detection`) on branch
`mission/turn-detection-phase-one`. Never the shared checkout, never `main`.

## Read these first

1. `cc-devthrottle workflow instructions mission --version 17` - your conduct.
2. `docs/reviews/turn-detection-phase-one-inspection.md` - the findings. This is your mandate.
3. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings. Do not reopen them.
4. `docs/missions/turn-detection-2026-09-15/build-report.md` - what the previous Manager built and,
   more usefully, what it admitted it had NOT proved.

**Every one of the six findings is accepted.** None is a false alarm and none is to be argued
away. Two of them need a design decision that is the Architect's and not yours; those decisions
are below and they are settled.

## The four findings you fix as written

**Finding 1 - a missing settled baseline can lose a turn.** The check calls a frame ambiguous only
when the CURRENT rows are empty. It never asks whether the SETTLED side exists. A settle whose
extraction failed leaves no baseline, or leaves the previous turn's rows, and then a small real
reply scores under the size threshold and holds the session red with no later byte to ask again.
That is a lost turn, which the whole design forbids. An absent or stale baseline is ambiguous and
takes the conservative open path, exactly as empty current rows do. Say so in the log rather than
recording a silent zero. Delete the unit-test comment claiming the detector never asks the rule
with no settled side; the detector's own failed-extraction path contradicts it.

**Finding 2 - the exception paths.** `OnContentCheckCore` clears the scheduled flag, the pending
flag and the byte count BEFORE it reads the screen, so anything that throws after that point
consumes the burst and a one-burst reply is never asked about again. And `MarkActiveFromContent`
sets `_active` before the state write, so a throw in between leaves the session red and latched
active, after which later bytes take the already-active branch and schedule nothing. Both windows
must close. The existing comment claiming the loose latch's worst outcome is a duplicate Working
write is false and must go with it.

**Finding 3 - production bypasses the interface.** The detector calls `GainedContent` and
`ChangedCharacters` directly and re-implements the size comparison beside them, so the interface
that is supposed to be the single seam is used by nobody in production. That is not a tidiness
point: work item five scores the interface implementations, so as written it could score one
function while the Director runs another, with every test green. The detector must hold and invoke
`ITerminalNoveltyRule` instances, and the threshold comparison must live inside the size rule
rather than being repeated at the call site.

**Finding 6 - the constants nothing pins.** Pin each one with a test that fails when it moves, and
independently of the value under test: the eighty percent near-duplicate threshold (today 0.90
would pass); the size threshold (today 201 would pass, because the sample carries exactly 201
changed characters and the boundary test builds its own unrelated threshold); the maximum deferral
(today removing the cap would pass, because no test reaches it - write one that does); each
declared marker made independently responsible for at least one row, so a single entry cannot be
removed while a neighbouring entry keeps the row passing, and the two entries currently exercised
by nothing are either covered or deleted; and the production wiring itself - that the environment
switch actually reaches the rule enum, and that the shadow log's default really is on.

## The two rulings that are the Architect's

**Finding 4 - the shadow log runs before the state decision.** Ruling: **compute the verdict, apply
the state, then append.** The append never sits between the decision and the write. That removes
the ordering hazard outright rather than bounding it, and it is the cheaper change. Keep the single
process-wide lock: at the corrected rate the contention is acceptable, and swapping it for
per-file locking is a change nobody has measured a need for. The sentence in `handoff.md` saying
the log can never affect the session it observes was mine and it was too strong - correct it to say
the log is appended after the state write and therefore cannot delay it, which is a claim the code
supports.

**Finding 5 - the continuous-idle driver pays an unbounded shadow cost.** An agent whose footer
repaints forever never goes byte-silent, so with the rule off and the shadow log on it forces a
check and an append at the deferral cap for as long as it sits idle, and nothing ever deletes those
files. Two rulings:

- **For a continuous-idle driver, schedule the shadow check on a BODY CHANGE, not on raw bytes.**
  That path already computes whether the body changed; the footer heartbeat is exactly the thing
  that is not worth a check. This is cheaper AND more meaningful - a row per real body change is
  the measurement we want; a row per footer frame is noise that would skew the shadow numbers.
- **The shadow log gets a retention bound.** A log with no rotation on a machine running a fleet
  all day is a disk leak, and it was mine to specify and I did not. Bound it, and make the bound
  a number in one named place rather than scattered.

Also correct the code comment the Inspector caught: a burst that outlives the maximum deferral IS
sampled while still drawing. The comment claiming a longer burst is never sampled half-drawn is
false. Say what the code does - the cap trades a possibly half-drawn sample for a guarantee that a
chattering agent is judged at all - rather than claiming a property the code does not have.

## How you are judged

- Every fix gets a test that was WATCHED FAILING with the reported symptom before it was made to
  pass. A test that has never been seen red is decoration.
- `.\scripts\test-local.ps1` green, and run the focused parked tests for this change explicitly
  since they live in a parked suite. Do NOT run `-Parked` in full: the Gateway suite's machine-wide
  lock is contended right now and the Architect runs that one before landing.
- Do not open a pull request and do not merge. The Architect lands the work.
- Plain ASCII everywhere. No mention of any assistant, model or vendor anywhere.

## Reporting

Append to `docs/missions/turn-detection-2026-09-15/build-report.md` a section per finding: what you
changed, the test that pins it, the mutation you watched turn it red, and anything you could NOT
close and why. Then report to the Architect ONCE, in a single line.

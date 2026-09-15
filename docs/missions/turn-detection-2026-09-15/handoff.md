# Turn detection, phase one - the running note

Mission `d2bb87ec`. Branch `mission/turn-detection-phase-one`, worktree
`D:\ReposFred\devthrottle-turn-detection`, cut from `origin/main` at `90dd38ad5`.

This is the note a fresh Manager is rebuilt from. It is short on purpose. Everything the
Architect knows is either here, in the brief, or in the design - never only in a conversation.

## Where the rest lives

- The brief: `devthrottle_internal/docs/missions/BRIEF-2026-09-15-turn-detection-phase-one.md`
- The design and its evidence: `devthrottle_internal/docs/design/trustworthy-state-switching/`
  (`trustworthy-state-switching.html` section five is the measurement,
  `implementation-plan.html` is the plan, `review-2026-09-15.md` is the independent review,
  `harness/` is the scoring code)
- How the mission is conducted: `cc-devthrottle workflow instructions mission`
- The issues: parent #2853, children #2854 through #2858

## The goal, in one sentence

A session should turn blue because the conversation gained something, not because a byte
arrived.

## Rulings that are already settled - do not reopen these

- Phase one is the terminal rule only. The ten-second silence rule is untouched, so red
  still lands mid-work. That needs each agent's own turn-end event and is phase two.
- The rule ships behind a switch that is OFF. Turning it on is the owner's decision and he
  wants the shadow numbers first.
- There are two candidate rules, not one, and neither is assumed to win. Both go behind one
  interface and the choice is made on live bytes.
- A miss must not be able to lose a turn. Any later byte re-arms the check, and bytes inside
  the settling window push it out rather than being dropped.
- The corpus never enters the product repository - it is public and those screens are real
  work with client names and paths in them. Only hand-redacted screen pairs cross over.
- The corpus is a regression gate, not the proof. Its labels come from the old detector's own
  timing and its body split is a guess. The proof is the shadow run on live bytes.
- Owner's decision, 15 September: the live shadow happens by cutting a release once the code
  lands, so his installed Director picks it up on its next auto-update. The corpus numbers are
  reported first and the live comparison follows.

## Where the work is up to

Filled in as the phase runs. See the phase log below.

## Phase log

- 15 September: worktree cut, five issues filed as children of #2853, the owner answered the
  one question that was his to answer (how the live shadow happens).

## Ruling: the eighty percent similarity is difflib's ratio, not a longest common subsequence

The near-duplicate filter's 0.80 threshold comes from Python's
`difflib.SequenceMatcher.ratio`, which is not a longest-common-subsequence ratio. It finds the
longest matching block, then recurses on what is left of it and what is right of it, and
answers twice the matched length over the total length. The greedy block choice can lose
matches an optimal subsequence would find.

Measured on twenty thousand random short pairs on 15 September: the two disagree on
twenty-eight percent of them, and the worst disagreement was 0.15 against 0.67 - far wider than
the threshold being tested. A C# implementation built on a longest common subsequence would
look right, pass its own tests, and quietly score differently from the published measurement,
which would make the whole candidate comparison in work item five meaningless.

So the C# must implement difflib's algorithm. Python's `autojunk` heuristic only engages on
sequences of two hundred elements or more; whatever the C# does above that has to be written
down rather than left to chance.

## Ruling: the pinned corpus is a NEW set, and the published numbers are re-taken against it

The published measurement counted four thousand three hundred and twenty-eight pairs. An
independent rerun during review counted four thousand three hundred and twenty-five. A third
rerun, by the Architect on 15 September from the same scripts, produced one thousand three
hundred and thirty-seven repaint-labelled pairs and two hundred and thirty-nine long
unexplained ones, where the published figures were nine hundred and sixty-eight plus three
hundred and sixty-three, and two hundred and thirty-one. The directory the corpus is built from
keeps growing and the scripts carry only a lower date bound, so every rerun scores a different
set. That is not a discrepancy to reconcile; it is the defect the manifest exists to remove.

**The published set is not recoverable and no attempt is made to recover it.** Reconstructing
the exact four thousand three hundred and twenty-eight would be fitting the corpus to a number
rather than pinning a corpus. The manifest is built fresh, with an explicit upper time bound as
well as a lower one, and every number in the phase one report is re-taken against it. The older
figures stay in the design document as history and are never quoted as current.

**What the manifest records, per wake:** the session id, the agent, the wake time, how long blue
lasted, whether a submission explained it, the class label, and both screen files by relative
path AND by content hash. The hashes are what make it a gate: a screen file that changes under
the manifest is caught rather than silently rescored.

## Ruling: the shadow verdicts go to a local append-only log, not to the activity ledger

The activity ledger's rows live on the hosted Gateway and a session key is refused there - that
is question five on #2853. A verdict written only to the ledger is a number nobody on the
machine that produced it can read, which makes the whole comparison unanswerable.

So phase one adds `TurnDetectionShadowLog`, built in exactly the shape of
`src/CcDirector.Core/Wingman/StateChangeLog.cs`: append-only lines of JSON, one file per
session, the path resolved through `CcStorage` so `CC_DIRECTOR_ROOT` redirects it under test, a
single process-wide lock, and failures logged and swallowed. It carries its own switch.

**Corrected 15 September, on the inspection.** This paragraph used to say the log could never
affect the session it observes. That was mine and it was too strong: the append is a synchronous
file write under a shared lock, it has no latency bound, and it ran BEFORE the state decision, so
a slow filesystem could delay the very decision the row describes. The ruling is to remove the
hazard rather than bound it - **compute the verdict, apply the state, then append** - and the claim
the code now supports is the narrower one: the log is appended after the state write and therefore
cannot delay it. The single process-wide lock stays; at the corrected check rate the contention is
acceptable and nobody has measured a need for per-file locking.

**The log is bounded on disk**, which it was not. Four megabytes per session file with one rolled
predecessor kept, and a file nothing has written to for fourteen days is deleted. Both numbers live
in `TurnDetectionShadowLog` and nowhere else.

One row per check, never per byte. Each row carries the time, the agent, what the row candidate
decided and the first new row it saw, what the size candidate decided and its magnitude, what
the old byte rule would have done, and both screen hashes so the pair can be matched back to a
saved turn-review capture.

**The rule switch defaults OFF and the shadow log defaults ON.** That pairing is the point: the
owner's Director produces the comparison numbers while behaving exactly as it does today.

## Phase log, continued

- 15 September, items one to four built by a Manager and committed as four commits plus a build
  report. Manager reaped once its worktree was verified clean and everything pushed.
- The branch was rebased onto origin/main, which had moved on while the work was built. The
  build report's claim that nothing in the change touches the Gateway was checked against the
  diff from the true merge base rather than taken on trust, and it holds: nineteen files, all
  under `CcDirector.Core`, its two test projects, and this mission folder. The Gateway and
  ControlApi files that appeared in a naive `origin/main..HEAD` diff were main moving forward,
  not this branch.
- `CcDirector.Gateway.Tests` still has NO verdict on this change. The machine-wide lock was held
  by another mission's run with a second run already queued nearly thirty minutes deep, which is
  the contention issue #1156 describes, observed rather than inferred. It is NOT called green,
  and it is run immediately before landing - after any inspection fixes, because a run taken
  before those fixes would describe a tree that no longer exists.
- An independent Inspector from a different agent family was seated against the rebased branch
  in its own worktree, with the sharp questions in `inspection-brief.md`.

## The machine rebooted mid-mission, 15 September 01:29 local

Windows ran updates and restarted. The Director died with no drain, no handover and no warning,
taking twelve sessions with it. This mission's Architect and its third Manager were both restored
from their transcripts, so no reasoning and no work was lost, but three things changed underneath
the mission and are recorded here because the next reader will otherwise trust stale facts:

- **Every session id from before the crash is dead.** The Architect is now
  `b564b0b3-148d-402d-a5ba-ac45414fb437`. Ids quoted earlier in this folder and in the review files
  are historical and must not be messaged or reasoned about.
- **The Manager came back unwired.** It was restored standalone with no controller, so the
  reporting chain the mission workflow depends on did not survive even though the seat did. It is
  driven by explicit message until it has landed its current piece, and only then re-spawned with a
  real controller - never while it holds unlanded work.
- **The Codex inspector is unavailable until 19 September**, having hit its provider's usage limit
  before the machine went down. Law three requires an inspector from a different family to the
  builder, and the builder is Claude Code. Round three therefore goes to Gemini rather than waiting
  four days for Codex. Gemini, Grok, Copilot, Pi and OpenCode are all installed on this machine and
  all satisfy the law; the requirement is a different family, not a particular one.

**Nothing was lost.** The branch was fully pushed at `505a5a7cf`, and the Manager's in-flight work
survived as uncommitted changes to three files in the worktree, including an atomic size verdict
and a conditional latch release. The instruction going forward is to commit and push as the work is
made rather than at the end of a piece, which is law two's reasoning applied at a smaller grain: the
crash cost nothing only because the branch happened to be current.

What the crash invalidated and was rechecked rather than quoted: the claim that each Director
resolves its own storage root. Re-verified in the new session - `CC_DIRECTOR_ROOT` reads
`...\cc-director\instances\default`, and five instance roots exist on this machine.

## Ruling: round three is inspected by Pi on GLM-5.3

The owner's instruction was Pi with GLM 5.2. **GLM 5.2 is not available on this machine.** Pi's own
model list offers `zai-org/GLM-5.3` and `zai-org/GLM-5.3-Flash` through the `deepinfra-mindzie`
provider, and nothing matching 5.2 under any provider - checked across all fifty-five models Pi
lists, not just a fuzzy search for the name.

Taking the full `zai-org/GLM-5.3` rather than Flash: same family, same provider, strictly newer than
what was asked for, and the non-Flash variant because an adversarial code review wants reasoning
rather than speed.

Verified before it was depended on, rather than at the moment it mattered: a one-shot run with that
model returned cleanly, so the model resolves and the provider credential works. What that does NOT
cover is whether the Director's own launch path passes the model through - that is checked by
reading the seated session's buffer once it is spawned, not assumed.

Pi satisfies law three because it is a different family from Claude Code, which built the work. The
law names a different family, not a particular vendor, which is what makes it possible to keep
moving while Codex is unavailable.

## Who owns what, after the restart

Two Architects sit on mission `d2bb87ec` and the fleet map now names them apart:

- **Turn detection - Architect - the design and all three phases** (`86c09e78`) owns the design
  across all three phases, including the per-agent turn-end hooks that phase two rests on.
- **Turn detection - Architect - phase one only, the terminal rule** (`b564b0b3`) - this seat - owns
  phase one and nothing beyond it.

**The owner has ruled that phase two does not open until phase one is finished and landed.** So the
scope line in the brief is now a sequencing rule as well as a scope one: work that belongs to phase
two is not merely out of scope here, it is not startable by anyone until this phase is done.

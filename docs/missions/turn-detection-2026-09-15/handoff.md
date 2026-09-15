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

## Gemini is out. GLM only.

Adding a second inspector on Gemini was explored and abandoned the same day, on the owner's
instruction. The evidence, so nobody re-derives it inside this mission: the credentials load, so it
is not a sign-in problem; the account is on a Google Workspace domain, which makes the command line
demand a cloud project; with a project supplied and ACCEPTED, the real error is that the account has
no Gemini Code Assist licence. There is no API key on file either.

It is fixable and it is not this mission's problem. It has its own standalone session
(`24eb8980`, "Fix Gemini - the command line has no license") holding the full diagnosis and standing
by for the owner.

**Every inspection from here runs on Pi with GLM-5.3.** One inspector, one family, and it is a
different family from the Claude Code that builds - which is all law three requires.

## SUPERSEDED: the published corpus WAS recoverable, and is now pinned

The ruling above - "the pinned corpus is a NEW set, and the published numbers are re-taken against
it" - is wrong and is superseded. It is left in place rather than deleted, because the reasoning
behind it was sound on what was known at the time and the next reader should see why it changed.

What I got wrong: I concluded the published set could not be recovered, because three reruns of the
labelling script produced three different populations and the script carries only a lower date
bound. That was true of the SCRIPT. It was not true of the run - the original wake table survived
the reboot in a scratchpad directory, and it names the exact records.

It is now merged on `devthrottle_internal` main as
`corpus/state-switching/wakes-2026-09-15.jsonl` (commit `e048b2c4`), and this seat verified it
independently rather than taking the report:

- 6,953 wake rows, of which **4,328 carry both screens** - the published figure exactly.
- The paired labels are **231 long-unexplained and 1,975 explained**, both exactly as published, and
  1,332 short-unexplained against the published 1,331. The manifest deliberately does NOT split that
  last group into repaint and stop-hook tail, because that split is a reading of the screen made at
  scoring time and does not belong in a manifest.
- The labels are named as **behaviour classes, not causes** - "short-unexplained", not "repaint
  phantom" - which is the correction the independent review asked for and the previous naming got
  wrong.
- Every screen is pinned by its SHA-256. On a random sample of 300 pairs, **600 of 600 screen files
  were present and hashed exactly**, with no misses and no mismatches. That is what makes it a gate
  rather than a list: a screen that changes underneath it is caught.

**Use it. Do not rebuild one.** Work item #2858's corpus half is done; what remains there is the
scorer that runs the shipped C# rule against it, and the shadow numbers.

**What it still cannot answer**, and this is unchanged by pinning it: the body split, because saved
screens do not record the cursor and production does; and anything about the settling window,
because the two screens in a pair are about ten seconds apart and carry no byte timing at all. A
good score here remains a regression gate, not proof. The proof is the shadow run on live bytes.

### The 1,332 against 1,331 is resolved: the table was incomplete, not the corpus

The one-record gap noted above is closed and the corpus was right. The short-unexplained class
splits three ways - 968 settled, 363 stop-hook tail, and **one mid-work** - and the published table
printed the first two rows and silently dropped the third.

Verified here rather than accepted: the named record is in the manifest exactly as described -
session `b2d6a3e7`, `2026-09-12T00:56:50Z`, blue 10.004 seconds, Codex - and the paired
short-unexplained total is 1,332.

Worth noticing what that one record IS, because it is not a rounding detail. It is a red that fired
while the agent was still working: a FALSE RED, which is the failure phase one explicitly does not
fix and phase two does. It is correctly outside repaint scoring. A published table that drops the
row it has no column for is how a known limitation quietly stops being visible.

## Accepted deviation: the latch release is CONDITIONAL, and my ruling was the weaker one

I ruled that the two shipped activation paths should get "the same latch release" already applied
next door. That ruling was incomplete. I specified a mechanism without checking the guard it had to
work with, and the Manager was right to deviate.

`OnQuietCore` returns on its first line unless the latch is set. So clearing the latch
unconditionally after a fault creates a NEW failure rather than closing the old one: if the state
write had already landed, the session is Working, the latch is clear, and the armed countdown fires
into a method that returns immediately - a session stuck blue for ever, with nothing left to bring
it back.

What shipped instead, and it is correct in both directions:

- The quiet timer is armed in a `finally`, so a fault can never cost the countdown.
- The latch is released **only when the session is not already sitting in Working** - that is, only
  when nothing was actually written. Then later bytes re-enter the settled path and can schedule a
  check.
- When the write DID land, the latch stays set and the armed countdown delivers red normally.

It was also extended to `MarkActiveFromContent`, because finding one's new open-the-turn fallback
calls it from a place no retry follows.

The general lesson, written down because it is the second time this mission has produced it: a
ruling that names a MECHANISM rather than an OUTCOME can be faithfully followed into a defect. The
outcome I wanted was "a fault can never leave the session in a state nothing recovers from". Had I
ruled that, the mechanism would have followed from it.

## OWNER'S RULING, 15 September 2026: the row rule is ON by default

**This supersedes the brief's instruction that the switch ships off.** The earlier rulings in this
file that say the rule ships off and waits for shadow numbers are history, not current.

The owner's words, when asked whether to turn the rule on by default for v2.2.0: "Yes, I want to
turn on by default." The reasoning he was given, so a later seat does not re-litigate it: shipping
it off meant v2.2.0 changed nothing anyone could feel and the benefit waited a whole further release;
the downside is bounded, because four inspection rounds established that the worst case is a turn
opening a second late, never a turn lost; and it is reversible per Director.

**Why the ROW candidate and not size:** work item five scored the shipped rule objects against all
4,328 pinned pairs with zero hash misses. The row rule holds 93.8 percent of short-unexplained wakes
red and still opens 229 of 231 long ones; the size rule at its shipped threshold of 200 holds only
88.9 percent. The row rule also reports WHICH row changed, so a wrong call can be read back, and its
definition is the one the published measurement used, where the size threshold is our own reading of
a reviewer's undeposited script. Stated limit, unchanged: the body split is a guess on 80.7 percent
of those screens, so the corpus has not scored exactly what ships.

**How it works now:** `CC_DIRECTOR_CONTENT_TURN_RULE` unset means the row rule. `off` (or `0`,
`false`, `no`) restores the byte rule on that Director - the escape hatch. `size` runs the other
candidate. A value it cannot read means OFF, deliberately: with the rule on by default the variable
exists to move away from the default, most likely to turn a misbehaving rule off, and a misspelt
"off" must never leave it running.

**The shadow log stays ON.** Each row still records what the byte rule would have done beside what
the running rule did, so the live comparison remains answerable - against the rule that is actually
running rather than one that is switched off.

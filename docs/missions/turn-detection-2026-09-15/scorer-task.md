# Task - work item five: score the shipped rule against the pinned corpus

You are a fresh Manager. **Items one to four of phase one are MERGED to origin/main** as `e294f40e7`,
after four rounds of independent inspection. This is the last item of phase one: issue #2858.

**Work only in this worktree** (`D:\ReposFred\devthrottle-turn-scorer`) on branch
`mission/turn-detection-scorer`. Commit and push as you go - this machine rebooted without warning
once during this mission.

## Read first

1. `cc-devthrottle workflow instructions mission --version 17` - your conduct.
2. `docs/missions/turn-detection-2026-09-15/handoff.md` - the settled rulings. Do not reopen them.
   The corpus section is the one that matters most to you, INCLUDING the ruling marked SUPERSEDED,
   because the superseding one is what you follow.
3. Issue #2858 on `thefrederiksen/devthrottle`.
4. `docs/reviews/turn-detection-phase-one-inspection-round-four.md` and the three before it - what is
   already verified about the rule you are about to score.

## The corpus is DONE. Do not build another one.

It is merged on `devthrottle_internal` main at
`corpus/state-switching/wakes-2026-09-15.jsonl` (commit `e048b2c4`), and the Architect verified it
independently rather than taking the report: 6,953 wake rows, **4,328 carrying both screens** - the
published figure exactly - with 231 long-unexplained and 1,975 explained, both exact. A random sample
of 300 pairs hashed all 600 screen files: 600 matched, zero missing, zero mismatched.

The screens it names live under
`%LOCALAPPDATA%\cc-director\instances\default\turn-review\<date>\<session>\<file>.json`, and each is
pinned by SHA-256 in the manifest. **Verify the hashes as you read** - a screen that no longer
matches its hash is a corpus miss, not a scoring input, and must be reported rather than scored.

## What to build

**A scorer, in THIS repository, that runs the SHIPPED rule.** Not a re-implementation, not a Python
copy. It constructs the real `TerminalContentNovelty` rules through `ITerminalNoveltyRule` and
`ITerminalSizeRule` - the same objects the detector holds - and asks them. A rule scored by a
re-implementation proves nothing about the rule that ships, which is the whole reason the interface
exists and was a finding in round one of the inspection.

It takes the manifest path and the screen root as ARGUMENTS. **The corpus never enters this
repository** - it is public and those screens are real client work with names and paths in them. The
tool is public; the data is passed in.

**What it must print**, per candidate and also for the old byte rule as the baseline:

- of the short-unexplained pairs, how many are held red;
- of the long-unexplained pairs, how many still open;
- of the explained pairs - the negative control - how many show work;
- **the same table per agent.** This is not optional and it is the check that catches over-reach: of
  the paired population, 3,621 are one agent, 633 the next, 55 a third and 8 a fourth. An aggregate
  number is evidence about one agent on one machine.

## What you must say, not bury

**The body split is a guess and it controls the result.** Saved screens do not record the cursor, so
the scorer has to guess where the input box is; production has the real cursor. Of 8,650 screens in
the earlier measurement only 1,674 had exactly one prompt-like row. Print how you split, print how
many screens were ambiguous, and say plainly that the corpus has therefore NOT scored the rule that
actually ships.

**The corpus is a regression gate, not proof.** Its labels come from the old detector's own timing -
"short-unexplained" is a behaviour class, not an observed repaint - and it can say nothing at all
about the settling window, because the two screens in a pair are about ten seconds apart and carry no
byte timing.

**Do not repeat the causal language.** The long-unexplained set is defined by duration. Nothing in
the records says a sub-agent or a background task returned. Say what was measured.

## Also in #2858

A small set of **hand-redacted** screen pairs crosses into this repository as ordinary test fixtures -
a repaint, a torn repaint, a ticking clock, a real reply. Hand-redacted means you read every line and
removed every client name, path and identifier. If you are not certain a pair is clean, leave it out.

## What is NOT in this item

The shadow run on live bytes. The owner has ruled that it happens by cutting a release once the code
lands, so his installed Director picks it up on its next auto-update. That is not yours to trigger.
Your numbers are the corpus numbers, and the report must say the live comparison is still outstanding.

## How you are judged

`.\scripts\test-local.ps1` green. Do NOT run `-Parked` in full - the Gateway suite's lock is
unwinnable on this machine (issue #2862). Redirect every long run to a file and read the exit code
from `dotnet` - do NOT pipe through `tail` or `head`, which buffers the log away and replaces the exit
code with the pipe's. That cost this mission real time twice.

Do not open a pull request and do not merge. Plain ASCII everywhere. No mention of any assistant,
model or vendor anywhere.

## Reporting

Write the numbers and what they do and do not establish to
`docs/missions/turn-detection-2026-09-15/corpus-scores.md`. Report to the Architect
`b564b0b3-148d-402d-a5ba-ac45414fb437` ONCE, in a single line.

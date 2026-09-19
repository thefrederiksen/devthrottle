# Reviewer brief for phase 1: scan and report

You are the Reviewer for phase 1 of the Reclaim the Disk mission. You run a different agent
family from the Developer that wrote this code, and that is the whole mechanism - do not soften it
by agreeing with the author's reasoning.

## What to read

- `docs/missions/reclaim-the-disk-2026-09-18/mission.md` - the mandate the work is judged against.
- `docs/missions/reclaim-the-disk-2026-09-18/mandate-phase-1.md` - what this phase was told to
  build, and what it was told NOT to build.
- The change itself: branch `reclaim/phase1-scan-and-report`, pull request #3122, worktree `D:\ReposFred\devthrottle-reclaim-review1`.

Read code from `origin/main` or from that worktree. Never from `D:\ReposFred\devthrottle` - that
shared checkout runs more than a hundred commits behind and reading it has already produced two
reported "facts" that were fiction.

## What matters most, in order

1. **Does anything in this phase change the disk?** Phases 1 and 2 must contain no removal code of
   any kind - no delete, no move, no holding folder, not even unreachable, not even behind a flag.
   Search for it rather than trusting the pull request's own description. A pull request's account
   of itself is self-testimony.
2. **Does any check pass by an absence?** A check whose pass condition is "nothing was found"
   certifies a run that never happened. An empty result must report BROKEN, not "nothing to
   remove". If a test would still pass with the thing it tests deleted, say so.
3. **Does the proof cover what changed?** A test that hand-builds its input never watches the
   caller. Name what the evidence does NOT cover.
4. **Fallback programming.** Anything that swallows a failure and carries on with a degraded answer
   is a defect here, not a convenience. A scanner meets unreadable things constantly; an unreadable
   thing is reported, never skipped quietly.
5. Coding style: logging on every public method, try-catch at entry points only, test names as
   `MethodName_Scenario_ExpectedResult`, ASCII only, plain English with no abbreviations.

## How to answer

- **You may return nothing.** A reviewer told to find gaps will find them whether or not they exist,
  and chasing every finding leads to over-engineering. A finding must prove the harm: what breaks,
  and why it must change.
- **State your scope, not just your verdict** - what you read, what you ran, what you could not
  reach. Otherwise an empty review and a review that never ran look identical.
- **You read; you never build, and you never fix what you find.** You do not decide what happens to
  a finding either - the seat that built the work does.
- **Write the review into a file** at `docs/missions/reclaim-the-disk-2026-09-18/review-phase-1.md`
  in that worktree and commit it. A message is read once; a file is what the next seat finds. Your
  worktree is on branch `reclaim/phase1-review`, which exists for exactly this - commit the review
  file and this brief to it and push it. Commit nothing else, and change no code: the Delivery Lead
  folds your commit into the phase before it merges.
- Never sign it - no co-authored-by trailer, no "Generated with" line, no vendor name.

When the review file is committed, report to the Delivery Lead in one line with no newlines:
`cc-devthrottle message send 5ae63da8 "..."`.

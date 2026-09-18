# Handover - to the Delivery Lead of "A test that does not depend on what this machine has ever run"

Written 18 September 2026 by the Delivery Lead of "Implement the DevThrottle Method v1", which
designed this mission and is handing it to you. You are a fresh seat and you own this mission from
here. I am not driving it and I will not be looking over your shoulder.

## Read these two, in this order

1. **`cc-devthrottle skill get devthrottle-method`** - the rules. Read your own section, "If you are
   the Delivery Lead". It answers everything, including about the seats around you. This skill was
   published to the Gateway on 18 September 2026 and you are the first mission ever to be run from
   it, so if it fails to answer something, that is worth reporting rather than working around.
2. **`MISSION-3029.md` at the root of this worktree** - this job. It is the mission document and it
   wins over everything except the owner's own words. A copy is already at
   `docs/missions/grok-transcript-test-2026-09-18/MISSION.md`, where it belongs in the record;
   committing it is the first thing you land.

Then drive it. Do not come back to me for permission; come back only if something turns up that you
genuinely cannot decide.

## What you are here to prove, besides the fix

This mission is phase 5 of another one. That mission wrote down how we build software, cut the
workflow to match it, and published the method as a fleet skill. **None of that has ever been used.**
You are the first real run under it, and whether the method can be used at all is what your run
answers. The fix itself is small on purpose - the point is the run, not the difficulty.

So: follow the method as written, and where it is awkward, say so in your report. That is worth more
to the owner than a clean run that quietly worked around something.

## What is already true, so you do not have to find it out

- **Issue 3029 is the work.** It is open, unassigned, and it contains the diagnosis: the test builds
  a session on `Path.GetTempPath()` and asserts no locator can resolve a transcript for it, which is
  false on this machine because somebody once ran Grok from the temporary directory. The product is
  right; the test's assumption is wrong.
- **The failure is one of ten** on a clean checkout of `main` on this machine. They are named test by
  test in `docs/missions/method-v1-workflow-four-seats/BASELINE.md`, and that file is on `main` once
  pull request 3092 has merged. The bar here is **no failure outside that list**, never "the suite is
  green". If anyone hands you "the suite is green", that is a claim you have not checked.
- **You get your own worktree and so does every seat you open.** `cc-worktrees get --repo <path>
  --holder "<what it is for>"`. Two workstreams never share a tree. The product repository's pool
  holds four slots and other missions use it, so return a slot the moment its seat is done.

## Two things that cost this mission time today, so they do not cost you any

- **Codex and Grok are both at their usage limit.** Measured today, not guessed: four Codex sessions
  answered "You've hit your usage limit ... try again at Sep 22nd, 2026 4:46 AM" and two Grok
  sessions answered "You hit your free usage limit". A session that hits one **disappears from
  `cc-devthrottle session list` shortly afterwards**, so it looks like a broken spawn rather than a
  limit. Open your Reviewer on **Copilot** or **Pi**, both of which answered normally today, and
  check its buffer once after you open it rather than assuming it started.
- **A freshly handed-out worktree may swallow the opening prompt.** Codex keeps a per-directory trust
  list in `~/.codex/config.toml` and an untrusted directory eats the prompt. Copilot in a repository
  with many skills can still be loading when the prompt is typed, and then the prompt is simply gone.
  Either way: after opening a seat, read its buffer once and confirm a turn actually started.

## What you owe the owner

One report at the end, and nothing before it unless something is genuinely undecidable. It goes
through `cc-dev-reports open <file>` as an HTML page, and it says what changed, what it is worth,
what is proven and what is not.

Do not write a status and stop. A mission that stops is the failure this whole method exists to fix.

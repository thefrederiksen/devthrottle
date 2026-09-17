# cc-ship phase 1 - the three assumptions, proven before building on them

Issue 2935, "First task". Everything below was run for real on the Mac mini Director
(`devthrottle-mac-mini`, Director 2.3.0) on 2026-09-16, against the live fleet. The
probes are in `probes/`; the modules they exercise are the ones the tool is built on
(`src/fleet.py`, `src/contracts.py`, `src/briefs.py`, `src/preview.py`).

The raw run folders (briefs, outputs, per-poll observations, screenshots) are under
`<cc-director data>/ship/probes/` on that machine; they are not committed.

## 1. Knowing a spawned session has finished - PROVEN

**Rule:** a session has FINISHED only when its output file exists AND it has been seen
flagged done (`pendingDeletion` in `cc-devthrottle session list --json`). Leaving the
list counts only after the flag was seen: the Director keeps a flagged session listed
for 30 seconds, much longer than one poll. A session that leaves the list without the
flag, or is reported `crashed`, has CRASHED, even if its file exists. (This is tighter
than the issue's "or is gone". The inspection showed a file followed by a process
death would otherwise count as a finish.) A session that was seen working and then
sits idle for 90 seconds with no output and no done flag has STALLED.

| Case | What happened | Detected as | Time |
|---|---|---|---|
| Normal finish | Claude Code session wrote the file, then ran `session done` | finished | 22 s from spawn |
| Crash | Session stopped with `session stop` while it was working (1 min 10 s into a long task) | crashed (reported then as "left the fleet list without writing its output") | first poll after the stop |
| Stall | Seen live, not staged: 6 reviewers could not authenticate to the Gateway, printed the error, and went idle | (led to adding the STALLED outcome) | - |

The finish timeline shows why both halves are needed: the file appeared at 11:51:04
and the done flag at 11:51:15. Treating "file exists" alone as finished would have read
a file the session might still be writing.

**What a crash looks like:** the row disappears from `session list` at once (the stop
output says "process ended, row removed"); the output file never appears.

**Not proven:** a process that dies on its own (killed from outside, or crashing) rather
than being stopped through the fleet. The `crashed` flag in the list is handled but was
never observed. The STALLED rule is unit-tested, not reproduced on demand.

## 2. A reviewer of another family writes a valid review.json from a file brief - PROVEN

Reviewer: Codex (codex-cli 0.154.0, model gpt-5.6-sol), author family Claude Code.
Subject: a one-function change in a devthrottle_internal worktree with a planted
defect (`Math.round(part / whole) * 100`, so 3 of 8 shows 0%).

| | Result |
|---|---|
| Runs (each a fresh session) | 6 |
| Valid `review.json` on the first write | **6 of 6** |
| Correction turns needed | **0 of 6** |
| Planted defect found | 6 of 6, each with a traced input (3 of 8 -> "0%") |
| Also found (real): the new formatter has no callers | 6 of 6 |
| Time per review | 67 - 89 s |

**The correction turn works** (run 7, not counted above): the probe replaced the
reviewer's first file with an invalid one. cc-ship saw the file, cleared the session's
done flag with `session done --undo` inside the Director's 30-second reaping grace,
wrote the problems to `correction-1.md`, and prompted the same session with one line.
The same session rewrote a valid file in one turn (118 s in all).

## 3. A verifier drives a Vercel preview unattended and captures a screenshot - PROVEN

Previews of devthrottle_internal redirect to Vercel sign-in. The owner enabled
Protection Bypass for Automation; the secret is `VERCEL_AUTOMATION_BYPASS_SECRET` in
`<cc-director data>/config/credentials.env` on each machine that verifies.

cc-ship, not the verifier, trades the secret for Vercel's `_vercel_jwt` cookie (curl,
with the secret in a private header file, never on a command line) and writes a
Playwright storage-state file. The verifier only loads that file. The secret never
reaches a brief, a URL, a screen or a log.

A fresh Claude Code verifier session, given only the brief file, ran three scenarios
derived from the intent against `devthrottle-2fb8771xm-...vercel.app`: home page, sign
up, pricing from the header. All three were live passes with a screenshot each in the
run folder, and `verify.json` was valid. 50 s from spawn to finished. The screenshots
were opened and checked by hand: they show the real site, not the sign-in page.

## What the probes found that the design did not expect

1. **A spawned session in a repository the agent has never trusted quits at once.**
   Claude Code and Codex both open with a "trust this folder?" question. The Director
   types the first prompt into it, and for Claude Code the preselected answer is "No,
   exit". The session exits cleanly (exit code 0) about 2 seconds later. Trust is per
   repository, not per worktree. **cc-ship must spawn its sessions in the author's
   worktree and check trust before spawning**, failing with the fix. (This is also a
   Director defect: the readiness check treats the trust question as a ready prompt.)
2. **A Gateway timeout on spawn does not mean the spawn failed.** All 7 spawns in one
   burst landed although the command reported a timeout. `spawn_session` now settles
   this by looking the session up by its name before reporting failure, so no reviewer
   is left running with nobody watching it.
3. **Spawning 7 sessions at once left them unable to authenticate to the Gateway**
   ("missing or invalid token"). They stopped, as their instructions say. cc-ship
   spawns one reviewer at a time, so this should not arise in a run; the STALLED
   outcome catches it if it does.
4. **Spawned reviewers inherit the author's mission**, and with it an instruction to
   fetch the mission workflow before anything else. That is noise for a reviewer, and
   it is the step that failed in finding 3.
5. **The python.org Python on the Mac has no certificate store**, so plain `urllib`
   cannot reach Vercel. cc-ship uses `curl`, which ships with macOS and Windows.

## Independent inspection

Codex inspected this pull request before merge and failed it with two errors and two
warnings, all fixed with a regression test each:

- A file followed by a vanished session counted as finished (the finish rule above).
- A scenario could be `pass` without having run live. Now `pass` and `fail` must be
  `live: true`, and `untested` must be `live: false`.
- An older `success` status could hide a newer `failure` on the same deployment. Now
  only each deployment's latest status counts.
- The derived bypass cookie file stayed on disk. It is now removed when the verifier
  ends, whatever the outcome.

A fresh Codex session re-inspected the fixes. It confirmed the four were fixed and found
two more errors and one warning, also fixed with regression tests:

- `go` was accepted with nothing run live (for example, a verifier that could not get
  past the preview sign-in). Now `go` needs at least one live pass, and a surface that
  exists but could not be driven is `inconclusive`.
- A finding whose `id` was a list crashed the checker instead of returning a problem,
  which would have broken the correction turn.
- The cookie file could still outlive a failure between its creation and the cleanup.
  Creating and removing it is now one `with` block. The live verifier probe was rerun
  through it (45 s, three live passes) and left no cookie file behind.

A third fresh session found the neighbouring gap: `no-surface` was still accepted when a
preview had been supplied but could not be opened. The validator now takes whether a
surface was supplied and rejects `no-surface` when one was. It also asked for the
cookie cleanup to be guarded through the probe itself; a test now runs the probe with a
failure straight after the cookie file is created.

Each fix, in every round, was reverted to confirm its test fails, then restored.

## Setup a machine needs before cc-ship runs there

- Codex installed and signed in (`codex login`).
- The pilot repositories trusted by Codex (`~/.codex/config.toml`) and by Claude Code
  (open it once in the repository and accept).
- The cc-secrets entry `vercel-automation-bypass-secret`, and curl 8.3 or newer.
- `playwright-cli` on the path.

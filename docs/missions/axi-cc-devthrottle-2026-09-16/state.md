# AXI for cc-devthrottle - mission record

Status: COMPLETE (2026-09-16 to 2026-09-17). Summary for the owner: `report.html` beside this file.

Mandate: issue #2922 and its Implementation brief comment, and `docs/axi-standard.md`.
Conduct: `cc-devthrottle workflow instructions mission`.

This file replaces the running state note kept during the mission. Live fleet identifiers, private
repository names and machine-local paths are left out because this repository is public.

## Design rulings (Architect)

1. **One plain state per session**, named from the roster's own triage fold, in this order:
   - `crashed == true` -> `crashed`
   - `triageBucket == "needsYou"` -> `needs-you`
   - `triageBucket == "onHold"` -> `snoozed` (this also holds supervised Workers that have stopped,
     which the Gateway parks in the same bucket - inferred as the right word, not stated by the owner)
   - `triageBucket == "active"` and `activityState == "Working"` -> `working`
   - `triageBucket == "active"` otherwise -> `ready`
   - a missing or unknown bucket fails loudly with exit 1. `lastStatusReason` is never read.
2. The shared output helper lives in `tools/cc_shared/axi_output.py`.
3. `--json` keeps its shape; filters narrow it to the same bare array. Unfiltered `--json` prints the
   Gateway's answer unchanged, and this is also why `machine restart-request-status --json` passes an
   empty answer through.
4. A missing or malformed Gateway answer is always an error, never an empty or negative result. Every
   list and every change command validates each field it reads, before filtering.
5. A full-path `--repo` filter ignores letter case and slash direction only for Windows-shaped paths (a
   drive letter, colon and slash, or a leading double backslash); every other path matches exactly. A
   folder-name match ignores letter case and keeps whitespace.
6. `message send` is suggested only when rows exist; the five-state line is shown when a filter matched
   nothing; an empty fleet needs neither.
7. Every usage error, whether raised by the parser or inside a command, goes through one formatter
   (`usage_errors.py`): Error line, Usage line, Valid options, `help[1]`, exit 2. Runtime errors exit 1.
8. Skill and workflow pulls validate the whole Gateway answer, down to the final bytes and file names,
   before touching the disk. A journal-based swap helper was tried and removed as out of scope; crash-safe
   replacement and Windows name aliases moved to issue #2972.
9. Linux proof: a real Linux machine joined the fleet during the mission and ran the live proof; the
   continuous integration Linux runner is the test proof.

## Owner orders during the mission

- 2026-09-16: no fleet messages. Seats received their whole task when started and reported through the
  pull request (last body line `Ready for inspection.`) or an issue comment starting `Blocked:`.
- 2026-09-16: merge #2932 at once. It merged before its last fix had been re-inspected.
- 2026-09-16: less continuous integration waiting. Runs for already merged pull requests were cancelled,
  and #2986 was built: a pull request runs only the jobs its files can affect, the lowest-version leg
  runs on Linux only, and a final `CI result` job can serve as the one required check. Separately, the
  owner's ruling in #2991 made main keep only the newest run as well.
- 2026-09-17: the repository is public. Live fleet rows had been pasted into eight pull request
  descriptions and two issue comments. The descriptions were redacted (their old text remains in
  GitHub's edit history; purging it is the owner's call) and the comments were deleted and re-posted
  with data rows withheld and agent names masked.
- 2026-09-17: every agent family used for inspection had run out of usage, so the owner had the last
  review of #2986 done by a session of the same family that built it.

## What landed

| Step | Pull requests |
|---|---|
| 1. Tests on Windows, macOS and Linux | #2932 |
| 2. Shared output helper | #2934, #2947; #2959 (tested dependency floor; an empty source counts as `count: 0`) |
| 3. `session list` | #2944 |
| 4. No-argument live state | #2953 |
| 5. `director`, `machine`, `mission`, `schedule`, `repo`, `worktree` lists | #2954, #2955, #2957 |
| 6. Usage errors, `help[]` after changes, short `--help`, actionable errors | #2963, #2962, #2965 |
| Owner request | #2986 (continuous integration runs only what changed) |

Every pull request was inspected by an agent of a different family before merging, with two
exceptions: the last fix on #2932 (merged on the owner's order before its re-check) and #2986 (reviewed
by the same family on the owner's order). 38 inspection rounds were spent on the twelve tool pull
requests.

## Proof

- macOS: `live-mac.txt` beside this file (12 commands exit 0, 3 deliberate usage errors exit 2, no
  non-ASCII output). The full suite passed 3102 tests on macOS in the last re-check of #2965.
- Windows and Linux: the live-proof comments on #2922 (3099 and 3101 tests passed; 0 non-ASCII bytes).
- Benchmark: the #2920 harness re-run against the merged change. Tokens per task 206K to 97K, commands
  5 to 2, right answers 29/30 to 30/30, sessions readable back 0/26 to 26/26; the two tasks that first
  showed no gain were re-run after the follow-up fix and dropped to 2 commands and 96K tokens each. The
  full results are kept in the private research repository.

## Lessons

- A proof pasted for convenience into a public repository is a disclosure. Show counts, headers and
  help lines; withhold data rows.
- Each inspection round found one more unvalidated Gateway field until Workers were told to audit every
  field a command reads, up front, and table it in the pull request.
- A watch that only looks for a result cannot see a reviewer that died or never started; watch the
  session too, and allow for the fleet list lagging a new session.
- Waiting on continuous integration was the largest delay; see #2986.

## Known and not in scope

- Issue #2972: skill and workflow pull can leave a mixed folder if killed mid-write, and do not refuse
  Windows name aliases such as `SKILL.md.`.
- Reported only, because the Gateway owns them: `worktree list` counts a worktree twice when two
  Directors on one machine report it (the output says so); worktree paths use forward slashes while
  repository paths use backslashes; a disabled schedule shows an empty next-run cell.
- Linux: neither the setup tool nor the installer adds `~/.local/bin` to PATH; the setup doctor reports
  it correctly.
- Not proven: the benchmark covers one frozen fleet, one agent setting and Windows only, and four of its
  six tasks were not re-run after the follow-up fix. `selftest` still runs only on Windows.

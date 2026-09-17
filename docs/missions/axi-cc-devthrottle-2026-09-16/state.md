# AXI for cc-devthrottle - running state

Status: COMPLETE (2026-09-16 to 2026-09-17). All six steps merged, proven live on macOS, Windows and Linux, benchmark re-run; the owner-requested continuous integration change (#2986) merged as d3e3d9bd. Summary: report.html beside this file. Mandate: issue #2922 plus its Implementation brief comment,
and `docs/axi-standard.md`. Conduct: `cc-devthrottle workflow instructions mission`.

Mission id 43b85d84-07bd-410b-a6c6-88a137bcc1c3, workflow run 231dfc6a.

## Seats

- Architect: 32b06118 (Mac). Only seat that lands on main.
- Windows Worker: none seated. 30e18aa1 was shut down by the owner. Spawn a fresh Worker on
  SOREN_NORTH (repo `D:\ReposFred\devthrottle_internal`) for the live Windows run and the benchmark
  re-run. Harness (uncommitted, on that disk only) at `docs\research\axi\session-list-bench`:
  run.py (--task/--reps/--report), fakecli.py (wraps the installed cc-devthrottle; condition B is the
  prototype - swap in the real command), tasks.py, fixture.json. run.py keeps agent workspaces
  under `C:\Users\soren\AppData\Local\Temp\fleet-work`, outside any repository - keep it so.
  Prototype results: `docs\research\axi\session-list-results.html`.
- Inspectors: Codex on SOREN_NORTH hit its usage limit on 2026-09-16 (until 2026-09-22); Gemini there did not start. From then on inspectors run as Codex on THIS Mac (signed in separately, working). Earlier: Codex sessions on SOREN_NORTH, one per pull request,
  posting their review as a pull request comment and replying with one line.
- Manager: the default agent on the Mac, reset at every step boundary. Steps 4-5 Manager: d08e5ed6.
- Tools the Architect uses (scratchpad, not durable): inspect.sh spawns a Codex inspector from a
  template; watch-axi.sh / watch-comments.sh watch for `Ready for inspection.` and `## Inspection`.

## Owner orders

- 2026-09-16: no fleet messages. Seats get their task in the spawn prompt and report through
  the pull request (last body line `Ready for inspection.`) or an issue comment starting `Blocked:`.
- 2026-09-16: merge #2932 now (done, before its last fix was re-inspected).

## Architect notes

- The .NET check does not gate these Python-only pull requests; the gate is the three Python
  tool-contract jobs. Say so in every handoff - a Manager waited 84 minutes on it.

## Design rulings (Architect)

1. **One plain state** is folded in the tool from the roster fields, in this order:
   - `crashed == true` -> `crashed`
   - `triageBucket == "needsYou"` -> `needs-you`
   - `triageBucket == "onHold"` -> `snoozed` (this also holds supervised-and-stopped Workers,
     which the Gateway parks in the same bucket - inferred as the right word, not stated by the owner)
   - `triageBucket == "active"` and `activityState == "Working"` -> `working`
   - `triageBucket == "active"` otherwise -> `ready`
   - anything else (missing or unknown bucket) -> fail loudly with exit 1 naming the value. No guess.
   `lastStatusReason` is never read. Values checked against the live roster on 2026-09-16:
   buckets seen `active`, `needsYou`; enum on main is NeedsYou / Active / OnHold
   (`src/CcDirector.Gateway.Contracts/SessionOrdering.cs`).
2. The shared output helper lives in `tools/cc_shared` (new module), with its own tests.
3. `--json` shape never changes; filters narrow it (same bare array).
4. Linux live proof: a Linux machine `devlinux` joined the fleet on 2026-09-17 (Directors 2.3.0 and 2.4.0) - use it for the live Linux run; the CI `ubuntu-latest` runner is the test proof.

## Steps

| # | Step | Pull request | State |
|---|------|--------------|-------|
| 1 | CI on three platforms | #2932 | MERGED 66609c3c on the owner's order, with both re-inspection fixes in (6d6be175); that last fix was not re-inspected, and the .NET check had not finished (no .NET code changed) |
| 2 | Shared output helper | #2934, #2947 | MERGED cccbba7b and 0649a282 (follow-up for the re-check's 2 low-severity defects); both inspected, 2947 with no findings |
| 3 | `session list` | #2944 | MERGED 914c4dee after inspection (6 findings fixed) and re-check. Re-check low finding: with Click 8.1.x on Windows, CliRunner merges stderr, so tests reading result.stderr fail (4 new + 5 already on main); CI installs current Click and passes -> handled in step 6 |
| 4 | No-argument live state | #2953 | MERGED 321722ce after inspection + clean re-check. Architect error: merged while 3 of 6 rebased-head Python checks were still queued (wait script misread them); all 6 later confirmed green on 70665dd6 |
| 5 | Other list commands | #2954, #2955, #2957 (+ shared #2959) | ALL MERGED (e6f763f2, 53cd5535, 2957 on main). History: Pattern: each re-check finds another unvalidated Gateway field -> every fix Worker now audits every field read (table in the pull request body). 2954: MERGED after re-check 3 (no findings). 2955: MERGED after re-check 3 (one low finding - non-ASCII in error messages - moved to 6a). 2957: inspection (missed for ~an hour by a watcher that matched only '## Inspection') found 5 -> Worker e9ede073 with audit; ruling (refined after re-check): full-path --repo match ignores case and slashes only for Windows-shaped paths = drive letter + colon + slash, or leading double backslash; everything else exact. Re-check found the any-backslash rule too loose -> Worker 9bef8c5c. |
| 6 | Errors, `help[]`, `--help` | #2963 (6a), #2962 (6c), #2965 (6b) | ALL MERGED: d91be1e9, 08878ba0, 2965 after 7 inspections (last clean). History: 2963 (6a) MERGED after inspection + re-check (re-check left one medium: matches_repo strips whitespace from folder names, so '/home/a' and '/home/a ' both match --repo a -> fold into 6b). Manager 8ed40987 (stopped, phase built). PLAN (Architect): 6b and 6c each ADD their own tools/cc-devthrottle/src/axi_cli.py - they will conflict. Merge 6a first; rebase 6c onto it (switch its usage_error to 6a's shared formatter), re-check, merge; then rebase 6b onto both, merging its helpers INTO the one axi_cli.py, then inspect 6b (not before - it would change too much).  2963 inspected: 4 findings (body usage errors bypass the formatter -> ruling: one shared formatter for parser AND body usage errors, 6c switches to it after 6a merges; setup role exit code - already fixed in 2962; Gateway sentences not escaped; POSIX backslash folder name) + C: edge case -> Worker b389829a. 2962: rebased onto 6a + 5 fixes (Worker 176ec143) -> re-check (5705254941) found 3: partial file entries / missing body still blank files (P1), workflow materialize deletes helpers on a partial answer (P1), browser start eval line unusable on Windows (ruling: help[] uses `browser attach <name>`) -> Worker f9fad24c fixed (bundle_swap check-then-swap). Re-check 2 (5705534263): 6 more in skill/workflow pull writers (omitted metadata emptied; support file overwrites SKILL.md; crash mid-swap; skill get omitted files; cache refresh loses hash; bad encoding type) -> Worker 750eb348 with full audit. Re-check 3 (5705838798): the new journal swap helper itself caused 3 of 5 findings. RULING APPLIED: journal swap helper removed; validate-whole-answer-first kept; crash-safe replacement and Windows name aliases moved to issue #2972; percent/PowerShell expansion in help values fixed here -> Worker d5aa2210. (Earlier ruling: if the next re-check finds only more pull-writer edge cases, file them as a separate issue rather than hold the mission (those writers predate this mission). 6b: consolidation Worker ae49dfc7 stacked on 6c branch (move with rebase --onto after 6c merges), plus benchmark follow-ups and the matches_repo whitespace fix. First 2962 inspection (comment 5704439597): 5 findings - P1 partial Gateway answer deletes local skill/workflow support files; P1 raw values in command-looking lines outside help[] (shell injection when copied); setup/autostart pass engine exit codes through (must be 1); control characters in ascii_text; actions JSON test compares against itself. Fix is HELD until 6a merges: one Worker then rebases 6c onto main, switches usage errors to 6a's formatter, merges axi_cli.py, and fixes all five. Merge order 6a, then rebase 6c and 6b. Earlier notes: Manager seated (handoff lists the collected items). Collected: must include: an unknown flag exits 2 AND lists the valid flags (2954 finding 5; today it prints a boxed Click error) ; also: with Rich 14.3.2 and FORCE_COLOR=1 on Windows, six session-stop output tests fail on line wrapping (2953 inspection) - decide a Rich floor or fix the tests ; also: sweep every command for notes/cautions escaped only when non-ASCII (`isascii()` check) - control characters split lines; always escape free text (2954 re-check finding 5) ; also: tools/cc-devthrottle/requirements.txt still says typer>=0.9.0 with no click floor - match pyproject (2959 inspection finding 1) ; also: session list --repo lowercases every full path (same defect as 2957 finding 3) - use the shared Windows-only case-insensitive matcher |
| - | Windows live run, benchmark re-run, report | - | FINAL PROOFS (after all six merged, origin/main 56fcdde9): Mac live run done (live-mac.txt beside this file: 12 exit 0, 3 deliberate usage errors exit 2, 0 non-ASCII lines). Linux Worker 248d944e on devlinux and Windows Worker de8900bd on SOREN_NORTH (live run + B-real re-run of the send and crashed tasks) post comments on #2922 headed 'Live proof - Linux' / 'Live proof - Windows'. Earlier: BENCHMARK DONE (devthrottle_internal branch research/axi-session-list-bench, docs/research/axi/session-list-real-results.md, measured on c38b96ab): right answers 29/30 -> 30/30; tokens 206K -> 97K (-53%, noise 1.5%); commands 5 -> 2; time 27s -> 16s; readable 0/26 -> 26/26. All #2920 criteria pass. Harness now committed on that branch (not merged). Live Windows run of every list command done (all ASCII, exit 0). Still to do: live Windows run again after step 6; post the table on #2922. |

## Known and not in this mission's scope

- Issue #2972 (filed by the Architect): skill/workflow pull writers can leave a mixed folder if
  killed mid-write, and do not refuse Windows name aliases (SKILL.md.). Both predate this mission.

- The 2957 re-check inspector could not delete its temporary Python environment on SOREN_NORTH
  (`D:\ReposFred\devthrottle-pr2957-inspect-1ab5a5ab-venv`), blocked by an approval policy. Left
  for the owner to remove. Same for `D:\Temp\axi-pr2962-inspect-env` (2962 re-check inspector).

- On Linux neither the setup tool nor the wizard adds `~/.local/bin` to PATH; the doctor correctly
  reports it. Found by re-inspection of #2932. Report to the owner at the end.

## Benchmark follow-ups (fold into 6b, which owns session_ops)

- From the 2963 re-check: `repo_ops.matches_repo` strips whitespace from folder names and the filter;
  compare folder names case-insensitively but keep their whitespace (session, repo, worktree lists).

- `session list` help[] has no way to message a session: agents guessed `session message`/`session send`
  (4 commands instead of 2). Add `cc-devthrottle message send <id> "<message>"` to help[].
- The list output never names the valid states when a state has no sessions, so no agent tried
  `--state crashed` and all dumped the 114 KB JSON. Name all five states in help[] (for example
  `cc-devthrottle session list --state needs-you|working|ready|snoozed|crashed`).
- `--fields` with `--json` refuses with "Drop one of them" - say which: `--fields` for a few fields,
  `--json` for every field.
- After these land, re-run only condition B-real for the send and crashed tasks (10 runs) to confirm.
- Report only (Gateway-owned, rendered verbatim): `worktree list` count counts rows reported by two
  Directors on one machine twice (the repeated: line explains it); worktree paths use forward slashes
  while repo paths use backslashes; disabled schedules show an empty next-run cell.

## Owner decision 2026-09-17: less CI waiting

- Cancelled 7 CI runs for already-merged pull requests.
- Owner chose: on a pull request, run only the jobs for what changed (lean to keep); floor leg on Linux
  only; main still runs everything per commit. Worker a3f0027c builds it on branch ci/run-what-changed = pull request #2986.
- Lesson: a comment-only watch cannot see an inspector that died (2965 re-check 5 inspector vanished when
  a SOREN_NORTH Director went away). wait-inspection.sh watches both.

- 2026-09-17: another session merged #2991 on the owner's ruling - main CI now keeps only the newest run per branch too. #2986 (run only what changed) must rebase onto it; its 'main runs every commit' text is now wrong.

## 2026-09-17 early morning

- Codex on this Mac also hit its usage limit (until 02:45). Inspector for #2986 re-check moved to Grok on
  SOREN_NORTH (dd7db93d). inspect.sh takes INSPECT_AGENT / INSPECT_MACHINE / INSPECT_REPO.
- Windows proof Worker de8900bd blocked on a permission prompt for a delete on a possibly-empty variable
  path; not approved; stopped. Replacement 6efa8322 (never delete via a variable path).
- Mac memory pressure repeatedly kills background waits; check results by hand when that happens.
- DONE: Linux live proof posted on #2922 (3101 passed, 0 non-ASCII bytes in 32 streams).

## 2026-09-17: public data exposure, fixed

- The repository is public. Live output with customer names and private paths had been pasted into pull
  requests 2944, 2953, 2954, 2955, 2957, 2962, 2963, 2965 and the Linux proof comment on #2922.
- Owner chose: redact the descriptions (done; old text remains in GitHub edit history - purging needs
  GitHub support, the owner's call). Both live-proof comments were deleted and re-posted with data rows
  and session rows withheld and agent names masked (Linux 5712600385, Windows 5712599899).
- live-mac.txt in this folder has the same rows withheld.
- DONE: Windows live proof (3099 passed, 3 skipped, 0 non-ASCII bytes in 16 outputs).

- Owner decision 2026-09-17 07:10: use a session of the builders' own agent family as the reviewer for the #2986 re-check (every other family was out of usage; a Copilot re-check had read a stale checkout and did not count). Reviewer d606f008.

- #2986 re-check by the same-family reviewer: no findings. MERGED d3e3d9bd.

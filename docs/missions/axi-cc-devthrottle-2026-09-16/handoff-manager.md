# Manager handoff - AXI for cc-devthrottle

You are the Manager. Read, in this order: `cc-devthrottle workflow instructions mission`,
`gh issue view 2922 --comments` (the Implementation brief comment is the mandate),
`git show origin/main:docs/axi-standard.md`, and `state.md` beside this file (design rulings - binding).

## Your phase: step 6 - everything else

Steps 1-5 are merged (session list, no-argument live state, and the six list commands). Read
`list_sessions` in `tools/cc-devthrottle/src/session_ops.py` and `machine_ops.py` on origin/main
for the settled pattern, and the audit tables in the bodies of merged pull requests 2954, 2955 and
2957 for how thoroughly Gateway answers are validated.

Brief item 6: unknown flags exit 2 with the valid values, errors an agent can act on, `help[]` after
mutations, short `--help` everywhere. Split into these pull requests, in this order:

- **6a - usage errors, everywhere at once** (branch `axi/usage-errors`). One place (a shared Typer/Click
  group or command class, applied to the root app and every sub-app) so that an unknown option, an
  unknown command, a missing argument or a bad value prints plain ASCII (no Rich box) to standard
  error, exits 2, and LISTS the valid options or commands for that command. Test it across every
  command programmatically (walk the command tree), not by hand-picking a few. Also in 6a, collected
  from inspections:
  - `tools/cc-devthrottle/requirements.txt` still says `typer>=0.9.0` with no Click floor - match
    `pyproject.toml` (Typer 0.16.1, Click 8.2.1).
  - `session list --repo` lowercases every full path. Use the shared matcher from `repo_ops`
    (`path_matches`: case- and slash-insensitive only for Windows-shaped paths - a drive letter, colon and slash, or a leading double backslash - exact otherwise; the matcher lands with pull request 2957). Also fix its known edge case: a drive-relative filter `C:` matches the drive root `C:\` because folding strips the root slash (2957 re-check 2) - keep them distinct.
  - Sweep every command for free text (notes, cautions, Gateway messages) escaped only when
    `isascii()` is false - control characters split lines. Always escape free text with
    `axi_output.escape_ascii`. Include error messages that quote Gateway values with `!r` (for example
    `schedule_ops.py` and `mission_ops._bad_mission`, found by the 2955 re-check 3): repr() keeps
    non-ASCII characters, so standard error stops being ASCII.
  - With Rich 14.3.2 and `FORCE_COLOR=1` on Windows, six `session stop` output tests fail on line
    wrapping (test_session_stop.py around lines 158, 174, 311, 345). Make the tests robust or the
    output unwrapped - do not just pin Rich.
- **6b and 6c - `help[]` after mutations, short `--help`, actionable errors** (branches
  `axi/help-mutations-a` and `axi/help-mutations-b`), splitting the command groups roughly in half
  (for example 6b: session, message, mission, repo, worktree, director, machine; 6c: schedule,
  workflow, skill, settings, setup, email, diag, autostart, browser, actions). Every mutating command
  ends with `help[]` next commands (placeholders, never guessed values); every command and group has a
  one-line `--help` summary; every error names what failed and what to do next. `--json` shapes never
  change. A test walks the command tree and fails for any command without a short help.

## How

- NO FLEET MESSAGES. The owner has ordered that nobody sends `cc-devthrottle message send` or
  `message ask` - they interrupt sessions. Give a Worker its whole task in the `--prompt` of
  `session spawn`, and learn what it is doing by reading it (`cc-devthrottle session list`,
  `cc-devthrottle session buffer <id>`, its branch, its pull request), never by messaging.
- Hire one Worker per pull request (the default agent on this Mac, `--controlled-by self`, `--role Worker`,
  named `AXI Tools - Worker - <what>`, `--mission <mission-id>`), each in its
  own worktree `/Users/soren/ReposFred/devthrottle-axi-<what>` cut from `origin/main`. They can run in
  parallel. Never the shared checkout. Foreground only. Two pull requests touching `cli.py` will
  conflict - have later ones rebase onto `origin/main` when an earlier one merges.
- 6a goes first. 6b and 6c may be built in parallel with it, but each rebases onto origin/main after
  6a merges (all three touch `cli.py`).
- Lesson from step 5: inspections kept finding one more unvalidated case per round. Tell every Worker
  to audit its whole surface before asking for inspection, and to put that audit in the pull request.
- Every pull request must pass all six `Tool contracts (Python)` jobs (latest and declared floor on
  three systems) on its FINAL commit.
- If pytest is missing on this Mac, make a venv in the worktree.
- Proof in each pull request body: the three `Tool contracts (Python)` jobs green, and the real
  command run against the live fleet from the Mac, output pasted. **The `.NET` job does NOT gate
  these changes - never wait for it.**
- Open the pull request; do NOT merge. Nothing but the Architect lands on main.
- No attribution of any assistant anywhere in commits or pull requests.
- When a pull request's three Python jobs are green and the proof is in the body, add the line
  `Ready for inspection.` as the LAST line of its body. That is the report. Stop that Worker.
- When every pull request of your phase is marked ready, stop working.
- Blocked on a real design fork: write it as a comment on issue 2922 starting `Blocked:` and stop.

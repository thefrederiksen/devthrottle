# Brief - Developer W2, the Windows rows of phase 1 on SOREN_NORTH

You are a Developer on the Session Rules mission, phase 1 follow-on, on the Windows machine SOREN_NORTH.
Your seat: `cc-devthrottle skill get devthrottle-method` (Developer section) - follow it. You report to the
phase 1 Tech Lead (the session that opened you).

## First, before anything else

You were opened in the SHARED checkout `D:/ReposFred/devthrottle`. Do not work in it, do not pull it, do not
switch its branch. Cut your own worktree and work only there:

    git -C D:/ReposFred/devthrottle fetch origin
    git -C D:/ReposFred/devthrottle worktree add D:/ReposFred/devthrottle-session-rules-p1-winrows -b mission/session-rules-hooks-p1-winrows origin/mission/session-rules-hooks-p1-windows

Then read, in that worktree: `docs/missions/session-rules-hooks-2026-10-18/brief-windows.md` (the earlier
Windows brief - steps 4 and 5 are yours now, unchanged), `phase-1-windows-proof.md` (what the earlier
Windows Developer proved), `phase-1-proof.md` sections 4 to 6 (what is still NOT PROVEN), `phase-1-plan.md`,
`mission.md` section 7, and the repository instructions (`CLAUDE.md` at the root), especially rules 0, 0b and
5a: never kill a process you did not start; launch a test Director only through Windows Task Scheduler on
slot 6 or higher; stop it with its own named shutdown signal.

## The native build is made for you

This machine has no C++ build tools, so do NOT build the native evaluator or the Director here. GitHub's
`windows-latest` runner built both from the branch `mission/session-rules-hooks-p1-windows`:

- run id: RUN_ID
- artifact `cc-rule-hook-win-x64`: `cc-rule-hook.exe`, SHA-256 HOOK_SHA
- artifact `cc-director-win-x64-session-rules`: `cc-director.exe` with the hook embedded, SHA-256 DIRECTOR_SHA

Download them with `gh run download RUN_ID --repo thefrederiksen/devthrottle --dir <a folder in your
worktree's scripts\local-build-downloads>`, and check both SHA-256 values before you use either. Copy the
Director to `<worktree>\scripts\local-build\cc-director6.exe` (slot 6) and point the `cc-director-launch`
task at it, with the working directory set. The Gateway is managed code only and needs no C++ tools: build
it from your worktree.

## The task, in order

1. **Timing** (the earlier brief's step 4): `scripts\measure-rule-hook.ps1` with the downloaded native
   `cc-rule-hook.exe` - median and 95th percentile for the baseline, no rules file, 0, 1 and 50 rules, run
   through Git Bash the way Claude Code on Windows runs a hook. Quote the numbers and the machine load.
2. **Live rows 1 and 2** (the earlier brief's step 5, unchanged): on a real slot-6 Director session on
   Windows against a Gateway OF ITS OWN built from your worktree - never the production Gateway.
   `scripts\restart-qa-rig.ps1` is the working reference for an isolated Windows root with its own Gateway.
   The positive row AND both negative controls, each negative proven by a `no_match` line in that session's
   evaluation log tied to the call id. Read each evaluation log BEFORE closing its session.
3. **The September usage-limit screen rule, live, on Windows `cmd`** - exactly as September's
   `docs/missions/session-rules-2026-09-02/demonstration-rig.md` and `qa-report.md` ran it: a RawCli
   session running `cmd`, the September rule body unchanged, the usage-limit notice printed on its screen,
   and a firing recorded on your Gateway. The model call needs the DevThrottle inference key. THIS machine's
   secret store already holds `devthrottle-api-key`: supply it to your scratch Gateway with `cc-secrets run`
   (read `cc-secrets run --help` for the exact form) - never print it, never write it to a file, never put
   it in a commit or a log you commit. The rule is created in dry run, so it types nothing; quote the
   firing's decision, reason and grounding from the Gateway. If the key is refused, say so and stop that
   step.

4. **Windows WITHOUT Git Bash** (finding 1 of `reviews/phase-1-whole-review.md`, fixed by registering the
   evaluator in exec form): start a Director Claude Code session with `CLAUDE_CODE_GIT_BASH_PATH` pointed at a
   path that does not exist (or Git Bash otherwise hidden from Claude Code - say which), confirm the session
   uses the `PowerShell` shell tool, run a command, and prove the hook RAN and did not block: a `no_match` or
   `would_deny` line in the evaluation log tied to that call id, and the command's output on screen. Do this
   with the build that carries the exec-form fix; the Tech Lead will name it.

Anything that fails on Windows is a finding: fix it on your branch with a test written first, and say what
you changed.

## Environment and rules

- Commit with `git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com commit`. Never any
  assistant, model or vendor name, and no Co-Authored-By or Generated-with line, anywhere. Grep before
  every commit.
- Plain English, no abbreviations. Nothing hidden: no sub-agents. Do not open a pull request, do not merge.
- Write what you proved, with commands, numbers, rule ids, call ids, evaluation log lines and the commit,
  into `docs/missions/session-rules-hooks-2026-10-18/phase-1-windows-rows-proof.md` on your branch,
  including a NOT PROVEN list. Push your branch. Shut down every process you started, cleanly.
- When done, report in ONE line: `cc-devthrottle session handback "<branch> <commit> done: <summary>"`.
  If blocked on something only the owner can do, say so in one line with `cc-devthrottle session raise`.

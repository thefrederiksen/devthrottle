# Brief - Developer W, phase 1 on Windows

You are a Developer on the Session Rules mission, phase 1, on the Windows machine SOREN_NORTH. Your seat:
`cc-devthrottle skill get devthrottle-method` (Developer section) - follow it. You report to the phase 1
Tech Lead (the session that opened you).

## First, before anything else

You were opened in the SHARED checkout `D:/ReposFred/devthrottle`. Do not work in it, do not pull it, do
not switch its branch - other sessions use it. Cut your own worktree and work only there:

    git -C D:/ReposFred/devthrottle fetch origin
    git -C D:/ReposFred/devthrottle worktree add D:/ReposFred/devthrottle-session-rules-p1-win -b mission/session-rules-hooks-p1-win origin/mission/session-rules-hooks-p1

Then read, in that worktree: `docs/missions/session-rules-hooks-2026-10-18/phase-1-plan.md` (rulings
P1-P16, wire shapes W1-W5), `mission.md` (section 7, the check), `phase-1-hook-proof.md` (what is NOT
proven on Windows - that list is your task) and `CLAUDE.md` (especially rules 0, 0b and 5a: never kill a
process you did not start, launch a test Director only through Windows Task Scheduler on slot 6 or higher,
stop it with its own named shutdown signal).

## The task

Prove phase 1 on Windows. In order:

1. **Build.** Publish the Director with `scripts\local-build-avalonia.ps1 -Slot <6 or higher> -OutputDir
   "<worktree>\scripts\local-build"`. It embeds `cc-rule-hook` natively, which needs the Visual Studio C++
   build tools. If they are missing, say so and stop that step - do NOT build without the hook and call it
   proven.
2. **Tests on Windows:** `dotnet test src\CcDirector.RuleHook.Tests`, `dotnet test src\CcDirector.Core.UnitTests
   --filter "FullyQualifiedName~Hook|FullyQualifiedName~SessionPointerDrop|FullyQualifiedName~Rules"`, and
   `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"`. Quote counts and exit codes.
3. **The installed Claude Code on Windows:** capture a real PreToolUse event for the shell tool (and for the
   `PowerShell` tool if this Claude Code uses it - check), into a test fixture beside the Linux one. Prove
   whether the PowerShell tool's input field is `command`. Prove a hook that exits 0 and prints nothing
   leaves the command alone, and how Claude Code runs a hook command string on Windows.
4. **Timing:** run `scripts\measure-rule-hook.ps1` with the natively built evaluator - median and 95th
   percentile, no rules file, 0, 1 and 50 rules, and the baseline. Quote the numbers and the machine load.
5. **Live check, rows 1 and 2 of mission section 7**, on a real Director session on Windows, against a
   Gateway OF ITS OWN built from your worktree - never the production Gateway. `scripts\restart-qa-rig.ps1`
   is the working reference for an isolated Windows root with its own Gateway. Wait for the Tech Lead's
   message that the Gateway piece is merged into `mission/session-rules-hooks-p1` before this step, then
   `git pull --rebase` your branch. Then: a hook rule with `appliesTo` `repo` for repo A matching `echo
   RULE-PROBE`; a session in repo A runs it - a `would_deny` firing on the Gateway; the same command in repo
   B - no firing, proven by a `no_match` line in that session's evaluation log; a rule with `appliesTo`
   `machine` naming a machine that is not this one - no firing, proven the same way. Repo A and repo B are
   two scratch git repositories with different `origin` remotes. Read each evaluation log BEFORE closing its
   session (the log is deleted with the session).

Steps 1-4 do not wait on anything. Anything that fails on Windows is a finding: fix it on your branch with a
test written first, and say what you changed.

## Environment and rules

- Commit with `git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com commit`. Never any assistant,
  model or vendor name, and no Co-Authored-By or Generated-with line, anywhere.
- Plain English, no abbreviations. Nothing hidden: no sub-agents. Do not open a pull request, do not merge.
- Write what you proved, with commands, counts, exit codes and the commit, into
  `docs/missions/session-rules-hooks-2026-10-18/phase-1-windows-proof.md` on your branch, including a
  NOT PROVEN list. Push your branch. Shut down every process you started, cleanly.
- When done, report in ONE line: `cc-devthrottle session handback "<branch> <commit> done: <summary>"`.
  If blocked on something only the owner can do, say so in one line with `cc-devthrottle session raise`.

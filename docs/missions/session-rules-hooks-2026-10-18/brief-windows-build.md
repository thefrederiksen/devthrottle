# Brief - Developer, the Windows native build on GitHub's runner

You are a Developer on the Session Rules mission, phase 1 follow-on. Your seat:
`cc-devthrottle skill get devthrottle-method` (Developer section) - follow it. You report to the phase 1
Tech Lead (the session that opened you).

## Why

No Windows machine we can reach has the Visual Studio C++ build tools, and installing them needs a person
at the screen. `cc-rule-hook` is Native AOT, and the Director embeds it when it is published with a
`win-` runtime identifier (the `EmbedRuleHook` target in `src/CcDirector.Avalonia/CcDirector.Avalonia.csproj`).
GitHub's `windows-latest` runner has the tools. So the Windows build is made there, downloaded onto
SOREN_NORTH, and a Windows Developer measures and checks with it.

## The task

You are in the worktree `/home/soren/ReposFred/devthrottle-session-rules-p1-winbuild`, on the branch
`mission/session-rules-hooks-p1-windows`, cut from `origin/mission/session-rules-hooks-p1`. Work only
there.

1. Add ONE workflow, `.github/workflows/session-rules-windows-build.yml`, named
   "Session Rules - Windows native build (one-off)". It:
   - runs on `push` to the branch `mission/session-rules-hooks-p1-windows` only, and on
     `workflow_dispatch` (a dispatch needs the file on main, so the push trigger is the one that works);
   - has `permissions: contents: read` and nothing more;
   - one job on `windows-latest`: checkout, setup .NET 10 exactly as `release.yml` does, then
     (a) publish `src/CcDirector.RuleHook` for `win-x64` natively (Native AOT), and
     (b) publish the Director exactly as `release.yml`'s "Publish for Windows x64" step does, EXCEPT
     `-p:UpdaterEnabled=false` (a test build must not update itself into the owner's install);
   - CHECKS, and fails the job if not true: the native `cc-rule-hook.exe` exists, is a native image (not a
     managed .NET assembly), and runs: feed it a minimal PreToolUse event on standard input with no rules
     file environment set, and it exits 0 and prints nothing. And the Director's published output really
     carries the embedded hook (find out how the embedding is stored and check for it - a presence check,
     not an absence one);
   - prints the time each publish took and the SHA-256 of each file it uploads;
   - uploads two artifacts with `actions/upload-artifact@v6`, `if-no-files-found: error`, retention 14
     days: `cc-rule-hook-win-x64` (the exe) and `cc-director-win-x64-session-rules` (the Director exe);
   - publishes NOTHING else: no release, no tag, no signing, no deploy, no secrets used, no push.
   - A header comment saying it is a one-off for Session Rules phase 1, and that it is disabled with
     `gh workflow disable` when phase 1's Windows rows are done (the repository instructions, section 5a).
2. Commit and push the branch. Watch the run with `gh run list --branch mission/session-rules-hooks-p1-windows`
   and `gh run watch`. If it fails, read the log, fix, push again. Never touch any other workflow.
3. When green: report to me the run id, the two artifact names, the SHA-256 of each, the publish times,
   and what the check of the embedded hook looked for.

## Rules

- Commit with `git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com commit`.
- NEVER put any assistant, model or vendor name, nor a Co-Authored-By or Generated-with line, in commits,
  comments or docs. Grep before every commit.
- Plain English, no abbreviations, in comments and docs.
- No pull request. Do not push to any branch but yours. Do not merge anything.
- Report with `cc-devthrottle session handback "..."` when done, or ask me with
  `cc-devthrottle session raise "..."` if blocked.

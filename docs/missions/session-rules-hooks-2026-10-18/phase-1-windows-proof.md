# Phase 1 on Windows - what was proven on SOREN_NORTH

Written 2026-10-18 by "Session Rules - Developer - phase 1 Windows proof", branch
`mission/session-rules-hooks-p1-win`, cut from `mission/session-rules-hooks-p1` at `32ef7e93f`. Self-testimony:
the Tech Lead re-runs it and a Reviewer on another agent reads the change.

Machine: SOREN_NORTH, Windows 11 Enterprise 10.0.26200, 24 logical processors, 64 GB. .NET SDK 10.0.303.
Claude Code 2.1.302. Git for Windows bash 5.2.37.

## Status by step of the brief

| Step | Status |
|---|---|
| 1. Native build of the Director with the hook embedded | BLOCKED - no C++ build tools on this machine |
| 2. Tests on Windows | PROVEN |
| 3. The installed Claude Code on Windows | PROVEN |
| 4. Timing with the natively built evaluator | BLOCKED on step 1 (script fixed and smoke-tested) |
| 5. Live check, rows 1 and 2 | BLOCKED on step 1 (the Gateway piece has merged) |

## 1. Native build - blocked

SOREN_NORTH has no Visual Studio and no Visual Studio Build Tools (no `VC\Tools\MSVC`, no `link.exe`, no
`vswhere.exe`; only leftover package caches under `C:\ProgramData\Microsoft\VisualStudio\Packages`). The
Native AOT publish of the evaluator alone fails:

    dotnet publish src/CcDirector.RuleHook -c Release -r win-x64
    error : Platform linker not found. Ensure you have all the required prerequisites documented at
    https://aka.ms/nativeaot-prerequisites, in particular the Desktop Development for C++ workload in Visual Studio.
    exit 1

Per the brief, no Director was built without the hook and nothing here is claimed for the native binary.
Raised to the Tech Lead for the owner: install Visual Studio Build Tools 2022 with the "Desktop development
with C++" workload (needs elevation).

## 2. Tests on Windows - proven

| Command | Result |
|---|---|
| `dotnet test src\CcDirector.RuleHook.Tests` (before this branch's change) | 63 passed, 0 failed, exit 0 |
| `dotnet test src\CcDirector.RuleHook.Tests` (with this branch's two new cases) | 65 passed, 0 failed, exit 0 |
| `dotnet test src\CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop\|FullyQualifiedName~Rules"` | 162 passed, 0 failed, exit 0 |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` | 494 passed, 0 failed, exit 0 |

Those counts are the mission branch before the Gateway piece (Developer A) merged. Re-run on Windows after
rebasing onto `origin/mission/session-rules-hooks-p1` at `4f21e04a7` (Gateway, Director and hook all merged):

| Command | Result |
|---|---|
| `dotnet test src\CcDirector.RuleHook.Tests` | 70 passed, 0 failed, exit 0 |
| `dotnet test src\CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop\|FullyQualifiedName~Rules"` | 163 passed, 0 failed, exit 0 |
| `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` | 562 passed, 0 failed, exit 0 |

## 2a. The repository gate on Windows - green except one failure main has already fixed

Asked for by the Tech Lead while the build tools are missing. All runs on this branch, Release.

**Run 1, at `6a86311d8`:** `.\scripts\test-local.ps1 -Configuration Release`, exit 1. The script chose to
run the parked `CcDirector.Gateway.UnitTests` and `CcDirector.Core.Tests` whole. Four failures:

| Test | What it said | Whose |
|---|---|---|
| `CcStorageProtectedPathsTests.every_public_static_path_member_is_either_protected_or_explicitly_not_protected` (Core.Tests) | `CcStorage.SessionRules` is in neither list | the mission's (Director piece) |
| `CatchLogLineScanTests.EveryLogLineInACatch_IsReportedOrSaysWhyNot` (Core.UnitTests) | `ClaudeHookInstaller.cs` lines 332 and 350 are in neither house form | the mission's (hook piece) |
| `DefinitionFormatDocumentTests.Document_NamesEveryKeyTheSchemasTake` (Gateway.UnitTests) | `docs/factories/definition-format.md` does not name nine keys | NOT the mission's: the mission branch touches neither file; main fixed it in #4218 after the branch was cut from `7a365544b`; passes on `origin/main` at `3e47021cb` (1 passed, exit 0) |
| `ApiModeSessionHostTests.TurnStartBound_PiAcknowledgesAndNeverStarts_...` (Core.UnitTests) | one extra event in a sequence | passed on the re-run (FLAKY by the script's rule); not in any code this mission touches |

`-RerunFailed` on that run folder, exit 1: the first three failed again; the fourth passed.

**Fixed on this branch, commit `64c41f86e`:** `SessionRules` is classified with `SessionPreambles`, as data the
reclaim tool reports and never offers (the same life as the preamble file, W2); a failed evaluator install
now logs `InstallRuleHook FAILED:`, so it reaches the Gateway as an error, and the leftover-temporary-file
line is marked `// not-an-error` with its reason. Both tests were watched red in run 1 and are green after:
`Core.UnitTests --filter CatchLogLineScanTests|Hook` 92 passed, `Core.Tests --filter
CcStorageProtectedPathsTests|ClaudeHookInstaller` 9 passed, exit 0.

**Run 2, at `64c41f86e`:** exit 1. Every suite `outcome=Completed` except `CcDirector.Gateway.UnitTests`, whose one
failure is `Document_NamesEveryKeyTheSchemasTake` (main's, above); 14,537 of 14,538 passed. Core.UnitTests
2,520, Core.Tests 4,540, RuleHook 70, Avalonia 1,093, setup engine 924, and the rest, all passed. Its COVERAGE
GAP line named `CcDirector.Gateway.Tests`, so:

**Run 3, at `64c41f86e`:** `.\scripts\test-local.ps1 -Configuration Release -Gateway`, exit 0, RESULT GREEN, tree
clean. Shards: 412, 480, 418 of 421 (3 skipped), 469 - all passed. The run folder is deleted after a green run,
so which three were skipped is not recorded.

Verdict: green for this mission's code on Windows; the one remaining red clears when the mission branch takes
main.

## 3. The installed Claude Code on Windows - proven

Method: a scratch git repository, `claude -p` with `--settings` naming a settings file whose PreToolUse hook
(matcher `^(Bash|PowerShell)$`) ran two commands: a Python script that saved standard input to a file and
exited 0 printing nothing, and `echo shell0=$0 comspec=%COMSPEC% bash=$BASH_VERSION psv=$PSVersionTable >>
shellprobe.txt`. The prompt asked for `echo RULE-PROBE` with the Bash tool and `Write-Output RULE-PROBE-PS`
with the PowerShell tool.

- **This Claude Code has a PowerShell tool and uses it** when asked: `tool_name` `PowerShell`. No setting
  was needed to turn it on.
- **The PowerShell tool's input field is `command`**: `"tool_input": {"command": "Write-Output
  RULE-PROBE-PS", "description": ...}`. The call id is in `tool_use_id`, as for Bash. The event's shape is
  otherwise identical to the Linux one.
- **A hook that exits 0 and prints nothing leaves the command alone**: both commands ran - the session
  transcript holds `"stdout":"RULE-PROBE"` and `"stdout":"RULE-PROBE-PS"`.
- **Claude Code on Windows runs a hook command string through Git Bash, not cmd.exe**: every probe line
  reads `shell0=/usr/bin/bash comspec=%COMSPEC% bash=5.2.37(1)-release psv=` - `$0` and `$BASH_VERSION`
  expanded, `%COMSPEC%` did not. Re-run from PowerShell with `SHELL` removed from the environment (a
  Director session does not inherit Git Bash's `SHELL`): same line. So the quoted absolute path the
  installer registers, `"C:\...\cc-rule-hook.exe"`, is read by bash; backslashes before letters are kept
  inside double quotes, so the path reaches Windows unchanged. That the native evaluator starts from that
  string is shown only with the non-native build (below), not with the native one.

Fixtures, captured unchanged except for indentation:
`src/CcDirector.RuleHook.Tests/Fixtures/claude-code-2.1.302-windows-pretooluse-bash.json` and
`...-windows-pretooluse-powershell.json`. New test `HookRunnerTests.Run_CapturedWindowsEvent_DecidesOnItsCommand`
feeds each one in as captured and asserts `would_deny`, `tool` `shell`, the real `agentTool`, the command as
`subject` and the call id at the end of the log line. Watched failing: with the evaluator's tool check
narrowed to `Bash` only, the PowerShell case failed (1 failed, 1 passed); restored, both pass.

## 4. Timing - blocked on step 1; the script was wrong for Windows and is fixed

**Finding:** `scripts/measure-rule-hook.ps1` ran the hook command through `cmd.exe /d /s /c` on Windows,
with `rem` as the baseline. Claude Code uses Git Bash (step 3), so it measured a shell Claude Code never
uses. Changed on this branch:
- On Windows the command string goes to Git Bash with `-c`, found the way Claude Code finds it:
  `CLAUDE_CODE_GIT_BASH_PATH` when set, otherwise `<Git>\bin\bash.exe` of the Git for Windows that provides
  `git` on the PATH (located through `git --exec-path`, because `git.exe` may be on the PATH from `cmd`,
  `bin` or `mingw64\bin`). Neither present is an error that says what to install.
- The Windows baseline is `/usr/bin/true`, a real program, like every hook row.
- On Windows the event is the Windows Bash fixture.

The script needs PowerShell 7 (`#Requires -Version 7`); this machine has only Windows PowerShell 5.1, so
PowerShell 7.6.7 was installed as a local tool into the session's scratch folder (`dotnet tool install
PowerShell --tool-path ...`), not system-wide.

**Smoke run, NOT the measurement:** a framework-dependent, just-in-time build of the evaluator (`dotnet
publish ... -p:PublishAot=false --self-contained false`), 30 runs a row, processor load 60 percent at the
time. It proves the script runs end to end through Git Bash and that the evaluator, started from the quoted
path through Git Bash, decides on the real Windows event: 64 decision files written of 64 expected, and each
evaluation log's last line reads `no_rules_file`, `no_match`, `would_deny`, `would_deny` with the call id.

| Row (non-native build) | Median | 95th percentile |
|---|---|---|
| baseline, Git Bash running `/usr/bin/true` | 59.93 ms | 77.86 ms |
| outside a Director session | 125.34 ms | 176.36 ms |
| rules file not there | 166.96 ms | 278.30 ms |
| 0 rules | 328.69 ms | 741.96 ms |
| 1 rule | 407.83 ms | 751.27 ms |
| 50 rules | 258.01 ms | 584.87 ms |

The hook rows are the .NET runtime starting and compiling, which Native AOT exists to remove; they say
nothing about the shipped evaluator. The baseline row does stand on its own: **on Windows, Git Bash alone
costs about 60 ms per hook call before the evaluator starts**, against 2.3 ms for `/bin/sh` on Linux. That
cost is Claude Code's way of running any hook on Windows, not ours to remove, but it is part of what the
owner pays per shell command.

## 5. Live check - not run

Waits on the native build: a Director built without the C++ tools carries no hook and registers no
PreToolUse hook. The Gateway piece merged at 17:43 UTC and this branch is rebased onto it. At 18:15 UTC,
after a 30-minute wait, the build tools were still not installed.

## NOT PROVEN on Windows

- The native Windows build of `cc-rule-hook` and of the Director embedding it.
- The native evaluator's timing (step 4's real numbers).
- That the installer, inside a real Director on Windows, writes `cc-rule-hook.exe` into `claude-hooks` and
  registers it, and that a refused replace of a running hook is retried on the next launch.
- Rows 1 and 2 of mission section 7 on a real Windows Director session: the `would_deny` firing on the
  Gateway, and both negative controls by `no_match` lines.
- Which three `CcDirector.Gateway.Tests` were skipped in run 3, and why.
- Behaviour on a Windows machine without Git for Windows (Claude Code itself requires it on Windows).

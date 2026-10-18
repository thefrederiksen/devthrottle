# Phase 1, Developer B (Hook) - what was built and what proves it

Written 2026-10-18 by "Session Rules - Developer - phase 1 hook evaluator", branch
`mission/session-rules-hooks-p1-hook`. Self-testimony: the Tech Lead re-runs the check and a Reviewer
on another agent reads the change. Questions asked on the way, and the rulings they produced (P12 to
P16), are in `phase-1-hook-questions.md`.

## What was built

| Piece | Where | Rulings |
|---|---|---|
| `cc-rule-hook`, the evaluator: reads the PreToolUse event and the rules file, decides, drops the decision, logs every evaluation, always exits 0 and prints nothing | `src/CcDirector.RuleHook` (no project references, source-generated JSON, Native AOT) | P1, P2, P3, P10, P11, P12, P13, P14, W2, W3, W4 |
| Its tests | `src/CcDirector.RuleHook.Tests` (in the solution and in `scripts/test-local.ps1`'s default list) | |
| The PreToolUse registration for `^(Bash|PowerShell)$`, in the installer's one settings builder beside the SessionStart hooks | `ClaudeHookInstaller.BuildSettings` | P9, P12 |
| The Director carries the evaluator and the installer writes it beside the hook scripts: compare before write, executable bit, atomic replace, a refused replace logged and retried, no registration when the build carries none | `ClaudeHookInstaller.InstallRuleHook`, `RuleHookBinary`, the `EmbedRuleHook` target in `CcDirector.Avalonia.csproj` | P16 |
| Both local build scripts: embedded by default, an explicit opt-out, and a presence check | `scripts/local-build-linux.sh --no-rule-hook`, `scripts/local-build-avalonia.ps1 -NoRuleHook` | P16 |
| Timing scripts | `scripts/measure-rule-hook.sh`, `scripts/measure-rule-hook.ps1` | P9 |

The release workflow needed NO change: its Windows and Linux Director jobs publish with a runtime
identifier, which runs the embedding target (the Windows runner has the C++ build tools, the Ubuntu
runner has clang). macOS is deliberately not given a hook yet (phase 4).

## Publish mode: Native AOT

Chosen because the evaluator runs before every shell command, so start-up is the cost that matters, and
Native AOT removes the .NET runtime start. It built with the existing toolchain on this machine with no
extra setting: the .NET 10 native compiler uses clang when present and falls back to gcc
(`Microsoft.NETCore.Native.Unix.targets`), and the binary's `.comment` section names GCC 13.3.0. Result:
a 2.7 MB executable that needs only libc and libm. A build without the native toolchain FAILS and says
so; `-p:EmbedRuleHook=false` builds a Director without the hook on purpose (the installer then logs
"RULE HOOK MISSING" and registers no PreToolUse hook).

## Verified against the installed Claude Code (2.1.302, Linux)

- A real PreToolUse event, captured with a throwaway hook from `claude -p`, is the test fixture
  `src/CcDirector.RuleHook.Tests/Fixtures/claude-code-2.1.302-pretooluse-bash.json`. Shell tool `Bash`,
  command in `tool_input.command`, call id in `tool_use_id`. The binary lists its shell tools as
  `["Bash","PowerShell"]`.
- Exit 0 with nothing on standard output leaves the command alone: the capture hook did exactly that and
  the command ran and printed `RULE-PROBE`.
- A live run with the NATIVE hook registered the way the installer registers it (quoted absolute path, in
  a folder with a space in its name), a `repo` rule matching `echo RULE-PROBE`, and two commands:
  both ran (`RULE-PROBE`, `harmless-control`); the evaluation log has one line each -
  `would_deny ... toolu_01AxkHAA...` and `no_match ... toolu_01FZqn2p...`; one decision file in the W3
  shape, `would_deny`, deciding rule `probe-rule`. The negative control is a `no_match` line, a presence.

## Tests

| Command | Result |
|---|---|
| `~/.dotnet/dotnet test src/CcDirector.RuleHook.Tests` | 62 passed, 0 failed, exit 0 |
| `~/.dotnet/dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop"` | 60 passed, 0 failed, exit 0 (includes `HookScriptContractTests` and the 15 new `ClaudeRuleHookRegistrationTests`) |
| `... Core.UnitTests --filter "FullyQualifiedName~FactoryKitLaunch\|FullyQualifiedName~FactorySeat"` | 44 passed, exit 0 (a seat's own settings still equal the shared file plus its permissions) |
| `... Core.UnitTests --filter "FullyQualifiedName~Architecture"` | 17 passed, exit 0 (includes the gate's machine-dependence guard, now reading the new suite) |
| `... Core.Tests --filter "FullyQualifiedName~ClaudeHookInstaller\|FullyQualifiedName~ReleaseWorkflowContract"` | 12 passed, exit 0 |
| `... Gateway.UnitTests --filter "FullyQualifiedName~NoListenerDependencyGuard"` | 4 passed, exit 0 (needs node on the path for the build) |

Each test was written before its code and seen to fail, except the resolver's, which was written first;
for those the code was broken on purpose instead and the tests caught every break: deny-wins flipped to
allow-wins (1 failed), lowest rank instead of highest (7 failed), case-insensitive match (1 failed),
"overridden" counting agreeing rules (1 failed). The installer was broken the same way: registering
without the file (2 failed), matcher `Bash` only (2 failed), no compare before write (1 failed), a
refused replace dropping the old hook (1 failed), no executable bit (1 failed).

## Timing (Linux, devlinux, 32 processors, 300 runs a row)

`scripts/measure-rule-hook.sh`, the exact registered command through `/bin/sh -c` - which is how Claude
Code 2.1.302 runs a hook command string, checked live - with the real event on standard input. Re-run after
review (finding 3, note N3): the baseline now starts a real program (`/bin/sh -c /bin/true`) as every hook
row does, and the no-session row runs with both session variables removed. The machine was busy - load
average 10 to 16 during the run - so every row carries contention; the added time is the row minus the
baseline.

| Row | Median | 95th percentile | Added (median) |
|---|---|---|---|
| baseline, `/bin/sh -c /bin/true` | 2.33 ms | 2.94 ms | - |
| outside a Director session | 6.20 ms | 7.97 ms | 3.9 ms |
| rules file not there | 6.54 ms | 8.22 ms | 4.2 ms |
| 0 rules | 6.17 ms | 8.58 ms | 3.8 ms |
| 1 rule, matching (decision written) | 7.59 ms | 9.18 ms | 5.3 ms |
| 50 rules, last matching (decision written) | 7.18 ms | 9.46 ms | 4.9 ms |

Most of the added time is the process starting (the "outside a session" row does nothing else). Fifty
rules cost no more than one. One defect found by measuring and fixed: the first version used the
cryptographic random generator for the decision id's eight characters, which loads OpenSSL on Linux and
made every matched command about 6 ms slower (13.8 ms median); a source guard now forbids it. Switching
off the background garbage collector was tried and changed nothing measurable.

## NOT proven

- **Windows, all of it.** No Windows machine here. Not run: the native build on Windows, the hook under
  Claude Code on Windows, the `PowerShell` tool's input field being `command` (if not, the evaluation log
  says `error: the PowerShell event has no command`), how Claude Code on Windows runs a hook command
  string, a replace refused by a running hook (tested only through an injected failure), and
  `scripts/measure-rule-hook.ps1` on Windows (it ran on Linux under PowerShell 7: same shape of numbers).
- **The Director end to end.** The embedded hook is proven present inside the published single-file
  Director (`local-build-linux.sh --slot 9`, its bytes found inside the bundle), and the installer is
  proven by unit tests, but no Director session was launched: that is the live rig (Developer D). The
  rules file and the two environment variables come from Developer C's work and are not on this branch.
- **The release workflow** was not run; that it needs no change rests on the runners having the native
  toolchains.

## After review (2026-10-18)

The review is `reviews/phase-1-hook-review.md` on `mission/session-rules-hooks-p1`; every finding and note
is answered in `reviews/phase-1-hook-answers.md` on this branch. Tables above are updated; the test counts
after the fixes are in the answers file.

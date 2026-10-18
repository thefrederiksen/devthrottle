# Phase 1, Developer B (the hook) - answers to the review

Written 2026-10-18 by "Session Rules - Developer - phase 1 hook evaluator", on
`mission/session-rules-hooks-p1-hook`, answering `reviews/phase-1-hook-review.md` (on
`mission/session-rules-hooks-p1`) and the Tech Lead's calls at its end. Every finding and note: accepted
or declined, with the reason and the proof.

## Finding 1 - an evaluator failure cost the session ALL its hooks. ACCEPTED, fixed.

`InstallRuleHook` can no longer throw. The compare-before-write read is inside the try, the catch takes
any exception, and deleting the temporary file is its own guarded step whose failure is only logged. Every
failure now ends in "register the evaluator already on disk" or "register none", and the log line names
the exception type. The SessionStart hooks are always written.

Tests, written first and seen failing (2 of the cases below failed before the fix; the I/O and access
cases already passed under the old narrow catch):
- `EnsureInstalled_ExistingEvaluatorCannotBeRead_StillWritesTheSettingsWithTheSessionStartHooks` - the
  reviewer's reproduction: an existing evaluator with no read permission. Before: `EnsureInstalled`
  returned null. After: the settings file is written with the four SessionStart hooks.
- `EnsureInstalled_EvaluatorStepThrowsAnything_StillWritesTheSettingsWithTheSessionStartHooks` for
  `IOException`, `UnauthorizedAccessException`, `InvalidOperationException` and `NotSupportedException`:
  settings written, no PreToolUse, no temporary file left.

What this does NOT do: with an unreadable evaluator on disk, that kept file stays registered (P16's "the
evaluator already there stays registered"). On Windows the realistic case, a scanner's sharing lock, leaves
a runnable file. On Linux a file with its execute permission removed would make Claude Code report a hook
error on each shell command until the next launch replaces it. Not reachable without someone changing the
file's mode by hand.

## Finding 2 - a factory seat launched first on a fresh instance got no rule hook. ACCEPTED, fixed.

Mechanism: `WriteSessionSettings` now installs the evaluator itself through the same `InstallRuleHook`
(compare before write), so the shared install that follows is a no-op. Its testable overload takes the
evaluator's bytes instead of a "carried" flag. `RuleHookBinary.Carried()` is read once more per seat launch
(2.7 MB from the Director's own resources), which is acceptable at session-launch frequency.

Test, in the real launch order, written first and seen failing:
`WriteSessionSettings_FreshInstanceBeforeTheSharedInstall_StillWritesAndRegistersTheRuleHook` (Windows and
Linux shapes): a fresh folder, the seat's file written first with the evaluator carried. The evaluator is
on disk and PreToolUse is present. The shared install that follows registers the identical block.

## Finding 3 - the Linux timing script measured and wrote into the operator's session. ACCEPTED, fixed.

`run_hook` now runs under `env -u CC_SESSION_RULES_FILE -u CC_SESSION_DECISION_DIR`. The script also
records the operator's own session evaluation log and decision drop before the run, and fails if either
grew by more than a handful (other commands of that session may add a few lines; the measurement would
add hundreds).

Proof:
- **The reviewer's case.** Both variables set to a stand-in session with a rule matching
  `echo RULE-PROBE`, `--runs 3`. Result: exit 0, and the stand-in session's log and drop are untouched
  (0 lines and 0 files, before and after).
- **The guard fails when it should.** The same run with `env -u` removed from a copy of the script:
  "this session's own evaluation log: 0 lines before, 32 after; its decision drop: 0 files before, 32
  after", then "ERROR: the measurement wrote into this session's own evaluation log or decision drop.",
  exit 1.

## N1 - error messages are resource keys. Tech Lead: keep. ACCEPTED, no change.

`UseSystemResourceKeys` stays. The log is local and read by engineers.

## N2 - the build scripts' presence check was a substring search. ACCEPTED, fixed differently.

The check moved into the build itself, so both scripts and the release workflow get it. The new target
`VerifyRuleHookEmbedded`, in `CcDirector.Avalonia.csproj`, runs after compilation on every embedding build.
It refuses a build unless:
- an embedded resource with the logical name `cc-rule-hook` was handed to the compiler, and
- the compiled assembly's managed-resources block (the one the CLI header points at, found by walking the
  PE headers) holds a resource whose bytes are EXACTLY the evaluator's.

A string literal cannot pass for that. The two scripts' own substring checks are removed; they now say
the build verified it.

Why not the manifest resource NAME read through `System.Reflection.Metadata`, as the Tech Lead's call
said: an inline build task compiles against the portable standard library only. Referencing the .NET 10
metadata assembly failed to compile (`System.Runtime` and then predefined types missing), and the
portable build of that package exists here only by chance in the package cache, so it would break on a
clean runner. Matching the exact bytes is the stronger check, and the name is checked on the compiler's
input.

Proof:
- **Passes on the real build.** `dotnet publish` of the Director as `local-build-linux.sh` runs it
  printed "Session Rules hook: embedded, 2704056 bytes, verified in the assembly's managed resources".
- **Fails without the hook.** The same task, run from a scratch project against a Director assembly
  built with `-p:EmbedRuleHook=false`, failed: "does not carry the Session Rules hook: no managed resource
  holds the 2704056 bytes".
- **The script end to end.** `scripts/local-build-linux.sh --slot 9` passes.

## N3 - the baseline never started a program, and the shell claim was unproven. ACCEPTED, fixed.

- **Baseline.** It is now `/bin/sh -c /bin/true`.
- **The shell claim, checked live.** On Claude Code 2.1.302, a PreToolUse hook that recorded `$0` and its
  own command line showed `/bin/sh` and `/bin/sh -c <the command string>`, with the parent process being
  `claude`. The script's header now states that, with the date. Re-measured numbers are in
  `phase-1-hook-proof.md`; the added time is about 4 to 5 ms median.

## N4 - whitespace-only match text matches everything. Goes to Developer A. ACCEPTED as routed.

No change on this branch. The evaluator still refuses an empty string. A whitespace-only one is refused
at the write gate (P4, P7), per the Tech Lead's call.

## N5 - anchor the matcher. ACCEPTED, done, proven live.

The matcher is now `^(Bash|PowerShell)$`. One live run on Claude Code 2.1.302 with four PreToolUse hooks,
one shell command (`echo MATCHER-PROBE`):

| Matcher | Fired? |
|---|---|
| `^(Bash|PowerShell)$` | yes |
| `^(Bas)$` | no |
| `Bas` | no |

So the anchored form is honoured. And the old plain form `Bash|PowerShell` was already an exact match on
tool names (`Bas` alone does not fire on `Bash`), so the gap the reviewer raised could not occur on this
version. The anchored form keeps it closed if a later version starts reading a plain word as a substring.
Test updated: `EnsureInstalled_RuleHookCarried_WritesItBesideTheScriptsAndRegistersThatPathForBashAndPowerShell`
asserts the anchored matcher, and failed until the constant changed.

## The small one - a rules path that is a directory logged `no_rules_file`. ACCEPTED, fixed.

The hook now logs `error: the rules file path is a directory`, keeping `no_rules_file` for "not written
yet" (W4). Test `Run_RulesPathIsADirectory_LogsAnErrorNotNoRulesFile`, written first and seen failing.

## Check after the fixes

| Command | Result |
|---|---|
| `~/.dotnet/dotnet test src/CcDirector.RuleHook.Tests` | 63 passed, exit 0 |
| `~/.dotnet/dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop"` | 67 passed, exit 0 |
| `... Core.UnitTests --filter "FullyQualifiedName~FactoryKitLaunch\|FullyQualifiedName~FactorySeat"` | 44 passed, exit 0 |
| `... Core.UnitTests --filter "FullyQualifiedName~Architecture"` | 17 passed, exit 0 |
| `... Core.Tests --filter "FullyQualifiedName~ClaudeHookInstaller\|FullyQualifiedName~ReleaseWorkflowContract"` | 12 passed, exit 0 |
| `scripts/local-build-linux.sh --slot 9` | built, hook verified in the assembly, exit 0 |

Still not proven, unchanged from the proof file: everything on Windows, and a running Director.

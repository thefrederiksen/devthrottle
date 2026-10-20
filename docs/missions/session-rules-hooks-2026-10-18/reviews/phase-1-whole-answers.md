# Answers to the review of the whole of phase 1, and to the Tech Lead's calls on it

Developer seat "Session Rules - Developer - whole-phase review answers", 2026-10-20, on devlinux, in the
worktree `devthrottle-session-rules-p1-wholefix` on branch `mission/session-rules-hooks-p1-wholefix`, cut
from the pull request branch at `3fc39fcf5`. The code change is commit `bdcf94e39`; this file and the
evidence are the commit after it. The review is `phase-1-whole-review.md`; its last section, "The Tech
Lead's calls", is answered item by item below. All seven are accepted.

## Call 1 - Finding 1: register the evaluator in exec form. ACCEPTED, done.

**What changed.** `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs`, `BuildSettings`: the PreToolUse hook
is now `{ type: "command", command: "<absolute path to cc-rule-hook>", args: [], timeout: 10 }` - the bare
path with no quotes, an empty argument list, and no `shell` field, on every platform. With `args` present
the agent spawns `command` itself, with no shell on any platform, so the PowerShell parsing problem in the
finding cannot arise and there is no quoting left to survive. The comment that justified the quotes is gone
and the new one says why exec form is used.

**The stated platform comment, corrected.** The comment on the SessionStart hooks (the review's lines
252-254) said the agent runs a hook string "through Git Bash on Windows" as a fact of the platform. It now
says what is known: sh on Linux and macOS; on Windows, Git Bash when it is installed (what the Windows proof
measured) and PowerShell when it is not (the vendor's documentation). It also says why the SessionStart
commands are still safe in shell form: both begin with a bare program name (`powershell ...`, `/bin/sh ...`),
which is a valid command line in sh, Git Bash and PowerShell alike - the review made the same observation.

**The test, written first and watched failing.** `src/CcDirector.Core.UnitTests/Claude/ClaudeRuleHookRegistrationTests.cs`:

- New: `EnsureInstalled_RuleHookCarried_RegistersItInExecFormWithNoShell` (Linux and Windows forms). It
  reads the settings file as the agent reads it and requires the key `args` to be present as an empty
  array, the keys to be exactly `type, command, args, timeout`, `command` to equal the evaluator's
  absolute path with no quotes, and no `shell` key.
- The same exec-form check (`AssertExecForm`) replaces the old `"\"<path>\""` expectation in the four tests
  that asserted the quoted shell-form string, so every path that registers the hook - the shared file,
  a factory seat's own file on a fresh instance, and the "replace refused, keep the old evaluator" path -
  is held to the new shape.
- Before the change: **7 failed, 17 passed** of 24 (`dotnet test src/CcDirector.Core.UnitTests --filter
  FullyQualifiedName~ClaudeRuleHookRegistration`) - the two new cases and the five updated ones. After it:
  all pass (counts in "The re-run" below).

**The live proof that the installed agent honours `args`, as a presence.** Run with
`scripts/session-rules-rig.sh` on this branch at `bdcf94e39` (rig folder
`~/.local/state/session-rules-rig-wholefix`, its own Gateway on port 7932, a test Director on slot 6 with the
rig's own root and HOME), stood down with the rig's own `down`: the Director exited on its named signal, the
Gateway on terminate, the sign-in copy deleted, nothing left running, and `~/.local/bin/cc-devthrottle`
unchanged. Evidence in `evidence/exec-form-*`.

1. The registration the Director actually wrote for the session (`evidence/exec-form-registration.json`):
   `command` the bare path, `args: []`, `timeout: 10`.
2. A rule with `appliesTo` `repo` for `github.com/session-rules-rig/repo-a`, matching `echo RULE-PROBE`. A
   real Director session in repo A (`6f85106b`) ran `echo RULE-PROBE` then `echo NOMATCH-PROBE`. The
   evaluation log (`evidence/exec-form-6f85106b.eval.log`) has a `would_deny` line ending in the first
   call's id (`toolu_01QfnJFrPQDJEusXdQXefsmB`) and a `no_match` line ending in the second's
   (`toolu_01Siv2Zts5X944k6dxjg8Abr`) - the hook ran in exec form for both calls. The Gateway recorded a
   `would_deny` firing on the rule with subject `echo RULE-PROBE`, repo A and machine `devlinux`. Both
   commands still ran and printed their text, `is_error` false: dry run did not block.
3. **The version.** The Director's session ran the agent at **2.1.304** - the machine's agent had updated
   itself past the 2.1.302 the call names. So 2.1.302 was run directly as well (`claude -p` from
   `~/.local/share/claude/versions/2.1.302`, the same exec-form registration, the rig HOME, a copy of that
   session's rules file): a `would_deny` line ending in its call id and a decision file written
   (`evidence/exec-form-2.1.302.txt`). Both versions honour exec form.

Two things the rig needed on this run that it did not before, both in the rig HOME's own scratch config,
never the owner's: the agent at 2.1.304 asked to trust the repository folder and to accept bypass mode before
it would take a prompt. The first two spawned sessions sat on those screens; they were stopped through the
rig's own Gateway and replaced. The rig script is unchanged by this work; whoever runs it next on a current
agent will meet the same two screens.

**Not proven here, and where it is.** Windows, in both cases: a machine with Git Bash, and the case the
finding is about, Git Bash absent or `CLAUDE_CODE_GIT_BASH_PATH` pointed at nothing, with a `no_match` line
as the presence. As the call says, that row is folded into the Windows follow-on on SOREN_NORTH. Exec form
uses no shell by the vendor's documentation, and devlinux proves the agent reads `args`; neither is a run on
Windows.

## Call 2 - Observation 1, the Windows timing. ACCEPTED: pointing to it.

Measured in the Windows follow-on: `brief-windows.md`, task 4 - `scripts\measure-rule-hook.ps1` with the
natively built evaluator, median and 95th percentile, with no rules file and with 0, 1 and 50 rules, against
the baseline, written to `phase-1-windows-proof.md`. The owner sees that number before phase 4, as the
mission's section 7 asks. No change here.

## Call 3 - Observation 2, the policed session can edit its own rules file. ACCEPTED: recorded for phase 4.

Added to `mission.md` section 10 as open item 4, in plain words: what the agent can do, why it is harmless in
dry run, and the question phase 4 must answer before any deny is live. No code change.

## Call 4 - Observation 3, nothing bounds the rules file. ACCEPTED: recorded for phase 4.

Added to `mission.md` section 10 as open item 5, beside item 4: no limit on match-text length, match count
or rule count, the cost paid on every shell command, and a bound on the Gateway's write side before phase 4.
No code change.

## Call 5 - Observation 4, the release workflow was not run. ACCEPTED: pointing to it.

The one-off Windows build on the hosted runner did find a defect in the embedding: in `PublishRuleHook`
(`src/CcDirector.Avalonia/CcDirector.Avalonia.csproj`) the output folder was passed as `"<folder>\"`, and
on Windows the backslash before the closing quote escapes the quote, so the hook was published into the
wrong folder and the Director's publish failed with "The Session Rules hook was not produced". The fix is
commit `89b6a572c`, "fix(session rules): publish the embedded hook to the right folder on Windows", on
`mission/session-rules-hooks-p1-windows`, carried onto the pull request branch separately as the call says.
It is not on this branch, and this work does not touch that target.

## Call 6 - Observations 5, 6 and 7. ACCEPTED: noted, no change. Why each is safe in dry run:

- **5, a forwarded decision's session id is not checked against the roster:** a firing is only a record and
  changes no command, so a wrong session id on one misattributes a "would have denied" line and nothing more.
- **6, a non-refusal exception fails the whole post:** the Director keeps every decision file and retries
  with backoff, so the records arrive late or not at all, and no command waits on them in either case.
- **7, a session keeps its repository identity while it runs:** the worst outcome is that the old
  repository's rules are evaluated, which in dry run writes a wrong "would" record and blocks nothing.

## Call 7 - the two 4000-character literals. ACCEPTED: one shared constant, with no project reference.

**What changed.** The constant now lives once, in the evaluator's own source:
`src/CcDirector.RuleHook/HookWireLimits.cs`, `HookWireLimits.SubjectMaxLength = 4000`. The evaluator's
`HookRunner.SubjectLimit` is defined as it. The Gateway compiles that same file in -
`src/CcDirector.Gateway/CcDirector.Gateway.csproj`, `<Compile Include="..\CcDirector.RuleHook\HookWireLimits.cs"
Link="Rules\HookWireLimits.cs" />` - and `HookRuleWords.SubjectMaxLength` (which sizes the subject column
and the write gate's length check) is defined as it. The evaluator still references no project (ruling
P11): a linked source file is not a reference, and the repository already shares test sources this way.
The class is `internal`, so the two copies of it are never visible to the same compilation. The Director's
`PublishRuleHook` target lists `..\CcDirector.RuleHook\*.cs` as its inputs, so a change to the new file
rebuilds the embedded evaluator.

**The test, written first and watched failing.**
`src/CcDirector.Gateway.UnitTests/Rules/HookSubjectLimitSharedTests.cs`:

- `SubjectMaxLength_GatewayAssembly_CompilesTheEvaluatorsOwnLimitsFile` - the Gateway assembly contains
  `CcDirector.RuleHook.HookWireLimits`.
- `SubjectMaxLength_GatewayLimit_IsTheEvaluatorsConstant` - that type's `SubjectMaxLength` is a literal equal
  to `HookRuleWords.SubjectMaxLength`.
- Before the change: **2 failed, 0 passed** (the type did not exist in the Gateway). After it: both pass.

Why not only a test pinning the two numbers equal: that would catch a drift, but sharing the constant makes
the drift impossible to write, and the test above fails if the Gateway ever stops compiling the shared file
and goes back to a literal of its own.

## The re-run

devlinux, .NET 10.0.301 at `~/.dotnet/dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1`, at `bdcf94e39`:

| What | Result |
|---|---|
| `dotnet test src/CcDirector.RuleHook.Tests` | 70 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Core.UnitTests --filter 'FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop\|FullyQualifiedName~Rules'` | 165 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests -p:RunWorkspaceTypecheck=false --filter 'FullyQualifiedName~Rules\|FullyQualifiedName~SessionRuleKindsMigration\|FullyQualifiedName~GatewayHostBootSmoke\|FullyQualifiedName~MigrationChain\|FullyQualifiedName~SessionKeyGuard'` | 1,284 passed, 0 failed, exit 0, 52 s |
| `scripts/session-rules-rig.sh build` of this worktree | Director published with "Session Rules hook: embedded"; Gateway and tool built at `bdcf94e39` |

`-p:RunWorkspaceTypecheck=false` only skips the web workspace's type check, which needs Node.js and is not
what these tests cover; the Gateway's own code is built in full. The Core count is lower than the review's 191
because the review's filter added `CatchLogLineScan` and `Architecture`; this is the filter the brief named.
The Gateway count is lower than the review's 1,302 for the same reason (the review's filter took every
`Migration` test).

## Not done, and why

- **Windows**: no run from this seat; see call 1.
- **The full `test-local.ps1` gate**: not run; the three suites above are the ones this change reaches.
  `Gateway.Tests` was not run; a search of it finds no use of `HookRuleWords`, `ClaudeHookInstaller`,
  `HookRunner` or `SubjectMaxLength`.
- **No pull request and no merge**, as the brief says.

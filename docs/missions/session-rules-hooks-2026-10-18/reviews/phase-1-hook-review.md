# Phase 1, Developer B (the hook) - review

Written 2026-10-18 by the Reviewer seat "Session Rules - Reviewer - phase 1 hook evaluator", on a different
agent family from the Developer, from the checkout `devthrottle-session-rules-p1-review-hook` at `f9d653e70`
(`mission/session-rules-hooks-p1-hook`), diffed against `mission/session-rules-hooks-p1`. The contract read
against is `phase-1-plan.md` as it stands on the mission branch (rulings P1 to P16, wire shapes W1 to W5).
The Developer's own account, `phase-1-hook-proof.md`, was treated as self-testimony and re-checked.

**Verdict: three findings that must change before the branch merges, and five notes that need no change
or are the Tech Lead's call. The evaluator itself (P10, P3, P13, P1, W3, W4) held up against everything I
threw at it. The two defects that matter are in the installer and the launch order around it, and one is
in the Linux timing script.**

---

## Scope

**Read, in full:** `src/CcDirector.RuleHook/*` (Program, HookRunner, RuleResolver, RulesFile, EvaluationLog,
the project file), `src/CcDirector.RuleHook.Tests/*` and the captured fixture, `ClaudeHookInstaller.cs`,
`RuleHookBinary.cs`, `ClaudeRuleHookRegistrationTests.cs`, the `EmbedRuleHook` targets in
`CcDirector.Avalonia.csproj`, both local build scripts, both timing scripts, the `test-local.ps1` and
solution changes, the relevant parts of `SessionManager.cs` and `FactoryKitLaunch.cs` (the callers), the
release workflow's Director jobs, the mission's sections 6 and 7, and the Developer's proof and questions.

**Ran:**
- `dotnet test src/CcDirector.RuleHook.Tests`: 62 passed, 0 failed, exit 0.
- `dotnet test src/CcDirector.Core.UnitTests` filtered to Hook, SessionPointerDrop, FactoryKitLaunch,
  FactorySeat and Architecture: 120 passed, 0 failed, exit 0.
- Published the evaluator natively from this checkout (`-r linux-x64`, exit 0; links only libc and libm)
  and ran it black-box through sixteen adverse cases (listed under "Verified sound").
- A scratch console program against the built `CcDirector.Core` to reproduce findings 1 and 2.
- The installed Claude Code 2.1.302 with a deliberately slow PreToolUse hook and a one-second timeout, to
  see what a timed-out hook does to the command.
- `dotnet msbuild -getProperty:EmbedRuleHook` for six runtime-identifier combinations.
- `scripts/measure-rule-hook.sh` with the two session variables present in the environment (finding 3).
- Byte search of the Developer's built `cc-director.dll` for the embedded evaluator and its resource name.

**Could not reach:** anything on Windows; a running Director (so `RuleHookBinary.IsCarried()` on a real
published Director is still not proven by anyone, only argued from the bytes); the release workflow; the
Gateway and Director pieces this hook talks to (Developers A and C), which are not on this branch.

---

## Findings (must change)

### 1. A failure around the evaluator file costs the session ALL its hooks, not just the rule hook

**Where:** `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs` line 303 (`File.ReadAllBytes(path)` in the
compare-before-write, OUTSIDE the try), line 319 (`File.Delete(tmp)` inside the catch, which can throw
itself), and the catch filter at line 317, which catches only `IOException` and `UnauthorizedAccessException`.
Any exception that escapes `InstallRuleHook` lands in `EnsureInstalled`'s catch at lines 180 to 183, which
returns null - and `SessionManager.cs` lines 1164 to 1166 then launch the session with NO `--settings` at
all.

**What breaks:** the session starts without the SessionStart hooks that existed before this change: no
session-pointer tracking across `/clear` and compaction, no fleet preamble. Ruling P16 says a refused
replace is "never fatal"; this path is worse than fatal for the rule hook - it is fatal for the hooks that
were working yesterday. The new, optional component sits in the critical path of the mandatory one.

**Proof:** scratch program, Linux. An evaluator already on disk with its read permission removed (what a
previous build left behind, made unreadable), then `EnsureInstalled(dir, false, newBytes)`:

    A: unreadable evaluator on disk -> EnsureInstalled returned: NULL
    A: files in the hooks folder: report-session.sh, cc-rule-hook          (no hooks-settings.json)
    B: readable again -> EnsureInstalled returned: .../claude-hooks/hooks-settings.json

On Windows the realistic trigger is not permissions but a sharing violation: a virus scanner holding the
freshly written `cc-rule-hook.exe` open at the moment the next session launches makes `ReadAllBytes` at
line 303 throw, with the same result. The tests cover a refused REPLACE (lines 136 to 159 of the
registration tests) but no failure of the READ, and no test asserts that the settings file is still written
when the evaluator step fails in any way.

**Why it must change:** the shared settings file must be written whatever happens to the evaluator. Every
failure inside `InstallRuleHook` should resolve to "register the kept file" or "register none", never to an
exception that reaches `EnsureInstalled`'s catch. A test should hold an unreadable or locked existing
evaluator and assert the SessionStart hooks are still registered.

### 2. A factory seat with rules, launched first on a fresh instance, gets no rule hook - silently

**Where:** `ClaudeHookInstaller.WriteSessionSettings` line 220 registers the PreToolUse hook only when
`File.Exists(onDisk)`. `SessionManager.cs` line 814 (`PrepareFactoryKitLaunch`, which calls `PlanFor`, which
calls `WriteSessionSettings`) runs BEFORE line 1164 (`EnsureInstalled`, the only thing that writes the file).

**What breaks:** on an instance where no Claude Code session has launched yet (a new install, a new slot,
a wiped `claude-hooks` folder), the first session, if it is a seat with rules, writes its own settings file
with no PreToolUse block, and that is the ONE `--settings` it is given. Every shell command of that seat for
its whole life is never evaluated. Nothing says so: the evaluator never runs, so there is no evaluation-log
line, and the Director will still have written the rules file, so the absence reads as "no command
matched". Ruling P9 says every Director Claude Code session; this is the exception, and it is the quiet
kind the mission names as the worst shape a defect takes.

**Proof:** scratch program, fresh folder, same order as the Director:

    C: seat settings written before the installer ran on a fresh instance -> has PreToolUse: False
    C: shared settings written afterwards -> has PreToolUse: True

The test `WriteSessionSettings_RuleHookCarried_CarriesTheSamePreToolUseAsTheSharedFile` (registration tests,
line 162) calls `EnsureInstalled` first and therefore cannot see this.

**Why it must change:** the seat's file must register the hook exactly when the shared file does, in the
real launch order. Either `EnsureInstalled` runs before the kit plan, or `WriteSessionSettings` installs the
evaluator itself (the same compare-before-write), or it is handed the path `EnsureInstalled` returned. A test
should write the seat's file on a fresh folder with the evaluator carried and assert PreToolUse is present.

### 3. The Linux timing script's "no-session" row inherits the operator's session, and writes into it

**Where:** `scripts/measure-rule-hook.sh` line 118 to 120 (`run_hook`) runs the evaluator with the inherited
environment; only `run_hook_with` sets the two variables. The PowerShell script removes both variables
before every run (lines 105 and 106); the Linux script does not.

**What breaks:** after phase 1 lands, every Director Claude Code session carries `CC_SESSION_RULES_FILE` and
`CC_SESSION_DECISION_DIR`. An agent in such a session running this script - the very situation the script is
for - measures a real session in the row labelled "outside a Director session", AND writes 302 lines into
its own session's evaluation log, AND, if any live rule matches `echo RULE-PROBE`, drops 302 decisions into
its own session's drop, which the Director forwards to the Gateway as firings. The row's number is wrong and
the run pollutes the record it is meant to leave alone ("nothing touches a real session", line 19).

**Proof:** with the two variables set to a stand-in "real" session and `--runs 3`:

    evaluation log lines in the real session: 5
    decision files in the real session's drop: 5
    last line: ... shell  rules=1  matched=1  would_deny  1146us  toolu_01MKy8QwHzZ26tkw5UsWdSkM

The script's own presence check still said "decision files written: 10 (expected 10)" because it counts
only its scratch folder.

**Why it must change:** `run_hook` must run under `env -u CC_SESSION_RULES_FILE -u CC_SESSION_DECISION_DIR`,
and the presence check should also assert that the no-session row wrote nothing anywhere. The reported
Linux numbers in the proof were taken in a session that predates the variables, so they are not themselves
wrong; the next operator's will be.

---

## Notes (no change required, or the Tech Lead's call)

**N1. Error messages in the evaluation log are resource keys, not sentences.** `UseSystemResourceKeys` is
true (`CcDirector.RuleHook.csproj` line 21), so observed lines read `error: would_deny not recorded:
UnauthorizedAccess_IODenied_Path, /path/...tmp`, `error: ExpectedStartOfPropertyNotFound, b Path: $ |
LineNumber: 0 | BytePositionInLine: 1.` and `error: DeserializeUnableToConvertValue,
CcDirector.RuleHook.ClaudeCodeToolEvent Path: $ ...`. W4 says `error: <message>`. These are decipherable by
an engineer and the setting buys a smaller, faster binary; if the phase 3 page will ever show these lines
to the account, the trade should be made knowingly.

**N2. The build scripts' presence check is a substring search over the whole Director assembly**
(`local-build-linux.sh` line 163, `local-build-avalonia.ps1` line 100). Today it is meaningful: the
Developer's built `cc-director.dll` holds the text `cc-rule-hook` exactly once outside the embedded bytes
(the resource name, at offset 4609700), so with no embedding the check fails. Any future string literal
`cc-rule-hook` in the Director's own project (a log line, a label) would make it pass without the resource.
The honest check is the manifest resource name, read as metadata.

**N3. The Linux timing baseline is `/bin/sh -c true` (line 133).** `true` is a shell builtin, so the baseline
never forks and executes a program while every hook row does; the "added time" therefore carries one
process start the baseline lacks, which overstates the hook's cost slightly - the conservative direction.
The header claim that Claude Code runs a hook command string "through /bin/sh -c the way a hook command
string is run" (line 7) is not proven by anything on the branch; the PowerShell script says so for Windows,
the Linux script does not.

**N4. Match text that is only whitespace passes `Validate` and matches every command.** The evaluator
forbids an empty string (RuleResolver.cs line 75) and nothing else; `" "` produced `would_deny` on
`echo RULE-PROBE`. The write gate (Developer A, P4 and P7) is the right place to refuse it; this is a
cross-piece gap for the Tech Lead, not a hook defect.

**N5. The matcher `Bash|PowerShell` is a regular expression to Claude Code, and I could not establish from
the installed binary whether it is anchored.** It makes no difference today: in 2.1.302 no other tool name
contains either word (searched the binary's quoted strings; only `Bash`, `PowerShell` and the path segment
`WindowsPowerShell` appear). A future or third-party tool whose name contained `Bash` would run the
evaluator and log `error: tool 'X' is not a shell tool` on every call. Worth knowing for phase 4.

A small one: a rules path that is a DIRECTORY logs `no_rules_file` (`HookRunner.cs` line 110; `File.Exists`
is false for a directory), which W4 reserves for "not written yet". Only a Director defect could produce it.

---

## Verified sound (the sharp questions, answered)

**Can the evaluator ever block, print, exit non-zero or hang?** Exit 0 and empty standard output on every
one of these, run against the natively published binary: no `CC_SESSION_RULES_FILE` (no log written either);
malformed event text; empty standard input; 5,000-deep nested event text; a 10 megabyte command (82
milliseconds; subject cut to exactly 4,000 characters); a rules path that is a directory; a 10.8 megabyte
rules file with 5,000 rules of which the last matches (115 milliseconds, one decision); a decision directory
with no permissions (an `error: would_deny not recorded:` line, no temporary file left); a read-only log
directory (decision still written, exit 0, nothing on standard error); a rules path in a missing directory;
a log path that is a directory; twenty parallel matched runs (20 decision files, 0 temporary files, 20 log
lines); a byte-order mark on the rules file; unknown extra fields and a `live` state (recorded as
`would_deny`, as P2 says). With standard input never closed the evaluator waits, as any reader must; Claude
Code closes it after writing the event, and the registered timeout is 10 seconds. I proved with a hook that
sleeps 4 seconds under a 1 second timeout, on Claude Code 2.1.302, that a timed-out PreToolUse hook is killed
and the command still runs (the reply was `RULE-PROBE-TIMEOUT`; the hook's log shows a start and no finish).
So the worst case is 10 seconds added, never a block.

**P3 ties and ranks, P13:** `RuleResolver.Decide` lines 93 to 106: highest rank among the matches; at that
rank deny if any deny; the first rule in file order with the winning result decides; overridden is every
other matching rule whose result differs. Each rank pair, the same-rank tie, the agreeing lower-rank rule
and the equal-rank determinism have tests. P1: ordinal `Contains`, with a test that a pattern-looking text
is literal.

**W3 and W4 shapes:** the decision's seventeen field names and their order are asserted exactly; the file
name is `<time>-<8 hex>.json` written as `.tmp` then renamed; written only on a match. The log is one line
per evaluation, including errors and `no_rules_file`; cut to its newest half past the limit on a whole
line; no byte-order mark; the call id last (P14).

**No network, no port:** the source guard forbids `System.Net`, `HttpClient`, `Socket` and URLs; the binary
links only libc and libm. P11: no project or package references, Native AOT, source-generated serialization,
each asserted by a guard test.

**The installer (P16), apart from findings 1 and 2:** compare before write; temporary name carrying the
process id; one atomic rename; executable bit; a refused replace keeps the old evaluator registered; nothing
on disk means no registration; a build that carries none registers none even with an old file on disk; the
path is registered only after a successful move or when a kept file exists, so the hook is never pointed at
a missing or partial file. Two Director instances have two `claude-hooks` folders (`CcStorage.Base()` is
per instance).

**Embedding:** the Developer's built `cc-director.dll` contains the published evaluator byte for byte (all
2,704,056 bytes, at offset 1209476) and the resource-name string the reader expects. `EmbedRuleHook`
evaluates empty with no runtime identifier, `true` for `linux-x64` and `win-x64`, empty for `osx-arm64`,
`false` when passed `false`, empty with `SelfContained` alone - so the tests, the solution build and the
macOS release skip the native compile. The release workflow's Windows and Linux Director jobs do publish
with a runtime identifier and would embed; that the runners have the toolchains remains unproven, as the
Developer says.

**Sessions outside a Director:** the hooks reach a session only through the Director's `--settings`; the
evaluator with no `CC_SESSION_RULES_FILE` writes nothing and exits 0. A seat's own file carries the same
PreToolUse block as the shared file (asserted by deep equality) - except in the launch order of finding 2.

**Timing scripts:** the percentile arithmetic is right (300 runs: median is row 150, 95th is row 285 of
the sorted samples); both scripts refuse a row whose hook wrote no decisions; the PowerShell script also
refuses any standard output and any non-zero exit.

---

## The Tech Lead's calls

The Developer answers every finding and note below, accepted or declined with the reason.

1. **Findings 1, 2 and 3: fix all three**, each with the test the reviewer describes. For finding 2 the
   Developer picks the mechanism, but the test must use the real launch order (seat settings written on a
   fresh folder with the evaluator carried, PreToolUse present).
2. **N1: keep** `UseSystemResourceKeys`. The evaluation log is local and read by engineers, and it is never
   forwarded; a readable message for the page is a phase 3 question if that page ever shows these lines.
3. **N2: fix** - check the manifest resource name, not a substring.
4. **N3: fix** - make the baseline start a real process (`/bin/sh -c /bin/true`), and say in the Linux
   script's header how Claude Code runs a hook command string only once it has been checked.
5. **N4: goes to Developer A** - the write gate refuses match text that is empty or only whitespace.
6. **N5: anchor the matcher** (`^(Bash|PowerShell)$`) if the installed Claude Code honours it - prove it with
   a live run that the hook still fires on `Bash`; otherwise keep `Bash|PowerShell` and say why.

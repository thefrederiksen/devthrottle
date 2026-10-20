# Review of the answers to the whole-phase review - commits `bdcf94e39` and `42cbb5d00`

Reviewer seat "Session Rules - Reviewer - whole-phase review answers", 2026-10-20, on devlinux, in the detached
checkout `devthrottle-session-rules-p1-review-wholefix` at `83b197c94` (the merge of both commits plus the
answers and this brief). The brief is `brief-whole-fixes.md`. The answers file `phase-1-whole-answers.md` and
the evidence under `evidence/exec-form-*` were read as the Developer's claims, not as proof; everything below
rests on the code, the vendor's documentation and changelog, the two hosted runs, and what ran here. Nothing
was edited or fixed; this file is the only thing written.

## Verdict

**READY. Two findings, both about the record and the measurement, neither in the code.** Both commits do what
they say. The evaluator is registered in exec form and the installed agent honours it - proven here in a way
the Developer's own evidence could not prove (below). The shared constant is one source with no project
reference, and a drift in either direction turns a test red. The `<folder>\.` fix works on both runtime
identifiers the embedding runs for: win-x64 on the hosted runner, linux-x64 built here. Finding 1 is that the
one platform case the whole-phase finding was about has no owner written anywhere. Finding 2 is that the
timing scripts now measure a form of the hook that no longer ships. Each is a short edit to a document or a
script and nothing in the two commits needs to move.

## Finding 1 - the Windows check with Git Bash absent is "folded into the Windows follow-on", but no document names it

**Where.** `phase-1-whole-answers.md`, call 1, last paragraph: "that row is folded into the Windows follow-on on
SOREN_NORTH". The Tech Lead's call said the same. The Windows follow-on brief is `brief-windows.md`; its five
tasks name a build, the tests, the hook event capture, the timing, and live rows 1 and 2. A search of the whole
mission folder for "without Git Bash", "Git Bash absent" and `CLAUDE_CODE_GIT_BASH_PATH` finds the phrase only
in the whole-phase review, its answers, this brief, and the Windows proof's timing-script note - never as a task
anyone holds. `phase-1-state.md` does not carry it either.

**What breaks.** The whole-phase finding was about exactly one configuration: a Windows machine whose shell
tool is PowerShell because Git Bash is not installed. Exec form removes the shell from the path by the vendor's
documentation and by the run here, so the fix is right by construction, but the brief asked for the proof on
that machine with a `no_match` line as the presence, and a check that lives only in a sentence that says
someone else will do it is a check that does not get run. The Windows proof so far was done on a machine WITH
Git for Windows, which never reaches the PowerShell path at all.

**What would close it.** One row in `brief-windows.md` (or the state note), in plain words: on SOREN_NORTH, with
`CLAUDE_CODE_GIT_BASH_PATH` pointed at a path that does not exist so the agent falls back to PowerShell, run
one shell command in a Director session and show the `no_match` line with its call id. Not my call; I changed
nothing.

## Finding 2 - the timing scripts measure shell form, which no longer ships; the Windows number call 2 points at will be for the wrong form

**Where.** `scripts/measure-rule-hook.sh` line 80-81 and lines 124 and 128: `HOOK_COMMAND="\"$HOOK\""`, run as
`/bin/sh -c "$HOOK_COMMAND"`. `scripts/measure-rule-hook.ps1` lines 79-80 and 113-121: `$hookCommand = '"' +
$Hook + '"'`, run through Git Bash with `-c`. Both headers say they run "the EXACT command the Director
registers - the quoted absolute path, handed to the system shell as one command string". After `bdcf94e39` the
Director registers the bare path in exec form and the agent spawns it with no shell, so the scripts now measure
a shell start-up (sh on Linux, Git Bash on Windows - the Windows proof put that baseline near 60 ms) that the
shipped hook does not pay.

**What breaks.** Nothing for a user: the number is too high, not too low, so the owner is not misled into
thinking the hook is cheaper than it is. What breaks is the proof. Call 2 answers observation 1 by pointing at
the Windows follow-on's task 4, which runs `measure-rule-hook.ps1`, and says "the owner sees that number before
phase 4". The number he will see is the cost of a form that is not registered, with the shell's own start-up
inside it and the per-command cost the exec form actually adds invisible. The script's own claim ("the EXACT
command") is now false in both files.

**What would close it.** Spawn the evaluator directly in both scripts - the program and no shell, the event on
standard input, which is one `Start-Process`-style call on both platforms - and change the header sentence to
say so; keep the shell baseline row only if it is relabelled as what it is. Then the Windows follow-on's task 4
measures what ships. Not my call; I changed nothing.

## The brief's questions, each with what proves it

### 1. Does exec form behave on every platform, and can an agent too old to know `args` block a command?

**Linux: yes, proven here, and in a way that tells exec form from shell form.** The Developer's live evidence
(`evidence/exec-form-*`) shows the hook firing with the bare path `/home/soren/.local/state/session-rules-rig-wholefix/.../cc-rule-hook`
registered with `args: []`. That path has no space and no shell character, so a shell-form run of the same
settings would have produced the identical `would_deny` line - the evidence proves the hook fired, not that
`args` was honoured. So I ran the installed agent with the rig's evaluator copied into a folder named
`dir with space`, the same rules file, and three settings files, prompt on standard input, the command allowed by
`--allowedTools 'Bash(echo:*)'`:

| Run | Agent | Registration | Evaluation log | Command ran |
|---|---|---|---|---|
| exec form | 2.1.304 (`~/.local/bin/claude`) | bare path with a space, `args: []`, `timeout: 10` | `shell rules=1 matched=1 would_deny 1814us toolu_01RcsMPY9mwXzVXt5yJNi33u` | yes, printed `RULE-PROBE` |
| shell form, control | 2.1.304 | the same bare path, no `args` | **none written** | yes, printed `RULE-PROBE` |
| exec form | 2.1.302 (`~/.local/share/claude/versions/2.1.302`, run directly) | as the first row | `shell rules=1 matched=1 would_deny 3740us toolu_014ki2EWbL1QwWfGfSpHPYeL` | yes, printed `RULE-PROBE` |

Two decision files were dropped, one per exec-form run. The control row is the proof: with no `args` the agent
handed the string to `sh -c`, which ran `/tmp/.../dir` with two arguments and found nothing, so no evaluator ran
and nothing was logged - and the command still went ahead, which is the documented non-blocking failure. With
`args: []` - an EMPTY array - both versions spawned the path directly. So an empty `args` counts as exec form on
the installed agent, which the documentation ("exec form when `args` is set") does not state for the empty case
and which the registration depends on.

**macOS: nothing to run.** No evaluator is embedded for a macOS runtime identifier (`CcDirector.Avalonia.csproj`,
`EmbedRuleHook` is set only for `win-` and `linux-`), so `ruleHookPath` is null and no PreToolUse hook is
registered; the SessionStart hooks keep shell form, which is right for them (they begin with a bare program
name). Exec form on macOS is the same `spawn` without a shell as on Linux and nothing platform-specific is in the
registration, but it was not run.

**Windows, with and without Git Bash: not run from this seat (no Windows machine); right by the vendor's text.**
The hooks documentation (read 2026-10-20) says exec form has "no shell ... No shell tokenization happens on any
platform", that on Windows exec form "requires `command` to resolve to a real executable such as a `.exe`"
(the registered path ends in `cc-rule-hook.exe`), that "absolute paths with spaces, such as `C:\Program
Files\nodejs\node.exe`, are a single valid executable", and - in the section on failed hooks on Windows without
Git Bash, for the case "the hook runs one `.exe`" - that with exec form and no `onFailure`, "the call can go
ahead when the program fails, is missing, or times out". The `shell` field is "Ignored when `args` is set". So
the PowerShell path in the whole-phase finding is not reached in either Windows configuration. The hosted
win-x64 run (39419360410) ran the built `cc-rule-hook.exe` on a PreToolUse event and checked exit 0 with nothing
on either stream, but it did not run the agent. See Finding 1 for the row that should prove it on SOREN_NORTH.

**An agent too old to know `args`: it cannot block, by the version history.** The agent's changelog (fetched
2026-10-20): `args: string[]` (exec form) was added in **2.1.139**; a shell-form PreToolUse hook on Windows
without Git Bash began to BLOCK on failure in **2.1.299** ("Fixed PreToolUse and PermissionRequest hooks that
set no shell failing open when Git for Windows isn't installed"); `onFailure: "block"` arrived in 2.1.295 and is
not set. An agent that ignores `args` is older than 2.1.139, and no agent older than 2.1.299 blocks a tool call
on a failed hook without `onFailure`. So on such an agent the registration degrades to shell form with an
UNQUOTED path: on a path with no space it runs the evaluator as before; on a path with a space (a Windows user
name with a space, say) the hook fails to start, the agent shows a hook error notice on every shell command,
every command goes ahead, and nothing is evaluated - the silent never-fire shape, but on an agent 160 releases
behind the one the Director is proven on. What is NOT proven is whether an agent older than 2.1.139 rejects a
settings file holding an unknown `args` key, which would drop the SessionStart hooks with it; no such binary is
on this machine (2.1.301 to 2.1.304 are installed) and the changelog does not say. The Director sets no agent
version floor anywhere I could find (`src/CcDirector.Core/Claude`, the setup engine). Noted, not a finding: the
phase proves itself against the installed agent, as every earlier proof did.

### 2. Does the linked file keep the evaluator free of project references, and can the two limits still drift without a test going red?

**No project reference, by the project file.** `src/CcDirector.RuleHook/CcDirector.RuleHook.csproj` has no
`ProjectReference` and no `PackageReference`; the Gateway's `<Compile Include="..\CcDirector.RuleHook\HookWireLimits.cs" Link="Rules\HookWireLimits.cs" />`
is a one-way link INTO the Gateway, so the evaluator's build does not change. The class is `internal` in both
assemblies, each compiled from the same file; the Gateway's `InternalsVisibleTo` lists only its test projects,
so the two copies can never be seen by one compilation. `RuleHookSource` in the Director's project includes
`..\CcDirector.RuleHook\*.cs`, so a change to the new file rebuilds the embedded evaluator (the Linux build
here recompiled it). The hosted Gateway image is unaffected: the repository `Dockerfile` copies the whole tree
(`COPY . .`) and `.dockerignore` excludes only build output, so the linked file is in the build context.

**Drift turns a test red in every direction I could construct:**

- Gateway goes back to a literal of a DIFFERENT value: `SubjectMaxLength_GatewayLimit_IsTheEvaluatorsConstant`
  compares `HookRuleWords.SubjectMaxLength` to the linked type's constant by reflection - red.
- Gateway drops the `Compile Include`: `SubjectMaxLength_GatewayAssembly_CompilesTheEvaluatorsOwnLimitsFile`
  cannot find the type - red (and `RuleKinds.cs` would not compile without it anyway).
- The shared constant changes value: the Gateway follows it by construction, and the evaluator's own suite pins
  the number - `HookRunnerTests.Run_CommandLongerThan4000Characters_SubjectIsCutTo4000` asserts a literal 4000,
  so the evaluator side goes red and says so.
- The evaluator's `HookRunner.SubjectLimit` is rewritten to its own literal: the same evaluator test is red
  unless the literal is 4000, in which case nothing has drifted.
- The column: `GatewayDbContext` sizes `Subject` with the Gateway constant, and the migration snapshot holds
  4000; a changed constant fails the newest-migration check that the gate already runs.

The one shape that passes green is the Gateway keeping the linked file AND writing a literal 4000 of its own
in `RuleKinds.cs` - two sources again, but equal, so nothing has drifted yet; the moment either moves, one of the
tests above is red. That is the drift being caught at the only moment it exists. Watched failing: with the
installer's exec form, the `Compile Include` and the constant all reverted in a scratch worktree at `83b197c94`,
`ClaudeRuleHookRegistration` ran 7 failed, 17 passed and `HookSubjectLimitShared` ran 2 failed, 0 passed -
exactly the counts the answers claim.

### 3. Does `<folder>\.` work for every runtime identifier the embedding runs for, and is any other quoted path in the build backslash-terminated?

**The identifiers.** `EmbedRuleHook` is true for any `win-*` or `linux-*` runtime identifier. What actually
builds with one: `release.yml` publishes the Director for win-x64 (line 107) and linux-x64 (line 584);
`local-build-avalonia.ps1` builds win-x64 (line 89); `local-build-linux.sh` publishes `$RID` (linux-x64 by
default); the rig script builds through the Linux script. macOS (`osx-arm64`) is excluded by the condition.

**win-x64: proven by the hosted runs.** Run 39418982124 at `f5fd578f8` (the commit before the fix) shows the
defect in its log: `CcDirector.RuleHook -> D:\a\devthrottle\devthrottle\src\CcDirector.Avalonia\obj\rule-hook\Release\win-x64 --nologo -v q\`
followed by `error : The Session Rules hook was not produced at ...\win-x64\cc-rule-hook.exe`. Run 39419360410
at `89b6a572c` (the fix, identical in content to `42cbb5d00` - `git diff` between them on the project file is
empty) shows `Embedding the Session Rules hook: ...\obj\rule-hook\Release\win-x64\cc-rule-hook.exe`, then the
job's own checks: the executable exists, is a native x64 image, ran on a PreToolUse event, exited 0 and printed
nothing; the job succeeded.

**linux-x64: proven here.** `dotnet build src/CcDirector.Avalonia -c Debug -r linux-x64 --self-contained true
-p:EmbedRuleHook=true` at `83b197c94`, after deleting the old output: the Exec line in the log reads
`-o "/home/.../obj/rule-hook/Debug/linux-x64/." --nologo -v q`, then `Embedding the Session Rules hook:
.../obj/rule-hook/Debug/linux-x64/cc-rule-hook`, build succeeded, exit 0, and the native executable is there
(5,282,128 bytes). The build engine on Linux turns the backslashes of the property into slashes before the
shell sees them (a three-line throwaway project confirmed `mkdir -p "<dir>\."` lands in `<dir>/`), so on Linux
the old form never failed and the new form cannot either. The Developer's rig build at `bdcf94e39` did NOT
contain this fix (the branch was cut before it; the merge brought the two together), so this is the first
Linux build of the merged form.

**Other backslash-terminated quoted paths in the build: none.** Every `<Exec>` in the repository's project
files was read (`CcDirector.Core.csproj` line 87 - clang, two file paths; the Gateway's npm commands with no
quoted paths; `CcDirector.DeliveryQualification.csproj` - git commands). A search of every project, props,
targets, workflow, shell, PowerShell and batch file for a backslash directly before a closing quote finds hits
only in PowerShell `Copy-Item` destinations and batch `copy` targets, where a trailing backslash inside quotes
is ordinary and correct, because those interpreters do not treat backslash as an escape. The only place a
quoted folder ending in a backslash reached a program that parses its command line with the Windows argument
rules was this `dotnet publish -o`, and it is fixed.

### 4. Is every call in "The Tech Lead's calls" answered as asked?

| Call | Asked | Answered | As asked? |
|---|---|---|---|
| 1 | Exec form, test first and watched failing, comment at 252-254 corrected, live proof that the installed agent honours `args`, Windows row folded into the follow-on | Done; the test and its 7 failed / 17 passed reproduced here; the comment now says sh, Git Bash when installed, PowerShell when not; live proof on 2.1.302 and 2.1.304 | Yes, except that the live proof could not tell exec form from shell form (closed here, question 1) and the Windows row is written nowhere (Finding 1) |
| 2 | Point at the Windows timing | Points at `brief-windows.md` task 4 | Yes - but that task measures shell form (Finding 2) |
| 3 | Observation 2 into `mission.md` open items, plain words, no code | Section 10, item 4, in plain words; no code change | Yes |
| 4 | Observation 3 beside it | Section 10, item 5; no code change | Yes |
| 5 | Point at the embedding fix | Names `89b6a572c` and the symptom; says it is carried separately | Yes; it is now on the branch as `42cbb5d00` |
| 6 | One line each for observations 5, 6 and 7 saying why safe in dry run | Three lines, each a dry-run argument that holds (a firing changes no command; a lost batch is retried; an old repo's rules only mis-record) | Yes |
| 7 | One shared constant without a project reference, else a pinning test; test first | Linked source file, `internal`, two tests written first (2 failed before) | Yes |

## What else was checked and found as designed

- **The registration shape.** `BuildSettings` emits `{ type, command, args: [], timeout: 10 }` with no `shell`
  key, from the ONE builder both the shared file and a seat's own settings file use; the test holds every path
  that registers the hook to `AssertExecForm`, including the "replace refused, keep the old evaluator" path. The
  SessionStart hooks are unchanged and remain valid under sh, Git Bash and PowerShell because each begins with
  a bare program name.
- **Nothing else reads the registered command.** `ClaudeSettingsFileTests` in the Avalonia tests reads only
  the SessionStart command; the measurement scripts build their own (Finding 2); no script or tool parses the
  PreToolUse entry.
- **No network, no listener** was added by either commit; the diff is a registration shape, a constant, a
  linked file, two tests and a build switch.
- **Test counts here**, devlinux, .NET 10.0.301 at `~/.dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1`, at `83b197c94`:

| What | Result |
|---|---|
| `dotnet test src/CcDirector.RuleHook.Tests` | 70 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Core.UnitTests --filter 'FullyQualifiedName~ClaudeRuleHookRegistration\|FullyQualifiedName~Hook'` | 84 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests -p:RunWorkspaceTypecheck=false --filter 'FullyQualifiedName~HookSubjectLimitShared\|FullyQualifiedName~HookRule'` | 78 passed, 0 failed, exit 0 |
| the same two filters with the fixes reverted (scratch worktree, removed after) | 7 failed / 17 passed; 2 failed / 0 passed |
| `dotnet build src/CcDirector.Avalonia -c Debug -r linux-x64 --self-contained true -p:EmbedRuleHook=true` | Build succeeded, hook embedded, exit 0 |

## Observations - no harm, nothing blocks

1. **The one-off Windows workflow is still active.** `gh workflow list --all` shows "Session Rules - Windows
   native build (one-off)" active (id 390490207). The repository rule says a one-off workflow is disabled by the
   pull request that finishes its work; this belongs to the Windows branch, not to these two commits.
2. **The answers say the embedding fix "is not on this branch".** True of `mission/session-rules-hooks-p1-wholefix`
   when written; the merge `62457e578` has since put both on the pull request branch, so a reader of the merged
   branch should not go looking for a missing commit.
3. **Earlier records describe shell form.** `phase-1-hook-proof.md`, `phase-1-windows-proof.md` and the Windows
   state line say the agent runs the hook string through Git Bash; they were true when written and are past
   tense. The live comment in `ClaudeHookInstaller.cs` is the one that is current, and it is.

## What was read

Both commits in full (`git show`), `ClaudeHookInstaller.cs` whole, `HookRunner.cs` (the environment contract,
the cut, the decision drop), `HookWireLimits.cs`, `RuleKinds.cs`, `CcDirector.RuleHook.csproj`,
`CcDirector.Gateway.csproj` (the link and `InternalsVisibleTo`), `CcDirector.Avalonia.csproj` lines 1-93 (the
embedding properties and both targets), `ClaudeRuleHookRegistrationTests.cs` (diff), `HookSubjectLimitSharedTests.cs`,
`HookRunnerTests.cs` lines 270-300, every `<Exec>` in the repository's project files, `release.yml`'s publish
lines, both local build scripts, both measurement scripts, `session-rules-rig.sh` (sign-in and build sections),
`Dockerfile` and `.dockerignore`, `brief-windows.md` whole, `phase-1-state.md`, the `mission.md` diff of
`39a2ee82f`, `phase-1-whole-review.md` and `phase-1-whole-answers.md` whole, the five `evidence/exec-form-*`
files, the two hosted run logs, the vendor's hooks documentation (exec form, shell form, `shell`, `onFailure`,
exit codes, timeouts, failed hooks on Windows without Git Bash) and the agent's changelog (`args`, the
PowerShell shell tool, the Windows block-on-failure change).

## What was not reached

- **Windows, at all.** No Windows machine from this seat. Exec form on Windows rests on the vendor's text and
  the hosted runner's direct execution of the built evaluator, not on the agent running it there (Finding 1).
- **macOS.** No evaluator is embedded there, so there is nothing to run; the exec-form spawn was not exercised.
- **An agent older than 2.1.139**, for whether an unknown `args` key invalidates the settings file.
- **A running Director** with the merged branch. The live runs here used the agent directly with a settings
  file shaped exactly as `BuildSettings` writes it (checked against `evidence/exec-form-registration.json` and
  the test), and the rig's own evaluator binary; the Director's launch path was covered by the registration
  tests, not by a launch.
- **The full `test-local.ps1` gate** - no `pwsh` here; the three suites above are the ones the change reaches,
  and `Gateway.Tests` names none of the changed types.

---

## The Tech Lead's calls

1. **Finding 1: already held, now named.** The check is step 4 of `brief-windows-rows.md` on the branch
   `mission/session-rules-hooks-p1-windows` (written after this review's checkout was cut): on SOREN_NORTH,
   `CLAUDE_CODE_GIT_BASH_PATH` pointed at a path that does not exist, a Director session using the
   `PowerShell` shell tool, and a `no_match` or `would_deny` line tied to the call id. The Windows Developer
   holding it is session `117f19f9`. No change here.
2. **Finding 2: fix.** The Developer who answered the whole-phase review changes both timing scripts to spawn
   the evaluator directly - the program, no shell, the event on standard input - as the Director now registers
   it, corrects both headers, and keeps a shell row only if it is relabelled as the shell's own cost. Then
   re-measures on Linux and replaces section 4 of `phase-1-proof.md` with the new numbers, saying the old ones
   included a shell start-up. Answered in `phase-1-whole-fixes-answers.md` beside this file.

# Review of the WHOLE of phase 1 - pull request #4268 at `def3aeb0f`

Reviewer seat "Session Rules - Reviewer - phase 1 whole phase", 2026-10-19, on devlinux. Reviewed in the
detached checkout `devthrottle-session-rules-p1-review` at `def3aeb0f`; `origin/main` was `bf9115c51` at the
time of reading, and the diff read was `git diff origin/main...HEAD` (120 files). This review did not read
`phase-1-proof.md` as evidence; it read code, the vendor's documentation, and what ran here. Nothing was
edited or fixed; this file is the only thing written.

## Verdict

**NOT YET. One finding.** The one promise phase 1 rests on - the hook cannot block, by construction - holds
on Linux and macOS and on a Windows machine that has Git Bash. On a Windows machine WITHOUT Git Bash, which is
exactly the machine whose shell tool is `PowerShell` and therefore the only place the registered `PowerShell`
matcher is ever reached, the registered hook command cannot run as written, and the vendor's documentation
says a failing hook in that configuration blocks the tool call. Finding 1 has the proof. It is a one-line
change in the registration, and nothing else in the phase needs to move for it.

Everything else asked in the brief was checked and found as designed; the answers to the eight questions are
below, each with what proves it, and seven observations follow that prove no harm and block nothing.

## Finding 1 - the PreToolUse hook is registered in shell form, which is bash; on Windows without Git Bash it runs in PowerShell, where it cannot work, and the documented outcome of a failing shell-form PreToolUse hook there is a BLOCK

**Where.** `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs`, `BuildSettings`: the evaluator is registered as
`{ type: "command", command: "\"<absolute path>\\cc-rule-hook.exe\"", timeout: 10 }` - a `command` string with
no `args` and no `shell` field.

**What the vendor's documentation says** (https://code.claude.com/docs/en/hooks, read 2026-10-19, quoted):

- "**Shell form** runs when `args` is absent. The `command` string is passed to a shell: `sh -c` on macOS and
  Linux, Git Bash on Windows, or PowerShell when Git Bash isn't installed. Set the `shell` field to choose
  explicitly."
- "On Windows without Git Bash, a shell-form `PreToolUse` or `PermissionRequest` command hook that fails with
  `onFailure` unset can instead block the tool call."
- From the section "Failed hooks on Windows without Git Bash": "A shell-form hook with no `shell` set runs in
  PowerShell when Git Bash isn't installed, and a script written for Bash may not run as written there."
- "**Exec form** runs when `args` is present. Claude Code resolves `command` as an executable ... and spawns
  it directly with `args` as the argument vector. There is no shell ... No shell tokenization happens on any
  platform."

**Why the registered string cannot run under PowerShell.** The command string is `"C:\...\cc-rule-hook.exe"`,
quotes included. That is a bash command line: bash strips the quotes and executes the path. PowerShell reads a
statement that begins with a quoted string as a string EXPRESSION, not an invocation - invoking a quoted path
in PowerShell needs the call operator (`& "C:\...\cc-rule-hook.exe"`). So, depending only on how Claude Code
hands the string to PowerShell:

1. the quotes survive: PowerShell evaluates the string and writes the path to standard output, exit 0. The
   evaluator never starts. Every shell command of every session on that machine is unevaluated, with NO
   evaluation-log line and NO decision - the exact "silent never-fire" shape the Tech Lead ruled the worst a
   defect can take (ruling P4), and the one shape the evaluation log exists to make impossible (W4); or
2. the quotes are consumed: PowerShell sees `C:\Users\<name>\AppData\Local\...\cc-rule-hook.exe`, which with
   the ordinary space in a Windows user path is "The term 'C:\Users\<first word>' is not recognized", a
   failing hook - and the documentation above says that on Windows without Git Bash a failing shell-form
   PreToolUse hook BLOCKS the tool call. That is a block, in dry run, from a hook whose whole design (P10) is
   that it cannot block.

Which of the two happens was not provable from here (no Windows machine, and no PowerShell on devlinux); both
are harm, and the second is the harm phase 1 exists to rule out.

**Why this configuration is real, and why it is the one that matters.** The PreToolUse matcher is
`^(Bash|PowerShell)$`. Claude Code uses its `PowerShell` shell tool on Windows when Git Bash is absent (the
Windows proof needed no setting to see it; the docs say shell form "runs in PowerShell when Git Bash isn't
installed"). So the `PowerShell` half of the matcher is reached mainly on the machines where the hook as
registered cannot run. The Director does not require Git Bash: nothing in the setup engine or the Director
checks for it (grep of `src`, `tools`, `docs` for "Git Bash" finds only path-translation and browser-shim
code), and a `git.exe` from MinGit, scoop, or Visual Studio satisfies `GitCommandRunner` without putting a
bash where Claude Code looks. The Windows proof (`phase-1-windows-proof.md`, section 3) proved the hook under
Claude Code 2.1.302 on a machine WITH Git for Windows and says so: "Claude Code on Windows runs a hook command
string through Git Bash". It did not test the other case, and the installer comment at
`ClaudeHookInstaller.cs:252-254` now states Git Bash as a fact of the platform rather than of that machine.

The pre-existing SessionStart hook does not have this problem: its command is
`powershell -NoProfile -ExecutionPolicy Bypass -File "<path>"`, which is a valid invocation under bash AND
under PowerShell. The rule hook is the first registration that only bash can read.

**What would close it.** Register the evaluator in exec form - `command` is the absolute path, `args` is `[]`
- which the documentation describes as the form for a Windows `.exe` and which uses no shell on any platform
(so the quoting comment in `BuildSettings` goes away too); or keep shell form and set `shell: "powershell"`
with the call operator on Windows. Either way, check that the installed 2.1.302 honours the field used (the
docs gate `onFailure` at 2.1.295 and give no version for `args` or `shell`), and prove it on a Windows machine
with Git Bash absent or `CLAUDE_CODE_GIT_BASH_PATH` pointed at nothing, with a `no_match` line as the presence.
Not my call to make, and I made no change.

## The eight questions

**1. Can the dry run block, time out, crash, or slow a command?** No path found on Linux or macOS, and none on
Windows with Git Bash. `HookRunner.Run` returns 0 on every path, writes nothing to standard output, catches at
the entry point only, and swallows a failure to write its own log (`HookRunner.cs:76-87`). Missing rules file
-> `no_rules_file`; the path is a directory -> `error:`; not JSON, wrong version, bad rank, empty match ->
`error:` with the reason; a half-written file cannot be seen because the Director writes it to a sibling and
renames (`SessionHookFiles.WriteAtomic`), and on Windows a rename refused by a reader is the Director's
failure, retried next cycle, never the hook's. Each of those has a test in `HookRunnerTests` and I ran them
(70 passed). The documentation says a timed-out PreToolUse hook "doesn't block the tool call", a non-zero exit
other than 2 or a hook that "fails in some other way, such as not starting" is a non-blocking error, and a
process that cannot be spawned is non-blocking too - all "except for hooks whose failure blocks the action",
which is the Windows-without-Git-Bash case in Finding 1. Slowness: the evaluator reads the whole rules file
on every command and nothing on the Gateway bounds a match text's length or a tenant's rule count
(observation 3); the measured added time on Linux was about 4 to 5 ms median at one and at fifty rules; on
Windows it was not measured (the native build was blocked by missing C++ tools).

**2. Network or ports in the hook?** None. `CcDirector.RuleHook` has no project references, no package
references, and no use of `System.Net`, sockets, pipes or HTTP (grep of the project: only the csproj's own
SDK line matched). Its I/O is three files: read the rules file, write one decision file, append one log line.
The Director's two new Gateway calls (W1 fetch, W5 post) ride the existing `GatewayClient` with its 10-second
timeout and the Director credential; no listener is opened anywhere in the diff.

**3. Most specific scope wins, machine "all except", remote normalisation, no remote.** Right on both sides.
The hook's `RuleResolver.Decide` takes the highest rank among the rules that match, deny wins a same-rank
tie, the first top-rank rule in file order decides, and "overridden" is only a matching rule that disagreed
(P13); `RuleResolverTests` cover each, including a rule for another event or tool never matching. The rank in
the file is checked against `appliesTo` so a Gateway re-ranking is an error, never a silent change. On the
Gateway, `RuleAppliesTo.Reaches` compares machine names without regard to case and "all except" as
`!Names(ExceptMachines)`; the write gate (`SessionRuleRecordRules.CheckAppliesTo`) refuses a machine rule
with neither list, with both lists, or with a repo, and a repo-on-machine rule with an except list - so the
one ambiguous shape `Reaches` would read as "every machine" cannot be stored. `GitRemoteIdentity.Normalise`
reduces https, ssh with port, scp form, user and password, a trailing `.git`, trailing slashes and case to one
spelling, is idempotent (the Director sends the normalised form and the Gateway normalises again and compares
the answer), lives in one place both ends reference, and the write gate refuses a rule repo not already in
its one spelling. A checkout with no `origin`, or a plain folder, answers the empty identity and only
everywhere and machine rules reach it (`Reaches` requires a non-empty repo for the repo kinds); any OTHER git
failure writes no file rather than an empty identity (`SessionRepoIdentity.ResolveAsync`), which is the
right side to fail on.

**4. Can one session read or write another's rules file, or see another tenant's rules?** Another tenant's:
no. Every Gateway read is scoped by `HasQueryFilter(e => e.TenantId == ActiveTenant)` and every write by the
tenant check in the write gate; the Director asks W1 with its own credential; a forwarded decision naming a
rule of another tenant finds no rule and is refused as "a record of nothing". Another session on the same
machine: yes, as with the preamble file - the rules file path carries no token (`session-rules/<id>.json`),
so any process of the same operating-system user can read or rewrite it, and the maintainer only rewrites
when the Gateway's answer differs from what is on disk. The decision directory is token-named, the same
capability as the pointer drop, and the forwarder refuses a directory whose token is wrong and a decision
whose body names another session. See observation 2 for what this means in phase 4.

**5. The migration on existing rows, both providers.** The same ten rule columns and thirteen firing columns
on both, with defaults, so existing rows become `screen` rules that apply `everywhere` with empty lists, and
existing firings become `screen` firings with a null decision id; the unique index on (tenant, decision id)
compares only hook firings because both providers let any number of rows leave it null. PostgreSQL: run
HERE against a real PostgreSQL 16 in a throwaway container (`SessionRuleKindsPostgresTests`, passed, see
"What was run"). SQLite: `SessionRuleKindsMigrationTests` in the Gateway unit suite (see "What was run").

**6. Can anything change how a September screen rule fires?** No. `RuleCandidateFilter` skips every rule that
is not `screen` with a stated reason, and `RuleEvaluator` answers `NoRules` when no screen rule exists; a hook
rule cannot be promoted to live (store and write gate both refuse); a screen rule's create path is unchanged
except that `appliesTo` defaults to everywhere, and the gate refuses a screen rule with any hook field or a
narrower `appliesTo`. The screen firing path changed only by passing the rule's kind into `CheckFiring`,
which requires a screen firing to be against a screen rule - true of every existing row. What a September
rule's reader sees differently is only the extra fields on list and show.

**7. Is the PreToolUse hook installed only for tools with rules, removed cleanly, and kept out of the user's
own settings?** It is installed for EVERY Director Claude Code session, for the two shell tools, whenever the
Director build carries the evaluator - Tech Lead ruling P9, because Claude Code reads hooks at launch and
rules arrive mid-session. That is a documented departure from the mission brief's "register hooks only for
tools that have rules", met at the tool level only; the cost was measured on Linux. A build that carries no
evaluator registers no PreToolUse hook and logs loudly; a failure to write the evaluator can no longer cost
the session its SessionStart hooks (the hook review's finding 1, fixed and tested). The settings file is the
Director's own, in the instance's `claude-hooks` folder, passed with `--settings`, which the documentation
says "lasts one session and doesn't write to any file"; `~/.claude/settings.json` is never touched. The
evaluator binary stays on disk in the instance folder between sessions, which is by design (compare before
write).

**8. Where could a constant be substituted and the tests stay green?** I looked and found the usual places
pinned: the matcher text and the 10-second timeout (`ClaudeRuleHookRegistrationTests`), deny-wins and the
rank table against `appliesTo` (`RuleResolverTests`), the W2 field names both ways and the evaluation-log
path on both sides (`DirectorAndHookContractTests`), the batch limit as one shared constant. Two places a
substitution would NOT be caught: the hook's registration shape (Finding 1 - no test runs the registered
command through anything but the test's own assertions), and the evaluator's 4000-character subject cut
against the Gateway's 4000-character column, which are two literals that happen to agree
(`HookRunner.SubjectLimit`, `HookRuleWords.SubjectMaxLength`); the Gateway refuses a longer subject by id, so a
drift would surface as refusals in the Director log, not silence.

## Observations - no harm proven, nothing blocks

1. **Hook registered for every session, not only when a rule exists.** Ruling P9; measured on Linux at about
   4 to 5 ms median per shell command; not measured on Windows. The owner should see the Windows number
   before phase 4, as the mission's section 7 asks.
2. **The policed agent can edit its own rules file.** `CC_SESSION_RULES_FILE` is in the agent's environment,
   the file has no token, and the maintainer rewrites it only when the Gateway's answer changes. In dry run
   the worst outcome is a missing or forged "would" record. In phase 4, a live deny can be removed by the
   session it polices with one write, and nothing today detects it. Not a phase 1 harm; it is a design
   question phase 4 has to answer before anything enforces.
3. **Nothing bounds the rules file.** No maximum match-text length, match count or rule count on the Gateway,
   and the evaluator reads and scans the whole file on every command. Only a tenant's own agents (phase 2)
   can grow it, so the harm is self-inflicted; worth a bound before phase 4.
4. **The release workflow was not run.** The embedding target runs on any build with a Windows or Linux
   runtime identifier, so the release jobs embed without a workflow change - IF the runners have the native
   toolchains. A runner without one fails the release loudly, which is the right failure. macOS Directors
   carry no evaluator until phase 4 and log "RULE HOOK MISSING" on every session launch meanwhile.
5. **A forwarded decision's session id is not checked against the tenant's roster.** The Gateway stores it
   as text. Within a tenant, a Director could record a firing against any session id. Same trust as the
   screen evaluator's firings; noted only.
6. **A non-refusal exception during `RecordHookFirings` is a 500 for the whole post.** The Director keeps
   every drop and backs off up to five minutes, so nothing is lost; a persistently failing batch would be
   retried forever. No such exception was found reachable under the store's lock.
7. **The maintainer keeps a real repo identity for the life of the session.** A session whose checkout
   changes its `origin` while it runs keeps its old rules until it is removed. A no-remote answer IS re-asked
   every cycle, so the common case (a clone gaining an origin) is handled.

## What was read

The whole diff `origin/main...HEAD` at the file level, and in full: `src/CcDirector.RuleHook/*`,
`ClaudeHookInstaller.cs`, `RuleHookBinary.cs`, `SessionHookFiles.cs`, `SessionManager.cs` (diff),
`CcStorage.cs` (diff), `SessionRulesMaintainer.cs`, `HookDecisionForwarder.cs`, `SessionRepoIdentity.cs`,
`SessionRulesFile.cs`, `SessionRuleWire.cs` (Core), `ControlApiHost.cs` (diff), `GatewayClient.cs` (diff),
`GitRemoteIdentity.cs`, `HookFiringLimits.cs`, `RuleKinds.cs`, `SessionRule.cs`, `SessionRuleBody.cs`,
`SessionRuleRecordRules.cs`, `SessionRuleStore.cs` (diff), `SessionRuleWire.cs` (Gateway, diff),
`SessionRuleEndpoints.cs` (diff), `RuleCandidateFilter.cs`, `RuleEvaluator.cs`, `SessionKeyGuard.cs` (diff),
`GatewayDbContext.cs` (diff), both entities, both migrations' `Up` and `Down`, `CcDirector.Avalonia.csproj`,
both local build scripts, `test-local.ps1` (diff), `cc-director.sln` (diff), `cli.py` and `rule_ops.py`
(diff), `docs/cli-reference.md` (diff), `mission.md`, `phase-1-plan.md` (P1 to P18, W1 to W5), the
`release.yml` publish steps, `SessionPointerWatcher.Purge` (to confirm it deletes files only, never the new
`decisions` and `decision-records` folders), the test names in `HookRunnerTests`, `RuleResolverTests`,
`ClaudeRuleHookRegistrationTests` and `DirectorAndHookContractTests`, the earlier reviews
`phase-1-phase-2-integration-review.md` and `phase-1-hook-answers.md`, and `phase-1-windows-proof.md` and
`phase-1-hook-proof.md` for what they claim NOT to have proven. The Claude Code hooks reference was read for
the shell-form, exec-form, timeout, non-blocking and Windows-without-Git-Bash wording quoted in Finding 1.

## What was run

All on devlinux, .NET 10.0.301 at `~/.dotnet`, `DOTNET_USE_POLLING_FILE_WATCHER=1`, this checkout at `def3aeb0f`.

| What | Result |
|---|---|
| `dotnet test src/CcDirector.RuleHook.Tests` | 70 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Rules\|~Hook\|~SessionPointerDrop\|~CatchLogLineScan\|~Architecture"` | 191 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules\|~SessionRuleKindsMigrationTests\|~SessionKeyGuard\|~Migration"` | 1,302 passed, 0 failed, exit 0, 1 min 23 s |
| `dotnet test src/CcDirector.Gateway.DatabaseTests --filter "FullyQualifiedName~SessionRuleKindsPostgresTests"` against a throwaway `postgres:16` container started for this run (database `ccpgproof_<instance>`, the rig gate's variables set by hand because `pwsh` is not on this machine), removed after | 1 passed, 0 failed, 0 skipped, exit 0, 19 s |

The working tree was clean before and after apart from this file.

## What was not reached

- **Windows, at all.** No Windows machine from this seat. Finding 1 rests on the vendor's documentation and
  on PowerShell's parsing rules, not on a run; the Windows proof's own run covered only the Git Bash case.
- **A running Director.** The live rig (`scripts/session-rules-rig.sh`, rows 1 and 2 of mission section 7)
  was not run here; a test Director on devlinux needs its own storage root and home, and the evidence files
  under `evidence/` are the mission's own testimony, which this review was told not to trust and did not.
- **The release workflow** and the native build on a GitHub runner.
- **The full `test-local.ps1` gate** - no `pwsh` here; the four suites above are the ones the change reaches.
- **The Python tool's tests** were run by the phase 1 meets phase 2 review (5,109 passed); not re-run here.
- **The September usage-limit screen rule on its demonstration rig** (mission section 7, row 5) - the code
  path is unchanged by construction (question 6), but it was not fired here.

---

## The Tech Lead's calls

The Developer who built the installer is gone, so a fresh Developer answers every item below, accepted or
declined with the reason, in `phase-1-whole-answers.md` beside this file.

1. **Finding 1: fix.** Register the evaluator in EXEC form: `command` is the absolute path to the evaluator,
   `args` is `[]`, no shell anywhere, on every platform, so the quoting in `BuildSettings` and its comment go.
   Before relying on it, prove the installed Claude Code (2.1.302 on devlinux) honours `args`: a real
   session whose exec-form PreToolUse hook writes its evaluation-log line (a presence). The test is written
   first and watched failing: the registration's shape, and the stated platform comment at
   `ClaudeHookInstaller.cs:252-254` corrected to what is known. The real-machine check (Windows with Git Bash
   absent, or `CLAUDE_CODE_GIT_BASH_PATH` pointed at nothing, with a `no_match` line as the presence) is folded
   into the Windows follow-on rows on SOREN_NORTH.
2. **Observation 1:** the Windows timing is being measured in the Windows follow-on; answer by pointing to it.
3. **Observation 2:** a phase 4 design question, not a phase 1 harm. Record it in `mission.md`'s open items for
   phase 4, in plain words, and make no code change.
4. **Observation 3:** record it as a phase 4 item beside observation 2; no code change in phase 1.
5. **Observation 4:** the hosted Windows run DID find a defect in the embedding (a trailing backslash inside
   quotes in `PublishRuleHook`); its fix is carried onto this branch separately. Answer by pointing to it.
6. **Observations 5, 6 and 7:** noted, no change; answer each with one line saying why it is safe in dry run.
7. **The 4000-character pair in question 8:** make the evaluator and the Gateway share one constant if a
   reference is possible without breaking the evaluator's rule of no project references; otherwise add a
   contract test that pins the two literals equal. Test first.

# Session Rules part 2 - phase 1 proof

Written 2026-10-19 by the phase 1 Tech Lead. Branch `mission/session-rules-hooks-p1`. Everything marked
RAN BY THE TECH LEAD was run by this seat, not taken from a Developer's report. The plan, rulings P1-P17
and wire shapes W1-W5 are in `phase-1-plan.md`; the reviews and the Developers' answers are in `reviews/`;
the running record is `phase-1-state.md`.

## What phase 1 is

Every rule now has a `kind` (`screen`, `hook`, `session_start`) and a scope, `appliesTo` (`everywhere`,
`machine`, `repo` by normalised git remote, `repo_on_machine`; most specific wins). Existing rules became
`screen` and `everywhere`. A hook rule with a Match handler is served to each Director session as a
per-session rules file, kept current like the preamble; a Claude Code PreToolUse hook on the shell tools
(`Bash`, `PowerShell`) runs `cc-rule-hook`, a small native program carried inside the Director, which
decides locally with no network call and no port, records `would_deny` or `would_allow`, and never
blocks. The Director forwards each decision to the Gateway, where it becomes a firing on its rule.

## 1. Unit and contract tests - RAN BY THE TECH LEAD on Linux

On `40124743d` (the branch after merging origin/main; the later commits add only the rig script and
documents), .NET 10.0.301, `DOTNET_USE_POLLING_FILE_WATCHER=1`:

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.RuleHook.Tests` | 70 passed, 0 failed, exit 0 |
| `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~Hook\|FullyQualifiedName~SessionPointerDrop\|FullyQualifiedName~Rules"` | 163 passed, 0 failed, exit 0 (includes `HookScriptContractTests`) |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules\|FullyQualifiedName~SessionRuleKindsMigration\|..."` | 642 passed, 0 failed, exit 0 (includes `RulesTypeNothingGuardTests`) |

Before the main merge, on `4f21e04a7`, the wider Core filter (adding FactoryKitLaunch, FactorySeat,
Architecture) was 212 passed, 0 failed.

Five Core tests (`CcWorktreesPoolSlotsTests`, `RepositorySourceIndexTests`, `WorktreePoolSettingsTests`)
fail on Linux; the Tech Lead ran them on the untouched base and they fail identically there.

**Taken from Developers, not re-run by the Tech Lead:** the Postgres migration test
`SessionRuleKindsPostgresTests` 1 of 1 passed on a hand-built `postgres:16` container shaped like
`scripts/test-database.ps1` (no PowerShell on this machine), after the migration was regenerated on top
of main's `AddMailDesk`; both providers reporting no pending model changes.

## 2. Tests and the repository gate on Windows - from the Windows Developer

On SOREN_NORTH (Windows 11, .NET 10.0.303), branch rebased on `4f21e04a7`, commit `6a86311d8`:
RuleHook.Tests 70, Core filter 163, Gateway Rules 562 - all passed, exit 0.

`scripts\test-local.ps1 -Configuration Release`, commit `96ca70087`: it found TWO defects of this
mission - `CcStorage.SessionRules` was not classified for the reclaim tool, and two catch-block log lines
were not in house form - fixed in `64c41f86e`, seen failing then passing. After the re-run every suite
completed except one Gateway.UnitTests test, `DefinitionFormatDocumentTests.Document_NamesEveryKeyTheSchemasTake`,
which was main's (fixed by #4218) and is cleared by the merge of main. `-Gateway`: green, exit 0.

## 3. Live check rows 1 and 2 on Linux - RAN BY THE TECH LEAD

A real Director session on Linux: the rig `scripts/session-rules-rig.sh` (see `live-rig.md`), built from
`a1cb2ed25` - its own Gateway (process 3927485, port 7931, auth on) and an isolated slot-6 Director
(process 3927880, machine `devlinux`), both on a scratch root and a scratch HOME, never the owner's.
The Director build reported the hook embedded; `cc-rule-hook` was written into the instance's
`claude-hooks` folder at the first session launch. Two scratch repositories:
repo A origin `https://github.com/session-rules-rig/repo-a.git`, repo B origin
`git@github.com:session-rules-rig/repo-b.git`.

Four dry-run hook rules (match strings chosen so none contains another):

| Rule id | appliesTo | Match |
|---|---|---|
| `20779208` | repo `github.com/session-rules-rig/repo-a` | `echo RULE-PROBE` |
| `1d157098` | machine `SOREN_NORTH` | `echo ELSEWHERE-PROBE` |
| `33e5fed7` | machine `devlinux` (the twin of the line above, aimed here) | `echo HERE-PROBE` |
| `52a640ec` | machine, all except `devlinux` | `echo EXCEPT-PROBE` |

**W1, what the Gateway sends each machine and repo, by named rule:** devlinux + repo A: `RULE-PROBE`
(rank 3) and `HERE-PROBE` (rank 2). devlinux + repo B: `HERE-PROBE` only. SOREN_NORTH + repo A:
`RULE-PROBE`, `EXCEPT-PROBE`, `ELSEWHERE-PROBE` - and not `HERE-PROBE`.

**W2, each session's rules file:** repo A's file names repo `github.com/session-rules-rig/repo-a`,
machine `devlinux`, rules `20779208` and `33e5fed7`; repo B's names repo
`github.com/session-rules-rig/repo-b` and holds `33e5fed7` only - the repo-A rule is absent from it.

### Row 1 - the positive: PROVEN

Claude Code session `8e91c0ac` in repo A was asked to run `echo RULE-PROBE`.
- The transcript: call `toolu_01LjdehxZF6M4n7Sw1reiCpX`, `Bash: echo RULE-PROBE`, and the agent answered
  "The command ran and printed RULE-PROBE" - dry run did not block it.
- The evaluation log: `shell rules=2 matched=1 would_deny 2152us toolu_01LjdehxZF6M4n7Sw1reiCpX`.
- The Gateway: one firing on rule `20779208`, `kind` hook, `decision` `would_deny`, `subject`
  `echo RULE-PROBE`, `repo` `github.com/session-rules-rig/repo-a`, `machine` `devlinux`, event
  `before_command`, tool `shell`, agent tool `Bash`, outcome "dry run: recorded what this rule would have
  decided; the command was not blocked."
- The decision drop directory was empty afterwards: forwarded and removed.

### Row 2 - the negative controls: PROVEN, each by a PRESENCE

Claude Code session `66cf68d9` in repo B, four commands, each tied to its call id in the evaluation log:

| Command | Call id | Evaluation log | Proves |
|---|---|---|---|
| `echo RULE-PROBE` | `toolu_019hXnYy...` | `rules=1 matched=0 no_match 903us` | other repository: not touched |
| `echo ELSEWHERE-PROBE` | `toolu_013HGsNf...` | `rules=1 matched=0 no_match 1306us` | rule for another machine: not touched |
| `echo EXCEPT-PROBE` | `toolu_01AZMEVi...` | `rules=1 matched=0 no_match 1355us` | "all machines except this one": not touched |
| `echo HERE-PROBE` | `toolu_015TRbaQ...` | `rules=1 matched=1 would_deny 2202us` | the twin: machine scoping does fire here |

Firings on the Gateway afterwards: `20779208` one (repo A's), `33e5fed7` one (repo B's `HERE-PROBE`),
`1d157098` none, `52a640ec` none. The evidence files are in `evidence/` beside this file.

Also seen live: every screen-evaluator pass skipped the four hook rules, saying "this is a hook rule, and
only screen rules are evaluated against a screen" (ruling P8).

The rig came down cleanly (Director on its named signal, Gateway on terminate, the sign-in copy
deleted). The owner's `~/.local/bin` links point where they did; their timestamps changed at 20:49:44,
which the owner's own Director log shows was its Python tools bundle 2.25.0 update, not the rig.

## 4. Timing - added time per shell command

**Linux - RAN BY THE TECH LEAD**, `scripts/measure-rule-hook.sh --runs 300` with the exact evaluator the
rig Director installed, through `/bin/sh -c` as Claude Code runs a hook, 32 processors, load average 23 to
25 (a busy machine):

| Row | Median | 95th percentile | Added (median) |
|---|---|---|---|
| baseline, `/bin/sh -c /bin/true` | 2.22 ms | 3.24 ms | - |
| outside a Director session | 6.37 ms | 8.54 ms | 4.2 ms |
| rules file not there | 7.16 ms | 9.26 ms | 4.9 ms |
| 0 rules | 7.62 ms | 9.70 ms | 5.4 ms |
| 1 rule, matching | 8.22 ms | 10.26 ms | 6.0 ms |
| 50 rules, last matching | 8.59 ms | 10.73 ms | 6.4 ms |

Inside the live sessions the evaluator itself reported 0.9 to 2.2 ms per decision. Most of the added time
is the process starting.

**Windows - NOT PROVEN.** No native build was possible (below). What is known: Claude Code on Windows runs
every hook command through Git Bash, and Git Bash alone costs about 60 ms per hook call (median 59.93 ms,
95th percentile 77.86 ms, 30 runs) before any hook program starts - Claude Code's cost for any hook on
Windows, not ours to remove.

## 5. The September usage-limit screen rule - NOT PROVEN live

- The September rule body is accepted unchanged and stored as `kind` `screen`, `appliesTo` `everywhere`
  (rule `39e98fc9` on the rig), and the screen evaluator's unit tests are green (section 1).
- On the rig the screen evaluator ran and considered it: a pass named its trigger words ("limit,
  usage-credits, allowance, out of credits") and skipped the hook rules.
- But the live firing did not happen. The target was a RawCli `bash` session; the notice was printed on
  its screen, but the Director never recorded that session going from working to idle, so no turn ended
  and the evaluator was not asked again. Whether a Linux RawCli shell behaves the same on main was not
  checked (the September demonstration ran on Windows `cmd`). And the step after that, the model call,
  needs a DevThrottle inference key the machine's secret store does not hold.
- To run it: the owner adds the key (`cc-secrets add devthrottle-api-key --env-name DEVTHROTTLE_API_KEY
  --uses run`), and the row is run on Windows `cmd` as in September, or the Linux shell's turn end is
  investigated.

## 6. NOT PROVEN, in one list

- Windows: the native build of `cc-rule-hook` and of the Director embedding it, the Windows timing, and
  live rows 1 and 2 on Windows. SOREN_NORTH has no C++ build tools; the install needs the owner's
  approval and has not completed. (The Windows Developer did prove, with a non-native build, that the
  evaluator decides correctly on real Windows events through Git Bash, and that the PowerShell tool's
  input field is `command`.)
- The September screen rule firing live (section 5).
- A real stop of a running Director with decisions undelivered: the shutdown drain and the start-up
  forwarding of leftovers are proven by unit tests only.
- The release workflow publishing the Director with the hook embedded: it rests on the GitHub runners
  having the native toolchains (Windows C++ tools, Ubuntu clang); not run.
- Codex never reviewed this work: it is not installed on devlinux, so every review ran on Claude Code
  with Fable, the next in the owner's reviewer order.

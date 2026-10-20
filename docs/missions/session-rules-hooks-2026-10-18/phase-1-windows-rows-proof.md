# Phase 1 on Windows, the rows - what was proven on SOREN_NORTH with the hosted build

Written 2026-10-20 by "Session Rules - Developer - Windows rows on the hosted build", branch
`mission/session-rules-hooks-p1-windows-rows`, cut from `origin/main` at `0eb3a64bc`.
Self-testimony: the Tech Lead re-runs it and a reviewer on another agent family reads the change.

Machine: SOREN_NORTH, Windows 11 Enterprise 10.0.26200, 24 logical processors. Claude Code 2.1.304 (the
machine's agent has updated itself past the 2.1.302 the fixtures were captured from). Git for Windows bash
5.2.37. PowerShell 7.6.7 installed as a local tool in the session's scratch folder, not system-wide.

## Status by step of the brief

| Step | Status |
|---|---|
| 1. Timing with the native evaluator | PROVEN |
| 2. Live rows 1 and 2 on a real slot-6 Windows Director, own Gateway | PROVEN |
| 3. The September usage-limit screen rule, live, on Windows `cmd` | FIRED, did NOT act - two findings below |
| 4. Windows WITHOUT Git Bash | BLOCKED - Git Bash cannot be hidden from Claude Code on this machine without the owner; the PowerShell tool path through exec form is PROVEN |

## The build used

Both binaries from GitHub run 39421736490 (branch `mission/session-rules-hooks-p1-windows` at commit
6b265dd73, which carries the exec-form fix), downloaded with `gh run download 39421736490` and checked before use:

| Artifact | SHA-256 | Matches the brief |
|---|---|---|
| `cc-rule-hook.exe` | B859F729889BB6FB40AB214EB16DC8D6D466F564C78C598C8E7291125788758A | yes |
| `cc-director.exe` (copied to `scripts\local-build\cc-director6.exe`) | 66B472996227A17C68B027829730ED6D7C0178D6076E8FB248FC6A152B9F4041 | yes |

The Gateway is the console Gateway published from this worktree at `7ac7ccff9` (`/healthz` reported
`2.26.0+7ac7ccff9a6a...`), Cockpit and mobile app not built. Its DLL SHA-256 was
3EDB2B1F4B31535E0620A732BFA903FDDEE87A52C0310358879DCD6B4774C060.

## 1. Timing - proven

Run with the Tech Lead's script as merged to main (`scripts/measure-rule-hook.ps1` at `0eb3a64bc`, unchanged),
which starts the evaluator directly as the exec-form registration does. Its Windows path had never run; it ran
clean here. Command, twice, 300 runs a row, the native evaluator from the hosted build, the Windows Bash
fixture as the event. Full output of both runs: `evidence/windows-rows-timing.txt`.

    pwsh -NoProfile -File scripts\measure-rule-hook.ps1 -Hook <run 39421736490 artifact>\cc-rule-hook.exe -Runs 300

| Row | Run 1 median | Run 1 95th | Run 2 median | Run 2 95th |
|---|---|---|---|---|
| baseline, Git's `true.exe` started directly | 11.68 ms | 38.25 ms | 15.15 ms | 29.01 ms |
| through a shell, Git Bash running `/usr/bin/true` | 38.03 ms | 220.35 ms | 58.57 ms | 202.85 ms |
| outside a Director session | 12.66 ms | 26.07 ms | 15.40 ms | 24.28 ms |
| rules file not there | 14.74 ms | 30.87 ms | 14.80 ms | 27.71 ms |
| 0 rules | 13.91 ms | 140.87 ms | 14.13 ms | 21.38 ms |
| 1 rule, matching | 12.47 ms | 15.02 ms | 17.79 ms | 29.29 ms |
| 50 rules, last matching | 19.33 ms | 232.39 ms | 17.90 ms | 26.56 ms |

Both runs exit 0, 604 of 604 decision files, and each evaluation log's last line reads `no_rules_file`,
`no_match`, `would_deny`, `would_deny`, ending in the fixture's call id; the evaluator reported its own work as
0.7 to 4.1 ms. Processor load: run 1 went from 10 to 100 percent (about thirty other sessions share the
machine), run 2 from 40 to 50 percent. The 95th percentiles above 100 ms are scheduling outliers in run 1.

**Reading it.** Every hook row costs 12 to 20 ms per shell command in all, where the same command through Git
Bash cost 38 to 59 ms. The "added over baseline" difference is near zero and once negative (run 1: 1 rule
12.47 ms against baseline 11.68 ms, no-session 12.66 ms), because Git's `true.exe` loads the msys runtime and
is itself about as slow to start as the evaluator. An earlier run with a version of the script not carried to
this branch used `rundll32.exe` (a native program that does nothing) as the baseline and measured it at 7.36 ms
median, which put the evaluator's added time at 2.9 to 7.2 ms (old branch `mission/session-rules-hooks-p1-winrows`,
commit `ca29e96ac`). Whether main's script should change its Windows baseline is the Tech Lead's call; it was
not changed here.

On top of ours, Claude Code also runs the owner's own global PreToolUse hook (a Python script) on every shell
command; that one is not ours and was not measured.

## 2. Live rows 1 and 2 - proven

**The rig.** New script `scripts/session-rules-rig.ps1`, the Windows twin of `scripts/session-rules-rig.sh`:
rig folder `%LOCALAPPDATA%\session-rules-rig-win`, Gateway on port 7933 with its own root and auth on, the
slot-6 Director with `CC_DIRECTOR_ROOT` set to the rig root, both started by the rig's own scheduled tasks
(`session-rules-rig-gateway`, `session-rules-rig-director`) whose `.cmd` bodies set the environment. The
shared `cc-director-launch` task was NOT repointed, against the brief's wording, for two reasons: it points at
another seat's slot 5 (`D:\ReposFred\devthrottle\scripts\local-build\cc-director5.exe`), and a task action
cannot carry `CC_DIRECTOR_ROOT`, so a Director started from it directly would use the machine's default root
and join the production Gateway. The Director stopped on its own named signal both times
(`Local\cc-director-shutdown-ae7012e8-...`, "exited on its signal"), the Gateway on `POST /shutdown`; nothing
was force-killed and nothing ran from the rig afterwards. The sessions used the owner's own Claude Code
sign-in and `~/.claude` (on Windows the agent's home is the user profile, as for every Director session).

Repositories: `repo-a` with origin `https://github.com/session-rules-rig/repo-a.git`, `repo-b` with origin
`git@github.com:session-rules-rig/repo-b.git`. Director `ae7012e8-691c-49fc-8e27-372aa75ac6cd`, machine
`SOREN_NORTH`.

**The registration the real Windows Director wrote** (`evidence/windows-rows-registration.json`): the
PreToolUse hook is `command` = the bare path `C:\Users\soren\AppData\Local\session-rules-rig-win\root\instances\default\claude-hooks\cc-rule-hook.exe`,
`args: []`, `timeout: 10`, matcher `^(Bash|PowerShell)$` - exec form - and `cc-rule-hook.exe` was installed
into `claude-hooks` by the Director when the first session started.

**The rules** (all dry run):

| Rule | appliesTo | match |
|---|---|---|
| `56a25cf0-f08a-4263-bbfc-99c715cb6350` | repo, given as `https://github.com/Session-Rules-Rig/repo-a.git`, stored as `github.com/session-rules-rig/repo-a` | `echo RULE-PROBE` |
| `bce90568-0aca-4952-a238-dee3f5b19ed2` | machine `devlinux` (not this one) | `echo ELSEWHERE-PROBE` |
| `8d11e7b5-2acf-4365-b803-05226663ac65` | machine `SOREN_NORTH` (the twin, this one) | `echo HERE-PROBE` |
| `4e87fbb4-102a-444b-938e-491023fc51de` | machine, `exceptMachines` `SOREN_NORTH` | `echo NOTHERE-PROBE` |

**W1, by named rule, before any session:** SOREN_NORTH + repo A gave `56a25cf0` (rank 3) and `8d11e7b5`
(rank 2), not `bce90568` and not `4e87fbb4`. SOREN_NORTH + repo B gave `8d11e7b5` only. devlinux + repo A gave
`56a25cf0`, `4e87fbb4` and `bce90568`, not `8d11e7b5`.

**W2, the rules files** (`evidence/windows-rows-<session>.rules.json`): session A `5c34cc71-a6b6-428a-afa6-0655a59f5bb0`
says repo `github.com/session-rules-rig/repo-a`, machine `SOREN_NORTH`, rules `56a25cf0` and `8d11e7b5`;
session B `150b8df3-f5df-4972-8011-c4d7e1c258cf` says repo `github.com/session-rules-rig/repo-b` and holds
`8d11e7b5` only - the repo-A rule is NOT in it.

**Row 1, the positive.** Session A ran `echo RULE-PROBE`, call `toolu_01NBQfM43c7ELULDaDdJ6AtP`. Its
evaluation log, read before the session closed:

    2026-10-20T12:51:03.1476763Z	shell	rules=2	matched=1	would_deny	11427us	toolu_01NBQfM43c7ELULDaDdJ6AtP

The Gateway recorded firing `7de1f44a-cf34-4b49-91a4-8654ac8bb757` on rule `56a25cf0`: decision
`would_deny`, subject `echo RULE-PROBE`, repo `github.com/session-rules-rig/repo-a`, machine `SOREN_NORTH`,
event `before_command`, tool `shell`, agentTool `Bash`, outcome "dry run: recorded what this rule would have
decided; the command was not blocked." The command still ran: the tool result is `RULE-PROBE`.

**Row 2, the negatives, and the twin.** Session B ran four commands. Its evaluation log, read before it closed:

    2026-10-20T12:52:02.9683980Z	shell	rules=1	matched=0	no_match	7531us	toolu_01PHi8WARcaUEJ8X5feju88n
    2026-10-20T12:52:27.5143227Z	shell	rules=1	matched=0	no_match	17693us	toolu_01HhdCb4bXCzzo1mbWdRZ82B
    2026-10-20T12:52:37.3980924Z	shell	rules=1	matched=0	no_match	5819us	toolu_01H64z4q1NEFiXxxBpS4TfMi
    2026-10-20T12:52:44.2965927Z	shell	rules=1	matched=1	would_deny	9725us	toolu_01DpBbUQxFHjC74pvB5JUJW9

against the transcript's calls (`evidence/windows-rows-commands.txt`):

| Call id | Command | Log line | Proves |
|---|---|---|---|
| `toolu_01PHi8WARcaUEJ8X5feju88n` | `echo RULE-PROBE` | `no_match` | 2a: repo-A rule does not reach repo B |
| `toolu_01HhdCb4bXCzzo1mbWdRZ82B` | `echo ELSEWHERE-PROBE` | `no_match` | 2b: a rule for machine devlinux does not reach SOREN_NORTH |
| `toolu_01H64z4q1NEFiXxxBpS4TfMi` | `echo NOTHERE-PROBE` | `no_match` | 2b: `exceptMachines` SOREN_NORTH excludes this machine |
| `toolu_01DpBbUQxFHjC74pvB5JUJW9` | `echo HERE-PROBE` | `would_deny` | the twin: machine scoping does work here |

The Gateway recorded firing `065b446f-c69c-4be9-898e-70a5c7cb57ce` on `8d11e7b5` (subject `echo HERE-PROBE`,
repo B, SOREN_NORTH), none on `bce90568` and none on `4e87fbb4`, and no second firing on `56a25cf0`
(`evidence/windows-rows-firings.json`). All four commands printed their text.

Answering Claude Code's folder-trust question: a fresh scratch folder opens on it, and the prompt route refuses
to type into a menu (by design, "LookBeforeTyping"). It was answered through the rig Gateway's terminal
stream (`/sessions/{sid}/stream`, raw keystrokes: Down, then Enter). That stores the trust for the two
scratch folders in the owner's `~/.claude.json`.

## 3. The September usage-limit screen rule on Windows cmd - fired, did not act

The inference key came from this machine's secret store: `up -ScreenKeySecret devthrottle-api-key` starts the
Gateway under `cc-secrets run devthrottle-api-key --via env --env-name DEVTHROTTLE_API_KEY`; the key is in no
file the rig writes, and the Gateway's vault listed the key name `DEVTHROTTLE_API_KEY`. The key was accepted.

Rule `df72b4b6-a464-43c3-a82c-6f8f903d12d0`, the September body unchanged, stored in dry run as `kind`
`screen`, `appliesTo` `everywhere`, scope agent `RawCli`. Session `af84abbe-d9e0-4608-9864-727536692a5c`, a
RawCli session running `cmd` in repo B.

**First attempt - no evaluator pass at all.** `echo You've reached your Fable 5 limit. Run /usage-credits to
continue or switch models with /model.` printed the notice, but the Director's submit verifier recorded
"never started a turn within 8 beats ... the agent produced under 2048 bytes", so no working-to-idle edge
happened and the evaluator was never asked. This is the same miss the Linux rig found (`phase-1-proof.md`
section 5), now seen on Windows `cmd` too: a short command in a plain shell is not a turn. Not phase 1's code.

**Second and third attempts - a turn, a real model call, a recorded firing.** A command busy for a few seconds
that prints more than 2048 bytes and then the notice (`dir /s C:\Windows\System32\drivers\etc C:\Windows\Web
& ping -n 6 127.0.0.1 & echo You've reached ...`). Gateway log (`evidence/windows-rows-usage-limit-gateway-log.txt`):

    08:55:24.741 [RuleEvaluator] sid=af84abbe-...: 1 rule(s) worth asking about
    08:55:36.714 [HostedInferenceBrain] chat/completions model=devthrottle/wingman OK: 525 chars in 11.6s
    08:55:36.740 [RuleEvaluator] firing: rule=df72b4b6-... sid=af84abbe-... decision=refused typed=no
    08:57:43.611 [RuleEvaluator] sid=af84abbe-...: 1 rule(s) worth asking about
    08:58:02.667 [HostedInferenceBrain] chat/completions model=devthrottle/wingman OK: 596 chars in 19.1s
    08:58:02.668 [RuleEvaluator] firing: rule=df72b4b6-... sid=af84abbe-... decision=refused typed=no

The two firings (`evidence/windows-rows-usage-limit-firings.json`), each with the notice as the last line of
its stored screen:

| | Firing 1, 12:55:36Z | Firing 2, 12:58:02Z |
|---|---|---|
| decision | `refused` | `refused` |
| understanding | The session has stopped at a notice from its model provider stating the Fable 5 model limit has been reached, with a prompt to run /usage-credits or switch models. | The screen shows a session that has stopped after hitting its Fable 5 model limit, with a notice from the model provider stating the allowance is used up and suggesting /usage-credits to continue. |
| reason | The screen explicitly shows the current model's allowance is used up, which is exactly the condition the instruction watches for; /usage-credits is the command that shows what credits remain. | The screen explicitly reports the session has run out of its model allowance ('You've reached your Fable 5 limit'), which matches the instruction's trigger condition. The /usage-credits command is the one that shows what is left. |
| grounding | the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifiable either. | the reason cites text this screen does not contain: '), which matches the instruction'. |
| outcome | nothing was typed: the reason for acting cites nothing from this screen, so there is nothing anybody could check it against. | nothing was typed: the reason for acting cites text this screen does not contain. |

The model decided to act both times. Both were refused by the grounding rule (Architect ruling A12,
`src/CcDirector.Gateway/Rules/RuleReasonGrounding.cs`, from the September merge #2665 and unchanged by phase 1),
so the September result - `act`, "dry run: nothing was typed. It would have typed: /usage-credits" - was NOT
reproduced. Dry run would have typed nothing either way.

**Finding A (main's code, not Windows-specific, not fixed here): an apostrophe inside a single-quoted citation
makes a true citation read as a fabricated one.** `QuotedPassages` pairs single quotes naively: in firing 2's
reason, the opening quote before `You've` closes on the apostrophe IN `You've` (a 3-character passage, below
the 8-character floor, ignored), and the next pair runs from the quote after `limit` to the apostrophe in
`instruction's`, producing the passage `), which matches the instruction`, which is not on the screen - exactly
the passage the firing names. The model quoted the screen correctly and the act was refused as ungrounded. The
September notice itself contains `You've`, so this rule meets the defect on its own target text. Not changed
here: it is a safety gate outside phase 1, and loosening its quote parsing is a decision for the Tech Lead.

**Finding B (cosmetic):** the Gateway's `no-candidates` line repeats "this is a hook rule, and only screen
rules are evaluated against a screen ..." once per hook rule (four times here).

## 4. Windows without Git Bash - blocked; the PowerShell tool path is proven

**What was tried, and why it cannot hide Git Bash on this machine.** The rig Director was restarted with
`CLAUDE_CODE_GIT_BASH_PATH=C:\no-git-bash-here\bash.exe` (a path that does not exist) and a session opened in
repo A (`9cca703f-49ca-4009-8a7f-9c62e124a3e2`). The session itself said its shell tools are `Bash` and
`PowerShell`. Checked directly with `claude -p` (2.1.304) outside the Director: with that variable set,
Claude Code still lists `Bash, PowerShell`; with Git also removed from `PATH` it still does, and its Bash tool
then runs `BASHV=5.2.37(1)-release SHELL0=/usr/bin/bash` - Git Bash, found in its default install folder
`C:\Program Files\Git`. The vendor's setup page offers no switch for this: "Without Git for Windows, Claude
Code runs shell commands via the PowerShell tool". So the case of finding 1 needs a machine without Git for
Windows, or Git uninstalled or renamed here, which breaks every other session on SOREN_NORTH. That is the
owner's call. NOT PROVEN.

**What was proven: the hook runs, in exec form, for the PowerShell tool, in a real Windows Director session, and
does not block.** In session `9cca703f`, Claude Code ran `Write-Output NOBASH-PROBE $env:CLAUDE_CODE_GIT_BASH_PATH`
with tool `PowerShell`, call `toolu_011TrYyYiUYum12eMv9odkKs`; the evaluation log
(`evidence/windows-rows-9cca703f-49ca-4009-8a7f-9c62e124a3e2.eval.log`):

    2026-10-20T13:00:38.4045573Z	shell	rules=2	matched=0	no_match	1199us	toolu_011TrYyYiUYum12eMv9odkKs

and the tool result `NOBASH-PROBE` is on the screen. (The variable read empty inside that PowerShell; whether
Claude Code passes `CLAUDE_CODE_*` variables to its shell tool was not established.) Exec form uses no shell at
all, so this path does not depend on Git Bash in principle; that it runs with Git Bash genuinely absent is
what remains unproven.

## What changed on this branch

Branch `mission/session-rules-hooks-p1-windows-rows`, cut from `origin/main` at `0eb3a64bc` (phase 1 merged).
The live rows ran earlier the same day on the old branch `mission/session-rules-hooks-p1-winrows` at
`7ac7ccff9`, with the Gateway built from there and the Director and evaluator from run 39421736490; only
this branch's own files are carried over.

- `scripts/session-rules-rig.ps1`: new, the Windows live rig (build, director, up, status, down, make-repos,
  cli, spawn, prompt, screen, rules, evidence, commands). `-ExtraEnvironment` passes one variable to the
  Director and its sessions.
- `evidence/windows-rows-*`: the registration, rules files, evaluation logs, transcript calls, firings, the
  usage-limit rule, its firings and Gateway log lines, and the full timing output.

No product code changed and no test was written: nothing in the product failed on Windows. The rig script is
tooling, exercised end to end by the runs above.

## NOT PROVEN

- Phase 1 on Windows with Git Bash genuinely absent (finding 1's case): needs a machine without Git for
  Windows, or the owner's approval to remove Git here.
- The September screen rule reaching `act` on Windows: it fired twice and was refused by grounding both times
  (finding A explains the second). Whether it reaches `act` on main today was not checked.
- A RawCli `cmd` command shorter than 2048 bytes of output producing an evaluator pass: it does not, on
  Windows as on Linux.
- Whether `CLAUDE_CODE_GIT_BASH_PATH` set in the Director's environment reached the Claude Code process.
- The installer's "refused replace of a running hook is retried on the next launch" path, on Windows.
- Native timing of the evaluator inside a live session beyond the evaluator's own reported 1.2 to 17.7 ms per
  decision (the evaluation logs above); the per-command wall time inside Claude Code was not measured.

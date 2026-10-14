# Review - Factory Definition, phase 0 launch-line proofs (pull request #3996)

Reviewer seat: "Factory Definition - Reviewer - phase 0 launch proofs" (session 6b2f70ac), a separate review
session on a different agent from the author. Reviewed 14 October 2026 against origin/main `e44ee8496` and the
pull request head `cffe6991f` at `D:\ReposFred\_wt\fd-p0-launch` (read only, not edited).

## Verdict in one line

**(a) PASS stands and is reproduced. (c) Claude Code PASS and Codex refuse stand and are reproduced. (b) must not
be recorded as PASS: in Codex 0.160.1 a PreToolUse hook that is missing, crashes, times out or exits 2 lets the
command run, silently - so by mission rule A4 a Codex seat that carries rules should be refused, or the owner must
decide in writing to accept a guardrail that is not an enforcement boundary.**

## Scope

What I read:

- The mission document on origin/main, sections 4 (A4, A6, R13), 5.5 steps 1 to 8, 5.5b, 5.9 and the phase 0 row
  of section 6.
- The pull request body, both commits, the README and all three scripts, `scripted_model.py`, and every committed
  output folder under `docs/qa/factory-definition/phase-0/launch/` (launch lines, verdicts, merged settings, pointer
  files, hook call logs, the Codex event streams and standard error).
- The Director's launch code on origin/main: `SessionManager.cs` lines 730 to 1140 (argument composition, the
  environment the Director stamps, the `--settings` append at line 988, the Codex hook install and
  `--dangerously-bypass-hook-trust` at lines 999 to 1001, the skills placement, the process start),
  `ClaudeHookInstaller.cs` in full, `AgentToolCatalog.cs` (permission flags), `CodexHookInstaller.cs`
  (hooks.json merge).
- The real `hooks-settings.json` on this machine and the command lines of the eight Claude Code sessions the
  Director is running right now (read from the process list).
- Claude Code documentation: the command line reference (`--settings`, `--plugin-dir`, `--add-dir`,
  `--dangerously-skip-permissions`), the permissions page (deny-first evaluation, bypass mode, path pattern
  anchoring, hooks versus rules) and the plugin authoring page (`--plugin-dir` is for one session, nothing written
  to settings; skills named `<plugin>:<skill>`).
- Codex documentation: hooks (which tools reach hooks, fail-open behaviour, exit codes, trust bypass) and rules
  (where `.rules` files load from; no command line path for a rules file).

What I ran, all from scratch copies of the committed scripts, none inside the worktree:

- Proof (a) `a-claude-gate.ps1`, Claude Code 2.1.300: all three modes reproduced the committed verdicts exactly
  (49 seconds).
- Proof (c) `c-factory-skills.ps1`: all four Claude Code sessions and all six Codex sessions reproduced the committed
  summary exactly, including `factory-kit:fd-proof-skill` in the init skill list, `Unknown skill` without the kit,
  `Not logged in` under a per-session `CODEX_HOME`, unchanged user skill folders, and the one untracked
  `.claude/sounds/.current_session` from the user's own hook (79 seconds).
- Proof (b) was not re-run as committed; instead I ran my own experiment on the same mechanism (finding 1), which
  also exercised the same `-c hooks.PreToolUse` route and reproduced that a well-formed deny blocks.

What I could not reach: an interactive Claude Code session (the proof and my re-run use `-p`; the permission
engine is the same code path, but I did not watch an interactive one); a resumed session (`--resume ...
--fork-session`, which the Director also launches); macOS or Linux; a real Codex model (the sign-in is at its usage
limit, as the README says); Codex hooks on `apply_patch` or MCP tools; two sessions running at the same time.

## The launch line, flag by flag (judgement (a))

| Director's real line (origin/main and the live processes) | Proof (a) line | Difference matters to permissions? |
|---|---|---|
| `--dangerously-skip-permissions` (every live session here) or `--permission-mode auto` (catalog default for a new entry, `AgentToolCatalog.cs`) | both run, one mode each | No. Both run. |
| `--model <x>` when the entry names one | `--model sonnet` | No. The model does not take part in rule evaluation. |
| `--session-id <guid>` | same | No. |
| `--settings "<hooks-settings.json>"`, appended last by `SessionManager.cs:988` | `--settings <kit>\settings.json`, a copy of the real file plus `permissions.deny` | No. One `--settings`, same hooks content. Flag order is irrelevant to Claude Code. |
| environment: `CC_SESSION_POINTER_FILE`, `CC_SESSION_PREAMBLE_FILE`, `CC_SESSION_ID`, Gateway pair, `PATH` repair | the two hook files only, `CC_*` cleared | No. None of these is read by the permission engine. The hook ran and wrote the pointer with the launched id, which is the proof the real hook path is intact. |
| interactive (ConPty) | `-p --output-format stream-json --verbose` | Not a permissions difference; it is the only way to capture every tool result. Unverified in interactive mode, see scope. |
| `--resume <id> --fork-session` on a resumed session | not run | Same flag set plus resume; the deny rules come from the same `--settings`. Nothing is removed, so this is not a gap that could make the proof pass where the real launch fails. |

Nothing in the proof's line could make it pass where the Director's line would fail. The merged deny binds under
both permission flags, the start folder's own `.claude/settings.json` deny binds, the Director's SessionStart hook
fired and the preamble reached the model, and the control run without the merge read the kit-denied file. **(a)
is proven as the mission asked**, and it is reproducible.

Two things worth knowing about what (a) does and does not promise, from Claude Code's own documentation, so the
owner is not oversold: a `Bash(git push:*)` deny does not match the same program given by path or inside
`sh -c`, and `Read` deny rules do not apply to a subprocess that opens the file itself (a Python script, say). So a
Claude Code rule is a strong guardrail against the model's ordinary tool calls, not an operating-system boundary.
The difference from Codex (finding 1) is not that; it is that Claude Code's engine is always there and evaluates
deny first, with no failure mode that lets a matching call through.

## Findings

A finding states the harm. Items 1 and 2 bear on a verdict; 3 to 6 are notes for phase 5 and the README.

### 1. (b) The Codex hook fails open in every failure mode, silently - the PASS does not meet A4

The README says Codex documents that a hook which errors, times out or answers malformed lets the call through.
Codex's hooks page says this for unsupported output and for managed callbacks, and calls tool hooks "a useful
guardrail, not a complete enforcement boundary"; it does not say what a PreToolUse hook that crashes or times out
does, and it says exit code 2 blocks. So I measured it, on the proof's own mechanism (same `-c hooks.PreToolUse`
route, same two Director bypass flags, same scripted endpoint, command `git tag fd-proof`):

| Hook | What Codex 0.160.1 did | Tag created | Anything on standard error or in the `--json` events |
|---|---|---|---|
| script file missing | ran the command | yes | nothing |
| exits 1, writes garbage to stdout | ran the command | yes | nothing |
| sleeps 20 s, `timeout=3` | ran the command (6 s run) | yes | nothing |
| exits 2, reason on stderr (the documented block) | ran the command | yes | nothing |
| well-formed `permissionDecision: deny` (control) | blocked: `Command blocked by PreToolUse hook: DENY-CONTROL` | no | the block, on stderr |

The exit codes were confirmed standalone (`$LASTEXITCODE` 2 and 1). So in this version the only thing that blocks
is a hook that runs to completion and answers the exact JSON; every other outcome is an allow, and no surface the
Director or the owner reads says the rule was skipped.

**The harm, concretely.** A factory seat on Codex with the rule "never push" or "never send": the machine is under
the release gate (this fleet runs it for an hour at a time; PowerShell start-up alone is one to two seconds and
climbs under load), the hook misses its timeout, the push goes out, and the run record shows a clean session with
a rule in force. The owner would believe the rule bound. The mission's own words for this case are A4: "an agent
that cannot take them that way cannot run a seat that has rules: publish refuses it." A rule that is skipped when
the machine is busy is not a rule the product can promise, and "no fallback programming" says the same thing
from the other side.

**What I recommend the Developer and Delivery Lead do.** Change the README verdict for (b) from "PASS, as a
command-line hook, with limits" to "a command-line hook can carry a rule, but Codex enforces it fail-open;
by A4 a Codex seat that carries rules is refused at publish unless the owner decides otherwise", and put the
measurement above beside it (the five-row table reproduces in under a minute with the proof's own
`scripted_model.py`). If the owner wants Codex seats with rules anyway, that is his decision to write into the
mission document, and the definition check must then say "guardrail only" to the person publishing. Either way,
phase 5 should not build Codex rule enforcement as if it were equivalent to Claude Code's.

### 2. (b) Two documented holes in the hook route the README does not name

From Codex's hooks page: `write_stdin` does not run PreToolUse again when it feeds input to a command that already
passed, and hosted tools such as web search never reach a hook. The first matters for a command-pattern rule: the
model starts a shell (the command `powershell` matches no rule), then writes `git tag x` to its standard input,
and the hook never sees the tag. Not tested here; it is in Codex's own words. The README names the hosted-tool
gap and the "shell commands only" matcher, and says phase 5 must match every tool. It should also name
`write_stdin`, because matching every tool does not close it.

### 3. (a) Phase 5 must write a PER-SESSION settings file, not merge into the shared one

The mission (5.5 step 4) says the kit's rules are "merged into THAT file for the session". The file the Director
passes today is one shared file for every Claude Code session on the machine, and `ClaudeHookInstaller.EnsureInstalled`
rewrites it with `File.WriteAllText` on every launch. Merging into it would give every session on the machine the
factory's deny list, and the next launch would erase the merge. The proof did the right thing - a copy of the real
file plus the deny list, in the kit - and that is the shape phase 5 must build. The README says "copied and given";
it should say in one sentence why a copy, so phase 5 does not read the mission's wording literally.

### 4. (a) Path anchoring in a `--settings` file is relative to the FILE's folder

Claude Code's permissions page: in a file passed with `--settings`, a rule written `Read(/path)` resolves against
the directory of that file, and `./path` against the current directory. The proof used `./kit-denied.txt`, so it
resolved against the start folder and worked. A rule author in phase 5 who writes `Read(/secrets/**)` in
`rules.json` will get a rule anchored at the kit folder, not the worktree, and it will match nothing. One line in
the definition check or the rules schema avoids it.

### 5. (c) A usage trace of the kit skill lands in the user's `.claude.json`

After the runs, `~/.claude.json` holds `skillUsage["factory-kit:fd-proof-skill"]` and plugin usage
`factory-kit@inline` (counts and timestamps). It is a statistic, not a skill body, and it does not make the skill
available: the no-kit sessions, run after the kit sessions in the same folders, got `Unknown skill`. The README's
"nothing written where it must not be" checked the three skill folders only; it should name this trace so phase
5's "nothing in the user's skills folder" check is not read as "nothing in the user's home".

### 6. (b) Soundness of the scripted model - accepted, with its edge stated

The scripted endpoint is a sound way to prove client-side enforcement: the block is logged by
`codex_core::tools::router` before any process starts, the hook call log holds the real payloads Codex sent
(`tool_name: Bash`, `permission_mode: bypassPermissions`), and the model's only part is to ask. What it does not
show: a real model's tool choice (the script always used `exec_command`; `apply_patch` and MCP tools reach hooks
under other names and were not exercised), and what a real model does after a refusal. Those are phase 5 tests,
not phase 0 ones.

## Answers to the brief's three questions

- **(a)** Yes, the launch line is the Director's line in every flag that touches permissions, checked against
  `SessionManager.cs` and `AgentToolCatalog.cs` on origin/main and against the eight live sessions. No difference
  could make the proof pass where the real launch would not. Reproduced.
- **(b)** A fail-open hook does not meet A4. Measured, not inferred: missing, crashing, timing out and exit-2
  hooks all let `git tag` run in Codex 0.160.1 with nothing reported. Recommend refusing rules-bearing seats on
  Codex unless the owner writes the opposite into the mission document. The scripted endpoint is a sound proof of
  client-side enforcement for the tool calls it made.
- **(c)** Yes. The skill is in the init skill list only when `--plugin-dir` is passed, two sessions in the same
  folders without the flag do not have it, the user's skill folders have the same entries before and after, no
  `SKILL.md` outside the kit carries the secret word, and the worktree holds nothing from the kit. The name the
  agent sees is `factory-kit:fd-proof-skill`, confirmed in the init event and in the model's own Skill call.
  Codex refuse is right: `skills.config` does not add a skill and a per-session `CODEX_HOME` carries no sign-in.

## Scratch

Everything I ran lived under this session's scratchpad and the temporary folders the scripts create and delete
themselves. The scratchpad copies are removed.

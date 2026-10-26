# A scheduled factory run never prompts

Owner, 26 October 2026: "A factory run never prompts. The Director also ignores ask rules in a scheduled factory
run, so no new list can ever stall one again." His decision covers deny rules too.

## In plain terms

At 07:00 on 26 October factory seats started by a Gateway schedule stopped on "Do you want to proceed?" because the
seat folder's `.claude/settings.json` held an ask list, and nobody is there at 07:00 to answer.

Now, when the Director starts Claude Code for a scheduled factory run, it does not let Claude Code read ANY settings
file: not the user's own, not the folder's, not the folder's local one. It gives the run everything in those files
except the ask and deny lists, in a settings file of the run's own, and it hands back by other means everything else
those files would have brought: the instructions (CLAUDE.md and rules folders), the skills, and the servers. Nothing
on disk is changed. A person's session, an ordinary mission seat, and a schedule that is not a factory's start exactly
as before.

## How a scheduled factory run is told apart

A session is a scheduled factory run when both are true:

- its origin is `schedule`. The Gateway's schedule starter (`DirectorCronSessionStarter`, both its plain and its
  defined-factory path) and its trigger starter (`TriggerService`) are the only code that sets it. The origin is
  settled at the Gateway's spawn doors from the verified credential (`SpawnOrigin`): a person's device is always
  `human` and a session's key is always `agent`, so nobody can make an ordinary session look scheduled;
- it belongs to a factory (`Session.Factory`), stamped from the schedule's own record.

## What the launch line becomes

Today, for every Claude Code session:

    <args> --settings "<the Director's hooks file, or a seat's own file>"

For a scheduled factory run (`ScheduledFactoryRunSettings`, called from `SessionManager.CreateSession`):

    <args> --setting-sources ""
           --add-dir "<working folder>" [--add-dir "<each folder above it with instructions>"] [--add-dir "<the folder holding the user's .claude>"]
           --settings "<claude-hooks>/scheduled-factory-runs/<session id>.json"
           [--plugin-dir "<the user's .claude>"] [--plugin-dir "<each .claude from the working folder up with skills, commands or agents>"]
           [--plugin-dir "<seat skills>"] [--mcp-config "<claude-hooks>/scheduled-factory-runs/<session id>.mcp.json"]

with `CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1` in the run's environment.

| What a settings source brought | How the run gets it back |
|---|---|
| Ask and deny rules, from the user's file, the folder's file and its local file | Not at all. That is the point. |
| Everything else in those three files, and the Director's own settings (hooks, a seat's kit) | The run's own `--settings` file: the four merged in Claude Code's order, ask and deny removed, nothing else changed. Hooks, environment, model, allow rules, default mode and enabled plugins stay. |
| CLAUDE.md and `.claude/rules` of the working folder and the folders above it | `--add-dir` for each, with the environment variable set. |
| The user's own `~/.claude/CLAUDE.md` and `~/.claude/rules` | `--add-dir` for the folder that holds `~/.claude` (Claude Code reads an added folder's `.claude/CLAUDE.md` and `.claude/rules`). |
| Skills, commands and agents in `~/.claude` and in every `.claude` from the working folder up | `--plugin-dir` for each such `.claude` folder. They are listed as `<folder name>:<skill>` (for example `.claude:website-ceo`); a bare `/website-ceo` still runs. |
| Servers: the user's (`.claude.json`), every `.mcp.json` from the working folder up, the folder's own in `.claude.json` | One `--mcp-config` file holding them, nearest winning a name, less those a settings file or `.claude.json` names in `disabledMcpjsonServers`. |

- The launch log says what was set aside, file by file and rule by rule, and where instructions, skills and servers
  came from. Both run files are deleted when the session ends, and when a create fails.
- A settings file or `.claude.json` that Claude Code could not read stops the start with the file named. The run is
  never started on a guess at what the file said.
- `CLAUDE_CONFIG_DIR`, when the Director's environment sets it, moves the user's folder and its `.claude.json`, as it
  does for Claude Code.

## Proof against the real Claude Code CLI

There are two rounds. The first (Windows) proved the folder rule; review 2 found two gaps in it; the second (Linux)
closes them and repeats every case.

### Round 2: review 2's findings, Claude Code 2.1.310 on Linux

Driven by `scheduled-factory-run-never-prompts/ptyprobe.py` in a real pseudo-terminal (the probe now runs on Windows,
Linux and macOS). Transcripts: `scheduled-factory-run-never-prompts/linux-review-2/`. Each `new` line was not typed by
hand: a scratch program called this branch's `ClaudeHookInstaller.EnsureInstalled` and
`ScheduledFactoryRunSettings.Compose` / `ClaudeArguments` to produce it, so its settings file holds the Director's real
hooks. Its log line is beside each transcript (`*.compose.txt`).

**The user source was isolated without a sign-in.** Each case has its own configuration folder (`CLAUDE_CONFIG_DIR`)
holding the test's user settings, CLAUDE.md, rules, skills and `.claude.json`. The probe reads the current access token
from the owner's credentials file and hands it to the child as `CLAUDE_CODE_OAUTH_TOKEN`; the refresh token is never
copied, so the test cannot rotate the owner's sign-in, and the owner's real `~/.claude/settings.json` was never read or
written. The isolated `.claude.json` carries only the onboarding fields and the two test folders marked trusted.

**The success mark cannot be guessed.** The probe puts a random value in the child's environment and asks for
`echo ASKPROBE-$((40+2))-$PROBE_SECRET`. A first version of the probe looked for `ASKPROBE-42` alone and once read the
model's own description of the command ("Print ASKPROBE-42 via echo") as a run while the prompt was still waiting;
every result below was taken again with the secret.

The test folder (seat outside the home, as on the Windows machines where seats live on `D:`):

    home/.claude/CLAUDE.md                 "The user codeword is OSPREY5."
    home/.claude/rules/u.md                "The user rule codeword is USERRULE4."
    home/.claude/skills/user-probe/        a skill
    home/.claude/.claude.json              a user server, usermcp
    nest/CLAUDE.md                         "The repository codeword is HERON3."
    nest/.claude/skills/review-probe/      a parent-folder skill (review 2, finding 2)
    nest/.mcp.json                         a parent-folder server, parentmcp
    nest/seat/CLAUDE.md                    "The folder codeword is PELICAN7."
    nest/seat/.claude/rules/r.md           "The folder rule codeword is RULE9."
    nest/seat/.claude/skills/seat-probe/   a skill
    nest/seat/.claude/settings.json        {"env": {"PROBE_ENV": "ENVOK"}} plus the case's rules

`ordinary` is today's line (`--dangerously-skip-permissions --model haiku --settings <Director hooks file>`), `branch`
is the line this pull request first proposed (`--setting-sources user` plus `--add-dir`), `new` is this change.

| # | Rule | ordinary | branch | new |
|---|---|---|---|---|
| 01-03 | user-level ask `Bash(echo *)` | prompted, still waiting after 25s | **prompted, still waiting after 25s** (finding 1) | ran, no prompt |
| 04-05 | folder ask | prompted, still waiting | | ran, no prompt |
| 06-07 | folder local-file ask | prompted, still waiting | | ran, no prompt |
| 08-09 | folder deny | refused | | ran, no prompt |
| 10-11 | user-level deny | refused | | ran, no prompt |
| 12 | all at once: user ask and deny, folder ask and deny, local ask | | | ran, no prompt |

What each launch knows (13-15), asked without reading any file:

| | ordinary | branch | new |
|---|---|---|---|
| CLAUDE.md and rules | OSPREY5 USERRULE4 HERON3 PELICAN7 RULE9 | all five | all five |
| servers | parentmcp, usermcp | **usermcp only** | parentmcp, usermcp |
| skills | user-probe, seat-probe, review-probe | **user-probe only** (finding 2) | `.claude:user-probe`, `.claude:seat-probe`, `.claude:review-probe` |
| typing `/review-probe` (16-17) | ran the skill | | ran the skill |

The folder's `env` value (ENVOK) reached the command in `new`, and the Director's hooks (UserPromptSubmit, PreToolUse,
PostToolUse, Stop) ran. The `branch` knowledge run was given the Director's hooks file rather than its own composed
file, so its missing ENVOK says nothing.

The `branch` column also shows a gap the review did not name: under `--setting-sources user` a `.mcp.json` in the
folder or above it is not loaded. No factory folder carries one today, but the new line restores it.

### Why this mechanism and no other

Every way of overriding a loaded ask rule was tried in round 1 (Windows) and **prompted**:

| Tried | Result |
|---|---|
| `--dangerously-skip-permissions` alone | prompted |
| `--settings` with `{"permissions": {"ask": []}}` | prompted (lists are joined, not replaced) |
| `--allowedTools "Bash(echo *)"` | prompted, still waiting after 25 seconds |
| a PreToolUse hook answering `permissionDecision: allow` | prompted |
| a PermissionRequest hook answering `behavior: allow` (the hook ran twice, its log shows the call) | prompted, still waiting after 25 seconds |
| `allowManagedPermissionRulesOnly: true` in `--settings` | prompted, still waiting after 25 seconds |

Round 2 tried the one way that would have kept every settings source loaded: a policy file of the run's own, holding
only `allowManagedPermissionRulesOnly: true`, named by `CLAUDE_CODE_MANAGED_SETTINGS_PATH` (the name is in the
2.1.310 binary). User ask, folder ask and local ask all still prompted. A policy file with an `env` value showed why:
the value never reached the command, folder path or file path alike. The function that would read the variable
returns nothing in this public build, so the policy file is only ever the machine's own
(`/etc/claude-code`, `C:\Program Files\ClaudeCode`), which is machine-wide and so cannot be one run's.

Only not loading a file works, and `--setting-sources` is all or nothing per source. What not loading a source loses,
measured in round 2:

| Launch | Lost |
|---|---|
| `--setting-sources user` (the first proposal) | user ask rules still load (finding 1); folder and parent skills not listed (finding 2); `.mcp.json` servers |
| `--setting-sources ""` alone | everything a source brings: every CLAUDE.md, the user's skills, every server |
| plus `--add-dir` for each folder and the environment variable | folder CLAUDE.md and rules back; the user's CLAUDE.md back when the folder holding `.claude` is added; skills and servers still lost |
| plus `--plugin-dir` for each `.claude` with skills | skills back, named `<folder>:<skill>`; bare `/skill` works |
| plus `--mcp-config` | servers back |

A `--plugin-dir` pointed at a `.claude` folder does not bring that folder's settings back: with user ask, folder ask,
local ask and folder deny in the very folders loaded as plugins, all four ran. A plugin manifest that lists skill
folders by absolute path is refused (`skills.0: Invalid string: must start with "./"`), and copying the user's skills
into each run would copy 20 MB and nearly a thousand files every night, so each folder is loaded where it is.

### Round 1: the folder rule, Claude Code 2.1.310 on Windows

| Run | Launch line | Result |
|---|---|---|
| 1, negative control (today's line) | `--dangerously-skip-permissions --model haiku --settings <Director hooks file>` | **Prompted**: "Permission rule Bash(echo *) requires confirmation for this command. Do you want to proceed?", still waiting 25 seconds later. |
| 2, the first proposal | `--setting-sources user` with `--add-dir` | **Ran with no prompt**: `ASKPROBE-42 PELICAN7 HERON3 ENVOK`. |

Transcripts: `scheduled-factory-run-never-prompts/PROOF-1-without-change.txt` and `PROOF-2-with-change.txt`.

## What is platform specific

The mechanism is the same on Windows, Linux and macOS: every flag is Claude Code's own and takes the same values. The
round 2 runs were on Linux; round 1 was on Windows, where the factory seats run. What differs, and is handled:

- **The empty argument.** `--setting-sources ""` must reach Claude Code as an empty word. On Linux and macOS the
  Director splits the line itself (`UnixProcessHost.TokenizeArgs`), and a unit test proves the result is an empty
  argument. On Windows the line is passed to the process as it is, where `""` is an empty argument by the standard
  command-line rules; this was not run on Windows in round 2.
- **Folder keys in `.claude.json`.** Claude Code files a folder's own servers under its path; on Windows with forward
  slashes. The match turns back slashes into forward ones on both sides and ignores case on Windows.
- **Locations.** The user's folder is `CLAUDE_CONFIG_DIR` when set, else `<home>/.claude`; `.claude.json` is in the
  home folder, or in `CLAUDE_CONFIG_DIR` when that is set. No drive's root is ever added.
- **Quoting.** Every path is wrapped in double quotes. A path ending in a separator is trimmed first, so no `\"` closes
  a quote early on Windows.

## Unit tests

`src/CcDirector.Core.UnitTests/Claude/ScheduledFactoryRunSettingsTests.cs`, 29 tests:

- which session is one: only origin `schedule` with a factory; `human`, `agent`, `unknown`, none, and a schedule
  with no factory are not;
- the run's settings file keeps everything but ask and deny from the user's file, the folder file, the local file and
  the Director's file, names every rule set aside with its file, never changes the folder's own files, reads comments
  and trailing commas, follows `CLAUDE_CONFIG_DIR`, and stops the start naming a settings file or `.claude.json` that
  is not JSON;
- the servers: the user's, every `.mcp.json` from the working folder up (nearest wins), the folder's own from
  `.claude.json`, less the disabled; none at all writes no file;
- the instruction folders: the working folder, each folder above it with instructions (not one holding only skills),
  the folder holding the user's `.claude`; the skill folders: the user's, then each `.claude` with skills, commands or
  agents, the user's loaded once;
- the launch line's exact shape, and that on Linux and macOS the empty source list reaches Claude Code as an empty
  argument;
- through the real create on the Studio backend: a scheduled factory run gets `--setting-sources ""`, one
  `--settings` (its own file, holding the Director's hooks and no ask or deny), `--add-dir`, `--mcp-config`, and the
  environment variable, and both its files are deleted when the session is removed; a session a person opens, a
  session an agent opens, and a schedule that is not a factory's get today's line with the shared hooks file and none
  of it.

Revert check: with the user's file left out of the merge, exactly the two tests about the user's settings fail.

## Other agents, and what this does not cover

Claude Code is the only agent a factory seat with rules can run today (`FactoryRulesSupport` refuses a kit with
rules for every other agent), and every factory seat that stalled on 26 October was Claude Code. The rest is follow-up
issue #4486, because each needs its own empirical check:

- **Codex.** Its equivalents are `decision = "prompt"` rules in `.codex/rules/*.rules` and `approval_policy` in
  `.codex/config.toml`. The Director's deny-to-ask rewrite (`DenyRulesBecomeAsk`) turns `forbidden` into `prompt`
  and `approval_policy = "never"` into `"on-request"` in those files for every session, which for a scheduled
  Codex factory run could create the very prompt this change removes for Claude Code.
- **Grok** reads the same Claude Code files when it has no permission section of its own, and its own
  `[permission]` ask list.
- **Gemini** (`ask_user` policies in `~/.gemini/policies`) and **OpenCode** (`"ask"` in `opencode.json`).
- **Pi** has no approval prompt, so nothing to do.
- **Children of a scheduled run.** A session the run itself starts has origin `agent`, so it keeps its folder's
  rules. A child that hits an ask rule would wait.
- **Managed settings.** An ask rule in the machine's own policy file (`/etc/claude-code/managed-settings.json`,
  `C:\Program Files\ClaudeCode\managed-settings.json`) still applies: no flag turns it off, by Claude Code's design. It
  is the machine administrator's, not a list a folder or a user writes; none exists on the owner's machines.
- **Not tested:** commands and agents in a `.claude` folder (they ride the same `--plugin-dir` that was proven for
  skills), and `CLAUDE.local.md` files, which no run here used.
- **A skill's listed name changes.** It now carries its folder's name (`.claude:website-ceo`). Typing the bare
  `/website-ceo` was proven to run it; a brief that names the skill in prose and leaves the model to find it was not
  tested separately, though in run 15 the model listed all three by their new names.

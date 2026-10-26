# A scheduled factory run never prompts

Owner, 26 October 2026: "A factory run never prompts. The Director also ignores ask rules in a scheduled factory
run, so no new list can ever stall one again." His decision covers deny rules too.

## In plain terms

At 07:00 on 26 October factory seats started by a Gateway schedule stopped on "Do you want to proceed?" because the
seat folder's `.claude/settings.json` held an ask list, and nobody is there at 07:00 to answer.

Now, when the Director starts Claude Code for a scheduled factory run, it does not let Claude Code read the folder's
own settings files at all. It gives the run everything in those files except the ask and deny lists, in a settings
file of the run's own, and it points the run at the folder's instructions (CLAUDE.md) so it still knows what it is
for. Nothing on disk is changed. A person's session, an ordinary mission seat, and a schedule that is not a
factory's start exactly as before.

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

    <args> --setting-sources user --add-dir "<working folder>" [--add-dir "<each folder above it with instructions>"]
           --settings "<claude-hooks>/scheduled-factory-runs/<session id>.json" [--plugin-dir "<seat skills>"]

with `CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1` in the run's environment.

- `--setting-sources user` leaves out the folder's `.claude/settings.json` and `.claude/settings.local.json`. The
  user's own `~/.claude/settings.json` is still read, as for every session.
- The run's own settings file is the folder's two files merged the way Claude Code merges them, under the
  Director's own settings (its hooks, and a seat's kit rules), with `permissions.ask` and `permissions.deny`
  removed and nothing else changed. Hooks, environment, model, allow rules and default mode all stay.
- `--add-dir` for the working folder and each folder above it that holds a CLAUDE.md, a `.claude/CLAUDE.md`, or a
  `.claude/rules` or `.claude/skills` folder, stopping below the user's home and never adding a drive's root. With
  the environment variable set, Claude Code reads each added folder's CLAUDE.md.
- The launch log says what was set aside, file by file and rule by rule. The file is deleted when the session
  ends, and when a create fails.
- A folder settings file that Claude Code could not read either stops the start with the file named. The run is
  never started on a guess at what the file said.

## Proof against the real Claude Code CLI

Claude Code 2.1.310 on Windows, driven in a real pseudo-terminal by `scheduled-factory-run-never-prompts/ptyprobe.py`
(it types one prompt and reports whether "Do you want to proceed?" appears or the command runs). The test folder:

    nest/CLAUDE.md                     "The repository codeword is HERON3."
    nest/seat/CLAUDE.md                "The folder codeword is PELICAN7."
    nest/seat/.claude/settings.json    {"env": {"PROBE_ENV": "ENVOK"}, "permissions": {"ask": ["Bash(echo *)"]}}

The prompt: run `echo ASKPROBE-$((40+2))` followed by every codeword from the project instructions and then
`$PROBE_ENV`.

| Run | Launch line | Result |
|---|---|---|
| 1, negative control (today's line) | `--dangerously-skip-permissions --model haiku --settings <Director hooks file>` | **Prompted**: "Permission rule Bash(echo *) requires confirmation for this command. Do you want to proceed?", still waiting 25 seconds later. The command it wanted was `echo ASKPROBE-$((40+2)) HERON3 PELICAN7 $PROBE_ENV` - both CLAUDE.md files read. |
| 2, with the change | The line the Director's own code composed for this folder (see the transcript) | **Ran with no prompt**: `ASKPROBE-42 PELICAN7 HERON3 ENVOK` - both CLAUDE.md files read, and the folder's own `env` setting applied. |

Line 2 was not typed by hand. A scratch program called `ClaudeHookInstaller.EnsureInstalled` and
`ScheduledFactoryRunSettings.Compose` / `ClaudeArguments` from this branch to produce it, so the settings file it used
holds the Director's real hooks. Transcripts: `scheduled-factory-run-never-prompts/PROOF-1-without-change.txt` and
`PROOF-2-with-change.txt` (screen text, private paths replaced by `<scratch>`).

### Why this mechanism and no other

Every other way of overriding the ask rule was tried against the same folder first. Each one **prompted**:

| Tried | Result |
|---|---|
| `--dangerously-skip-permissions` alone | prompted |
| `--settings` with `{"permissions": {"ask": []}}` | prompted (lists are joined, not replaced) |
| `--allowedTools "Bash(echo *)"` | prompted, still waiting after 25 seconds |
| a PreToolUse hook answering `permissionDecision: allow` | prompted |
| a PermissionRequest hook answering `behavior: allow` (the hook ran twice, its log shows the call) | prompted, still waiting after 25 seconds |
| `allowManagedPermissionRulesOnly: true` in `--settings` | prompted, still waiting after 25 seconds |

In 2.1.310 an ask rule from a loaded settings file beats all of them. Only not loading the file worked:

| Tried | Result |
|---|---|
| `--setting-sources user` alone | ran, but the folder's CLAUDE.md was lost (the model answered NONE) |
| plus `--add-dir <folder>` and the environment variable | ran, and PELICAN7 was read |
| the same, folder only, with a parent CLAUDE.md | ran, but HERON3 from the parent was lost - so each parent with instructions is added too |
| the full line, with an `env` value and a logging hook in `--settings` | ran, both codewords, ENVOK, and the hook ran |

## Unit tests

`src/CcDirector.Core.UnitTests/Claude/ScheduledFactoryRunSettingsTests.cs`, 18 tests:

- which session is one: only origin `schedule` with a factory; `human`, `agent`, `unknown`, none, and a schedule
  with no factory are not;
- the run's settings file keeps everything but ask and deny from the folder file, the local file and the
  Director's file (hooks joined, local values on top), names every rule set aside with its file, never changes the
  folder's own files, reads comments and trailing commas, and stops the start naming a file that is not JSON;
- the instruction folders: the working folder first, then each folder above it with instructions, stopping at home;
- the launch line's exact shape;
- through the real create on the Studio backend: a scheduled factory run gets `--setting-sources user`, one
  `--settings` (its own file, holding the Director's hooks and no ask or deny), `--add-dir`, and the environment
  variable, and its file is deleted when the session is removed; a session a person opens, a session an agent opens,
  and a schedule that is not a factory's get today's line with the shared hooks file and none of it.

Revert check: with the decision forced to false in `SessionManager`, exactly the scheduled-run create test fails.

## Other agents, and what this does not cover

Claude Code is the only agent a factory seat with rules can run today (`FactoryRulesSupport` refuses a kit with
rules for every other agent), and every factory seat that stalled on 26 October was Claude Code. The rest is follow-up issue #4486,
because each needs its own empirical check:

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
- **The user's own `~/.claude/settings.json`** is still read. It holds no ask rules on the owner's machine today; an
  ask rule written there would stop every session alike, scheduled or not.

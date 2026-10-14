# Factory Definition, phase 0: launch-line proofs (a), (b), (c)

Run on SOREN_NORTH, 14 October 2026, Claude Code 2.1.299 and Codex CLI 0.160.1, code read on origin/main at
`e44ee8496`. Each proof is one script beside this file; its output is the folder of the same letter. Every script
works in a scratch folder under the temporary directory and deletes it at the end; none writes the user's settings,
hooks or skill folders, and none starts a Director.

| Proof | Verdict |
|---|---|
| (a) Claude Code: rules merged into the one settings file | **PASS** |
| (b) Codex: rules at launch from a file outside the checkout | **PASS, as a command-line hook, with limits** |
| (c) Factory skills for one session only | **Claude Code PASS. Codex: refuse factory skills** |

## The Director's real launch lines (origin/main)

- **Claude Code.** `SessionManager.cs:985` calls `ClaudeHookInstaller.EnsureInstalled()`, which writes the one
  settings file `hooks-settings.json` (`ClaudeHookInstaller.cs:139`), and `SessionManager.cs:988` appends
  `--settings "<that file>"`. The session's two hook files are passed in the environment
  (`SessionManager.cs:942` preamble, `SessionManager.cs:969` pointer). The permission flag comes from the agent
  entry: the catalog default is `--permission-mode auto` (`AgentToolCatalog.cs:79`, `:154`, applied by
  `AgentLaunchDefaults.cs:133`); the entries on this machine carry `--dangerously-skip-permissions`
  (`AgentToolCatalog.cs:98`). Every live Director session here was started as
  `claude --dangerously-skip-permissions [--model ...] --session-id <guid> --settings "...\claude-hooks\hooks-settings.json"`
  (read from the running processes). Proof (a) runs both permission flags.
- **Codex.** The entry's flag is `--dangerously-bypass-approvals-and-sandbox` (`AgentToolCatalog.cs:125`, `:155`).
  `SessionManager.cs:999` merges the Director's SessionStart hook into the user's `~/.codex/hooks.json`
  (`CodexHookInstaller.cs:109`) and `SessionManager.cs:1001` appends `--dangerously-bypass-hook-trust`
  (`CodexHookInstaller.cs:100`). There is no settings file on the Codex line.

## (a) The gate - `a-claude-gate.ps1` - PASS

The Director's real `hooks-settings.json` is copied and given `permissions.deny = ["Read(./kit-denied.txt)",
"Bash(git push:*)"]`: the merge phase 5 will do, still one `--settings`. The start folder is a git repository whose
own `.claude/settings.json` denies `Read(./repo-denied.txt)`. The session is asked to read three files and run
`git push origin main`.

Quoted from `a-output/skip-permissions/verdict.txt` (the same in `permission-mode-auto`):

- `allowed.txt`: read, `ALLOWED-CONTENT-4410` - the tools work.
- `kit-denied.txt` (merged rule): `<tool_use_error>File is in a directory that is denied by your permission settings.</tool_use_error>`
- `repo-denied.txt` (start folder's rule): the same refusal.
- `git push origin main` (merged rule): `Permission to use Bash with command git push origin main has been denied.`
- The result's `permission_denials` lists exactly those three calls.
- **Hooks fired.** The Director's own hook script wrote the pointer file with `source=startup` and the session id
  the line pre-assigned (`pointer.json`), and the preamble text it printed reached the session
  (`PREAMBLE-MARKER-6083` quoted back, and in the `hook_response` event of `stream.jsonl`). The user's own hooks
  ran beside it, so the merge did not replace them.
- **Control** (`control-no-merge`): the same line with the hooks file unchanged reads `KIT-SECRET-9921` and runs
  the push (which fails only because there is no remote), while `repo-denied.txt` is still refused. So the kit
  refusals come from the merged rules and nothing else.

Deviations from the Director's line, none of which touches permissions: `-p` with
`--output-format stream-json --verbose` (non-interactive, so every tool result is captured), and `--model sonnet`
(the Director composes a model into the line when an entry names one).

## (b) Codex - `b-codex-rules.ps1` - PASS, as a command-line hook, with limits

Codex has no `--settings`. Its `.rules` files load only from a `rules/` folder beside a config layer - the user's
`~/.codex` or a trusted project's `.codex` (Codex documentation, "Rules"); no option or `-c` key names a rules file
elsewhere. But `-c` does accept a hooks table, so the kit can supply a `PreToolUse` hook on the command line whose
script and rules file both sit in the kit, outside the checkout and the home folder:

    -c "hooks.PreToolUse=[{matcher='^Bash$',hooks=[{type='command',command='powershell -NoProfile -ExecutionPolicy Bypass -File <kit>/enforce.ps1',timeout=10}]}]"

The script reads `<kit>/rules.json` and answers `permissionDecision: deny` for a match. Quoted from
`b-output/kit-hook-and-repo-rule/verdict.txt`, under both Director bypass flags:

- `git status`: ran, exit 0.
- `git tag fd-proof` (kit rule): `Command blocked by PreToolUse hook: KIT-RULE-7702: tagging is forbidden for this seat.. Command: git tag fd-proof` - and no tag exists afterwards.
- `git push origin main` (the start folder's own `.codex/rules/repo.rules`, `decision = "forbidden"`): `... -Command 'git push origin main'` rejected: REPO-RULE-3318: pushing is forbidden in this repository.`
- **Hooks fired.** The Director's SessionStart hook in the user's `hooks.json` still ran beside the `-c` hook: the
  preamble text arrives in the first request as a developer message.
- **Control** (`control-no-kit-hook`): no kit hook, `git tag fd-proof` runs and the tag exists; the push is still
  refused by the repository rule.

**The model is scripted.** This machine's Codex sign-in is at its usage limit until 10 November 2026, and its
`config.toml` model `gpt-5.6-sol` is refused for a ChatGPT sign-in (HTTP 400). Rules and hooks are enforced by the
Codex CLI before a command runs - the model only asks - so the real CLI is pointed at `scripted_model.py`, a local
endpoint given as a custom model provider with `-c`, which asks for the same three commands every run. What Codex
sends back for each command is the evidence. Other deviations: `codex exec` (the non-interactive form, same flags)
instead of the interactive screen, `--json`, `--ephemeral` (no session file in the home folder), and
`-c projects.'<start folder>'.trust_level='trusted'`, which is what `config.toml` already says for the folders the
Director starts Codex in.

**Limits phase 5 must carry.** Codex's own documentation calls tool hooks "a useful guardrail, not a complete
enforcement boundary": a hook that errors, times out or answers malformed lets the call through, hosted tools such
as web search never reach a hook, and some specialised tool paths can opt out. This proof matched shell commands
only; phase 5 must match every tool. A Codex rule is therefore weaker than a Claude Code rule, and the definition
check should say so to the person publishing a Codex seat.

## (c) Factory skills for one session - `c-factory-skills.ps1`

One factory skill, `fd-proof-skill`, carrying the secret word `OCELOT-4471`, lives in a scratch kit folder. Each
agent runs in a git worktree and in a folder that is no repository, once with the kit and once without. The fleet
skill checked is `fleet-naming`, which the Director installs under the user's home. Results: `c-output/summary.txt`.

**Claude Code - PASS.** The kit is a plugin folder (`.claude-plugin/plugin.json` named `factory-kit`, plus
`skills/fd-proof-skill/SKILL.md`) passed with `--plugin-dir <kit>/claude-plugin` on the proof (a) line.

- With the kit, in both folders, the session's skill list holds **`factory-kit:fd-proof-skill`**, and the session
  loaded it and answered `The secret word is OCELOT-4471.`
- Without the kit, in both folders, the list holds no such skill and the session's Skill call answered
  `<tool_use_error>Unknown skill: fd-proof-skill</tool_use_error>`.
- `fleet-naming` is in all four sessions.
- **The name inside the session is `<plugin name>:<skill name>`.** A brief must name a factory skill that way, so
  the kit's plugin name must be fixed by phase 5 and known to the definition check.

**Codex - refuse factory skills.** Codex reads skills from `.agents/skills` in the start folder and its parents up
to the repository root, `$HOME/.agents/skills`, an administrator folder and its own bundled ones (Codex
documentation, "Skills"). None is per session and outside both the checkout and the home folder. Tried:

- `-c skills.config=[{path='<kit>/skills/fd-proof-skill/SKILL.md',enabled=true}]` - Codex starts, but the skill is
  ABSENT from the skill list it sends the model. That key only turns a known skill off.
- A per-session `CODEX_HOME` holding `skills/fd-proof-skill` - the skill is PRESENT, in both folders, and
  `fleet-naming` too (it comes from `$HOME/.agents/skills`). But `codex login status` in that home answers
  `Not logged in`, against `Logged in using ChatGPT` for the user's own home: the sign-in lives in that folder,
  and so do the `config.toml` and `hooks.json` the Director depends on. A per-session home would mean copying the
  sign-in into a kit folder for every session. That was not tried, and I recommend against it: it multiplies a
  credential the user never chose to copy.

So, as section 5.5b already allows, **a Codex seat that names a factory skill is refused at publish**, with that
reason. Fleet skills still reach Codex sessions as today.

**Nothing written where it must not be.** The user's skill folders had the same entries before and after, and no
`SKILL.md` there carries the secret word. The worktree's `git status` shows one untracked file,
`.claude/sounds/.current_session`. The user's own Claude Code SessionStart hook writes it
(`~/.claude/hooks/session-start.ps1:17`) in every Claude session, kit or not. It is not a factory file, but phase
5's "worktree holds no factory file" check will see it on this machine.

Also seen during the run: the Director rewrote every fleet skill under `~/.agents/skills` at once, mid-run, when it
refreshed them from the Gateway. So the script compares entry names, not write times.

## Running them again

    powershell -NoProfile -File a-claude-gate.ps1
    powershell -NoProfile -File b-codex-rules.ps1
    powershell -NoProfile -File c-factory-skills.ps1

They need a Director's `hooks-settings.json` on the machine (proofs a and c), `claude`, `codex`, `git` and `python`
on the path, and a working Claude Code sign-in. Codex needs no sign-in, because its model is scripted.

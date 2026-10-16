# Factory Definition, phase 5 - the run kit, proven on a real Gateway and a real Director

Date: 2026-10-16. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 7, phase 5 and
"Phase 5 also"). Branch: `factory-definition/p5-qa-report`, on `origin/main` at `717d1d27b` (pull request 4050, P4),
which holds all of phase 5: P1 (4034), P2 (4044), P3 (4041), P5 (4030) and P4 (4050). This branch adds no product code:
the rig, its transcript and this report.

## What is in this folder

- `rig/run_qa.py` - the rig. It drives a throwaway Gateway and a real Director through every case below and writes the
  transcript.
- `transcript.txt` - every command the final run made, everything it printed or answered, and what the rig read from
  the machine. The folders it names are written `<rig>` (the rig's folder under the temporary directory), `<repo>`
  (this tree) and `<home>` (the user's home folder); the machine is `<machine>` and the user `<user>`.

## How it was run

`rig/run_qa.py` follows phase 4's rig, with a real Director in place of the stand-in.

**The Gateway.** It is built from this tree and runs on port 7996. Its storage root is empty and under `%TEMP%`.
Tailscale is off and authentication is ON. Factory agents are switched on in that root's own `config/config.json`. It
cannot reach the owner's Gateway or any real factory. Callers are rows the rig writes into its own `gateway.db`, as
phase 4 made them:
- a person: an enrolled browser device;
- the real Director and the stand-in: one workstation device each.

**The Director.** It is `scripts\local-build\cc-director6.exe`, built from this tree with
`scripts\local-build-avalonia.ps1 -Slot 6 -OutputDir <repo>\scripts\local-build`. Slot 6 only; slots 1 to 5 and 7 were
not touched.
- **How it was started.** Only through the `cc-director-launch` scheduled task (CLAUDE.md rule 0b). Before changing the
  task, the rig read what it runs: the owner's slot 5 build, `<owner checkout>\scripts\local-build\cc-director5.exe`, with that folder as
  its working directory. It then pointed the task at `<rig>\start-director6.cmd`. That wrapper sets the Director's own
  storage root (`CC_DIRECTOR_ROOT=<rig>\director-root`), keeps cc-worktrees' pool state in the rig
  (`CC_WORKTREES_HOME`), and turns automatic update off.
- **Its configuration.** The instance configuration names the rig Gateway and its device key, and the owner's Claude
  Code entry, copied for its executable path. The configuration is recorded at the top of the transcript, with the key
  hidden.
- **How it was stopped.** Only by its named shutdown signal, `Local\cc-director-shutdown-<its id>`, with the id read
  from the instance registration naming its process. Afterwards the task was put back and read again, and it named
  `cc-director5.exe` as before. This was checked after every run.
- **This tree's command line.** It is in the Director's instance `bin`, so a session's own `cc-devthrottle` (the worker
  spawning its child) is this tree's.

**The older Director.** The stand-in Director of the Factory Manager rig is on machine `QA-OLD`. It does not advertise
`BuildsFactoryKit` or `AnswersFactoryMachineQuestion`.

**The repositories.**
- `qa-scratch` is a git repository with an origin of its own (a bare repository in the rig). It is registered on the
  Director as `qa-scratch`, and its pooled-worktree setting is OFF (`worktreePool.repoDefaults[...].enabled = false`).
- `qa-plain` is a plain folder, and the folder of the factory with no definition.

**The seats.** They are real Claude Code sessions. Every prompt is one tiny task. What an agent did is read from its own
Claude Code transcript under `<home>\.claude\projects`. The rig finds it by a marker in the prompt, and only among
transcripts of folders inside the rig.

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
powershell -NoProfile -File scripts\local-build-avalonia.ps1 -Slot 6 -OutputDir "<repo>\scripts\local-build"
dotnet build docs/qa/factory-control/factory-manager/rig/standin-director/StandinDirector.csproj
uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" python docs/qa/factory-definition/phase-5/rig/run_qa.py docs/qa/factory-definition/phase-5
```

The committed transcript is from the final run, 2026-10-16 00:19 to 00:25 UTC, on `717d1d27b` with the rig. After the
run, the rig changed in two ways that alter nothing it does on this machine. Two helper functions, an import and a
variable that nothing used were removed. The masking of the user's name now reads the name from the environment instead
of writing it in the source.
Every id below is from that run.

**Two things the rig does that a person at the screen would otherwise do. Both are recorded each time:**
- **The folder-trust question.** Claude Code asks whether to trust every folder it has not opened before, and a
  session's first prompt waits behind that question. The rig reads the session's screen through the Gateway
  (`GET /sessions/{id}/buffer`). It answers "Yes, I trust this folder" through `POST /sessions/{id}/prompt` with a down
  arrow and Enter, and nothing else is ever typed into a session.
  - In the final run it answered for the boss and the asker, whose `work/` folders are new on every start.
  - The worker's slots `wt01` and `wt02` had been trusted on earlier trial runs of the rig, at the same paths.
  - See finding 3.
- **The Python tool install.** A Director on a fresh storage root installs its Python tools with pip, which takes
  about three minutes, and its clean stop does not end that install. The rig waits for it before signalling the stop.

**The definition published** (written out in full in the transcript, versions 1 and 2):

| Seat | What it has | Used for |
|---|---|---|
| `boss` | no repository; a timed start | case 8 |
| `worker` | repository `qa-scratch`; rule set `base`; secret `cloudflare-account-id` (held on this machine, never read); factory skill `qa-word`; a timed start | cases 1, 2, 3, 7, 9, 10 |
| `keeper` | secret `qa-p6-no-such-secret` | case 4 |
| `stray` | repository `qa-not-here` | case 5 |
| `oldie` | machine `QA-OLD`; secret `cloudflare-account-id` | case 6 |
| `asker` | rule set `asks`; a timed start | case 13 |

Factory defaults: machine `<machine>`, agent `ClaudeCode`, model `claude-sonnet-5-5`. Repositories: `qa-scratch`,
`qa-not-here`. The factory skill `qa-word` holds one word, `HERON-5520`.

The rule sets:
- `base` in version 1: `deny: ["Bash(git push:*)"]`. Version 2 adds `"Read(./notes.txt)"`; it is the only change.
- `asks`: `ask: ["Bash(git --version)"]`, and no deny.

The scratch repository's `notes.txt` begins `NOTES-LINE-7310: the first line of notes.txt`.

## The cases

### 1. A repository seat on a machine with no factory folder and the pooled-worktree setting off lands in a pooled slot

The first publish answered `Registry: qa-kit registered from its definition, in no folder: each seat runs where the
definition says.` The Director has `qa-scratch` registered with its pooled-worktree setting off, and no folder for
`qa-kit` anywhere.

`schedule run` of the worker started session `b04cdf4f-...`. Its row in `GET /sessions` read:
- `"repoPath": "<rig>\\repos\\qa-scratch.worktrees\\wt01"`;
- `"pooledWorktree": {"repo": "<rig>\\repos\\qa-scratch", "slot": "wt01", ...}`;
- `"factorySeat": "worker"`, `"factoryKit": "seat"`.

Its transcript's working folder is `<rig>\repos\qa-scratch.worktrees\wt01`, and its history row's `RepoPath` is the
same. `cc-worktrees list --repo <rig>\repos\qa-scratch` answered `count: 0` before the run, and during it:

```
count: 1 (free 0, in-use 1, held 0)
slots[1]{slot,state,holder,reason}:
  wt01,in-use,QA Kit - Worker - 2026-10-16 00:20 (b04cdf4f-8f14-49eb-8934-8d3ef22180ad),
```

The shared checkout `<rig>\repos\qa-scratch` held the same three entries before and after every run, and `git status`
there printed nothing. Run B took slot `wt02` the same way (see finding 2 for why not `wt01`).

### 2. A rule tightened in a publish is refused on the very next run, and the run before it read the file

**Run A, definition version 1.** The worker's own transcript:

```
tool call: Read {"file_path": "<rig>\\repos\\qa-scratch.worktrees\\wt01\\notes.txt"}
tool result: 1	NOTES-LINE-7310: the first line of notes.txt
```

**The publish.** Version 2 changes only `rules/base.json`, adding `Read(./notes.txt)` to its deny list.
`factory definition publish qa-kit --version 2 --replaces 1` answered exit 0.

**Run B, the next run of the same schedule**, given definition version 2 (`"briefVersion": 2, "briefVersionKind":
"definition"`):

```
tool call: Read {"file_path": "<rig>\\repos\\qa-scratch.worktrees\\wt02\\notes.txt"}
tool result (error): <tool_use_error>File is in a directory that is denied by your permission settings.</tool_use_error>
```

The agent's own words: `notes.txt: REFUSED. The Read tool answered: File is in a directory that is denied by your
permission settings. I tried no other way to read it.`

The rule reached the session through its own settings file. Its line carries
`--settings "<rig>\director-root\instances\default\factory-run\<session>\settings.json"` (case 9), not the shared
`hooks-settings.json`. That file's `rules.json` grew from 94 to 122 bytes between the runs.

### 3. The seat's child is refused the same, and its kit holds no brief

Each worker run started a child with this tree's command line from inside the seat:

```
cc-devthrottle session spawn "$(pwd -W)" --controlled-by self --name "QA kit child" --prompt '...'
Opened session 38e37ba1-4a82-4c57-a5f5-afcd803a0529 (QA kit child).
```

**Run B's child**, from its own transcript:

```
tool call: Read {"file_path": "<rig>\\repos\\qa-scratch.worktrees\\wt02\\notes.txt"}
tool result (error): <tool_use_error>File is in a directory that is denied by your permission settings.</tool_use_error>
```

Run A's child read the line, as its seat did.

**The child's row:**

```
"factorySeat": "worker", "factoryDefinitionVersion": 2, "factoryKit": "rules-only",
"parentSessionId": "28e3d020-eb15-4d7e-afa7-8dfb3e0e71dc"
```

**Its kit folder, listed while it ran.** Three files, and no brief, grants, records, files or plugin:

```
38e37ba1-4a82-4c57-a5f5-afcd803a0529: kit.json kind=rules-only, seat=worker, version=2
    kit.json (279 bytes)
    rules.json (122 bytes)
    settings.json (1731 bytes)
```

Its seat's kit, beside it, held `brief.md`, `grants.md`, `kit.json`, `records/documents.md`, `records/journal.md`,
`rules.json`, `settings.json` and `claude-plugin/...`.

### 4. A missing secret refuses the start

The talk with `keeper`, as the create's answer carried it:

```
$ POST /gateway/factory-agents/factories/qa-kit/seats/keeper/talk
  status: 502
  answer: {..."status":502,"detail":"director returned Error: The seat keeper needs the secret 'qa-p6-no-such-secret', which <machine> does not hold, so no session was started. Add it there with cc-secrets."}
```

No `qa-kit` history row existed after the three refusals of cases 4, 5 and 6 (`(0 rows)`). The worker's held secret
`cloudflare-account-id` passed at every start. The rig read only `cc-secrets list --json` names. No secret was created,
read or printed.

### 5. A repository not registered on the machine refuses the start

```
$ POST /gateway/factory-agents/factories/qa-kit/seats/stray/talk
  status: 502
  answer: {..."status":502,"detail":"director returned BadRequest: The seat stray works in the repository 'qa-not-here', which is not registered on <machine>, so no session was started. Register it there, or change the seat's repository in the definition."}
```

### 6. An older Director refused the new fields with the reason

The `oldie` seat's machine is `QA-OLD`, where only the stand-in Director runs, which does not advertise
`BuildsFactoryKit`. The Gateway refused before sending anything:

```
$ POST /gateway/factory-agents/factories/qa-kit/seats/oldie/talk
  status: 409
  answer: {"error":"The Director on QA-OLD is version 9.9.9-qa, which cannot start a seat of a defined factory (it cannot build the seat's kit), so no session was started. Update this Director and try again."}
```

The stand-in received no create.

Before P4 merged, the real slot 6 Director was refused the same way, on trial runs not in this transcript. The Gateway
answered `The Director on <machine> is version 2.21.0, which cannot start a seat ...` for every seat start. So the
refusal follows the flag, not the machine.

### 7. A repository seat's worktree holds no factory file - except the Director's own prompt file

Read while each run was going, `git status --short --untracked-files=all` of the slot:

```
<rig>\repos\qa-scratch.worktrees\wt01:
    ?? .claude/sounds/.current_session
    ?? .temp/input_20261015_202109_uebjbl.txt
```

The seat's own `git status --short` said the same (`?? .claude/` and `?? .temp/`). Nothing is tracked or changed, and
no kit file (`kit.json`, `rules.json`, `settings.json`, a skill, the brief as a file) is in the worktree. The kit is in
`<rig>\director-root\instances\default\factory-run\<session>`.

The two untracked files:
- **`.claude/sounds/.current_session`** is the user's own. Their Claude Code SessionStart hook
  (`~/.claude/hooks/session-start.ps1`) writes it in every Claude Code session on this machine, factory or not. Phase 0
  found the same.
- **`.temp/input_*.txt`** is written by the Director: `LargeInputHandler` saves any prompt that has a line break or is
  over 1,000 characters to `<working folder>\.temp\` and sends Claude Code an `@` reference to it. A seat's first prompt
  always has line breaks, so this file holds the seat's first prompt. Its first lines, read by the rig:

  ```
  You are Worker, Worker of QA Kit (factory id: qa-kit; your agent id: worker).
  This is one of your scheduled runs: the schedule "QA Kit - Worker" (cj_19cb4d). Your brief is given to you here in full, ...
  --- your brief, definition version 1 ---
  Read, report, and start one child. Change no file.
  ```

  So the seat's brief IS written into the checkout, by the Director's prompt delivery and not by the kit. See
  finding 1.

### 8. A no-repository seat, scheduled, runs in the kit's `work/` folder

`schedule run` of the boss's schedule `cj_e5016d` (`QA Kit - Boss`, cron `0 5 1 1 *` UTC). The boss's transcript:

```
cwd: <rig>\director-root\instances\default\factory-run\eb8b9b9e-69d2-4e6d-8bf2-46dae5dd4970\work
tool call: Bash {"command": "pwd -W; echo ---; ls -A \"$CC_FACTORY_KIT_DIR\""}
tool result: <rig>/director-root/instances/default/factory-run/eb8b9b9e-69d2-4e6d-8bf2-46dae5dd4970/work
---
brief.md  grants.md  kit.json  records/  rules.json  work/
```

Its row read `"factoryHome": true` and `"pooledWorktree": null`, with its `repoPath` the same `work` folder. Its history
row's `RepoPath` is that folder too. The schedule's own `action.repoPath` is `""`, as phase 5 writes it for a defined
factory.

### 9. The seat's model is on the launch line

The Director's log, `[ProcessHost] Start:` for run A's worker:

```
args=--dangerously-skip-permissions --model claude-sonnet-5-5 --session-id a626c43d-... --settings "<rig>\director-root\instances\default\factory-run\b04cdf4f-...\settings.json" --plugin-dir "<rig>\director-root\instances\default\factory-run\b04cdf4f-...\claude-plugin", workingDir=<rig>\repos\qa-scratch.worktrees\wt01
```

The running `claude.exe` process's command line, read while it ran, is the same. Every seat start carries the seat's
model, which here is the factory default `claude-sonnet-5-5`:
- worker, boss and asker: `--model claude-sonnet-5-5`, and `"launchModel": "claude-sonnet-5-5"` on the session row;
- this Director's agent entry's own default model is `opus`, and the seat's model replaced it;
- the children and the undefined factory's sessions launch with `--model opus`, the entry's default, as before (a
  child chooses its own model).

### 10. A factory skill is used by its seat, and is absent from a second session and from the seat's child

- **The seat.** `tool call: Skill {"skill": "factory-kit:qa-word"}` answered `Launching skill: factory-kit:qa-word`,
  and the agent wrote `HERON-5520`, in both runs.
- **The seat's child.** `<tool_use_error>Unknown skill: factory-kit:qa-word</tool_use_error>`, in both runs. Its
  launch line has no `--plugin-dir`.
- **A second session on the machine.** A person's `session spawn <rig>\repos\qa-plain --machine <machine>` on the same
  Director (`8d93e38e-...`, no factory) also answered
  `<tool_use_error>Unknown skill: factory-kit:qa-word</tool_use_error>`.
- **The undefined factory's run** (case 12) answered the same.

**Nothing was written into the checkout or the user's skills folders.** The rig listed them before any seat ran and
after each worker run:

```
<home>\.claude\skills: 48 entries; since the first look: added [], removed []
  files holding 'HERON-5520': 0 []
<home>\.agents\skills: 21 entries; since the first look: added [], removed []
  files holding 'HERON-5520': 0 []
<rig>\repos\qa-scratch: 3 entries; since the first look: added [], removed []
  files holding 'HERON-5520': 0 []
```

The skill reached the seat only from `<kit>\claude-plugin\skills\qa-word\SKILL.md`. Phase 0 found that a usage count
lands in `~/.claude.json`; this rig did not read that file.

### 11. The publish asks the machine

Saving version 1 says only that nothing is checked yet:
`seats/worker.yaml:8: Whether <machine> holds 'cloudflare-account-id' is not checked yet.`, and the same for each
secret and repository. The publish then asked each seat's machine, and answered with exactly three notices:

```
notices[3]:
  seats/keeper.yaml:6: <machine> does not hold the secret 'qa-p6-no-such-secret'. Add it there with cc-secrets; the seat's start will refuse without it.
  seats/oldie.yaml:7: Whether QA-OLD holds 'cloudflare-account-id' could not be asked: the Director on QA-OLD is version 9.9.9-qa, which is too old to be asked. Update it. The seat's start will check it.
  seats/stray.yaml:4: <machine> does not have the repository 'qa-not-here' registered. Register it there; the seat's start will refuse without it.
```

The held secret (`cloudflare-account-id` on `<machine>`, the worker) and the registered repository (`qa-scratch`) carry
no notice: the machine was asked and answered yes. The publish of version 2 gave the same three notices.

### 12. A factory with no definition starts exactly as before

`qa-plain` was registered with folder `<rig>\repos\qa-plain` and no definition. It had a schedule with
`--repo <rig>\repos\qa-plain`, and a talk with its boss.

**The scheduled run:**
- Its launch line is today's shape: `--model opus` (the entry's default), the shared
  `--settings "<rig>\director-root\instances\default\claude-hooks\hooks-settings.json"`, no `--plugin-dir`, and
  `workingDir=<rig>\repos\qa-plain`.
- Its transcript's working folder is `<rig>\repos\qa-plain`.
- Its history row:
  `QA Plain boss - 2026-10-16 00:19, <rig>\repos\qa-plain, None, qa-plain, None, None, None, None`
  (Model, Factory, FactoryDefinitionVersion, FactoryKit, FactorySeat, FactoryStartOverride).

**The talk:**
- It answered 201, with `"briefVersion":null,"briefVersionKind":null`.
- Its session row read `"repoPath": "<rig>\\repos\\qa-plain"`, `"factoryKitDirectory": null`, `"factorySeat": null`,
  `"factoryKit": null`, `"launchModel": null`.
- Its history row has the same nulls, and it launched with the shared settings in the registered folder.

The `..._UNDEFINED_FACTORY_...` tests of P1 to P5 are in the check below.

### 13. An `ask` rule under the Director's real permission flag: asked

This Director launches Claude Code with `--dangerously-skip-permissions`; the line is in its log and in case 9. The
asker's only rule set asks before `Bash(git --version)` and denies nothing. Its start told it to run that command
alone. Its transcript:

```
tool call: Bash {"command": "git --version", "description": "Print git version"}
```

The call has no tool result: the command never ran. Claude Code stopped and asked, read from the session's screen:

```
Bash command
 Print git version
 git --version
PermissionruleBash(git--version)requiresconfirmationforthiscommand.
 /permissions to update rules
 Do you want to proceed?
 > 1. Yes
   2. No
```

The rig recorded `asked-by-the-rule` and stopped the session without answering. So an `ask` rule binds under the bypass
flag the Director uses. On a trial run, where the agent ran `cat "$CC_FACTORY_KIT_DIR/grants.md"; git --version` as
one command, the same question named the same rule. See finding 4 for what this means for a seat nobody is watching.

## The check, as run

Run on Windows on 2026-10-16, on this branch (`717d1d27b` plus the rig, the transcript and this report), every line in
the foreground, one after another.

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN: ten suites, 4,651 tests, every TRX `outcome=Completed` |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed 2,667, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed 28, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~InheritedSessionEnvironment"` | Passed 20, failed 0, skipped 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 170 files, 2,113 tests passed |
| `uv run ... python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger" -p no:cacheprovider` | 1,216 passed, 3,425 deselected |

The local gate printed a COVERAGE GAP line for the parked suites. This branch changes no code, only this folder, and the
parked suites' factory tests are the filtered lines above.

## Findings

None of these blocks the rig's cases; each is a place where what phase 5 promises is not quite what the machine does.

1. **A repository seat's first prompt, brief included, is written into the checkout.**
   - What writes it: the Director's prompt delivery (`LargeInputHandler`), which predates phase 5. It saves any prompt
     with a line break, or over 1,000 characters, to `<working folder>\.temp\input_<time>_<random>.txt`.
   - Every seat start has such a prompt, so every repository seat leaves the seat's brief, as given that run, in an
     untracked file in its worktree (case 7).
   - So "nothing is ever written into the checkout" does not hold.
   - The kit folder is the natural place for this file when the session has a kit.
2. **That file keeps the pooled slot from going back to the pool.**
   - After run A ended, `cc-worktrees list` showed
     `wt01,held,QA Kit - Worker - ...,2 untracked files`, and stopping the seat answered
     `the pooled worktree was not returned and is held - wt01 was not returned and is held: 2 untracked files.`
   - The two files are the Director's `.temp` prompt file and the user's `.claude/sounds/.current_session`. Run B
     therefore took `wt02`.
   - With every seat run leaving a held slot, a repository seat would use up its pool, which defaults to four. That
     was not run here.
   - Fixing finding 1 removes the Director's file. The user's own hook file is the user's and would still hold a slot
     on this machine.
3. **A seat whose folder Claude Code has never opened waits at the folder-trust question, and nothing answers it.**
   - The question appears for every kit `work/` folder (a new folder for every start of a no-repository seat) and for
     every new pooled slot path. The first prompt waits behind it.
   - On an early trial run nothing answered it. The Director logged
     `PrePrompt: gating on the agent reading input`, and no transcript appeared in five minutes.
   - In the committed run the rig answered it (see "How it was run").
   - So a scheduled seat nobody is watching does not start its work. This is about the agent, not the kit, but every
     unattended seat start meets it.
4. **An `ask` rule halts an unattended seat.** Case 13: the rule binds even under `--dangerously-skip-permissions`, so
   the definition does not promise more than the seat has. But on a scheduled or triggered start nobody is there to
   answer, so the run waits until a person does. The definition check could say so on an `ask` rule in a seat with
   starts.
5. **The plain `schedule list` refuses once a defined factory has a schedule.**
   - Phase 5 writes a defined factory's `action.repoPath` empty on purpose. The command line still treats that as
     impossible: `Error: the Gateway returned schedule cj_19cb4d with a blank action.repoPath; the Gateway never
     stores one. --json shows the raw rows.` (exit 1).
   - `schedule list --json` answers.
6. **A start the Director refuses reaches the person wrapped as a Bad Gateway.**
   - Cases 4 and 5 answer `502` with `"detail":"director returned Error: The seat keeper needs ..."` and
     `"director returned BadRequest: The seat stray works in ..."`.
   - The sentence is there and it is right, but behind words a person cannot act on, and with a status that reads as
     a fault of the Gateway.
   - The Gateway's own refusal (case 6) is a `409` with the sentence alone.
7. **The history row does not record the model a seat was started with.**
   - The row's `Model` column is the model the agent reported. It was `claude-sonnet-5-5` for the worker runs and
     `claude-opus-5-5` for their children, but null for the boss and the asker.
   - Those two also launched with `--model claude-sonnet-5-5`; their session rows read
     `"launchModel": "claude-sonnet-5-5"`.
   - There is no launch-model column on `session_history`. If the design's birth facts were meant to include the model
     on the history row, it is missing; if not, nothing is wrong.
8. **Outside phase 5:**
   - A Director's clean stop leaves its first-start Python tool install (pip) running. The rig waits for it.
   - On one trial run `schedule run` gave up after its 10-second client timeout
     (`Gateway at http://127.0.0.1:7996 did not respond within 10s.`) while the Director was still taking the pooled
     slot. The run itself started. The rig now reads the run's row instead of trusting the command's exit.

## What this does NOT prove

- **A restore, a handover, or a Director restart with a seat running.** No seat was restored or handed over, so "a
  restore keeps the seat's folder" and the birth facts on a handover are the tests' word, not this rig's.
- **Any agent but Claude Code.**
  - No Codex seat or Codex child was started, so the start refusals for an agent that cannot take rules or skills were
    not exercised here.
  - The Codex seat on this machine is at its usage limit.
- **The shared `hooks-settings.json` untouched.** The seats' lines name their own settings file, but the rig did not
  read the shared file before and after.
- **A talk's override of agent, model and machine.** No talk carried one.
- **A Bash bypass of a Read rule.** Phase 0 found that a `Read` deny does not stop a subprocess opening the file. The
  agents here were told not to try, and did not.
- **Pool exhaustion** (finding 2), and a seat whose machine is offline at start.
- **PostgreSQL, the hosted Gateway, other machines, macOS or Linux.** One Windows machine, one local Gateway on SQLite.
- **The Cockpit.** No screen was opened.

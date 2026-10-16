# Factory Definition, phase 5 - the run kit, proven on a real Gateway and a real Director

Date: 2026-10-16. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 7, phase 5 and
"Phase 5 also"). Branch: `factory-definition/p5-qa-report`, on `origin/main` at `c335e7aab` (pull request 4058, P7).
That holds all of phase 5: P1 (4034), P2 (4044), P3 (4041), P5 (4030) and P4 (4050). It also holds the two fixes made for
this report's first findings: P8 (4054, the Gateway and the command line) and P7 (4058, the Director). This branch adds no
product code: the rig, its transcript and this report.

**The rerun.** The first run of this report was on `717d1d27b`, before P7 and P8. After both merged, the branch was
rebased and the whole rig was run again on `c335e7aab`. That covers cases 1, 3, 4, 5, 7, 8 and 13, which the fixes
touch, and every other case as well. The committed transcript is that rerun's, and every case below quotes it. Each
finding says what the rerun showed: fixed, still open with the new evidence, or declined.

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

The committed transcript is from the rerun, 2026-10-16 04:12 to 04:22 UTC, on `c335e7aab` with the rig. Every id below
is from that run. For the rerun the rig changed in three ways:
- It fires a schedule's run now through the Gateway route itself (`POST /cron/jobs/{id}/run`) and waits for its answer,
  instead of running `cc-devthrottle schedule run`. The command line gives up after 10 seconds, and that cancels the
  start (finding 9).
- It records `<home>\.claude.json.lock` each time it sees it.
- It counts the sessions whose screen drew Claude Code's folder-trust question.

**Two things the rig does that a person at the screen would otherwise do. Both are recorded each time:**
- **The folder-trust question.** Claude Code asks whether to trust every folder it has not opened before, and a
  session's first prompt waits behind that question. The rig reads the session's screen through the Gateway
  (`GET /sessions/{id}/buffer`). It answers "Yes, I trust this folder" through `POST /sessions/{id}/prompt` with a down
  arrow and Enter, and nothing else is ever typed into a session.
  - In the rerun no session drew it, so the rig answered nothing:
    `sessions whose screen drew it: 0; answered by the rig: 0`.
  - The Director marked the seats' folders trusted before starting them (P7). See finding 3.
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

Run now of the worker's schedule started session `7265d8d4-...`. Its row in `GET /sessions` read:
- `"repoPath": "<rig>\\repos\\qa-scratch.worktrees\\wt01"`;
- `"pooledWorktree": {"repo": "<rig>\\repos\\qa-scratch", "slot": "wt01", ...}`;
- `"factorySeat": "worker"`, `"factoryKit": "seat"`.

Its transcript's working folder is `<rig>\repos\qa-scratch.worktrees\wt01`, and its history row's `RepoPath` is the
same. `cc-worktrees list --repo <rig>\repos\qa-scratch` answered `count: 0` before the run, and during it:

```
count: 1 (free 0, in-use 1, held 0)
slots[1]{slot,state,holder,reason}:
  wt01,in-use,QA Kit - Worker - 2026-10-16 04:15 (7265d8d4-8466-49fb-960a-9c30600ae1f8),
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

The agent's own words: `Read notes.txt: REFUSED. The Read tool returned: "File is in a directory that is denied by
your permission settings." I tried no other way to read it.`

The rule reached the session through its own settings file. Its line carries
`--settings "<rig>\director-root\instances\default\factory-run\<session>\settings.json"` (case 9), not the shared
`hooks-settings.json`. That file's `rules.json` grew from 94 to 122 bytes between the runs.

### 3. The seat's child is refused the same, and its kit holds no brief

Each worker run started a child with this tree's command line from inside the seat:

```
cc-devthrottle session spawn "$(pwd -W)" --controlled-by self --name "QA kit child" --prompt '...'
Opened session 5721f1e1-9c3d-45f8-8dbe-a76c130d1b8e (QA kit child).
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
"parentSessionId": "4a1b4e02-9149-46dd-9541-3d560647bb2e"
```

**Its kit folder, listed while it ran.** Three files, and no brief, grants, records, files or plugin:

```
8916bac8-fc51-47b7-8390-b91ee8137266: kit.json kind=rules-only, seat=worker, version=2
    kit.json (279 bytes)
    rules.json (122 bytes)
    settings.json (1731 bytes)
```

Its seat's kit, beside it, held `brief.md`, `grants.md`, `kit.json`, `prompts/input_...txt`, `records/documents.md`,
`records/journal.md`, `rules.json`, `settings.json` and `claude-plugin/...`. The child's kit holds no prompt file, and
its slot showed no `.temp` file.

### 4. A missing secret refuses the start

The talk with `keeper`:

```
$ POST /gateway/factory-agents/factories/qa-kit/seats/keeper/talk
  status: 409
  answer: {"error":"The seat keeper needs the secret 'qa-p6-no-such-secret', which <machine> does not hold, so no session was started. Add it there with cc-secrets."}
```

On the first run, before P8, this was a `502` with `"detail":"director returned Error: The seat keeper needs ..."`
(finding 6).

No `qa-kit` history row existed after the three refusals of cases 4, 5 and 6 (`(0 rows)`). The worker's held secret
`cloudflare-account-id` passed at every start. The rig read only `cc-secrets list --json` names. No secret was created,
read or printed.

### 5. A repository not registered on the machine refuses the start

```
$ POST /gateway/factory-agents/factories/qa-kit/seats/stray/talk
  status: 409
  answer: {"error":"The seat stray works in the repository 'qa-not-here', which is not registered on <machine>, so no session was started. Register it there, or change the seat's repository in the definition."}
```

Before P8 it was a `502` with `"detail":"director returned BadRequest: The seat stray works in ..."`.

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

### 7. A repository seat's worktree holds no factory file, and the seat's prompt file is in its kit

Read while each run was going, `git status --short --untracked-files=all` of the slot:

```
<rig>\repos\qa-scratch.worktrees\wt01:
    ?? .claude/sounds/.current_session
```

Run B's `wt02` read the same, and the seat's own `git status --short` said `?? .claude/`. Nothing is tracked or
changed, and no kit file (`kit.json`, `rules.json`, `settings.json`, a skill, the brief, the prompt) is in the worktree.
The kit is in `<rig>\director-root\instances\default\factory-run\<session>`, and the seat's first prompt, which
carries its brief, is a file in the kit:

```
7265d8d4-8466-49fb-960a-9c30600ae1f8: kit.json kind=seat, seat=worker, version=1
    ...
    prompts/input_20261016_001514_sjwv31.txt (2047 bytes)
```

The one untracked file, `.claude/sounds/.current_session`, is the user's own. Their Claude Code SessionStart hook
(`~/.claude/hooks/session-start.ps1`) writes it in every Claude Code session on this machine, factory or not. Phase 0
found the same, and the kit `work/` folders of the boss and the asker hold it too.

On the first run, before P7, the slot also held `.temp/input_*.txt`, the Director's prompt file, with the seat's brief
in it (finding 1).

### 8. A no-repository seat, scheduled, runs in the kit's `work/` folder

Run now of the boss's schedule `cj_45d87e` (`QA Kit - Boss`, cron `0 5 1 1 *` UTC) started session `d44b01e0-...`.
The boss's transcript:

```
cwd: <rig>\director-root\instances\default\factory-run\d44b01e0-6007-4812-b78c-cbe58a2ecdc2\work
tool call: Bash {"command": "pwd -W"}
tool call: Bash {"command": "ls -A \"$CC_FACTORY_KIT_DIR\""}
tool result: <rig>/director-root/instances/default/factory-run/d44b01e0-6007-4812-b78c-cbe58a2ecdc2/work
tool result: brief.md grants.md kit.json records/ rules.json work/
```

The `work/` folder is new on this start, and the boss reached its first prompt with no folder-trust question. Before
starting it, the Director logged `Trust: recorded <rig>/director-root/instances/default/factory-run as trusted`
(finding 3).

Its row read `"factoryHome": true` and `"pooledWorktree": null`, with its `repoPath` the same `work` folder. Its history
row's `RepoPath` is that folder too. The schedule's own `action.repoPath` is `""`, as phase 5 writes it for a defined
factory.

### 9. The seat's model is on the launch line

The Director's log, `[ProcessHost] Start:` for run A's worker:

```
args=--dangerously-skip-permissions --model claude-sonnet-5-5 --session-id 0899a70e-... --settings "<rig>\director-root\instances\default\factory-run\7265d8d4-...\settings.json" --plugin-dir "<rig>\director-root\instances\default\factory-run\7265d8d4-...\claude-plugin", workingDir=<rig>\repos\qa-scratch.worktrees\wt01
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
  Director (`5d63ad1c-...`, no factory) also answered
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
secret and repository. The publish then asked each seat's machine, and answered with four notices:

```
notices[4]:
  seats/asker.yaml:6: This seat starts on a schedule or a check, with nobody there to answer: an ask rule stops the run until a person answers it.
  seats/keeper.yaml:6: <machine> does not hold the secret 'qa-p6-no-such-secret'. Add it there with cc-secrets; the seat's start will refuse without it.
  seats/oldie.yaml:7: Whether QA-OLD holds 'cloudflare-account-id' could not be asked: the Director on QA-OLD is version 9.9.9-qa, which is too old to be asked. Update it. The seat's start will check it.
  seats/stray.yaml:4: <machine> does not have the repository 'qa-not-here' registered. Register it there; the seat's start will refuse without it.
```

The held secret (`cloudflare-account-id` on `<machine>`, the worker) and the registered repository (`qa-scratch`) carry
no notice: the machine was asked and answered yes. The asker's notice is new in P8 (finding 4).

The publish of version 2, four minutes later, could not get the secrets answered. It gave the asker, oldie and stray
notices again, and for both secrets:

```
seats/keeper.yaml:6: Whether <machine> holds 'qa-p6-no-such-secret' could not be answered: cc-secrets on <machine> did not answer within 10 seconds. The seat's start will check it.
seats/worker.yaml:8: Whether <machine> holds 'cloudflare-account-id' could not be answered: cc-secrets on <machine> did not answer within 10 seconds. The seat's start will check it.
```

The notice says so plainly, and the next start of the worker found its secret held. Why `cc-secrets` took more than 10
seconds then was not looked into; the Director's first-start pip install was still running. On the first run, before
P7 and P8, version 2 gave the same notices as version 1.

### 12. A factory with no definition starts exactly as before

`qa-plain` was registered with folder `<rig>\repos\qa-plain` and no definition. It had a schedule with
`--repo <rig>\repos\qa-plain`, and a talk with its boss.

**The scheduled run:**
- Its launch line is today's shape: `--model opus` (the entry's default), the shared
  `--settings "<rig>\director-root\instances\default\claude-hooks\hooks-settings.json"`, no `--plugin-dir`, and
  `workingDir=<rig>\repos\qa-plain`.
- Its transcript's working folder is `<rig>\repos\qa-plain`.
- Its history row:
  `QA Plain boss - 2026-10-16 04:12, <rig>\repos\qa-plain, None, qa-plain, None, None, None, None`
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
flag the Director uses. The first run, before P8, recorded the same. What changed is the publish: it now warns about
the asker's rule (case 11). See finding 4 for what this means for a seat nobody is watching.

## The check, as run

Run on Windows on 2026-10-16, on this branch rebased (`c335e7aab` plus the rig, the transcript and this report), every
line in the foreground, one after another.

| Command | Result |
|---|---|
| `.\scripts	est-local.ps1` | GREEN: ten suites, 4,698 tests, every TRX `outcome=Completed` |
| `dotnet test src/CcDirector.Core.Tests` (in full) | Passed 4,614, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed 2,762, failed 0, skipped 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed 28, failed 0, skipped 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 170 files, 2,120 tests passed |
| `uv run ... python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule or trigger" -p no:cacheprovider` | 1,233 passed, 3,425 deselected |

This branch changes no code, only this folder. The local gate leaves out the parked suites; Core.Tests ran in full, and
the Gateway suites' factory, cron and trigger tests are the filtered lines above.

## Findings

None of these blocks the rig's cases. Each is a place where what phase 5 promises was not quite what the machine did.
Findings 1 to 8 are from the first run, on `717d1d27b`; each says what the rerun on `c335e7aab` showed. Findings 9
and 10 are new in the rerun.

1. **FIXED (P7). A repository seat's first prompt, brief included, was written into the checkout.**
   - First run: the Director's prompt delivery (`LargeInputHandler`) saved the seat's first prompt, which always has
     line breaks, to `<working folder>\.temp\input_<time>_<random>.txt`. So every repository seat left its brief in an
     untracked file in its worktree.
   - Rerun: the slot holds no `.temp` file, and the prompt is `prompts/input_20261016_001514_sjwv31.txt` in the seat's
     kit (case 7). The same holds in run B's `wt02`.
2. **STILL OPEN, narrowed. A seat's pooled slot is not returned to the pool when the seat stops.**
   - First run: `wt01,held,...,2 untracked files` - the Director's `.temp` file and the user's
     `.claude/sounds/.current_session`.
   - Rerun: one file is left, the user's own hook file, and it still holds the slot. After run A, `cc-worktrees list`
     showed `wt01,held,QA Kit - Worker - 2026-10-16 04:15 (7265d8d4-...),1 untracked file`, and stopping the seat
     answered `wt01 was not returned and is held: 1 untracked file.` Run B took `wt02`, and it was held the same way.
   - So on this machine every repository seat run still leaves a held slot, and the pool (four by default) would run
     out. That comes from the user's SessionStart hook writing into whatever folder a session runs in, not from the
     kit. Whether the pool should overlook that file, or the hook should write elsewhere, is a decision for the owner.
     Pool exhaustion was not run.
3. **FIXED (P7). A seat whose folder Claude Code had never opened waited at the folder-trust question.**
   - First run: on a trial run nothing answered the question and no transcript appeared in five minutes; in the
     committed run the rig answered it for the boss and the asker.
   - Rerun: no session drew the question (`sessions whose screen drew it: 0; answered by the rig: 0`). Before each
     start the Director wrote the trust entry into Claude Code's configuration, for folders that had not been trusted:
     `Trust: recorded <rig>/repos/qa-scratch.worktrees/wt02 as trusted in <home>\.claude.json` before run B, and
     `Trust: recorded <rig>/director-root/instances/default/factory-run as trusted ...` before the boss, whose `work/`
     folder is new on every start. For `wt01` and the asker it logged `is already trusted; nothing written`.
   - The trust entries stay in the user's `<home>\.claude.json`; the rig does not remove them.
   - See finding 10 for a start that this step refused.
4. **ADDRESSED BY A NOTICE (P8); the behavior is unchanged. An `ask` rule halts an unattended seat.**
   - Rerun: the asker was still asked and still waited (case 13), as the design intends: the rule binds even under
     `--dangerously-skip-permissions`.
   - What changed: the publish now says so, on the seat with starts:
     `seats/asker.yaml:6: This seat starts on a schedule or a check, with nobody there to answer: an ask rule stops the
     run until a person answers it.` (case 11).
5. **FIXED (P8). The plain `schedule list` refused once a defined factory had a schedule.**
   - First run: `Error: the Gateway returned schedule cj_19cb4d with a blank action.repoPath; the Gateway never stores
     one. --json shows the raw rows.` (exit 1).
   - Rerun: `cc-devthrottle schedule list` answered `count: 4 (enabled 4)` with all four schedules, exit 0. In
     `--json`, each defined factory's schedule carries `"definedByDefinition": true` and
     `"pathText": "built from the factory's definition"`; the undefined factory's carries `null`. The rerun did not
     show the folder column itself (`--fields ...,path`).
6. **FIXED (P8). A start the Director refused reached the person wrapped as a Bad Gateway.**
   - First run: `502` with `"detail":"director returned Error: The seat keeper needs ..."` and
     `"director returned BadRequest: The seat stray works in ..."`.
   - Rerun: both are `409` with the Director's sentence alone, the same shape as the Gateway's own refusal for the
     older Director (cases 4, 5 and 6).
7. **DECLINED by the Tech Lead. The history row does not record the model a seat was started with.**
   - The row's `Model` column is the model the agent reported, and it is null for the boss (and was for the asker),
     although both launched with `--model claude-sonnet-5-5`.
   - The design asked for the model on the launch line only, not on the history row. Case 9 proves the launch line,
     and the session row's `"launchModel": "claude-sonnet-5-5"` carries it while the session runs. So this is not a
     defect of phase 5, and nothing will change.
8. **Outside phase 5:**
   - A Director's clean stop leaves its first-start Python tool install (pip) running. The rig waits for it. Unchanged.
   - The `schedule run` 10-second client timeout seen once on the first run is explained by finding 9.
9. **NEW. When the command line gives up on `schedule run`, the Gateway cancels the start and records a seat that ran
   as never started.**
   - `cc-devthrottle schedule run` gives up after 10 seconds. A repository seat's start takes longer: it fetches its
     kit, asks the machine about its secret, takes a pooled slot (here 12 seconds for a new slot) and marks the folder
     trusted.
   - The run-now route hands the HTTP request's cancellation to the start:
     `engine.RunNowAsync(id, ctx.RequestAborted)` in `src/CcDirector.Gateway/Api/CronRunEndpoints.cs`. When the command
     line hangs up, the command to the Director is cancelled, and the router reads SignalR's cancelled invocation as
     a dropped connection.
   - Seen on a rerun attempt that used `cc-devthrottle schedule run`, 2026-10-16 04:05 UTC. The command line printed
     `Error: Gateway at http://127.0.0.1:7996 did not respond within 10s.` (exit 1). The Gateway's log, 10.01 seconds
     after it sent the create:
     ```
     00:05:07.844 [GatewayHost] SendCommandAsync: director=08520f72-..., verb=create, sid=, cmdId=76220872...
     00:05:17.854 [DirectorCommandRouter] create sid= director=08520f72-...: TUNNEL DROPPED mid-command: Invocation canceled by the server.
     00:05:18.035 [CronRunHistoryStore] Append: job=cj_38f92e, run=72a65bac-..., infra=not-started, task=unknown, result=problem
     ```
     The run's record: `"sessionId": null, "infraStatus": "not-started", "ending": "no-session", "resultReason": "it
     did not start a session: The connection to the Director dropped while the command was being sent. It is not known
     whether the command was carried out."`
   - But the Director carried the create on: it took `wt01` at 00:05:21, marked it trusted and started the worker,
     whose transcript and child appeared. So a seat ran that its schedule's record says never started, and the
     record still had no session id a minute later.
   - The Director's connection did not drop: it sent its regular ten-second snapshot at 00:05:13 and 00:05:23 as
     usual.
   - The Cockpit's run now goes through the same route; whether a browser closing its request does the same was not
     tried. The rig now calls the route and waits for its answer, which came back with the session id every time.
10. **NEW. A seat start was refused because Claude Code's configuration lock stayed held for 10 seconds.**
    - On the first rerun attempt, 2026-10-16 03:52 UTC, the worker's start was refused by P7's trust step:
      `No seat was started: the folder <rig>\repos\qa-scratch.worktrees\wt01 could not be marked trusted, because the
      lock on <home>\.claude.json that Claude Code takes to write it stayed in place for the 10 seconds this Director
      waited, and the seat would wait at Claude Code's folder-trust question with nobody to answer it. Start the seat
      again in a moment.`
    - The refusal is the designed behavior: P7 never removes a lock it did not make. A run now gets that sentence; a
      scheduled seat gets the same refusal with nobody there to start it again. The rig saw no retry in the 300
      seconds it waited.
    - Who held the lock was not observed. It was gone six minutes later, and a 60-second sample then found it absent
      every time. In the committed rerun the rig saw the lock four times, each last modified 0.5 to 2.1 seconds before,
      and no start was refused. This machine runs many Claude Code sessions, and each of them takes that lock.
    - So how often an unattended seat start loses its run this way on a busy machine is not known.

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
- **What held Claude Code's configuration lock** in finding 10, or how often it is held for 10 seconds.
- **PostgreSQL, the hosted Gateway, other machines, macOS or Linux.** One Windows machine, one local Gateway on SQLite.
- **The Cockpit.** No screen was opened.

# Review - Session Rules phase 1: the Windows proof, the Linux live rig, and the merge up to main

Written 2026-10-19 by the Reviewer seat "Session Rules - Reviewer - phase 1 Windows, rig and up-to-main",
from the brief `reviews/brief-rest.md`. Reviewed in the worktree `devthrottle-session-rules-p1-review-rest`
at `a1cb2ed25` (the tip of `mission/session-rules-hooks-p1` when the worktree was cut). The reviewer read
and ran; it built nothing into the product and edited no source file.

**Verdict: 3 findings (one medium, two low), none in the product code paths that ship. The merge and the
regenerated migration are mechanically correct. The rig cannot reach the owner's Director, Gateway or tools
folder, `reset` cannot delete outside its own folder, and `down` force-kills nothing. The one real risk is
the copied Claude Code sign-in, where the protection is a written convention rather than a mechanism.**

---

## Scope

**Read.** The plan (`phase-1-plan.md`, rulings P1 to P17, wire shapes W1 to W5), `mission.md`, the brief
template, the Developer accounts `phase-1-windows-proof.md` and `live-rig.md`, and every commit named in
the brief with `git show`: `1fae87257`, `6a86311d8`, `64c41f86e`, `96ca70087` (Windows); `162f35a7f`
and `b2ceb977b` (rig); `e722ad39b` (the merge) and `40124743d` (the migration). For the merge, every file
that differs from both parents was diffed against each parent separately, and the whole merge was compared
with git's own automatic merge of the same two parents (`git merge-tree --write-tree`). For the migration,
the two Designer files were compared with their provider's model snapshot, and the migration bodies
before and after the regeneration were compared byte for byte. Around the rig script, the product code it
leans on was read: the Director's configuration readers, the registration file writer, the lifecycle
signal, the storage root, the update service, the Gateway's authentication switches, the command line
tool's Gateway resolution and its `session spawn` and `session prompt` commands, and the secrets tool's
`run` flags in `docs/cli-reference.md`.

**Ran, on this Linux machine, all green.**

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.RuleHook.Tests` | 70 passed (includes the two new Windows fixture cases) |
| `dotnet test src/CcDirector.Core.Tests --filter CcStorageProtectedPathsTests` | 5 passed |
| `dotnet test src/CcDirector.Core.UnitTests --filter "CatchLogLineScanTests\|ClaudeHookInstaller"` | 10 passed |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "SessionRuleKindsMigrationTests\|GatewayHostBootSmokeTests\|MigrationChain"` (web typecheck skipped) | 16 passed |
| The rig's exact import of the copied tool (`from cc_devthrottle.cli import tool_main`) with `session spawn --help`, from a scratch copy | runs; `--director`, `--standalone`, `--command`, `--name`, `--agent` all present |

The boot smoke tests assert `HasPendingModelChanges()` is false for BOTH the SQLite and the PostgreSQL
model, so both merged snapshots match the model.

**Could not reach.** No Windows machine: nothing in `phase-1-windows-proof.md` about the Windows gate
runs, their counts, or the watched-failing claim was re-run. No Docker: `SessionRuleKindsPostgresTests`
(the real PostgreSQL migration test) was not run. The rig was not stood up: doing so starts processes and
copies the owner's sign-in, and a reviewer does not build or run the mission's rig; the script was read
line by line instead and its assumptions were checked against the code. The branch on origin is four
commits past the reviewed tip (`22e553930`, `4df5d3ec3`, `c1d75c028`, `75903a971`: the proof with the
Linux live rows, its evidence files, the brief, and the state note); those were NOT reviewed here.

---

## Findings

### F1 (medium) - the copied sign-in's protection against rotating the owner's own is a convention, and `down` can claim the copy is gone while sessions still hold it

`scripts/session-rules-rig.sh` lines 466 to 496 (`copy_signin`), 701 to 704 and 684 (`cmd_down`).

The script copies `~/.claude/.credentials.json` into the rig's HOME for every Claude Code session it
spawns, refusing only when the owner's token has under 120 minutes left (the repository precedent in
`src/CcDirector.DeliveryQualification/Rig.cs` lines 175 to 177 uses 30 minutes and states the same reason:
a copy that refreshes "could rotate the owner's own out"). The repository therefore already treats a
refresh from the copy as a real harm: the owner signed out of Claude Code on this machine.

What the script does not do:

- **Nothing bounds a run against that margin.** Once a rig Claude Code session is alive, it stays alive
  across the 120 minutes; when the access token nears expiry the rig's copy refreshes, and the owner's
  real file holds the superseded refresh token. `live-rig.md` says "keep a rig run short; `down` when
  finished" - a sentence, not a check. Neither `status` nor `spawn-session` reads the copied token's
  expiry back, so a run has no visible deadline.
- **`down` reports "deleted the rig's copy of the Claude Code sign-in" unconditionally** (line 703), even
  on the path at line 684 where the Director "is STILL RUNNING after its signal - NOT forced". A Director
  that did not exit still has its Claude Code sessions, and each holds the token in memory and can refresh
  it. The file is gone; the capability is not. The message tells the operator the risk is over when it is
  not.
- A second `spawn-session` overwrites the rig copy with the owner's current file (line 479). If the rig
  copy had already refreshed, this puts the superseded token back under the rig too, so neither side
  holds the live one.

**What must change.** `down` should claim the copy is deleted only after the Director process has exited,
and otherwise say that live sessions may still hold the sign-in; `spawn-session` should record the copied
token's `expiresAt` and `status` should print the minutes left, refusing a new spawn under the margin. The
120-minute margin itself is sound and more conservative than the precedent.

### F2 (low) - two switches in the rig turn nothing off

`scripts/session-rules-rig.sh` line 246 (`CC_AUTOUPDATE=0`) and line 310 (`"autoUpdate": {"enabled":
False}` at the top level of the Director's `config.json`).

No code in `src` reads `CC_AUTOUPDATE` (`grep -rn CC_AUTOUPDATE src` finds nothing). The product's
auto-update key is `tools` -> `autoUpdate` -> `enabled` (`src/CcDirector.Core/Configuration/ToolAutoUpdateSetting.cs`
line 8), so a top-level `autoUpdate` is ignored and the tool sync runs with its default, enabled. The
intent still holds for other reasons: a slot build has no self-updater at all (`UpdateService.cs` line 13,
gated by the project's `UpdaterEnabled`), and the tool sync that does run lands under the rig's HOME. So
there is no harm today. The harm is to the next reader, who sees two switches and believes the rig's
isolation depends on them; this is exactly the "constant substituted and nothing notices" shape. Delete
both lines, or write the real key under `tools`.

### F3 (low) - two command lines in the Windows proof cannot be run as written

`docs/missions/session-rules-hooks-2026-10-18/phase-1-windows-proof.md` lines 57 and 81 read
`.\scripts<TAB>est-local.ps1` - a literal tab character where `\test-local.ps1` was meant (the `\t` was
swallowed when the text was produced). A reader copying the gate command gets a path that does not exist.
Write `.\scripts\test-local.ps1`.

---

## Notes (no change required, recorded so nobody re-finds them)

- **N1.** `write_director_config` (rig line 305) passes the rig Gateway's token as a `python3` command line
  argument, visible in the process list for that moment, while line 227 says the token "never appears in
  the process list". It is the throwaway rig's token on the owner's own machine, so no harm; the sentence
  is inexact.
- **N2.** `cmd_build` runs the worktree's `scripts/local-build-linux.sh`, which writes
  `<worktree>/local_builds/linux/cc-director-linux<N>` (that script, line 171) - outside the rig folder,
  against the header's "nothing anywhere else" (line 7). It is gitignored (`local_builds/*`) and `reset`
  leaves it. Harmless; the header is inexact.
- **N3.** `live-rig.md` section 1 rests on "a Director on a fresh root links its cc-* tools into
  `~/.local/bin` at start-up whatever `CC_DIRECTOR_ROOT` says". Main's change #4211, now in the branch
  through the merge, makes a Director on a non-default root never write the user's global bin. The rig's
  own HOME is now a second guard rather than the only one; the document's premise is out of date but the
  protection is stronger, not weaker.
- **N4.** `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs` lines 252 to 253 still say the hook command
  form "works whether Claude runs hooks through cmd.exe or sh". The Windows proof established that Claude
  Code on Windows runs a hook command string through Git Bash. Not in the reviewed diff; a stale comment.
- **N5.** The measurement script's statement that Git Bash is found "as Claude Code finds it"
  (`CLAUDE_CODE_GIT_BASH_PATH`, then the Git for Windows on the PATH) is a claim about Claude Code's
  internals that this repository cannot verify. On a standard Git for Windows install both routes reach
  the same `bin\bash.exe`, and the script fails loudly with instructions when neither is present, so the
  measurement does not depend on the claim being exact.

---

## The brief's questions, answered

### Piece 1 - the Windows Developer's commits

**The captured events and their test** (`1fae87257`). The two fixtures carry the shape of the Linux
capture - `hook_event_name`, `tool_name`, `tool_input.command`, `tool_use_id` - with Windows paths; the
PowerShell event's command is in `tool_input.command`, which P12 had left unproven. The new theory test
feeds each file unchanged and asserts exit 0, one log line with `would_deny` ending in the event's own
call id (P14), and a decision file with `tool` `shell`, the real `agentTool` and the command as `subject`
(W3). It cannot pass with the fixture's call id or command substituted, and it passes here. The fixtures
are picked up by the project's `Fixtures\**` include.

**`scripts/measure-rule-hook.ps1`.** The change moves the Windows run from `cmd.exe` to Git Bash with
`-c`, finds Git Bash through `git --exec-path` three levels up (correct for the Git for Windows layout),
refuses with instructions when `git.exe` or `bash.exe` is missing, uses a real program (`/usr/bin/true`)
as the Windows baseline, and picks the Windows fixture on Windows. The quoted absolute Windows path
survives bash's double quotes because backslash before a letter is literal there, and the Director's
existing SessionStart hook already relies on that today. The numbers in the proof document come from a
non-native build and the document says so plainly; the only standalone claim is the Git Bash baseline of
about 60 ms, which is the shell's cost and not the evaluator's.

**The reclaim classification** (`64c41f86e`). `SessionRules` is classified beside `SessionPreambles` as
"data the mission reports and never offers for removal; no rule matches it". That is the right class:
the folder has the preamble file's life (W2), holds no credential, and no reclaim rule matches anything
under the product root. The guard test passes.

**The two log lines.** `InstallRuleHook FAILED:` is the house form and the right call: P16 wants a
refused evaluator install logged loudly, and now it reaches the Gateway as an error. The
`// not-an-error:` marker sits on the line above the call with its reason. Compare-before-write means the
replace is attempted only after a Director update, so the error is rare and real when it fires. The scan
test passes.

**Self-testimony not verified here.** The Windows gate runs and their counts, and the statement that the
PowerShell case was watched failing with the tool check narrowed to `Bash`. Neither can be checked from
Linux; the commits are consistent with both.

### Piece 2 - the Linux live rig

**Can it touch the owner's real Director, Gateway, `~/.local/bin`, or Claude Code sign-in?**

- *Director and Gateway: no.* Every process the script acts on is found by `/proc/<pid>/exe` under the
  rig folder (lines 159 to 171) - never by name. Registrations are read only from the rig root's
  `instances/default/config/director/instances` (line 140), whose `Pid` and `DirectorId` keys match the
  writer (`InstanceRegistration.cs` line 93 serialises `DirectorDto` with default, Pascal-case names).
  The shutdown request file goes under the rig root (`LifecycleSignal.cs` line 195 puts it under
  `SharedRoot`, which is `CcStorage.Root()`, which is `CC_DIRECTOR_ROOT`, which is the rig root). Port
  7878 is refused (line 122). Both processes start from `env -i` plus an allow-list (lines 241 to 254),
  so the calling session's Gateway address and key do not pass. The Director's `config.json` names only
  the rig Gateway; its keys were checked against the readers: `gateway.url`, `gateway.token`,
  `gateway.streamMode` (`GatewayCredentialStore.cs`), `onboarding.completed` (`OnboardingModel.cs` lines
  84 to 88), `agent.entries` with the `AgentEntry` field names, and the preset names `Skip permissions`
  and `Standard` (`AgentToolCatalog.cs` lines 66 and 86). The tool runs with the rig URL and key set
  together, so `tool_errors.py`'s fall-back to the machine's own `gateway-token.txt` (line 681) is not
  reached.
- *`~/.local/bin`: no.* HOME is the rig's for both processes, and after the merge a Director on a
  non-default root never writes the global bin anyway (N3). `up`, `status` and `down` print the owner's
  `cc-devthrottle` link so the before-and-after check is in the output.
- *The sign-in: read, copied, and the copy can refresh.* The owner's files are only read (`command -v
  claude`, `~/.claude/.credentials.json`, `~/.claude.json`). The risk is server-side, through the copy,
  and is F1.
- *Other reads outside the rig:* `/proc` of the user's processes for the inotify count. *Other writes
  outside the rig:* the worktree's `local_builds/linux` (N2).

**Can `reset` delete anything outside its own folder?** No. `rm -rf -- "${RIG_DIR:?}"` (line 721) runs
only after `assert_rig_dir` (lines 105 to 119): the path is made absolute with symlinks resolved
(`realpath -m`), must carry `session-rules-rig` in its leaf name, must not be the home folder or `/`,
must not be inside or above `~/.local/share/cc-director`, and if it exists must hold the sentinel file
this script writes. `reset` also refuses while anything still runs from the rig (line 718). The only
folder it can delete is one this script created.

**Does `down` force-kill anything?** No. The Director gets its own named shutdown request file (line
679), the Gateway a terminate signal (line 691) aimed at one process id verified by executable path
(`gateway_pid`, line 173). Each is waited on (90 and 60 seconds) and a process that does not exit is
reported and left. There is no `-KILL`, no `pkill`, no `-9` in the file.

**Does copying the sign-in risk rotating the owner's own?** Yes, and the repository's own precedent
says so. The 120-minute margin lowers the chance; nothing enforces the run's length and `down` can
overstate what it removed. F1.

**What the Developer did not prove at the reviewed tip.** A Director joining the rig Gateway,
`spawn-session`, `prompt`, and rows 1 and 2 (blocked by the inotify limit, and said so). The tool
import path and the spawn flags were unproven then; this review ran them and they work. The proof commit
after the reviewed tip claims the Linux live rows; it is outside this review.

### Piece 3 - bringing the branch up to main

**Did the conflict resolution keep both sides' behaviour?** Yes, mechanically. Git's own automatic merge
of the two parents (`git merge-tree --write-tree b7dc38b2c 9bf8dc6a9`) differs from the committed merge
in exactly one hunk: the `GatewayClient` class declaration, where git left both lines and the resolution
carries both `IGatewayWheel` (main's) and `CcDirector.Core.Rules.ISessionRuleGateway` (the mission's).
Every other file came out as git merged it. Five files differ from both parents (`GatewayClient.cs`,
`ControlApiHost.cs`, `GatewayDbContext.cs`, both model snapshots); for each, the diff against the mission
parent shows only main's additions and the diff against the main parent shows only the mission's. Main's
wheel methods and the mission's `FetchEffectiveHookRulesAsync` and `PostHookFiringsAsync` are both in the
merged client.

**Do both model snapshots hold both migrations and nothing else changed?** Yes. Both snapshots were
merged by git without conflict; against the main parent each shows only the session rules columns and
the `(tenant_id, DecisionId)` unique index, against the mission parent only the Mail Desk tables.
`40124743d` did not touch either snapshot, and nothing between the merge and the reviewed tip did. Each
regenerated Designer's target model is textually identical to its provider's snapshot model (only the
method name differs), and the boot smoke tests assert no pending model changes for both providers.

**Is the regenerated migration the same schema change as before?** Yes. Both `AddSessionRuleKindsAndHookFirings.cs`
bodies are byte-identical renames (git similarity 100 percent) with new timestamps `20261018230524`
(SQLite) and `20261018230534` (PostgreSQL), so each now follows `AddMailDesk` in its provider's order.
The hand-restored PostgreSQL `defaultValueSql: "'{}'"` lines for the five array columns are intact. The
Designer files differ from the old ones only in the migration id attribute and the Mail Desk entities.
The two migration tests name `AddMailDesk` as the migration before, and pass. The real PostgreSQL test
in `CcDirector.Gateway.DatabaseTests` needs Docker and was not run.

---

## What this review does not cover

The four documentation commits after `a1cb2ed25` on the mission branch, the Windows runs themselves, the
PostgreSQL database test, and a live run of the rig. The ruling behind `-Configuration Release` for the
gate, the choice of a 120-minute margin, and whether a Director built without the native compiler should
refuse to start are product decisions and were not re-opened.

---

## The Tech Lead's calls

The original Developers are gone, so a fresh Developer answers every item below, accepted or declined
with the reason, in `phase-1-rest-answers.md` beside this file.

1. **F1: fix as the reviewer says.** `down` says the sign-in copy is gone only once the Director has
   exited, and otherwise says live sessions may still hold it; `spawn-session` records the copied token's
   expiry, `status` prints the minutes left, and a spawn under the margin is refused; a second
   `spawn-session` never overwrites a copy that is newer than the owner's file.
2. **F2: fix** - delete both switches that turn nothing off, and say in the script why the rig cannot
   update itself (a slot build has no self-updater; the tool sync lands under the rig's HOME).
3. **F3: fix** the two command lines in `phase-1-windows-proof.md`.
4. **N1, N2, N3: fix the sentences** so they say what the script does.
5. **N4: fix the stale comment** in `ClaudeHookInstaller` (Claude Code on Windows runs a hook command
   string through Git Bash).
6. **N5: keep.**

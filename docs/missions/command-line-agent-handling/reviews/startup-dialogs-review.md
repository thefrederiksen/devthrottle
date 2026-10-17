# Review: start-up questions (pull request #4141, branch `startup-dialogs`)

Mission: Command Line Agent Handling. Reviewer seat, a different agent family from the author. Read only; nothing
was fixed.

Reviewed commit: `647e22546` (the branch head on 17 October 2026), against `origin/main` at `617912aab`.

## Verdict

**No finding proves a harm that must change before merge.** Both changes do what the pull request says, in the
safe direction: the Codex trust line is only ever written for a folder that Claude Code's own record already
trusts, and the Bypass Permissions refusal fires only for a session nobody will answer and only when the launch
line asks for bypass. Everything below is either a scope statement, a point I could not verify without running a
real agent (which the brief forbade), or a low-severity observation for the Developer to accept or decline.

## Scope: what I read and ran

- The whole diff of the branch against `origin/main`: `ClaudeBypassConsent.cs`, `ClaudeFolderTrust.Covers`,
  `CodexStartupArguments.cs`, the `SessionManager` wiring, `App.axaml.cs`, both test files, and the delivery rig
  (`Program.cs`, `Rig.cs`, `Scenarios.cs`, `README.md`).
- The code around the change on the branch: the rest of `CreateSession` (the catch that gives back a pooled slot
  and a kit), `LinkedWorktree.ParentRepositoryOf`, `ClaudeAgent.BuildLaunchSpec`, `CommandLineLauncher`, the Unix
  argument tokenizer (`UnixProcessHost.TokenizeArgs`), the spawn handler in `SessionCommandExecutor`, the spawn
  ownership rule in `SpawnOrigin`, the scheduled-run starter, and the API-mode backend.
- The installed Claude Code 2.1.302 binary on devlinux, read with `grep` for the strings that decide the warning
  (`skipDangerousModePermissionPrompt`, `bypassPermissionsModeAccepted`, the dialog's own gate functions). Not run.
- Codex 0.163.0's own trust record on devlinux (`~/.codex/config.toml`, keys only) and its 16 October backup. Not run.
- Ran, on Linux with .NET 10, in a worktree cut at the branch head:
  - `StartupQuestionsTests` and `StartupQuestionsAtStartTests`: **31 of 31 passed**.
  - The whole `CcDirector.Core.UnitTests` project once, alone: see the line at the end of this file.
- Not done: no real Codex or Claude Code session was started (as instructed). No Windows or macOS run. The delivery
  rig was not run; its numbers in the pull request are the author's own testimony and I could not reproduce them
  without starting real agents.

## Change 1: Codex's update check and the trust carried over

### Does it ever trust a folder the owner did not trust? No.

`ClaudeFolderTrust.Covers` answers true only when Claude Code's record holds `hasTrustDialogAccepted: true` for
one of: the folder itself, a parent up to and including the first git root, or, when the folder is itself a linked
worktree, its main checkout. Each candidate is looked up by the same key form (`KeyFor`: full path, forward
slashes, no trailing slash) that the existing writer uses, so a folder is trusted only by an entry the owner's
own Claude Code wrote or that the Director's seat writer wrote for a folder the Director made. There is no
entry it can invent. An unreadable record, a missing record, and a record whose `projects` is not an object all
answer false, so Codex asks as before. That is the right direction for every failure.

The candidate set is no wider than Claude Code's own rule as this repository has already proven it (the
`ClaudeFolderTrust` header: parents are walked, the walk stops at a git root, and a worktree is covered by its
main checkout, which `trusted-worktree` measured at 3 of 3 on main).

### Can path forms fool it? Only in the safe direction.

- **Trailing separators:** `KeyFor` trims them before the lookup, on both sides.
- **Case:** the lookup is an exact JSON key match and nothing folds case, same as the existing `IsRecorded`. A
  session folder spelled in a different case from what Claude Code recorded is a miss, so Codex asks. It can
  never produce a trust that is not in the record.
- **Symbolic links:** `Path.GetFullPath` does not resolve them. Claude Code records the path its process reports as
  the working directory, which on Linux is the physical path. A session opened through a link path therefore
  misses the entry and Codex asks. Again a miss, never a false trust.
- **Windows paths:** `KeyFor` turns backslashes into forward slashes, which is the key form the existing writer
  proved against Claude Code 2.1.301. The Codex side (a backslash path inside a TOML literal string) is unproven on
  a real Windows Codex, as the pull request says.
- **A sub-folder of a worktree:** the walk stops at the worktree's own `.git` file and the main checkout is
  consulted only when the session folder is the worktree root itself. A session opened in `worktree/src` is a
  miss. Safe, just not carried over.

### The launch line reaches Codex intact

The trust line is `-c "projects={'<folder>'={trust_level='trusted'}}"`. On Linux and macOS the Director splits the
argument string itself (`UnixProcessHost.TokenizeArgs`): inside a double-quoted region a single quote is kept as a
character, so the TOML literal string arrives as one argument with its single quotes, and a folder with a space
in its name survives. On Windows the whole line goes to the process, which parses it by the usual rules, and a
backslash before anything but a double quote is literal. A folder whose path holds a quote gets no trust line and
Codex asks; that is stated in the code and tested.

### Not verified

- That Codex honours `projects` by the exact working directory when that directory is a sub-folder of a repository
  (Codex may look its trust up by the git root). The pull request reports a hand check that it does. The worst
  case is that Codex asks, as it always did.
- That the two project entries added to Codex's own record on 16 October 08:48 (`devthrottle` and
  `_delivery-qa`) do not make the `claude-trusted-folder` scenario pass on main by themselves. The scenario's
  folders are new git repositories beneath `_delivery-qa`, and the pull request's 0 of 2 on main was measured on
  16 and 17 October, after that entry existed, which says Codex did not inherit the parent's trust. Consistent,
  but reported by the author, not seen by me.

### Observation 1 (low): one log line can be untrue

When the caller's own line already sets `check_for_update_on_startup=true`, `Append` correctly leaves it alone,
but `SessionManager` still logs "Started Codex with its update check off." The session log is the place a person
looks when a start misbehaves, so it should say what happened. Not a correctness defect in the launch itself.

## Change 2: the Bypass Permissions warning

### Does it ever refuse a session that would NOT have stopped?

**Through the product's real spawn paths today, I found no such case.** A session opened by another session comes
through `SessionCommandExecutor.Create` with the terminal backend; a seat does too. The refusal needs bypass on the
effective line (the caller's arguments, or the configured Claude Code defaults when the caller gave none) and no
consent in any of the four places read. A session the owner opens is never asked.

Three edges where the check would refuse a start that Claude Code would not have stopped, none reachable today
through a spawn by a session, listed so the Developer can decide whether any matters:

- **Print mode.** Claude Code 2.1.302 runs its start-up steps, the bypass warning among them, only when it is not in
  print mode (the binary gates them on the print flag). The Studio backend starts `claude -p`, so a Studio session
  would never see the dialog, yet `WhyItWouldStop` would refuse it. No production path creates a controlled or
  seated session on the Studio backend; `StartupQuestionsAtStartTests` does, with a fake check, so the test does
  not prove the real reader against a real print-mode line.
- **A machine whose managed settings disable bypass** (`permissions.disableBypassPermissionsMode: "disable"`):
  Claude Code prints "Bypass permissions mode was disabled by settings", drops the flag and starts with no dialog.
  The Director would refuse with a reason that says the warning was never accepted. Enterprise-only; not this
  fleet.
- **Consent in a managed-settings drop-in folder** (`managed-settings.d`, which the 2.1.302 binary also reads): the
  Director reads only `managed-settings.json`. Same population as the previous point.

### Does it ever let through a session that WOULD have stopped?

Within what the pull request claims, one point I could not settle by reading, and one scope gap:

- **Observation 2 (low, verify by hand): which consents Claude Code honours at its start-up gate.** The binary has
  two gates around `BypassPermissionsModeDialog`. One (`h4`) accepts the consent from user settings, the folder's
  local settings, a `--settings` file or policy settings - the set the branch reads, plus the old key. The other,
  the asynchronous gate that also writes the consent back after acceptance, skips the dialog only for policy
  settings, user settings or the old configuration key; it honours local and `--settings` consent only when the
  permission mode was inherited. If that second gate is the one a fresh interactive start runs, then a consent that
  lives only in `.claude/settings.local.json` would NOT skip the warning, and neither would one carried in a
  caller's `--settings` file, so in those two configurations the Director would start a session that then stops -
  the very dead session this change exists to prevent. Accepting the dialog writes user settings, so both gates
  agree in the ordinary case; only a hand-written local consent, or a spawn that brings its own `--settings`,
  diverges. I could not run Claude Code to see which gate fires. A Developer can settle it in two hand runs on a
  scratch config folder: consent only in the folder's `settings.local.json`; consent only in a `--settings` file.
  If the second gate wins, the fix is to read user, policy and the old key only, and to drop the `--settings`
  exemption (or read that file when it is a path).
- **Scope gap (not a regression): a scheduled run that is not a seat.** `DirectorCronSessionStarter` opens a
  plain scheduled session with no controller and no kit, so the refusal does not apply to it, yet nobody is there
  to answer either. A scheduled Claude Code job started with bypass on a machine that never accepted it would
  stop silently, exactly as a reviewer did on 16 October. The pull request does not claim to cover it. Worth a
  line in the mission record as follow-up.

### Is the refusal honest and actionable?

Yes, in the common case. It names the machine, says the Director does not accept the warning for the owner, and
gives the one command Claude Code itself gives for the same situation (the binary's own `--bg` message reads "Run
`claude --dangerously-skip-permissions` once interactively"), plus the alternative of starting without the flag.
The message travels to the caller as the create error (`DirectorCommandResult.Fail(..., ex.Message)`), the same
route as the existing "command is not a file on this machine" refusal.

Two cases where the sentence says something that is not quite what happened, both logged with the truth:

- A settings file that exists but cannot be read or parsed is treated as holding no consent and the refusal says
  "never been accepted on this machine". The log says the file could not be read; the person is not told.
- The managed-settings-disables-bypass case above.

### Logging and no-fallback programming

- `Covers`, `Append`, `TrustFolder`, `IsGiven`, `WhyItWouldStop` and the `SessionManager` refusal all log entry
  or outcome with the class prefix. `IsRequested` is a pure token scan and logs nothing, which is fine.
- `IsTrueIn` catches read and parse errors and answers false. This is argued in the code as not-an-error: the
  outcome is a refusal in words naming the command that writes the file properly, not a session started blind. I
  accept the argument, with the honesty note above. `Covers` catching `ClaudeFolderTrustException` and answering
  false has the same shape and the same justification: the agent's own question stays in place.
- The refusal is thrown inside the create's existing try, so the catch returns the pooled slot, revokes any key,
  discards a fresh kit and disposes the session. `StartupQuestionsAtStartTests` checks the roster is empty after
  the refusal. A seat refused here has already had its folder written as trusted by `TrustSeatFolder`; that write
  is idempotent and harmless.

## The delivery harness scenarios

- `claude-trusted-folder` is a real discriminating scenario in shape: a fresh repository is trusted in Claude Code
  through the product's own writer and never seen by Codex, then a Codex session is created on the terminal
  backend and its first prompt delivered. On main nothing adds the trust line, so Codex's own question swallows the
  prompt; on the branch the line is added. The 0 of 2 and 5 of 5 are reported, not reproduced here.
- `trusted-worktree` is a measurement, not a proof of this change, and the pull request says so: it passes on main
  and on the branch and exists to show reviewer worktrees were never Claude Code's trust problem.
- The rig now wires the real trust record and the real bypass check into its `SessionManager`, so a rig start
  asks what the Director asks. Rig sessions carry no controller and no kit, so the bypass refusal is never
  exercised by the rig; the pull request says the same.
- **Observation 3 (process): the proof is not in the record.** The branch commits no rig output. The numbers in
  the table live only in the pull request body. The method says the proof is committed beside the code, and the
  gate run on the frozen candidate will not re-run the rig. The Delivery Lead may want the run logs under the
  mission record.

## Full Core unit suite

Run once, alone, after the build above (`dotnet test --no-build`): **2,163 passed, 5 failed, 23 skipped, 2,191 total**. The five failures are Windows-path tests in `RepositorySourceIndexTests`, `CcWorktreesPoolSlotsTests` and `WorktreePoolSettingsTests`, none of which touch this change; the pull request reports the same five failing on main on Linux. I did not re-run main to confirm that.

## The Developer's answers

1. **Observation 1, the untrue log line: accepted and fixed.** `SessionManager` now logs "its update check as the
   launch line sets it" when the caller's own line sets `check_for_update_on_startup`, and "its update check off"
   only when the Director turned it off.
2. **Observation 2, which consents the start-up gate honours: settled by hand, and the branch stands.** Three runs on
   devlinux, Claude Code 2.1.302, in a trusted scratch repository, no input: with no consent the warning showed; with
   the consent only in the folder's `settings.local.json`, and with it only in a `--settings` file, Claude Code
   started in Bypass Permissions mode with no warning. So a fresh interactive start honours both, and the reader and
   the `--settings` exemption are right as written. The screens are in
   `docs/missions/command-line-agent-handling/evidence/startup-dialogs/`.
3. **Observation 3, the proof is not in the record: accepted.** The rig's ledger rows and run logs for that afternoon
   are committed under `docs/missions/command-line-agent-handling/evidence/startup-dialogs/`, with a README saying
   which rows were built from main and which from the change.

The three edges listed under "refuses a session that would not have stopped" (print mode on the Studio backend,
managed settings that disable bypass, a `managed-settings.d` drop-in) are not reachable through a spawn by a session
today and are left as recorded. The scheduled run that is neither a seat nor controlled is a real gap and is follow-up
work, not part of this change.

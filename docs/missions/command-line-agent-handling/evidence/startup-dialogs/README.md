# Evidence: the start-up questions change (pull request #4141)

Recorded on devlinux, 17 October 2026, Claude Code 2.1.302 and Codex 0.163.0.

## The delivery rig

`ledger-rows.jsonl` holds every rig attempt of that afternoon, as the rig wrote them to its ledger. The rows tagged
`dfdb177818b8` were built from main; the rows tagged `dfdb177818b8+local-changes` were built from this branch's
working changes before they were committed (the branch was later rebased; the Codex and trust code is the same, and
the bypass check it gained afterwards is never reached by a rig start, which has no controller and no kit).

| Scenario | main | this change |
|---|---|---|
| `claude-trusted-folder` (Codex) | 0 of 2 (`rig-codex-baseline.log`) | 5 of 5 (`rig-codex-fix.log`, `rig-codex-fix2.log`) |
| `trusted-worktree` (Claude Code) | 3 of 3 (`rig-baseline.log`) | 2 of 2 (`rig-claude-fix.log`) |
| Codex `first` | - | 3 of 3 |
| Claude Code `first`, `idle-short`, `idle-special` | - | 2 of 2 each |
| Codex `idle-short`, `idle-special` | - | 0 of 3 each: the account is at its usage limit until 9 November and Codex shows a "Switch model?" menu, which the Director refuses to type into (`rig-codex-fix2.log` shows the screen) |

## The Bypass Permissions consent, by hand

Claude Code started with `--dangerously-skip-permissions --model haiku` in a rig scratch repository that Claude Code
trusts, for 12 seconds, with no input (no prompt sent, nothing accepted); the screen text is in the three files.

| Run | Consent recorded | Screen |
|---|---|---|
| `bypass-hand-control.txt` | nowhere | the warning: "WARNING: Claude Code running in Bypass Permissions mode" |
| `bypass-hand-local.txt` | only in the folder's `.claude/settings.local.json` | no warning; "bypass permissions on" |
| `bypass-hand-flagsettings.txt` | only in a file passed with `--settings` | no warning; "bypass permissions on" |

The local file was removed after its run, and the user's own `~/.claude/settings.json` was not changed.

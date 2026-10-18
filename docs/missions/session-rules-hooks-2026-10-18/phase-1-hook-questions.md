# Phase 1, Developer B (Hook) - questions for the Tech Lead

Written 2026-10-18 by "Session Rules - Developer - phase 1 hook evaluator", branch
`mission/session-rules-hooks-p1-hook`. Each question has the answer I am building to until told
otherwise, so nothing waits on it.

## Found on the installed Claude Code (2.1.302, Linux)

A real PreToolUse event, captured from a `claude -p` run with a throwaway hook, is committed as
`src/CcDirector.RuleHook.Tests/Fixtures/claude-code-2.1.302-pretooluse-bash.json`. The shell tool is
`Bash`; the command is `tool_input.command`. A hook that exits 0 and prints nothing left the command
alone: it ran and printed its output.

The same binary lists its shell tools as `["Bash","PowerShell"]`. The `PowerShell` tool is the
Windows shell tool (used when Git Bash is missing, or when `CLAUDE_CODE_USE_POWERSHELL_TOOL=1`).

## Q1. Register the hook for the PowerShell tool too?

Building to: matcher `Bash|PowerShell`, both recorded as `tool` `shell` with `agentTool` holding the
real name. On Linux the PowerShell tool does not exist, so the extra name costs nothing. NOT proven:
that the PowerShell tool's input field is also `command` - no Windows machine here. If it is not, the
evaluation log says `error: ...` for that call, so it shows rather than hides.

## Q2. Which matching rules count as "overridden" (P3)?

P3 says the decision names "every matching rule it overrode". Building to: every OTHER matching rule
whose result DIFFERS from the decision (a lower-rank rule that disagrees, or a same-rank `allow` that
lost to `deny`). A matching rule that agrees with the decision is in `matchedRuleIds` only - showing
an agreeing global `deny` as "overridden" by a repo `deny` on the phase 3 page would be misleading.
The alternative reading is "every matching rule except the deciding one".

## Q3. May the evaluation log line (W4) carry the agent's call id?

W4 lists: time, tool, rules read, rules matched, the decision, microseconds. To prove a negative
control "by a `no_match` line FOR THAT COMMAND", the only link from the line to the command is the
time. Building to W4 exactly. Proposal: add Claude Code's `tool_use_id` as the last field, which the
live rig can match against the session transcript without the log ever holding command text.

## Q4. Shipping cc-rule-hook through the installer and the release - how far?

The release ships the Director as ONE file per platform (`cc-director-win-x64.exe`,
`cc-director-linux-x64`) and the setup engine places and updates exactly that file
(`ComponentRegistry.Director`, `InstallLayout.PathFor`, `DirectorUpdateApply` with its staged swap and
roll back). A second file beside it needs: two new release assets, publish steps in the Windows and
Linux Director jobs of `release.yml`, and the setup engine placing it on install, replacing it on
update and removing it on uninstall.

Building to: a new app component `rule-hook` in `ComponentRegistry` (assets `cc-rule-hook-win-x64.exe`
and `cc-rule-hook-linux-x64`, both roles, no macOS asset in phase 1), placed by `InstallLayout` beside
the Director, so the generic install and update runner carries it with no new path; excluded from tool
discovery; plus the `release.yml` publish and rename steps; plus both local build scripts. Tell me if
you want the setup engine part split out of this branch.

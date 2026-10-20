# Brief - phase 3 Developer A: the Gateway fold and routes for the Rules page

You are "Session Rules - Developer - phase 3 Gateway". One task. You report to the phase 3 Tech Lead
(the session that opened you), never to the owner.

Worktree: /home/soren/ReposFred/devthrottle-session-rules-p3-gateway, branch
mission/session-rules-hooks-p3-gateway (cut from the phase branch mission/session-rules-hooks-p3). Work only there.

Read first: `phase-3-plan.md` in this folder (rulings T1-T10, wire P1-P3 - build to them EXACTLY), then
`mission.md` sections 4-5, then `phase-1-plan.md` rulings P2, P3, P4, P13 and wire W2/W5. The code:
`src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs`, `SessionRuleWire.cs`, `src/CcDirector.Gateway/Rules/SessionRuleStore.cs`,
`src/CcDirector.RuleHook/RuleResolver.cs`.

Build: the fold class (under `src/CcDirector.Gateway/Rules/`), the three read-only routes (T2), the linked
resolver (T6 - stop and tell me if it will not compile cleanly, do not write a second resolver), the T7
standing-override rule, the T3 time zone handling (400 on a missing or unknown zone).

Tests (Method_Scenario_Result, Arrange-Act-Assert): every tab's membership and count; fired today across a
zone boundary (a firing that is today in one zone and yesterday in UTC); changed by agents vs changed by a
person; session start empty text; a rule with no firings; an override recorded on a firing and named in P2,
including a deleted overridden rule; preview with no rules, with a repo rule overriding an everywhere rule
(T7), with a sample command equal to `RuleResolver.Decide`, with a command matching nothing; 400 for missing
machine and bad tz; 404 for an unknown rule. Prove at least one test can fail: break the rank order, watch
the override test go red, restore.

The check you must run and report with exit codes: `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests
--filter "FullyQualifiedName~Rules"` and `~/.dotnet/dotnet test src/CcDirector.RuleHook.Tests`. Set
DOTNET_USE_POLLING_FILE_WATCHER=1. .NET 10 is at ~/.dotnet/dotnet (/usr/bin/dotnet is 8 and fails).

House rules: CLAUDE.md (logging on every public method, no fallbacks, try-catch at entry points only, plain
English, no abbreviations). Commit with `git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com`,
no assistant attribution anywhere. Push your branch. No sub-agents. Do not merge anything, do not open a
pull request.

When done: `cc-devthrottle session report` (or message the Tech Lead) with ONE line: your commit and the
test counts. If a ruling is genuinely undecidable, ask the Tech Lead and carry on with the rest.

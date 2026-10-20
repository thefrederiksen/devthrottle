# Brief - Developer, bring phase 1 up to main after phase 2 (#4267)

You are a Developer on the Session Rules mission, phase 1. Seat: `cc-devthrottle skill get
devthrottle-method` (Developer section). You report to the phase 1 Tech Lead.

Your worktree holds `mission/session-rules-hooks-p1` (pull request 4268). Phase 2 merged to main as #4267
(`46ab20e2e`): session keys on the rule routes, a change route, the `session_rule_changes` history
enforced in `GuardRuleWrites`, and `cc-devthrottle rules`. Read `phase-1-plan.md` (rulings P1-P18 - P18 is
the one for this task) and the phase 2 change itself (`git show 46ab20e2e`).

1. `git fetch origin; git merge origin/main`. Resolve the conflicts in `SessionRuleEndpoints.cs`,
   `SessionRuleWire.cs` and `SessionRuleStore.cs` keeping BOTH phases' behaviour, and check every file git
   merged on its own in the same area (`SessionRule.cs`, `SessionRuleRecordRules.cs`, `GatewayDbContext.cs`,
   the `cc-devthrottle rules` command).
2. Regenerate `AddSessionRuleKindsAndHookFirings` on top of main for BOTH providers, as commit `7f8420187`
   did (same schema change, timestamp after main's latest, both snapshots holding everything, the
   hand-restored Postgres `defaultValueSql` lines kept, the migration tests naming the right previous
   migration). If phase 2's history table needs the phase 1 columns in its before and after, that is
   part of this migration.
3. Build P18, each part with a test written first and seen failing: the phase 1 fields in the change
   route and in the history's before and after; the write gate on a change; `kind` immutable; hook rule
   promotion refused for a session; W1 and W5 still refusing a session key. If `cc-devthrottle rules`
   needs the phase 1 fields to create or show a hook rule, add them there with tests in
   `tools/cc-devthrottle` (run that suite too).
4. Run: `~/.dotnet/dotnet test src/CcDirector.RuleHook.Tests`; `src/CcDirector.Core.UnitTests --filter
   "FullyQualifiedName~Hook|FullyQualifiedName~SessionPointerDrop|FullyQualifiedName~Rules"`;
   `src/CcDirector.Gateway.UnitTests --filter
   "FullyQualifiedName~Rules|FullyQualifiedName~SessionRuleKindsMigration|FullyQualifiedName~GatewayHostBootSmoke|FullyQualifiedName~MigrationChain|FullyQualifiedName~SessionKeyGuard"`;
   the Postgres `SessionRuleKindsPostgresTests` on a throwaway `postgres:16` as before, destroyed after; and
   the `tools/cc-devthrottle` tests if you touched it.

Environment: `export PATH=$HOME/.local/bin:$HOME/.local/share/node/bin:$PATH`; .NET 10 is `~/.dotnet/dotnet`;
`DOTNET_USE_POLLING_FILE_WATCHER=1`. Commit with `git -c user.name=thefrederiksen -c
user.email=soren@duksrevo.com commit`. No assistant, model or vendor names and no attribution lines
anywhere. Plain English. Nothing hidden, no sub-agents. Do not touch the pull request, do not merge to main.
Push your branch and report one line: `cc-devthrottle session handback "<branch> <commit> done: <tests>"`.

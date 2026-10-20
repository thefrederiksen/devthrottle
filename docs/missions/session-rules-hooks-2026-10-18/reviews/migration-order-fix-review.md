# Review of commit 2313fcd1d - "Fix main: the newest migration's Designer carries Session Rules phase 1"

Reviewer seat: Session Rules - Reviewer - migration order fix (session adec3e85, machine devlinux).
Reviewed on 2026-10-20 in the worktree `devthrottle-session-rules-migfix`, whose branch
`fix/session-rules-migration-order` is origin/main (5d671bbb7) plus this one commit. origin/main was
fetched before reading. I did not write the change and I changed nothing in it; the tree is clean.

## Verdict: MERGE

No finding proves harm. Every sharp question in the brief was answered with evidence, not by reading
the commit message.

## Scope

- The four files in the commit: the two `AddMailDeskDecisionAttempts` Designers (SQLite and PostgreSQL)
  and the two `AddTriggerBackupMachines` tests (SQLite unit test, PostgreSQL database test).
- The whole five-migration tail of both providers' chains (`AddSessionRuleChanges`,
  `AddSessionRuleKindsAndHookFirings`, `AddTriggerBackupMachines`, `AddMailDeskChannelChecks`,
  `AddMailDeskDecisionAttempts`) and both model snapshots.
- What production actually applied, from the deploy workflow's run history.
- Not reviewed: anything outside the migration chain; the Session Rules phase 1 feature itself.

## What I checked, and what it showed

### 1. Is the Designer now exactly the model snapshot for each provider?

Yes. I extracted the body of `BuildTargetModel` from each `AddMailDeskDecisionAttempts.Designer.cs` and
the body of `BuildModel` from each `GatewayDbContextModelSnapshot.cs` and diffed them.

| Provider   | Designer body lines | Snapshot body lines | diff      |
|------------|---------------------|---------------------|-----------|
| SQLite     | 6658                | 6658                | identical |
| PostgreSQL | 6781                | 6781                | identical |

### 2. Does the snapshot itself match the model?

Yes, for both providers. Ran from this worktree with .NET 10 and dotnet-ef:

```
dotnet ef migrations has-pending-model-changes --project src/CcDirector.Gateway --startup-project src/CcDirector.Gateway --context GatewayDbContext
  -> No changes have been made to the model since the last migration.
CC_GATEWAY_EF_PROVIDER=postgres dotnet ef migrations has-pending-model-changes --project src/CcDirector.Gateway.Migrations.Postgres --startup-project src/CcDirector.Gateway.Migrations.Postgres --context GatewayDbContext
  -> No changes have been made to the model since the last migration.
```

So Designer == snapshot == model on both providers. The repository's own guard for this,
`FleetOutcomeStopIdentityMigrationTests.TheNewestMigrationsDesigner_IsDiscovered_AndCarriesTheCurrentModel`
(derives the newest migration from the assembly, diffs its Designer against the model with
`IMigrationsModelDiffer`, and checks `HasPendingModelChanges`), passes for both providers (see section 5).

### 3. Does anything else in the chain still disagree with the model?

The two Designers between phase 1 and the newest migration - `AddTriggerBackupMachines` and
`AddMailDeskChannelChecks`, both providers - do not carry the phase 1 columns (zero occurrences of
`HookEvent`; the phase 1 Designer and the fixed newest Designer each have two). That is inherent in the
chosen fix, which keeps the phase 1 id in the middle of the chain, and it is harmless:

- At runtime EF uses a migration's TargetModel only to generate SQL for that migration's own operations
  (and the previous migration's TargetModel for its Down). I listed every operation after phase 1: they
  touch only `triggers` (add two columns, one UPDATE), `mail_desk_channel_checks` (create table) and
  `mail_desk_decisions` (add two columns). None touches `session_rules` or `session_rule_firings`, so a
  stale TargetModel cannot produce wrong SQL for them. On SQLite the only table rebuilds are the two
  Downs (`triggers`, `mail_desk_decisions`), and those tables are correct in the preceding Designers.
- `dotnet ef migrations add` diffs against the snapshot, not a Designer, and the snapshot is current.

One observation, not a finding (no harm today, and outside this commit's stated scope): if anyone later
runs `dotnet ef migrations remove` on `AddMailDeskDecisionAttempts`, EF rebuilds the snapshot from the
`AddMailDeskChannelChecks` Designer, which lacks phase 1, and the next `migrations add` would re-add the
phase 1 columns. That hazard existed before this commit and the commit does not widen it. Worth a
sentence in the chain's documentation; not a reason to hold the merge.

### 4. Will a production database start cleanly, and will a fresh database build the same schema?

Yes to both, proven on a real PostgreSQL rather than argued.

First the facts from the deploy history (`gh run list`, workflow "Deploy hosted Gateway", 2026-10-20):

| Run          | Time (UTC) | Commit    | Holds the Mail Desk migrations | Holds phase 1 |
|--------------|------------|-----------|--------------------------------|---------------|
| 39381793396  | 06:40      | 9f9ad80a8 | yes                            | no            |
| 39431533659  | 13:34      | 5d671bbb7 | yes                            | yes           |

So production's history table received the three Mail Desk migrations at 06:40 and phase 1 alone at
13:34 - the opposite of id order. The commit message's reason for keeping the phase 1 id is therefore
exactly right: renaming it would make the next start try to add columns that already exist.

I wrote a throwaway test (not committed; deleted after the run) in the database-test project that
replays that order against a throwaway PostgreSQL 16 in Docker:

1. Migrate to `AddSessionRuleChanges`.
2. Execute the SQL `IMigrator.GenerateScript(fromPhase1, toDecisionAttempts)` produces - which is the
   three Mail Desk migrations and their history rows, with phase 1 absent - as the 06:40 deploy did.
   Asserted the history table ends `AddTriggerBackupMachines, AddMailDeskChannelChecks,
   AddMailDeskDecisionAttempts`.
3. `GetPendingMigrations()` returns exactly `AddSessionRuleKindsAndHookFirings`; `Migrate()` applies it;
   nothing is pending afterwards; `HasPendingModelChanges()` is false. History now ends
   `AddMailDeskDecisionAttempts, AddSessionRuleKindsAndHookFirings` - production's state today.
4. A further `Migrate()` (the next start, with this fix) has nothing pending and is a no-op.
5. A second, fresh database built by `Migrate()` in id order: its full `gateway` schema (every column
   with type, nullability, default and length; every index definition; every constraint definition)
   is string-equal to the replayed production database's schema, and that schema holds
   `session_rules.HookEvent`, `triggers.BackupMachinesJson` and `mail_desk_decisions.AttemptCount`.

The test passed (11 s).

### 5. Did the test change weaken what the tests prove?

No. Both `AddTriggerBackupMachines` tests assert `Before == all[all.IndexOf(UnderTest) - 1]`: the named
"before" must be the migration immediately preceding the one under test in the assembly. Before the
commit the constant named `AddSessionRuleChanges`, which stopped being the predecessor the moment
phase 1 merged - that assertion is what went red on main. The commit changes only the constant, to the
real predecessor; the assertion, the row-preserving data checks, the `HasPendingModelChanges()` check
on the full chain, and the Down checks are untouched. Migrating to the real predecessor is in fact a
stronger arrangement than before: the old constant would have had phase 1 applied silently in between
by `Migrate(UnderTest)`.

The reworded summaries are accurate: created before, merged after, applied in production under that id.

Test runs in this worktree (all green):

| Suite | Filter | Result |
|-------|--------|--------|
| `CcDirector.Gateway.UnitTests` (SQLite, no database) | `~Migration` and `~GatewayHostBootSmoke` | 46 passed, 0 failed |
| `CcDirector.Gateway.DatabaseTests` (PostgreSQL 16 in Docker, rig promise set) | `AddTriggerBackupMachinesPostgres`, `SessionRuleKindsPostgres`, `MailDeskDecisionAttemptsPostgres`, plus the throwaway replay above | 4 passed, 0 skipped |

The unit run includes `TheNewestMigrationsDesigner_IsDiscovered_AndCarriesTheCurrentModel` (both
providers), `SessionRuleKindsMigrationTests` (phase 1's own Designer still carries its columns), and
`AddMailDeskDecisionAttemptsMigrationTests` (still names `AddMailDeskChannelChecks` as its predecessor,
which is still true).

## Findings

None that prove harm.

## Notes for the Architect (not blocking)

- The `migrations remove` hazard in section 3. Pre-existing; a one-line warning near the chain would do.
- The database tests skip silently under a plain `dotnet test` unless `CC_TEST_REQUIRE_POSTGRES` names a
  rig whose database names match; I ran them with a hand-built rig (`ccpgproof_review`,
  `ccpgstats_review`, role `gateway_app_review`) because `scripts/test-database.ps1` needs PowerShell,
  which this Linux machine does not have. Nothing in the commit depends on that.

# Brief - Developer, urgent: the archive migration is missing for PostgreSQL

Round 2 track B (pull request 3608, merged as ca403bdef) added three nullable columns to the factory registry with
the migration `20261007035034_ArchiveFactories` - but ONLY in `src/CcDirector.Gateway/Data/Migrations` (the local
database). It has no counterpart in `src/CcDirector.Gateway.Migrations.Postgres/Migrations`, so the hosted Gateway's
PostgreSQL database never got the columns, and after the deploy every factory registry query failed ("internal
error" on the Factories page, `factory list` and `factory register`). The deploy said "No Postgres migration change".
The Lead rolled production back to 43fe9b6.

Your task, one pull request: add the PostgreSQL migration and its model snapshot change, generated the way phase A
generated `AddFactoryRegistry` in both projects (look at how that pair was made, and at any script or doc in the
repository for adding a PostgreSQL migration). Then prove it: the model-consistency / migration-chain tests for BOTH
providers, and a test (or the existing guard, if there is one) that fails when an entity change has a local migration
and no PostgreSQL one - if no such guard exists, say so in the pull request and propose it; do not build a large one
now. If Docker is available, apply the migration chain to a real throwaway PostgreSQL (`.\scripts\test-local.ps1
-Parked` builds one) and show the registry query works.

Worktree: `D:/ReposFred/_wt/factories-screen-pgfix`, branch `factories-screen/pg-migration`, from origin/main.
Commit `type(scope): description`, NO attribution. Push, `gh pr create`, then `cc-devthrottle message send b496c54d
"<one line>"`. Never deploy. This is urgent: the owner's Factories screen waits on it.

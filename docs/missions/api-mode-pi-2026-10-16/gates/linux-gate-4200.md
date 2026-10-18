# Linux gate: pull request #4200

**Verdict: NOT FINISHED.** CcDirector.Core.UnitTests and CcDirector.Gateway.UnitTests pass, and
every migration and model-snapshot test passes. CcDirector.Gateway.Tests could not be judged: the
machine ran out of inotify instances during both the branch run and the main run, so most of its
failures say nothing about the code. It must be run again once the limit is raised.

Linux gate under the owner's decision of 18 October; Windows re-check pending.

- Pull request: #4200, branch `api-mode-durable-conversation`
- Branch head: `74fbafddc559c02f95fac04a62948a9c00b1eca` (one documentation-only commit on top of
  `9063dc5c1`; nothing was run on `9063dc5c1`)
- Merge commit tested: `4983bbdafb29870a63574d223c47b4d51339fb60`, from `git merge --no-edit
  origin/main` with no conflict
- Main commit for the baseline: `7a365544b0143bf6b6fd88e49cb8255f04b3a017` (it contains the
  #4179 merge, `266f16865`)
- Machine: devlinux, .NET 10.0.301, `DOTNET_USE_POLLING_FILE_WATCHER=1`, one suite at a time
- Filter on every suite:
  `Gate!=Quarantine&FullyQualifiedName!~PythonToolsHealAndShimTests&FullyQualifiedName!~ToolReconcilerTests`
- After every suite, `~/.local/bin/cc-devthrottle` still pointed into
  `~/.local/share/cc-director/pyenv/bin`.

## Why these suites

The pull request changes the Gateway and adds the database migration `AddApiModeConversationItems`
for both providers (`src/CcDirector.Gateway/Data/Migrations` and
`src/CcDirector.Gateway.Migrations.Postgres/Migrations`, each with its model snapshot). So it runs
CcDirector.Gateway.UnitTests and CcDirector.Gateway.Tests, plus CcDirector.Core.UnitTests, which
always runs.

## Results

| Suite | Pull request: passed / failed / skipped | Main: passed / failed / skipped |
|---|---|---|
| CcDirector.Core.UnitTests | 2,400 / 5 / 23 | 2,399 / 6 / 23 |
| CcDirector.Gateway.UnitTests | 14,452 / 4 / 2 | 14,423 / 4 / 2 |
| CcDirector.Gateway.Tests | 1,588 / 193 / 1, not judged | 1,658 / 123 / 1, not judged |

**Migration and model-snapshot tests:** every test whose name concerns a migration, the model
snapshot, a pending model change or the conversation items passed on the branch: 152 in
CcDirector.Gateway.UnitTests and 1 in CcDirector.Gateway.Tests, with none failed. The real
PostgreSQL migration tests live in `CcDirector.Gateway.DatabaseTests` and need Docker
(`scripts/test-database.ps1`); they were not run here.

## Core and Gateway unit tests: no failure counts

- CcDirector.Core.UnitTests: the branch's 5 failures are the same Windows-path tests that fail on
  main. Main had one more,
  `ApiModeSessionHostTests.TurnStartBound_PiAcknowledgesAndNeverStarts_TurnFailsAndPiRestartsOnTheConversationWithoutResending`,
  which passed on the branch. It fails on main only and does not count against the pull request.
- CcDirector.Gateway.UnitTests: the 4 failures are identical on main
  (`DefinitionFormatDocumentTests`, `HeldDeliveryDriverTests`, `VoiceAutoOffLineTests`,
  `VoiceUnheardCountTests`). The pull request's 29 new tests all pass.

## Gateway.Tests: why it was not judged

183 of the branch's 193 failures, and 114 of main's 123, are
`System.IO.IOException: The configured user limit (128) on the number of inotify instances has been
reached`. The limit is per user, across every process on the machine. After the runs, with no test
running, other processes held 126 to 127 of the 128 instances. The installed Director held 11, and
browsers, a local model server and desktop services held the rest. `DOTNET_USE_POLLING_FILE_WATCHER=1`
was set, but it does not cover this. Earlier on 18 October, with fewer instances in use, the same
suite failed only 10 tests on main. Which tests hit the limit is a matter of timing, so 162 tests
failed on the branch only because of it, and 92 on main only.

The failures that are NOT the inotify error were checked one by one:
- `ContextLessRouteCensusTests.The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families`
  failed on the branch. On main it was hidden by the inotify error, so it was run alone on both
  sides. It fails alone on main `7a365544b` with the same assertion (the expected route list
  includes `DELETE /exes/slots/{n}`), and it also failed on main `dac07950f` earlier. It does not count.
- `TestStorageRootRedirectTests.CcStorageRoot_ResolvesUnderTheTemporaryRoot_NotTheOwnersRealStorage`
  failed on the branch and passed on main. Run alone on the branch, it passed, so it is
  **failed-once**, not blocking.
- The other 8 are failures with the same names on main.

## To finish this gate

Raise the limit (`sudo sysctl fs.inotify.max_user_instances=1024`, the owner's step since it needs
`sudo`), then run CcDirector.Gateway.Tests again on the same two commits and compare. This record is
updated with that result.

## What this gate does not cover

- Windows.
- The PostgreSQL database tests, which need Docker.
- Gateway.Tests, until it is run again.

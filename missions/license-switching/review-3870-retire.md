# Review: PR 3870 - retire the Accounts dialog

Reviewed head: `2b3916b39516a986ab66e4247238f7234d850da9` (`origin/main...HEAD`).

## Verdict

One finding: the storage migration still copies the retired token-bearing `accounts.json` file.

## Accepted conditions

1. **`cc-devthrottle` ships with the Director and is reachable on Windows and Mac: partially evidenced.** The PR description includes output showing an installed executable on each platform and a Windows shim / Mac symlink to the installed copy. Those outputs also show both installed copies at v1.3.0 returning "No such command 'license'"; the description says the license commands arrive with the next release. The own-copy resolver is in the separately open PR 3868, not this diff. I could not independently inspect either machine, so the evidence supports executable presence and shell reachability, but does not prove the license commands are currently usable on those installations or that PR 3870 alone gives the Director its own-copy path.
2. **Existing `accounts.json` files are not deleted: deletion condition met; stop-use condition not met.** This diff adds no deletion, but startup migration and two Python migration utilities still copy legacy `accounts.json` files. See finding 1.
3. **Replacement is stated: met.** The PR description names the command line as the current replacement and the Cockpit Licenses page as the phase 3 replacement.

## Finding

### Legacy storage migration still copies sign-in tokens

`App.axaml.cs:363` calls `CcStorageMigration.EnsureMigrated()`. When the legacy `%LOCALAPPDATA%\CcDirector` directory exists, `CcStorageMigration.MigrateDirectorConfig()` calls `CopyFileIfNewer` for its `accounts.json` at `src/CcDirector.Core/Storage/CcStorageMigration.cs:79`. That helper uses `File.Copy` when the destination is missing or stale (`:171`, `:182`, `:190`) and retains the source. Contract section 1.1 says these account records contain `AccessToken` and `RefreshToken`.

So a startup with a legacy account file still writes a second token-bearing JSON file after this PR retires the store and dialog. This is the same duplicate-holder problem the retirement is meant to remove. The added `RetiredAccountsFileIsLeftAloneTests` explicitly permits the copy, and its C#-only scan does not cover the Python copies at `scripts/migrate-storage.py:166` and `tools/cc_storage/migrate.py:58`.

## Other repository findings

I found no remaining product call sites for `ClaudeAccountStore` or `AccountsDialog`. Repository-wide tracked-file search also found historical documentation/research mentions, an error-message test fixture, and `tools/gateway-load`'s unrelated synthetic `accounts.json`; none consumes the retired Director store. The only remaining product paths naming `accounts.json` are the migration paths described above.

The intended license key store still persists sign-in keys: `tools/cc-devthrottle/src/license_store.py:332` writes the Windows/Linux key to its `.credentials.json` location, and the macOS backend writes it to Keychain. Its add/switch/refresh operations use the designated store and move or replace the key there. I found no surviving `ClaudeAccountStore` writer.

## Scope and proof

- Read `missions/license-switching/MISSION.md` section 5, `missions/license-switching/contract.md` section 1.1, the full `origin/main...HEAD` change summary and all non-deletion hunks, and the PR 3870 description. I inspected the linked PR 3868 evidence and diff for `ResolveOwnCopy`; GitHub reported that PR open and not merged.
- Searched the tracked `HEAD` tree (10,031 tracked paths) for `accounts.json`, `ClaudeAccountStore`, `ClaudeAccount`, and `AccountsDialog`, and searched source under `src`, `apps`, `packages`, `scripts`, and `tools` for token references. This includes the web-client and script trees. I could not independently reach the Windows or Mac machines; condition 1 above relies on the terminal output included in the PR description.
- Ran only the specific affected test projects:
  - `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --verbosity minimal` - 4,934 passed, 1 failed, 0 skipped (4,935 total; 6m 37s). `WorktreeInventoryIntegrationTests.CancelledWhileListingWorktrees_PropagatesCancellation_NotAFailure` failed while deleting its temporary `primary` directory because another process held it. This test is outside the changed behavior; I cannot determine from this run whether the failure is environmental or a test defect.
  - `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --verbosity minimal` - 1,519 passed, 0 failed, 0 skipped.
  - `dotnet test src/CcDirector.Avalonia.Tests/CcDirector.Avalonia.Tests.csproj --verbosity minimal` - 1,036 passed, 0 failed, 0 skipped.
- I did not run the full solution, modify product code, commit, or push.

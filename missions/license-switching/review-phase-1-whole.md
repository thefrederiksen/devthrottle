# License Switching — phase 1 whole-phase review

**Verdict: Findings. Phase 1 does not yet meet all of its stated invariants.** One integration gap makes unused spares expire; an upgrade gap can leave old token copies on disk.

## Scope

Reviewed HEAD e3c5e1f5a (origin/main, detached) and the merged commits for PRs 3857 (cbf37c4a2), 3858 (be7d0cc04), 3863 (7207db5b8), 3868 (a5393eb12), 3869 (2e2019f75) and 3870 (76c37c54e). Read missions/license-switching/MISSION.md sections 5 and 7, the full contract.md, and phase-1-report.md.

Inspected the merged Python key store, usage and refresh code, CLI wiring, and the Director command adapter, reporter, switch coordinator, setting, executor and session-input guard/dialog path. Also read the committed Windows and Mac live-exchange proof outputs and README.

**Ran:** read-only source and Git inspection (git status, git log, git show --stat, rg, and file reads). **Did not run** unit tests or live proofs and made no worktree edits.

**Could not reach:** the owner's machines, their existing accounts.json files, a live Claude login, macOS Keychain, or the Gateway. The committed exchange proof logs were read, not reproduced. In particular, I did not run the 24-hour spare check or the usage-limit-dialog tests.

## Findings

### P1 — The Director never refreshes an unused spare

LicenseUsageReporter.BuildReportAsync calls ReadUsageAsync for the active holding and, on its spare-read interval, for all holdings (src/CcDirector.Core/Licenses/LicenseUsageReporter.cs:212-220). The production adapter constructs license usage and optionally adds only --active; it never passes --refresh (src/CcDirector.Core/Licenses/LicenseCommandLine.cs:132-136). The CLI defaults its --refresh option to false, and license_usage_ops.usage calls read_with_refresh only when that flag is true; otherwise it calls read_usage (tools/cc-devthrottle/src/cli.py:1759-1768, tools/cc-devthrottle/src/license_usage_ops.py:87-102).

An expired access token therefore becomes an unauthorized reading instead of being renewed (tools/cc-devthrottle/src/license_usage.py:405-431). The switch coordinator refuses a spare whose last read is unauthorized or refresh-failed (src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:191-195). With no manual refresh, an unused spare stops being a viable target after its short-lived access token expires. That defeats the mission's requirement to keep unused spares fresh (MISSION.md section 5, trap 5; contract.md section 2.3). The phase report itself says the 24-hour check is still open (phase-1-report.md:70-73).

**Required to close:** wire the Director's spare read through the refresh path and verify that the actual Director-to-CLI path renews an expiring spare and leaves it readable.

### P1 — Retiring ClaudeAccountStore leaves its old token copies behind

The contract says the old ClaudeAccountStore wrote the active sign-in's access and refresh tokens into accounts.json beside the live credentials, creating the exact duplicate trap 2 forbids (contract.md:42-56). PR 3870 retires that writer, but the phase report explicitly says existing accounts.json files are not deleted (phase-1-report.md:16). The new retention test describes those files as holding sign-in tokens and enforces that Director code and storage migrations do not name the file (src/CcDirector.Core.UnitTests/Background/RetiredAccountsFileIsLeftAloneTests.cs:4-44).

On an existing installation that used the old account store, an old token copy can therefore remain on disk alongside the live .credentials.json or Keychain item. Stopping future writes does not restore the one-location invariant for those installations; the exchange code does not migrate or clear the retired file. This review could not inspect the owner's private machine files, so the finding is about affected upgrades, not a claim that every installation has such a file.

**Required to close:** define and verify an upgrade path that removes or otherwise safely disposes of the old token copies. Until then, the one-place key invariant cannot be claimed across upgrades.

## Other requested traps

- **oauthAccount follows the exchanged key:** the journaled store steps copy the key with its account block and verify active/spare readback. The committed Windows and Mac proof logs show the expected active account before and after exchange and verified switch-back. I found no cross-PR defect in this path by inspection; I did not rerun its tests or live proof.
- **Only Escape reaches the usage-limit dialog:** each ConPTY session wraps its backend with UsageLimitInputGuard; it permits an automatic write only when it is exactly one Escape byte, and the settled-screen path sends Escape once. The dialog recognition has explicit certain and conservative checks. No source-level gap found; the committed live exchange proof does not exercise this dialog.
- **Feature off leaves the real default login untouched:** the setting defaults false; startup returns before starting the reporter, and the coordinator rejects before reading or switching when disabled. The Python store also refuses the real default folder. No source-level gap found.
- **No key leaves the machine:** the contract message types carry account/usage metadata, not sign-in tokens; the adapter clears untrusted read-error detail before reports are built. No source-level route carrying a Claude sign-in key to the Gateway found.


# License Switching - review of pull request 3877 (phase 1 review fixes)

**Verdict: Approve. No finding blocks the merge.** Finding 1 is fixed on the path the Director actually runs, the
key stays in one place, a refresh cannot corrupt a switch, and the answer to finding 2 is sound. Two minor
observations follow; neither is required for this pull request.

## Scope

**Read:** `git diff origin/main...HEAD` (commits 71e8d4b3a, 16a10abc8, a2eb5446f): the one-line change in
`src/CcDirector.Core/Licenses/LicenseCommandLine.cs` and the two new tests in `LicenseCommandLineTests.cs`;
`review-phase-1-whole.md` and `review-phase-1-whole-answers.md`; MISSION.md section 5; contract.md sections 2.3
and 5.3-5.4 and the reason codes. Also the code the fix leans on: `license_usage_ops.usage`,
`license_usage.read_with_refresh` / `refresh_spare` / `_refresh_holding_the_lock` / `StoreKey.exclusive`,
`license_store._StoreLock` and `exclusive()` / `store_refreshed_key`, `LicenseUsageReporter.BuildReportAsync`,
`LicenseSwitchCoordinator` (the run gate, step 9 and the exchange step), and the retired `ClaudeAccountStore`
at `76c37c54e^`.

**Ran:** `dotnet test src/CcDirector.Core.UnitTests --filter FullyQualifiedName~LicenseCommandLineTests` -
23 passed, 0 failed (both new tests among them).

**Could not run:** the Python license tests. With the system Python, 10 of 102 selected tests fail with
`ValueError: stderr not separately captured` from Click's test runner - a Click version mismatch in this
machine's Python, not this change (the pull request touches no Python). The tool's own `.venv` has no pytest,
and there is no `uv.lock`. I read the Python tests that cover the refresh races instead of running them.

**Could not reach:** the live proof (read, not reproduced - its expired key was spent on the run), a running
Director with its reporter and Gateway, macOS Keychain, the owner's real `accounts.json` files. The Gateway's
switch decision is not on main yet, so its reaction to a refused switch is judged from contract.md 5.3-5.4.

## Question 1 - does every Director spare read now renew an expiring spare, with the key in one place?

**Yes.** The reporter makes exactly one call that reads spares, `ReadUsageAsync(activeOnly: !readSpares)` in
`BuildReportAsync`, and the adapter now maps `activeOnly: false` to `license usage --refresh`. The CLI passes
that through to `read_with_refresh` for every held key. For a spare with under an hour left it renews under
the store lock and writes the renewed key back with `store_refreshed_key`, which replaces the key in the folder
that already holds it - no copy is made. The active key is refused inside the lock (`REFUSED_ACTIVE`) and read
as it is, so the sessions' own renewal is never raced. The active-only read (every 2 minutes, and the
coordinator's verify read) gets no `--refresh`, which is right: it reads no spare.

The new adapter tests run the real `CcDevthrottleLicenseCommandLine` against a stand-in tool and pin both argument
lists; the answers file's revert proof is consistent with that. The live proof's fingerprints (renewed refresh
token in exactly one place, active refresh token unchanged) are the right evidence for the one-place rule.

The 10-minute spare interval against the 60-minute margin gives about six attempts before a spare dies, which
meets MISSION.md trap 5. The 24-hour check itself is still owed by the phase, as the phase report says.

## Question 2 - can a refresh race a switch?

**Not harmfully.** I traced each ordering:

- **Refresh holds the lock, a switch arrives.** `_StoreLock` is non-blocking, so the tool's switch fails at
  once with `switch-in-progress` before touching anything. The coordinator returns `failed` /
  `switch-in-progress`; contract.md 5.4 ends that row, and the Gateway decides again on its next tick. Cost:
  one failed log row and about a minute's delay. No key is touched. See observation B for why this window is
  now wider than before.
- **A switch holds the lock, a refresh is due.** `StoreBusy` becomes `BUSY`; nothing is sent to the token
  endpoint and the key is read as it is (`test_read_with_refresh_StoreBusy_ReadsTheKeyAsItIsInsteadOfBarringIt`).
- **A switch finished between the listing and the refresh.** `StoreKey.exclusive` re-reads where the account is
  now under the lock; an account made active is refused, not refreshed in its old folder
  (`test_refresh_spare_ASwitchFinishedFirst_...`, `test_read_with_refresh_ASwitchFinishedFirst_...`).
- **The provider rotates the token and the write then fails.** Reported plainly as needing a new sign-in, never
  silent. A switch cannot slip into that gap because the read, the post and the write are one locked step.

## Question 3 - is the answer to finding 2 sound?

**Yes.** I checked the retired `ClaudeAccountStore` at `76c37c54e^`: it only ever copied the live tokens IN
(`CaptureFromCredentials`, `RefreshActiveTokenFromCredentials`); it never wrote them back to `.credentials.json`
and never called a token endpoint. Nothing on main reads `accounts.json` now. So the leftover copy cannot renew,
and trap 2's harm - one copy's renewal cancelling the other - cannot come from it. What remains is an exposure at
rest, and the answer names it as that and gives the decision to the owner in phase 5. Not deleting user files
without the owner's word is correct.

One precision the answer should carry (not a defect): on a machine that still runs a Director older than
pull request 3870 - an older installed build or a development slot - that Director keeps refreshing the copy
from the live `.credentials.json`. The exposure is therefore not only an old, stale token; it is a current copy
of the live key, rewritten until every Director on the machine is upgraded. It is still never used to renew,
so the conclusion holds. The phase 5 cleanup decision should say "after every Director on the machine is at or
past 3870".

## Observations (minor, not required to close)

**A. A token-endpoint blip in a spare's last hour bars a still-valid spare for up to 10 minutes.** In
`read_with_refresh`, every `RefreshError` except `BUSY`, `REFUSED_ACTIVE` and `STORE_UNREADABLE` becomes
`refresh-failed` and nothing is read - including `NETWORK` and `RATE_LIMITED`, where nothing was spent and the
key may still have most of an hour. The coordinator's step 9 refuses a `refresh-failed` spare with "needs a new
sign-in", which is false in that case, until the next spare read 10 minutes later. Before this change the
Director never reached this code, so the path is new to the Director. It needs a network failure inside the
narrow window and clears on its own, so it does not block. Suggested later: for `NETWORK` and `RATE_LIMITED`
with the access token still unexpired, read the key as it is, as `BUSY` already does.

**B. The lock window a switch can collide with is wider.** Before, the reporter took the store lock only for the
brief listing. Now it holds it across a token-endpoint POST (up to the 20-second timeout) for each due spare.
A Gateway switch arriving then is refused `switch-in-progress` and retried next tick (question 2). This happens
about once per spare per 7 hours and costs a minute; worth knowing when reading a switch log, not worth
changing now.

Nothing here touches the "only Escape" or "feature off" invariants; the change does not reach either path.

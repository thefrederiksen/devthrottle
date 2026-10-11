# License Switching - answers to the phase 1 whole-phase review

Answers `review-phase-1-whole.md`. The Tech Lead who built phase 1 has left, so the Developer seated for the
review findings answers it.

## Finding 1 - The Director never refreshes an unused spare: ACCEPTED, fixed

The finding is right. `CcDevthrottleLicenseCommandLine.ReadUsageAsync` built `license usage` with `--active` or
nothing, and the tool renews a spare only under `--refresh`. Nothing else uses an unused spare's key, so its
8-hour access token expired, every later read answered `unauthorized`, and the switch coordinator refused it
as a target.

**The fix** (`src/CcDirector.Core/Licenses/LicenseCommandLine.cs`): a read of every holding now passes
`--refresh`; the active-only read still passes `--active` and nothing else. That is the one call the usage
reporter makes on its spare read (`ReadUsageAsync(activeOnly: false)`, at most every 10 minutes), so each
spare is renewed once it has under an hour left - several chances before it expires. `--refresh` never
touches the active key (`read_with_refresh` renews spares only; the sessions renew the active one), and a
renewal that finds the store busy sends nothing and is retried on the next round, so the flag adds no new
way to harm a running switch.

The active-only read (every report, and the switch coordinator's re-read) is deliberately NOT given
`--refresh`: it reads no spare, so there is nothing for it to renew.

**Tests, through the real adapter** (`LicenseCommandLineTests`): the production `CcDevthrottleLicenseCommandLine`
is pointed at a stand-in `cc-devthrottle` that records the arguments it receives.
- `ReadUsageAsync_EveryHolding_RunsTheToolWithRefreshSoAnUnusedSpareIsRenewed` - the full read runs
  `license usage --refresh --json --default-config-dir ... --store-dir ...`.
- `ReadUsageAsync_ActiveOnly_RunsTheToolWithoutRefresh` - the active read runs `license usage --active --json`.

Revert proof: with the old line put back, the first test fails ("Collections differ") and nothing else in
`LicenseCommandLineTests` does; with the fix, all pass.

**Live proof** - see the section below.

## Finding 2 - Retiring ClaudeAccountStore leaves its old token copies behind: answered, no change in this pull request

No file is deleted or edited here, by instruction: removing or editing anyone's `accounts.json` destroys data
on the owner's machines, and the owner has not granted that.

What the finding describes is real but is not the harm trap 2 guards against:

- **Nothing reads `accounts.json` any more.** Pull request 3870 removed the account store, its dialog and
  every reader, and `RetiredAccountsFileIsLeftAloneTests` fails the build if Director code or a storage
  migration names the file again. The license code never looked at it: the key store reads the default
  folder's `.credentials.json` (or the Keychain) and its own spare folders only.
- **So no refresh can ever happen from that copy.** Trap 2's harm is one live copy renewing and thereby
  cancelling the other - a refresh rotates the refresh token and kills the other copy's access token at
  once (measured, `spare-refresh.md`). A copy that no program reads cannot renew, so it cannot cancel the
  live key, and the one-place rule for every key in USE holds across the upgrade.
- **What is left is an exposure at rest.** A leftover `accounts.json` can still hold an old token on disk.
  Its access token is long expired, and its refresh token is spent the first time the live key renews
  (`invalid_grant`, `spare-refresh.md`), but until then it is a readable secret in a file nobody needs.
  Whether to remove such files, and how (a one-time cleanup, a prompt, or leave it to the owner), is the
  owner's decision. It belongs to phase 5, which is where the mission first touches the machines' real
  folders.

## Live proof (finding 1)

To follow in this file once the spare's access token has expired (21:33 UTC, 11 October).

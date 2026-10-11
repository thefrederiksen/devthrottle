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

**Live proof**: an expired spare was renewed and read through the Director's own adapter. See the section below.

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

Run on the Windows machine on 11 October 2026 at 21:34 UTC, in ISOLATED folders only, inside the test root
the live exchange proof marked. The machine's real default Claude login was not touched.

**Setup.** Account C had a sign-in of its own in an isolated folder, made at 09:33 UTC, whose access token
expired at 21:33:34 UTC. Its refresh token was distinct from every other key in the test root. It was MOVED
(not copied) into a fresh isolated store with `cc-devthrottle license add`, after which the source folder held
no key. The isolated default folder was signed in to account A.

**What ran.** A scratch console program referencing `CcDirector.Core` built the production
`CcDevthrottleLicenseCommandLine` (its tool seam pointed at the checkout's `tools/cc-devthrottle/src`, run with
the installed tool's own Python environment, because the installed tool predates the `license` command) and
made exactly the reporter's spare read: `ReadUsageAsync(activeOnly: false)`. It printed the first 10 hex
characters of each token's SHA-256, never a key, and replaced email addresses with labels. Its output:

```
now: 2026-10-11 21:34:10 UTC
before: spare access token e6099b39a5, refresh token 14bfa3bd34, expires 2026-10-11 21:33:34 UTC (EXPIRED)
row: account A (active), readError=none, fiveHour=11%, sevenDay=90%, readAt=21:34:11
row: account C (spare), readError=none, fiveHour=0%, sevenDay=99%, readAt=21:34:11
after: spare access token 626f241fce, refresh token d30e671fd7, expires 2026-10-12 05:34:11 UTC (valid)
```

**What it shows.** The spare went in with an EXPIRED access token and came out renewed (new access token,
rotated refresh token, 8 hours of life) and READ with no error - a usable switch target. Afterwards every key in
the test root was fingerprinted: the renewed refresh token exists in exactly one place (the store's spare
folder), and the active account A's refresh token is the same as before the run, so the active key was not
renewed.

**Not shown here.** The run does not include a control read of the same expired key without `--refresh`: one
key could be expired only once, and it was spent on the proof. The control is the adapter test and its revert
proof above, together with the code path the review cites (`read_usage` answers `unauthorized` for an expired
key). The proof drove the adapter call, not a whole running Director with its reporter and Gateway connection.

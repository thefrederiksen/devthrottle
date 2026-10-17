# Phase 6, track B: the Director keeps the active sign-in alive

## Why

On the night of 16 October a machine's Director died. With nothing running on the machine, its ACTIVE
sign-in's 8-hour access token expired and the Licenses page said "Sign-in needed". `license refresh` keeps
spares fresh but by design never touched the active key (`spare-refresh.md` decision 3: running sessions
renew it themselves, and a renewal from outside would end their token mid-turn). Both statements are true.
The design reconciles them: renew the active key in place, the way spares are renewed, only when doing so
cannot pull the key out from under a running Claude Code session.

## 1. What was measured

Claude Code 2.1.302, the version installed on the Windows machine on 17 October 2026. Two methods,
neither touching a real account or the machine's real `~/.claude`.

**Read from the installed binary** (`claude.exe`, as `spare-refresh.md` did; the bundled JavaScript is
readable text). The functions are minified, so they are named here by what they do and by the strings in
them:

| Question | What the code does |
|---|---|
| When does a session renew its key? | Only on its way to an API call, when the stored access token has under 5 minutes left: `zT(expiresAt)` is `now + 300000 >= expiresAt`. An idle session renews nothing. |
| Does it take a lock on the config folder? | Yes, two, both made with mkdir (a `proper-lockfile` lock): `<config>/.oauth_refresh.lock` (`lockfilePath: cf(e, ".oauth_refresh.lock")`, `stale: 60000`, `update: 5000`) and a legacy `<realpath of config>.lock` beside the folder. The folder is `CLAUDE_SECURESTORAGE_CONFIG_DIR`, else the config folder (`CLAUDE_CONFIG_DIR`, else `~/.claude`). |
| Can our renewal take the same lock? | Yes: it is a plain folder made with mkdir, so creating the same folder is taking the same lock. A session that finds it taken retries 5 times one to two seconds apart, and takes a held lock over only when its owner record (written beside it by Claude Code) proves the holder's process gone; with no owner record the answer is `no_owner_record` and it does not take it over, so a lock we hold is never stolen. A lock untouched for 60 seconds is stale to Claude Code's lock library. |
| Does it re-read the key on disk? | Yes, at two points. After taking its lock it clears its cache and reads the stored key again; if the access token changed from the one it started with, it uses the stored one and posts nothing (`tengu_oauth_token_refresh_race_resolved`). And on a 401 it clears its cache, reads again, and uses the stored key if it differs (`tengu_oauth_401_recovered_from_disk` / `_from_keychain`). |
| How does it know other sessions? | Every running Claude Code writes `<config>/sessions/<pid>.json` (`pid`, `procStart`, `startedAt`, `sessionId` ...) at start and deletes it at exit (`[concurrentSessions]`). |

**Run with no account** (`measure_registry.py`, kept in the session's scratch folder): a real `claude -p`
with `CLAUDE_CONFIG_DIR` set to a fresh empty folder, a made-up API key and `ANTHROPIC_BASE_URL` pointing
at a local socket that accepts and never answers, so the process stays alive inside its first request and
nothing leaves the machine. Result:

```
while running, sessions folder holds: ['60412.<hash>.key', '60412.json']
  60412.json: pid=60412 kind=interactive fields=['cwd', 'entrypoint', 'kind', 'messagingSocketPath', 'name',
  'nameSince', 'nameSource', 'peerFeatures', 'peerProtocol', 'pid', 'pidDomain', 'procStart', 'sessionId',
  'startedAt', 'version']
process still running: True
after the process ended, sessions folder holds: ['60412.<hash>.key', '60412.json']
```

So a `-p` run registers like an interactive one, and a session that is killed (here a hard terminate)
LEAVES its record behind. A record therefore counts only while its process is alive.

Not measured, because it needs a real sign-in: a live session actually recovering from a key renewed under
it. The code above says it does (on its next 401, or when it next takes its lock); the rule below does not
depend on it, because it never renews a key a session may be using with time left.

## 2. The rule that was built

`license usage --refresh` (`tools/cc-devthrottle/src/license_usage.py`, `refresh_key` and
`_refresh_active_holding_the_lock`) renews the active key in place when:

- its access token has under one hour left (`REFRESH_MARGIN_SECONDS`, the spares' margin), AND
- no Claude Code session is registered in the default folder - a record in `<config>/sessions/` counts
  only while its process is running and started no later than the record says (five seconds' slack), so a
  record left by a killed session, or one whose process number was used again, does not count; a record
  that cannot be read DOES count, so not knowing never reads as "no session" -
- OR the access token has already ended, when no session can be using it: a session holding a dead token
  renews on its next call, under its lock, where it first reads the stored key and finds ours.

When a session is running and the token still works, nothing is done: the session renews it itself before
it ends (`in-use`, not a failure).

The renewal holds three locks from reading the refresh token to writing the renewed key: the key store's
own (as every store write), and Claude Code's two lock folders, taken in Claude Code's order. Either
Claude Code lock fresh means a session is renewing right now: nothing is sent, the read goes ahead with
the key as it is, and the next round tries again (`busy`). A Claude Code lock untouched for over 60
seconds was left by a killed session and is taken, exactly as Claude Code itself treats it. Under the
locks the key is read again, as Claude Code does, and if a session renewed it in the meantime that renewal
stands and nothing is posted (`renewed-by-a-session`).

**Exactly one copy.** The request is the spares' request; the answer is written back with
`LicenseStore.store_refreshed_key` into the same place (the default folder's credentials file, or its
Keychain item on macOS), atomically. Nothing is copied, cached or written anywhere else.

**A failed renewal says so.** It is the active row's `refresh-failed`, which the Director reports as "The
stored sign-in could not be renewed; the account needs a new sign-in." - the way a failed spare renewal is
reported today.

**Where it runs.** On the rounds that already exist: the Director's two-minute active read now passes
`--refresh` (`license usage --active --refresh`), and the ten-minute read of every holding already did. No
new timer.

## 3. The tests, in plain terms

`tools/cc-devthrottle/tests/test_license_usage.py`:

- `test_read_with_refresh_ActiveKey_UnderAnHourAndNoSession_IsRenewedInPlaceThenRead` - half an hour
  left and no session: the refresh token is posted, the renewed key is stored in the default folder and
  read with, and the store lock and both Claude Code locks are released afterwards.
- `test_read_with_refresh_ActiveKey_ASessionIsUsingIt_IsNotRenewed` - a session running and time left:
  nothing is posted and the key is read as it is.
- `test_read_with_refresh_ActiveKey_MoreThanAnHourLeft_IsNotRenewed`.
- `test_read_with_refresh_ActiveKey_AlreadyExpiredWhileASessionIsRegistered_IsRenewed` - sessions that sat
  idle past the token's end do not stop the renewal.
- `test_refresh_key_ActiveKey_ClaudeCodeHoldsItsRefreshLock_IsBusyAndSendsNothing` - a fresh Claude Code
  lock: nothing posted, nothing written, the session's lock left alone, the read still works.
- `test_refresh_key_ActiveKey_OnlyTheLegacyLockIsHeld_IsBusy_AndTheFirstLockIsGivenBack`.
- `test_refresh_key_ActiveKey_ALockLeftByAKilledSession_IsTakenAfterSixtySeconds`.
- `test_refresh_key_ActiveKey_ASessionRenewedItFirst_UsesThatAndSendsNothing` - the read under the lock
  sees a session's renewal; the spent refresh token is never posted.
- `test_read_with_refresh_ActiveKey_RenewalRefused_IsRefreshFailedSayingItNeedsASignIn` - the token
  endpoint refuses: `refresh-failed`, "the active account needs a new sign-in", nothing read.
- `test_refresh_key_ActiveKey_AfterRenewal_TheKeyIsInExactlyOnePlace_AndTheOldTokenIsGone` - every file
  under the test machine (default folder, key store, and a stand-in Director folder) is searched: the new
  tokens are in exactly one file, the default folder's credentials file, and the old access and refresh
  tokens are nowhere on disk.
- `test_claude_sessions_in_use_*` - no folder is no session; this test's own running process registered
  after it started is in use; a record whose process is gone is not; a record whose number another,
  later process took is not; an unreadable record counts as in use.
- `test_process_started_at_ThisProcess_IsInThePast` - the process start reader works on this platform.
- Changed: `test_read_with_refresh_ASwitchFinishedFirst_ReadsTheAccountWhereItIsNow` (the account a
  switch made active is now renewed and read in the default folder), and the old
  `test_read_with_refresh_ActiveKey_IsReadWithoutRenewal` became the session and margin tests above.

Director (`src/CcDirector.Core.UnitTests/Licenses`):

- `ReadUsageAsync_ActiveOnly_AlsoRunsTheToolWithRefresh` - the two-minute active read now asks the tool to
  renew (the tool decides if it is due and safe).
- `TickAsync_TheActiveKeysRenewalFailed_TheReportSaysTheAccountNeedsANewSignIn`.

Revert check: with the active rule replaced by "never renew", nine of the new tests fail; the three that
still pass are the ones that assert nothing is renewed.

## 4. The gate

Recorded in the pull request after the run on the committed change.

## 5. What the live proof should look for

On a machine whose Director has license switching on and no Claude Code session running: within the hour
before the active token ends, the store log (`license-store.log`) shows `key-refreshed` for the active
account, the Director log shows the active read without an error, and the Licenses page keeps showing the
machine's account as readable through the night. With a session open and working, no `key-refreshed` for
the active account appears until the token has ended.

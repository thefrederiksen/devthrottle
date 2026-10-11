# Answers to the review of pull request 3858 (usage and spare refresh)

The review is `review-3858-usage.md` beside this file, made on commit `ab6b5ee59`. Every finding is
answered below. Each fix has tests that fail on the reviewed code: with `src/license_usage.py` put back
to `ab6b5ee59` and the new tests kept, 24 of them fail; with the fix, all 82 tests in
`tests/test_license_usage.py` pass.

## 1. Provider response text can expose a sign-in token - ACCEPTED, fixed

The reviewer's two routes were real: a malformed `resets_at` reached the error detail and the log
through the exception's own text, and any 40-character word in the token endpoint's `error` field was
repeated. Error text is now built only from fixed sentences and an allow-list:

- The usage answer is parsed by functions that raise `BadAnswer` with a fixed sentence naming a field this
  code knows (`five_hour.resets_at is not a time`). The provider's value never reaches the sentence, and
  the parser's own exception text is never used: an unparsable time and a JSON error are caught and
  replaced. Only `BadAnswer` messages are put into `readErrorDetail`.
- An OAuth error code is repeated only when it is one of the six in RFC 6749 section 5.2
  (`OAUTH_ERROR_CODES`). Anything else in the `error` field is dropped.
- A network failure names its type only when the text is a plain identifier; anything else becomes "a
  connection failure". The HTTP layer raises it with the exception's class name, so this only matters
  if another HTTP layer is plugged in.

Tests: `test_read_usage_TokenEchoedInAMalformedField_NeverReachesDetailOrLog`,
`test_read_usage_TokenAsANumberFieldValue_NeverReachesDetail`,
`test_refresh_spare_TokenEchoedAsTheErrorCode_IsNotRepeated` (as a string and as `{"type": ...}`),
`test_refresh_spare_KnownOAuthErrorCode_IsNamed` (all six), and
`test_NetworkFailure_WithTextThatIsNotATypeName_IsNotRepeated`.

## 2. Failed reads erase the previous numbers and timestamp - DECLINED, with the reason

The command line keeps no state between runs. Each `cc-devthrottle license usage` is one process that
reads and exits. It could only "keep the previous numbers" by writing every reading into the key
store's folder and reading it back next time, which would add a second record of each account's state
beside the one the Director already holds.

The Director is the long-lived side. It already holds the last report it sent, so it is the right place
to keep the last good numbers and their `ReadAtUtc` when a new read fails, as contract section 2.2 asks.
The Tech Lead has taken that onto the Director's task.

So the command reports a failure honestly: `readError` and `readErrorDetail` set, every number null, and
`readAtUtc` null. An old number never appears as if it had just been read. This is now written in the
`--json` help ("A failed read has readError set and null numbers; the Director keeps the last good
ones.") and in the module's description. The existing tests that assert empty numbers on failure stay:
they test this behaviour.

## 3. Numeric values outside the contract range are accepted - ACCEPTED, fixed

Every percentage - `five_hour.utilization`, `seven_day.utilization` and `extra_usage.utilization` - must
be a finite number from 0 to 100 inclusive. A negative number, a number over 100, NaN or an infinity
makes the whole reading `unexpected-answer` with no numbers. As the reviewer said, a wrong low number
would make a full account look like the best switch target. A non-boolean `extra_usage.is_enabled` is now
also `unexpected-answer`, where before it quietly became "not known".

Tests: `test_read_usage_PercentageOutside0To100OrNotFinite_IsUnexpectedAnswer` (-4, -0.01, 100.5, 250,
NaN, Infinity, -Infinity, for each of the three fields) and
`test_read_usage_PercentageAtTheBounds_IsAReading` (0 and 100 still read).

## The refresh loss window - ACCEPTED as a known risk

If the process dies after the token endpoint answers and before the new key is written, the stored key
is the old, spent one, and the spare needs a new sign-in. The write stays immediately after the answer,
and it is a same-folder atomic replace, so the window is the time of one small file write. No recovery
can be built: the new key existed only in the dead process's memory, and the old refresh token is spent
(measured, `spare-refresh.md`). The cure is a new sign-in. A failed write already says so plainly, and
the Director's next read reports the spare as `refresh-failed` or `unauthorized`, which makes it
unusable for a switch (contract section 3.3 step 9).

## The interim file reader - DELETED

The key store (#3857) has merged, and the interim reader is gone. `license usage` and `license refresh`
now open the store exactly as `license list` does, list accounts with `held_accounts()`, read a key with
`HeldAccount.read_key()`, and write a renewed key back with `LicenseStore.store_refreshed_key()`, which
replaces it in the same place and logs `key-refreshed` in the store's log. The macOS Keychain is
therefore covered by the store's own backend.

# Round 2 (`review-3858-round-2.md`, made on commit `2c882ffb6`)

## Refresh can lose a rotated key during an exchange - ACCEPTED, fixed

Both orders the reviewer reproduced were real. A refresh took only its own per-account lock, and a switch
took only the store lock, so neither saw the other: a switch holding the store lock made the renewed key's
write fail after the provider had already rotated it, and a switch that finished first left the refresh
pointing at the emptied spare folder.

The fix is the Tech Lead's recommendation: one lock. A refresh now takes the key store's own lock (the
one every switch, add and remove takes) before it reads the refresh token, and keeps it until the
renewed key is written. Under the lock it reads again where the account is now. The separate per-account
refresh lock is deleted.

- **A switch already running:** the refresh is `busy` and sends nothing, so no refresh token is spent.
  `license usage --refresh` then reads the key as it is and leaves the renewal to the next round: a busy
  store is not `refresh-failed`, which would bar a usable spare from being a switch target.
- **A switch tried while the provider answers:** it is refused as `switch-in-progress`. The Director
  retries it, and the retry moves the RENEWED key.
- **A switch that finished before the refresh took the lock:** the account is now active, so the refresh
  is refused before anything is sent. `license usage --refresh` reads it where it is now, in the default
  folder.

`LicenseStore` gains one public method, `exclusive()`, which returns the store's reentrant lock, so the
refresh holds it across its reads and the store's own write without reaching into a private field.

Tests (all five fail on `2c882ffb6`, and all 86 in `tests/test_license_usage.py` pass with the fix). The
other process is the same store opened a second time, which takes its own handle on the lock file
exactly as a separate Director process does:
`test_refresh_spare_ASwitchHoldsTheStore_IsBusyAndSendsNothing`,
`test_refresh_spare_ASwitchTriedWhileTheProviderAnswers_IsRefusedAndTheRotatedKeyIsKept`,
`test_refresh_spare_ASwitchFinishedFirst_RefusesTheNowActiveAccountAndSendsNothing`,
`test_read_with_refresh_ASwitchFinishedFirst_ReadsTheAccountWhereItIsNow`, and
`test_read_with_refresh_StoreBusy_ReadsTheKeyAsItIsInsteadOfBarringIt`.

What remains of the accepted loss window is only a process dying between the provider's answer and the
write, as recorded in round 1. Ordinary lock contention can no longer cause it.

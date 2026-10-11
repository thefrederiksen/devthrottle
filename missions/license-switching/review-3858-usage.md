# Review: pull request 3858 usage and spare refresh

Reviewed commit: ab6b5ee59f12b7be3036a41663d04f7230819902

## Scope and proof

Read MISSION.md sections 5 and 7, contract.md section 2, spare-refresh.md, and the pull request diff for the usage and refresh changes. The prompt's path tools/cc-dev-throttle does not exist in this checkout; the source directory is tools/cc-devthrottle. I reviewed that directory's diff and the mission-record diff.

The full Python suite ran from tools/cc-devthrottle with the supplied Python 3.11.6 environment:

    python -m pytest
    3948 passed, 3 skipped in 469.84s (0:07:49)
    exit code 0

The changed tests create temporary credential JSON and use FakeGet/FakePost; no test called Anthropic or the token endpoint with a real sign-in. I also ran synthetic probes only. A malformed usage answer put a synthetic access token into readErrorDetail and a warning log; a refresh error put a synthetic refresh token into the raised exception. A usage answer with utilization -4 returned with no read error. No real login was read or written, and no provider request was made.

The interim Windows/Linux file reader is explicitly out of scope. Its file-backend behavior, including protection for noncanonical config paths, was not reviewed here. The suite's exact canonical ~/.claude refusal test is not evidence about that excluded reader's other path cases.

## Findings

### 1. Provider response text can expose a sign-in token

Location: tools/cc-devthrottle/src/license_usage.py, _utc_text and read_usage error handling (lines 179-184, 333-337); _oauth_error_code (lines 532-542).

A 200 usage answer with a malformed five_hour.resets_at value reaches _utc_text. For a value without an offset, the raised ValueError includes the original value. read_usage inserts that exception into read_error_detail, _failed logs the detail, and license usage --json serializes it. I reproduced this with a synthetic access token as resets_at: the complete synthetic value appeared in both read_error_detail and the log.

The refresh path has a second route: _oauth_error_code copies any provider error string of up to 40 alphanumeric/underscore characters into RefreshError. The CLI then prints that exception as detail. A synthetic echoed refresh token in the error field appeared in the exception. This conflicts with contract section 2's rule that ReadErrorDetail contains no sign-in material and the implementation's claim that a token is never shown. Keep provider-controlled values out of error text, or use a strict allowlist of known error codes. The existing no-token test does not cover malformed 200 usage fields or a token echoed in the error field.

### 2. Failed reads erase the previous numbers and timestamp

Location: tools/cc-devthrottle/src/license_usage.py, lines 283-297.

read_usage always creates a blank UsageReading. On any failure, _failed clears both windows, extra usage, and read_at_utc. There is no prior reading supplied or stored to restore. After a successful reading followed by a transient 401, network error, or malformed answer, the next JSON row therefore has no previous numbers and no old timestamp. Contract section 2.2 requires the holding to retain its previous numbers and ReadAtUtc when a read fails. The tests currently assert the blank result, for example test_read_usage_401WithLiveKey and test_read_usage_UnreadableBody, so they reinforce the contract mismatch.

### 3. Numeric values outside the contract range are accepted as valid readings

Location: tools/cc-devthrottle/src/license_usage.py, _window (lines 252-267).

_window checks that utilization is numeric but does not enforce the contract's 0..100 range or reject non-finite numbers. I supplied a synthetic answer with utilization -4; read_usage returned it with read_error unset. A negative value can be treated as a fresh, low-use account and selected as a switch target even though the provider answer is invalid. Reject out-of-range values as unexpected-answer and cover the bounds with tests.

## Confirmed refresh loss window

A successful refresh rotates the provider token before this process writes the new credentials. If the process dies after the response and before key.write_oauth, the file still contains the old, now-spent tokens; the only new key was in memory. The next refresh cannot recover it, so the spare needs a new sign-in. The same-folder replace prevents a partial credential file, but it does not close this response-to-write window. spare-refresh.md already records this risk. The current write-failure test replaces write_oauth with a function that raises; it does not exercise a process crash or prove recovery. The successful path does update the same credential file and does not create a second durable copy.

## Other requested checks

- Usage endpoint errors stay distinct in the direct read path: 401/403 becomes unauthorized, network exceptions become network, and non-200 or unreadable answers become unexpected-answer. A failed spare renewal becomes refresh-failed, as the contract specifies.
- The tests prove the implemented branches against hand-built credential files and fake HTTP answers. They do not prove the provider's rotation behavior; that fact is recorded in spare-refresh.md from an isolated manual measurement, and I did not repeat it because this review must not call the provider with a real sign-in.
- I did not review the Gateway, macOS Keychain, the excluded interim file reader, or any live-provider behavior.

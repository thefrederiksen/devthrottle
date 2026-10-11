# Review: pull request 3858 rebase round 2

Reviewed commit: `2c882ffb6a6e70be8dc7bc49dcbceb47152f9795`

## Scope and proof

Read `missions/license-switching/MISSION.md` section 5, `contract.md` section 2, the first review, and its answers. Reviewed `git diff origin/main...HEAD -- tools/cc-devthrottle` (the prompt's `tools/cc-dev-throttle` path is absent; the source directory is `tools/cc-devthrottle`). The diff contains `cli.py`, `license_usage.py`, `license_usage_ops.py`, and `tests/test_license_usage.py`.

Ran the requested Python 3.11.6 command from `tools/cc-devthrottle`:

```text
python -m pytest tests/
4144 passed, 3 skipped in 461.86s
exit code 0
```

I also ran synthetic probes using temporary config/store folders and fake token and usage endpoints. One paused a switch while it held the store lock; another completed a switch during the fake refresh request. No live provider request, real default login, or macOS Keychain item was used.

## Finding

### Refresh can lose a rotated key during an exchange

Locations: `tools/cc-devthrottle/src/license_usage.py` (`StoreKey.write_oauth`, `refresh_spare`); `tools/cc-devthrottle/src/license_store.py` (`switch`, `store_refreshed_key`); `tools/cc-devthrottle/src/license_usage_ops.py` (`usage`).

`refresh_spare` checks that its snapshot says “spare,” then holds a per-account refresh lock while it reads the refresh token and calls the token endpoint. An exchange does not use that lock: `switch` uses the separate store lock. The store lock is acquired again only when `StoreKey.write_oauth` calls `store_refreshed_key` after the provider has returned and rotated the token.

I reproduced the harmful overlap with synthetic credentials: a switch held the store lock while the fake provider returned a new access and refresh token. The write was refused as “another license operation,” `refresh_spare` returned `unexpected-answer` and said a new sign-in was needed, and the switch then completed with the old, spent refresh token in the active folder. This is the accepted response-to-write loss window occurring during ordinary exchange lock contention, without a process crash. The exchange and refresh need coordination so a completed provider rotation cannot be discarded this way.

If the exchange finishes before the refreshed key is written, the write instead finds that email in its new active folder and updates it there. `StoreKey` still holds the original `HeldAccount` path, which is now the emptied spare folder; `read_with_refresh` then reports `store-unreadable` and makes no usage request. I reproduced that order too. The completed writes replaced one key in one location; I did not observe a duplicate sign-in. The failure is the uncoordinated exchange and stale holding snapshot.

## Other requested checks

- The provider-text leak fixes are present: malformed usage values produce fixed `BadAnswer` messages, and refresh errors repeat only allow-listed OAuth codes. The added echo tests and the full suite passed. I found no route in the reviewed usage/refresh code that put a synthetic access or refresh token into output, logs, or raised error text.
- Percentage validation rejects non-numeric, non-finite, and out-of-range values and accepts 0 and 100. The added boundary tests passed.
- The accepted refresh-loss risk remains: if rotation succeeds and the process cannot persist the response, the new key is lost. The exchange-lock case above makes that risk reachable through normal concurrent store use.
- Both commands resolve the store through `license_ops._open`, which constructs `LicenseStore` without `allow_real_default`. Its constructor refuses the real default Claude folder before reading it. The tests cover explicit and implicit real-default paths. No real login was touched.
- The prior review's declined “retain last usage numbers on read failure” item is delegated to the Director in the answer. This Python-only diff does not let me verify the Director's retention behavior.

## Scope limits

I did not call Anthropic's endpoints, exercise a live macOS Keychain, or inspect/run the Director and Gateway implementation. The refresh/exchange probes used the file backend and synthetic tokens; they prove the store-lock and stale-path behavior, not provider rotation behavior.

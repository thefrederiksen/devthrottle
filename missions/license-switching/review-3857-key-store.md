# Review: pull request 3857 key store

Reviewed head `ef8771907` against `origin/main`.

## Findings

### P1 - Rollback can overwrite and lose a sign-in written after the crash

`LicenseStore._step_copy` writes the target key and account block unconditionally
(`tools/cc-devthrottle/src/license_store.py:644-656`). The switch journal uses that same operation for
the rollback which restores the old active sign-in (`license_store.py:704-731`). It never proves that
the default folder still holds the value the interrupted switch put there before overwriting it.

I reproduced this with the production `LicenseStore` and `FileKeyBackend`:

1. Start with A active and B spare.
2. Crash at the `drop-key` checkpoint, after A has been put away and B copied into the default folder.
3. Put a valid C sign-in in the default folder, representing a login or credential rewrite while the
   crashed process is absent.
4. Recover.

The pending drop correctly notices that B's spare no longer matches the default folder and starts a
rollback. The rollback then copies A over C without checking C, deletes A's spare copy, and reports
`exchange-failed-restored`. The keys afterward were only `A@default` and `B@spare`; `C` survived in
zero places. A running Claude process is outside the store lock, so an intervening rewrite of the
active credential is not excluded by the lock.

This breaks the central guarantee that recovery never loses a sign-in. A rollback copy needs a proven
target state, just as `_step_drop` already requires a proven matching destination before deletion.

### P1 - The macOS service hash is not calculated from the exact config-folder string

The required non-default service is `Claude Code-credentials-` plus the first eight hex characters of
SHA-256 of the config-folder path. The implementation changes that input in two ways: it normalizes it
to NFC in `keychain_service_for` (`tools/cc-devthrottle/src/license_store.py:179-185`), and the store
converts the default and add-source strings to absolute paths before asking the backend for their
service (`license_store.py:544-545,803`).

Both changes produce a different Keychain service from the required hash:

- For the relative config string `isolated-claude`, the direct hash gives
  `Claude Code-credentials-6e480956`; the store looks up
  `Claude Code-credentials-a3c4586b` after making it absolute.
- For `/Users/example/cafe\u0301` (a decomposed Unicode path), the direct hash gives
  `Claude Code-credentials-c317c9c9`; the helper returns
  `Claude Code-credentials-aa057155` after NFC normalization.

On macOS, `license list`/`add` therefore cannot find a sign-in created with either supported path
shape, and a write uses a service Claude Code does not use. The naming tests cover only absolute ASCII
paths, and the fake Keychain accepts whichever service the implementation asks for, so all 101 tests
pass while these cases remain uncovered.

### P2 - A crash during remove leaves `oauthAccount` behind permanently

`LicenseStore.remove` deletes the key, then clears the account block, without a journal
(`tools/cc-devthrottle/src/license_store.py:851-869`). I raised at its `remove-account` checkpoint,
after the key deletion. The spare then had no `.credentials.json` but still had B's `oauthAccount`.
The next `held_accounts()` run found no journal and left that block in place.

That leaves the account metadata separated from its key indefinitely; anything inspecting that config
folder can report B even though B was removed. It also contradicts the module's claim that every public
operation recovers an interrupted move of the key and account block.

The crash test misses this because `World.snapshot()` includes only folders that still have a key
(`tools/cc-devthrottle/tests/test_license_store.py:159-173`), and the remove branch treats absence from
`held_accounts()` as a completed removal (`test_license_store.py:549-561`). The oracle never examines
an account file after its key disappears.

### P2 - The command-line secret test cannot detect the representation the Keychain backend uses

The Keychain writer hex-encodes the complete credential before passing it to `security -i`
(`tools/cc-devthrottle/src/license_store.py:386-388`). The test named
`test_keychain_NoKeyIsEverOnACommandLine` checks the recorded argument list only for the plaintext word
`SECRET` and for the raw JSON credential (`tools/cc-devthrottle/tests/test_license_store.py:702-712`).

I put the same hex-encoded credential in a simulated argument list: the current assertion returned
true while the encoded credential was present. A regression from stdin to `security ... -X <hex key>`
would therefore expose the full key in the process list and this test would stay green. The test needs
to assert the exact safe argument shape, or at minimum check the derived hex representation as well as
the plaintext value.

## Scope and evidence

Read:

- `missions/license-switching/MISSION.md`, sections 5 and 7.
- `missions/license-switching/contract.md`, sections 3.3 and 6.
- The complete `origin/main...HEAD` change under `tools/cc-devthrottle`: `src/cli.py`,
  `src/license_ops.py`, `src/license_store.py`, `tests/test_help_and_errors_axi.py`,
  `tests/test_license_cli.py`, and `tests/test_license_store.py`.

Ran:

- The requested command with the machine's system Python. It collected 101 tests, but produced
  80 passes and 21 CLI-test failures because the installed Click is 8.1.8 and does not capture stderr
  separately. This repository declares Click 8.2.1 as its minimum and already records the stale local
  installation as an environment problem.
- The same two test files in an isolated environment at the declared floors (Python 3.11.6,
  Typer 0.16.1, Click 8.2.1, Rich 13.0.0, Requests 2.31.0, pytest 7.4.3): **101 passed in 37.96s**.
- Direct temporary-directory reproductions for the rollback overwrite, interrupted remove, relative
  and decomposed-Unicode Keychain hashes, and the hex command-line assertion blind spot.
- `git diff --check origin/main...HEAD -- tools/cc-devthrottle`: passed with no output.

No test or reproduction touched the real `~/.claude` or a real Keychain item. File-backend runs used
temporary directories; Keychain tests use the injected fake runner; the real-default guards were read
and exercised without backend access.

I could not reach a macOS host or observe Claude Code and the real Keychain together. The Keychain
naming finding is measured against the mission's exact hash formula and the strings the production
code actually hashes; the subprocess behavior is covered only by the fake `security` runner. I did
not run the repository's wider suites because the requested check named only these two files.

Within that scope, I found no current path that prints a stored key in CLI output, the operations log,
the journal, or a handled `LicenseStoreError`; the fourth finding is that the committed test would not
catch the most direct regression of the current hex payload onto the command line.

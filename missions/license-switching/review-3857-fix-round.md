# Review: pull request 3857 fix round

Reviewed the changes from `ef8771907` to `bfb8c94b2` under `tools/cc-devthrottle`, alongside `MISSION.md`, `contract.md`, the first review (`review-3857-key-store.md`), and the developer answers (`review-3857-answers.md`).

## Findings

### P1 - Recovery of an interrupted remove can delete a later sign-in

`LicenseStore._step_erase` (`src/license_store.py:696-702`) unconditionally deletes the key currently in the journaled folder, then clears its account block. If removal crashes at `erase-account`, a later `claude auth login` can write a new sign-in into that folder before the next store operation recovers the journal. Recovery then deletes the new key and its account block, although it is not the sign-in the user removed.

I reproduced this with the production `LicenseStore`: start with A active and B spare; crash removal after B's key is deleted; sign in C to B's now-empty spare folder; call `held_accounts()` to trigger recovery. C's key and `oauthAccount` block were deleted. The reproduction produced the same result with `FileKeyBackend` and the test `FakeSecurity` Keychain backend. The new remove test covers recovery when the folder stays empty, but not a sign-in arriving between the crash and recovery.

### P2 - The canonical-folder rule refuses relative config folders Claude Code can use

`license_ops.resolve_default_config_dir` (`src/license_ops.py:40-41`) passes `CLAUDE_CONFIG_DIR` through unchanged, but `canonical_folder` (`src/license_store.py:179-200`) rejects every relative value. I reproduced the refusal with `isolated-claude`. The preceding review's Mac measurement recorded relative config-folder strings as distinct Claude Code Keychain service inputs; the answers also describe Claude Code hashing the raw value without resolving it. For such a Claude Code setup, every license operation fails at construction. Following the error's advice to use the absolute spelling would select a different Keychain service, so it cannot manage the existing sign-in.

## Findings from the first review

- **Rollback overwrite:** fixed for the reported scenario. `test_review_RollBackNeverOverwritesASigninWrittenAfterTheCrash` reproduces the crash, writes C into the default folder, and verifies recovery preserves A, B, and C. It passed for both backends in the requested test run.
- **Interrupted remove leaving metadata:** the original scenario is fixed. `test_review_RemoveCrashBetweenKeyAndAccountBlock_TheNextRunClearsTheBlock` passes on both backends. The later-sign-in case above is a separate gap in the new erase recovery.
- **Hex credential on a command line:** fixed in the regression check. `_assert_no_key_on_a_command_line` checks safe argument shapes and raw, lowercase-hex, and uppercase-hex key forms. `test_keychain_TheCommandLineCheckCanFail` rejects the `-X <hex>` argument case and passed.
- **Unicode normalization:** I found the declined point sound on the evidence available here. The implementation hashes NFC, and the decomposed/composed tests pass. The answers record a Claude Code 2.1.296 binary inspection and a matching Mac measurement. I could not independently rerun Claude Code with a real Keychain on macOS from this Windows checkout.

## Scope and evidence

Read the two mission documents, both prior review records, and the complete diff from `ef8771907` to `HEAD` under `tools/cc-devthrottle`.

Ran from `tools/cc-devthrottle` using the supplied virtual-environment Python:

```text
python -m pytest tests/test_license_store.py tests/test_license_cli.py
129 passed in 57.19s
```

Also ran `git diff --check ef8771907 HEAD -- tools/cc-devthrottle`; it passed. The remove reproduction used temporary directories and the production store with the file backend and a fake Keychain runner. The canonical-folder check was reproduced against the production constructor. No real `~/.claude` folder or real Keychain item was accessed. I did not independently verify Claude Code's config-folder behavior on macOS; the relative-path compatibility finding relies on the prior review's recorded Mac measurement and the developer's cited Claude Code source inspection.

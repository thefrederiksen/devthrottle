# Answers: review of pull request 3857, key store

Answers to `review-3857-key-store.md`, which reviewed head `ef8771907`. Every finding is answered as
accepted or declined. For each accepted finding, a test was shown to fail with the fix removed and to
pass with it restored.

## P1 - Rollback can overwrite and lose a sign-in written after the crash

**Accepted.** A copy step now refuses to overwrite a key it cannot prove is held somewhere else. Before
writing, `_step_copy` checks that the target is in one of three states:

- it is empty;
- it already holds the source's key (the step being run again after a crash);
- it holds the same key as the folder the journal names in the step's `replaces` field.

Anything else stops the step and changes nothing.

The switch journal now names `replaces` in two places:

- **Bringing the spare in** replaces the old active sign-in, which the first step put away. This
  forward step had the same defect as the rollback: re-running it after a crash would have overwritten
  a sign-in written in between.
- **Undoing that step** replaces the spare's copy.

In the review's scenario, a sign-in C written into the default folder after the crash survives. A, B
and C each stay in exactly one place, recovery reports `exchange-failed-unknown`, and the journal is
kept for a person to read.

Tests:

- `test_review_RollBackNeverOverwritesASigninWrittenAfterTheCrash` is the review's reproduction (crash
  at `drop-key`, write C, recover).
- `test_review_RollForwardNeverOverwritesASigninWrittenAfterTheCrash` covers the forward direction
  (crash while bringing B in).
- `test_contract_SpareRefreshedMidSwitch_StopsWithNothingLost`: a spare that was refreshed mid-switch
  now stops with nothing changed, instead of being rolled back over a key held nowhere else.

All run on both backends. With the check removed, four of the review tests fail.

## P1 - The macOS service hash is not calculated from the exact config-folder string

**Accepted in part.**

**The evidence.** Two sources agree.

- **Measured on the Mac** with Claude Code 2.1.296, by the live-proof seat: the Keychain service is
  `Claude Code-credentials-` + the first 8 hex of the SHA-256 of the RAW `CLAUDE_CONFIG_DIR` string,
  after Unicode normal form C. A relative path, a trailing slash, `./` or `..` each give a different
  service name.
- **Read from the same version's binary**, its config folder is
  `ve = memo(() => (process.env.CLAUDE_CONFIG_DIR ?? join(homedir(), ".claude")).normalize("NFC"))`,
  hashed when `CLAUDE_CONFIG_DIR` is set, with no resolving.

**Making a folder absolute before hashing: accepted.** That was wrong. Fixed by `canonical_folder`:
every folder the store manages (the default config folder, the store folder, and a folder being added)
is accepted in exactly one form:

- absolute;
- no trailing separator;
- no `.` or `..` segment;
- no doubled separator;
- not the root.

Anything else is refused with a clear error naming the canonical spelling to use. The accepted string is
hashed exactly as given, never rewritten. One spelling per folder also means one Keychain name per folder.

Tests:

- `test_store_NonCanonicalFolder_IsRefusedNamingTheCanonicalForm` covers a trailing separator, a `.`
  segment, a `..` segment and a doubled separator, each for the default, store and added folder.
- `test_store_RelativeFolder_IsRefused` and `test_store_RootFolder_IsRefused`.
- `test_store_CanonicalFolderIsHashedExactlyAsGiven`.
- `test_realDefault_IsRefused_InEverySpelling`.
- With the canonical check removed, all 13 refusal tests fail.

**Unicode normal form C: declined,** per the measurement above. Claude Code normalizes before hashing,
so the store does the same:

- `test_keychain_ServiceName_NormalFormCLikeClaudeCode`: a decomposed and a composed path name the same
  item.
- `test_store_DecomposedUnicodeFolder_IsAcceptedAndHashedInNormalFormC`: a path written in decomposed
  form (NFD) is accepted and hashed in normal form C.

## P2 - A crash during remove leaves `oauthAccount` behind permanently

**Accepted.** Remove is now journaled like add and switch. It is one `erase` step that deletes the key,
then clears the account block, and finishing an already-finished erase changes nothing. A crash
between the two halves is finished by the next call.

The test oracle had the gap the review describes: it never looked at a folder after its key was gone.
`assert_each_signin_in_exactly_one_place` now also fails when any folder keeps an account block without
its key. Every crash sweep uses that check, the remove sweep included.

`test_review_RemoveCrashBetweenKeyAndAccountBlock_TheNextRunClearsTheBlock` is the review's
reproduction. With remove put back to unjournaled, four tests fail.

The stricter check also exposed two test fixtures that deleted a spare's key but left its account
block. They now remove both (`_vanish`), which is what a spare disappearing means.

## P2 - The command-line secret test cannot detect the representation the Keychain backend uses

**Accepted.** The check is now one helper, `_assert_no_key_on_a_command_line`. It fails when:

- a `security` call has any argument shape other than the four the backend may use (service lookups,
  delete by service, and `-i` with the payload on standard input);
- a key appears in the arguments raw, or as hex in either case.

`test_keychain_TheCommandLineCheckCanFail` feeds the helper the argument list a regression to
`add-generic-password ... -X <hex>` would produce, and a safe shape carrying the hex, and requires both
to fail.

## Fix round (`review-3857-fix-round.md`)

### P1 - Recovery of an interrupted remove can delete a later sign-in

**Accepted.** Remove's journal step now records a fingerprint of the key it sets out to delete: a
SHA-256 of the key, never the key itself (`HeldKey.fingerprint()`). The erase deletes only a key with
that fingerprint. A different key in the folder means the removal already happened and a new sign-in
arrived; it is left alone and the log says so (`erase-left-alone`). The account block is cleared only
when it names the removed account.

The same rule now holds for the other step that deletes. Add's drop already deleted a key only when the
same key was proven in its new place. When the key is already gone, it now clears the account block
only when that block names the account that moved.

Tests, on both backends:

- `test_fixRound_RemoveRecoveryNeverDeletesASigninMadeAfterTheCrash` is the reviewer's scenario: crash
  after B's key is deleted, C signs in to that folder, recovery. C's key and block survive.
- `test_fixRound_RemoveJournalHoldsAFingerprintNotTheKey`.
- `test_fixRound_AddRecoveryNeverDeletesASigninMadeAfterTheCrash`.
- `test_fixRound_RecoveryLeavesAnotherAccountsBlockAlone`: for add and for remove, another account's
  block written before recovery is kept.

With the fingerprint check removed, the reviewer's scenario fails. With the block checks removed, all
four block tests fail.

### P2 - The canonical-folder rule refuses relative config folders Claude Code can use

**Declined as a change of behaviour; the error message is fixed.** A relative folder, or any spelling
other than the canonical one, stays refused, on the Tech Lead's recommendation:

- a person who sets a relative `CLAUDE_CONFIG_DIR` is rare;
- a relative folder's meaning depends on where each process starts, so the Director and Claude Code
  could resolve it differently;
- failing loudly beats guessing.

The reviewer is right that the old advice ("pass the full path", "pass it as <canonical>") pointed at a
different Keychain item from the one the existing sign-in lives in. The errors now say a relative or
non-canonical `CLAUDE_CONFIG_DIR` is not supported for license switching, and that the way out is to
sign the account in again with `CLAUDE_CONFIG_DIR` set to the absolute, canonical folder, which is
named.

`test_store_RelativeFolder_IsRefused` and `test_store_NonCanonicalFolder_IsRefusedNamingTheCanonicalForm`
assert the new wording and that the next step is a new sign-in, not a retyped path.

## Tests run

cc-devthrottle only, as the Tech Lead asked: `tests/test_license_store.py`, `tests/test_license_cli.py`,
`tests/test_help_and_errors_axi.py` and `tests/test_actions_cli.py`, at the declared dependency floors,
after each round.

# Step 5 proof: a sign-in by hand never deletes a login

## What was wrong

On 19 October a machine was signed in by hand with `claude auth login` straight into its default Claude folder.
Claude Code's own write replaced the sign-in that was there, so the account the machine had been on was gone from
it - the Licenses log said "the sign-in by hand overwrote it". A week later that machine had no sign-in for the one
account with room left, and could not switch. That is root cause 1 of the 26 October stranding. On 26 October itself
the machine was moved off the full account by `/login` typed inside a session, which overwrote the login the same way.

The owner's decision (D3): keep the replaced login as a spare automatically.

## What changed

`cc-devthrottle license sign-in <email> --make-active` is the sign-in by hand that replaces the active account.

- The new account is signed in exactly as every other sign-in is: into its own folder, by Claude Code's own write.
  It never goes straight over the active sign-in.
- Then the key store's existing switch makes it the active account. The switch is the exchange the store already
  uses: the active sign-in goes into its own spare folder, `<store>/spares/<account>`, in the same format, the same
  journal and the same crash recovery as every switch - on the credentials file (Windows, Linux) and on the macOS
  Keychain. No second spare store was added.
- Before any login starts, the store is asked whether the active account could be kept
  (`LicenseStore.active_to_keep`). When it could not, the sign-in is refused with the reason and the fix, and nothing
  is signed in or written. The switch checks again under the store lock, against the account seen at the start; if a
  switch ran in between, the new account stays a spare and the command fails loudly saying so.
- Signing in the account that is already active renews it in place; it is not put into the spares a second time.
- A stale spare of the active account (what a raw `claude auth login` over a spare leaves) is moved to the store's
  `hand-sign-in-backups` by the existing heal, so the spare ends up holding the current sign-in, once.
- The answer says what was kept: `previousKeptAsSpare` and `previousKeptIn` under `--json`, and a sentence in text.
- The refusal for a spare folder that already holds another sign-in now names that account and a fix that works.
  It used to say "a spare for it is held too" and suggest `license remove <active account>`, which is refused.

The Gateway learns of the new spare with no change: every usage report the Director sends carries the whole
`license list`, active and spares, and the test below checks that list shows the kept account as a spare.

### A raw `claude auth login` or `/login`: the kept copy

The 26 October overwrite was neither of the paths above: the owner typed `/login` inside a session, and Claude Code
wrote the new sign-in over the active one. Nothing of ours runs before that write. But the key store is opened every
two minutes by the Director (its usage read), so it now keeps a COPY of the active sign-in, in
`<store>/active-copy/`, brought up to date every time it is opened and every time the store itself writes the active
key (a renewal, a sign-in). When the store finds the default folder holding another account, and the copy's account
held nowhere, the copy is moved - journaled, like the heal - into that account's spare folder. The replaced account
is a spare again, with its own sign-in, on the next open; the Director's next report carries it to the Gateway.

Why a copy is safe here, when the store's first rule is that a key is never copied: that rule exists because two
copies that are both USED share one refresh token, and a refresh in one kills the other (measured,
`missions/license-switching/spare-refresh.md`). This copy is never used - never listed, renewed, refreshed or
switched to - so it never rotates the token. It is dropped, not kept, when its account is held elsewhere (a switch
put it away) or when the default folder holds no sign-in (a `/logout`, which revokes the sign-in on the server).

Why the copy still works after the login, read from the installed Claude Code 2.1.311 (the program only; no sign-in
was touched): `/logout` revokes - its logout routine posts the refresh token to the token endpoint's `/revoke` -
but installing a new login calls that routine with `preserveInProcessTokens: true`, which skips the revoke. So the
replaced sign-in is still alive on the server.

The one thing it cannot do: when Claude Code refreshes the active sign-in (about every eight hours) and a hand sign-in
replaces it before the store is opened again (within two minutes on a Director), the copy is one refresh behind, and
the refresh spent its refresh token (`invalid_grant`, measured). The account is still kept as a spare, and the next
usage read finds its sign-in refused and reports it. About two minutes in eight hours, so roughly 1 hand sign-in in
240. A test pins this so it is never mistaken for more.

The way to sign in by hand with nothing at risk is `license sign-in --make-active`. No Director (C#) path replaces
the active login: the Director only adds spares and renews in place through this same tool, so no C# was changed.

## The proof run

`run-proof.sh` runs the real `cc-devthrottle license sign-in` from this checkout against a new temporary default
folder and store. `fake_claude.py` stands in for `claude auth login` on PATH: it prints an authorize URL, reads the
code and writes a made-up sign-in into the folder it was pointed at, which is all a real login does to a folder. No
browser, no claude.ai, no real sign-in or Keychain is touched. Output: `proof-output.txt` (the temporary folder is
shown as `<temp>`).

1. The isolated machine holds account A, active.
2. `license sign-in account-b@example.com --make-active` signs B in by hand.
3. B is active and A is a spare - and A's spare holds the very sign-in A had before (same fingerprint).
4. Signing B in again renews it in place; the list is still B active, A spare.
5. Failure case: B's spare folder already holds another account's sign-in, so B could not be kept. The sign-in of
   C is refused with that reason and the fix, exit 1, and no file in the default folder or the spares changed.
6. The 26 October path, on a second isolated machine: A active, the store opened once, then C signed in BY HAND
   straight into the default folder by the stand-in login. The next `license list` warns that A was replaced and is
   kept, lists C active and A spare, and A's spare holds the very sign-in A had.

The run ended `PROOF PASSED`.

## The tests

In `tools/cc-devthrottle/tests/test_license_signin.py`. The store tests run twice, on the credentials file and on
the macOS Keychain through the key store's own fake `security`, so the Mac path is proven on every platform. Each
checks afterwards that every sign-in is held in exactly one place and none was lost.

- `test_signInMakeActive_ActiveAHandSignInToB_BIsActiveAndAIsKeptAsASpare` - A active, sign in B by hand: B is active,
  A is a spare holding its own unchanged sign-in, and B's login ran into B's spare folder, never over A.
- `test_signInMakeActive_BHeldAsASpare_IsRenewedThenMadeActive_AndAIsKept` - B already a spare: renewed, made
  active, A kept.
- `test_signInMakeActive_TheActiveAccountAgain_IsRenewedInPlace_NotDuplicatedInTheSpares` - same account again: no
  duplicate.
- `test_signInMakeActive_AAlreadyHeldAsASpare_TheSpareIsRefreshedNotDoubled` - A active and a stale spare of A: A is
  held once, the spare holds A's current sign-in, the stale one is in the backups, not deleted.
- `test_signInMakeActive_NothingActive_BBecomesActive_AndNothingIsKept`.
- `test_signInMakeActive_ActiveCannotBeKept_NothingIsSignedInOrOverwritten_AndItSaysWhy` - the active account's
  account is unknown, or its spare folder holds another sign-in: refused with the reason and a fix, no login
  started, no key changed anywhere.
- `test_signInMakeActive_ActiveChangesDuringTheApproval_SwitchIsRefused_NothingIsOverwritten` - another switch runs
  while the approval waits: the switch to B is refused, B stays a spare, every sign-in still held.
- `test_signIn_WithoutMakeActive_StillOnlyAddsASpare` - the old behaviour is unchanged.
- `test_cli_signInMakeActive_AnswersWhatWasKept_AndTheListTheDirectorReportsHoldsAAsASpare` and
  `test_cli_signInMakeActive_Text_SaysWhichAccountWasKept` - the command line, and the `license list --json` the
  Director reports to the Gateway.

Revert check: with the keep-as-spare logic switched off (`make_active` ignored), 16 of these 20 test runs failed;
the 4 that passed are the same-account renewal and the plain sign-in, which never reach it. Restored, all pass.

The kept copy, in `tests/test_license_store.py` (each on the credentials file and the Keychain):

- `test_keptCopy_StoreOpenedWhileAIsActive_HoldsACopyOfTheActiveSignIn_ThatIsNeverListed`.
- `test_keptCopy_HandSignInToAnAccountNotHeld_TheReplacedAccountIsKeptAsASpare` - the 26 October picture: A is a
  spare with its own key, the copy follows the new account, the log names it and holds no key.
- `test_keptCopy_ClaudeCodeRefreshesTheActiveSignIn_TheNextOpenTakesTheNewKey`.
- `test_keptCopy_ActiveRefreshedThenReplacedBeforeTheNextOpen_TheKeptSpareHoldsTheKeyTheCopyHad` - the limit above.
- `test_keptCopy_ASwitchPutsTheActiveAway_TheCopyIsDroppedSoTheAccountIsKeptOnce`.
- `test_keptCopy_SignedOutByHand_TheCopyIsDroppedAndNothingIsKept`.
- `test_keptCopy_TheReplacedAccountsSpareFolderExists_TheCopyIsNeverWrittenOver_AndTheNextOpenKeepsIt`.
- `test_keptCopy_CrashAtEveryChangePoint_TheReplacedAccountIsKeptOnceAndNeverLost`.
- `tests/test_license_cli.py::test_list_HandSignInToAnAccountNotHeld_KeepsTheReplacedOneAsASpare_AndSaysSo`.

Changed, because they encoded the loss this step removes: the heal tests (`test_heal_*`,
`test_exchange_ActiveAccountAlsoHeldAsSpare_*`, `test_list_Json_HandSignInOverASpare_*`) said the account a hand
sign-in replaced was gone; they now say it is kept as a spare. The heal's own crash test now crashes only at the
heal's change points; keeping the account has a crash test of its own. And
`test_refresh_key_ActiveKey_AfterRenewal_*` now finds the renewed key in the default folder and the kept copy, and the
spent token nowhere.

Revert check: with `_keep_active_copy` returning at once, 28 test runs failed - 14 of the 16 new kept-copy runs
(the signed-out pair passes either way, since it asserts nothing is kept) and the changed ones above. Restored, all
pass.

`FakeSecurity` and `World` moved from `test_license_store.py` to `tests/license_store_world.py`, so each test file
collects on its own: importing one test file from another resolved `tests` to an unrelated installed package on a
machine that has one.

`tool-tests-output.txt`: the whole `tools/cc-devthrottle` suite, 5,353 passed and 6 skipped, on Python 3.11 with
the dependency floors the CI job installs, and both license test files collecting alone on the machine's own Python.
No C# was touched, so `scripts\test-local.ps1` was not run.

After merge, the Python tests run on GitHub only when asked: `gh workflow run CI --ref main`.

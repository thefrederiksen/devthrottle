# Answers to the review of pull request 3875 (`review-3875.md`), and to the Tech Lead on contract section 8

Every finding is accepted and fixed. Nothing is declined.

## P1 - Adoption leaves duplicate sign-in keys across a crash window - ACCEPTED, fixed by removing the move

The finding was right for every adoption path: the new key was written into its folder by one journal step and
removed from the staging folder by a later one, so a crash between them left one sign-in in two places. A
refreshed copy then makes the other stale, and recovery could block.

Making the move "safer" cannot fix that: copying a key between two places always has a moment when it is in both.
So the sign-in is no longer moved at all:

- `claude auth login` runs straight into the folder the account belongs in: its spare folder for a new spare, or
  the folder already holding it for a renewal (its spare folder, or the default folder for the active account).
  Claude Code's own single write is the only write. The staging folder, the adopt journal, and the `replace` step
  are gone (`LicenseStore.sign_in_target`, `finish_sign_in`, `abandon_sign_in`).
- The store lock is held only around that write, after checking the folder still holds what it held when the login
  started. A switch since then (which would have put another account's key there) stops the sign-in with nothing
  written (`test_signIn_SwitchBetweenPlanAndWrite_NothingIsWritten`). The lock is not held while waiting for the
  browser, which can take minutes on another machine, so a sign-in never blocks a switch for long.
- A new spare folder that never received a sign-in is removed. One that received ANOTHER account's sign-in (the
  browser approved the wrong account) is deleted too: that sign-in was made a moment before and exists nowhere else.
  A folder holding the account's own key is never deleted.
- **Uniqueness is now asserted in the state right after each simulated crash, before any recovery runs**, for a
  new spare, a spare renewal and an active renewal, with the crash on either side of the write
  (`test_signIn_CrashAtEitherSide_EachSignInIsInOnePlace_AtTheInstantOfTheCrash`, file and Keychain backends). The
  test also checks the active folder is never empty and that no journal is left.

Live: the no-move design passed the full proof 5 of 5 times on Windows and once on the Mac
(`phase-4/README.md`), including a login straight into a folder that already held a sign-in - the renewal case.

## P2 - Proof cleanup recursively deletes an unchecked caller path - ACCEPTED, fixed

`live_sign_in_proof.py` no longer takes a root. It takes an existing parent folder (`--test-parent`,
`--remote-parent`), creates its own root inside it with an exclusive `mkdir` (it fails if the folder exists, so it
never adopts a folder it did not make), and writes a marker holding the run's random id. Cleanup deletes the root
only when that marker holds this run's id, locally and on the Mac. Checked on the Mac: a root with an empty spares
folder is cleaned, and a root whose marker names another run is refused and left alone. Cleanup on the Mac also no
longer uses a file pattern, which zsh aborts on when nothing matches - the cause of two cleanups failing in the live
runs.

## P2 - The proposed renewal error vocabulary does not match the command line - ACCEPTED, fixed

The command line now emits `no-approver` (`failed-no-approver` in a `license renew` row), the word contract 8.1
uses; `no-browser-configured` is gone from code and tests. `approver-offline` remains the one word only the
Gateway relay can produce, as 8.1 says.

## The Tech Lead's four changes to contract 8.3 - all made (commit 339f0e8b8)

1. No waiting hub call: `RequestLicenseApproval` returns at once, and the answer reaches the target as the command
   `license-approval-result`, keyed by `SignInId`; sent at once with `approver-offline` or `no-approver` when there
   is no approving Director; a target that hears nothing by `NotAfterUtc` gives up as `expired`.
2. `SealToPublicKey` is renamed `SealToRecipient`; `SealedApproval` is the one named exception to rule 6.1.
3. The Gateway relays and never keeps `AuthorizeUrl` or `SealedApproval`: never stored, never logged; a Gateway test
   proves no table has a column for either.
4. The key holder reports even when it holds nothing, written into section 2.4 itself.

## Found while answering, for the Delivery Lead

claude.ai began asking the approving profile to log in again ("authenticate connections") after about 30 automated
approvals in a day, though the profile stayed signed in to claude.ai itself. The tool refuses correctly
(`browser-not-signed-in`), but unattended renewal can meet this wall, and passing it needs the email-link login the
prototype proved. It is recorded in `phase-4/README.md` as not in this pull request. The live runs stopped there;
re-proving needs the owner to sign that profile in again.

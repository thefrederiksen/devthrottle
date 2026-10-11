# Phase 4 - adding a spare by signing it in, and renewing sign-ins - live proof, 11 October 2026

`live_sign_in_proof.py` was run with the Windows machine as the target and with the Mac as the target, against the
code in this pull request. The committed outputs are the last runs on the final code; both passed. The script
replaces account addresses with "account A" and "account B", and the test root, home folder, user, machine and
browser profile names with labels. It never prints the authorize code, and replaces every authorize URL (which
names the account in its login hint) with `<authorize URL>`.

| Output | Target (gains the sign-in) | Approval | Key kept in |
|---|---|---|---|
| `windows-2026-10-11.txt` | the Windows machine | the same machine, a Director browser profile | `.credentials.json` |
| `mac-2026-10-11.txt` | the Mac, over SSH, with `license sign-in --remote-approval` | the Windows machine, `license approve` | the Keychain |

On the final code the Windows run passed three times out of three and the Mac run twice out of two; each run makes
four approvals.

## What each run shows

1. The isolated store starts empty.
2. A sign-in for account B is attempted while the browser profile is signed in to claude.ai as account A. The
   approval reads "Logged in as" on the consent page, sees account A, clicks nothing and refuses
   (`wrong-account`). The target keeps nothing: no spare, and its sign-in folder is deleted.
3. A sign-in for account A adds it as a spare.
4. `license usage` reads the new spare with its own key, and reports `signedInAtUtc` (the moment the login
   finished) and `signInExpiresAtUtc` (from the new key).
5. Signing account A in again RENEWS the spare in place: same folder, a new sign-in time, a new end date.
6. The renewed spare reads; exactly one spare is held; nothing is left in the sign-in area.
7. Account A is switched in, so it is the ACTIVE account of the isolated default folder, and signing it in again
   renews the ACTIVE sign-in in place: the default folder, a new sign-in time, and it reads with `--active`.
8. The test root is deleted and checked: on Windows the folder is gone; on the Mac the Keychain item of the default
   folder and of every spare folder is gone too. (The store refuses to empty the active folder, by design, so the
   proof deletes its own test root itself.)

## How the active account is renewed

Its sessions read the default folder's key before every call, so it can never be emptied, not even for an instant.
The new key and account block overwrite the old ones in one write per store, and only if the key there is still the
one the renewal planned on: a running session that renewed its key in the meantime stops the renewal with nothing
changed, to be tried again (`test_adopt_ActiveKeyRenewedBySessionMeanwhile_IsRefusedAndNothingChanges`). A crash at
every change point leaves a sign-in in the default folder throughout and never loses the new one
(`test_crash_AdoptAtEveryChangePoint_TheNewSignInIsNeverLost[renew-active]`). The old sign-in stays valid at the
provider until its own end, so a session holding it in memory carries on and picks up the new key on its next read.

Not proven live, and cannot be in isolated folders: a real session racing the renewal. If a session was already
part way through renewing the OLD key when the new one was written, it writes its renewed old key back over the new
one. Nothing stops working - the old sign-in is still valid - but the renewal is undone, and the next daily run sees
the end date unchanged and renews again.

## Setup (not in the output files)

- **Isolated folders only.** An empty default config folder and a store under a fresh test root, on both
  machines. The real default Claude login was never read or touched.
- **The code under test came from this checkout.** On Windows, run from `tools/cc-devthrottle/src` in a virtual
  environment at the tool's floor versions (Typer 0.16.1, Click 8.2.1). On the Mac, the same source copied over and
  run with the installed tool's own Python. The Mac's Keychain was unlocked over SSH first (MISSION.md trap 3).
- **The browser profile is one of the Director's own** (`cc-devthrottle browser list`), started by the command when
  it was stopped and stopped again afterwards. No browser was launched by hand.

## Findings

- **A sign-in lives a little less than 30 days, and not always the same.** The 28 sign-ins made today reported end
  dates 27.1 to 29.9 days after they were made. So nothing assumes 30 days: renewal is due when the end date
  the key itself reports is within five days (`RENEW_AHEAD`), and that date is what is reported.
- **Three browser races, each seen live and fixed before the committed runs:**
  - a stopped profile left browser-harness's daemon behind, and the next approval failed with "Access is denied" on
    the daemon's port file - every approval now ends its own daemon (`browser-harness --reload`) first;
  - the Authorize click was sometimes lost, the consent page staying up - the page is long and the button can sit
    below the visible area, so it is now scrolled to the middle and its position read just before each click, and
    clicked again (at most three times) while it still shows;
  - the Authorize button can render before the "Logged in as" line, which made the approval refuse a page that
    named no account - the page is now judged only once both are there.
  When an approval still finds no code, the error names the page's address and its first words, so a new race is
  diagnosable rather than a bare failure.

## What this pull request does not do yet

- **The Director does not run `license renew` on a schedule** and does not report a failed renewal; **two machines
  are joined by hand** (`license sign-in --remote-approval` and `license approve`, joined over SSH in this proof);
  **there is no Cockpit button.** The messages for all three are proposed in `../contract.md` section 8, for the
  phase 2 Tech Lead to agree before they are built.
- **A claude.ai browser session that has itself expired is not signed back in.** The approval refuses
  (`browser-not-signed-in`) and says to sign the profile in by hand, once.

# Phase 4 - adding a spare by signing it in, and renewing it - live proof, 11 October 2026

`live_sign_in_proof.py` was run once with the Windows machine as the target and once with the Mac as the target,
against the code in this pull request. Both runs passed. Each output file is exactly what the script printed; the
script replaces account addresses with "account A" and "account B", and the test root, home folder, user, machine
and browser profile names with labels. It never prints the authorize code, and replaces every authorize URL (which
names the account in its login hint) with `<authorize URL>`.

| Output | Target (gains the spare) | Approval | Key kept in |
|---|---|---|---|
| `windows-2026-10-11.txt` | the Windows machine | the same machine, a Director browser profile | `.credentials.json` |
| `mac-2026-10-11.txt` | the Mac, over SSH, with `license sign-in --remote-approval` | the Windows machine, `license approve` | the Keychain |

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
7. The test spare is removed. On the Mac, the Keychain item for the spare's folder was checked afterwards and is
   gone.

## Setup (not in the output files)

- **Isolated folders only.** An empty default config folder and a store under a fresh test root, on both
  machines. The real default Claude login was never read or touched.
- **The code under test came from this checkout.** On Windows, run from `tools/cc-devthrottle/src` in a virtual
  environment at the tool's floor versions (Typer 0.16.1, Click 8.2.1). On the Mac, the same source copied over and
  run with the installed tool's own Python. The Mac's Keychain was unlocked over SSH first (MISSION.md trap 3).
- **The browser profile is one of the Director's own** (`cc-devthrottle browser list`), started by the command when
  it was stopped and stopped again afterwards. No browser was launched by hand.

## Findings

- **A sign-in lives a little less than 30 days, and not always the same.** Six sign-ins today reported end dates
  between 27.8 and 29.9 days after they were made. So nothing assumes 30 days: renewal is due when the end date the
  key itself reports is within five days (`RENEW_AHEAD`), and that date is what is reported.
- **A stopped browser leaves browser-harness's daemon behind.** The first two-approval run on Windows failed its
  second approval with "Access is denied" on the daemon's port file, because the profile had been stopped and
  started again under a daemon still attached to the old browser. Every approval now ends its own daemon
  (`browser-harness --reload` for that profile) before the profile is stopped; the runs above were made after that
  fix.
- **One approval on the Mac run before last clicked Authorize and saw no code.** It did not happen again in the
  next full run. The approval now reports the page's address and its first words when that happens, so a repeat is
  diagnosable rather than a bare failure.

## What this pull request does not do yet

- **The Director does not run `license renew` on a schedule.** The command exists and exits 1 when a renewal fails;
  a Director job that runs it daily and reports a failure to the Gateway is the next pull request.
- **Two machines are joined by hand.** `license sign-in --remote-approval` on the target and `license approve` on
  the browser machine are the two halves; this proof joined them over SSH. Relaying them through the Gateway, and a
  Cockpit button that starts it, need new messages in `contract.md`.
- **A claude.ai browser session that has itself expired is not signed back in.** The approval refuses
  (`browser-not-signed-in`) and says to sign the profile in by hand, once.

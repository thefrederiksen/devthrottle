# License Switching - phase 1 report (the Director)

Phase 1 of `MISSION.md` section 6: the Director's key store, usage reading, the exchange, the command line,
the Director's side of `contract.md`, and the live proof. Written by the phase 1 Tech Lead on 11 October 2026.
Every pull request below is merged to main.

## What was built

| Pull request | What it is | Merged |
|---|---|---|
| 3857 | The key store and the exchange, in `tools/cc-devthrottle` (`license_store.py`, `license_ops.py`): `cc-devthrottle license list`, `switch <account>`, `add <account-folder>`, `remove <account>`. Windows and Linux keep the key in `<folder>/.credentials.json`; macOS in the Keychain under Claude Code's own service names. A switch EXCHANGES the active key with the spare and moves `oauthAccount` with it; it is journaled, crash safe, takes one store lock, and reads the new active account back. `switch --json` speaks the contract's result fields and reason codes. The real default Claude folder is refused unless explicitly allowed. | cbf37c4a2 |
| 3858 | Usage reading and keeping spares fresh: `cc-devthrottle license usage` (the contract's holding fields, the six read-error codes, `--active`, `--refresh`) and `license refresh`. A refresh renews a spare in its own folder while holding the store lock from reading the refresh token to writing the new key. | be7d0cc04 |
| 3863 | Only Escape reaches Claude Code's usage-limit dialog: one guard on every session's input refuses all automatic input but Escape while the dialog shows; a person typing at the terminal on the desktop is the one exception; one Escape is sent and logged; a blocked phone or Cockpit prompt is answered "not delivered" with a reason instead of being dropped silently. | 7207db5b8 |
| 3869 | The live proof script `live_exchange_proof.py` and its output for Windows and the Mac (`proof/`). | 2e2019f75 |
| 3868 | The Director's side of the contract: the key-holder lock, the usage report loop (`PushLicenseUsage`), and `LicenseSwitchExecutor` for `license-switch` (every step of contract 3.3, results kept 24 hours by switch id). The Director runs its OWN installed copy of `cc-devthrottle`, never whatever is on the path. License switching is OFF until the setting `licenseSwitching.enabled` is set to true, so nothing touches a machine's real default login before phase 5. `NoUnreadUsagePollTests` was rewritten on purpose: the Director's C# still names no usage endpoint (the command line does the reading), and the test now also proves the reading reaches `PushLicenseUsage`. | a5393eb12 |
| 3870 | The old Accounts dialog and `ClaudeAccountStore` are retired: that store copied every sign-in's tokens into `accounts.json`, a second copy that trap 2 forbids. Nobody's `accounts.json` is deleted, and the storage migrations no longer copy it. The command line replaces the dialog now; the Licenses page (phase 3) replaces it in the Cockpit. | 76c37c54e |

## The check, run by the Tech Lead

- **Unit tests for the key store** (`MISSION.md` section 7): exchange on the Windows file backend and on the
  macOS Keychain backend with the exact service names; a key in exactly one place after every operation and
  after a crash at every step and its recovery; `oauthAccount` follows the key; no key on a command line or in
  output. Full `cc-devthrottle` suite on the final merge of 3858: **4147 passed, 3 skipped**, with Typer 0.16.1
  and Click 8.2.1 (the continuous integration floor).
- **Local gate** (`.\scripts\test-local.ps1`) on each pull request merged with the main of its time: **green,
  all 10 suites completed**, for 3857, 3858, 3863, 3868 and 3870. `CcDirector.Gateway.UnitTests` run in full
  for 3863 and 3868, which change the command path: **10426 passed** each time. One earlier run hung while
  another mission's release gate ran Gateway tests on the same machine; the rerun with hang detection finished
  with the same total.
- **Live proof** (`proof/README.md`): a `claude -p` task paused inside a tool call; the key was exchanged with
  `cc-devthrottle license switch` while it waited; its next call was answered by the other account. The script
  passes only when the call before the exchange matches the SOURCE account's own reset times and the call
  after matches the TARGET account's, and when the switch-back is verified. Isolated folders only, under a
  marked test root.
  - Windows (key in `.credentials.json`): the Tech Lead ran it twice - **PROOF PASSED** both times (account A
    to account C, `SECOND CALL OK`). The Developer's run is committed.
  - The Mac (key in the Keychain): the Developer's run, **PROOF PASSED** (account C to account B; account B
    had used its week, and the call was refused with account B's own limit and reset time).
- **Usage reading live**: `cc-devthrottle license usage` through the merged store on the isolated folders read
  both accounts' five-hour and weekly use and reset times.

## Reviews and their answers

Every pull request was reviewed by a separate review session before merge; every finding is answered,
accepted or declined with the reason, in the files below.

| Pull request | Review | Answers | In short |
|---|---|---|---|
| 3857 | `review-3857-key-store.md`, `review-3857-fix-round.md` | `review-3857-answers.md` | Found: a rollback could overwrite a sign-in written after a crash; the Keychain name was hashed from a changed path; a crash inside remove left an account block behind; a test could not see a hex key on a command line; then (fix round) a recovering remove could delete a later sign-in. All fixed with tests. Declined, with a measurement on the Mac: Claude Code does normalize the folder to Unicode form C before hashing. Relative folders stay refused (Claude Code hashes the folder exactly as typed). |
| 3858 | `review-3858-usage.md`, `review-3858-round-2.md` | `review-3858-answers.md` | Found: provider text could reach error detail; out-of-range percentages were accepted; then a refresh racing an exchange could lose a rotated key. Fixed. Declined: keeping the previous numbers after a failed read is the Director's job (the command line is stateless); 3868 does it. |
| 3863 | `review-3863-escape-only.md`, `review-3863-fix-round.md` | `review-3863-answers.md` | Found: remote phone or browser input counted as a person at the terminal; a wrapped option line made recognition fail open; then unrelated "usage limit" text near an ordinary menu blocked it and dropped a phone answer silently. All fixed. |
| 3868 | `review-3868-director.md`, `review-3868-fix-round.md` | `review-3868-answers.md` | Found: unknown error words from the command line could carry provider text; an expired spare was not refused; a `switch-in-progress` refusal was not replayed by switch id; then the replay carried an empty active account. Fixed. Plus the Tech Lead's requirement: the off-by-default setting. |
| 3869 | `review-3869-live-proof.md` | `review-3869-answers.md` | Found: "different reset times" did not prove which account answered; the switch-back was not guaranteed or checked; isolation was a deny-list; no deadlines; redaction depended on the caller. All fixed. |
| 3870 | `review-3870-retire.md` | `review-3870-answers.md` | Found: the storage migration still copied `accounts.json`. Fixed. |

## Decisions the phase made

- The key store, exchange and usage reading live ONCE, in the Python command line; the Director runs it with
  `--json`. Why: the code that moves a sign-in key is the most dangerous code in the mission, and the command
  line must work without a running Director. Accepted by the Delivery Lead on three conditions, all met in 3870.
- The live proof on Windows used account A in a new isolated sign-in instead of account B, because the only
  browser that could sign in as B was signed in to claude.ai as the owner's main account and would have been
  signed out. A separate sign-in is an independent key, not a copy. The Mac run used account B.
- With license switching turned off, a `license-switch` command is answered `failed` with reason
  `not-key-holder` and an empty `ActiveAccountEmailAfter`. An empty value means "not known" and must never
  update the Gateway's picture of the machine.

## Open, for later phases

- **The 24-hour spare check is not done yet.** An isolated folder signed in at 13:33 UTC on 11 October was
  left untouched; its access token expired at 21:33 UTC. Due 13:33 UTC on 12 October: add that folder to an
  isolated store with `cc-devthrottle license add`, run `cc-devthrottle license usage --refresh`, and confirm
  it renews and reads.
- **A spare needs a new sign-in about once a month.** The refresh token keeps the end date of its original
  sign-in, roughly 30 days (`spare-refresh.md`). `license usage` reports `signInExpiresAtUtc`; `signedInAtUtc`
  stays empty until a sign-in records its own time (phase 4). The Licenses page should warn ahead of it.
- **For the Gateway (phase 2):** a result with `Replayed = true` is an old answer and must not overwrite a
  newer report's picture of the machine; an empty `ActiveAccountEmailAfter` means "not known".
- **On Windows the canonical-folder rule refuses forward slashes** (`D:/...`), although the Windows backend
  does not hash the path. Harmless today (the Director passes its own spelling); worth relaxing for people
  typing paths in Git Bash.
- **The installed `cc-devthrottle` on both machines has no `license` command yet**; it arrives with the next
  release, and phase 5 turns `licenseSwitching.enabled` on.

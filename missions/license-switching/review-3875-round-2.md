# Review, round 2: pull request 3875 - License Switching phase 4 spares

Round 1 is `review-3875.md`; the answers are `review-3875-answers.md` (commit a6e83828d). All three round-1
findings are closed, and contract 8.3 has the four Gateway changes. Three new findings: one in the code, two in the
contract text the next pull request builds from.

## Round-1 findings: verified closed

**P1, the duplicate sign-in during adoption: closed.** There is no staging folder, adopt journal or `replace` step
any more. `claude auth login` writes straight into the folder that holds the account (`sign_in_target`), under the
store lock, and only after `finish_sign_in` re-checks that the folder holds the same account in the same role.
Claude Code's one write is the only write, so no step puts a key in two places. The crash test now asserts
exactly-one-place and a non-empty active folder before any recovery runs. It does this for a new spare, a spare
renewal and an active renewal, with the crash before and after the write
(`test_license_store.py:1183`).

The active renewal never leaves the machine without a key while it waits for the browser. I checked this directly,
because the live proof only shows the end state. I started a real `claude auth login --claudeai` with
`CLAUDE_CONFIG_DIR` pointed at a scratch folder holding a `.credentials.json`. Fifteen seconds later the login was
waiting on its authorize URL and the file was byte-identical. It was still there after the login was killed. So a
failed or abandoned active renewal leaves the old key in place, and `abandon_sign_in` never touches a renewal's
folder.

**P2, the proof cleanup: closed.** `make_root` creates `license-proof-<run id>` with an exclusive `mkdir`, locally
and with `set -e; mkdir` on the Mac, and writes the run id into a marker file. `clean` deletes the root only when the
marker holds this run's id.

**P2, the error vocabulary: closed for the command line.** `no-browser-configured` is gone from the source and the
tests. The code is `no-approver`, which matches 8.1. See finding 3 for a new gap of the same kind, in 8.3.

**The four contract 8.3 changes: all present.**
- No hub call waits. `RequestLicenseApproval` returns at once, and the answer comes back as the command
  `license-approval-result`, keyed by `SignInId`.
- No field is named `SealToPublicKey`. It is `SealToRecipient`, and `SealedApproval` is named as the one exception
  to rule 6.1.
- The Gateway relays and never keeps. `AuthorizeUrl` and `SealedApproval` are never stored or logged, and a Gateway
  test proves no table has a column for either.
- The key holder reports with no holdings. This is written into section 2.4 itself.

## New findings

### P2 - A renewal that receives another account's sign-in overwrites the held key, and nothing undoes it

**Where:** `license_store.py` `finish_sign_in` (the read-back after `write()`) and `abandon_sign_in` (returns at
once when `target.renewing`); `license_signin.py` `approve_in_browser`; `license_signin_ops.py` `approve`.

The code expects that a login can end holding the wrong account. `finish_sign_in` reads the folder back and
raises `sign-in-wrong-account`, and `abandon_sign_in` deletes a NEW spare folder that received another account's
key. On a renewal, though, that read-back comes after Claude Code has already replaced the held key, and
`abandon_sign_in` returns without touching anything. Here is a path that reaches it with the commands as built:

1. Machine T renews account A, which is active. It runs `license sign-in A --remote-approval`, and the authorize
   URL carries `login_hint=A`.
2. On the approving machine, the operator runs `license approve --email C --browser <C's profile> <that URL>`, with
   a mistyped email or the wrong profile. `approve_in_browser` compares the consent page's "Logged in as" only with
   `--email`. Both say C, so it clicks Authorize. The account named in the URL is never checked.
3. The code goes back to T, and `finish_sign_in` types it in. Claude Code writes C's sign-in into T's default
   folder, which replaces A's key. Then the read-back raises, and `abandon_sign_in` returns because this is a
   renewal.

The result: A's sign-in is no longer on the machine, and the machine runs as C. If C is also held as a spare on T,
C is now in two places, which is exactly the harm round-1 P1 was about. For a spare renewal, A's spare folder ends
up holding C. `test_signIn_LoginWroteAnotherAccount_IsReportedNotAccepted` covers only a new spare, so no test
covers the renewal case.

One assumption matters. If Claude Code refuses a code for an account other than `--email`, this path is closed.
Nothing in the pull request shows that it does. `login_hint` is only a hint (it is one of the URL parameters
`claude auth login --claudeai` prints), and the code's own wrong-account branch assumes the write happens.

The read-back cannot repair a renewal, because the old key is gone by then, so the check has to come before the
code is typed in. That check is cheap: `approve_in_browser` can refuse, as `wrong-account`, whenever the URL's
`login_hint` differs from `email`. That protects the hand-joined path today and the 8.3 relay later. A renewal
wrong-account test, for both a spare and the active account, would hold it in place.

### P2 - Contract 8.3 says to delete "its sign-in folder" on expiry. For a renewal, that folder holds the key in use

**Where:** `contract.md` 8.3, the paragraph after `LicenseApprovalResult`: *"A target that has received no
`license-approval-result` ... gives up: the login is ended, its sign-in folder deleted, and the attempt is
`failed` / `expired`."*

This sentence comes from the staging design that this round removed. In the no-move design, the sign-in folder of
a renewal is the folder that holds the account: its spare folder, or the default folder for the active account.
8.4 says this same 8.3 path serves "Renew now" and the scheduled renewal, and the Director side is the next phase 4
pull request. Built as written, an approval that times out on an active renewal deletes the folder holding the
machine's only working key.

The code already does the right thing (`abandon_sign_in`). The contract should describe that behaviour: the login
is ended; a new spare folder that received nothing is removed; a renewal's folder is never touched.

### P3 - `expired` is not in `LicenseRenewalErrors`, but a renewal through 8.3 can fail with it

**Where:** `contract.md` 8.1 (the closed list) and 8.3 (`ReasonCode` is "a LicenseRenewalErrors code, or
`expired`"; on a timeout the attempt is `failed` / `expired`).

A scheduled renewal (8.2) whose remote approval times out is a failed renewal. 8.1 says it travels as
`RenewalError`, a `LicenseRenewalErrors` code. `expired` is not in that list, so the Gateway receives a code it does
not recognise, which is the same kind of mismatch as round-1 P2. The text has a second sign of the gap: 8.1 says
"plus the two only the relay can see", but lists only one (`approver-offline`, as the answers also say). Adding
`expired` to `LicenseRenewalErrors` fixes both.

## Residual risk, not a finding

Suppose the `cc-devthrottle` process alone dies, for example by being terminated, after typing the code and before
`claude auth login` exits. On Windows the child process survives, and it can finish its write after the store lock
has been released. If a switch takes the lock in those few seconds, the late write can land in a folder that now
holds another account. The window is the token exchange, about a second, and it needs a crash plus a switch inside
it, so I am not raising it. A job object or a kill-on-close child would close it, if the Director side wants to.

## Scope and evidence

- I read `review-3875.md` and `review-3875-answers.md`, plus the full diff `origin/main...HEAD` for
  `license_store.py`, `license_signin.py`, `license_signin_ops.py` and the new store and sign-in tests. I also read
  `phase-4/README.md`, the proof's `make_root` and `clean`, and `contract.md` sections 2.4, 6 and 8.
- **Tests:** I ran `pytest tests/test_license_signin.py tests/test_license_store.py`, and 175 passed with 1 failure.
  The failure is `test_cli_signInRemoteApproval_PrintsTheUrl_ReadsTheCode_AddsTheSpare`, which mixes stderr into
  stdout. That is my environment, not the code: this machine's Python has Click 8.1.8, and the tool's floor is
  `click>=8.2.1` (`pyproject.toml`), where the test runner separates the two. I did not run `test-local.ps1`.
- **Probe:** I ran a real `claude auth login --claudeai --email probe@example.com` twice, into a scratch folder
  under my session's scratchpad (`BROWSER=none`, killed before any code). The first run checked that an existing
  `.credentials.json` survives the wait. The second listed the authorize URL's parameters, which include
  `login_hint`. No real account or real config folder was involved, and the scratch folder was deleted afterwards.
- I did not run the live proof and changed no existing file.

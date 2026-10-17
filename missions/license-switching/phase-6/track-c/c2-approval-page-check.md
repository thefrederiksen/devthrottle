# Track C, Developer C2 - the approval-page check

Pull request 1 of 3: the Python tool. `license approve` and the new `license check-approver` now read an approval
page with ONE function, `classify_page` in `tools/cc-devthrottle/src/license_signin.py`, and the "reauth" page that
broke the 16 October sign-ins (problem 12) has its own word, `needs-fresh-sign-in`, everywhere.

## What changed

- `classify_page(text, address, authorizeOnScreen, email)` answers `can-approve`, `wrong-account`, `signed-out`,
  `needs-fresh-sign-in` or `check-failed`. The browser-harness scripts run this same function (its source is put at
  the top of each script), so the approval and the check cannot disagree about a page.
- `approve_in_browser` clicks Authorize only on a page that function calls `can-approve`. The reauth page, which it
  used to call "not signed in", is now refused with the reason `needs-fresh-sign-in` and the next step
  `cc-devthrottle browser signin "<profile>"`.
- `cc-devthrottle license check-approver --browser <profile> [--email <account>] --json` answers
  `{browserProfile, accountEmail, shownAccount, state, detail}`. With no email it first asks claude.ai's own account
  endpoint (`/api/account`, read from a claude.ai tab in that profile) who is signed in; a 401 or 403 is
  `signed-out`. It then starts a throwaway `claude auth login --claudeai --email <account>` into a new temporary
  folder, opens the printed address in the profile, reads the page, and in a `finally` cancels the login, deletes the
  folder, and puts the profile back as it found it. It never clicks, never types, never signs in. Exit 0 for every
  state read; exit 1 only for `check-failed`, which still answers JSON with why.
- The word `needs-fresh-sign-in` joins `LicenseRenewalErrors` (contract 8.1, now nine words), with its own sentence
  in the Director (`LicenseRenewalSentences`), on the Licenses page (`LicenseDisplayFold.RenewalWhy`) and in the
  sign-in log (`LicenseSignInFold.ReasonText`).

## The recorded page shapes

`tools/cc-devthrottle/tests/fixtures/license_approval_pages.json`, every name replaced (alice@example.com is the
account being approved, bob@example.com someone else). Found in the License Switching sessions' own transcripts;
nothing was captured live for this work.

| Shape | Where it was recorded | Reads as |
|---|---|---|
| consent: Authorize button, "Logged in as alice" | 13 October, the approval run that first proved the flow | can-approve |
| consent, signed in as bob | the same page with another account | wrong-account |
| consent before the "Logged in as" line has rendered | seen live, described in the approval script | check-failed (keep waiting) |
| sign-in page at claude.ai/login | 14 October, an approver with no session | signed-out |
| reauth on the authorize page: "Sign in again to continue. For your security, granting this access requires a recent sign-in", "Logged in as alice", no Authorize | 13 October, a profile whose chat worked | needs-fresh-sign-in |
| reauth as a redirect to claude.ai/login?reauth=1&email=alice | 13 and 14 October | needs-fresh-sign-in |
| reauth page naming bob | the reauth page with another account | wrong-account |
| any other page | - | check-failed |

## The tests, in plain words

New file `tools/cc-devthrottle/tests/test_license_approver_check.py`. Its fake browser does not pretend: it RUNS the
very script browser-harness would run, against a recorded page, with fake helpers for the page. A click where none is
allowed fails the test, and so does any typing or key press, because the fake does not define those helpers.

- `test_classifyPage_RecordedPage_SaysWhatItShows` - each of the eight recorded shapes gives the state in the table.
- `test_classifyPage_AccountCaseAndTrailingFullStop_AreNotADifferentAccount` - "Alice@Example.com." is still alice.
- `test_classifyPage_TheBrowserStepRunsTheSameFunction` - both browser scripts start with `classify_page`, and
  neither carries page rules of its own.
- `test_checkApprover_RealApprovalPage_AnswersItsState_ClicksNothing_AndLeavesNothing` - for each shape: the right
  state, the page opened is the throwaway login's own address, no click, the tab closed, focus emulation switched
  back off, the login cancelled and never completed, the temporary folder gone, the profile put back.
- `test_checkApprover_ConsentPageStillLoading_WaitsForTheAccountLine` - a consent page without its account line yet
  is not an answer; the check waits and then answers can-approve.
- `test_checkApprover_ASignInPageOnTheWay_IsNotTakenFromOneRead` - a negative answer needs two reads in a row.
- `test_checkApprover_NoEmail_ReadsTheSignedInAccountFirst_AndChecksThatOne` - discovery reads the account
  endpoint, then checks that account's approval page.
- `test_checkApprover_NoEmail_SignedOutProfile_IsSignedOut_AndNoLoginStarts` - 401 or 403 from the account
  endpoint is signed-out, and no login or folder is made.
- `test_checkApprover_NoEmail_AccountEndpointSaysNeither_IsCheckFailed` - a 500 is check-failed with the status.
- `test_checkApprover_BrowserStepFails_IsCheckFailed_AndTheLoginAndFolderAreGone` - a browser-harness failure is
  reported as check-failed, and the login and the folder are still cleaned up.
- `test_checkApprover_UnexpectedCrash_StillCancelsTheLoginAndDeletesTheFolder` - an unexpected crash is raised, not
  hidden, and still leaves no login and no folder behind.
- `test_checkApprover_LoginPrintsNoUrl_IsCheckFailed_AndNoBrowserStepRuns` and
  `test_checkApprover_ProfileCannotBeStarted_IsCheckFailed_AndNoLoginStarts` - the check fails with why, before the
  browser or the login is touched.
- `test_checkApprover_DetailIsTheToolsOwnSentence_NeverPageText` - the detail is this tool's sentence.
- `test_temporaryLoginFolder_IsANewEmptyFolderInTheTemporaryArea` - the real folder maker makes a new, empty folder
  in the system's temporary area, never a store folder or the real Claude login.
- `test_approve_ConsentPage_ClicksAuthorize_AndReturnsTheCode` - the approval still works end to end: one click,
  the code read back.
- `test_approve_PageThatCannotApprove_IsRefusedWithItsOwnReason_AndClicksNothing` - both reauth forms are
  `needs-fresh-sign-in`, the sign-in page is `browser-not-signed-in`, another account is `wrong-account`, an
  unknown page is `approval-failed`; none of them is clicked.
- `test_approve_ReauthPage_SaysWhatToDo` - the refusal says the page asks for a fresh sign-in and names the command.
- `test_cli_checkApproverJson_*` and `test_cli_checkApprover_NoEmail_AsksForDiscovery` - the command's five JSON
  fields, exit 0 for each state read, exit 1 with JSON still printed for check-failed, and no `--email` means
  discovery.

Changed in `test_license_signin.py`: the refusal cases use the new page words and add `needs-fresh-sign-in`; the
script test reads the script as it is sent.

C#:

- `RenewalErrors_AreTheContractsNineWords` - the contract's list now has the new word.
- `RenewalSentences_EveryWordTheContractNames_HasItsOwnSentence` (Director) and
  `ReasonText_EveryRenewalWordTheContractNames_HasItsOwnSentence` (Gateway) - no contract word falls through to a
  "does not recognise" sentence or the bare word, and no two words share a sentence.
- `ReasonText_NeedsFreshSignIn_SaysTheApprovalPageAskedForOne` - the sign-in log's sentence for it.
- `Holding_FailedRenewal_EveryReasonTheContractNames_HasItsOwnWords` - now nine distinct Licenses page sentences.
- `ReadSignInAnswer` and `ReadApproveAnswer` cases: the tool's `needs-fresh-sign-in` reaches the Director as that
  word, on a sign-in and on an approval.
- `ApproveAsync_ToolRefuses_AnswersItsReason_AndSealsNothing` gains the new word: refused with its own sentence.

## Gate output

- `pytest tools/cc-devthrottle/tests` (run from `tools/cc-devthrottle` as continuous integration does, in a clean
  virtual environment from the tool's requirements): 4,720 passed, 3 skipped, 1 failed - the new command's help
  summary was 83 characters, over the 80 allowed. Shortened, then the four affected files re-run: 1,334 passed,
  0 failed. Note: this machine's
  system Python has an older Click and an unrelated `tests` package in site-packages; under it, 1,851 tests fail on
  clean origin/main too - the same set with and without this change, compared test by test.
- `.\scripts\test-local.ps1`: RESULT GREEN, every suite outcome Completed with all tests executed. It ran on the
  working tree that became commit f89a3a0e8 (the only later change is this file).
- `.\scripts\test-local.ps1 -Parked`: REFUSED before building (exit 7) - drive C: had 11.6 GB free and a release gate
  needs 15 GB. The temporary folders filling it belong to other sessions' runs, so nothing was deleted. In its place
  the License tests of the parked suites were run directly on the committed change:
  `CcDirector.Gateway.UnitTests` filtered to `CcDirector.Gateway.Tests.Licenses`: 830 passed, 0 failed;
  `CcDirector.Gateway.Tests` filtered to `License`: 21 passed, 0 failed; `CcDirector.Core.Tests` holds no License
  tests. The full `-Parked` run is still owed once there is room, and is required for pull request 3.

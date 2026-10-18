# Answers to the review of pull request 4116

The review is `review-4116.md` (head reviewed dd4683af7). Answered by the Developer who built it.

## 1. MEDIUM - a cleanup step that fails skips the ones after it

**Accepted and fixed.** The three cleanup steps now run in nested `finally` blocks (`_CheckResources.clean_up`), so
the folder is deleted whatever ending the login did, and the profile is put back whatever deleting the folder did.
Each step's expected failure - `OSError` (a locked folder), `subprocess.TimeoutExpired` (a login that will not end),
`GatewayError` (a stop the Gateway did not answer) - is recorded and logged rather than raised, and a check whose
cleanup was incomplete answers `check-failed` with what could not be done, as the docstring promises; the Director
reports it. I took the recommendation over a raise: a raise loses a state that was read, and the Director then gets
a traceback instead of the JSON it reads. An unexpected exception still propagates, but only after every step ran.
Tests: `test_checkApprover_FolderCannotBeRemoved_ProfileIsStillPutBack_AndTheCheckFailsWithWhy` (the reviewer's
probe as a test: `profile.stopped == 1`), `test_checkApprover_LoginCannotBeEnded_FolderAndProfileAreStillPutBack`,
`test_checkApprover_ProfileCannotBePutBack_IsCheckFailed_AfterTheRestIsCleanedUp`.

## 2. MEDIUM - `browserProfile` echoes the given string, and `browserProfileName` is missing

**Accepted; already fixed in 7c79ad11d**, pushed after the head this review read. `BrowserProfile` exposes `id` and
`name`; the answer's `browserProfile` is the identifier whichever string `--browser` was given, `browserProfileName`
is the display name, and the sentences use the name. A profile that cannot be found echoes only what was given,
with no name. Test: `test_checkApprover_GivenTheNameOrTheIdentifier_AnswersTheIdentifier_AndTheName` (given
"Work Chrome" or "work-chrome", the answer is "work-chrome" named "Work Chrome").

## 3. LOW - `detail` carries page text on wrong-account

**Accepted and fixed.** The sentence is now "... is signed in to claude.ai as another account, not <the account
asked for>." The account the page showed travels only in `shownAccount`. Test:
`test_checkApprover_WrongAccount_DetailNeverCarriesTheAccountThePageShowed`.

## 4. LOW - a 403 from the account endpoint is read as signed out

**Accepted and fixed, from the record.** The 16 October session recorded what claude.ai's `/api/account` answers a
signed-out profile: `account_session_invalid` / "Invalid authorization" (the status itself was not recorded). So
`signed-out` now needs that answer in the body, with a 401 or 403; any other refusal is `unreadable`, which the tool
answers `check-failed` with the status. Test:
`test_checkApprover_NoEmail_RefusedWithoutTheSignedOutAnswer_IsCheckFailed_NotSignedOut`; the signed-out test now
carries the recorded answer.

## The notes for the Tech Lead

- The Reclaim red: the proof now calls it environmental (drive D: makes no short names), not flaky, as asked.
- A non-JSON answer from this command: pull request 4123's `ReadApproverCheck` already reads it as `check-failed`
  (`ToolAnswered` false), and a test pins it.

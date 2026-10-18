# Review of pull request 4116 - License Switching Track C, the approval-page check

Reviewer session: "License Switching - Reviewer - Track C 4116 approval page check" (c5225d8d).
Head reviewed: `refs/review/4116` = dd4683af7, merge base with origin/main 6c783e3d4. Three commits:
4b449679e (the change), b75edad0c and dd4683af7 (gate records). 17 files, +996 / -48.

## Scope

What I read, all through git objects (never the working tree):

- The whole of `tools/cc-devthrottle/src/license_signin.py` at the head, line by line for the three browser
  scripts (`_SETTLE_SCRIPT`, `_APPROVE_SCRIPT` tail, `_CHECK_SCRIPT` tail, `_ACCOUNT_SCRIPT`), `classify_page`,
  `check_approver`, `read_signed_in_account`, `_temporary_login_folder`, and the unchanged pieces they lean on
  (`ClaudeLogin`, `director_browser`, `_run_harness`, `approve_in_browser`).
- The diff of `license_signin_ops.py`, `cli.py`, the new test file, the fixture, the changed `test_license_signin.py`.
- `browser_ops.DrivenBrowser` (how `--browser` is resolved), `license_store.is_real_default`.
- The Director side: `LicenseCommandLine.SignIn.cs` (`ReadApproveAnswer`, `ReadSignInAnswer`, `KnownRenewalError`),
  `LicenseApprovalExchange.cs`, the contract constants, and the two Gateway folds, with their tests.
- `missions/license-switching/contract.md` and the proof file `c2-approval-page-check.md`.
- Outside the repository: browser-harness `helpers.py` `js()` / `_js_evaluate` (whether a promise is awaited - it is,
  `await_promise=True`), so the account script's `fetch` works as written.
- The Track C design including "Which string a browser profile is", and the Developer's brief.

What I ran, out of tree: `git archive refs/review/4116 tools/cc-devthrottle tools/cc_shared tools/cc_storage` into a
scratch folder, then `python -I -m pytest tests/test_license_approver_check.py tests/test_license_signin.py`
(system Python 3.11.6, typer 0.20.0, click 8.1.8): **85 passed**. Plus one probe script (finding 1).

What I could not reach: no live claude.ai page, no real browser profile, no real `claude auth login` - by design of
this review. So the recorded shapes in the fixture are taken as the Developer recorded them; I checked them for
real names and plausibility only. I could not verify which status claude.ai's account endpoint really answers for a
signed-out profile (finding 4 hinges on that).

## Safety reading (no finding)

Every line of the check path was read for a click or an input. The check script does: `new_tab(URL)`, focus
emulation on, `wait_for_load()`, then in `settle()` a loop of `querySelectorAll('button')` plus `scrollIntoView`
(a scroll, not a press), `innerText`, `page_info()`, then `finish(...)`, and in `finally` focus emulation off and
`Target.closeTarget`. No `click_at_xy`, no `Input.*`, no `insertText`, no key. The only click in the module is in the
`_APPROVE_SCRIPT` tail, behind `if state != "can-approve": finish(...) else: ... click_at_xy`. The account script
opens `https://claude.ai/`, runs one `fetch('/api/account')` and closes the tab.

The throwaway login: `tempfile.mkdtemp(prefix="cc-license-check-")` - a new, empty, uniquely named folder in the
system temporary area, refused if it is the real default folder; `ClaudeLogin` always sets `CLAUDE_CONFIG_DIR` to it
(it only unsets that variable for the real default, which cannot happen here) and drops the allow-real-default
variable. The login's `finish` is never called; `cancel()` kills the process and waits; `rmtree` follows. The tests
prove cancel-and-delete on: every page state, a browser failure, an unexpected crash, a login that prints no URL,
a profile that cannot start (no folder is made at all in the last case).

## Classifier reading (no finding)

Precedence in `classify_page`: an account named that is not `email` -> `wrong-account` (even on the reauth page);
then "requires a recent sign-in" / "Sign in again to continue" text or `/login?reauth=1` -> `needs-fresh-sign-in`;
then `can-approve` ONLY when the Authorize button is on screen AND the page names the right account (a positive
reading, two presences); a bare `/login` -> `signed-out`; anything else -> `check-failed`. A page it cannot classify
can never be `can-approve`. `settle()` takes `can-approve` at once and any other answer only on two agreeing reads,
`check-failed` only at the deadline; it carries no page rule of its own (the test asserts that, and that the
classifier source heads both scripts). The reauth and signed-out shapes are told apart by what the page shows
(the "Sign in again" text and the `reauth=1` query against a plain `/login`), which is problem 12 as asked.
The fixture holds alice@example.com and bob@example.com only.

## The refusal and the new word (no finding)

`approve_in_browser` raises `SignInError(NEEDS_FRESH_SIGN_IN)` with its own next step for both reauth shapes and
clicks nothing (proven through the real script by `test_approve_PageThatCannotApprove_...`). `_fail` prints
`{"result":"refused","reason":"needs-fresh-sign-in"}`; the Director's `KnownRenewalError` accepts it because
`LicenseRenewalErrors.All` now contains it (tested); `LicenseRenewalSentences`, `LicenseDisplayFold.RenewalWhy`
and `LicenseSignInFold.ReasonText` each have a distinct sentence (each tested, including "every word in the set has
its own sentence"). No production site that switches on `BrowserNotSignedIn` lacks the new word.

## Tests (no finding)

`FakeBrowser` executes the very script the harness would run; it fails the test on any click when not allowed, on
any JavaScript or CDP command it does not know, and typing helpers are undefined (NameError). `check_approver` is
called through its real seams with a fake login that writes a file into the folder, so a leftover folder would show.
The command tests patch `license_signin.check_approver` - acceptable, they test the command's wiring and exit codes.

## Public repository (no finding)

Every added line scanned for email addresses, machine names, the owner's name and IP addresses: only
`example.com` names and `127.0.0.1`.

---

## Findings

### 1. MEDIUM - A cleanup step that fails skips the ones after it: the browser profile is left running and a read result turns into a crash

`tools/cc-devthrottle/src/license_signin.py` lines 731-739 (`check_approver`'s `finally`):

```python
finally:
    if login is not None:
        login.cancel()
    if folder is not None and os.path.isdir(folder):
        shutil.rmtree(folder)
    if browser is not None:
        browser.stop()
```

The three steps run in sequence with no nesting. `ClaudeLogin.cancel()` (line 218) kills the process and then
`wait(timeout=10)`, which raises `TimeoutExpired` when the process does not go; `shutil.rmtree` raises
`PermissionError` on Windows when anything still holds a file in the folder (the login was killed a moment before).
Either exception skips `browser.stop()`.

Proven out of tree with the pull request's own fakes and `rmtree` made to raise the Windows "being used by another
process" error, on the `consent` page (a check that had read `can-approve`):

```
RAISED: PermissionError [Errno 13] ... check-0
login cancelled: 1
folder still exists: True
profile.stopped: 0 (1 means the profile was put back)
```

The harm: (a) a browser profile the check STARTED stays running - the design says "Starting a stopped profile for
the check puts it back stopped after"; the Director will run this daily for every profile on an approver machine,
so one locked folder leaves a browser up for a day; (b) a state that WAS read (`can-approve` here) is replaced by an
uncaught exception, so the command exits with a traceback instead of the JSON the Director reads, against the
docstring's own rule "Every failure of the check itself is answered check-failed with why, never raised"; the comment
on line 735 says a folder that cannot be removed is raised deliberately, but it does not say that the profile is then
abandoned, and the test for that path does not exist (`test_checkApprover_UnexpectedCrash_...` crashes the browser
step, not the cleanup).

Change: nest the three steps so each runs whatever the earlier ones did (`try: cancel finally: try: rmtree
finally: stop`), and add a test in which `rmtree` raises and asserts `profile.stopped == 1`. Whether the rmtree
failure then becomes `check-failed` with why (my recommendation - the Director reports it, as the docstring
promises) or stays a raise is the Developer's call; either way the profile must be put back.

### 2. MEDIUM - `check-approver` answers whatever string it was given as `browserProfile`, not the profile's identifier

`tools/cc-devthrottle/src/license_signin.py` line 727 (`return ApproverCheck(profile, ...)`), with
`director_browser` lines 495-505 and `browser_ops.DrivenBrowser.__init__` (resolves `target` by id FIRST, then by
NAME, case-insensitively, and keeps both `self.id` and `self.name`).

The decided rule ("Which string a browser profile is", 17 October): `browserProfile` is the IDENTIFIER everywhere,
compared exactly; a record set by name must never match a check. This command accepts a display name on `--browser`
(the resolver allows it) and echoes it back verbatim: `check-approver --browser "Work Chrome"` answers
`"browserProfile": "Work Chrome"`. The Director (pull request 2) cannot tell from the answer which string it holds,
and if it passes an assignment that was set by name before the rule, the check resolves it, succeeds, and reports
the NAME - which the Gateway's exact compare then matches to the name-set record, exactly what the design says must
not happen ("simply never matches a check, so it reads not-checked and says so"). The tool also throws away
`DrivenBrowser.name`, which the design says the check row carries as `browserProfileName` (at most 64 characters) so
the Gateway can write "Work Chrome" in its sentences.

I note this rule was decided after the pull request was written, so this is the design catching up with the code,
not a lapse. Change: `BrowserProfile` exposes `id` and `name` (the `FakeProfile` in the tests gains them);
`ApproverCheck.browser_profile` is `browser.id` and a new `browser_profile_name` is `browser.name`; `to_json` adds
`browserProfileName`; a test passes a display name in and asserts the identifier comes out. The `_detail` sentence
can then say the name, which is what the owner reads.

### 3. LOW - `detail` carries page text on `wrong-account`

`tools/cc-devthrottle/src/license_signin.py` line 662: `f"{who} is signed in to claude.ai as {shown}, not {email}."`.
`shown` is lifted from the page by the regex `[^\s]+@[^\s]+` (any non-whitespace token with an `@` in it, no length
limit). The design says `detail` is "a short sentence from the Director's fixed words - never page text", and gives
the page's account its own shape-checked field, `shownAccount`. A long or odd token on a page would land in
`detail`, and the Gateway's length check on `detail` then refuses the whole row ("refused rows are refused whole") -
so a wrong-account profile with a strange page could go unreported rather than reported as wrong-account.
`test_checkApprover_DetailIsTheToolsOwnSentence_NeverPageText` checks only the reauth shape. Change: the sentence
says "is signed in to claude.ai as another account" and leaves the account to `shownAccount`. (The same `shown` in
`approve_in_browser`'s message is pre-existing and is a command-line message, not a hub row.)

### 4. LOW - a 403 from the account endpoint is read as "signed out"

`tools/cc-devthrottle/src/license_signin.py` lines 474 (`if status in (401, 403): finish("signed-out")`) and 674.
The account script opens `https://claude.ai/` fresh in a new tab and fetches `/api/account`. A 403 is also what a bot
challenge or an edge block answers; read as `signed-out`, a healthy, signed-in approver profile would be reported
signed out, with the instruction to sign it in by hand, and no approval page would be opened to find out. The
direction is safe (a false alarm, never a false pass), so this is low. I could not verify which status a truly
signed-out profile receives. Change, if 401 is what the recorded signed-out answer was: treat 401 alone as
`signed-out` and 403 as `unreadable` (check-failed with the status); if 403 is genuinely what claude.ai answers,
require the body to be the endpoint's JSON before reading it as signed out, and say so in a comment.

---

## Notes for the Tech Lead (not findings against the code)

- The proof file records that the parked gate had one failure in a suite this change does not touch
  (`ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused`, when TEMP was on drive D:), that it
  passed alone, and that the Developer left it for the Tech Lead rather than deleting it. The 13 October ruling is
  that a test that fails on correct code is deleted the first time; that decision is yours, not this pull request's.
- The design's `check-failed` on `detail` for an unexpected crash (a non-`SignInError`) is a raise with a traceback,
  which the tests pin as intended ("the crash is raised, not hidden"). Pull request 2 should read a non-JSON answer
  from this command as `check-failed`, as the Director already does for `approve`.
- No finding on the C# side: the word is added in the one contract set and every switch over it has a tested sentence.
